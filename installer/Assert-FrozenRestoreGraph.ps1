[CmdletBinding()]
param(
    [string]$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$LockPath,
    [switch]$UserTestCandidate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ([String]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'restore-assets.lock.json'))
}
$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
$packageDirectoryName = if ($UserTestCandidate) { '.packages' } else { '.packages-rc48-r3' }
$feedDirectoryName = if ($UserTestCandidate) { 'offline-feed' } else { '.offline-feed' }
$workspacePackages = [IO.Path]::GetFullPath((Join-Path $rootPath $packageDirectoryName)).TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar
$offlineConfig = [IO.Path]::GetFullPath((Join-Path $rootPath 'installer\NuGet.Offline.config'))
$sdkResolverConfig = [IO.Path]::GetFullPath((Join-Path $rootPath 'NuGet.config'))
$offlineFeed = [IO.Path]::GetFullPath((Join-Path $rootPath "installer\$feedDirectoryName")).TrimEnd('\', '/') +
    [IO.Path]::DirectorySeparatorChar
$restoreGraphProject = [IO.Path]::GetFullPath((Join-Path $rootPath 'installer\RestoreGraph.proj'))
$script:seenWorkspacePackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$manualPackageReferenceProjects = @(
    'src/SwSearch.App/SwSearch.App.csproj',
    'src/SwSearch.Integration/SwSearch.Integration.csproj',
    'src/SwSearch.PropertyViewer/SwSearch.PropertyViewer.csproj'
)
$lockedProjectDgspecs = [ordered]@{
    'installer/wix/obj/project.assets.json' = 'installer/wix/obj/SwSearch.Package.wixproj.nuget.dgspec.json'
    'src/SwSearch.App/obj/project.assets.json' = 'src/SwSearch.App/obj/SwSearch.App.csproj.nuget.dgspec.json'
    'src/SwSearch.Broker/obj/project.assets.json' = 'src/SwSearch.Broker/obj/SwSearch.Broker.csproj.nuget.dgspec.json'
    'src/SwSearch.Contracts/obj/project.assets.json' = 'src/SwSearch.Contracts/obj/SwSearch.Contracts.csproj.nuget.dgspec.json'
    'src/SwSearch.Everything/obj/project.assets.json' = 'src/SwSearch.Everything/obj/SwSearch.Everything.csproj.nuget.dgspec.json'
    'src/SwSearch.Indexer/obj/project.assets.json' = 'src/SwSearch.Indexer/obj/SwSearch.Indexer.csproj.nuget.dgspec.json'
    'src/SwSearch.InstallerCoordinator/obj/project.assets.json' = 'src/SwSearch.InstallerCoordinator/obj/SwSearch.InstallerCoordinator.csproj.nuget.dgspec.json'
    'src/SwSearch.Integration/obj/project.assets.json' = 'src/SwSearch.Integration/obj/SwSearch.Integration.csproj.nuget.dgspec.json'
    'src/SwSearch.Metadata.Worker/obj/project.assets.json' = 'src/SwSearch.Metadata.Worker/obj/SwSearch.Metadata.Worker.csproj.nuget.dgspec.json'
    'src/SwSearch.PropertyConfig/obj/project.assets.json' = 'src/SwSearch.PropertyConfig/obj/SwSearch.PropertyConfig.csproj.nuget.dgspec.json'
    'src/SwSearch.PropertyViewer/obj/project.assets.json' = 'src/SwSearch.PropertyViewer/obj/SwSearch.PropertyViewer.csproj.nuget.dgspec.json'
    'src/SwSearch.ShellRegistration/obj/project.assets.json' = 'src/SwSearch.ShellRegistration/obj/SwSearch.ShellRegistration.csproj.nuget.dgspec.json'
    'src/SwSearch.WebUi/obj/project.assets.json' = 'src/SwSearch.WebUi/obj/SwSearch.WebUi.csproj.nuget.dgspec.json'
    'tests/SwSearch.App.Tests/obj/project.assets.json' = 'tests/SwSearch.App.Tests/obj/SwSearch.App.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.CandidateIlGate/obj/project.assets.json' = 'tests/SwSearch.CandidateIlGate/obj/SwSearch.CandidateIlGate.csproj.nuget.dgspec.json'
    'tests/SwSearch.Everything.Tests/obj/project.assets.json' = 'tests/SwSearch.Everything.Tests/obj/SwSearch.Everything.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.Indexer.Tests/obj/project.assets.json' = 'tests/SwSearch.Indexer.Tests/obj/SwSearch.Indexer.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.InstallerCoordinator.NativeHelper/obj/project.assets.json' = 'tests/SwSearch.InstallerCoordinator.NativeHelper/obj/SwSearch.InstallerCoordinator.NativeHelper.csproj.nuget.dgspec.json'
    'tests/SwSearch.InstallerCoordinator.Tests/obj/project.assets.json' = 'tests/SwSearch.InstallerCoordinator.Tests/obj/SwSearch.InstallerCoordinator.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.InstallerCoordinator.WatchdogHelper/obj/project.assets.json' = 'tests/SwSearch.InstallerCoordinator.WatchdogHelper/obj/SwSearch.InstallerCoordinator.WatchdogHelper.csproj.nuget.dgspec.json'
    'tests/SwSearch.Integration.Tests/obj/project.assets.json' = 'tests/SwSearch.Integration.Tests/obj/SwSearch.Integration.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.Metadata.Worker.Tests/obj/project.assets.json' = 'tests/SwSearch.Metadata.Worker.Tests/obj/SwSearch.Metadata.Worker.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.PropertyConfig.Tests/obj/project.assets.json' = 'tests/SwSearch.PropertyConfig.Tests/obj/SwSearch.PropertyConfig.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.PropertyViewer.Tests/obj/project.assets.json' = 'tests/SwSearch.PropertyViewer.Tests/obj/SwSearch.PropertyViewer.Tests.csproj.nuget.dgspec.json'
    'tests/SwSearch.ShellRegistration.Tests/obj/project.assets.json' = 'tests/SwSearch.ShellRegistration.Tests/obj/SwSearch.ShellRegistration.Tests.csproj.nuget.dgspec.json'
}
$runtimeProjectFrameworks = [ordered]@{
    'src/SwSearch.App/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.Broker/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.Contracts/obj/project.assets.json' = 'net8.0'
    'src/SwSearch.Everything/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.Indexer/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.InstallerCoordinator/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.Integration/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.Metadata.Worker/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.PropertyConfig/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.PropertyViewer/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.ShellRegistration/obj/project.assets.json' = 'net8.0-windows7.0'
    'src/SwSearch.WebUi/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.App.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.CandidateIlGate/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.Everything.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.Indexer.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.InstallerCoordinator.NativeHelper/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.InstallerCoordinator.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.InstallerCoordinator.WatchdogHelper/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.Integration.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.Metadata.Worker.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.PropertyConfig.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.PropertyViewer.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
    'tests/SwSearch.ShellRegistration.Tests/obj/project.assets.json' = 'net8.0-windows7.0'
}
$lockedReferencePacks = @(
    [pscustomobject]@{
        Name = 'Microsoft.NETCore.App.Ref'
        Version = '8.0.27'
        Marker = 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.27/data/FrameworkList.xml'
        Bytes = 35006L
        Sha256 = '0AFBDAD2630F9F732BC8C6FC91C4CDEB07197E886534B5AB1F3DD5A351F3DD38'
    },
    [pscustomobject]@{
        Name = 'Microsoft.WindowsDesktop.App.Ref'
        Version = '8.0.27'
        Marker = 'C:/Program Files/dotnet/packs/Microsoft.WindowsDesktop.App.Ref/8.0.27/data/FrameworkList.xml'
        Bytes = 11221L
        Sha256 = '01E2B382D4646E96079B3A7E820B1215ECA90CC141F6E767BCE1ECBF5E271E2C'
    }
)
$lockedSdk = [pscustomobject]@{
    Version = '8.0.421'
    Marker = 'C:/Program Files/dotnet/sdk/8.0.421/.version'
    Bytes = 87L
    Sha256 = '5A60F64119AF8F7551FD1A2D3FA45C4224B2227D254BD89D1B29EC2C3B62EF41'
}
$expectedRuntimeDownloads = @(
    [pscustomobject]@{ Name = 'Microsoft.AspNetCore.App.Runtime.win-x64'; Version = '[8.0.27, 8.0.27]'; PackageVersion = '8.0.27' },
    [pscustomobject]@{ Name = 'Microsoft.NETCore.App.Runtime.win-x64'; Version = '[8.0.27, 8.0.27]'; PackageVersion = '8.0.27' },
    [pscustomobject]@{ Name = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Version = '[8.0.27, 8.0.27]'; PackageVersion = '8.0.27' }
)

function Test-FrozenFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$ExpectedBytes,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "FROZEN_RESTORE_GRAPH_MISSING: $Label"
    }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -ne $ExpectedBytes) {
        throw "FROZEN_RESTORE_GRAPH_LENGTH_MISMATCH: $Label"
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -cne $ExpectedSha256) {
        throw "FROZEN_RESTORE_GRAPH_HASH_MISMATCH: $Label"
    }
}

