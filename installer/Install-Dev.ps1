[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\SwSearch')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$installRootFull = [System.IO.Path]::GetFullPath($InstallRoot)

Write-Host "正在发布到 $installRootFull"
dotnet publish (Join-Path $repositoryRoot 'src\SwSearch.App\SwSearch.App.csproj') -c Release --no-self-contained -o (Join-Path $installRootFull 'App')
dotnet publish (Join-Path $repositoryRoot 'src\SwSearch.PropertyViewer\SwSearch.PropertyViewer.csproj') -c Release --no-self-contained -o (Join-Path $installRootFull 'Viewer')
dotnet publish (Join-Path $repositoryRoot 'src\SwSearch.ShellRegistration\SwSearch.ShellRegistration.csproj') -c Release --no-self-contained -o (Join-Path $installRootFull 'Tools')

$viewer = Join-Path $installRootFull 'Viewer\SwSearch.PropertyViewer.exe'
$registrationTool = Join-Path $installRootFull 'Tools\SwSearch.ShellRegistration.exe'

Write-Host '即将按用户显式请求写入 HKCU 右键菜单。'
& $registrationTool install --viewer $viewer
if ($LASTEXITCODE -ne 0) {
    throw "右键菜单注册失败，退出码：$LASTEXITCODE"
}

Write-Host '开发环境安装完成。主程序位置：'
Write-Host (Join-Path $installRootFull 'App\SwSearch.App.exe')
