$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'SwSearch.ShellRegistration.exe'
& $tool uninstall
exit $LASTEXITCODE