function Resolve-WorkspaceLockedPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([IO.Path]::IsPathRooted($RelativePath)) {
        throw "FROZEN_RESTORE_GRAPH_ABSOLUTE_PROJECT_PATH: $RelativePath"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    if (-not $full.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "FROZEN_RESTORE_GRAPH_PATH_ESCAPE: $RelativePath"
    }
    return $full
}

function Assert-CanonicalNuGetConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSource,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageFolder,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "FROZEN_RESTORE_GRAPH_CONFIG_MISSING: $Label / $Path"
    }
    $configText = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ($configText -match '(?i)https?://') {
        throw "FROZEN_RESTORE_GRAPH_NETWORK_CONFIG: $Label"
    }
    [xml]$configXml = $configText
    $sourceChildren = @($configXml.SelectNodes('/configuration/packageSources/*'))
    $sourceAdds = @($configXml.SelectNodes('/configuration/packageSources/add'))
    $sourceClears = @($configXml.SelectNodes('/configuration/packageSources/clear'))
    $fallbackChildren = @($configXml.SelectNodes('/configuration/fallbackPackageFolders/*'))
    $fallbackAdds = @($configXml.SelectNodes('/configuration/fallbackPackageFolders/add'))
    $fallbackClears = @($configXml.SelectNodes('/configuration/fallbackPackageFolders/clear'))
    $configAdds = @($configXml.SelectNodes('/configuration/config/add'))
    if ($sourceChildren.Count -ne 2 -or $sourceClears.Count -ne 1 -or $sourceAdds.Count -ne 1 -or
        [string]$sourceAdds[0].GetAttribute('key') -cne 'SwSearchOffline' -or
        [string]$sourceAdds[0].GetAttribute('value') -cne $ExpectedSource -or
        $fallbackChildren.Count -ne 1 -or $fallbackClears.Count -ne 1 -or $fallbackAdds.Count -ne 0 -or
        $configAdds.Count -ne 2) {
        throw "FROZEN_RESTORE_GRAPH_CONFIG_SHAPE: $Label"
    }
    $configValues = @{}
    foreach ($entry in $configAdds) {
        $key = [string]$entry.GetAttribute('key')
        if ($configValues.ContainsKey($key)) {
            throw "FROZEN_RESTORE_GRAPH_CONFIG_DUPLICATE: $Label / $key"
        }
        $configValues[$key] = [string]$entry.GetAttribute('value')
    }
    if ($configValues.Count -ne 2 -or
        -not $configValues.ContainsKey('globalPackagesFolder') -or
        $configValues['globalPackagesFolder'] -cne $ExpectedPackageFolder -or
        -not $configValues.ContainsKey('auditMode') -or
        $configValues['auditMode'] -cne 'direct') {
        throw "FROZEN_RESTORE_GRAPH_CONFIG_VALUES: $Label"
    }
}

