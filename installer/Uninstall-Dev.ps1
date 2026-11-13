[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\SwSearch'),
    [switch]$KeepFiles
)

$ErrorActionPreference = 'Stop'
$installRootFull = [System.IO.Path]::GetFullPath($InstallRoot)
$registrationTool = Join-Path $installRootFull 'Tools\SwSearch.ShellRegistration.exe'

if (Test-Path -LiteralPath $registrationTool) {
    Write-Host '即将按用户显式请求删除 HKCU 下的 SwSearch.ViewProperties 产品键。'
    & $registrationTool uninstall
    if ($LASTEXITCODE -ne 0) {
        throw "右键菜单卸载失败，退出码：$LASTEXITCODE"
    }
} else {
    Write-Warning '未找到已发布的注册工具；请先从源码运行 unregister 命令，或确认右键菜单本来未安装。'
}

if (-not $KeepFiles -and (Test-Path -LiteralPath $installRootFull)) {
    $defaultParent = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
    $defaultPrefix = $defaultParent.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $installRootFull.StartsWith($defaultPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw '为避免误删，自动清理仅允许位于 LocalAppData\Programs 下；请使用 -KeepFiles 后手动处理自定义目录。'
    }

    Remove-Item -LiteralPath $installRootFull -Recurse -Force
    Write-Host "已删除开发安装目录：$installRootFull"
}
