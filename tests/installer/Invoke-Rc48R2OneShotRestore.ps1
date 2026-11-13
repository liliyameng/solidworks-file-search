[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Freeze', 'Execute')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,
    [string]$ApprovedManifestSha256 = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$timeoutSeconds = 600
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
$testResults = [IO.Path]::GetFullPath((Join-Path $root 'TestResults')).TrimEnd('\') + '\'
$r2 = Join-Path $root '.packages-rc48-r2'
$feed = Join-Path $root 'installer\.offline-feed'
$graph = Join-Path $root 'installer\RestoreGraph.proj'
$offlineConfig = Join-Path $root 'installer\NuGet.Offline.config'
$resolverConfig = Join-Path $root 'NuGet.config'
$restoreLock = Join-Path $root 'installer\restore-assets.lock.json'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$msbuildDll = 'C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll'
$sdkPath = 'C:\Program Files\dotnet\sdk\8.0.421\Sdks'
$directoryBuildPropsPath = Join-Path $root 'Directory.Build.props'
$sentinelSource = Join-Path $root (
    'TestResults\rc13-helper-r2-a-20260716-114206985\dotnet-cli-home\.dotnet')
$utf8 = [Text.UTF8Encoding]::new($false)
$baselinePath = Join-Path $evidence 'approved-baseline.json'
$commandPath = Join-Path $evidence 'command.json'
$cliHome = Join-Path $evidence 'dotnet-cli-home'
$cliDotnet = Join-Path $cliHome '.dotnet'
$temp = Join-Path $evidence 'temp'
$httpCache = Join-Path $evidence 'nuget-http-cache'
$nugetScratch = Join-Path $evidence 'nuget-scratch'
$pluginCache = Join-Path $evidence 'nuget-plugin-cache'
$nugetPluginPaths = Join-Path $evidence 'nuget-plugin-paths-empty'
$nugetNetCorePluginPaths = Join-Path $evidence 'nuget-netcore-plugin-paths-empty'
$nugetNetFxPluginPaths = Join-Path $evidence 'nuget-netfx-plugin-paths-empty'
$msbuildUserExtensions = Join-Path $evidence 'msbuild-user-extensions-empty'
$isolatedUserProfile = Join-Path $evidence 'user-profile'
$isolatedLocalAppData = Join-Path $isolatedUserProfile 'AppData\Local'
$isolatedRoamingAppData = Join-Path $isolatedUserProfile 'AppData\Roaming'
$attemptPath = Join-Path $evidence 'execute-attempt.json'
$sentinelNames = @(
    '8.0.421.dotnetFirstUseSentinel',
    '8.0.421.aspNetCertificateSentinel'
)

function Write-JsonEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 16) + [Environment]::NewLine,
        $utf8)
}

function Write-NewJsonEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )
    $payload = $utf8.GetBytes(
        ($Value | ConvertTo-Json -Depth 16) + [Environment]::NewLine)
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $stream.Write($payload, 0, $payload.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-FileRecord {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Frozen input is missing: $full"
    }
    $item = Get-Item -LiteralPath $full
    [pscustomobject][ordered]@{
        path = $full
        bytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    }
}

function Get-DirectoryRecords {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "Frozen directory is missing: $full"
    }
    @(
        [IO.Directory]::EnumerateFiles($full, '*', [IO.SearchOption]::AllDirectories) |
            Sort-Object |
            ForEach-Object { Get-FileRecord -Path $_ }
    )
}

function Assert-FileRecordSchema {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Records,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $requiredProperties = @('path', 'bytes', 'sha256')
    foreach ($record in @($Records)) {
        foreach ($name in $requiredProperties) {
            if ($null -eq $record.PSObject.Properties[$name]) {
                throw "$Label record is missing required property: $name"
            }
        }
        if (@($record.PSObject.Properties.Name).Count -ne $requiredProperties.Count) {
            throw "$Label record contains an unexpected property."
        }
        $recordPath = [string]$record.path
        if ([String]::IsNullOrWhiteSpace($recordPath) -or
            -not [IO.Path]::IsPathRooted($recordPath) -or
            -not $recordPath.Equals(
                [IO.Path]::GetFullPath($recordPath),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label record path is not absolute."
        }
        $byteValue = $record.bytes
        $allowedByteTypes = @(
            'System.SByte',
            'System.Byte',
            'System.Int16',
            'System.UInt16',
            'System.Int32',
            'System.UInt32',
            'System.Int64'
        )
        if ($null -eq $byteValue -or
            $allowedByteTypes -cnotcontains $byteValue.GetType().FullName) {
            throw "$Label record byte count is not an integer CLR value."
        }
        if ([long]$byteValue -lt 0) {
            throw "$Label record byte count is negative."
        }
        if ([string]$record.sha256 -cnotmatch '^[A-F0-9]{64}$') {
            throw "$Label record SHA256 is malformed."
        }
    }
}

function Get-FileRecordByteTotal {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Records,
        [Parameter(Mandatory = $true)][string]$Label
    )
    Assert-FileRecordSchema -Records $Records -Label $Label
    [long]$total = 0
    foreach ($record in @($Records)) {
        $total += [long]$record.bytes
    }
    $total
}

