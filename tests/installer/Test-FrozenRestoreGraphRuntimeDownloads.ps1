[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$gate = Join-Path $repoRoot 'installer\Assert-FrozenRestoreGraph.ps1'
$fixtureParent = Join-Path ([IO.Path]::GetTempPath()) ("swsearch-frozen-runtime-" + [Guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)
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
    'src/SwSearch.WebUi/obj/project.assets.json' = 'net8.0-windows7.0'
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
$expectedDownloads = @(
    [pscustomobject]@{ name = 'Microsoft.AspNetCore.App.Runtime.win-x64'; version = '[8.0.27, 8.0.27]' },
    [pscustomobject]@{ name = 'Microsoft.NETCore.App.Runtime.win-x64'; version = '[8.0.27, 8.0.27]' },
    [pscustomobject]@{ name = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; version = '[8.0.27, 8.0.27]' }
)
$expectedReferencePacks = @(
    [pscustomobject]@{
        name = 'Microsoft.NETCore.App.Ref'
        version = '8.0.27'
        marker = 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.27/data/FrameworkList.xml'
        bytes = 35006L
        sha256 = '0AFBDAD2630F9F732BC8C6FC91C4CDEB07197E886534B5AB1F3DD5A351F3DD38'
    },
    [pscustomobject]@{
        name = 'Microsoft.WindowsDesktop.App.Ref'
        version = '8.0.27'
        marker = 'C:/Program Files/dotnet/packs/Microsoft.WindowsDesktop.App.Ref/8.0.27/data/FrameworkList.xml'
        bytes = 11221L
        sha256 = '01E2B382D4646E96079B3A7E820B1215ECA90CC141F6E767BCE1ECBF5E271E2C'
    }
)

function Write-Utf8Json {
    param([string]$Path, [object]$Value)
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), $utf8)
}

