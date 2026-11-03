param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch' }
$repoRoot = $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts'
$folderName = "big-head-desktop-pet-zed-v$Version-win-x64"
$publishDirectory = Join-Path $artifactRoot $folderName
New-Item -ItemType Directory -Force $publishDirectory | Out-Null
dotnet publish (Join-Path $repoRoot 'src\DesktopPet.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item (Join-Path $repoRoot 'README.md') $publishDirectory -Force
Copy-Item (Join-Path $repoRoot 'third_party') $publishDirectory -Recurse -Force
$archive = Join-Path $artifactRoot ($folderName + '.zip')
Compress-Archive -LiteralPath $publishDirectory -DestinationPath $archive -Force
$digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $artifactRoot 'SHA256SUMS.txt'), "$digest  $folderName.zip`n")
Write-Output $archive