function Get-ControlledSourceRecords {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($directoryName in @('src', 'tests', 'installer', 'scripts', 'docs')) {
        $directory = Join-Path $root $directoryName
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
        foreach ($file in [IO.Directory]::EnumerateFiles(
                $directory,
                '*',
                [IO.SearchOption]::AllDirectories)) {
            $relative = $file.Substring($root.Length).TrimStart('\')
            $normalized = '\' + $relative.Replace('/', '\') + '\'
            if ($normalized -match '\\(?:bin|obj|TestResults|\.build|\.offline-feed)\\') {
                continue
            }
            $files.Add($file)
        }
    }
    foreach ($name in @(
            'SolidWorksSearch.sln',
            'Directory.Build.props',
            'Directory.Build.targets',
            'global.json',
            'NuGet.config',
            'README.md')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add($path) }
    }
    @(
        $files |
            Sort-Object -Unique |
            ForEach-Object { Get-FileRecord -Path $_ }
    )
}

function Assert-RecordSetEqual {
    param(
        [Parameter(Mandatory = $true)][object[]]$Expected,
        [Parameter(Mandatory = $true)][object[]]$Actual,
        [Parameter(Mandatory = $true)][string]$Label
    )
    Assert-FileRecordSchema -Records $Expected -Label "$Label expected"
    Assert-FileRecordSchema -Records $Actual -Label "$Label actual"
    $expectedMap = @{}
    foreach ($record in $Expected) { $expectedMap[[string]$record.path] = $record }
    $actualMap = @{}
    foreach ($record in $Actual) { $actualMap[[string]$record.path] = $record }
    if ($expectedMap.Count -ne $actualMap.Count) {
        throw "$Label file count mismatch: $($expectedMap.Count) / $($actualMap.Count)"
    }
    foreach ($path in $expectedMap.Keys) {
        if (-not $actualMap.ContainsKey($path) -or
            [long]$actualMap[$path].bytes -ne [long]$expectedMap[$path].bytes -or
            [string]$actualMap[$path].sha256 -cne [string]$expectedMap[$path].sha256) {
            throw "$Label identity mismatch: $path"
        }
    }
}

function Assert-EmptyDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label directory is missing: $Path"
    }
    if (@([IO.Directory]::EnumerateFileSystemEntries($Path)).Count -ne 0) {
        throw "$Label directory is not empty: $Path"
    }
}

function Assert-NuGetPluginDiscoveryIsolation {
    foreach ($entry in @(
            [pscustomobject]@{ Path = $nugetPluginPaths; Label = 'NuGet common plugin paths' },
            [pscustomobject]@{ Path = $nugetNetCorePluginPaths; Label = 'NuGet .NET plugin paths' },
            [pscustomobject]@{ Path = $nugetNetFxPluginPaths; Label = 'NuGet .NET Framework plugin paths' })) {
        Assert-EmptyDirectory -Path $entry.Path -Label $entry.Label
    }
}

