using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

internal sealed record AppDefinition(string Key, string Name, string[] Executables, string[] InstallNames, string[] RelativePaths);
internal sealed record AppLocation(string Path, string Source);

internal static class AppLocator
{
    internal static readonly AppDefinition[] Apps = {
        new("Weixin", "微信", new[] { "Weixin.exe", "WeChat.exe" }, new[] { "微信", "Weixin", "WeChat" },
            new[] { @"Weixin\Weixin.exe", @"WeChat\WeChat.exe", @"Tencent\Weixin\Weixin.exe", @"Tencent\WeChat\WeChat.exe" }),
        new("Steam", "Steam", new[] { "steam.exe" }, new[] { "Steam" }, new[] { @"Steam\steam.exe" }),
        new("WeGame", "WeGame", new[] { "wegame.exe" }, new[] { "WeGame" },
            new[] { @"WeGame\wegame.exe", @"Tencent\WeGame\wegame.exe" }),
        new("5E", "5E对战平台", new[] { "5EClient.exe" }, new[] { "5E对战平台", "5EClient", "5EPlay", "5E平台" },
            new[] { @"5EClient\5EClient.exe", @"5EPlay\5EClient\5EClient.exe", @"5e\5EClient\5EClient.exe", @"5E对战平台\5EClient.exe" })
    };
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigHeadDesktopPet");
    static string OverrideFile(AppDefinition app) => Path.Combine(DataDirectory, "paths", app.Key + ".txt");

    internal static AppLocation? Find(string key)
    {
        var app = Apps.Single(a => a.Key == key);
        try {
            var saved = ValidatePath(app, File.ReadAllText(OverrideFile(app)));
            if (saved != null) return new(saved, "手动设置");
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (var source in new (string Name, Func<AppDefinition, IEnumerable<string>> Paths)[] {
            ("运行中的程序", RunningPaths), ("注册表", RegistryPaths), ("开始菜单或桌面快捷方式", ShortcutPaths), ("常见安装目录", CommonPaths)
        }) {
            foreach (string candidate in source.Paths(app)) {
                var path = ValidatePath(app, candidate);
                if (path != null) return new(path, source.Name);
            }
        }
        return null;
    }

    internal static string? FindOrChoose(string key)
    {
        var found = Find(key);
        if (found != null) return found.Path;
        var app = Apps.Single(a => a.Key == key);
        var picker = new OpenFileDialog {
            Title = $"未自动找到{app.Name}，请选择它的启动程序",
            Filter = $"{app.Name}启动程序|{string.Join(';', app.Executables)}|可执行程序 (*.exe)|*.exe",
            CheckFileExists = true, Multiselect = false
        };
        while (picker.ShowDialog() == true) {
            string? path = ValidatePath(app, picker.FileName);
            if (path == null) {
                System.Windows.MessageBox.Show("请选择 " + string.Join(" 或 ", app.Executables), "选择软件位置");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(OverrideFile(app))!);
            File.WriteAllText(OverrideFile(app), path);
            return path;
        }
        return null;
    }

    internal static string? ValidatePath(AppDefinition app, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try {
            string path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path) || !app.Executables.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return null;
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        } catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    internal static string? ExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string value = Environment.ExpandEnvironmentVariables(command.Trim());
        if (value.StartsWith('"')) {
            int end = value.IndexOf('"', 1);
            return end > 1 ? value.Substring(1, end - 1) : null;
        }
        int exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? value.Substring(0, exe + 4).Trim() : null;
    }

    static IEnumerable<string> RunningPaths(AppDefinition app)
    {
        ProcessIdToSessionId((uint)Environment.ProcessId, out uint session);
        foreach (string exe in app.Executables) {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) using (process) {
                if (!ProcessIdToSessionId((uint)process.Id, out uint other) || other != session) continue;
                IntPtr handle = OpenProcess(0x1000, false, (uint)process.Id);
                if (handle == IntPtr.Zero) continue;
                string? path = null;
                try {
                    var buffer = new StringBuilder(32768); int size = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref size)) path = buffer.ToString();
                } finally { CloseHandle(handle); }
                if (path != null) yield return path;
            }
        }
    }

    internal static IEnumerable<string> RegistryPaths(AppDefinition app)
    {
        var paths = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
            try {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                foreach (string exe in app.Executables) {
                    using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                    if (key?.GetValue("") is string path) paths.Add(path);
                }
                foreach (string branch in app.Key switch {
                    "Steam" => new[] { @"Software\Valve\Steam" },
                    "Weixin" => new[] { @"Software\Tencent\WeChat", @"Software\Tencent\Weixin" },
                    "WeGame" => new[] { @"Software\Tencent\WeGame" },
                    _ => Array.Empty<string>()
                }) {
                    using var key = root.OpenSubKey(branch);
                    foreach (string value in new[] { "InstallPath", "InstallLocation", "SteamPath", "SteamExe", "Path" })
                        if (key?.GetValue(value) is string path) AddInstallation(paths, app, path);
                }
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (string child in uninstall.GetSubKeyNames()) {
                    try {
                        using var key = uninstall.OpenSubKey(child);
                        string name = key?.GetValue("DisplayName") as string ?? "";
                        if (!app.InstallNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                        if (key?.GetValue("InstallLocation") is string directory) AddInstallation(paths, app, directory);
                        foreach (string value in new[] { "DisplayIcon", "UninstallString" }) {
                            string? executable = ExecutableFromCommand(key?.GetValue(value) as string);
                            if (executable == null) continue;
                            paths.Add(executable);
                            AddInstallation(paths, app, Path.GetDirectoryName(executable));
                        }
                    } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return paths;
    }
    static void AddInstallation(List<string> paths, AppDefinition app, string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return;
        string root = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
        if (root.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { paths.Add(root); return; }
        foreach (string exe in app.Executables) paths.Add(Path.Combine(root, exe));
        foreach (string relative in app.RelativePaths) paths.Add(Path.Combine(root, relative));
    }
    internal static IEnumerable<string> ShortcutPaths(AppDefinition app)
    {
        var paths = new List<string>();
        object? shell = null;
        try {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return paths;
            shell = Activator.CreateInstance(type);
            foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }) {
                string root = Environment.GetFolderPath(folder);
                if (!Directory.Exists(root)) continue;
                try {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (string link in Directory.EnumerateFiles(root, "*.lnk", options).Take(5000)) {
                        object? shortcut = null;
                        try {
                            shortcut = ((dynamic)shell!).CreateShortcut(link);
                            string target = ((dynamic)shortcut).TargetPath;
                            if (app.Executables.Contains(Path.GetFileName(target), StringComparer.OrdinalIgnoreCase)) paths.Add(target);
                        } catch (COMException) { }
                        finally { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); }
                    }
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        } catch (COMException) { }
        finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        return paths;
    }
    static IEnumerable<string> CommonPaths(AppDefinition app)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        foreach (var drive in DriveInfo.GetDrives()) {
            try {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                foreach (string directory in new[] { "", "Program Files", "Program Files (x86)", "Games", "Tencent", "腾讯游戏" })
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, directory));
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).SelectMany(r => app.RelativePaths.Select(p => Path.Combine(r, p)));
    }

    [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
