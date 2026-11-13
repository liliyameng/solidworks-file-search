[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$graph = Join-Path $root 'installer\RestoreGraph.proj'
$config = Join-Path $root 'installer\NuGet.Offline.config'
$packages = Join-Path $root '.packages-rc48-r2'
$feed = Join-Path $root 'installer\.offline-feed'
$sdkPath = 'C:\Program Files\dotnet\sdk\8.0.421\Sdks'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$msbuildDll = 'C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll'
$directoryBuildProps = Join-Path $root 'Directory.Build.props'

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    if ($Value.Contains('"')) { throw "Unsupported quote in native argument: $Value" }
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '\s') { return $Value }
    if ($Value.EndsWith('\', [StringComparison]::Ordinal)) {
        throw "Unsupported trailing slash in quoted native argument: $Value"
    }
    return '"' + $Value + '"'
}

function Invoke-Preflight {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Properties,
        [Parameter(Mandatory = $true)][string]$Case
    )

    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @(
            'exec',
            $msbuildDll,
            $graph,
            '-target:ValidateRestoreBoundaries',
            '-noAutoResponse',
            '-nologo',
            '-verbosity:minimal',
            '-nodeReuse:false')) {
        $arguments.Add($argument)
    }
    foreach ($key in @($Properties.Keys | Sort-Object)) {
        $arguments.Add("-property:$key=$($Properties[$key])")
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnet
    $startInfo.Arguments = (@($arguments | ForEach-Object {
                ConvertTo-NativeArgument -Value $_
            }) -join ' ')
    $startInfo.WorkingDirectory = $root
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables['NUGET_PACKAGES'] = $packages
    $startInfo.EnvironmentVariables['MSBuildSDKsPath'] = $sdkPath
    $startInfo.EnvironmentVariables['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $startInfo.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $startInfo.EnvironmentVariables['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE'] = '1'

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Preflight did not start: $Case" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            [void]$process.WaitForExit(5000)
            throw "Preflight timed out: $Case"
        }
        [pscustomobject]@{
            Case = $Case
            ExitCode = $process.ExitCode
            Output = ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-Exit {
    param(
        [Parameter(Mandatory = $true)][object]$Result,
        [Parameter(Mandatory = $true)][int]$Expected,
        [string]$Contains = ''
    )
    if ($Result.ExitCode -ne $Expected) {
        throw "Unexpected preflight exit for $($Result.Case): $($Result.ExitCode)`n$($Result.Output)"
    }
    if ($Contains.Length -gt 0 -and
        $Result.Output.IndexOf($Contains, [StringComparison]::Ordinal) -lt 0) {
        throw "Preflight output for $($Result.Case) omitted '$Contains':`n$($Result.Output)"
    }
}

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Frozen dotnet host is missing: $dotnet"
}
if (-not (Test-Path -LiteralPath $sdkPath -PathType Container)) {
    throw "Frozen SDK directory is missing: $sdkPath"
}
if (-not (Test-Path -LiteralPath $msbuildDll -PathType Leaf)) {
    throw "Frozen MSBuild assembly is missing: $msbuildDll"
}
if (Test-Path -LiteralPath $packages) {
    throw "The pre-restore package directory must not exist: $packages"
}

$base = [ordered]@{
    RestoreConfigFile = $config
    RestorePackagesPath = $packages
    RestoreSources = $feed
    NUGET_PACKAGES = $packages
    MSBuildSDKsPath = $sdkPath
    DirectoryBuildPropsPath = $directoryBuildProps
    ImportDirectoryBuildTargets = 'false'
    ImportDirectoryPackagesProps = 'false'
    ImportProjectExtensionProps = 'false'
    ImportProjectExtensionTargets = 'false'
    ExcludeRestorePackageImports = 'true'
    MSBuildEnableWorkloadResolver = 'false'
}

Assert-Exit -Result (Invoke-Preflight -Properties $base -Case 'canonical') -Expected 0

$internalPoison = [ordered]@{} + $base
$internalPoison['_ExpectedRestoreConfigFile'] = (Join-Path $root 'wrong.config')
$internalPoison['_ActualRestoreConfigFile'] = (Join-Path $root 'wrong.config')
$internalPoison['_ExpectedDirectoryBuildPropsPath'] = 'C:\untrusted\Directory.Build.props'
$internalPoison['_ActualDirectoryBuildPropsPath'] = 'C:\untrusted\Directory.Build.props'
Assert-Exit -Result (Invoke-Preflight -Properties $internalPoison `
        -Case 'internal-properties-are-local') -Expected 0

$pairedBypass = [ordered]@{} + $base
$pairedBypass['RestoreConfigFile'] = (Join-Path $root 'wrong.config')
$pairedBypass['_ExpectedRestoreConfigFile'] = $pairedBypass['RestoreConfigFile']
$pairedBypass['_ActualRestoreConfigFile'] = $pairedBypass['RestoreConfigFile']
Assert-Exit -Result (Invoke-Preflight -Properties $pairedBypass -Case 'paired-bypass') `
    -Expected 1 -Contains 'RestoreConfigFile must be the canonical frozen offline NuGet config.'

$directoryPropsPairedBypass = [ordered]@{} + $base
$directoryPropsPairedBypass['DirectoryBuildPropsPath'] =
    'C:\untrusted\Directory.Build.props'
$directoryPropsPairedBypass['_ExpectedDirectoryBuildPropsPath'] =
    $directoryPropsPairedBypass['DirectoryBuildPropsPath']
$directoryPropsPairedBypass['_ActualDirectoryBuildPropsPath'] =
    $directoryPropsPairedBypass['DirectoryBuildPropsPath']
Assert-Exit -Result (Invoke-Preflight -Properties $directoryPropsPairedBypass `
        -Case 'directory-props-paired-bypass') -Expected 1 `
    -Contains 'DirectoryBuildPropsPath must resolve to the frozen workspace Directory.Build.props.'

$injections = @(
    [pscustomobject]@{
        Name = 'additional-source'
        Key = 'RestoreAdditionalProjectSources'
        Value = 'https://example.invalid/v3/index.json'
        Text = 'RestoreAdditionalProjectSources must be empty'
    },
    [pscustomobject]@{
        Name = 'fallback-folder'
        Key = 'RestoreFallbackFolders'
        Value = 'C:\untrusted-fallback'
        Text = 'RestoreFallbackFolders must be empty'
    },
    [pscustomobject]@{
        Name = 'additional-fallback'
        Key = 'RestoreAdditionalProjectFallbackFolders'
        Value = 'C:\untrusted-additional-fallback'
        Text = 'RestoreAdditionalProjectFallbackFolders must be empty'
    },
    [pscustomobject]@{
        Name = 'additional-fallback-excludes'
        Key = 'RestoreAdditionalProjectFallbackFoldersExcludes'
        Value = 'C:\untrusted-exclude'
        Text = 'RestoreAdditionalProjectFallbackFoldersExcludes must be empty'
    },
    [pscustomobject]@{
        Name = 'repository-path'
        Key = 'RestoreRepositoryPath'
        Value = 'C:\untrusted-repository'
        Text = 'RestoreRepositoryPath must be empty'
    },
    [pscustomobject]@{
        Name = 'repository-path-override'
        Key = 'RestoreRepositoryPathOverride'
        Value = 'C:\untrusted-repository-override'
        Text = 'RestoreRepositoryPathOverride must be empty'
    },
    [pscustomobject]@{
        Name = 'root-config-directory'
        Key = 'RestoreRootConfigDirectory'
        Value = 'C:\untrusted-config-root'
        Text = 'RestoreRootConfigDirectory must be empty'
    },
    [pscustomobject]@{
        Name = 'directory-build-props-path'
        Key = 'DirectoryBuildPropsPath'
        Value = 'C:\untrusted\Directory.Build.props'
        Text = 'DirectoryBuildPropsPath must resolve to the frozen workspace Directory.Build.props'
    },
    [pscustomobject]@{
        Name = 'directory-build-targets-import'
        Key = 'ImportDirectoryBuildTargets'
        Value = 'true'
        Text = 'ImportDirectoryBuildTargets must be false'
    },
    [pscustomobject]@{
        Name = 'directory-packages-props-import'
        Key = 'ImportDirectoryPackagesProps'
        Value = 'true'
        Text = 'ImportDirectoryPackagesProps must be false'
    },
    [pscustomobject]@{
        Name = 'project-extension-props-import'
        Key = 'ImportProjectExtensionProps'
        Value = 'true'
        Text = 'ImportProjectExtensionProps must be false'
    },
    [pscustomobject]@{
        Name = 'project-extension-targets-import'
        Key = 'ImportProjectExtensionTargets'
        Value = 'true'
        Text = 'ImportProjectExtensionTargets must be false'
    },
    [pscustomobject]@{
        Name = 'restore-package-imports'
        Key = 'ExcludeRestorePackageImports'
        Value = 'false'
        Text = 'ExcludeRestorePackageImports must be true'
    },
    [pscustomobject]@{
        Name = 'workload-resolver'
        Key = 'MSBuildEnableWorkloadResolver'
        Value = 'true'
        Text = 'MSBuildEnableWorkloadResolver must be false'
    }
)
foreach ($injection in $injections) {
    $properties = [ordered]@{} + $base
    $properties[$injection.Key] = $injection.Value
    Assert-Exit -Result (Invoke-Preflight -Properties $properties -Case $injection.Name) `
        -Expected 1 -Contains $injection.Text
}

Write-Output 'PASS restore-graph-global-property-preflight cases=18'