function Assert-UserProfileIsolation {
    $expectedProfileDirectories = @(
        [IO.Path]::GetFullPath((Join-Path $isolatedUserProfile 'AppData')),
        [IO.Path]::GetFullPath($isolatedLocalAppData),
        [IO.Path]::GetFullPath($isolatedRoamingAppData)
    ) | Sort-Object
    $actualProfileDirectories = @(
        [IO.Directory]::EnumerateDirectories(
            $isolatedUserProfile,
            '*',
            [IO.SearchOption]::AllDirectories) |
            ForEach-Object { [IO.Path]::GetFullPath($_) } |
            Sort-Object)
    if ($actualProfileDirectories.Count -ne $expectedProfileDirectories.Count -or
        (Compare-Object -ReferenceObject $expectedProfileDirectories `
            -DifferenceObject $actualProfileDirectories -SyncWindow 0)) {
        throw 'Isolated USERPROFILE directory tree is not exact.'
    }
    if (@([IO.Directory]::EnumerateFiles(
                $isolatedUserProfile,
                '*',
                [IO.SearchOption]::AllDirectories)).Count -ne 0) {
        throw 'Isolated USERPROFILE contains an unexpected file.'
    }
}

function Assert-DotNetCliHomeIsolation {
    $cliHomeEntries = @(
        [IO.Directory]::EnumerateFileSystemEntries(
            $cliHome,
            '*',
            [IO.SearchOption]::TopDirectoryOnly))
    if ($cliHomeEntries.Count -ne 1 -or
        -not [IO.Path]::GetFullPath($cliHomeEntries[0]).Equals(
            [IO.Path]::GetFullPath($cliDotnet),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Isolated DOTNET_CLI_HOME root is not exact.'
    }
    $actualSentinels = @(
        [IO.Directory]::EnumerateFiles(
            $cliDotnet,
            '*',
            [IO.SearchOption]::TopDirectoryOnly) |
            ForEach-Object { [IO.Path]::GetFileName($_) } |
            Sort-Object)
    $expectedSentinels = @($sentinelNames | Sort-Object)
    if ($actualSentinels.Count -ne $expectedSentinels.Count -or
        (Compare-Object -ReferenceObject $expectedSentinels `
            -DifferenceObject $actualSentinels -SyncWindow 0)) {
        throw 'Isolated DOTNET_CLI_HOME sentinel set is not exact.'
    }
    if (@([IO.Directory]::EnumerateDirectories(
                $cliDotnet,
                '*',
                [IO.SearchOption]::TopDirectoryOnly)).Count -ne 0) {
        throw 'Isolated DOTNET_CLI_HOME contains an unexpected directory.'
    }
}

function Assert-PrelaunchIsolation {
    foreach ($entry in @(
            [pscustomobject]@{ Path = $temp; Label = 'temporary workspace' },
            [pscustomobject]@{ Path = $httpCache; Label = 'NuGet HTTP cache' },
            [pscustomobject]@{ Path = $nugetScratch; Label = 'NuGet scratch' },
            [pscustomobject]@{ Path = $pluginCache; Label = 'NuGet plugin cache' },
            [pscustomobject]@{ Path = $msbuildUserExtensions; Label = 'MSBuild user extensions' },
            [pscustomobject]@{ Path = $isolatedLocalAppData; Label = 'isolated local app data' },
            [pscustomobject]@{ Path = $isolatedRoamingAppData; Label = 'isolated roaming app data' })) {
        Assert-EmptyDirectory -Path $entry.Path -Label $entry.Label
    }
    Assert-NuGetPluginDiscoveryIsolation
    Assert-UserProfileIsolation
    Assert-DotNetCliHomeIsolation
}

function ConvertTo-NativeArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    if ($Value.Contains('"')) { throw "Unsupported quote in native argument: $Value" }
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '\s') { return $Value }
    if ($Value.EndsWith('\', [StringComparison]::Ordinal)) {
        throw "Unsupported trailing slash in quoted native argument: $Value"
    }
    '"' + $Value + '"'
}