function Get-FrozenIdentity {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path
    [ordered]@{ bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}

function New-RestoreDescriptor {
    param([string]$CaseRoot)
    $packages = [IO.Path]::GetFullPath((Join-Path $CaseRoot '.packages')) + '\'
    $config = [IO.Path]::GetFullPath((Join-Path $CaseRoot 'installer\NuGet.Offline.config'))
    $source = [IO.Path]::GetFullPath((Join-Path $CaseRoot 'installer\offline-feed'))
    [ordered]@{
        packagesPath = $packages
        configFilePaths = @($config)
        fallbackFolders = @()
        sources = [ordered]@{ $source = [ordered]@{} }
    }
}

function New-PackageMaterial {
    param([string]$PackagesRoot, [string]$Id, [string]$Version, [switch]$WithoutMarker)
    $normalizedId = $Id.ToLowerInvariant()
    $normalizedVersion = $Version.ToLowerInvariant()
    $directory = Join-Path $PackagesRoot (Join-Path $normalizedId $normalizedVersion)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if (-not $WithoutMarker) {
        [IO.File]::WriteAllText(
            (Join-Path $directory "$normalizedId.$normalizedVersion.nupkg.sha512"), 'fixture-sha512', $utf8)
    }
}

function Copy-Downloads {
    @($expectedDownloads | ForEach-Object {
        [pscustomobject]@{ name = $_.name; version = $_.version }
    })
}

function New-CaseFixture {
    param([string]$CaseName)
    $schema2Case = $CaseName.StartsWith('schema2-', [StringComparison]::Ordinal)
    $caseRoot = Join-Path $fixtureParent $CaseName
    $packagesRoot = Join-Path $caseRoot '.packages'
    $offlineConfig = Join-Path $caseRoot 'installer\NuGet.Offline.config'
    $offlineFeed = Join-Path $caseRoot 'installer\offline-feed'
    $sdkMarker = Join-Path $caseRoot 'sdk.marker'
    New-Item -ItemType Directory -Force -Path $packagesRoot, $offlineFeed | Out-Null
    [IO.File]::WriteAllText($offlineConfig, '<configuration />', $utf8)
    [IO.File]::WriteAllText($sdkMarker, 'sdk-8.0.421', $utf8)

    New-PackageMaterial -PackagesRoot $packagesRoot -Id 'Fixture.Package' -Version '1.0.0'
    foreach ($download in $expectedDownloads) {
        if ($CaseName -eq 'package-missing' -and $download.name -ceq $expectedDownloads[0].name) { continue }
        New-PackageMaterial -PackagesRoot $packagesRoot -Id $download.name -Version '8.0.27' `
            -WithoutMarker:($CaseName -eq 'marker-missing' -and $download.name -ceq $expectedDownloads[0].name)
    }

    $missingAsset = switch ($CaseName) {
        'missing-app' { 'src/SwSearch.App/obj/project.assets.json' }
        'missing-broker' { 'src/SwSearch.Broker/obj/project.assets.json' }
        'missing-contracts' { 'src/SwSearch.Contracts/obj/project.assets.json' }
        'missing-everything' { 'src/SwSearch.Everything/obj/project.assets.json' }
        'missing-indexer' { 'src/SwSearch.Indexer/obj/project.assets.json' }
        'missing-integration' { 'src/SwSearch.Integration/obj/project.assets.json' }
        'missing-worker' { 'src/SwSearch.Metadata.Worker/obj/project.assets.json' }
        'missing-property-config' { 'src/SwSearch.PropertyConfig/obj/project.assets.json' }
        'missing-property-viewer' { 'src/SwSearch.PropertyViewer/obj/project.assets.json' }
        'missing-coordinator' { 'src/SwSearch.InstallerCoordinator/obj/project.assets.json' }
        'missing-portable' { 'src/SwSearch.ShellRegistration/obj/project.assets.json' }
        default { $null }
    }

    $relativeAssetsPaths = @($lockedProjectDgspecs.Keys | Where-Object { $_ -cne $missingAsset })
    if ($CaseName -eq 'extra-project') {
        $relativeAssetsPaths += 'src/Extra/obj/project.assets.json'
    }
    if ($CaseName -eq 'path-replacement') {
        $relativeAssetsPaths = @($relativeAssetsPaths | ForEach-Object {
            if ($_ -ceq 'src/SwSearch.ShellRegistration/obj/project.assets.json') {
                'src/SwSearch.ShellRegistration.Replaced/obj/project.assets.json'
            }
            else { $_ }
        })
    }
    $restore = New-RestoreDescriptor -CaseRoot $caseRoot
    $packageFolders = [ordered]@{ ([IO.Path]::GetFullPath($packagesRoot) + '\') = [ordered]@{} }
    $lockedProjects = @()

    foreach ($relativeAssets in $relativeAssetsPaths) {
        $authorized = $runtimeProjectFrameworks.Contains($relativeAssets)
        $projectDirectory = ($relativeAssets -replace '/obj/project\.assets\.json$', '')
        $relativeDgspec = if ($lockedProjectDgspecs.Contains($relativeAssets)) {
            [string]$lockedProjectDgspecs[$relativeAssets]
        }
        else {
            $replacementName = [IO.Path]::GetFileName($projectDirectory)
            "$projectDirectory/obj/$replacementName.csproj.nuget.dgspec.json"
        }
        $dgspecLeaf = [IO.Path]::GetFileName($relativeDgspec)
        $projectFileName = $dgspecLeaf.Substring(0, $dgspecLeaf.Length - '.nuget.dgspec.json'.Length)
        $projectPath = Join-Path $caseRoot (($projectDirectory + '/' + $projectFileName).Replace('/', '\'))
        $framework = if ($authorized) { [string]$runtimeProjectFrameworks[$relativeAssets] } else { 'net8.0' }
        $downloads = if ($authorized) { @(Copy-Downloads) } else { @() }
        $runtimeNames = if ($authorized) { @('win-x64') } else { @() }
        $targetNames = if ($authorized) { @($framework, "$framework/win-x64") } else { @($framework) }

        if ($relativeAssets -ceq 'src/SwSearch.InstallerCoordinator/obj/project.assets.json') {
            switch ($CaseName) {
                'extra' {
                    $downloads += [pscustomobject]@{
                        name = 'Microsoft.Unexpected.App.Runtime.win-x64'
                        version = '[8.0.27, 8.0.27]'
                    }
                }
                'missing' { $downloads = @($downloads[0..1]) }
                'version-drift' { $downloads[0].version = '[8.0.28, 8.0.28]' }
                'duplicate' {
                    $downloads = @(
                        $downloads[0],
                        $downloads[1],
                        [pscustomobject]@{ name = $downloads[0].name; version = $downloads[0].version }
                    )
                }
                'wrong-framework' {
                    $framework = 'net8.0'
                    $targetNames = @('net8.0', 'net8.0/win-x64')
                }
            }
        }
        if ($relativeAssets -ceq 'src/SwSearch.Contracts/obj/project.assets.json' -and
            $CaseName -eq 'contracts-wrong-tfm') {
            $framework = 'net8.0-windows7.0'
            $targetNames = @($framework, "$framework/win-x64")
        }
        if ($relativeAssets -ceq 'src/SwSearch.App/obj/project.assets.json') {
            switch ($CaseName) {
                'rid-target-missing' { $targetNames = @($framework) }
                'rid-target-extra' { $targetNames += "$framework/win-arm64" }
                'runtime-missing' { $runtimeNames = @() }
                'runtime-wrong' { $runtimeNames = @('win-arm64') }
                'runtime-extra' { $runtimeNames = @('win-x64', 'win-arm64') }
            }
        }
        if ($relativeAssets -ceq 'src/SwSearch.ShellRegistration/obj/project.assets.json' -and
            $CaseName -eq 'wrong-project') {
            $downloads = @([pscustomobject]@{
                name = $expectedDownloads[0].name
                version = $expectedDownloads[0].version
            })
        }
        if ($relativeAssets -ceq 'src/SwSearch.ShellRegistration/obj/project.assets.json' -and
            $CaseName -eq 'unauthorized-rid') {
            $targetNames = @($framework, "$framework/win-x64")
            $runtimeNames = @('win-x64')
            $downloads = @(Copy-Downloads)
        }

        $targets = [ordered]@{}
        foreach ($targetName in $targetNames) { $targets[$targetName] = [ordered]@{} }
        $runtimes = [ordered]@{}
        foreach ($runtimeName in $runtimeNames) { $runtimes[$runtimeName] = [ordered]@{} }
        $frameworks = [ordered]@{
            $framework = [ordered]@{ downloadDependencies = @($downloads) }
        }
        $assets = [ordered]@{
            version = 3
            targets = $targets
            libraries = if ($relativeAssets -like '*InstallerCoordinator*') {
                [ordered]@{ 'Fixture.Package/1.0.0' = [ordered]@{ type = 'package' } }
            }
            else { [ordered]@{} }
            packageFolders = $packageFolders
            project = [ordered]@{
                restore = $restore
                frameworks = $frameworks
                runtimes = $runtimes
            }
        }

        $dgspecFramework = $framework
        $dgspecRuntimes = @($runtimeNames)
        if ($relativeAssets -ceq 'src/SwSearch.App/obj/project.assets.json') {
            if ($CaseName -eq 'dgspec-tfm-drift') { $dgspecFramework = 'net8.0' }
            if ($CaseName -eq 'dgspec-rid-drift') { $dgspecRuntimes = @('win-arm64') }
        }
        $dgspecRuntimeObject = [ordered]@{}
        foreach ($runtimeName in $dgspecRuntimes) { $dgspecRuntimeObject[$runtimeName] = [ordered]@{} }
        $dgspecFrameworks = [ordered]@{
            $dgspecFramework = [ordered]@{ downloadDependencies = @($downloads) }
        }
        $dgspecProjects = [ordered]@{
            ([IO.Path]::GetFullPath($projectPath)) = [ordered]@{
                restore = $restore
                frameworks = $dgspecFrameworks
                runtimes = $dgspecRuntimeObject
            }
        }
        if ($relativeAssets -ceq 'src/SwSearch.App/obj/project.assets.json') {
            $nestedContractsFramework = if ($CaseName -eq 'nested-contracts-tfm-drift') {
                'net8.0-windows7.0'
            }
            else { 'net8.0' }
            $nestedContractsRuntimes = if ($CaseName -eq 'nested-contracts-rid-drift') {
                @('win-arm64')
            }
            else { @('win-x64') }
            $nestedRuntimeObject = [ordered]@{}
            foreach ($runtimeName in $nestedContractsRuntimes) {
                $nestedRuntimeObject[$runtimeName] = [ordered]@{}
            }
            $nestedFrameworks = [ordered]@{
                $nestedContractsFramework = [ordered]@{ downloadDependencies = @(Copy-Downloads) }
            }
            $nestedContractsPath = Join-Path $caseRoot 'src\SwSearch.Contracts\SwSearch.Contracts.csproj'
            $dgspecProjects[[IO.Path]::GetFullPath($nestedContractsPath)] = [ordered]@{
                restore = $restore
                frameworks = $nestedFrameworks
                runtimes = $nestedRuntimeObject
            }
        }
        $dgspec = [ordered]@{
            projects = $dgspecProjects
        }

        $assetsPath = Join-Path $caseRoot ($relativeAssets.Replace('/', '\'))
        $dgspecPath = Join-Path $caseRoot ($relativeDgspec.Replace('/', '\'))
        Write-Utf8Json -Path $assetsPath -Value $assets
        Write-Utf8Json -Path $dgspecPath -Value $dgspec
        $assetsIdentity = Get-FrozenIdentity -Path $assetsPath
        $dgspecIdentity = Get-FrozenIdentity -Path $dgspecPath
        $lockedProject = [ordered]@{
            assets = $relativeAssets
            assetsBytes = $assetsIdentity.bytes
            assetsSha256 = $assetsIdentity.sha256
            dgspec = $relativeDgspec
            dgspecBytes = $dgspecIdentity.bytes
            dgspecSha256 = $dgspecIdentity.sha256
        }
        if ($schema2Case) {
            $objDirectory = [IO.Path]::GetDirectoryName($relativeAssets).Replace('\', '/')
            $projectPrefix = $relativeDgspec.Substring(
                0,
                $relativeDgspec.Length - '.nuget.dgspec.json'.Length)
            $generated = [ordered]@{
                props = "$projectPrefix.nuget.g.props"
                targets = "$projectPrefix.nuget.g.targets"
                cache = "$objDirectory/project.nuget.cache"
            }
            foreach ($generatedEntry in $generated.GetEnumerator()) {
                $generatedPath = Join-Path $caseRoot ($generatedEntry.Value.Replace('/', '\'))
                [IO.File]::WriteAllText($generatedPath, "$($generatedEntry.Key)-fixture", $utf8)
                $identity = Get-FrozenIdentity -Path $generatedPath
                $lockedProject[$generatedEntry.Key] = $generatedEntry.Value
                $lockedProject["$($generatedEntry.Key)Bytes"] = $identity.bytes
                $lockedProject["$($generatedEntry.Key)Sha256"] = $identity.sha256
                if ($CaseName -eq 'schema2-props-missing' -and
                    $relativeAssets -ceq 'src/SwSearch.App/obj/project.assets.json' -and
                    $generatedEntry.Key -ceq 'props') {
                    Remove-Item -LiteralPath $generatedPath -Force
                }
                if ($CaseName -eq 'schema2-cache-hash' -and
                    $relativeAssets -ceq 'src/SwSearch.App/obj/project.assets.json' -and
                    $generatedEntry.Key -ceq 'cache') {
                    [IO.File]::WriteAllText($generatedPath, 'CACHE-fixture', $utf8)
                }
            }
        }
        $lockedProjects += $lockedProject
    }

    $configIdentity = Get-FrozenIdentity -Path $offlineConfig
    $sdkIdentity = Get-FrozenIdentity -Path $sdkMarker
    $referencePacks = @($expectedReferencePacks | ForEach-Object {
        [ordered]@{
            name = $_.name
            version = $_.version
            marker = $_.marker
            bytes = $_.bytes
            sha256 = $_.sha256
        }
    })
    switch ($CaseName) {
        'reference-pack-missing' { $referencePacks = @($referencePacks[0]) }
        'reference-pack-extra' {
            $referencePacks += [ordered]@{
                name = 'Microsoft.Unexpected.Ref'
                version = '8.0.27'
                marker = 'C:/Program Files/dotnet/packs/Microsoft.Unexpected.Ref/8.0.27/data/FrameworkList.xml'
                bytes = 1L
                sha256 = ('0' * 64)
            }
        }
        'reference-pack-replaced' {
            $referencePacks[0].marker = 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.27/data/Replaced.xml'
        }
    }
    $lock = [ordered]@{
        schema = if ($schema2Case) { 2 } else { 1 }
        offlineConfig = [ordered]@{
            path = 'installer/NuGet.Offline.config'
            bytes = $configIdentity.bytes
            sha256 = $configIdentity.sha256
        }
        sdk = [ordered]@{
            version = '8.0.421'
            marker = [IO.Path]::GetFullPath($sdkMarker)
            bytes = $sdkIdentity.bytes
            sha256 = $sdkIdentity.sha256
        }
        referencePacks = $referencePacks
        projects = $lockedProjects
    }
    $lockPath = Join-Path $caseRoot 'installer\restore-assets.lock.json'
    Write-Utf8Json -Path $lockPath -Value $lock
    [pscustomobject]@{ Root = $caseRoot; Lock = $lockPath }
}

$expectations = [ordered]@{
    normal = $null
    'schema2-normal' = $null
    'schema2-props-missing' = 'FROZEN_RESTORE_GRAPH_MISSING'
    'schema2-cache-hash' = 'FROZEN_RESTORE_GRAPH_HASH_MISMATCH'
    extra = 'FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_SET'
    missing = 'FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_SET'
    'version-drift' = 'FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_IDENTITY'
    duplicate = 'FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_DUPLICATE'
    'wrong-project' = 'FROZEN_RESTORE_GRAPH_DOWNLOAD_DEPENDENCY_WRONG_PROJECT'
    'wrong-framework' = 'FROZEN_RESTORE_GRAPH_RUNTIME_TARGET_SET'
    'package-missing' = 'FROZEN_RESTORE_GRAPH_WORKSPACE_PACKAGE_MISSING'
    'marker-missing' = 'FROZEN_RESTORE_GRAPH_WORKSPACE_PACKAGE_SHA512_MISSING'
    'missing-app' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-broker' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-contracts' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-everything' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-indexer' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-integration' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-worker' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-property-config' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-property-viewer' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-coordinator' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'missing-portable' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_MISSING'
    'extra-project' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_EXTRA'
    'path-replacement' = 'FROZEN_RESTORE_GRAPH_PROJECT_PATH_EXTRA'
    'contracts-wrong-tfm' = 'FROZEN_RESTORE_GRAPH_RUNTIME_TARGET_SET'
    'rid-target-missing' = 'FROZEN_RESTORE_GRAPH_RUNTIME_TARGET_SET'
    'rid-target-extra' = 'FROZEN_RESTORE_GRAPH_RUNTIME_TARGET_SET'
    'runtime-missing' = 'FROZEN_RESTORE_GRAPH_RUNTIME_RID_SET'
    'runtime-wrong' = 'FROZEN_RESTORE_GRAPH_RUNTIME_RID_SET'
    'runtime-extra' = 'FROZEN_RESTORE_GRAPH_RUNTIME_RID_SET'
    'unauthorized-rid' = 'FROZEN_RESTORE_GRAPH_RUNTIME_WRONG_PROJECT'
    'dgspec-tfm-drift' = 'FROZEN_RESTORE_GRAPH_RUNTIME_FRAMEWORK'
    'dgspec-rid-drift' = 'FROZEN_RESTORE_GRAPH_RUNTIME_RID_SET'
    'nested-contracts-tfm-drift' = 'FROZEN_RESTORE_GRAPH_RUNTIME_FRAMEWORK'
    'nested-contracts-rid-drift' = 'FROZEN_RESTORE_GRAPH_RUNTIME_RID_SET'
    'reference-pack-missing' = 'FROZEN_RESTORE_GRAPH_REFERENCE_PACK_SET'
    'reference-pack-extra' = 'FROZEN_RESTORE_GRAPH_REFERENCE_PACK_SET'
    'reference-pack-replaced' = 'FROZEN_RESTORE_GRAPH_REFERENCE_PACK_SET'
}
$results = @()
try {
    New-Item -ItemType Directory -Path $fixtureParent | Out-Null
    foreach ($entry in $expectations.GetEnumerator()) {
        $fixture = New-CaseFixture -CaseName $entry.Key
        $previousErrorAction = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $gate `
                -Root $fixture.Root -LockPath $fixture.Lock -UserTestCandidate 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorAction
        }
        $observedCode = [regex]::Match($output, 'FROZEN_RESTORE_GRAPH_[A-Z0-9_]+').Value
        if (($null -eq $entry.Value -and $exitCode -ne 0) -or
            ($null -ne $entry.Value -and ($exitCode -eq 0 -or $observedCode -cne $entry.Value))) {
            throw "RUNTIME_DOWNLOAD_FIXTURE_UNEXPECTED_RESULT: $($entry.Key) exit=$exitCode output=$output"
        }
        if ($entry.Key -in @('package-missing', 'marker-missing') -and
            -not $output.Contains($expectedDownloads[0].name)) {
            throw "RUNTIME_DOWNLOAD_FIXTURE_RUNTIME_ID_MISSING: $($entry.Key) output=$output"
        }
        $results += [pscustomobject]@{
            Case = $entry.Key
            ExitCode = $exitCode
            ExpectedCode = $entry.Value
            ObservedCode = $observedCode
        }
    }
    $results
}
finally {
    $full = [IO.Path]::GetFullPath($fixtureParent)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notmatch '^swsearch-frozen-runtime-[0-9a-f]{32}$') {
        throw "Unsafe fixture cleanup path: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    if (Test-Path -LiteralPath $full) { throw "Fixture cleanup failed: $full" }
}
