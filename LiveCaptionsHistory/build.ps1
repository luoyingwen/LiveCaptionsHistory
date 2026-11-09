$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot '..\LiveCaptionsHistory.Tests\LiveCaptionsHistory.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project (Join-Path $PSScriptRoot '..\LiveCaptionsHistory.LifecycleTests\LiveCaptionsHistory.LifecycleTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $PSScriptRoot 'LiveCaptionsHistory.csproj') -c Release -o (Join-Path $PSScriptRoot 'dist') --self-contained false --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $PSScriptRoot '字幕历史.lnk'))
$shortcut.TargetPath = Join-Path $PSScriptRoot 'dist\LiveCaptionsHistory.exe'
$shortcut.WorkingDirectory = Join-Path $PSScriptRoot 'dist'
$shortcut.Description = '独立本机语音识别与可滚动字幕历史'
$shortcut.Save()
exit 0