function Assert-WorkspacePackageMaterial {
    param(
        [Parameter(Mandatory = $true)][string]$PackageId,
        [Parameter(Mandatory = $true)][string]$PackageVersion,
        [string]$ExpectedContentHash = '',
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($PackageId -notmatch '^[A-Za-z0-9._-]+$' -or $PackageVersion -notmatch '^[A-Za-z0-9.+_-]+$') {
        throw "FROZEN_RESTORE_GRAPH_PACKAGE_IDENTITY: $Label / $PackageId $PackageVersion"
    }
    $normalizedId = $PackageId.ToLowerInvariant()
    $normalizedVersion = $PackageVersion.ToLowerInvariant()
    [void]$script:seenWorkspacePackages.Add("$normalizedId/$normalizedVersion")
    $workspacePackage = [IO.Path]::GetFullPath((Join-Path $workspacePackages (Join-Path $normalizedId $normalizedVersion)))
    if (-not ($workspacePackage + [IO.Path]::DirectorySeparatorChar).StartsWith(
            $workspacePackages, [StringComparison]::OrdinalIgnoreCase)) {
        throw "FROZEN_RESTORE_GRAPH_WORKSPACE_PACKAGE_ESCAPE: $Label / $PackageId $PackageVersion"
    }
    if (-not (Test-Path -LiteralPath $workspacePackage -PathType Container)) {
        throw "FROZEN_RESTORE_GRAPH_WORKSPACE_PACKAGE_MISSING: $Label / $PackageId $PackageVersion"
    }
    $nupkg = Join-Path $workspacePackage "$normalizedId.$normalizedVersion.nupkg"
    $shaMarker = Join-Path $workspacePackage "$normalizedId.$normalizedVersion.nupkg.sha512"
    $metadataPath = Join-Path $workspacePackage '.nupkg.metadata'
    if ($UserTestCandidate) {
        if (-not (Test-Path -LiteralPath $shaMarker -PathType Leaf)) {
            throw "FROZEN_RESTORE_GRAPH_WORKSPACE_PACKAGE_SHA512_MISSING: $Label / $PackageId $PackageVersion"
        }
        return
    }
    if (-not (Test-Path -LiteralPath $nupkg -PathType Leaf) -or
        -not (Test-Path -LiteralPath $shaMarker -PathType Leaf) -or
        -not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "FROZEN_RESTORE_GRAPH_WORKSPACE_PACKAGE_EVIDENCE_MISSING: $Label / $PackageId $PackageVersion"
    }
    $feedPackage = Join-Path $offlineFeed "$normalizedId.$normalizedVersion.nupkg"
    if (-not (Test-Path -LiteralPath $feedPackage -PathType Leaf)) {
        throw "FROZEN_RESTORE_GRAPH_OFFLINE_PACKAGE_MISSING: $Label / $PackageId $PackageVersion"
    }
    $feedSha256 = (Get-FileHash -LiteralPath $feedPackage -Algorithm SHA256).Hash
    $packageSha256 = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
    if ($packageSha256 -cne $feedSha256) {
        throw "FROZEN_RESTORE_GRAPH_PACKAGE_COPY_MISMATCH: $Label / $PackageId $PackageVersion"
    }
    $stream = [IO.File]::OpenRead($feedPackage)
    try {
        $sha512 = [Security.Cryptography.SHA512]::Create()
        try { $archiveContentHash = [Convert]::ToBase64String($sha512.ComputeHash($stream)) }
        finally { $sha512.Dispose() }
    }
    finally { $stream.Dispose() }
    if ((Get-Content -LiteralPath $shaMarker -Raw -Encoding UTF8).Trim() -cne $archiveContentHash) {
        throw "FROZEN_RESTORE_GRAPH_PACKAGE_SHA512_MISMATCH: $Label / $PackageId $PackageVersion"
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $metadataSource = [IO.Path]::GetFullPath([string]$metadata.source).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $metadataContentHash = [string]$metadata.contentHash
    if ([int]$metadata.version -ne 2 -or
        [String]::IsNullOrWhiteSpace($metadataContentHash) -or
        (-not [String]::IsNullOrWhiteSpace($ExpectedContentHash) -and
         $metadataContentHash -cne $ExpectedContentHash) -or
        -not $metadataSource.Equals($offlineFeed, [StringComparison]::OrdinalIgnoreCase)) {
        throw "FROZEN_RESTORE_GRAPH_PACKAGE_SOURCE_MISMATCH: $Label / $PackageId $PackageVersion"
    }
    Assert-ExpandedPackageMatchesArchive -PackageDirectory $workspacePackage -NupkgPath $nupkg `
        -MetadataPath $metadataPath -ShaMarkerPath $shaMarker -Label "$Label / $PackageId $PackageVersion"
}

function Assert-ExpandedPackageMatchesArchive {
    param(
        [Parameter(Mandatory = $true)][string]$PackageDirectory,
        [Parameter(Mandatory = $true)][string]$NupkgPath,
        [Parameter(Mandatory = $true)][string]$MetadataPath,
        [Parameter(Mandatory = $true)][string]$ShaMarkerPath,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $evidence = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($NupkgPath, $MetadataPath, $ShaMarkerPath)) {
        [void]$evidence.Add([IO.Path]::GetFullPath($path))
    }
    $actualByRelativePath = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($actualPath in [IO.Directory]::EnumerateFiles(
            $PackageDirectory,
            '*',
            [IO.SearchOption]::AllDirectories)) {
        $actualFullPath = [IO.Path]::GetFullPath($actualPath)
        if ($evidence.Contains($actualFullPath)) { continue }
        if (-not $actualFullPath.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_ESCAPE: $Label / $actualFullPath"
        }
        $relative = $actualFullPath.Substring($packageRoot.Length).Replace('/', '\')
        if ($actualByRelativePath.ContainsKey($relative)) {
            throw "FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_DUPLICATE: $Label / $relative"
        }
        $actualByRelativePath.Add($relative, $actualFullPath)
    }

    $seenArchivePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $archive = [IO.Compression.ZipFile]::OpenRead($NupkgPath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([String]::IsNullOrEmpty($entry.Name)) { continue }
            $relative = [Uri]::UnescapeDataString($entry.FullName).Replace('/', '\')
            if ($relative -ceq '_rels\.rels' -or
                $relative -ceq '[Content_Types].xml' -or
                $relative -like 'package\services\metadata\core-properties\*.psmdcp') {
                continue
            }
            if ([IO.Path]::IsPathRooted($relative) -or
                $relative.Split('\') -contains '..' -or
                -not $seenArchivePaths.Add($relative)) {
                throw "FROZEN_RESTORE_GRAPH_PACKAGE_ARCHIVE_PATH: $Label / $relative"
            }
            if (-not $actualByRelativePath.ContainsKey($relative)) {
                throw "FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_MISSING: $Label / $relative"
            }
            $actualPath = $actualByRelativePath[$relative]
            if ((Get-Item -LiteralPath $actualPath).Length -ne [long]$entry.Length) {
                throw "FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_LENGTH: $Label / $relative"
            }
            $entryStream = $entry.Open()
            try {
                $entryHasher = [Security.Cryptography.SHA256]::Create()
                try {
                    $entryHash = ([BitConverter]::ToString(
                            $entryHasher.ComputeHash($entryStream))).Replace('-', '')
                }
                finally { $entryHasher.Dispose() }
            }
            finally { $entryStream.Dispose() }
            $actualHash = (Get-FileHash -LiteralPath $actualPath -Algorithm SHA256).Hash
            if ($actualHash -cne $entryHash) {
                throw "FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_HASH: $Label / $relative"
            }
        }
    }
    finally { $archive.Dispose() }

    if ($actualByRelativePath.Count -ne $seenArchivePaths.Count) {
        $extra = @($actualByRelativePath.Keys | Where-Object { -not $seenArchivePaths.Contains($_) })
        throw "FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_EXTRA: $Label / $($extra -join ',')"
    }
}

function Assert-ExactOrdinalSet {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Actual,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$ErrorCode,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actualSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Actual) {
        if (-not $actualSet.Add([string]$value)) {
            throw "$ErrorCode`: $Label / duplicate $value"
        }
    }
    if ($actualSet.Count -ne $Expected.Count) {
        throw "$ErrorCode`: $Label"
    }
    foreach ($value in $Expected) {
        if (-not $actualSet.Contains($value)) {
            throw "$ErrorCode`: $Label / missing $value"
        }
    }
}

