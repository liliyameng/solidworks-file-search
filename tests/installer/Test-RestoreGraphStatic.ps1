$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw "RESTORE_GRAPH_STATIC_GATE: $Message" }
}

function Read-WorkspaceText {
    param([Parameter(Mandatory = $true)][string]$RelativePath)
    $path = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    Assert-True -Condition ($path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) `
        -Message "path escaped workspace: $RelativePath"
    Assert-True -Condition (Test-Path -LiteralPath $path -PathType Leaf) `
        -Message "file missing: $RelativePath"
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

function Assert-CanonicalConfig {
    param(
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$ExpectedSource,
        [Parameter(Mandatory = $true)][string]$ExpectedPackages
    )
    $text = Read-WorkspaceText $RelativePath
    Assert-True -Condition ($text -notmatch '(?i)https?://') `
        -Message "network source in $RelativePath"
    [xml]$xml = $text
    $sourceChildren = @($xml.SelectNodes('/configuration/packageSources/*'))
    $sourceAdds = @($xml.SelectNodes('/configuration/packageSources/add'))
    $sourceClears = @($xml.SelectNodes('/configuration/packageSources/clear'))
    $fallbackChildren = @($xml.SelectNodes('/configuration/fallbackPackageFolders/*'))
    $fallbackClears = @($xml.SelectNodes('/configuration/fallbackPackageFolders/clear'))
    $configAdds = @($xml.SelectNodes('/configuration/config/add'))
    Assert-True -Condition (
        $sourceChildren.Count -eq 2 -and
        $sourceAdds.Count -eq 1 -and
        $sourceClears.Count -eq 1 -and
        [string]$sourceAdds[0].GetAttribute('key') -ceq 'SwSearchOffline' -and
        [string]$sourceAdds[0].GetAttribute('value') -ceq $ExpectedSource -and
        $fallbackChildren.Count -eq 1 -and
        $fallbackClears.Count -eq 1 -and
        $configAdds.Count -eq 2) -Message "non-canonical shape in $RelativePath"
    $values = @{}
    foreach ($entry in $configAdds) {
        $key = [string]$entry.GetAttribute('key')
        Assert-True -Condition (-not $values.ContainsKey($key)) `
            -Message "duplicate config key in ${RelativePath}: $key"
        $values[$key] = [string]$entry.GetAttribute('value')
    }
    Assert-True -Condition (
        $values.Count -eq 2 -and
        $values['globalPackagesFolder'] -ceq $ExpectedPackages -and
        $values['auditMode'] -ceq 'direct') -Message "non-canonical values in $RelativePath"
}

Assert-CanonicalConfig -RelativePath 'installer/NuGet.Offline.config' `
    -ExpectedSource '.offline-feed' -ExpectedPackages '..\.packages-rc48-r2'
Assert-CanonicalConfig -RelativePath 'NuGet.config' `
    -ExpectedSource 'installer\.offline-feed' -ExpectedPackages '.packages-rc48-r2'