function Get-CommandDefinition {
    $consoleLog = Join-Path $evidence 'restore.console.log'
    $binlog = Join-Path $evidence 'restore.binlog'
    $arguments = @(
        'exec',
        $msbuildDll,
        $graph,
        '-target:Restore',
        '-noAutoResponse',
        '-nologo',
        '-verbosity:minimal',
        '-nodeReuse:false',
        '-maxCpuCount:1',
        "-binaryLogger:$binlog",
        '-fileLogger',
        "-fileLoggerParameters:LogFile=$consoleLog;Verbosity=diagnostic;Encoding=UTF-8",
        "-property:RestoreConfigFile=$offlineConfig",
        "-property:RestorePackagesPath=$r2",
        "-property:RestoreSources=$feed",
        "-property:NUGET_PACKAGES=$r2",
        "-property:MSBuildSDKsPath=$sdkPath",
        "-property:MSBuildUserExtensionsPath=$msbuildUserExtensions",
        "-property:DirectoryBuildPropsPath=$directoryBuildPropsPath",
        '-property:DirectoryBuildTargetsPath=',
        '-property:DirectoryPackagesPropsPath=',
        '-property:ImportDirectoryBuildTargets=false',
        '-property:ImportDirectoryPackagesProps=false',
        '-property:ImportProjectExtensionProps=false',
        '-property:ImportProjectExtensionTargets=false',
        '-property:ExcludeRestorePackageImports=true',
        '-property:MSBuildEnableWorkloadResolver=false',
        '-property:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false',
        '-property:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false',
        '-property:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false',
        '-property:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false',
        '-property:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false',
        '-property:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false',
        '-property:RestoreNoCache=true',
        '-property:RestoreDisableParallel=true',
        '-property:RestoreForceEvaluate=true',
        '-property:NuGetAudit=false',
        '-property:RestoreIgnoreFailedSources=false',
        '-property:RestoreAdditionalProjectSources=',
        '-property:RestoreFallbackFolders=',
        '-property:RestoreAdditionalProjectFallbackFolders=',
        '-property:RestoreAdditionalProjectFallbackFoldersExcludes=',
        '-property:RestoreRepositoryPath=',
        '-property:RestoreRepositoryPathOverride=',
        '-property:RestoreRootConfigDirectory='
    )
    $environment = [ordered]@{
        SystemRoot = $env:SystemRoot
        WINDIR = $env:WINDIR
        ComSpec = $env:ComSpec
        PATH = 'C:\Program Files\dotnet;C:\Windows\System32;C:\Windows'
        PATHEXT = $env:PATHEXT
        ProgramFiles = $env:ProgramFiles
        'ProgramFiles(x86)' = ${env:ProgramFiles(x86)}
        ProgramW6432 = $env:ProgramW6432
        USERPROFILE = $isolatedUserProfile
        LOCALAPPDATA = $isolatedLocalAppData
        APPDATA = $isolatedRoamingAppData
        DOTNET_ROOT = 'C:\Program Files\dotnet'
        DOTNET_MULTILEVEL_LOOKUP = '0'
        DOTNET_CLI_HOME = $cliHome
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        DOTNET_NOLOGO = '1'
        TEMP = $temp
        TMP = $temp
        NUGET_PACKAGES = $r2
        NUGET_HTTP_CACHE_PATH = $httpCache
        NUGET_SCRATCH = $nugetScratch
        NUGET_PLUGINS_CACHE_PATH = $pluginCache
        NUGET_PLUGIN_PATHS = $nugetPluginPaths
        NUGET_NETCORE_PLUGIN_PATHS = $nugetNetCorePluginPaths
        NUGET_NETFX_PLUGIN_PATHS = $nugetNetFxPluginPaths
        NUGET_CERT_REVOCATION_MODE = 'offline'
        MSBuildSDKsPath = $sdkPath
        MSBUILDDISABLENODEREUSE = '1'
        MSBUILDUSESERVER = '0'
    }
    [ordered]@{
        executable = $dotnet
        workingDirectory = $root
        target = 'Restore'
        timeoutSeconds = $timeoutSeconds
        arguments = $arguments
        environment = $environment
    }
}

if (-not $evidence.StartsWith($testResults, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($evidence) -notmatch '^rc48-r2-oneshot-restore-[0-9]{8}-[0-9]{9}$') {
    throw "Evidence directory is not a unique RC48 restore directory: $evidence"
}
if (Test-Path -LiteralPath $r2) { throw "RC48 r2 package folder already exists: $r2" }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "dotnet host missing: $dotnet" }
if (-not (Test-Path -LiteralPath $sdkPath -PathType Container)) { throw "SDK missing: $sdkPath" }

$fixedInputs = @(
    $dotnet,
    'C:\Program Files\dotnet\sdk\8.0.421\.version',
    $msbuildDll,
    'C:\Program Files\dotnet\sdk\8.0.421\MSBuild.deps.json',
    'C:\Program Files\dotnet\sdk\8.0.421\MSBuild.runtimeconfig.json',
    'C:\Program Files\dotnet\sdk\8.0.421\NuGet.targets',
    'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.27\data\FrameworkList.xml',
    'C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Ref\8.0.27\data\FrameworkList.xml',
    $offlineConfig,
    $resolverConfig,
    $graph,
    $restoreLock,
    $directoryBuildPropsPath
)
$fixedInputs += @($sentinelNames | ForEach-Object { Join-Path $cliDotnet $_ })

