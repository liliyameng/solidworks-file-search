[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$gate = Join-Path $repoRoot 'installer\Assert-FrozenRestoreGraph.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'swsearch-frozen-production-' + [Guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-ThrowsCode {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Code
    )
    try {
        & $Action
        throw "Expected failure was not raised: $Code"
    }
    catch {
        if (-not $_.Exception.Message.Contains($Code)) { throw }
    }
}

function Write-CanonicalConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Packages
    )
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="SwSearchOffline" value="$Source" />
  </packageSources>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <config>
    <add key="globalPackagesFolder" value="$Packages" />
    <add key="auditMode" value="direct" />
  </config>
</configuration>
"@
    [IO.File]::WriteAllText($Path, $xml, $utf8)
}

function Add-ZipFile {
    param(
        [Parameter(Mandatory = $true)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )
    $entry = $Archive.CreateEntry($Path, [IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    finally { $stream.Dispose() }
}

try {
    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

    # Dot-sourcing stops at the intentionally missing lock only after all
    # production helper functions have been defined in this test scope.
    $missingLock = Join-Path $fixtureRoot 'missing.lock.json'
    try {
        . $gate -Root $fixtureRoot -LockPath $missingLock -UserTestCandidate
        throw 'Expected failure was not raised: FROZEN_RESTORE_GRAPH_LOCK_MISSING'
    }
    catch {
        if (-not $_.Exception.Message.Contains('FROZEN_RESTORE_GRAPH_LOCK_MISSING')) {
            throw
        }
    }
    foreach ($functionName in @(
            'Assert-CanonicalNuGetConfig',
            'Assert-FrozenSdkIdentity',
            'Assert-WorkspacePackageMaterial',
            'Assert-WorkspacePackageDirectorySet')) {
        if (-not (Get-Command -Name $functionName -CommandType Function -ErrorAction SilentlyContinue)) {
            throw "Production helper was not loaded: $functionName"
        }
    }

    $installer = Join-Path $fixtureRoot 'installer'
    New-Item -ItemType Directory -Path $installer | Out-Null
    $config = Join-Path $installer 'NuGet.Offline.config'
    Write-CanonicalConfig -Path $config -Source '.offline-feed' -Packages '..\.packages-rc48-r2'
    Assert-CanonicalNuGetConfig -Path $config -ExpectedSource '.offline-feed' `
        -ExpectedPackageFolder '..\.packages-rc48-r2' -Label 'production fixture'
    Write-CanonicalConfig -Path $config -Source 'https://example.invalid/v3/index.json' `
        -Packages '..\.packages-rc48-r2'
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_NETWORK_CONFIG' -Action {
        Assert-CanonicalNuGetConfig -Path $config -ExpectedSource '.offline-feed' `
            -ExpectedPackageFolder '..\.packages-rc48-r2' -Label 'network fixture'
    }
    Write-CanonicalConfig -Path $config -Source '.offline-feed' -Packages '..\.wrong-cache'
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_CONFIG_VALUES' -Action {
        Assert-CanonicalNuGetConfig -Path $config -ExpectedSource '.offline-feed' `
            -ExpectedPackageFolder '..\.packages-rc48-r2' -Label 'cache fixture'
    }

    $UserTestCandidate = $false
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_SDK_SET' -Action {
        Assert-FrozenSdkIdentity -Sdk ([pscustomobject]@{
                version = '8.0.422'
                marker = 'C:/Program Files/dotnet/sdk/8.0.422/.version'
                bytes = 87L
                sha256 = ('0' * 64)
            })
    }
    Assert-FrozenSdkIdentity -Sdk $lockedSdk

    $workspacePackageRoot = Join-Path $fixtureRoot '.packages-rc48-r2'
    $feedRoot = Join-Path $installer '.offline-feed'
    $packageId = 'fixture.package'
    $packageVersion = '1.0.0'
    $versionRoot = Join-Path $workspacePackageRoot "$packageId\$packageVersion"
    New-Item -ItemType Directory -Path $versionRoot, $feedRoot | Out-Null

    $feedNupkg = Join-Path $feedRoot "$packageId.$packageVersion.nupkg"
    $fileStream = [IO.File]::Open(
        $feedNupkg,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $fileStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            Add-ZipFile -Archive $archive -Path 'lib/net8.0/fixture.dll' `
                -Bytes ([Text.Encoding]::UTF8.GetBytes('payload'))
            Add-ZipFile -Archive $archive -Path '_rels/.rels' `
                -Bytes ([Text.Encoding]::UTF8.GetBytes('opc'))
            Add-ZipFile -Archive $archive -Path '[Content_Types].xml' `
                -Bytes ([Text.Encoding]::UTF8.GetBytes('opc'))
            Add-ZipFile -Archive $archive `
                -Path 'package/services/metadata/core-properties/fixture.psmdcp' `
                -Bytes ([Text.Encoding]::UTF8.GetBytes('opc'))
        }
        finally { $archive.Dispose() }
    }
    finally { $fileStream.Dispose() }

    $packageNupkg = Join-Path $versionRoot "$packageId.$packageVersion.nupkg"
    [IO.File]::Copy($feedNupkg, $packageNupkg)
    $contentStream = [IO.File]::OpenRead($feedNupkg)
    try {
        $sha512 = [Security.Cryptography.SHA512]::Create()
        try { $contentHash = [Convert]::ToBase64String($sha512.ComputeHash($contentStream)) }
        finally { $sha512.Dispose() }
    }
    finally { $contentStream.Dispose() }
    $shaMarker = Join-Path $versionRoot "$packageId.$packageVersion.nupkg.sha512"
    [IO.File]::WriteAllText($shaMarker, $contentHash, $utf8)
    $metadataPath = Join-Path $versionRoot '.nupkg.metadata'
    $metadata = [ordered]@{
        version = 2
        contentHash = $contentHash
        source = [IO.Path]::GetFullPath($feedRoot)
    }
    [IO.File]::WriteAllText(
        $metadataPath,
        ($metadata | ConvertTo-Json -Depth 3),
        $utf8)
    $expandedDirectory = Join-Path $versionRoot 'lib\net8.0'
    New-Item -ItemType Directory -Path $expandedDirectory | Out-Null
    $expandedFile = Join-Path $expandedDirectory 'fixture.dll'
    [IO.File]::WriteAllText($expandedFile, 'payload', $utf8)

    $workspacePackages = [IO.Path]::GetFullPath($workspacePackageRoot).TrimEnd('\') + '\'
    $offlineFeed = [IO.Path]::GetFullPath($feedRoot).TrimEnd('\') + '\'
    $script:seenWorkspacePackages = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    Assert-WorkspacePackageMaterial -PackageId $packageId -PackageVersion $packageVersion `
        -Label 'production material positive'
    Assert-WorkspacePackageDirectorySet

    [IO.File]::WriteAllText($expandedFile, 'PAYLOAD', $utf8)
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_HASH' -Action {
        Assert-WorkspacePackageMaterial -PackageId $packageId -PackageVersion $packageVersion `
            -Label 'expanded hash fixture'
    }
    [IO.File]::WriteAllText($expandedFile, 'payload', $utf8)

    $extraFile = Join-Path $versionRoot 'extra.bin'
    [IO.File]::WriteAllText($extraFile, 'extra', $utf8)
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_EXTRA' -Action {
        Assert-WorkspacePackageMaterial -PackageId $packageId -PackageVersion $packageVersion `
            -Label 'expanded extra fixture'
    }
    Remove-Item -LiteralPath $extraFile -Force

    Remove-Item -LiteralPath $expandedFile -Force
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_MISSING' -Action {
        Assert-WorkspacePackageMaterial -PackageId $packageId -PackageVersion $packageVersion `
            -Label 'expanded missing fixture'
    }
    [IO.File]::WriteAllText($expandedFile, 'payload', $utf8)

    $metadata.source = [IO.Path]::GetFullPath((Join-Path $fixtureRoot 'wrong-feed'))
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 3), $utf8)
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_PACKAGE_SOURCE_MISMATCH' -Action {
        Assert-WorkspacePackageMaterial -PackageId $packageId -PackageVersion $packageVersion `
            -Label 'metadata source fixture'
    }
    $metadata.source = [IO.Path]::GetFullPath($feedRoot)
    [IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 3), $utf8)

    $extraPackageRoot = Join-Path $workspacePackageRoot 'extra.package'
    New-Item -ItemType Directory -Path $extraPackageRoot | Out-Null
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_PACKAGE_ID_EMPTY' -Action {
        Assert-WorkspacePackageDirectorySet
    }
    $extraVersionRoot = Join-Path $extraPackageRoot '1.0.0'
    New-Item -ItemType Directory -Path $extraVersionRoot | Out-Null
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_PACKAGE_DIRECTORY_SET' -Action {
        Assert-WorkspacePackageDirectorySet
    }
    [IO.Directory]::Delete($extraVersionRoot, $false)
    [IO.Directory]::Delete($extraPackageRoot, $false)

    $rootFile = Join-Path $workspacePackageRoot 'unexpected.txt'
    [IO.File]::WriteAllText($rootFile, 'unexpected', $utf8)
    Assert-ThrowsCode -Code 'FROZEN_RESTORE_GRAPH_PACKAGE_ROOT_FILES' -Action {
        Assert-WorkspacePackageDirectorySet
    }

    Write-Output 'PASS frozen-restore-production-config-sdk-package-boundaries'
}
finally {
    $full = [IO.Path]::GetFullPath($fixtureRoot)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notmatch '^swsearch-frozen-production-[0-9a-f]{32}$') {
        throw "Unsafe fixture cleanup path: $full"
    }
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
    if (Test-Path -LiteralPath $full) { throw "Fixture cleanup failed: $full" }
}
