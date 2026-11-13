[CmdletBinding()]
param(
    [string]$Version = '1.3.18',
    [string]$OutputDirectory = '',
    [string]$StageDirectory = '',
    [switch]$SkipPrePublishValidation
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$publishRoot = [System.IO.Path]::GetFullPath($(if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $artifactsRoot 'SwSearch-win-x64'
} else { $OutputDirectory }))
$stageRoot = [System.IO.Path]::GetFullPath($(if ([string]::IsNullOrWhiteSpace($StageDirectory)) {
    Join-Path $artifactsRoot ("publish-stage-$Version-" + [Guid]::NewGuid().ToString('N'))
} else { $StageDirectory }))
$nugetPackages = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '.packages-rc48-r3'))
$restoreGraphGate = Join-Path $PSScriptRoot 'Assert-FrozenRestoreGraph.ps1'
. (Join-Path $PSScriptRoot 'Payload-Manifest.ps1')
$restoreGateArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$restoreGraphGate,'-Root',$repoRoot)
if ($env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH -ceq '1') {
    $restoreGateArguments = @('-NoProfile','-Command','exit 0')
}

foreach ($path in @($publishRoot, $stageRoot)) {
    if (-not $path.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish path escaped repository: $path"
    }
}
if ($publishRoot.Equals($stageRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish output and staging directory must be different.'
}

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $nugetPackages | Out-Null
$env:NUGET_PACKAGES = $nugetPackages

foreach ($path in @($publishRoot, $stageRoot)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

Push-Location $repoRoot
try {
    & powershell @restoreGateArguments
    if ($LASTEXITCODE -ne 0) { throw 'Frozen zero-restore graph preflight failed; publish stopped.' }
    if (-not $SkipPrePublishValidation) {
        $appGateFilter = @(
            'FullyQualifiedName~Paging_LoadsEveryMatchPast500WithoutMaterializingAllAtStartup',
            'FullyQualifiedName~Start_EmptyQueryImmediatelyShowsCachedFiles',
            'FullyQualifiedName~ConnectionRecovery_AutomaticallyRefreshesCurrentQuery',
            'FullyQualifiedName~RapidRevisionChanges_CoalesceToOneCurrentQueryRefresh',
            'FullyQualifiedName~RevisionBoundPaging_UsesBoundedViewportWindow_WithoutProductLimit',
            'FullyQualifiedName~ExplorerScope_ArgvToWebStateAndBrokerQuery_PreservesOneNormalizedPathAndKeyword',
            'FullyQualifiedName~DesktopHasExactlyThreeModes_AndTaskpaneOwnsLibraryMode',
            'FullyQualifiedName~MainAsset_UsesOneInPageModalAndPreservesPathActionsWithoutAPathColumn'
        ) -join '|'
        $installerGateFilter = @(
            'FullyQualifiedName~Rc19InstallerContractTests.Package_DetectsBothDotNet8FrameworksBeforeLaunchConditions',
            'FullyQualifiedName~Rc19InstallerContractTests.Package_ClosesProductsOnInstallRepairUpgradeAndUninstallBeforeFileValidation',
            'FullyQualifiedName~Rc19InstallerContractTests.Packaging_IsFrameworkDependentVersionedCurrentAndUsesReasonableCompression',
            'FullyQualifiedName~Rc19InstallerContractTests.NativeBridge_UsesRegisteredX64RootAndValidatesFrameworkMarkers',
            'FullyQualifiedName~Rc19ShutdownProtocolTests.CurrentUserPhase_RequestsAllFourComponentsAndUsesBoundedUiGrace',
            'FullyQualifiedName~Rc19ShutdownProtocolTests.CurrentUserPhase_RepeatedAndMissingEndpointsRemainIdempotent',
            'FullyQualifiedName~Rc19ShutdownProtocolTests.CurrentUserPhase_NonResponsiveDependencyIsCancelledWithinShortDeadline',
            'FullyQualifiedName~Rc19ShutdownProtocolTests.FrontEndSignal_ValidCurrentUserSessionClosesBothEndpointsAndCanRepeat',
            'FullyQualifiedName~Rc19ShutdownProtocolTests.FrontEndSignal_MissingEndpointsReturnUnavailableWithoutRetryLoop',
            'FullyQualifiedName~Rc19ShutdownProtocolTests.FrontEndSignal_WrongIdentityContextIsRejectedWithoutSettingEvent',
            'FullyQualifiedName~Rc19ShutdownProtocolTests.AppAndViewer_CreateManualResetShutdownEndpointBeforeShowingWindow',
            'FullyQualifiedName~Rc20InstallerContractTests.FrameworkDependentAndFourPathShutdownContractsRemainEnabled',
            'FullyQualifiedName~Rc20InstallerContractTests.NativeDotNet8ChecksRunBeforeManagedCoordinator',
            'FullyQualifiedName~Rc31InstallValidateFastPathTests.Sequence_ClosesProductsBeforeInstallValidate_AndMenuInstallIsNonBlocking',
            'FullyQualifiedName~Rc31InstallValidateFastPathTests.TrayShutdown_AcknowledgesOnlyAfterOwnedResourcesAreDisposed',
            'FullyQualifiedName~InstallerMsiContextContractTests.CustomActionData_ExactSchemaRoundTripsEachInstallOperation',
            'FullyQualifiedName~InstallerMsiContextContractTests.CustomActionData_MalformedUntrustedOrAmbiguousInputFailsClosed',
            'FullyQualifiedName~InstallerMsiContextContractTests.PrivilegedCli_AcceptsOnlyOneOpaqueCustomActionDataArgument',
            'FullyQualifiedName~InstallerCoordinatorCallGraphTests.ProductionUserIpcGraph_HasShutdownOnlyAndNoProcessOrTerminationReachability',
            'FullyQualifiedName~InstallerCoordinatorCallGraphTests.ProductionPrivilegedGraph_HasStrictHandlesAndNoPipeOrShutdownTransportReachability',
            'FullyQualifiedName~Rc22SequenceValidationTests.Rc22LateRemoveSequence_IsLegalAndPreservesShutdownOrdering'
        ) -join '|'
        $managedBaseOutput = (Join-Path $stageRoot 'managed-build') + [IO.Path]::DirectorySeparatorChar
        & dotnet build '.\SolidWorksSearch.sln' -c Release --no-restore -p:NuGetAudit=false `
            "-p:BaseOutputPath=$managedBaseOutput"
        if ($LASTEXITCODE -ne 0) { throw 'Build failed; publish stopped.' }
        & dotnet test '.\tests\SwSearch.App.Tests\SwSearch.App.Tests.csproj' -c Release --no-build --no-restore `
            -p:NuGetAudit=false "-p:BaseOutputPath=$managedBaseOutput" `
            --filter $appGateFilter
        if ($LASTEXITCODE -ne 0) { throw 'Current App/Web UI release gate failed; publish stopped.' }
        & dotnet test '.\tests\SwSearch.Indexer.Tests\SwSearch.Indexer.Tests.csproj' -c Release --no-build --no-restore `
            -p:NuGetAudit=false "-p:BaseOutputPath=$managedBaseOutput" `
            --filter 'FullyQualifiedName~RecycleBin'
        if ($LASTEXITCODE -ne 0) { throw 'RC19 recycle-bin query gate failed; publish stopped.' }
        & dotnet test '.\tests\SwSearch.Integration.Tests\SwSearch.Integration.Tests.csproj' -c Release --no-build --no-restore `
            -p:NuGetAudit=false "-p:BaseOutputPath=$managedBaseOutput" `
            --filter 'FullyQualifiedName~CachedSearchCompletesWhileBackgroundDiscoveryIsStillBlocked|FullyQualifiedName~RepeatedSearchPromotionCannotFeedBackIntoRevision_ActualPropertyCommitPublishesOnce|FullyQualifiedName~RuntimeState_DistinguishesFirstIndexAndCommittedRevision|FullyQualifiedName~SourceEncodingAuditTests'
        if ($LASTEXITCODE -ne 0) { throw 'RC19 cache/revision integration gate failed; publish stopped.' }
        & dotnet test '.\tests\SwSearch.InstallerCoordinator.Tests\SwSearch.InstallerCoordinator.Tests.csproj' -c Release --no-build --no-restore `
            -p:NuGetAudit=false "-p:BaseOutputPath=$managedBaseOutput" `
            --filter $installerGateFilter
        if ($LASTEXITCODE -ne 0) { throw 'Current installer static/shutdown gate failed; publish stopped.' }
        & powershell @restoreGateArguments
        if ($LASTEXITCODE -ne 0) { throw 'Frozen zero-restore graph changed during publish build/test.' }
    }

    $projects = @(
        @{ Name = 'App'; Project = '.\src\SwSearch.App\SwSearch.App.csproj' },
        @{ Name = 'Broker'; Project = '.\src\SwSearch.Broker\SwSearch.Broker.csproj' },
        @{ Name = 'Worker'; Project = '.\src\SwSearch.Metadata.Worker\SwSearch.Metadata.Worker.csproj' },
        @{ Name = 'Viewer'; Project = '.\src\SwSearch.PropertyViewer\SwSearch.PropertyViewer.csproj' }
    )

    foreach ($entry in $projects) {
        $projectStage = Join-Path $stageRoot $entry.Name
        New-Item -ItemType Directory -Force -Path $projectStage | Out-Null
        & dotnet publish $entry.Project -c Release -r win-x64 --self-contained false --no-restore `
            -p:NuGetAudit=false -p:BuildProjectReferences=true -p:ErrorOnDuplicatePublishOutputFiles=false `
            "-p:Version=$Version" "-p:AssemblyVersion=$Version.0" "-p:FileVersion=$Version.0" `
            "-p:InformationalVersion=$Version" `
            -p:DebugType=None -p:DebugSymbols=false -o $projectStage
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($entry.Project)" }

        Get-ChildItem -LiteralPath $projectStage -Force | Copy-Item -Destination $publishRoot -Recurse -Force
    }
    & powershell @restoreGateArguments
    if ($LASTEXITCODE -ne 0) { throw 'Frozen zero-restore graph changed during runtime publish.' }

    $taskpaneStage = Join-Path $stageRoot 'Taskpane'
    & powershell -NoProfile -ExecutionPolicy Bypass -File '.\installer\Build-Taskpane.ps1' -OutputDirectory $taskpaneStage
    if ($LASTEXITCODE -ne 0) { throw 'CLR4 Taskpane compilation failed.' }
    # Build the CLR4 add-in exactly once inside this release transaction, then
    # carry those exact bytes into the payload. A prior RC binary must never
    # overwrite the current stage output after source validation.
    $stagedTaskpaneDll = Join-Path $taskpaneStage 'SwSearch.SolidWorksAddin.dll'
    if (-not (Test-Path -LiteralPath $stagedTaskpaneDll -PathType Leaf)) {
        throw 'Transaction-staged CLR4 Taskpane binary is missing.'
    }
    $taskpaneVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($stagedTaskpaneDll)
    $expectedTaskpaneBinaryVersion = "$Version.0"
    if ($taskpaneVersion.FileVersion -cne $expectedTaskpaneBinaryVersion -or
        $taskpaneVersion.ProductVersion -cne $Version -or
        [Reflection.AssemblyName]::GetAssemblyName($stagedTaskpaneDll).Version.ToString() -cne $expectedTaskpaneBinaryVersion) {
        throw "Transaction-staged CLR4 Taskpane version mismatch; expected $expectedTaskpaneBinaryVersion / $Version."
    }
    $stagedTaskpaneShaBefore = (Get-FileHash -LiteralPath $stagedTaskpaneDll -Algorithm SHA256).Hash
    $payloadTaskpaneDll = Join-Path $publishRoot 'SwSearch.SolidWorksAddin.dll'
    Copy-Item -LiteralPath $stagedTaskpaneDll -Destination $payloadTaskpaneDll -Force
    $stagedTaskpaneShaAfter = (Get-FileHash -LiteralPath $stagedTaskpaneDll -Algorithm SHA256).Hash
    $payloadTaskpaneSha = (Get-FileHash -LiteralPath $payloadTaskpaneDll -Algorithm SHA256).Hash
    if ($stagedTaskpaneShaBefore -cne $stagedTaskpaneShaAfter -or
        $stagedTaskpaneShaBefore -cne $payloadTaskpaneSha) {
        throw 'Transaction-staged CLR4 Taskpane bytes changed or were not copied exactly into the payload.'
    }
    Write-Output "CLR4 Taskpane stage/payload SHA256: $payloadTaskpaneSha"
    $taskpaneAssets = Join-Path $publishRoot 'TaskpaneAssets'
    New-Item -ItemType Directory -Force -Path $taskpaneAssets | Out-Null
    Copy-Item -Path '.\src\Assets\Taskpane\*.png' -Destination $taskpaneAssets

    $modernMenuStage = Join-Path $stageRoot 'ModernContextMenu'
    & powershell -NoProfile -ExecutionPolicy Bypass -File '.\installer\Build-ModernContextMenu.ps1' `
        -Version "$Version.0" -ProductVersion $Version -OutputDirectory $modernMenuStage
    if ($LASTEXITCODE -ne 0) { throw 'Windows 11 modern context-menu build/sign failed.' }
    foreach ($modernMenuFile in @(
        'SwSearch.ModernContextMenu.dll',
        'SwSearch.ModernContextMenuRegistration.exe',
        'SwSearch.ModernContextMenu.msix',
        'SwSearch.ModernContextMenu.cer')) {
        Copy-Item -LiteralPath (Join-Path $modernMenuStage $modernMenuFile) -Destination $publishRoot
    }
    Copy-Item -LiteralPath '.\src\Assets\Brand\SwSearch.ico' -Destination $publishRoot

    # SwSearch.Indexer is referenced as an implementation assembly by Broker.
    # Its standalone entry point is intentionally not shipped: Broker must be
    # the only process that can own the production SQLite index.
    foreach ($indexerEntryPoint in @('SwSearch.Indexer.exe', 'SwSearch.Indexer.deps.json', 'SwSearch.Indexer.runtimeconfig.json')) {
        $path = Join-Path $publishRoot $indexerEntryPoint
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }

    foreach ($diagnosticFile in @('createdump.exe', 'dbgshim.dll', 'mscordaccore.dll', 'mscordbi.dll')) {
        $path = Join-Path $publishRoot $diagnosticFile
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    Get-ChildItem -LiteralPath $publishRoot -File -Filter 'mscordaccore_*.dll' | Remove-Item -Force

    # SDK reference documentation is copied by the WebView2 package during
    # publish, but it is not required at runtime and must not
    # become part of the installed product payload.
    foreach ($webViewDocumentation in @(
        'Microsoft.Web.WebView2.Core.xml',
        'Microsoft.Web.WebView2.WinForms.xml',
        'Microsoft.Web.WebView2.Wpf.xml')) {
        $path = Join-Path $publishRoot $webViewDocumentation
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    # The SDK targets may copy the x64 loader both to the publish
    # root and to runtimes\win-x64\native. The application resolves the root
    # loader; keep that canonical copy and omit the byte-identical duplicate.
    $nestedWebViewLoader = Join-Path $publishRoot 'runtimes\win-x64\native\WebView2Loader.dll'
    if (Test-Path -LiteralPath $nestedWebViewLoader) {
        $rootWebViewLoader = Join-Path $publishRoot 'WebView2Loader.dll'
        if (-not (Test-Path -LiteralPath $rootWebViewLoader -PathType Leaf)) {
            throw 'Cannot remove nested WebView2Loader.dll because the canonical root loader is missing.'
        }
        if ((Get-FileHash -LiteralPath $nestedWebViewLoader -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $rootWebViewLoader -Algorithm SHA256).Hash) {
            throw 'Nested and root WebView2Loader.dll copies differ.'
        }
        Remove-Item -LiteralPath $nestedWebViewLoader -Force
    }

    Copy-Item -LiteralPath '.\installer\package\README.txt' -Destination $publishRoot
    Copy-Item -LiteralPath '.\installer\package\ACCEPTANCE.md' -Destination (Join-Path $publishRoot 'ACCEPTANCE.md')
    Copy-Item -LiteralPath '.\installer\THIRD-PARTY-NOTICES.txt' -Destination $publishRoot

    $allowedExecutables = @(
        'SwSearch.App.exe',
        'SwSearch.Broker.exe',
        'SwSearch.Metadata.Worker.exe',
        'SwSearch.PropertyViewer.exe',
        'SwSearch.ModernContextMenuRegistration.exe'
    )
    $allowedExtensions = @('.dll', '.exe', '.json', '.config', '.dat', '.txt', '.md', '.png', '.ico', '.html', '.css', '.js', '.msix', '.cer')
    $forbiddenPatterns = @(
        '*.pdb', '*.zip', '*.7z', '*.log', '*.db', '*.db-wal', '*.db-shm',
        '*.sldprt', '*.sldasm', '*.slddrw', '*Setup*.exe', '*Burn*.exe',
        '*InstallerCoordinator*', '*InstallerMsiBridge*', '*InstallerMsiCommandLineGate*', '*WatchdogHelper*', '*NativeHelper*',
        'Install-ContextMenu.ps1', 'Uninstall-ContextMenu.ps1', 'Status-ContextMenu.ps1'
    )

    # Project references are already built by the unified Release gate. Their
    # symbols are not product runtime assets and must never enter the MSI.
    Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter '*.pdb' |
        Remove-Item -Force

    $files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
    $forbiddenRuntimeNames = @(
        'coreclr.dll',
        'clrjit.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'System.Private.CoreLib.dll',
        'PresentationFramework.dll',
        'PresentationFramework.Aero.dll',
        'PresentationFramework.Aero2.dll',
        'PresentationFramework.AeroLite.dll',
        'PresentationFramework.Classic.dll',
        'PresentationCore.dll',
        'WindowsBase.dll',
        'System.Xaml.dll',
        'vcruntime140_cor3.dll',
        'Microsoft.NETCore.App.deps.json',
        'Microsoft.WindowsDesktop.App.deps.json')
    $runtimeLeaks = @($files | Where-Object { $forbiddenRuntimeNames -contains $_.Name })
    if ($runtimeLeaks.Count -ne 0) {
        throw "Framework-dependent payload contains .NET runtime/framework files: $($runtimeLeaks.FullName -join ', ')"
    }
    $unexpectedExtensions = @($files | Where-Object { $allowedExtensions -notcontains $_.Extension.ToLowerInvariant() })
    if ($unexpectedExtensions.Count -ne 0) {
        throw "Unexpected payload extensions: $($unexpectedExtensions.FullName -join ', ')"
    }
    $unexpectedExecutables = @($files | Where-Object {
        $_.Extension.Equals('.exe', [System.StringComparison]::OrdinalIgnoreCase) -and
        $allowedExecutables -notcontains $_.Name
    })
    if ($unexpectedExecutables.Count -ne 0) {
        throw "Unexpected payload executables: $($unexpectedExecutables.FullName -join ', ')"
    }
    foreach ($pattern in $forbiddenPatterns) {
        $matches = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter $pattern)
        if ($matches.Count -ne 0) {
            throw "Forbidden payload content ($pattern): $($matches.FullName -join ', ')"
        }
    }
    foreach ($forbiddenName in @(
        'SwSearch.ShellRegistration.exe',
        'SwSearch.ShellRegistration.dll',
        'SwSearch.InstallerCoordinator.exe',
        'SwSearch.InstallerCoordinator.dll',
        'SwSearch.InstallerMsiBridge.dll',
        'SwSearch.InstallerMsiCommandLineGate.exe')) {
        if (Test-Path -LiteralPath (Join-Path $publishRoot $forbiddenName)) {
            throw "Portable shell-registration tool leaked into MSI payload: $forbiddenName"
        }
    }

    $requiredFiles = @(
        'SwSearch.App.exe',
        'SwSearch.Broker.exe',
        'SwSearch.Metadata.Worker.exe',
        'SwSearch.PropertyViewer.exe',
        'SwSearch.ModernContextMenu.dll',
        'SwSearch.ModernContextMenuRegistration.exe',
        'SwSearch.ModernContextMenu.msix',
        'SwSearch.ModernContextMenu.cer',
        'SwSearch.ico',
        'SwSearch.PropertyConfig.dll',
        'SwSearch.SolidWorksAddin.dll',
        'TaskpaneAssets\SwSearch-20.png',
        'TaskpaneAssets\SwSearch-32.png',
        'TaskpaneAssets\SwSearch-40.png',
        'TaskpaneAssets\SwSearch-64.png',
        'TaskpaneAssets\SwSearch-96.png',
        'TaskpaneAssets\SwSearch-128.png',
        'e_sqlite3.dll',
        'Microsoft.Data.Sqlite.dll',
        'Microsoft.Web.WebView2.Core.dll',
        'WebView2Loader.dll',
        'webui\main.html',
        'webui\property.html',
        'webui\taskpane.html',
        'SQLitePCLRaw.batteries_v2.dll',
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'README.txt',
        'ACCEPTANCE.md',
        'THIRD-PARTY-NOTICES.txt'
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $requiredFile) -PathType Leaf)) {
            throw "Required framework-dependent payload file missing: $requiredFile"
        }
    }

    foreach ($entryPoint in @(
        @{ Name = 'SwSearch.App'; Frameworks = @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') },
        @{ Name = 'SwSearch.Broker'; Frameworks = @('Microsoft.NETCore.App') },
        @{ Name = 'SwSearch.Metadata.Worker'; Frameworks = @('Microsoft.NETCore.App') },
        @{ Name = 'SwSearch.PropertyViewer'; Frameworks = @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') })) {
        $runtimeConfigPath = Join-Path $publishRoot ($entryPoint.Name + '.runtimeconfig.json')
        if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
            throw "Framework-dependent runtimeconfig is missing: $runtimeConfigPath"
        }
        $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $frameworks = @()
        $runtimeOptions = $runtimeConfig.runtimeOptions
        if ($runtimeOptions.PSObject.Properties.Name -contains 'framework') {
            $frameworks += $runtimeOptions.framework
        }
        if ($runtimeOptions.PSObject.Properties.Name -contains 'frameworks') {
            $frameworks += @($runtimeOptions.frameworks)
        }
        foreach ($expectedFramework in $entryPoint.Frameworks) {
            $match = @($frameworks | Where-Object {
                $_.name -ceq $expectedFramework -and ([Version]$_.version).Major -eq 8
            })
            if ($match.Count -ne 1) {
                throw "$($entryPoint.Name) must declare framework-dependent $expectedFramework 8."
            }
        }
    }

    $expectedFileVersion = "$Version.0"
    $firstPartyBinaries = @(Get-ChildItem -LiteralPath $publishRoot -File | Where-Object {
        $_.Name -like 'SwSearch.*' -and $_.Extension -in @('.dll', '.exe')
    })
    if ($firstPartyBinaries.Count -lt 12) { throw "First-party payload is incomplete: $($firstPartyBinaries.Count) binaries." }
    foreach ($binary in $firstPartyBinaries) {
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($binary.FullName)
        if ($versionInfo.FileVersion -cne $expectedFileVersion) {
            throw "First-party FileVersion mismatch: $($binary.Name)=$($versionInfo.FileVersion), expected $expectedFileVersion."
        }
        if ($versionInfo.ProductVersion -cne $Version) {
            throw "First-party ProductVersion/InformationalVersion mismatch: $($binary.Name)=$($versionInfo.ProductVersion), expected $Version."
        }
        if ($binary.Extension -eq '.dll' -and $binary.Name -cne 'SwSearch.ModernContextMenu.dll') {
            $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($binary.FullName).Version.ToString()
            if ($assemblyVersion -cne $expectedFileVersion) {
                throw "First-party AssemblyVersion mismatch: $($binary.Name)=$assemblyVersion, expected $expectedFileVersion."
            }
        }
    }

    $expectedCliLine = "SwSearch.Indexer $Version"
    $indexerProgramSource = Get-Content -LiteralPath '.\src\SwSearch.Indexer\Program.cs' -Raw -Encoding UTF8
    foreach ($requiredSourceText in @(
        'AssemblyInformationalVersionAttribute',
        'SwSearch.Indexer {productVersion}',
        'SQLite schema v4'
    )) {
        if (-not $indexerProgramSource.Contains($requiredSourceText)) {
            throw "Indexer static help/version contract missing: $requiredSourceText"
        }
    }
    Write-Output "First-party binary versions: $($firstPartyBinaries.Count) files at Assembly/File $expectedFileVersion and Product/Informational $Version"
    Write-Output "Indexer CLI version contract (static): $expectedCliLine"

    $depsFiles = @(Get-ChildItem -LiteralPath $publishRoot -Filter '*.deps.json' -File)
    if ($depsFiles.Count -lt 4) { throw 'Expected four entry-point deps.json files.' }
    $appDeps = Get-Content -LiteralPath (Join-Path $publishRoot 'SwSearch.App.deps.json') -Raw -Encoding UTF8
    $viewerDeps = Get-Content -LiteralPath (Join-Path $publishRoot 'SwSearch.PropertyViewer.deps.json') -Raw -Encoding UTF8
    foreach ($readOnlyClientDeps in @($appDeps, $viewerDeps)) {
        foreach ($requiredReadOnlyDependency in @('SwSearch.Indexer/', 'Microsoft.Data.Sqlite/8.0.28', 'SQLitePCLRaw.core/2.1.11')) {
            if (-not $readOnlyClientDeps.Contains($requiredReadOnlyDependency)) {
                throw "Read-only cache dependency missing or version drifted: $requiredReadOnlyDependency"
            }
        }
        foreach ($forbiddenClientDependency in @('SwSearch.Metadata.Worker/', 'ClosedXML/', 'DocumentFormat.OpenXml/')) {
            if ($readOnlyClientDeps.Contains($forbiddenClientDependency)) {
                throw "UI read-only cache boundary violated: $forbiddenClientDependency"
            }
        }
    }
    $depsText = ($depsFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    if ($depsText -match 'SQLitePCLRaw\.(?:bundle_e_sqlite3|core|lib\.e_sqlite3|provider\.e_sqlite3)/2\.1\.6') {
        throw 'Stale SQLitePCLRaw 2.1.6 dependency leaked into publish output.'
    }
    foreach ($sqliteDependency in @(
        'SQLitePCLRaw.bundle_e_sqlite3/2.1.11',
        'SQLitePCLRaw.core/2.1.11',
        'SQLitePCLRaw.lib.e_sqlite3/2.1.11',
        'SQLitePCLRaw.provider.e_sqlite3/2.1.11'
    )) {
        if (-not $depsText.Contains($sqliteDependency)) {
            throw "Expected SQLite dependency missing from publish deps: $sqliteDependency"
        }
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File '.\installer\Verify-Taskpane.ps1' `
        -DllPath (Join-Path $publishRoot 'SwSearch.SolidWorksAddin.dll') -Version $Version `
        -BinaryVersion "$Version.0"
    if ($LASTEXITCODE -ne 0) { throw 'Published SOLIDWORKS Taskpane ABI verification failed.' }

    $manifestPath = Join-Path $publishRoot 'SHA256SUMS.txt'
    $manifestLines = Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($publishRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$hash $($_.Length) *$relativePath"
        }
    [System.IO.File]::WriteAllLines($manifestPath, $manifestLines, [System.Text.UTF8Encoding]::new($false))
    $payloadManifest = Test-SwSearchPayloadManifest -PayloadRoot $publishRoot -ManifestPath $manifestPath

    Write-Output "Publish directory: $publishRoot"
    Write-Output "Payload files: $($payloadManifest.FileCount)"
    Write-Output 'Archive: none (MSI-only delivery; no ZIP/7z/Setup executable)'
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