if ($Mode -eq 'Freeze') {
    if (Test-Path -LiteralPath $evidence) { throw "Evidence directory already exists: $evidence" }
    New-Item -ItemType Directory -Path $evidence | Out-Null
    New-Item -ItemType Directory -Path $cliDotnet, $temp, $httpCache, $nugetScratch, `
        $pluginCache, $nugetPluginPaths, $nugetNetCorePluginPaths, `
        $nugetNetFxPluginPaths, $msbuildUserExtensions, `
        $isolatedLocalAppData, $isolatedRoamingAppData | Out-Null
    foreach ($sentinel in $sentinelNames) {
        $source = Join-Path $sentinelSource $sentinel
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Audited first-use sentinel missing: $source"
        }
        [IO.File]::Copy($source, (Join-Path $cliDotnet $sentinel), $false)
    }
    Assert-PrelaunchIsolation
    $sourceRecords = @(Get-ControlledSourceRecords)
    $feedRecords = @(Get-DirectoryRecords -Path $feed)
    $fixedRecords = @($fixedInputs | ForEach-Object { Get-FileRecord -Path $_ })
    $feedBytes = Get-FileRecordByteTotal -Records $feedRecords -Label 'offline feed'
    if ($feedRecords.Count -ne 50 -or
        $feedBytes -ne 194634460L) {
        throw 'Offline feed identity mismatch before approval.'
    }
    $command = Get-CommandDefinition
    $baseline = [ordered]@{
        schema = 1
        createdUtc = [DateTime]::UtcNow.ToString('o')
        source = $sourceRecords
        feed = $feedRecords
        fixedInputs = $fixedRecords
        command = $command
    }
    Write-JsonEvidence -Path $baselinePath -Value $baseline
    Write-JsonEvidence -Path $commandPath -Value $command
    [pscustomobject]@{
        Result = 'FROZEN_AWAITING_INDEPENDENT_APPROVAL'
        Evidence = $evidence
        Manifest = $baselinePath
        ManifestSha256 = (Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash
        SourceFiles = $sourceRecords.Count
        FeedFiles = $feedRecords.Count
        FixedInputs = $fixedRecords.Count
    }
    return
}

if (-not (Test-Path -LiteralPath $evidence -PathType Container)) {
    throw "Approved evidence directory is missing: $evidence"
}
if ($ApprovedManifestSha256 -notmatch '^[A-F0-9]{64}$') {
    throw 'Approved manifest SHA256 is missing or malformed.'
}
if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
    throw "Approved baseline is missing: $baselinePath"
}
$actualBaselineHash = (Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash
if ($actualBaselineHash -cne $ApprovedManifestSha256) {
    throw "Approved baseline hash mismatch: $actualBaselineHash / $ApprovedManifestSha256"
}
$baseline = Get-Content -LiteralPath $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$baseline.schema -ne 1) { throw 'Approved baseline schema is invalid.' }
Assert-PrelaunchIsolation
$sourceBefore = @(Get-ControlledSourceRecords)
$feedBefore = @(Get-DirectoryRecords -Path $feed)
$fixedBefore = @($fixedInputs | ForEach-Object { Get-FileRecord -Path $_ })
Assert-RecordSetEqual -Expected @($baseline.source) -Actual $sourceBefore -Label 'approved source'
Assert-RecordSetEqual -Expected @($baseline.feed) -Actual $feedBefore -Label 'approved feed'
Assert-RecordSetEqual -Expected @($baseline.fixedInputs) -Actual $fixedBefore -Label 'approved fixed inputs'
$command = Get-CommandDefinition
$approvedCommandJson = $baseline.command | ConvertTo-Json -Depth 12 -Compress
$actualCommandJson = $command | ConvertTo-Json -Depth 12 -Compress
if ($actualCommandJson -cne $approvedCommandJson) { throw 'Approved command identity mismatch.' }
if ((Get-Content -LiteralPath $commandPath -Raw -Encoding UTF8 | ConvertFrom-Json |
        ConvertTo-Json -Depth 12 -Compress) -cne $approvedCommandJson) {
    throw 'Command evidence file no longer matches the approved baseline.'
}

Write-NewJsonEvidence -Path $attemptPath -Value ([ordered]@{
        schema = 1
        approvedManifestSha256 = $ApprovedManifestSha256
        executorSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        attemptedUtc = [DateTime]::UtcNow.ToString('o')
    })

$env:TEMP = $temp
$env:TMP = $temp
$nativeRunnerSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class Rc48SuspendedJobRunner
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 258;
    private const uint CLEANUP_WAIT_MILLISECONDS = 5000;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,
        int infoClass,
        IntPtr info,
        uint length,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static uint WaitForExit(IntPtr process, uint milliseconds, string context)
    {
        uint wait = WaitForSingleObject(process, milliseconds);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " wait failed.");
        return wait;
    }

    private static uint GetActiveJobProcessCount(IntPtr job, string context)
    {
        int size = Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
        IntPtr info = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    job,
                    JobObjectBasicAccountingInformation,
                    info,
                    (uint)size,
                    IntPtr.Zero))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    context + " job accounting query failed.");
            var accounting =
                (JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)Marshal.PtrToStructure(
                    info,
                    typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
            return accounting.ActiveProcesses;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    private static bool WaitForJobEmpty(
        IntPtr job,
        uint milliseconds,
        string context)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (GetActiveJobProcessCount(job, context) == 0)
                return true;
            if (stopwatch.ElapsedMilliseconds >= milliseconds)
                return false;
            Thread.Sleep(10);
        }
    }

    private static void TerminateOwnedProcessTree(
        IntPtr job,
        IntPtr process,
        bool assigned,
        uint exitCode,
        string context)
    {
        if (assigned)
        {
            if (job == IntPtr.Zero)
                throw new InvalidOperationException(
                    context + " lost its private job capability.");
            if (GetActiveJobProcessCount(job, context) == 0)
                return;

            bool jobTerminationRequested = TerminateJobObject(job, exitCode);
            int jobTerminationError = jobTerminationRequested
                ? 0
                : Marshal.GetLastWin32Error();
            if (WaitForJobEmpty(job, CLEANUP_WAIT_MILLISECONDS, context))
                return;
            if (!jobTerminationRequested)
                throw new Win32Exception(
                    jobTerminationError,
                    context + " job termination request failed and the owned tree remained alive.");
            throw new TimeoutException(
                context + " job termination was requested but ACTIVE_PROCESS_ZERO was not reached.");
        }

        if (process == IntPtr.Zero ||
            WaitForExit(process, 0, context) == WAIT_OBJECT_0)
            return;
        bool terminationRequested = TerminateProcess(process, exitCode);
        int terminationError = terminationRequested
            ? 0
            : Marshal.GetLastWin32Error();

        if (WaitForExit(process, CLEANUP_WAIT_MILLISECONDS, context) == WAIT_OBJECT_0)
            return;

        if (!terminationRequested)
            throw new Win32Exception(
                terminationError,
                context + " termination request failed and the owned process remained alive.");
        throw new TimeoutException(
            context + " termination was requested but the owned process remained alive.");
    }

    public static int Run(
        string executable,
        string commandLine,
        string workingDirectory,
        IDictionary<string, string> environment,
        int timeoutMilliseconds)
    {
        IntPtr job = IntPtr.Zero;
        IntPtr process = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        bool assigned = false;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr limitsPtr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, limitsPtr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                        limitsPtr, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(limitsPtr); }

            var environmentText = new StringBuilder();
            foreach (var pair in environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                environmentText.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            environmentText.Append('\0');
            environmentBlock = Marshal.StringToHGlobalUni(environmentText.ToString());

            var startup = new STARTUPINFO();
            startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION info;
            var mutableCommand = new StringBuilder(commandLine);
            if (!CreateProcess(executable, mutableCommand, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT,
                    environmentBlock, workingDirectory, ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = info.hProcess;
            thread = info.hThread;
            if (!AssignProcessToJobObject(job, process))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The suspended restore process could not be assigned to its private job.");
            assigned = true;
            if (ResumeThread(thread) == UInt32.MaxValue)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The job-bound restore process could not be resumed.");

            uint wait = WaitForExit(
                process,
                (uint)timeoutMilliseconds,
                "The one-shot restore");
            if (wait == WAIT_TIMEOUT)
            {
                TerminateOwnedProcessTree(
                    job,
                    process,
                    true,
                    1460,
                    "The timed-out one-shot restore");
                throw new TimeoutException("The one-shot restore exceeded its hard deadline.");
            }
            uint exitCode;
            if (!GetExitCodeProcess(process, out exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)exitCode);
        }
        finally
        {
            try
            {
                TerminateOwnedProcessTree(
                    job,
                    process,
                    assigned,
                    1223,
                    "One-shot restore cleanup");
            }
            finally
            {
                // Closing the configured job first is the final fail-safe for every
                // process that was successfully assigned to this private job.
                if (job != IntPtr.Zero) CloseHandle(job);
                if (thread != IntPtr.Zero) CloseHandle(thread);
                if (process != IntPtr.Zero) CloseHandle(process);
                if (environmentBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }
}
'@
Add-Type -TypeDefinition $nativeRunnerSource -Language CSharp

$arguments = @($command.arguments)
$nativeCommandLine = '"' + $dotnet + '" ' +
    (@($arguments | ForEach-Object { ConvertTo-NativeArgument -Value ([string]$_) }) -join ' ')
$nativeEnvironment = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $command.environment.psobject.Properties) {
    if (-not [String]::IsNullOrEmpty([string]$entry.Value)) {
        $nativeEnvironment[[string]$entry.Name] = [string]$entry.Value
    }
}

$startedUtc = [DateTime]::UtcNow
try {
    $exitCode = [Rc48SuspendedJobRunner]::Run(
        $dotnet,
        $nativeCommandLine,
        $root,
        $nativeEnvironment,
        $timeoutSeconds * 1000)
}
catch {
    Write-JsonEvidence -Path (Join-Path $evidence 'restore-failure.json') -Value ([ordered]@{
            startedUtc = $startedUtc.ToString('o')
            failedUtc = [DateTime]::UtcNow.ToString('o')
            error = $_.Exception.ToString()
        })
    throw
}
$endedUtc = [DateTime]::UtcNow
Write-JsonEvidence -Path (Join-Path $evidence 'restore-result.json') -Value ([ordered]@{
        startedUtc = $startedUtc.ToString('o')
        endedUtc = $endedUtc.ToString('o')
        elapsedMilliseconds = [long]($endedUtc - $startedUtc).TotalMilliseconds
        exitCode = $exitCode
    })
if ($exitCode -ne 0) { throw "The one-shot restore failed with exit code $exitCode." }
$binlog = Join-Path $evidence 'restore.binlog'
$consoleLog = Join-Path $evidence 'restore.console.log'
if (-not (Test-Path -LiteralPath $r2 -PathType Container)) {
    throw 'The one-shot restore returned success without creating the r2 package folder.'
}
foreach ($requiredOutput in @($binlog, $consoleLog)) {
    if (-not (Test-Path -LiteralPath $requiredOutput -PathType Leaf)) {
        throw "The one-shot restore output is missing: $requiredOutput"
    }
}
Assert-EmptyDirectory -Path $msbuildUserExtensions -Label 'MSBuild user extensions'
Assert-NuGetPluginDiscoveryIsolation
Assert-UserProfileIsolation
Assert-DotNetCliHomeIsolation

$sourceAfter = @(Get-ControlledSourceRecords)
$feedAfter = @(Get-DirectoryRecords -Path $feed)
$fixedAfter = @($fixedInputs | ForEach-Object { Get-FileRecord -Path $_ })
Assert-RecordSetEqual -Expected @($baseline.source) -Actual $sourceAfter -Label 'controlled source'
Assert-RecordSetEqual -Expected @($baseline.feed) -Actual $feedAfter -Label 'offline feed'
Assert-RecordSetEqual -Expected @($baseline.fixedInputs) -Actual $fixedAfter -Label 'fixed inputs'
Write-JsonEvidence -Path (Join-Path $evidence 'source-after.json') -Value $sourceAfter
Write-JsonEvidence -Path (Join-Path $evidence 'feed-after.json') -Value $feedAfter
Write-JsonEvidence -Path (Join-Path $evidence 'fixed-inputs-after.json') -Value $fixedAfter

[pscustomobject]@{
    Result = 'RESTORE_EXIT_ZERO_APPROVED_INPUTS_FROZEN'
    Evidence = $evidence
    ElapsedMilliseconds = [long]($endedUtc - $startedUtc).TotalMilliseconds
    FeedFiles = $feedAfter.Count
    PackageFolder = $r2
    Binlog = $binlog
    ConsoleLog = $consoleLog
}