function Assert-WorkspacePackageDirectorySet {
    if (@([IO.Directory]::EnumerateFiles(
                $workspacePackages,
                '*',
                [IO.SearchOption]::TopDirectoryOnly)).Count -ne 0) {
        throw 'FROZEN_RESTORE_GRAPH_PACKAGE_ROOT_FILES'
    }
    $actualWorkspacePackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($idDirectory in [IO.Directory]::EnumerateDirectories($workspacePackages)) {
        $id = [IO.Path]::GetFileName($idDirectory)
        if ($id -notmatch '^[a-z0-9._-]+$' -or $id -cne $id.ToLowerInvariant()) {
            throw "FROZEN_RESTORE_GRAPH_PACKAGE_DIRECTORY_IDENTITY: $id"
        }
        if (@([IO.Directory]::EnumerateFiles(
                    $idDirectory,
                    '*',
                    [IO.SearchOption]::TopDirectoryOnly)).Count -ne 0) {
            throw "FROZEN_RESTORE_GRAPH_PACKAGE_ID_FILES: $id"
        }
        $versionDirectories = @([IO.Directory]::EnumerateDirectories($idDirectory))
        if ($versionDirectories.Count -eq 0) {
            throw "FROZEN_RESTORE_GRAPH_PACKAGE_ID_EMPTY: $id"
        }
        foreach ($versionDirectory in $versionDirectories) {
            $version = [IO.Path]::GetFileName($versionDirectory)
            if ($version -notmatch '^[a-z0-9.+_-]+$' -or $version -cne $version.ToLowerInvariant()) {
                throw "FROZEN_RESTORE_GRAPH_PACKAGE_DIRECTORY_VERSION: $id/$version"
            }
            if (-not $actualWorkspacePackages.Add("$id/$version")) {
                throw "FROZEN_RESTORE_GRAPH_PACKAGE_DIRECTORY_DUPLICATE: $id/$version"
            }
        }
    }
    foreach ($expectedPackage in $script:seenWorkspacePackages) {
        if (-not $actualWorkspacePackages.Contains($expectedPackage)) {
            throw "FROZEN_RESTORE_GRAPH_PACKAGE_DIRECTORY_SET: $workspacePackages / missing $expectedPackage"
        }
    }
    $extraPackages = @(
        $actualWorkspacePackages |
            Where-Object { -not $script:seenWorkspacePackages.Contains($_) } |
            Sort-Object
    )
    if ($extraPackages.Count -ne 0) {
        Write-Output "FROZEN_RESTORE_GRAPH_PACKAGE_DIRECTORY_EXTRA_ALLOWED: $($extraPackages -join ', ')"
    }
}

function Assert-FrozenSdkIdentity {
    param([Parameter(Mandatory = $true)][object]$Sdk)

    if (-not $UserTestCandidate) {
        if ([string]$Sdk.version -cne $lockedSdk.Version -or
            [string]$Sdk.marker -cne $lockedSdk.Marker -or
            [long]$Sdk.bytes -ne $lockedSdk.Bytes -or
            [string]$Sdk.sha256 -cne $lockedSdk.Sha256) {
            throw 'FROZEN_RESTORE_GRAPH_SDK_SET'
        }
        Test-FrozenFile -Path ([IO.Path]::GetFullPath($lockedSdk.Marker)) `
            -ExpectedBytes $lockedSdk.Bytes -ExpectedSha256 $lockedSdk.Sha256 `
            -Label "SDK $($lockedSdk.Version)"
    }
    else {
        Test-FrozenFile -Path ([IO.Path]::GetFullPath([string]$Sdk.marker)) `
            -ExpectedBytes ([long]$Sdk.bytes) -ExpectedSha256 ([string]$Sdk.sha256) `
            -Label "SDK $($Sdk.version)"
    }
}

