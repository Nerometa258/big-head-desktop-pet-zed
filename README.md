# 大脑袋桌宠 Zed

一个可拖动、可按压、有声音反馈的 Windows 桌面宠物。当前版本：**v1.0.0**。

<img src="assets/pet.png" alt="大脑袋桌宠" height="152" />

## 下载运行

前往 [v1.0.0 下载页](https://github.com/Nerometa258/big-head-desktop-pet-zed/releases/tag/v1.0.0)，下载 `big-head-desktop-pet-zed-v1.0.0-win-x64.zip`。

完整解压后，双击 **大脑袋桌宠.exe**。支持 Windows 10/11 x64，运行环境已包含，无需另装 .NET。请保留整个文件夹，不能只复制 EXE。

## 使用方式

- 默认显示大小 152px，透明背景、桌面置顶。
- 左键短按不足 200ms：播放短按音频。
- 左键长按满 200ms：播放长按音频，每次按住只触发一次。
- 按住保持压扁，松手弹性回弹；长按松手不触发额外声音。
- 按住左键移动，可以在桌面拖动桌宠。
- 右键菜单：打开微信、打开 Steam、打开 WeGame、打开 5E 对战平台、退出桌宠。

## 自动识别软件

在每位使用者的电脑上查找运行进程、安装注册信息、开始菜单/桌面快捷方式和常见安装目录。未找到时，选择软件的启动 EXE 一次即可记住位置。支持新版 `Weixin.exe` 和旧版 `WeChat.exe`。

已运行的软件优先恢复现有界面。微信关闭到后台时，通过微信自己的托盘图标重新打开，避免直接显示隐藏窗口。微信和 WeGame 的托盘唤起会短暂移动鼠标并恢复原位置。

首次启动需要管理员权限的软件时，保留 Windows UAC 提示；取消后静默返回。后台托盘唤起依赖应用的托盘图标及 Windows 资源管理器正常工作。不同应用版本可能存在界面兼容差异，特殊安装位置可手动选择。

## 配置与素材

本机设置保存在 `%LOCALAPPDATA%\BigHeadDesktopPet`，包含窗口位置、手动设置的软件路径及诊断日志。发布包不包含维护者的账号、聊天记录、本机路径配置或运行日志。

`assets/pet.png` 是宠物图片；`assets/press.mp3` 是短按声音，`assets/release.mp3` 是长按声音。

## 从源码构建

需要 .NET 10 SDK，在仓库根目录执行：

```powershell
dotnet publish src/DesktopPet.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -o artifacts/publish
```

图片和音频会自动复制到输出目录。也可以执行 `./build-release.ps1`，生成带版本号的 ZIP 和 SHA256 校验文件。

测试及兼容性验证范围见 [验证说明](docs/verification.txt)。
