$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'SwSearch.ShellRegistration.exe'
$viewer = Join-Path $PSScriptRoot 'SwSearch.PropertyViewer.exe'
& $tool install --viewer $viewer
exit $LASTEXITCODE