function Assert-RuntimeDownloads {
    param(
        [Parameter(Mandatory = $true)][object]$Framework,
        [Parameter(Mandatory = $true)][string]$FrameworkName,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $downloadDependencies = @(
        if ($Framework.psobject.Properties.Name -contains 'downloadDependencies') {
            $Framework.downloadDependencies
        }
    )
    if ($downloadDependencies.Count -ne $expectedRuntimeDownloads.Count) {
        throw "FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_SET: $Label / $FrameworkName"
    }
    $seenRuntimeDownloads = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($dependency in $downloadDependencies) {
        $dependencyName = [string]$dependency.name
        $dependencyVersion = [string]$dependency.version
        if (-not $seenRuntimeDownloads.Add($dependencyName)) {
            throw "FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_DUPLICATE: $Label / $FrameworkName / $dependencyName"
        }
        $expected = @($expectedRuntimeDownloads | Where-Object { $_.Name -ceq $dependencyName })
        if ($expected.Count -ne 1 -or $dependencyVersion -cne $expected[0].Version) {
            throw "FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_IDENTITY: $Label / $FrameworkName / $dependencyName $dependencyVersion"
        }
        Assert-WorkspacePackageMaterial -PackageId $dependencyName `
            -PackageVersion $expected[0].PackageVersion `
            -Label "$Label / $FrameworkName / $dependencyName"
    }
    foreach ($expected in $expectedRuntimeDownloads) {
        if (-not $seenRuntimeDownloads.Contains($expected.Name)) {
            throw "FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_MISSING: $Label / $FrameworkName / $($expected.Name)"
        }
    }
}

function Assert-RuntimeProjectMetadata {
    param(
        [Parameter(Mandatory = $true)][object]$ProjectMetadata,
        [Parameter(Mandatory = $true)][string]$ExpectedFramework,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $frameworkProperties = @($ProjectMetadata.frameworks.psobject.Properties)
    Assert-ExactOrdinalSet -Actual @($frameworkProperties | ForEach-Object { $_.Name }) `
        -Expected @($ExpectedFramework) -ErrorCode 'FROZEN_RESTORE_GRAPH_RUNTIME_FRAMEWORK' -Label $Label
    $runtimeNames = @(
        if ($ProjectMetadata.psobject.Properties.Name -contains 'runtimes') {
            $ProjectMetadata.runtimes.psobject.Properties | ForEach-Object { $_.Name }
        }
    )
    Assert-ExactOrdinalSet -Actual $runtimeNames -Expected @('win-x64') `
        -ErrorCode 'FROZEN_RESTORE_GRAPH_RUNTIME_RID_SET' -Label $Label
    Assert-RuntimeDownloads -Framework $frameworkProperties[0].Value `
        -FrameworkName $ExpectedFramework -Label $Label
}

function Assert-PortableProjectMetadata {
    param(
        [Parameter(Mandatory = $true)][object]$ProjectMetadata,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $runtimeNames = @(
        if ($ProjectMetadata.psobject.Properties.Name -contains 'runtimes') {
            $ProjectMetadata.runtimes.psobject.Properties | ForEach-Object { $_.Name }
        }
    )
    if ($runtimeNames.Count -ne 0) {
        throw "FROZEN_RESTORE_GRAPH_RUNTIME_WRONG_PROJECT: $Label"
    }
    foreach ($frameworkProperty in @($ProjectMetadata.frameworks.psobject.Properties)) {
        $downloads = @(
            if ($frameworkProperty.Value.psobject.Properties.Name -contains 'downloadDependencies') {
                $frameworkProperty.Value.downloadDependencies
            }
        )
        if ($downloads.Count -ne 0) {
            throw "FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_WRONG_PROJECT: $Label / $($frameworkProperty.Name)"
        }
    }
}

function Test-RuntimeProjectMetadataPresent {
    param([Parameter(Mandatory = $true)][object]$ProjectMetadata)

    if ($ProjectMetadata.psobject.Properties.Name -contains 'runtimes' -and
        @($ProjectMetadata.runtimes.psobject.Properties | ForEach-Object { $_.Name }).Count -ne 0) {
        return $true
    }
    foreach ($frameworkProperty in @($ProjectMetadata.frameworks.psobject.Properties)) {
        if ($frameworkProperty.Value.psobject.Properties.Name -contains 'downloadDependencies' -and
            @($frameworkProperty.Value.downloadDependencies).Count -ne 0) {
            return $true
        }
    }
    return $false
}

function Assert-RestoreDescriptor {
    param(
        [Parameter(Mandatory = $true)][object]$Restore,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $packagesPath = [IO.Path]::GetFullPath([string]$Restore.packagesPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $UserTestCandidate -and
        -not $packagesPath.Equals($workspacePackages, [StringComparison]::OrdinalIgnoreCase)) {
        throw "FROZEN_RESTORE_GRAPH_USER_CACHE_FORBIDDEN: $Label"
    }
    $configPaths = @($Restore.configFilePaths)
    if ($configPaths.Count -eq 0) {
        throw "FROZEN_RESTORE_GRAPH_CONFIG_COUNT: $Label"
    }
    $seenConfigPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $hasCanonicalConfig = $false
    foreach ($configPath in $configPaths) {
        if ([String]::IsNullOrWhiteSpace([string]$configPath)) {
            throw "FROZEN_RESTORE_GRAPH_CONFIG_PATH_EMPTY: $Label"
        }
        $configFullPath = [IO.Path]::GetFullPath([string]$configPath)
        if (-not $configFullPath.StartsWith(
                $rootPath + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "FROZEN_RESTORE_GRAPH_CONFIG_OUTSIDE_WORKSPACE: $Label / $configFullPath"
        }
        if (-not (Test-Path -LiteralPath $configFullPath -PathType Leaf)) {
            throw "FROZEN_RESTORE_GRAPH_CONFIG_MISSING: $Label / $configFullPath"
        }
        if (-not $seenConfigPaths.Add($configFullPath)) {
            throw "FROZEN_RESTORE_GRAPH_CONFIG_DUPLICATE: $Label / $configFullPath"
        }
        if ($configFullPath.Equals($offlineConfig, [StringComparison]::OrdinalIgnoreCase) -or
            $configFullPath.Equals($sdkResolverConfig, [StringComparison]::OrdinalIgnoreCase)) {
            $hasCanonicalConfig = $true
        }
    }
    if (-not $UserTestCandidate -and -not $hasCanonicalConfig) {
        throw "FROZEN_RESTORE_GRAPH_CONFIG_NOT_FROZEN: $Label"
    }
    if ($Restore.psobject.Properties.Name -contains 'fallbackFolders' -and
        @($Restore.fallbackFolders).Count -ne 0) {
        throw "FROZEN_RESTORE_GRAPH_FALLBACK_FORBIDDEN: $Label"
    }
    $sources = @($Restore.sources.psobject.Properties.Name)
    if (-not $UserTestCandidate -and $sources.Count -ne 1) {
        throw "FROZEN_RESTORE_GRAPH_SOURCE_SET: $Label"
    }
    foreach ($source in $sources) {
        if ($source -match '^(?i)https?://') {
            throw "FROZEN_RESTORE_GRAPH_NETWORK_SOURCE: $Label"
        }
        $sourcePath = [IO.Path]::GetFullPath([string]$source).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $UserTestCandidate -and
            -not $sourcePath.Equals($offlineFeed, [StringComparison]::OrdinalIgnoreCase)) {
            throw "FROZEN_RESTORE_GRAPH_SOURCE_NOT_FROZEN: $Label"
        }
    }
}

if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) {
    throw "FROZEN_RESTORE_GRAPH_LOCK_MISSING: $LockPath"
}
if (-not (Test-Path -LiteralPath $offlineConfig -PathType Leaf)) {
    throw "FROZEN_RESTORE_GRAPH_OFFLINE_CONFIG_MISSING: $offlineConfig"
}
if (-not $UserTestCandidate) {
    if (-not (Test-Path -LiteralPath $restoreGraphProject -PathType Leaf)) {
        throw "FROZEN_RESTORE_GRAPH_TRAVERSAL_MISSING: $restoreGraphProject"
    }
    Assert-CanonicalNuGetConfig -Path $offlineConfig -ExpectedSource '.offline-feed' `
        -ExpectedPackageFolder '..\.packages-rc48-r3' -Label 'explicit restore config'
    Assert-CanonicalNuGetConfig -Path $sdkResolverConfig -ExpectedSource 'installer\.offline-feed' `
        -ExpectedPackageFolder '.packages-rc48-r3' -Label 'SDK resolver config'
    foreach ($relativeProject in $manualPackageReferenceProjects) {
        $projectPath = Resolve-WorkspaceLockedPath $relativeProject
        $projectText = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
        if ($projectText.IndexOf('..\..\.packages\', [StringComparison]::Ordinal) -ge 0 -or
            $projectText.IndexOf('..\..\.packages-rc48\', [StringComparison]::Ordinal) -ge 0 -or
            $projectText.IndexOf('..\..\.packages-rc48-r2\', [StringComparison]::Ordinal) -ge 0) {
            throw "FROZEN_RESTORE_GRAPH_LEGACY_MANUAL_PACKAGE_REFERENCE: $relativeProject"
        }
        if ($projectText.IndexOf('..\..\.packages-rc48-r3\', [StringComparison]::Ordinal) -lt 0) {
            throw "FROZEN_RESTORE_GRAPH_MANUAL_PACKAGE_REFERENCE_MISSING: $relativeProject"
        }
    }
}
$lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$lockedProjects = @($lock.projects)
if (($UserTestCandidate -and $lock.schema -notin @(1, 2)) -or
    (-not $UserTestCandidate -and $lock.schema -ne 2)) {
    throw 'FROZEN_RESTORE_GRAPH_LOCK_INVALID'
}
$expectedProjectOrder = @($lockedProjectDgspecs.Keys)
$seenLockedProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($lockedProject in $lockedProjects) {
    $relativeAssets = ([string]$lockedProject.assets).Replace('\', '/')
    $relativeDgspec = ([string]$lockedProject.dgspec).Replace('\', '/')
    if (-not $seenLockedProjects.Add($relativeAssets)) {
        throw "FROZEN_RESTORE_GRAPH_PROJECT_PATH_DUPLICATE: $relativeAssets"
    }
    if (-not $lockedProjectDgspecs.Contains($relativeAssets)) {
        throw "FROZEN_RESTORE_GRAPH_PROJECT_PATH_EXTRA: $relativeAssets"
    }
    if ($relativeDgspec -cne [string]$lockedProjectDgspecs[$relativeAssets]) {
        throw "FROZEN_RESTORE_GRAPH_PROJECT_PATH_SET: $relativeAssets / $relativeDgspec"
    }
}
foreach ($expectedProject in $expectedProjectOrder) {
    if (-not $seenLockedProjects.Contains([string]$expectedProject)) {
        throw "FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING: $expectedProject"
    }
}
if ($lockedProjects.Count -ne $lockedProjectDgspecs.Count) {
    throw 'FROZEN_RESTORE_GRAPH_LOCK_INVALID'
}
for ($projectIndex = 0; $projectIndex -lt $lockedProjects.Count; $projectIndex++) {
    $relativeAssets = ([string]$lockedProjects[$projectIndex].assets).Replace('\', '/')
    if ($relativeAssets -cne [string]$expectedProjectOrder[$projectIndex]) {
        throw "FROZEN_RESTORE_GRAPH_PROJECT_ORDER: $relativeAssets"
    }
}
$lockedOfflineConfig = Resolve-WorkspaceLockedPath ([string]$lock.offlineConfig.path)
if (-not $lockedOfflineConfig.Equals($offlineConfig, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'FROZEN_RESTORE_GRAPH_OFFLINE_CONFIG_IDENTITY'
}
Test-FrozenFile -Path $offlineConfig -ExpectedBytes ([long]$lock.offlineConfig.bytes) `
    -ExpectedSha256 ([string]$lock.offlineConfig.sha256) -Label 'offline NuGet config'
$lockedFeedFiles = @()
if (-not $UserTestCandidate) {
    Test-FrozenFile -Path $restoreGraphProject -ExpectedBytes ([long]$lock.restoreGraph.bytes) `
        -ExpectedSha256 ([string]$lock.restoreGraph.sha256) -Label 'restore traversal project'
    Test-FrozenFile -Path $sdkResolverConfig -ExpectedBytes ([long]$lock.sdkResolverConfig.bytes) `
        -ExpectedSha256 ([string]$lock.sdkResolverConfig.sha256) -Label 'SDK resolver NuGet config'
    if ([string]$lock.restoreGraph.path -cne 'installer/RestoreGraph.proj' -or
        [string]$lock.sdkResolverConfig.path -cne 'NuGet.config' -or
        [string]$lock.offlineFeed.path -cne 'installer/.offline-feed' -or
        [string]$lock.packageFolder.path -cne '.packages-rc48-r3') {
        throw 'FROZEN_RESTORE_GRAPH_CANONICAL_PATHS'
    }
    if (-not (Test-Path -LiteralPath $offlineFeed -PathType Container) -or
        -not (Test-Path -LiteralPath $workspacePackages -PathType Container)) {
        throw 'FROZEN_RESTORE_GRAPH_CANONICAL_DIRECTORY_MISSING'
    }
    $lockedFeedFiles = @($lock.offlineFeed.files)
    $actualFeedFiles = @(
        [IO.Directory]::EnumerateFiles($offlineFeed, '*', [IO.SearchOption]::TopDirectoryOnly) |
            Sort-Object { [IO.Path]::GetFileName($_) } |
            ForEach-Object { Get-Item -LiteralPath $_ }
    )
    if ($lockedFeedFiles.Count -ne [int]$lock.offlineFeed.fileCount -or
        $actualFeedFiles.Count -ne $lockedFeedFiles.Count -or
        ($actualFeedFiles | Measure-Object Length -Sum).Sum -ne [long]$lock.offlineFeed.totalBytes) {
        throw 'FROZEN_RESTORE_GRAPH_OFFLINE_FEED_SET'
    }
    for ($feedIndex = 0; $feedIndex -lt $lockedFeedFiles.Count; $feedIndex++) {
        $lockedFeed = $lockedFeedFiles[$feedIndex]
        $actualFeed = $actualFeedFiles[$feedIndex]
        if ($actualFeed.Extension -cne '.nupkg') {
            throw "FROZEN_RESTORE_GRAPH_OFFLINE_FEED_NON_PACKAGE: $($actualFeed.Name)"
        }
        $expectedFeedRelative = "installer/.offline-feed/$($actualFeed.Name)"
        if ([string]$lockedFeed.path -cne $expectedFeedRelative) {
            throw "FROZEN_RESTORE_GRAPH_OFFLINE_FEED_ORDER: $expectedFeedRelative"
        }
        Test-FrozenFile -Path $actualFeed.FullName -ExpectedBytes ([long]$lockedFeed.bytes) `
            -ExpectedSha256 ([string]$lockedFeed.sha256) -Label $expectedFeedRelative
    }
}

Assert-FrozenSdkIdentity -Sdk $lock.sdk
$referencePacks = @($lock.referencePacks)
if ($referencePacks.Count -ne $lockedReferencePacks.Count) {
    throw 'FROZEN_RESTORE_GRAPH_REFERENCE_PACK_SET'
}
for ($packIndex = 0; $packIndex -lt $lockedReferencePacks.Count; $packIndex++) {
    $actualPack = $referencePacks[$packIndex]
    $expectedPack = $lockedReferencePacks[$packIndex]
    if ([string]$actualPack.name -cne $expectedPack.Name -or
        [string]$actualPack.version -cne $expectedPack.Version -or
        [string]$actualPack.marker -cne $expectedPack.Marker -or
        [long]$actualPack.bytes -ne $expectedPack.Bytes -or
        [string]$actualPack.sha256 -cne $expectedPack.Sha256) {
        throw "FROZEN_RESTORE_GRAPH_REFERENCE_PACK_SET: index $packIndex"
    }
}
foreach ($pack in $referencePacks) {
    Test-FrozenFile -Path ([IO.Path]::GetFullPath([string]$pack.marker)) `
        -ExpectedBytes ([long]$pack.bytes) -ExpectedSha256 ([string]$pack.sha256) `
        -Label "reference pack $($pack.name) $($pack.version)"
}

$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$seenRuntimeProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($project in $lockedProjects) {
    $assetsPath = Resolve-WorkspaceLockedPath ([string]$project.assets)
    $dgspecPath = Resolve-WorkspaceLockedPath ([string]$project.dgspec)
    $generatedPaths = @($assetsPath, $dgspecPath)
    if ($lock.schema -eq 2) {
        $propsPath = Resolve-WorkspaceLockedPath ([string]$project.props)
        $targetsPath = Resolve-WorkspaceLockedPath ([string]$project.targets)
        $cachePath = Resolve-WorkspaceLockedPath ([string]$project.cache)
        $objDirectory = [IO.Path]::GetDirectoryName($assetsPath)
        $dgspecLeaf = [IO.Path]::GetFileName($dgspecPath)
        if (-not $dgspecLeaf.EndsWith('.nuget.dgspec.json', [StringComparison]::Ordinal)) {
            throw "FROZEN_RESTORE_GRAPH_DGSPEC_NAME: $dgspecLeaf"
        }
        $projectLeaf = $dgspecLeaf.Substring(0, $dgspecLeaf.Length - '.nuget.dgspec.json'.Length)
        $expectedProps = Join-Path $objDirectory "$projectLeaf.nuget.g.props"
        $expectedTargets = Join-Path $objDirectory "$projectLeaf.nuget.g.targets"
        $expectedCache = Join-Path $objDirectory 'project.nuget.cache'
        if (-not $propsPath.Equals($expectedProps, [StringComparison]::OrdinalIgnoreCase) -or
            -not $targetsPath.Equals($expectedTargets, [StringComparison]::OrdinalIgnoreCase) -or
            -not $cachePath.Equals($expectedCache, [StringComparison]::OrdinalIgnoreCase)) {
            throw "FROZEN_RESTORE_GRAPH_GENERATED_PATH_SET: $($project.assets)"
        }
        $generatedPaths += @($propsPath, $targetsPath, $cachePath)
    }
    foreach ($generatedPath in $generatedPaths) {
        if (-not $seen.Add($generatedPath)) {
            throw "FROZEN_RESTORE_GRAPH_DUPLICATE_PATH: $generatedPath"
        }
    }
    Test-FrozenFile -Path $assetsPath -ExpectedBytes ([long]$project.assetsBytes) `
        -ExpectedSha256 ([string]$project.assetsSha256) -Label ([string]$project.assets)
    Test-FrozenFile -Path $dgspecPath -ExpectedBytes ([long]$project.dgspecBytes) `
        -ExpectedSha256 ([string]$project.dgspecSha256) -Label ([string]$project.dgspec)
    if ($lock.schema -eq 2) {
        Test-FrozenFile -Path $propsPath -ExpectedBytes ([long]$project.propsBytes) `
            -ExpectedSha256 ([string]$project.propsSha256) -Label ([string]$project.props)
        Test-FrozenFile -Path $targetsPath -ExpectedBytes ([long]$project.targetsBytes) `
            -ExpectedSha256 ([string]$project.targetsSha256) -Label ([string]$project.targets)
        Test-FrozenFile -Path $cachePath -ExpectedBytes ([long]$project.cacheBytes) `
            -ExpectedSha256 ([string]$project.cacheSha256) -Label ([string]$project.cache)
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-RestoreDescriptor -Restore $assets.project.restore -Label $assetsPath
    $packageFolders = @($assets.packageFolders.psobject.Properties.Name)
    if ($packageFolders.Count -ne 1) {
        throw "FROZEN_RESTORE_GRAPH_PACKAGE_FOLDER_COUNT: $assetsPath"
    }
    $packageFolder = [IO.Path]::GetFullPath([string]$packageFolders[0]).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $UserTestCandidate -and
        -not $packageFolder.Equals($workspacePackages, [StringComparison]::OrdinalIgnoreCase)) {
        throw "FROZEN_RESTORE_GRAPH_USER_CACHE_FORBIDDEN: $assetsPath"
    }
    foreach ($libraryProperty in @($assets.libraries.psobject.Properties)) {
        if ([string]$libraryProperty.Value.type -cne 'package') { continue }
        $parts = ([string]$libraryProperty.Name).Split('/')
        if ($parts.Count -ne 2) {
            throw "FROZEN_RESTORE_GRAPH_PACKAGE_IDENTITY: $assetsPath / $($libraryProperty.Name)"
        }
        Assert-WorkspacePackageMaterial -PackageId $parts[0] -PackageVersion $parts[1] `
            -ExpectedContentHash ([string]$libraryProperty.Value.sha512) `
            -Label "$assetsPath / $($libraryProperty.Name)"
    }
    $relativeAssets = ([string]$project.assets).Replace('\', '/')
    $targetNames = @($assets.targets.psobject.Properties.Name)
    if ($runtimeProjectFrameworks.Contains($relativeAssets)) {
        $expectedFramework = [string]$runtimeProjectFrameworks[$relativeAssets]
        Assert-ExactOrdinalSet -Actual $targetNames -Expected @($expectedFramework, "$expectedFramework/win-x64") `
            -ErrorCode 'FROZEN_RESTORE_GRAPH_RUNTIME_TARGET_SET' -Label $assetsPath
        Assert-RuntimeProjectMetadata -ProjectMetadata $assets.project `
            -ExpectedFramework $expectedFramework -Label $assetsPath
        if (-not $seenRuntimeProjects.Add($relativeAssets)) {
            throw "FROZEN_RESTORE_GRAPH_RUNTIME_PROJECT_DUPLICATE: $relativeAssets"
        }
    }
    else {
        if (@($targetNames | Where-Object { $_.Contains('/') }).Count -ne 0) {
            throw "FROZEN_RESTORE_GRAPH_RUNTIME_WRONG_PROJECT: $assetsPath"
        }
        Assert-PortableProjectMetadata -ProjectMetadata $assets.project -Label $assetsPath
    }

    $dgspec = Get-Content -LiteralPath $dgspecPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($projectPath in @($dgspec.projects.psobject.Properties.Name)) {
        $fullProject = [IO.Path]::GetFullPath([string]$projectPath)
        if (-not $fullProject.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "FROZEN_RESTORE_GRAPH_PROJECT_ESCAPE: $dgspecPath"
        }
    }
    foreach ($projectProperty in @($dgspec.projects.psobject.Properties)) {
        Assert-RestoreDescriptor -Restore $projectProperty.Value.restore -Label $dgspecPath
        $fullProject = [IO.Path]::GetFullPath([string]$projectProperty.Name)
        $relativeProject = $fullProject.Substring($rootPath.Length + 1).Replace('\', '/')
        $relativeProjectAssets = ([IO.Path]::GetDirectoryName($relativeProject)).Replace('\', '/') + '/obj/project.assets.json'
        if ($runtimeProjectFrameworks.Contains($relativeProjectAssets) -and
            ($runtimeProjectFrameworks.Contains($relativeAssets) -or
             (Test-RuntimeProjectMetadataPresent -ProjectMetadata $projectProperty.Value))) {
            Assert-RuntimeProjectMetadata -ProjectMetadata $projectProperty.Value `
                -ExpectedFramework ([string]$runtimeProjectFrameworks[$relativeProjectAssets]) `
                -Label "$dgspecPath / $relativeProjectAssets"
        }
        else {
            Assert-PortableProjectMetadata -ProjectMetadata $projectProperty.Value `
                -Label "$dgspecPath / $relativeProjectAssets"
        }
    }
}

foreach ($runtimeProject in $runtimeProjectFrameworks.Keys) {
    if (-not $seenRuntimeProjects.Contains([string]$runtimeProject)) {
        throw "FROZEN_RESTORE_GRAPH_RUNTIME_PROJECT_MISSING: $runtimeProject"
    }
}
if (-not $UserTestCandidate) {
    # The WiX SDK is resolved before the wixproj restore descriptor exists, so
    # it is not represented as an assets library.  It is still part of the
    # frozen package material and must carry the same local-feed provenance.
    Assert-WorkspacePackageMaterial -PackageId 'WixToolset.Sdk' -PackageVersion '5.0.2' `
        -Label 'WiX SDK resolver package'

    Assert-WorkspacePackageDirectorySet
}
$lockedFileCount = if ($lock.schema -eq 2) {
    if ($UserTestCandidate) {
        $seen.Count + 2 + $referencePacks.Count
    }
    else {
        $seen.Count + 4 + $referencePacks.Count + $lockedFeedFiles.Count
    }
}
else {
    $seen.Count + 2 + $referencePacks.Count
}
$expectedGeneratedCount = if ($lock.schema -eq 2) { 125 } else { 50 }
$expectedLockedFileCount = if ($lock.schema -eq 2) {
    if ($UserTestCandidate) { 129 } else { 181 }
}
else { 54 }
if ($seen.Count -ne $expectedGeneratedCount -or $lockedFileCount -ne $expectedLockedFileCount) {
    throw "FROZEN_RESTORE_GRAPH_LOCKED_FILE_COUNT: projects=$($lockedProjects.Count) generated=$($seen.Count) total=$lockedFileCount"
}

[pscustomobject]@{
    Schema = [int]$lock.schema
    ProjectCount = $lockedProjects.Count
    LockedFileCount = $lockedFileCount
    SdkVersion = [string]$lock.sdk.version
    PackageFolder = $workspacePackages
    WorkspacePackageCount = $script:seenWorkspacePackages.Count
    Mode = if ($UserTestCandidate) { 'USER-TEST locked historical restore metadata' } else { 'Release offline-only' }
}
