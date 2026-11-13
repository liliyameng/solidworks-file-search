[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$UserTestCandidate
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$coordinatorProject = Join-Path $repoRoot 'src\SwSearch.InstallerCoordinator\SwSearch.InstallerCoordinator.csproj'
$bridgeSource = Join-Path $repoRoot 'src\SwSearch.InstallerMsiBridge\InstallerMsiBridge.cpp'
$bridgeGateSource = Join-Path $repoRoot 'src\SwSearch.InstallerMsiBridgeGate\InstallerMsiBridgeGate.cpp'
$commandLineGateSource = Join-Path $repoRoot 'src\SwSearch.InstallerMsiCommandLineGate\InstallerMsiCommandLineGate.cpp'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$restoreGraphGate = Join-Path $PSScriptRoot 'Assert-FrozenRestoreGraph.ps1'

$restoreGateArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$restoreGraphGate,'-Root',$repoRoot)
if ($env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH -ceq '1') {
    $restoreGateArguments = @('-NoProfile','-Command','exit 0')
}
if ($UserTestCandidate) { $restoreGateArguments += '-UserTestCandidate' }
& powershell @restoreGateArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Frozen zero-restore graph preflight failed; installer action build stopped.'
}

if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'INSTALLER_BRIDGE_TOOLCHAIN_MISSING: Visual Studio Build Tools with MSVC x64 and Windows SDK are required.'
}
$installationPath = (& $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.Windows11SDK.26100 `
    -property installationPath | Select-Object -First 1)
if (-not $installationPath) {
    throw 'INSTALLER_BRIDGE_TOOLCHAIN_MISSING: MSVC x64 plus Windows 11 SDK 26100 were not found.'
}
$vcvars = Join-Path $installationPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf)) {
    throw "INSTALLER_BRIDGE_TOOLCHAIN_MISSING: $vcvars"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output | Out-Null
$coordinatorOutput = Join-Path $output 'coordinator-publish'
$bridgeOutput = Join-Path $output 'SwSearch.InstallerMsiBridge.dll'
$bridgeGateOutput = Join-Path $output 'SwSearch.InstallerMsiBridgeGate.exe'
$commandLineGateOutput = Join-Path $output 'SwSearch.InstallerMsiCommandLineGate.exe'
$privateAssets = $null
$privateAssetsCache = $null

$publishExtraProperties = @()
if ($UserTestCandidate) {
    $sourceAssets = Join-Path $repoRoot 'src\SwSearch.InstallerCoordinator\obj\project.assets.json'
    $privateAssets = Join-Path $output 'coordinator.project.assets.json'
    $privateAssetsCache = Join-Path $output 'coordinator.project.assets.cache'
    $assetsText = [IO.File]::ReadAllText($sourceAssets)
    $userPackageRoot = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.nuget\packages')).TrimEnd('\') + '\'
    $workspacePackageRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.packages-rc48-r3')).TrimEnd('\') + '\'
    $assetsText = $assetsText.Replace($userPackageRoot.Replace('\','\\'), $workspacePackageRoot.Replace('\','\\'))
    $assetsText = $assetsText.Replace($userPackageRoot, $workspacePackageRoot)
    [IO.File]::WriteAllText($privateAssets, $assetsText, [Text.UTF8Encoding]::new($false))
    $publishExtraProperties = @(
        "-p:ProjectAssetsFile=$privateAssets",
        "-p:ProjectAssetsCacheFile=$privateAssetsCache",
        "-p:NuGetPackageRoot=$workspacePackageRoot",
        "-p:NuGetPackageFolders=$workspacePackageRoot",
        "-p:PkgMicrosoft_NET_ILLink_Tasks=$(Join-Path $workspacePackageRoot 'microsoft.net.illink.tasks\8.0.27')"
    )
}
& dotnet publish $coordinatorProject -c $Configuration -r $RuntimeIdentifier --self-contained false --no-restore `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:DebugType=None -p:DebugSymbols=false -p:EnableCompressionInSingleFile=false `
    -o $coordinatorOutput @publishExtraProperties
if ($LASTEXITCODE -ne 0) { throw "Installer coordinator publish failed: $LASTEXITCODE" }
& powershell @restoreGateArguments
if ($LASTEXITCODE -ne 0) { throw 'Frozen zero-restore graph changed during coordinator publish.' }
$coordinatorFiles = @(Get-ChildItem -LiteralPath $coordinatorOutput -File)
if ($coordinatorFiles.Count -ne 1 -or $coordinatorFiles[0].Name -cne 'SwSearch.InstallerCoordinator.exe') {
    throw "Coordinator must publish as exactly one framework-dependent single file; found: $($coordinatorFiles.Name -join ', ')"
}
$coordinatorBinary = Join-Path $output 'SwSearch.InstallerCoordinator.exe'
Move-Item -LiteralPath $coordinatorFiles[0].FullName -Destination $coordinatorBinary
Remove-Item -LiteralPath $coordinatorOutput -Recurse -Force
foreach ($privateRestoreArtifact in @($privateAssets, $privateAssetsCache)) {
    if ($privateRestoreArtifact -and (Test-Path -LiteralPath $privateRestoreArtifact)) {
        Remove-Item -LiteralPath $privateRestoreArtifact -Force
    }
}

$bridgeObject = Join-Path $output 'InstallerMsiBridge.obj'
$bridgeImportLibrary = Join-Path $output 'InstallerMsiBridge.lib'
$compileCommand = 'call "{0}" >nul && cl.exe /nologo /std:c++20 /EHsc /O2 /GL /MT /GS /guard:cf /DUNICODE /D_UNICODE /LD /Fo:"{1}" /Fe:"{2}" "{3}" /link /LTCG /OPT:REF /OPT:ICF /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /MACHINE:X64 /IMPLIB:"{4}" msi.lib bcrypt.lib advapi32.lib crypt32.lib' -f `
    $vcvars, $bridgeObject, $bridgeOutput, $bridgeSource, $bridgeImportLibrary
& $env:ComSpec /d /s /c $compileCommand
if ($LASTEXITCODE -ne 0) { throw "Native MSI bridge build failed: $LASTEXITCODE" }
foreach ($intermediate in @(
    $bridgeObject,
    $bridgeImportLibrary,
    [IO.Path]::ChangeExtension($bridgeOutput, '.exp'),
    [IO.Path]::ChangeExtension($bridgeImportLibrary, '.exp'))) {
    if (Test-Path -LiteralPath $intermediate) { Remove-Item -LiteralPath $intermediate -Force }
}

$bridgeGateObject = Join-Path $output 'InstallerMsiBridgeGate.obj'
$gateCompileCommand = 'call "{0}" >nul && cl.exe /nologo /std:c++20 /EHsc /O2 /GL /MT /GS /guard:cf /DUNICODE /D_UNICODE /Fo:"{1}" /Fe:"{2}" "{3}" /link /LTCG /OPT:REF /OPT:ICF /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /MACHINE:X64 bcrypt.lib' -f `
    $vcvars, $bridgeGateObject, $bridgeGateOutput, $bridgeGateSource
& $env:ComSpec /d /s /c $gateCompileCommand
if ($LASTEXITCODE -ne 0) { throw "Native MSI bridge isolated gate build failed: $LASTEXITCODE" }
if (Test-Path -LiteralPath $bridgeGateObject) { Remove-Item -LiteralPath $bridgeGateObject -Force }

$commandLineGateObject = Join-Path $output 'InstallerMsiCommandLineGate.obj'
$commandLineGateCompileCommand = 'call "{0}" >nul && cl.exe /nologo /std:c++20 /EHsc /O2 /GL /MT /GS /guard:cf /DUNICODE /D_UNICODE /Fo:"{1}" /Fe:"{2}" "{3}" /link /LTCG /OPT:REF /OPT:ICF /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /MACHINE:X64 msi.lib ole32.lib' -f `
    $vcvars, $commandLineGateObject, $commandLineGateOutput, $commandLineGateSource
& $env:ComSpec /d /s /c $commandLineGateCompileCommand
if ($LASTEXITCODE -ne 0) { throw "Native MSI command-line gate build failed: $LASTEXITCODE" }
if (Test-Path -LiteralPath $commandLineGateObject) { Remove-Item -LiteralPath $commandLineGateObject -Force }

$dumpbin = Get-ChildItem -LiteralPath (Join-Path $installationPath 'VC\Tools\MSVC') -Filter dumpbin.exe -Recurse -File |
    Where-Object FullName -Match '\\Hostx64\\x64\\dumpbin\.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $dumpbin) { throw 'dumpbin.exe x64 was not found after bridge build.' }
$headers = & $dumpbin.FullName /headers $bridgeOutput 2>&1 | Out-String
$dependents = & $dumpbin.FullName /dependents $bridgeOutput 2>&1 | Out-String
if ($headers -notmatch '(?im)^\s*8664 machine \(x64\)') { throw 'MSI bridge is not an AMD64 PE.' }
$exports = & $dumpbin.FullName /exports $bridgeOutput 2>&1 | Out-String
foreach ($requiredExport in @('CaptureTrustedInstallerContext','CheckSwSearchDotNet8','InstallModernMenuCertificate','RemoveModernMenuCertificate','SwSearchInstallerTokenPolicySelfTest')) {
    if ($exports -notmatch "(?m)\b$([regex]::Escape($requiredExport))\b") {
        throw "MSI bridge export $requiredExport is missing."
    }
}
if ($dependents -match '(?i)VCRUNTIME|MSVCP|UCRTBASE|api-ms-win-crt') {
    throw 'MSI bridge has a forbidden target-machine VC/UCRT runtime dependency.'
}
$dependencyNames = @([regex]::Matches($dependents, '(?im)^\s*([A-Z0-9_.-]+\.dll)\s*$') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$allowedBridgeDependencies = @('ADVAPI32.dll','bcrypt.dll','CRYPT32.dll','KERNEL32.dll','MSI.dll')
if ($dependencyNames.Count -lt 3 -or @($dependencyNames | Where-Object { $_ -notin $allowedBridgeDependencies }).Count -ne 0) {
    throw "MSI bridge dependency allowlist mismatch: $($dependencyNames -join ', ')"
}
foreach ($requiredDependency in @('ADVAPI32.dll','bcrypt.dll','CRYPT32.dll','MSI.dll')) {
    if ($dependencyNames -notcontains $requiredDependency) {
        throw "MSI bridge required dependency missing: $requiredDependency"
    }
}

$commandGateHeaders = & $dumpbin.FullName /headers $commandLineGateOutput 2>&1 | Out-String
$commandGateDependents = & $dumpbin.FullName /dependents $commandLineGateOutput 2>&1 | Out-String
if ($commandGateHeaders -notmatch '(?im)^\s*8664 machine \(x64\)') {
    throw 'MSI command-line gate is not an AMD64 PE.'
}
if ($commandGateDependents -match '(?i)VCRUNTIME|MSVCP|UCRTBASE|api-ms-win-crt') {
    throw 'MSI command-line gate has a forbidden target-machine VC/UCRT runtime dependency.'
}
$commandGateDependencyNames = @([regex]::Matches($commandGateDependents, '(?im)^\s*([A-Z0-9_.-]+\.dll)\s*$') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$allowedCommandGateDependencies = @('KERNEL32.dll','MSI.dll','OLE32.dll')
if ($commandGateDependencyNames.Count -lt 3 -or
    @($commandGateDependencyNames | Where-Object { $_ -notin $allowedCommandGateDependencies }).Count -ne 0 -or
    @($allowedCommandGateDependencies | Where-Object { $_ -notin $commandGateDependencyNames }).Count -ne 0) {
    throw "MSI command-line gate dependency allowlist mismatch: $($commandGateDependencyNames -join ', ')"
}

$allowed = @(
    'SwSearch.InstallerCoordinator.exe',
    'SwSearch.InstallerMsiBridge.dll',
    'SwSearch.InstallerMsiBridgeGate.exe',
    'SwSearch.InstallerMsiCommandLineGate.exe')
$unexpected = @(Get-ChildItem -LiteralPath $output -File | Where-Object Name -NotIn $allowed)
if ($unexpected.Count -ne 0) { throw "Unexpected installer action output: $($unexpected.Name -join ', ')" }
$manifestPath = Join-Path $output 'installer-actions.sha256.json'
$manifest = [ordered]@{
    schema = 2
    runtime = $RuntimeIdentifier
    coordinator = [ordered]@{
        file = 'SwSearch.InstallerCoordinator.exe'
        bytes = (Get-Item -LiteralPath $coordinatorBinary).Length
        sha256 = (Get-FileHash -LiteralPath $coordinatorBinary -Algorithm SHA256).Hash
    }
    bridge = [ordered]@{
        file = 'SwSearch.InstallerMsiBridge.dll'
        bytes = (Get-Item -LiteralPath $bridgeOutput).Length
        sha256 = (Get-FileHash -LiteralPath $bridgeOutput -Algorithm SHA256).Hash
    }
    bridgeGate = [ordered]@{
        file = 'SwSearch.InstallerMsiBridgeGate.exe'
        bytes = (Get-Item -LiteralPath $bridgeGateOutput).Length
        sha256 = (Get-FileHash -LiteralPath $bridgeGateOutput -Algorithm SHA256).Hash
    }
    commandLineGate = [ordered]@{
        file = 'SwSearch.InstallerMsiCommandLineGate.exe'
        bytes = (Get-Item -LiteralPath $commandLineGateOutput).Length
        sha256 = (Get-FileHash -LiteralPath $commandLineGateOutput -Algorithm SHA256).Hash
    }
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Output $manifestPath
