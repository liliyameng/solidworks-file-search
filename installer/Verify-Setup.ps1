[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$setupPath = Join-Path $artifactsRoot 'SwSearch-Setup-x64.exe'
$msiPath = Join-Path $artifactsRoot 'SwSearch-x64.msi'
$inspectionRoot = Join-Path $artifactsRoot 'installer-inspection'
$reportPath = Join-Path $artifactsRoot 'installer-verification.txt'
$wixPath = Join-Path $repoRoot '.packages-rc48-r3\wixtoolset.sdk\5.0.2\tools\net472\x64\wix.exe'

foreach ($path in @($setupPath, $msiPath, $wixPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing installer verification input: $path"
    }
}

$setupBytes = [System.IO.File]::ReadAllBytes($setupPath)
if ($setupBytes.Length -lt 2 -or $setupBytes[0] -ne 0x4D -or $setupBytes[1] -ne 0x5A) {
    throw 'Setup is not a Windows PE executable.'
}
$msiBytes = [System.IO.File]::ReadAllBytes($msiPath)
$oleHeader = @(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)
for ($i = 0; $i -lt $oleHeader.Count; $i++) {
    if ($msiBytes[$i] -ne $oleHeader[$i]) { throw 'MSI compound-file header is invalid.' }
}

if (Test-Path -LiteralPath $inspectionRoot) {
    Remove-Item -LiteralPath $inspectionRoot -Recurse -Force
}
$bundleRoot = Join-Path $inspectionRoot 'bundle'
$baRoot = Join-Path $inspectionRoot 'ba'
$decompiledPath = Join-Path $inspectionRoot 'decompiled.wxs'
New-Item -ItemType Directory -Force -Path $inspectionRoot | Out-Null

& $wixPath msi decompile $msiPath -o $decompiledPath
if ($LASTEXITCODE -ne 0) { throw 'WiX MSI decompile verification failed.' }
& $wixPath burn extract $setupPath -o $bundleRoot -oba $baRoot
if ($LASTEXITCODE -ne 0) { throw 'WiX Burn extraction verification failed.' }

$decompiled = Get-Content -LiteralPath $decompiledPath -Raw -Encoding UTF8
$manifestPath = Join-Path $baRoot 'manifest.xml'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8

$requiredMsiText = @(
    'Scope="perUser"',
    '<Directory Id="LocalProgramsFolder" Name="Programs">',
    '<Directory Id="INSTALLFOLDER" Name="SwSearch">',
    '<Feature Id="MainFeature"',
    '<Feature Id="ContextMenuFeature"',
    '<Feature Id="DesktopShortcutFeature"',
    'Id="StartMenuShortcut"',
    'Id="DesktopShortcut"',
    'Software\Classes\SystemFileAssociations\.sldprt\shell\SwSearch.ViewProperties',
    'Software\Classes\SystemFileAssociations\.sldasm\shell\SwSearch.ViewProperties',
    'Software\Classes\SystemFileAssociations\.slddrw\shell\SwSearch.ViewProperties',
    'Name="SwSearch.App.exe"',
    'Name="SwSearch.PropertyViewer.exe"',
    'Name="SwSearch.Metadata.Worker.exe"',
    'Name="SwSearch.ShellRegistration.exe"'
)
foreach ($expected in $requiredMsiText) {
    if (-not $decompiled.Contains($expected)) { throw "MSI structure missing: $expected" }
}
foreach ($expected in @('EngineVersion="5.0.2.0"', 'Win64="yes"', 'PerMachine="no"', 'FilePath="SwSearch-x64.msi"', 'Packaging="embedded"')) {
    if (-not $manifest.Contains($expected)) { throw "Burn manifest missing: $expected" }
}

$embeddedMsi = Get-ChildItem -LiteralPath $bundleRoot -Filter 'SwSearch-x64.msi' -Recurse | Select-Object -First 1
if (-not $embeddedMsi) { throw 'Burn bundle did not contain SwSearch-x64.msi.' }
$msiHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash
$embeddedMsiHash = (Get-FileHash -LiteralPath $embeddedMsi.FullName -Algorithm SHA256).Hash
if ($msiHash -ne $embeddedMsiHash) { throw 'Embedded MSI hash differs from the standalone MSI.' }

$setupInfo = Get-Item -LiteralPath $setupPath
$msiInfo = Get-Item -LiteralPath $msiPath
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$fileCount = ([regex]::Matches($decompiled, '<File Id=')).Count
$featureCount = ([regex]::Matches($decompiled, '<Feature Id=')).Count
$contextMenuCount = ([regex]::Matches($decompiled, 'SystemFileAssociations\\\.sld(?:prt|asm|drw)\\shell\\SwSearch\.ViewProperties\\command')).Count

$lines = @(
    'SwSearch installer verification',
    "Generated: $([DateTimeOffset]::Now.ToString('u'))",
    'Method: WiX Toolset 5.0.2 MSI decompile plus Burn container extraction',
    'Installation executed: no',
    'Registry modified: no',
    "Setup path: $setupPath",
    "Setup bytes: $($setupInfo.Length)",
    "Setup SHA256: $setupHash",
    "MSI path: $msiPath",
    "MSI bytes: $($msiInfo.Length)",
    "MSI SHA256: $msiHash",
    "Embedded MSI SHA256: $embeddedMsiHash",
    "MSI file rows: $fileCount",
    "MSI features: $featureCount (main, optional context menu, optional desktop shortcut)",
    "Context menu command rows: $contextMenuCount",
    'Burn engine: 5.0.2.0 x64, per-user, embedded MSI',
    'Archive technology: WiX Burn/MSI; no 7-Zip, 7z, self-extracting 7z, or 7z format'
)
$lines | Set-Content -LiteralPath $reportPath -Encoding UTF8
$lines
Remove-Item -LiteralPath $inspectionRoot -Recurse -Force