[xml]$restoreGraph = Read-WorkspaceText 'installer/RestoreGraph.proj'
$expectedLocalProperties = @(
    '_ExpectedRestoreConfigFile',
    '_ExpectedRestorePackagesPath',
    '_ExpectedRestoreSources',
    '_ExpectedMSBuildSDKsPath',
    '_ExpectedDirectoryBuildPropsPath',
    '_ActualRestoreConfigFile',
    '_ActualRestorePackagesPath',
    '_ActualRestoreSources',
    '_ActualNuGetPackages',
    '_ActualMSBuildSDKsPath',
    '_ActualDirectoryBuildPropsPath'
)
$actualLocalProperties = @(
    ([string]$restoreGraph.DocumentElement.GetAttribute('TreatAsLocalProperty')).Split(
        [char[]]@(';'),
        [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { $_.Trim() })
Assert-True -Condition (
    $restoreGraph.DocumentElement.Attributes.Count -eq 1 -and
    $actualLocalProperties.Count -eq $expectedLocalProperties.Count -and
    -not (Compare-Object -ReferenceObject $expectedLocalProperties `
        -DifferenceObject $actualLocalProperties -SyncWindow 0)) `
    -Message 'preflight internal properties are not an exact TreatAsLocalProperty set'

$portableRoots = @($restoreGraph.SelectNodes('/Project/ItemGroup/PortableRestoreRoot') |
        ForEach-Object { [string]$_.GetAttribute('Include') })
$runtimeRoots = @($restoreGraph.SelectNodes('/Project/ItemGroup/RuntimeRestoreRoot') |
        ForEach-Object { [string]$_.GetAttribute('Include') })
$installerRoots = @($restoreGraph.SelectNodes('/Project/ItemGroup/InstallerRestoreRoot') |
        ForEach-Object { [string]$_.GetAttribute('Include') })
$expectedRuntimeRoots = @(
    '$(MSBuildThisFileDirectory)..\src\SwSearch.App\SwSearch.App.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.Broker\SwSearch.Broker.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.Contracts\SwSearch.Contracts.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.Everything\SwSearch.Everything.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.Indexer\SwSearch.Indexer.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.InstallerCoordinator\SwSearch.InstallerCoordinator.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.Integration\SwSearch.Integration.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.Metadata.Worker\SwSearch.Metadata.Worker.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.PropertyConfig\SwSearch.PropertyConfig.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.PropertyViewer\SwSearch.PropertyViewer.csproj',
    '$(MSBuildThisFileDirectory)..\src\SwSearch.WebUi\SwSearch.WebUi.csproj'
)
Assert-True -Condition (
    $portableRoots.Count -eq 1 -and
    $portableRoots[0] -ceq '$(MSBuildThisFileDirectory)..\SolidWorksSearch.sln') `
    -Message 'portable traversal must be the solution exactly once'
Assert-True -Condition (
    $runtimeRoots.Count -eq $expectedRuntimeRoots.Count -and
    -not (Compare-Object -ReferenceObject $expectedRuntimeRoots -DifferenceObject $runtimeRoots -SyncWindow 0)) `
    -Message 'runtime root order/set drifted'
Assert-True -Condition (
    $installerRoots.Count -eq 1 -and
    $installerRoots[0] -ceq '$(MSBuildThisFileDirectory)wix\SwSearch.Package.wixproj') `
    -Message 'WiX traversal root drifted'

$msbuildCalls = @($restoreGraph.SelectNodes('/Project/Target[@Name="Restore"]/MSBuild'))
Assert-True -Condition ($msbuildCalls.Count -eq 3) -Message 'restore must contain exactly three ordered MSBuild calls'
$expectedCalls = @('@(PortableRestoreRoot)', '@(RuntimeRestoreRoot)', '@(InstallerRestoreRoot)')
$expectedRemovedProperties = @(
    'RuntimeIdentifier',
    'RuntimeIdentifiers',
    'PublishRuntimeIdentifier',
    'SelfContained',
    'SwSearchFrozenRuntimeGraph',
    'RestoreConfigFile',
    'RestorePackagesPath',
    'RestoreSources',
    'RestoreNoCache',
    'RestoreDisableParallel',
    'RestoreIgnoreFailedSources',
    'NuGetAudit',
    'RestoreAdditionalProjectSources',
    'RestoreFallbackFolders',
    'RestoreAdditionalProjectFallbackFolders',
    'RestoreAdditionalProjectFallbackFoldersExcludes',
    'RestoreRepositoryPath',
    'RestoreRepositoryPathOverride',
    'RestoreRootConfigDirectory',
    'DirectoryBuildPropsPath',
    'DirectoryBuildTargetsPath',
    'DirectoryPackagesPropsPath',
    'ImportDirectoryBuildTargets',
    'ImportDirectoryPackagesProps',
    'ImportProjectExtensionProps',
    'ImportProjectExtensionTargets',
    'ExcludeRestorePackageImports',
    'MSBuildEnableWorkloadResolver',
    '_RestoreSourcesOverride',
    '_RestoreFallbackFoldersOverride',
    '_RestorePackagesPathOverride',
    '_RestoreRepositoryPathOverride'
)
$commonChildProperties = [ordered]@{
    RestoreConfigFile = '$(RestoreConfigFile)'
    RestorePackagesPath = '$(RestorePackagesPath)'
    RestoreSources = '$(RestoreSources)'
    RestoreNoCache = 'true'
    RestoreDisableParallel = 'true'
    NuGetAudit = 'false'
    RestoreIgnoreFailedSources = 'false'
    RestoreAdditionalProjectSources = ''
    RestoreFallbackFolders = ''
    RestoreAdditionalProjectFallbackFolders = ''
    RestoreAdditionalProjectFallbackFoldersExcludes = ''
    RestoreRepositoryPath = ''
    RestoreRepositoryPathOverride = ''
    RestoreRootConfigDirectory = ''
    DirectoryBuildPropsPath = '$(DirectoryBuildPropsPath)'
    DirectoryBuildTargetsPath = ''
    DirectoryPackagesPropsPath = ''
    ImportDirectoryBuildTargets = 'false'
    ImportDirectoryPackagesProps = 'false'
    ImportProjectExtensionProps = 'false'
    ImportProjectExtensionTargets = 'false'
    ExcludeRestorePackageImports = 'true'
    MSBuildEnableWorkloadResolver = 'false'
}
for ($index = 0; $index -lt $expectedCalls.Count; $index++) {
    $call = $msbuildCalls[$index]
    Assert-True -Condition (
        [string]$call.GetAttribute('Projects') -ceq $expectedCalls[$index] -and
        [string]$call.GetAttribute('Targets') -ceq 'Restore' -and
        [string]$call.GetAttribute('BuildInParallel') -ceq 'false') `
        -Message "restore call $index drifted"
    $attributeNames = @($call.Attributes | ForEach-Object { [string]$_.Name })
    $allowedAttributeNames = @(
        'Projects',
        'Targets',
        'BuildInParallel',
        'RemoveProperties',
        'Properties'
    )
    Assert-True -Condition (
        $attributeNames.Count -eq $allowedAttributeNames.Count -and
        -not (Compare-Object -ReferenceObject $allowedAttributeNames `
            -DifferenceObject $attributeNames -SyncWindow 0)) `
        -Message "restore call $index attribute allowlist drifted"
    $removedProperties = @(
        ([string]$call.GetAttribute('RemoveProperties')).Split(
            [char[]]@(';'),
            [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() })
    Assert-True -Condition (
        $removedProperties.Count -eq $expectedRemovedProperties.Count -and
        -not (Compare-Object -ReferenceObject $expectedRemovedProperties `
            -DifferenceObject $removedProperties -SyncWindow 0)) `
        -Message "restore call $index RemoveProperties drifted"

    $propertyEntries = @(
        ([string]$call.GetAttribute('Properties')).Split(
            [char[]]@(';'),
            [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() })
    $propertyMap = @{}
    foreach ($entry in $propertyEntries) {
        $equalsIndex = $entry.IndexOf('=')
        Assert-True -Condition ($equalsIndex -gt 0) `
            -Message "restore call $index has malformed property: $entry"
        $key = $entry.Substring(0, $equalsIndex)
        $value = $entry.Substring($equalsIndex + 1)
        Assert-True -Condition (-not $propertyMap.ContainsKey($key)) `
            -Message "restore call $index duplicates property: $key"
        $propertyMap[$key] = $value
    }
    $expectedPropertyMap = [ordered]@{}
    foreach ($entry in $commonChildProperties.GetEnumerator()) {
        $expectedPropertyMap[$entry.Key] = $entry.Value
    }
    if ($index -eq 1) {
        $expectedPropertyMap['RuntimeIdentifier'] = 'win-x64'
        $expectedPropertyMap['SelfContained'] = 'false'
        $expectedPropertyMap['SwSearchFrozenRuntimeGraph'] = 'true'
    }
    Assert-True -Condition ($propertyMap.Count -eq $expectedPropertyMap.Count) `
        -Message "restore call $index property count drifted"
    foreach ($entry in $expectedPropertyMap.GetEnumerator()) {
        Assert-True -Condition (
            $propertyMap.ContainsKey($entry.Key) -and
            [string]$propertyMap[$entry.Key] -ceq [string]$entry.Value) `
            -Message "restore call $index property drifted: $($entry.Key)"
    }
}

$validationTargets = @($restoreGraph.SelectNodes('/Project/Target[@Name="ValidateRestoreBoundaries"]'))
Assert-True -Condition (
    $validationTargets.Count -eq 1 -and
    $validationTargets[0].Attributes.Count -eq 1 -and
    [string]$validationTargets[0].GetAttribute('Name') -ceq 'ValidateRestoreBoundaries') `
    -Message 'validation target shape drifted'
$restoreTargets = @($restoreGraph.SelectNodes('/Project/Target[@Name="Restore"]'))
Assert-True -Condition (
    $restoreTargets.Count -eq 1 -and
    $restoreTargets[0].Attributes.Count -eq 2 -and
    [string]$restoreTargets[0].GetAttribute('DependsOnTargets') -ceq 'ValidateRestoreBoundaries') `
    -Message 'restore target no longer depends exactly on boundary validation'
Assert-True -Condition (
    @($restoreGraph.SelectNodes('/Project/Target[@Name="Restore"]/Error')).Count -eq 0) `
    -Message 'restore actions contain inline errors outside the validation target'

$restoreErrors = @($validationTargets[0].SelectNodes('Error'))
$expectedErrorConditions = @(
    "'`$(_ActualRestoreConfigFile)' != '`$(_ExpectedRestoreConfigFile)'",
    "'`$(_ActualRestorePackagesPath)' != '`$(_ExpectedRestorePackagesPath)'",
    "'`$(_ActualRestoreSources)' != '`$(_ExpectedRestoreSources)'",
    "'`$(_ActualNuGetPackages)' != '`$(_ExpectedRestorePackagesPath)'",
    "'`$(_ActualMSBuildSDKsPath)' != '`$(_ExpectedMSBuildSDKsPath)'",
    "'`$(_ActualDirectoryBuildPropsPath)' != '`$(_ExpectedDirectoryBuildPropsPath)'",
    "'`$(ImportDirectoryBuildTargets)' != 'false'",
    "'`$(ImportDirectoryPackagesProps)' != 'false'",
    "'`$(ImportProjectExtensionProps)' != 'false'",
    "'`$(ImportProjectExtensionTargets)' != 'false'",
    "'`$(ExcludeRestorePackageImports)' != 'true'",
    "'`$(MSBuildEnableWorkloadResolver)' != 'false'",
    "Exists('`$(_ExpectedRestorePackagesPath)')",
    "'`$(RestoreAdditionalProjectSources)' != ''",
    "'`$(RestoreFallbackFolders)' != ''",
    "'`$(RestoreAdditionalProjectFallbackFolders)' != ''",
    "'`$(RestoreAdditionalProjectFallbackFoldersExcludes)' != ''",
    "'`$(RestoreRepositoryPath)' != ''",
    "'`$(RestoreRepositoryPathOverride)' != ''",
    "'`$(RestoreRootConfigDirectory)' != ''"
)
Assert-True -Condition ($restoreErrors.Count -eq $expectedErrorConditions.Count) `
    -Message 'canonical one-shot preflight error count drifted'
for ($index = 0; $index -lt $restoreErrors.Count; $index++) {
    $errorNode = $restoreErrors[$index]
    $errorAttributes = @($errorNode.Attributes | ForEach-Object { [string]$_.Name })
    Assert-True -Condition (
        $errorAttributes.Count -eq 2 -and
        $errorAttributes[0] -ceq 'Condition' -and
        $errorAttributes[1] -ceq 'Text' -and
        [string]$errorNode.GetAttribute('Condition') -ceq $expectedErrorConditions[$index] -and
        -not [string]::IsNullOrWhiteSpace([string]$errorNode.GetAttribute('Text'))) `
        -Message "canonical preflight error $index drifted"
}

[xml]$buildProps = Read-WorkspaceText 'Directory.Build.props'
$runtimeGroups = @($buildProps.SelectNodes(
        "/Project/ItemGroup[@Condition=`"'`$(SwSearchFrozenRuntimeGraph)' == 'true'`"]"))
Assert-True -Condition ($runtimeGroups.Count -eq 1) `
    -Message 'frozen runtime PackageDownload group missing or duplicated'
$downloads = @($runtimeGroups[0].SelectNodes('PackageDownload'))
$expectedDownloads = @(
    'Microsoft.AspNetCore.App.Runtime.win-x64',
    'Microsoft.NETCore.App.Runtime.win-x64',
    'Microsoft.WindowsDesktop.App.Runtime.win-x64'
)
Assert-True -Condition ($downloads.Count -eq $expectedDownloads.Count) `
    -Message 'frozen runtime PackageDownload count drifted'
for ($index = 0; $index -lt $downloads.Count; $index++) {
    Assert-True -Condition (
        [string]$downloads[$index].GetAttribute('Include') -ceq $expectedDownloads[$index] -and
        [string]$downloads[$index].GetAttribute('Version') -ceq '[8.0.27]') `
        -Message "frozen runtime PackageDownload $index drifted"
}

$productionFiles = @(
    'installer/New-RestoreAssetsLock.ps1',
    'installer/Build-InstallerActions.ps1',
    'installer/Build-Msi.ps1',
    'installer/Build-Taskpane.ps1',
    'installer/Publish.ps1',
    'installer/Verify-Msi.ps1',
    'installer/Verify-Setup.ps1',
    'src/SwSearch.App/SwSearch.App.csproj',
    'src/SwSearch.Integration/SwSearch.Integration.csproj',
    'src/SwSearch.PropertyViewer/SwSearch.PropertyViewer.csproj'
)
foreach ($relative in $productionFiles) {
    $text = Read-WorkspaceText $relative
    Assert-True -Condition ($text.IndexOf('.packages-rc48-r2', [StringComparison]::Ordinal) -ge 0) `
        -Message "r2 package root missing from $relative"
    Assert-True -Condition ($text -notmatch '(?<![A-Za-z0-9_-])\.packages-rc48(?!-r2)') `
        -Message "legacy package root remains in $relative"
}

$lockGenerator = Read-WorkspaceText 'installer/New-RestoreAssetsLock.ps1'
foreach ($required in @(
        "sdkResolverConfig = Get-FrozenFile 'NuGet.config'",
        "packageFolder = [ordered]@{ path = '.packages-rc48-r2' }",
        "restoreGraph = Get-FrozenFile 'installer/RestoreGraph.proj'")) {
    Assert-True -Condition ($lockGenerator.IndexOf($required, [StringComparison]::Ordinal) -ge 0) `
        -Message "lock generator omitted: $required"
}

$gate = Read-WorkspaceText 'installer/Assert-FrozenRestoreGraph.ps1'
foreach ($required in @(
        'FROZEN_RESTORE_GRAPH_SOURCE_NOT_FROZEN',
        '.packages-rc48-r2',
        'SDK resolver config',
        'FROZEN_RESTORE_GRAPH_SDK_SET',
        'FROZEN_RESTORE_GRAPH_PACKAGE_DIRECTORY_SET',
        'FROZEN_RESTORE_GRAPH_PACKAGE_SOURCE_MISMATCH',
        'FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_HASH',
        'FROZEN_RESTORE_GRAPH_PACKAGE_CONTENT_EXTRA')) {
    Assert-True -Condition ($gate.IndexOf($required, [StringComparison]::Ordinal) -ge 0) `
        -Message "production gate omitted: $required"
}

$preflightFixture = Read-WorkspaceText 'tests/installer/Test-RestoreGraphGlobalPropertyPreflight.ps1'
foreach ($required in @(
        '-target:ValidateRestoreBoundaries',
        '-noAutoResponse',
        'RestoreAdditionalProjectSources',
        'DirectoryBuildPropsPath',
        'ImportDirectoryPackagesProps',
        'ImportProjectExtensionProps',
        'ExcludeRestorePackageImports',
        'MSBuildEnableWorkloadResolver',
        '_ExpectedRestoreConfigFile',
        '_ActualRestoreConfigFile',
        'paired-bypass',
        'directory-props-paired-bypass',
        'PASS restore-graph-global-property-preflight cases=18')) {
    Assert-True -Condition (
        $preflightFixture.IndexOf($required, [StringComparison]::Ordinal) -ge 0) `
        -Message "global-property preflight fixture omitted: $required"
}
Assert-True -Condition (
    $preflightFixture.IndexOf('-target:Restore', [StringComparison]::Ordinal) -lt 0 -and
    $preflightFixture.IndexOf('dotnet restore', [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    -Message 'global-property preflight fixture may invoke restore'

Write-Output 'PASS restore-graph-static-r2-offline-runtime-split'
