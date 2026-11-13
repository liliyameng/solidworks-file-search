[CmdletBinding()]
param(
    [string]$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$OutputPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'restore-assets.lock.json'))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not $output.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Restore lock output escaped the workspace: $output"
}

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

function Get-FrozenFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ([IO.Path]::IsPathRooted($RelativePath)) {
        throw "Restore lock path must be workspace-relative: $RelativePath"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    if (-not $full.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Restore lock input is missing or escaped the workspace: $RelativePath"
    }
    $item = Get-Item -LiteralPath $full
    return [ordered]@{
        path = $RelativePath.Replace('\', '/')
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    }
}

$projects = @()
foreach ($assetsRelative in $lockedProjectDgspecs.Keys) {
    $dgspecRelative = [string]$lockedProjectDgspecs[$assetsRelative]
    $projectPrefix = $dgspecRelative.Substring(
        0,
        $dgspecRelative.Length - '.nuget.dgspec.json'.Length)
    $objDirectory = [IO.Path]::GetDirectoryName($assetsRelative).Replace('\', '/')
    $assets = Get-FrozenFile $assetsRelative
    $dgspec = Get-FrozenFile $dgspecRelative
    $props = Get-FrozenFile ($projectPrefix + '.nuget.g.props')
    $targets = Get-FrozenFile ($projectPrefix + '.nuget.g.targets')
    $cache = Get-FrozenFile ($objDirectory + '/project.nuget.cache')
    $projects += [ordered]@{
        assets = $assets.path
        assetsBytes = $assets.bytes
        assetsSha256 = $assets.sha256
        dgspec = $dgspec.path
        dgspecBytes = $dgspec.bytes
        dgspecSha256 = $dgspec.sha256
        props = $props.path
        propsBytes = $props.bytes
        propsSha256 = $props.sha256
        targets = $targets.path
        targetsBytes = $targets.bytes
        targetsSha256 = $targets.sha256
        cache = $cache.path
        cacheBytes = $cache.bytes
        cacheSha256 = $cache.sha256
    }
}

$feedRoot = [IO.Path]::GetFullPath((Join-Path $rootPath 'installer\.offline-feed'))
$feedFiles = @()
[long]$feedBytes = 0
foreach ($feedPath in @(
        [IO.Directory]::EnumerateFiles($feedRoot, '*', [IO.SearchOption]::TopDirectoryOnly) |
            Sort-Object { [IO.Path]::GetFileName($_) })) {
    $feedItem = Get-Item -LiteralPath $feedPath
    if ($feedItem.Extension -cne '.nupkg') {
        throw "Offline feed contains a non-package file: $($feedItem.Name)"
    }
    $feedBytes += $feedItem.Length
    $feedFiles += [ordered]@{
        path = 'installer/.offline-feed/' + $feedItem.Name
        bytes = [long]$feedItem.Length
        sha256 = (Get-FileHash -LiteralPath $feedItem.FullName -Algorithm SHA256).Hash
    }
}
if ($feedFiles.Count -ne 50 -or $feedBytes -ne 194634460L) {
    throw "Offline feed set is not the reviewed RC48 set: files=$($feedFiles.Count) bytes=$feedBytes"
}

$sdkMarker = 'C:/Program Files/dotnet/sdk/8.0.421/.version'
$sdkItem = Get-Item -LiteralPath $sdkMarker
$sdkSha256 = (Get-FileHash -LiteralPath $sdkMarker -Algorithm SHA256).Hash
if ($sdkItem.Length -ne 87L -or
    $sdkSha256 -cne '5A60F64119AF8F7551FD1A2D3FA45C4224B2227D254BD89D1B29EC2C3B62EF41') {
    throw 'Frozen .NET SDK 8.0.421 marker identity changed.'
}
$lock = [ordered]@{
    schema = 2
    offlineConfig = Get-FrozenFile 'installer/NuGet.Offline.config'
    sdkResolverConfig = Get-FrozenFile 'NuGet.config'
    restoreGraph = Get-FrozenFile 'installer/RestoreGraph.proj'
    offlineFeed = [ordered]@{
        path = 'installer/.offline-feed'
        fileCount = $feedFiles.Count
        totalBytes = $feedBytes
        files = $feedFiles
    }
    packageFolder = [ordered]@{ path = '.packages-rc48-r3' }
    sdk = [ordered]@{
        version = '8.0.421'
        marker = $sdkMarker
        bytes = [long]$sdkItem.Length
        sha256 = $sdkSha256
    }
    referencePacks = @(
        [ordered]@{
            name = 'Microsoft.NETCore.App.Ref'
            version = '8.0.27'
            marker = 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.27/data/FrameworkList.xml'
            bytes = 35006L
            sha256 = '0AFBDAD2630F9F732BC8C6FC91C4CDEB07197E886534B5AB1F3DD5A351F3DD38'
        },
        [ordered]@{
            name = 'Microsoft.WindowsDesktop.App.Ref'
            version = '8.0.27'
            marker = 'C:/Program Files/dotnet/packs/Microsoft.WindowsDesktop.App.Ref/8.0.27/data/FrameworkList.xml'
            bytes = 11221L
            sha256 = '01E2B382D4646E96079B3A7E820B1215ECA90CC141F6E767BCE1ECBF5E271E2C'
        })
    projects = $projects
}

$json = $lock | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($output, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    Schema = 2
    ProjectCount = $projects.Count
    GeneratedFileCount = $projects.Count * 5
    FeedFileCount = $feedFiles.Count
    Output = $output
    Bytes = (Get-Item -LiteralPath $output).Length
    Sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
}
