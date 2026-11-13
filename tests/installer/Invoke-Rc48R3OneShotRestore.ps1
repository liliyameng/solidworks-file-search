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
$r3 = Join-Path $root '.packages-rc48-r3'
$feed = Join-Path $root 'installer\.offline-feed'
$graph = Join-Path $root 'installer\RestoreGraph.proj'
$offlineConfig = Join-Path $root 'installer\NuGet.Offline.config'
$resolverConfig = Join-Path $root 'NuGet.config'
$restoreLock = Join-Path $root 'installer\restore-assets.lock.json'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$msbuildDll = 'C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll'
$sdkPath = 'C:\Program Files\dotnet\sdk\8.0.421\Sdks'
$msbuildExtensionsPath = 'C:\Program Files\dotnet\sdk\8.0.421'
$directoryBuildPropsPath = Join-Path $root 'Directory.Build.props'
$utf8 = [Text.UTF8Encoding]::new($false)
$utf8Strict = [Text.UTF8Encoding]::new($false, $true)
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
$stdoutPath = Join-Path $evidence 'restore.stdout.log'
$stderrPath = Join-Path $evidence 'restore.stderr.log'
$resultPath = Join-Path $evidence 'restore-result.json'
$failurePath = Join-Path $evidence 'restore-failure.json'
$binlogPath = Join-Path $evidence 'restore.binlog'
$consoleLogPath = Join-Path $evidence 'restore.console.log'
$sourceAfterPath = Join-Path $evidence 'source-after.json'
$feedAfterPath = Join-Path $evidence 'feed-after.json'
$fixedAfterPath = Join-Path $evidence 'fixed-inputs-after.json'
$toolchainAfterPath = Join-Path $evidence 'toolchain-after.json'
$toolchainSelectionSetId = 'dotnet-sdk-8.0.421-netcore-8.0.27-win-x64-v1'
$toolchainFileLimit = 5000
$toolchainDirectoryLimit = 5000
$toolchainEntryLimit = 10000
$toolchainByteLimit = 600MB
$toolchainDepthLimit = 16
$approvedInputFileLimit = 20000
$approvedInputDirectoryLimit = 10000
$approvedInputByteLimit = 2GB
$toolchainFileRoots = @(
    'C:\Program Files\dotnet\dotnet.exe'
)
$toolchainDirectoryRoots = @(
    'C:\Program Files\dotnet\sdk\8.0.421',
    'C:\Program Files\dotnet\host\fxr',
    'C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.27',
    'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.27',
    'C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Ref\8.0.27',
    'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\8.0.27'
)
$script:evidenceLease = $null
$script:approvedInputLease = $null
$script:freezeCreatedSentinelPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$evidenceTempActivated = $false
$expectedEnvironmentNames = @(
    'SystemRoot',
    'WINDIR',
    'ComSpec',
    'PATH',
    'PATHEXT',
    'ProgramFiles',
    'ProgramFiles(x86)',
    'ProgramW6432',
    'USERPROFILE',
    'LOCALAPPDATA',
    'APPDATA',
    'DOTNET_ROOT',
    'DOTNET_MULTILEVEL_LOOKUP',
    'DOTNET_ROLL_FORWARD',
    'DOTNET_CLI_HOME',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE',
    'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH',
    'DOTNET_GENERATE_ASPNET_CERTIFICATE',
    'DOTNET_NOLOGO',
    'TEMP',
    'TMP',
    'NUGET_PACKAGES',
    'NUGET_HTTP_CACHE_PATH',
    'NUGET_SCRATCH',
    'NUGET_PLUGINS_CACHE_PATH',
    'NUGET_PLUGIN_PATHS',
    'NUGET_NETCORE_PLUGIN_PATHS',
    'NUGET_NETFX_PLUGIN_PATHS',
    'NUGET_CERT_REVOCATION_MODE',
    'MSBuildSDKsPath',
    'MSBUILDDISABLENODEREUSE',
    'MSBUILDUSESERVER'
)
$sentinelNames = @(
    '8.0.421.dotnetFirstUseSentinel',
    '8.0.421.aspNetCertificateSentinel'
)

function ConvertTo-JsonEvidenceBytes {
    param([Parameter(Mandatory = $true)][object]$Value)
    $utf8Strict.GetBytes(
        ($Value | ConvertTo-Json -Depth 16) + [Environment]::NewLine)
}

function Assert-CanonicalJsonBytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Actual,
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )
    [byte[]]$canonical = ConvertTo-JsonEvidenceBytes -Value $Value
    if ($Actual.Length -ne $canonical.Length) {
        throw "$Label is not canonical strict UTF-8 JSON."
    }
    for ($index = 0; $index -lt $Actual.Length; $index++) {
        if ($Actual[$index] -ne $canonical[$index]) {
            throw "$Label is not canonical strict UTF-8 JSON."
        }
    }
}

function Write-NewJsonEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )
    [byte[]]$payload = ConvertTo-JsonEvidenceBytes -Value $Value
    if ($null -eq $script:evidenceLease) {
        throw 'Evidence output cannot be created before the evidence lease is acquired.'
    }
    $script:evidenceLease.WriteNewFile(
        $Path,
        [IO.Path]::GetFileName($Path),
        $payload)
}

function Write-NewEmptyEvidenceFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ($null -eq $script:evidenceLease) {
        throw 'Evidence output cannot be created before the evidence lease is acquired.'
    }
    $script:evidenceLease.WriteNewFile(
        $Path,
        [IO.Path]::GetFileName($Path),
        [byte[]]::new(0))
}

function Get-FileRecord {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $stream = [IO.FileStream]::new(
        $full,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read,
        131072,
        [IO.FileOptions]::SequentialScan)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = [BitConverter]::ToString(
                $sha.ComputeHash($stream)).Replace('-', '')
        }
        finally {
            $sha.Dispose()
        }
        [pscustomobject][ordered]@{
            path = $full
            bytes = [long]$stream.Length
            sha256 = $hash
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-HeldSentinelRecord {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ($Mode -cne 'Freeze') {
        throw 'Held sentinel records are valid only while freezing the current evidence lease.'
    }
    if ($null -eq $script:evidenceLease) {
        throw 'Held sentinel records require the current evidence lease.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    $exactEvidenceRoot = [IO.Path]::GetFullPath($evidence).TrimEnd('\')
    $exactSentinelDirectory = [IO.Path]::GetFullPath($cliDotnet).TrimEnd('\')
    if (-not $exactSentinelDirectory.StartsWith(
            $exactEvidenceRoot + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Held sentinel directory escaped the exact evidence root.'
    }
    $expectedSentinelPaths = @(
        [IO.Path]::GetFullPath((Join-Path $exactSentinelDirectory '8.0.421.dotnetFirstUseSentinel')),
        [IO.Path]::GetFullPath((Join-Path $exactSentinelDirectory '8.0.421.aspNetCertificateSentinel'))
    )
    $isExpectedSentinelPath = $false
    foreach ($expectedSentinelPath in $expectedSentinelPaths) {
        if ($full.Equals($expectedSentinelPath, [StringComparison]::OrdinalIgnoreCase)) {
            $isExpectedSentinelPath = $true
            break
        }
    }
    if (-not $isExpectedSentinelPath -or
        $script:freezeCreatedSentinelPaths.Count -ne 2 -or
        -not $script:freezeCreatedSentinelPaths.Contains($full)) {
        throw 'Held sentinel record path is not one of the exact sentinels created by this evidence lease.'
    }
    # Validate the root, anchored sentinel directory, and held sentinel objects before
    # the shared read. The existing CREATE_NEW write handles still deny writers/deleters.
    $script:evidenceLease.Validate()
    $stream = [IO.FileStream]::new(
        $full,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite,
        131072,
        [IO.FileOptions]::SequentialScan)
    try {
        if ($stream.Length -ne 0) {
            throw 'Held sentinel changed from its required empty content.'
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = [BitConverter]::ToString(
                $sha.ComputeHash($stream)).Replace('-', '')
        }
        finally {
            $sha.Dispose()
        }
        [pscustomobject][ordered]@{
            path = $full
            bytes = [long]$stream.Length
            sha256 = $hash
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-BytesSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '')
    }
    finally {
        $sha.Dispose()
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

function Assert-NotReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][bool]$Directory,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $attributes = [IO.File]::GetAttributes($Path)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label is a reparse point: $Path"
    }
    $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
    if ($isDirectory -ne $Directory) {
        throw "$Label has the wrong object type: $Path"
    }
}

function Sort-RecordsByOrdinalPath {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Records
    )
    $sorted = [Collections.Generic.List[object]]::new()
    foreach ($record in @($Records)) { $sorted.Add($record) }
    $sorted.Sort([Comparison[object]]{
            param($left, $right)
            [StringComparer]::OrdinalIgnoreCase.Compare(
                [string]$left.path,
                [string]$right.path)
        })
    @($sorted)
}

function Get-BoundedToolchainSnapshot {
    $directories = [Collections.Generic.List[object]]::new()
    $files = [Collections.Generic.List[object]]::new()
    [long]$totalBytes = 0

    foreach ($fileRoot in $toolchainFileRoots) {
        $fullFile = [IO.Path]::GetFullPath($fileRoot)
        Assert-NotReparsePoint -Path $fullFile -Directory $false -Label 'toolchain file root'
        $record = Get-FileRecord -Path $fullFile
        $files.Add($record)
        $totalBytes += [long]$record.bytes
        if ($files.Count -gt $toolchainFileLimit -or
            ($files.Count + $directories.Count) -gt $toolchainEntryLimit -or
            $totalBytes -gt $toolchainByteLimit) {
            throw 'Frozen toolchain exceeds a built-in global cap.'
        }
    }

    foreach ($directoryRoot in $toolchainDirectoryRoots) {
        $fullRoot = [IO.Path]::GetFullPath($directoryRoot).TrimEnd('\')
        $pending = [Collections.Generic.Stack[object]]::new()
        $pending.Push([pscustomobject]@{ Path = $fullRoot; Depth = 0 })
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            $currentPath = [IO.Path]::GetFullPath([string]$current.Path).TrimEnd('\')
            $currentDepth = [int]$current.Depth
            if ($currentDepth -gt $toolchainDepthLimit) {
                throw "Toolchain directory depth exceeds $toolchainDepthLimit`: $currentPath"
            }
            Assert-NotReparsePoint -Path $currentPath -Directory $true -Label 'toolchain directory'
            $directories.Add([pscustomobject][ordered]@{ path = $currentPath })
            if ($directories.Count -gt $toolchainDirectoryLimit -or
                ($files.Count + $directories.Count) -gt $toolchainEntryLimit) {
                throw 'Frozen toolchain directory topology exceeds a built-in global cap.'
            }
            $entries = @(
                [IO.Directory]::EnumerateFileSystemEntries(
                    $currentPath,
                    '*',
                    [IO.SearchOption]::TopDirectoryOnly) |
                    Sort-Object -Descending)
            foreach ($entry in $entries) {
                $fullEntry = [IO.Path]::GetFullPath($entry)
                $attributes = [IO.File]::GetAttributes($fullEntry)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Toolchain topology contains a reparse point: $fullEntry"
                }
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    $pending.Push([pscustomobject]@{
                            Path = $fullEntry
                            Depth = $currentDepth + 1
                        })
                }
                else {
                    $record = Get-FileRecord -Path $fullEntry
                    $files.Add($record)
                    $totalBytes += [long]$record.bytes
                    if ($files.Count -gt $toolchainFileLimit -or
                        ($files.Count + $directories.Count) -gt $toolchainEntryLimit -or
                        $totalBytes -gt $toolchainByteLimit) {
                        throw (
                            'Frozen toolchain exceeds its built-in global cap: ' +
                            "$($files.Count) files / $totalBytes bytes.")
                    }
                }
            }
        }
    }

    $sortedDirectories = @(Sort-RecordsByOrdinalPath -Records @($directories))
    $sortedFiles = @(Sort-RecordsByOrdinalPath -Records @($files))
    [pscustomobject][ordered]@{
        schema = 1
        selectionSetId = $toolchainSelectionSetId
        fileRoots = @($toolchainFileRoots)
        directoryRoots = @($toolchainDirectoryRoots)
        limits = [pscustomobject][ordered]@{
            fileCount = $toolchainFileLimit
            directoryCount = $toolchainDirectoryLimit
            entryCount = $toolchainEntryLimit
            bytes = [long]$toolchainByteLimit
            depth = $toolchainDepthLimit
        }
        directories = $sortedDirectories
        files = $sortedFiles
        fileCount = $sortedFiles.Count
        directoryCount = $sortedDirectories.Count
        entryCount = $sortedFiles.Count + $sortedDirectories.Count
        bytes = $totalBytes
    }
}

function Assert-ToolchainSnapshotSchema {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $required = @(
        'schema', 'selectionSetId', 'fileRoots', 'directoryRoots',
        'limits', 'directories', 'files', 'fileCount', 'directoryCount',
        'entryCount', 'bytes')
    $actualTopLevel = @($Snapshot.PSObject.Properties.Name)
    if (($actualTopLevel -join "`n") -cne ($required -join "`n")) {
        throw "$Label top-level schema is not exact and ordered."
    }
    if ([int]$Snapshot.schema -ne 1 -or
        [string]$Snapshot.selectionSetId -cne $toolchainSelectionSetId) {
        throw "$Label selection set is not the built-in toolchain set."
    }
    if ((@($Snapshot.fileRoots) -join "`n") -cne
        (@($toolchainFileRoots) -join "`n") -or
        (@($Snapshot.directoryRoots) -join "`n") -cne
        (@($toolchainDirectoryRoots) -join "`n")) {
        throw "$Label toolchain roots do not match the built-in literal roots."
    }
    $requiredLimitProperties = @(
        'fileCount', 'directoryCount', 'entryCount', 'bytes', 'depth')
    if ((@($Snapshot.limits.PSObject.Properties.Name) -join "`n") -cne
        ($requiredLimitProperties -join "`n")) {
        throw "$Label limit schema is not exact and ordered."
    }
    $expectedLimits = (
        "$toolchainFileLimit|$toolchainDirectoryLimit|$toolchainEntryLimit|" +
        "$([long]$toolchainByteLimit)|$toolchainDepthLimit")
    $actualLimits = (
        "$([int]$Snapshot.limits.fileCount)|" +
        "$([int]$Snapshot.limits.directoryCount)|" +
        "$([int]$Snapshot.limits.entryCount)|" +
        "$([long]$Snapshot.limits.bytes)|" +
        "$([int]$Snapshot.limits.depth)")
    if ($actualLimits -cne $expectedLimits) {
        throw "$Label toolchain limits do not match the built-in limits."
    }
    $snapshotFiles = @($Snapshot.files)
    $snapshotDirectories = @($Snapshot.directories)
    Assert-FileRecordSchema -Records $snapshotFiles -Label "$Label files"
    $seenDirectories = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $directoryPaths = [Collections.Generic.List[string]]::new()
    foreach ($directory in $snapshotDirectories) {
        if ((@($directory.PSObject.Properties.Name) -join "`n") -cne 'path' -or
            $null -eq $directory.PSObject.Properties['path']) {
            throw "$Label contains an invalid directory record."
        }
        $directoryPath = [string]$directory.path
        if (-not [IO.Path]::IsPathRooted($directoryPath) -or
            $directoryPath -cne [IO.Path]::GetFullPath($directoryPath).TrimEnd('\')) {
            throw "$Label contains a non-canonical directory path."
        }
        if (-not $seenDirectories.Add($directoryPath)) {
            throw "$Label contains a duplicate directory path."
        }
        $withinBuiltInRoot = $false
        foreach ($directoryRoot in $toolchainDirectoryRoots) {
            $rootPath = [IO.Path]::GetFullPath($directoryRoot).TrimEnd('\')
            if ($directoryPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
                $withinBuiltInRoot = $true
                break
            }
            $prefix = $rootPath + '\'
            if ($directoryPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $relative = $directoryPath.Substring($prefix.Length)
                $depth = @($relative.Split(
                            [char[]]@('\'),
                            [StringSplitOptions]::RemoveEmptyEntries)).Count
                if ($depth -gt $toolchainDepthLimit) {
                    throw "$Label contains a directory beyond the built-in depth cap."
                }
                $withinBuiltInRoot = $true
                break
            }
        }
        if (-not $withinBuiltInRoot) {
            throw "$Label contains a directory outside the built-in roots."
        }
        $directoryPaths.Add($directoryPath)
    }
    $sortedDirectoryPaths = @($directoryPaths)
    [Array]::Sort($sortedDirectoryPaths, [StringComparer]::OrdinalIgnoreCase)
    if (($directoryPaths -join "`n") -cne ($sortedDirectoryPaths -join "`n")) {
        throw "$Label directory topology is not ordinally sorted."
    }
    $seenFiles = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $filePaths = [Collections.Generic.List[string]]::new()
    foreach ($file in $snapshotFiles) {
        $filePath = [string]$file.path
        if (-not $seenFiles.Add($filePath)) {
            throw "$Label contains a duplicate file path."
        }
        $withinBuiltInRoot = @($toolchainFileRoots) -icontains $filePath
        if (-not $withinBuiltInRoot) {
            foreach ($directoryRoot in $toolchainDirectoryRoots) {
                $prefix = [IO.Path]::GetFullPath($directoryRoot).TrimEnd('\') + '\'
                if ($filePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    $withinBuiltInRoot = $true
                    break
                }
            }
        }
        if (-not $withinBuiltInRoot) {
            throw "$Label contains a file outside the built-in roots."
        }
        $filePaths.Add($filePath)
    }
    $sortedFilePaths = @($filePaths)
    [Array]::Sort($sortedFilePaths, [StringComparer]::OrdinalIgnoreCase)
    if (($filePaths -join "`n") -cne ($sortedFilePaths -join "`n")) {
        throw "$Label files are not ordinally sorted."
    }
    if ([int]$Snapshot.fileCount -ne $snapshotFiles.Count -or
        [int]$Snapshot.fileCount -gt $toolchainFileLimit -or
        [int]$Snapshot.directoryCount -ne $snapshotDirectories.Count -or
        [int]$Snapshot.directoryCount -gt $toolchainDirectoryLimit -or
        [int]$Snapshot.entryCount -ne
        ($snapshotFiles.Count + $snapshotDirectories.Count) -or
        [int]$Snapshot.entryCount -gt $toolchainEntryLimit -or
        [long]$Snapshot.bytes -lt 0 -or
        [long]$Snapshot.bytes -gt $toolchainByteLimit -or
        [long]$Snapshot.bytes -ne
        (Get-FileRecordByteTotal -Records $snapshotFiles -Label "$Label files")) {
        throw "$Label aggregate file identity is inconsistent."
    }
}

function Assert-ToolchainSnapshotEqual {
    param(
        [Parameter(Mandatory = $true)][object]$Expected,
        [Parameter(Mandatory = $true)][object]$Actual,
        [Parameter(Mandatory = $true)][string]$Label
    )
    Assert-ToolchainSnapshotSchema -Snapshot $Expected -Label "$Label expected"
    Assert-ToolchainSnapshotSchema -Snapshot $Actual -Label "$Label actual"
    $expectedJson = $Expected | ConvertTo-Json -Depth 8 -Compress
    $actualJson = $Actual | ConvertTo-Json -Depth 8 -Compress
    if ($actualJson -cne $expectedJson) {
        throw "$Label topology or content identity changed."
    }
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
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Expected,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Actual,
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

function Assert-ExactOrdinalProfilePaths {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Expected,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Actual,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $actualOrdinalIgnoreCasePaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($actualPath in $Actual) {
        if (-not $actualOrdinalIgnoreCasePaths.Add($actualPath)) {
            throw "$Label contains a case-collision path: $actualPath"
        }
    }
    [Array]::Sort($Expected, [StringComparer]::Ordinal)
    [Array]::Sort($Actual, [StringComparer]::Ordinal)
    if ($Actual.Count -ne $Expected.Count) {
        throw "$Label raw path count mismatch: $($Expected.Count) / $($Actual.Count)"
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if (-not $Actual[$index].Equals(
                $Expected[$index],
                [StringComparison]::Ordinal)) {
            throw "$Label raw ordinal path mismatch: $($Expected[$index]) / $($Actual[$index])"
        }
    }
}

function Get-UniqueApprovedInputRecords {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Records
    )
    Assert-FileRecordSchema -Records $Records -Label 'approved input lease'
    $byPath = [Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    [long]$totalBytes = 0
    foreach ($record in @($Records)) {
        $path = [IO.Path]::GetFullPath([string]$record.path)
        if ($byPath.ContainsKey($path)) {
            $prior = $byPath[$path]
            if ([long]$prior.bytes -ne [long]$record.bytes -or
                [string]$prior.sha256 -cne [string]$record.sha256) {
                throw "Approved input evidence conflicts for one path: $path"
            }
            continue
        }
        $byPath.Add($path, $record)
        $totalBytes += [long]$record.bytes
        if ($byPath.Count -gt $approvedInputFileLimit -or
            $totalBytes -gt $approvedInputByteLimit) {
            throw 'Approved input files exceed a built-in lifetime-lease cap.'
        }
    }
    @(Sort-RecordsByOrdinalPath -Records @($byPath.Values))
}

function Get-ApprovedInputDirectorySets {
    param(
        [Parameter(Mandatory = $true)][object[]]$SourceRecords,
        [Parameter(Mandatory = $true)][object[]]$FeedRecords,
        [Parameter(Mandatory = $true)][object[]]$FixedRecords,
        [Parameter(Mandatory = $true)][object]$Toolchain
    )
    $immutable = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $stable = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $feedRoot = [IO.Path]::GetFullPath($feed).TrimEnd('\')
    [void]$immutable.Add($feedRoot)
    foreach ($record in @($FeedRecords)) {
        $current = [IO.Path]::GetFullPath(
            [IO.Path]::GetDirectoryName([string]$record.path)).TrimEnd('\')
        while ($current.StartsWith(
                $feedRoot + '\',
                [StringComparison]::OrdinalIgnoreCase)) {
            [void]$immutable.Add($current)
            $current = [IO.Path]::GetFullPath(
                [IO.Path]::GetDirectoryName($current)).TrimEnd('\')
        }
    }
    foreach ($directory in @($Toolchain.directories)) {
        [void]$immutable.Add(
            [IO.Path]::GetFullPath([string]$directory.path).TrimEnd('\'))
    }
    foreach ($record in @($SourceRecords) + @($FixedRecords)) {
        $parent = [IO.Path]::GetFullPath(
            [IO.Path]::GetDirectoryName([string]$record.path)).TrimEnd('\')
        if (-not $immutable.Contains($parent)) {
            [void]$stable.Add($parent)
        }
    }
    if (($immutable.Count + $stable.Count) -gt $approvedInputDirectoryLimit) {
        throw 'Approved input directories exceed the built-in lifetime-lease cap.'
    }
    $immutableRecords = @($immutable | ForEach-Object {
            [pscustomobject][ordered]@{ path = [string]$_ }
        })
    $stableRecords = @($stable | ForEach-Object {
            [pscustomobject][ordered]@{ path = [string]$_ }
        })
    [pscustomobject][ordered]@{
        immutable = @(
            Sort-RecordsByOrdinalPath -Records $immutableRecords |
                ForEach-Object { [string]$_.path })
        stable = @(
            Sort-RecordsByOrdinalPath -Records $stableRecords |
                ForEach-Object { [string]$_.path })
    }
}

function Acquire-ApprovedInputLease {
    param(
        [Parameter(Mandatory = $true)][object[]]$SourceRecords,
        [Parameter(Mandatory = $true)][object[]]$FeedRecords,
        [Parameter(Mandatory = $true)][object[]]$FixedRecords,
        [Parameter(Mandatory = $true)][object]$Toolchain
    )
    Assert-ToolchainSnapshotSchema -Snapshot $Toolchain -Label 'input lease toolchain'
    $records = @(Get-UniqueApprovedInputRecords -Records @(
            @($SourceRecords) +
            @($FeedRecords) +
            @($FixedRecords) +
            @($Toolchain.files)))
    $directories = Get-ApprovedInputDirectorySets `
        -SourceRecords $SourceRecords `
        -FeedRecords $FeedRecords `
        -FixedRecords $FixedRecords `
        -Toolchain $Toolchain
    [string[]]$filePaths = @($records | ForEach-Object { [string]$_.path })
    [long[]]$expectedBytes = @($records | ForEach-Object { [long]$_.bytes })
    [string[]]$expectedSha256 = @($records | ForEach-Object { [string]$_.sha256 })
    [Rc48ApprovedInputLease]::Acquire(
        [string[]]@($directories.immutable),
        [string[]]@($directories.stable),
        $filePaths,
        $expectedBytes,
        $expectedSha256)
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
    param([switch]$AllowPostRestoreNuGetArtifacts)

    if ($AllowPostRestoreNuGetArtifacts) {
        $expectedProfileDirectories = @(
            [IO.Path]::GetFullPath((Join-Path $isolatedUserProfile 'AppData')),
            [IO.Path]::GetFullPath($isolatedLocalAppData),
            [IO.Path]::GetFullPath((Join-Path $isolatedLocalAppData 'NuGet')),
            [IO.Path]::GetFullPath((Join-Path $isolatedLocalAppData 'NuGet\Migrations')),
            [IO.Path]::GetFullPath($isolatedRoamingAppData),
            [IO.Path]::GetFullPath((Join-Path $isolatedRoamingAppData 'NuGet'))
        ) | Sort-Object
        $expectedProfileFiles = @(
            [pscustomobject][ordered]@{
                path = [IO.Path]::GetFullPath((Join-Path $isolatedLocalAppData 'NuGet\Migrations\1'))
                bytes = [long]0
                sha256 = 'E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855'
            },
            [pscustomobject][ordered]@{
                path = [IO.Path]::GetFullPath((Join-Path $isolatedRoamingAppData 'NuGet\NuGet.Config'))
                bytes = [long]210
                sha256 = '3BDB050540ECDD6FE717CBB9028C0FF9E2AF5E1544AFA71B6BADE80140C7A236'
            }
        )
    }
    else {
        $expectedProfileDirectories = @(
            [IO.Path]::GetFullPath((Join-Path $isolatedUserProfile 'AppData')),
            [IO.Path]::GetFullPath($isolatedLocalAppData),
            [IO.Path]::GetFullPath($isolatedRoamingAppData)
        ) | Sort-Object
        $expectedProfileFiles = @()
    }

    $actualProfileDirectories = [Collections.Generic.List[string]]::new()
    $actualProfileFiles = [Collections.Generic.List[object]]::new()
    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push([IO.Path]::GetFullPath($isolatedUserProfile))
    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Pop()
        Assert-NotReparsePoint `
            -Path $currentDirectory `
            -Directory $true `
            -Label 'isolated USERPROFILE directory'
        foreach ($entry in [IO.DirectoryInfo]::new($currentDirectory).GetFileSystemInfos()) {
            $fullEntry = [IO.Path]::GetFullPath($entry.FullName)
            $attributes = [IO.File]::GetAttributes($fullEntry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Isolated USERPROFILE contains a reparse point: $fullEntry"
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $actualProfileDirectories.Add($fullEntry)
                $pendingDirectories.Push($fullEntry)
            }
            else {
                Assert-NotReparsePoint `
                    -Path $fullEntry `
                    -Directory $false `
                    -Label 'isolated USERPROFILE file'
                $actualProfileFiles.Add((Get-FileRecord -Path $fullEntry))
            }
        }
    }
    Assert-ExactOrdinalProfilePaths `
        -Expected ([string[]]$expectedProfileDirectories) `
        -Actual ([string[]]@($actualProfileDirectories)) `
        -Label 'isolated USERPROFILE directory paths'
    $expectedProfileFilePaths = [string[]]@(
        $expectedProfileFiles | ForEach-Object { [string]$_.path })
    $actualProfileFilePaths = [string[]]@(
        $actualProfileFiles | ForEach-Object { [string]$_.path })
    Assert-ExactOrdinalProfilePaths `
        -Expected $expectedProfileFilePaths `
        -Actual $actualProfileFilePaths `
        -Label 'isolated USERPROFILE file paths'
    Assert-RecordSetEqual `
        -Expected $expectedProfileFiles `
        -Actual @($actualProfileFiles) `
        -Label 'isolated USERPROFILE'
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

function Get-ExecutionIdentityDefinition {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($null -eq $identity.User -or
            [String]::IsNullOrWhiteSpace($identity.User.Value) -or
            [String]::IsNullOrWhiteSpace($identity.Name)) {
            throw 'The current Windows identity is incomplete.'
        }
        [ordered]@{
            sid = $identity.User.Value
            name = $identity.Name
            authenticationType = [string]$identity.AuthenticationType
            isSystem = [bool]$identity.IsSystem
        }
    }
    finally {
        $identity.Dispose()
    }
}

function ConvertTo-NativeEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.IDictionary]$Environment,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedNames
    )

    if ($Environment.Count -ne 34 -or $ExpectedNames.Count -ne 34) {
        throw "Native environment entry count is not exactly 34: $($Environment.Count)."
    }
    $expectedOrdinal = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $expectedIgnoreCase = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $ExpectedNames) {
        if ([String]::IsNullOrWhiteSpace($name) -or
            $name.Contains('=') -or
            $name.Contains([char]0) -or
            -not $expectedOrdinal.Add($name) -or
            -not $expectedIgnoreCase.Add($name)) {
            throw "Expected native environment name is invalid or duplicated: $name"
        }
    }

    $native = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $seenOrdinal = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($entry in $Environment.GetEnumerator()) {
        $name = [string]$entry.Key
        $value = [string]$entry.Value
        if ([String]::IsNullOrWhiteSpace($name) -or
            $name.Contains('=') -or
            $name.Contains([char]0) -or
            -not $expectedOrdinal.Contains($name)) {
            throw "Native environment name is invalid, unexpected, or has wrong casing: $name"
        }
        if ([String]::IsNullOrEmpty($value) -or $value.Contains([char]0)) {
            throw "Native environment value is empty or contains NUL: $name"
        }
        if (-not $seenOrdinal.Add($name) -or $native.ContainsKey($name)) {
            throw "Native environment name is duplicated: $name"
        }
        $native.Add($name, $value)
    }
    if ($native.Count -ne 34) {
        throw "Native environment conversion did not preserve exactly 34 entries: $($native.Count)."
    }
    foreach ($name in $ExpectedNames) {
        if (-not $native.ContainsKey($name)) {
            throw "Native environment conversion omitted approved entry: $name"
        }
    }
    Write-Output -NoEnumerate $native
}

function New-NativeEnvironmentBlock {
    param(
        [Parameter(Mandatory = $true)]
        [Collections.Generic.IDictionary[string, string]]$Environment
    )

    if ($Environment.Count -ne 34) {
        throw "Native environment block source count is not exactly 34: $($Environment.Count)."
    }
    [string[]]$names = @($Environment.Keys)
    [Array]::Sort($names, [StringComparer]::OrdinalIgnoreCase)
    $builder = [Text.StringBuilder]::new()
    foreach ($name in $names) {
        [void]$builder.Append($name)
        [void]$builder.Append('=')
        [void]$builder.Append($Environment[$name])
        [void]$builder.Append([char]0)
    }
    [void]$builder.Append([char]0)
    $text = $builder.ToString()
    if ($text.Length -gt 32767) {
        throw "Native Unicode environment block is too large: $($text.Length) characters."
    }
    [byte[]]$bytes = [Text.Encoding]::Unicode.GetBytes($text)
    if ($bytes.Length -lt 4 -or
        $bytes[$bytes.Length - 1] -ne 0 -or
        $bytes[$bytes.Length - 2] -ne 0 -or
        $bytes[$bytes.Length - 3] -ne 0 -or
        $bytes[$bytes.Length - 4] -ne 0) {
        throw 'Native Unicode environment block is not double-NUL terminated.'
    }

    $segments = $text.Split(
        [char[]]@([char]0),
        [StringSplitOptions]::None)
    if ($segments.Count -ne ($Environment.Count + 2) -or
        $segments[$segments.Count - 1] -cne '' -or
        $segments[$segments.Count - 2] -cne '') {
        throw 'Native Unicode environment block segment count is invalid.'
    }
    $roundTrip = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    for ($index = 0; $index -lt $Environment.Count; $index++) {
        $segment = $segments[$index]
        $separator = $segment.IndexOf('=')
        if ($separator -le 0) {
            throw 'Native Unicode environment block contains a malformed entry.'
        }
        $name = $segment.Substring(0, $separator)
        $value = $segment.Substring($separator + 1)
        if ($roundTrip.ContainsKey($name)) {
            throw "Native Unicode environment block contains a duplicate entry: $name"
        }
        $roundTrip.Add($name, $value)
    }
    if ($roundTrip.Count -ne $Environment.Count) {
        throw 'Native Unicode environment block round-trip count mismatch.'
    }
    foreach ($pair in $Environment.GetEnumerator()) {
        if (-not $roundTrip.ContainsKey($pair.Key) -or
            $roundTrip[$pair.Key] -cne $pair.Value) {
            throw "Native Unicode environment block round-trip mismatch: $($pair.Key)"
        }
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
    [pscustomobject][ordered]@{
        bytes = $bytes
        sha256 = $hash
        entryCount = $Environment.Count
        charCount = $text.Length
        unicodeByteCount = $bytes.Length
    }
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
        "-property:RestorePackagesPath=$r3",
        "-property:RestoreSources=$feed",
        "-property:NUGET_PACKAGES=$r3",
        "-property:MSBuildSDKsPath=$sdkPath",
        "-property:MSBuildExtensionsPath=$msbuildExtensionsPath",
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
        '-property:DisableImplicitNuGetFallbackFolder=true',
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
        DOTNET_ROLL_FORWARD = 'Disable'
        DOTNET_CLI_HOME = $cliHome
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        DOTNET_NOLOGO = '1'
        TEMP = $temp
        TMP = $temp
        NUGET_PACKAGES = $r3
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

$evidenceRootLeaseSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

public sealed class Rc48EvidenceDirectoryLease : SafeHandle
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint CREATE_NEW = 1;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileAttributeTagInfo = 9;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
    private readonly string expectedPath;
    private readonly object sync = new object();
    private readonly List<HeldObject> heldObjects = new List<HeldObject>();
    private readonly HashSet<string> anchoredDirectories =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileIdentity> anchoredOutputs =
        new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);

    private Rc48EvidenceDirectoryLease(IntPtr openedHandle, string expectedPath)
        : base(IntPtr.Zero, true)
    {
        this.expectedPath = NormalizeFullPath(expectedPath);
        anchoredDirectories.Add(this.expectedPath);
        SetHandle(openedHandle);
    }

    public override bool IsInvalid
    {
        get { return handle == IntPtr.Zero || handle == InvalidHandleValue; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed class HeldObject
    {
        internal readonly IntPtr Handle;
        internal readonly string Path;
        internal readonly bool IsDirectory;

        internal HeldObject(IntPtr handle, string path, bool isDirectory)
        {
            Handle = handle;
            Path = path;
            IsDirectory = isDirectory;
        }
    }

    private struct FileIdentity
    {
        internal uint VolumeSerialNumber;
        internal ulong FileIndex;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(
        string pathName,
        IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr handle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        IntPtr file,
        int fileInformationClass,
        out FILE_ATTRIBUTE_TAG_INFO fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        IntPtr file,
        out BY_HANDLE_FILE_INFORMATION fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(IntPtr file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(IntPtr file, out long fileSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path.Substring(4);
        return path;
    }

    private static string NormalizeFullPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static string GetFinalPath(IntPtr current, string context)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(
            current,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " final-path resolution failed.");
        return Path.GetFullPath(NormalizeFinalPath(buffer.ToString()));
    }

    private static void AssertExactFinalPath(
        IntPtr current,
        string expectedPath,
        string context)
    {
        string expected = Path.GetFullPath(expectedPath);
        string actual = GetFinalPath(current, context);
        if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                context + " resolved outside its exact approved path.");
    }

    private static void AssertObjectKind(
        IntPtr current,
        bool expectDirectory,
        string context)
    {
        FILE_ATTRIBUTE_TAG_INFO information;
        if (!GetFileInformationByHandleEx(
                current,
                FileAttributeTagInfo,
                out information,
                (uint)Marshal.SizeOf(typeof(FILE_ATTRIBUTE_TAG_INFO))))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " attribute-tag query failed.");
        if ((information.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            throw new InvalidOperationException(
                context + " is a reparse point.");
        bool isDirectory =
            (information.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        if (isDirectory != expectDirectory)
            throw new InvalidOperationException(
                context + " has the wrong object type.");
        if (!expectDirectory)
        {
            BY_HANDLE_FILE_INFORMATION fileInformation;
            if (!GetFileInformationByHandle(current, out fileInformation))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    context + " link-count query failed.");
            if (fileInformation.NumberOfLinks != 1)
                throw new InvalidOperationException(
                    context + " is not a single-link evidence file.");
        }
    }

    private static FileIdentity GetIdentity(IntPtr current, string context)
    {
        BY_HANDLE_FILE_INFORMATION information;
        if (!GetFileInformationByHandle(current, out information))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " object-identity query failed.");
        return new FileIdentity
        {
            VolumeSerialNumber = information.VolumeSerialNumber,
            FileIndex =
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow
        };
    }

    private static bool SameIdentity(FileIdentity left, FileIdentity right)
    {
        return
            left.VolumeSerialNumber == right.VolumeSerialNumber &&
            left.FileIndex == right.FileIndex;
    }

    private static IntPtr OpenExact(
        string full,
        uint desiredAccess,
        uint shareMode,
        uint creationDisposition,
        uint flags,
        bool expectDirectory,
        string context)
    {
        var attributes = new SECURITY_ATTRIBUTES();
        attributes.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
        IntPtr openedHandle = CreateFile(
            full,
            desiredAccess,
            shareMode,
            ref attributes,
            creationDisposition,
            flags | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        if (openedHandle == InvalidHandleValue)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " could not be opened exactly.");
        try
        {
            AssertObjectKind(openedHandle, expectDirectory, context);
            AssertExactFinalPath(openedHandle, full, context);
            return openedHandle;
        }
        catch
        {
            CloseHandle(openedHandle);
            throw;
        }
    }

    public static Rc48EvidenceDirectoryLease Acquire(string evidenceDirectory)
    {
        string full = NormalizeFullPath(evidenceDirectory);
        IntPtr openedHandle = OpenExact(
            full,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            true,
            "The evidence directory");
        try
        {
            return new Rc48EvidenceDirectoryLease(openedHandle, full);
        }
        catch
        {
            CloseHandle(openedHandle);
            throw;
        }
    }

    public static Rc48EvidenceDirectoryLease CreateNew(string evidenceDirectory)
    {
        string full = NormalizeFullPath(evidenceDirectory);
        if (!CreateDirectory(full, IntPtr.Zero))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The unique evidence directory could not be created.");
        return Acquire(full);
    }

    private string RequireContainedPath(string path, string context)
    {
        string full = NormalizeFullPath(path);
        string prefix = expectedPath + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                context + " escaped the exact evidence root.");
        return full;
    }

    private void RequireAnchoredParent(string full, string context)
    {
        string parent = NormalizeFullPath(Path.GetDirectoryName(full));
        if (!anchoredDirectories.Contains(parent))
            throw new InvalidOperationException(
                context + " parent directory is not held by the evidence lease.");
    }

    private void EnsureUsable()
    {
        if (IsClosed || IsInvalid)
            throw new ObjectDisposedException("Rc48EvidenceDirectoryLease");
    }

    public void AnchorDirectories(string[] requiredDirectories)
    {
        if (requiredDirectories == null)
            throw new ArgumentNullException("requiredDirectories");
        lock (sync)
        {
            EnsureUsable();
            foreach (string requested in requiredDirectories)
            {
                string full = RequireContainedPath(
                    requested,
                    "An evidence child directory");
                if (anchoredDirectories.Contains(full))
                    throw new InvalidOperationException(
                        "An evidence child directory was duplicated.");
                RequireAnchoredParent(full, "An evidence child directory");
                IntPtr child = OpenExact(
                    full,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS,
                    true,
                    "An evidence child directory");
                heldObjects.Add(new HeldObject(child, full, true));
                anchoredDirectories.Add(full);
            }
        }
    }

    public byte[] ReadExactFileBytes(
        string path,
        string requiredFileName,
        int maximumBytes)
    {
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException("maximumBytes");
        lock (sync)
        {
            EnsureUsable();
            string full = RequireContainedPath(path, "Approved evidence input");
            if (!String.Equals(
                    Path.GetFileName(full),
                    requiredFileName,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Approved evidence input has the wrong exact file name.");
            RequireAnchoredParent(full, "Approved evidence input");
            IntPtr input = OpenExact(
                full,
                GENERIC_READ,
                FILE_SHARE_READ,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                false,
                "Approved evidence input");
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(
                    new SafeFileHandle(input, false),
                    FileAccess.Read,
                    4096,
                    false))
                {
                    if (stream.Length < 1 || stream.Length > maximumBytes)
                        throw new InvalidOperationException(
                            "Approved evidence input has an invalid byte length.");
                    bytes = new byte[(int)stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0)
                            throw new EndOfStreamException(
                                "Approved evidence input ended unexpectedly.");
                        offset += read;
                    }
                    if (stream.ReadByte() != -1)
                        throw new InvalidOperationException(
                            "Approved evidence input grew while being read.");
                }
                heldObjects.Add(new HeldObject(input, full, false));
                input = IntPtr.Zero;
                return bytes;
            }
            finally
            {
                if (input != IntPtr.Zero)
                    CloseHandle(input);
            }
        }
    }

    public void WriteNewFile(
        string path,
        string requiredFileName,
        byte[] payload)
    {
        if (payload == null)
            throw new ArgumentNullException("payload");
        lock (sync)
        {
            EnsureUsable();
            string full = RequireContainedPath(path, "New evidence output");
            if (!String.Equals(
                    Path.GetFileName(full),
                    requiredFileName,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "New evidence output has the wrong exact file name.");
            RequireAnchoredParent(full, "New evidence output");
            IntPtr output = OpenExact(
                full,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ,
                CREATE_NEW,
                FILE_ATTRIBUTE_NORMAL,
                false,
                "New evidence output");
            try
            {
                using (var stream = new FileStream(
                    new SafeFileHandle(output, false),
                    FileAccess.Write,
                    4096,
                    false))
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                heldObjects.Add(new HeldObject(output, full, false));
                output = IntPtr.Zero;
            }
            finally
            {
                if (output != IntPtr.Zero)
                    CloseHandle(output);
            }
        }
    }

    public void CreateAnchoredOutput(string path, string requiredFileName)
    {
        lock (sync)
        {
            EnsureUsable();
            string full = RequireContainedPath(path, "Child-owned evidence output");
            if (!String.Equals(
                    Path.GetFileName(full),
                    requiredFileName,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Child-owned evidence output has the wrong exact file name.");
            RequireAnchoredParent(full, "Child-owned evidence output");
            if (anchoredOutputs.ContainsKey(full))
                throw new InvalidOperationException(
                    "Child-owned evidence output was anchored twice.");
            IntPtr output = OpenExact(
                full,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                CREATE_NEW,
                FILE_ATTRIBUTE_NORMAL,
                false,
                "Child-owned evidence output");
            FileIdentity identity = GetIdentity(
                output,
                "Child-owned evidence output");
            heldObjects.Add(new HeldObject(output, full, false));
            anchoredOutputs.Add(full, identity);
        }
    }

    public long FlushAnchoredOutput(string path, string requiredFileName)
    {
        lock (sync)
        {
            EnsureUsable();
            string full = RequireContainedPath(path, "Child-owned evidence output");
            if (!String.Equals(
                    Path.GetFileName(full),
                    requiredFileName,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Child-owned evidence output has the wrong exact file name.");
            FileIdentity expectedIdentity;
            if (!anchoredOutputs.TryGetValue(full, out expectedIdentity))
                throw new InvalidOperationException(
                    "Child-owned evidence output is not anchored.");
            IntPtr output = OpenExact(
                full,
                GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                false,
                "Child-owned evidence output");
            try
            {
                FileIdentity actualIdentity = GetIdentity(
                    output,
                    "Child-owned evidence output");
                if (!SameIdentity(expectedIdentity, actualIdentity))
                    throw new InvalidOperationException(
                        "Child-owned evidence output changed file identity.");
                if (!FlushFileBuffers(output))
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Child-owned evidence output could not be durably flushed.");
                long size;
                if (!GetFileSizeEx(output, out size))
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Child-owned evidence output size could not be read.");
                return size;
            }
            finally
            {
                CloseHandle(output);
            }
        }
    }

    public void Validate()
    {
        lock (sync)
        {
            EnsureUsable();
            AssertExactFinalPath(
                DangerousGetHandle(),
                expectedPath,
                "The evidence directory lease");
            AssertObjectKind(
                DangerousGetHandle(),
                true,
                "The evidence directory lease");
            foreach (HeldObject current in heldObjects)
            {
                AssertExactFinalPath(
                    current.Handle,
                    current.Path,
                    "A held evidence child");
                AssertObjectKind(
                    current.Handle,
                    current.IsDirectory,
                    "A held evidence child");
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        bool success = true;
        for (int index = heldObjects.Count - 1; index >= 0; index--)
        {
            IntPtr current = heldObjects[index].Handle;
            if (current != IntPtr.Zero &&
                current != InvalidHandleValue &&
                !CloseHandle(current))
                success = false;
        }
        heldObjects.Clear();
        anchoredOutputs.Clear();
        anchoredDirectories.Clear();
        if (!CloseHandle(handle))
            success = false;
        return success;
    }
}

public sealed class Rc48ApprovedInputLease : SafeHandle
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileAttributeTagInfo = 9;
    private const int MaximumFiles = 20000;
    private const int MaximumDirectories = 10000;
    private const long MaximumBytes = 2147483648L;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
    private readonly object sync = new object();
    private readonly List<HeldInput> heldInputs = new List<HeldInput>();

    private Rc48ApprovedInputLease() : base(IntPtr.Zero, true)
    {
        // This SafeHandle owns a bounded set of native handles rather than one
        // kernel object. A private nonzero sentinel keeps ReleaseHandle reliable.
        SetHandle(new IntPtr(1));
    }

    public override bool IsInvalid
    {
        get { return handle == IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed class HeldInput
    {
        internal readonly IntPtr Handle;
        internal readonly string Path;
        internal readonly bool IsDirectory;
        internal readonly long ExpectedBytes;
        internal readonly string ExpectedSha256;
        internal readonly uint VolumeSerialNumber;
        internal readonly ulong FileIndex;

        internal HeldInput(
            IntPtr handle,
            string path,
            bool isDirectory,
            long expectedBytes,
            string expectedSha256,
            uint volumeSerialNumber,
            ulong fileIndex)
        {
            Handle = handle;
            Path = path;
            IsDirectory = isDirectory;
            ExpectedBytes = expectedBytes;
            ExpectedSha256 = expectedSha256;
            VolumeSerialNumber = volumeSerialNumber;
            FileIndex = fileIndex;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr handle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        IntPtr file,
        int fileInformationClass,
        out FILE_ATTRIBUTE_TAG_INFO fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        IntPtr file,
        out BY_HANDLE_FILE_INFORMATION fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path.Substring(4);
        return path;
    }

    private static string NormalizePath(string path, bool directory)
    {
        string full = Path.GetFullPath(path);
        return directory
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    private static string GetFinalPath(IntPtr current, string context)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(
            current,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " final-path resolution failed.");
        return Path.GetFullPath(NormalizeFinalPath(buffer.ToString()));
    }

    private static void GetIdentity(
        IntPtr current,
        string context,
        out uint volumeSerialNumber,
        out ulong fileIndex)
    {
        BY_HANDLE_FILE_INFORMATION information;
        if (!GetFileInformationByHandle(current, out information))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " object-identity query failed.");
        volumeSerialNumber = information.VolumeSerialNumber;
        fileIndex =
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow;
        if (fileIndex == 0)
            throw new InvalidOperationException(
                context + " has an invalid object identity.");
    }

    private static void AssertExactObject(
        IntPtr current,
        string expectedPath,
        bool expectDirectory,
        string context,
        out uint volumeSerialNumber,
        out ulong fileIndex)
    {
        FILE_ATTRIBUTE_TAG_INFO tag;
        if (!GetFileInformationByHandleEx(
                current,
                FileAttributeTagInfo,
                out tag,
                (uint)Marshal.SizeOf(typeof(FILE_ATTRIBUTE_TAG_INFO))))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " attribute-tag query failed.");
        if ((tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            throw new InvalidOperationException(context + " is a reparse point.");
        bool isDirectory =
            (tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        if (isDirectory != expectDirectory)
            throw new InvalidOperationException(
                context + " has the wrong object type.");
        string finalPath = GetFinalPath(current, context);
        if (!String.Equals(
                finalPath,
                NormalizePath(expectedPath, expectDirectory),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                context + " resolved outside its exact approved path.");
        GetIdentity(
            current,
            context,
            out volumeSerialNumber,
            out fileIndex);
    }

    private static IntPtr OpenExact(
        string path,
        bool directory,
        bool immutableDirectory,
        string context)
    {
        string full = NormalizePath(path, directory);
        var attributes = new SECURITY_ATTRIBUTES();
        attributes.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
        uint shareMode = directory && !immutableDirectory
            ? FILE_SHARE_READ | FILE_SHARE_WRITE
            : FILE_SHARE_READ;
        uint flags = directory
            ? FILE_FLAG_BACKUP_SEMANTICS
            : FILE_ATTRIBUTE_NORMAL;
        IntPtr current = CreateFile(
            full,
            GENERIC_READ,
            shareMode,
            ref attributes,
            OPEN_EXISTING,
            flags | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        if (current == InvalidHandleValue)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " could not be opened with the approved share policy.");
        return current;
    }

    private static string ComputeSha256(IntPtr current, string context, out long bytes)
    {
        using (var nonOwning = new SafeFileHandle(current, false))
        using (var stream = new FileStream(
            nonOwning,
            FileAccess.Read,
            131072,
            false))
        using (var sha = SHA256.Create())
        {
            stream.Position = 0;
            bytes = stream.Length;
            string hash = BitConverter.ToString(
                sha.ComputeHash(stream)).Replace("-", "");
            if (stream.Position != stream.Length)
                throw new EndOfStreamException(
                    context + " did not hash the complete held object.");
            return hash;
        }
    }

    private static void RequireStrictlySortedUnique(
        string[] paths,
        string context)
    {
        for (int index = 0; index < paths.Length; index++)
        {
            if (String.IsNullOrWhiteSpace(paths[index]))
                throw new InvalidOperationException(
                    context + " contains an empty path.");
            if (index > 0 &&
                StringComparer.OrdinalIgnoreCase.Compare(
                    paths[index - 1],
                    paths[index]) >= 0)
                throw new InvalidOperationException(
                    context + " is not strictly ordinally sorted and unique.");
        }
    }

    private void HoldDirectory(string path, bool immutable)
    {
        string full = NormalizePath(path, true);
        IntPtr current = OpenExact(
            full,
            true,
            immutable,
            immutable ? "An immutable approved directory" : "A stable approved directory");
        try
        {
            uint volume;
            ulong fileIndex;
            AssertExactObject(
                current,
                full,
                true,
                immutable ? "An immutable approved directory" : "A stable approved directory",
                out volume,
                out fileIndex);
            heldInputs.Add(new HeldInput(
                current,
                full,
                true,
                0,
                null,
                volume,
                fileIndex));
            current = IntPtr.Zero;
        }
        finally
        {
            if (current != IntPtr.Zero)
                CloseHandle(current);
        }
    }

    private void HoldFile(
        string path,
        long expectedBytes,
        string expectedSha256)
    {
        string full = NormalizePath(path, false);
        IntPtr current = OpenExact(
            full,
            false,
            true,
            "An approved input file");
        try
        {
            uint volume;
            ulong fileIndex;
            AssertExactObject(
                current,
                full,
                false,
                "An approved input file",
                out volume,
                out fileIndex);
            long actualBytes;
            string actualSha256 = ComputeSha256(
                current,
                "An approved input file",
                out actualBytes);
            if (actualBytes != expectedBytes ||
                !String.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "An approved input file changed before its lifetime lease was acquired.");
            heldInputs.Add(new HeldInput(
                current,
                full,
                false,
                expectedBytes,
                expectedSha256,
                volume,
                fileIndex));
            current = IntPtr.Zero;
        }
        finally
        {
            if (current != IntPtr.Zero)
                CloseHandle(current);
        }
    }

    public static Rc48ApprovedInputLease Acquire(
        string[] immutableDirectoryPaths,
        string[] stableDirectoryPaths,
        string[] filePaths,
        long[] expectedBytes,
        string[] expectedSha256)
    {
        if (immutableDirectoryPaths == null ||
            stableDirectoryPaths == null ||
            filePaths == null ||
            expectedBytes == null ||
            expectedSha256 == null)
            throw new ArgumentNullException(
                "Approved input lease arrays cannot be null.");
        if (filePaths.Length != expectedBytes.Length ||
            filePaths.Length != expectedSha256.Length)
            throw new InvalidOperationException(
                "Approved input lease file evidence is not parallel.");
        if (filePaths.Length > MaximumFiles ||
            immutableDirectoryPaths.Length + stableDirectoryPaths.Length >
            MaximumDirectories)
            throw new InvalidOperationException(
                "Approved input lease exceeds its built-in handle cap.");
        RequireStrictlySortedUnique(
            immutableDirectoryPaths,
            "Immutable approved directories");
        RequireStrictlySortedUnique(
            stableDirectoryPaths,
            "Stable approved directories");
        RequireStrictlySortedUnique(filePaths, "Approved input files");
        var directorySet = new HashSet<string>(
            immutableDirectoryPaths,
            StringComparer.OrdinalIgnoreCase);
        foreach (string stable in stableDirectoryPaths)
        {
            if (!directorySet.Add(stable))
                throw new InvalidOperationException(
                    "Approved input directory classes overlap.");
        }
        long totalBytes = 0;
        for (int index = 0; index < filePaths.Length; index++)
        {
            if (expectedBytes[index] < 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(
                    expectedSha256[index] ?? String.Empty,
                    "^[A-F0-9]{64}$"))
                throw new InvalidOperationException(
                    "Approved input file evidence is malformed.");
            checked { totalBytes += expectedBytes[index]; }
            if (totalBytes > MaximumBytes)
                throw new InvalidOperationException(
                    "Approved input lease exceeds its built-in byte cap.");
        }

        var lease = new Rc48ApprovedInputLease();
        try
        {
            foreach (string path in immutableDirectoryPaths)
                lease.HoldDirectory(path, true);
            foreach (string path in stableDirectoryPaths)
                lease.HoldDirectory(path, false);
            for (int index = 0; index < filePaths.Length; index++)
                lease.HoldFile(
                    filePaths[index],
                    expectedBytes[index],
                    expectedSha256[index]);
            lease.Validate();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void EnsureUsable()
    {
        if (IsClosed || IsInvalid)
            throw new ObjectDisposedException("Rc48ApprovedInputLease");
    }

    public void Validate()
    {
        lock (sync)
        {
            EnsureUsable();
            foreach (HeldInput input in heldInputs)
            {
                uint volume;
                ulong fileIndex;
                AssertExactObject(
                    input.Handle,
                    input.Path,
                    input.IsDirectory,
                    "A held approved input",
                    out volume,
                    out fileIndex);
                if (volume != input.VolumeSerialNumber ||
                    fileIndex != input.FileIndex)
                    throw new InvalidOperationException(
                        "A held approved input changed object identity.");
                if (!input.IsDirectory)
                {
                    long bytes;
                    string sha256 = ComputeSha256(
                        input.Handle,
                        "A held approved input",
                        out bytes);
                    if (bytes != input.ExpectedBytes ||
                        !String.Equals(
                            sha256,
                            input.ExpectedSha256,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "A held approved input changed bytes.");
                }
            }
        }
    }

    protected override bool ReleaseHandle()
    {
        bool success = true;
        for (int index = heldInputs.Count - 1; index >= 0; index--)
        {
            IntPtr current = heldInputs[index].Handle;
            if (current != IntPtr.Zero &&
                current != InvalidHandleValue &&
                !CloseHandle(current))
                success = false;
        }
        heldInputs.Clear();
        return success;
    }
}
'@

if (-not $evidence.StartsWith($testResults, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($evidence) -notmatch '^rc48-r3-oneshot-restore-[0-9]{8}-[0-9]{9}$') {
    throw "Evidence directory is not a unique RC48 restore directory: $evidence"
}

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

# This bootstrap compilation intentionally uses the caller's ambient TEMP/TMP.
# No evidence path is read or written before the root handle is acquired.
Add-Type -TypeDefinition $evidenceRootLeaseSource -Language CSharp
if ($Mode -eq 'Freeze') {
    $script:evidenceLease = [Rc48EvidenceDirectoryLease]::CreateNew($evidence)
}
else {
    $script:evidenceLease = [Rc48EvidenceDirectoryLease]::Acquire($evidence)
}

try {
    $evidenceLease.Validate()
    $requiredEvidenceDirectories = @(
        $cliHome,
        $cliDotnet,
        $temp,
        $httpCache,
        $nugetScratch,
        $pluginCache,
        $nugetPluginPaths,
        $nugetNetCorePluginPaths,
        $nugetNetFxPluginPaths,
        $msbuildUserExtensions,
        $isolatedUserProfile,
        (Join-Path $isolatedUserProfile 'AppData'),
        $isolatedLocalAppData,
        $isolatedRoamingAppData
    )
    foreach ($requiredDirectory in $requiredEvidenceDirectories) {
        if ($Mode -eq 'Freeze') {
            $createdDirectory = [IO.Directory]::CreateDirectory($requiredDirectory)
            if (-not $createdDirectory.FullName.Equals(
                    [IO.Path]::GetFullPath($requiredDirectory),
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Evidence child directory resolved unexpectedly: $requiredDirectory"
            }
        }
        $evidenceLease.AnchorDirectories([string[]]@($requiredDirectory))
    }
    $evidenceLease.Validate()

    if (Test-Path -LiteralPath $r3) {
        throw "RC48 r3 package folder already exists: $r3"
    }
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw "dotnet host missing: $dotnet"
    }
    if (-not (Test-Path -LiteralPath $sdkPath -PathType Container)) {
        throw "SDK missing: $sdkPath"
    }

    if ($Mode -eq 'Freeze') {
        foreach ($sentinel in $sentinelNames) {
            $sentinelPath = [IO.Path]::GetFullPath((Join-Path $cliDotnet $sentinel))
            Write-NewEmptyEvidenceFile -Path $sentinelPath
            if (-not $script:freezeCreatedSentinelPaths.Add($sentinelPath)) {
                throw 'A required held sentinel was created more than once.'
            }
        }
        if ($script:freezeCreatedSentinelPaths.Count -ne 2) {
            throw 'The current evidence lease did not create the exact two held sentinels.'
        }
        Assert-PrelaunchIsolation
        $sourceRecords = @(Get-ControlledSourceRecords)
        $feedRecords = @(Get-DirectoryRecords -Path $feed)
        $fixedRecords = @(
            $fixedInputs | ForEach-Object {
                $fixedInput = [IO.Path]::GetFullPath($_)
                if ($script:freezeCreatedSentinelPaths.Contains($fixedInput)) {
                    Get-HeldSentinelRecord -Path $fixedInput
                }
                else {
                    Get-FileRecord -Path $fixedInput
                }
            }
        )
        $toolchain = Get-BoundedToolchainSnapshot
        Assert-ToolchainSnapshotSchema -Snapshot $toolchain -Label 'frozen toolchain'
        $feedBytes = Get-FileRecordByteTotal -Records $feedRecords -Label 'offline feed'
        if ($feedRecords.Count -ne 50 -or
            $feedBytes -ne 194634460L) {
            throw 'Offline feed identity mismatch before approval.'
        }
        $command = Get-CommandDefinition
        $executionIdentity = Get-ExecutionIdentityDefinition
        if ($executionIdentity.isSystem) {
            throw 'Freeze is forbidden under LocalSystem because TEMP/TMP isolation has different semantics.'
        }
        $nativeEnvironment = ConvertTo-NativeEnvironment `
            -Environment $command.environment `
            -ExpectedNames $expectedEnvironmentNames
        $environmentBlock = New-NativeEnvironmentBlock -Environment $nativeEnvironment
        $baseline = [ordered]@{
            schema = 3
            createdUtc = [DateTime]::UtcNow.ToString('o')
            source = $sourceRecords
            feed = $feedRecords
            fixedInputs = $fixedRecords
            toolchain = $toolchain
            command = $command
            executionIdentity = $executionIdentity
            environmentBlock = [ordered]@{
                sha256 = $environmentBlock.sha256
                entryCount = $environmentBlock.entryCount
                charCount = $environmentBlock.charCount
                unicodeByteCount = $environmentBlock.unicodeByteCount
            }
        }
        [byte[]]$baselineBytes = ConvertTo-JsonEvidenceBytes -Value $baseline
        [byte[]]$commandBytes = ConvertTo-JsonEvidenceBytes -Value $command
        $evidenceLease.WriteNewFile(
            $baselinePath,
            'approved-baseline.json',
            $baselineBytes)
        $evidenceLease.WriteNewFile(
            $commandPath,
            'command.json',
            $commandBytes)
        $manifestSha256 = Get-BytesSha256 -Bytes $baselineBytes
        [pscustomobject]@{
            Result = 'FROZEN_AWAITING_INDEPENDENT_APPROVAL'
            Evidence = $evidence
            Manifest = $baselinePath
            ManifestSha256 = $manifestSha256
            SourceFiles = $sourceRecords.Count
            FeedFiles = $feedRecords.Count
            FixedInputs = $fixedRecords.Count
            ToolchainFiles = $toolchain.fileCount
            ToolchainBytes = $toolchain.bytes
        }
        return
    }

    if ($ApprovedManifestSha256 -notmatch '^[A-F0-9]{64}$') {
        throw 'Approved manifest SHA256 is missing or malformed.'
    }
    [byte[]]$baselineBytes = $evidenceLease.ReadExactFileBytes(
        $baselinePath,
        'approved-baseline.json',
        16MB)
    $actualBaselineHash = Get-BytesSha256 -Bytes $baselineBytes
    if ($actualBaselineHash -cne $ApprovedManifestSha256) {
        throw "Approved baseline hash mismatch: $actualBaselineHash / $ApprovedManifestSha256"
    }
    $baselineJson = $utf8Strict.GetString($baselineBytes)
    $baseline = $baselineJson | ConvertFrom-Json -ErrorAction Stop
    Assert-CanonicalJsonBytes -Actual $baselineBytes -Value $baseline -Label 'approved baseline'
    if ([int]$baseline.schema -ne 3) { throw 'Approved baseline schema is invalid.' }
    [byte[]]$commandBytes = $evidenceLease.ReadExactFileBytes(
        $commandPath,
        'command.json',
        4MB)
    $commandEvidenceJson = $utf8Strict.GetString($commandBytes)
    $commandEvidence = $commandEvidenceJson | ConvertFrom-Json -ErrorAction Stop
    Assert-CanonicalJsonBytes -Actual $commandBytes -Value $commandEvidence -Label 'command evidence'

    Assert-PrelaunchIsolation
    $sourceBefore = @(Get-ControlledSourceRecords)
    $feedBefore = @(Get-DirectoryRecords -Path $feed)
    $fixedBefore = @($fixedInputs | ForEach-Object { Get-FileRecord -Path $_ })
    $toolchainBefore = Get-BoundedToolchainSnapshot
    Assert-RecordSetEqual -Expected @($baseline.source) -Actual $sourceBefore -Label 'approved source'
    Assert-RecordSetEqual -Expected @($baseline.feed) -Actual $feedBefore -Label 'approved feed'
    Assert-RecordSetEqual -Expected @($baseline.fixedInputs) -Actual $fixedBefore -Label 'approved fixed inputs'
    Assert-ToolchainSnapshotEqual `
        -Expected $baseline.toolchain `
        -Actual $toolchainBefore `
        -Label 'approved toolchain'
    $script:approvedInputLease = Acquire-ApprovedInputLease `
        -SourceRecords @($baseline.source) `
        -FeedRecords @($baseline.feed) `
        -FixedRecords @($baseline.fixedInputs) `
        -Toolchain $baseline.toolchain
    $approvedInputLease.Validate()
    $sourceUnderLease = @(Get-ControlledSourceRecords)
    $feedUnderLease = @(Get-DirectoryRecords -Path $feed)
    $fixedUnderLease = @($fixedInputs | ForEach-Object { Get-FileRecord -Path $_ })
    $toolchainUnderLease = Get-BoundedToolchainSnapshot
    Assert-RecordSetEqual `
        -Expected @($baseline.source) `
        -Actual $sourceUnderLease `
        -Label 'leased approved source'
    Assert-RecordSetEqual `
        -Expected @($baseline.feed) `
        -Actual $feedUnderLease `
        -Label 'leased approved feed'
    Assert-RecordSetEqual `
        -Expected @($baseline.fixedInputs) `
        -Actual $fixedUnderLease `
        -Label 'leased approved fixed inputs'
    Assert-ToolchainSnapshotEqual `
        -Expected $baseline.toolchain `
        -Actual $toolchainUnderLease `
        -Label 'leased approved toolchain'
    $command = Get-CommandDefinition
    $approvedCommandJson = $baseline.command | ConvertTo-Json -Depth 12 -Compress
    $actualCommandJson = $command | ConvertTo-Json -Depth 12 -Compress
    if ($actualCommandJson -cne $approvedCommandJson) { throw 'Approved command identity mismatch.' }
    if (($commandEvidence | ConvertTo-Json -Depth 12 -Compress) -cne $approvedCommandJson) {
        throw 'Command evidence file no longer matches the approved baseline.'
    }

    $executionIdentity = Get-ExecutionIdentityDefinition
    if ($executionIdentity.isSystem) {
        throw 'Execute is forbidden under LocalSystem because TEMP/TMP isolation has different semantics.'
    }
    $approvedIdentityJson = $baseline.executionIdentity | ConvertTo-Json -Depth 4 -Compress
    $actualIdentityJson = $executionIdentity | ConvertTo-Json -Depth 4 -Compress
    if ($actualIdentityJson -cne $approvedIdentityJson) {
        throw 'Execution identity no longer matches the approved non-SYSTEM SID and identity.'
    }
    $nativeEnvironment = ConvertTo-NativeEnvironment `
        -Environment $command.environment `
        -ExpectedNames $expectedEnvironmentNames
    $environmentBlock = New-NativeEnvironmentBlock -Environment $nativeEnvironment
    $approvedEnvironmentBlockJson = $baseline.environmentBlock |
        ConvertTo-Json -Depth 4 -Compress
    $actualEnvironmentBlockJson = [ordered]@{
        sha256 = $environmentBlock.sha256
        entryCount = $environmentBlock.entryCount
        charCount = $environmentBlock.charCount
        unicodeByteCount = $environmentBlock.unicodeByteCount
    } | ConvertTo-Json -Depth 4 -Compress
    if ($actualEnvironmentBlockJson -cne $approvedEnvironmentBlockJson) {
        throw 'Native Unicode environment block no longer matches the approved command.'
    }
    foreach ($unusedOutput in @(
            $attemptPath,
            $stdoutPath,
            $stderrPath,
            $resultPath,
            $failurePath,
            $binlogPath,
            $consoleLogPath,
            $sourceAfterPath,
            $feedAfterPath,
            $fixedAfterPath,
            $toolchainAfterPath)) {
        if (Test-Path -LiteralPath $unusedOutput) {
            throw "One-shot execute output already exists: $unusedOutput"
        }
    }

    $nativeRunnerSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

public static class Rc48SuspendedJobRunner
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const long PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint CREATE_NEW = 1;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileAttributeTagInfo = 9;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
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
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr handle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        IntPtr file,
        int fileInformationClass,
        out FILE_ATTRIBUTE_TAG_INFO fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        IntPtr file,
        out BY_HANDLE_FILE_INFORMATION fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

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

    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    private static string ComputeSha256(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static void ValidateEnvironmentBlock(
        IDictionary<string, string> environment,
        byte[] blockBytes,
        string expectedSha256,
        int expectedEntryCount)
    {
        if (environment == null || blockBytes == null)
            throw new ArgumentNullException("The approved native environment is missing.");
        if (environment.Count != 34 || expectedEntryCount != 34)
            throw new InvalidOperationException(
                "The native environment must contain exactly 34 entries.");
        if (blockBytes.Length < 4 || (blockBytes.Length & 1) != 0 ||
            blockBytes[blockBytes.Length - 1] != 0 ||
            blockBytes[blockBytes.Length - 2] != 0 ||
            blockBytes[blockBytes.Length - 3] != 0 ||
            blockBytes[blockBytes.Length - 4] != 0)
            throw new InvalidOperationException(
                "The native Unicode environment block is not double-NUL terminated.");

        string actualSha256 = ComputeSha256(blockBytes);
        if (!String.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The native Unicode environment block SHA256 changed before CreateProcessW.");

        string text = Encoding.Unicode.GetString(blockBytes);
        string[] segments = text.Split(new[] { '\0' }, StringSplitOptions.None);
        if (segments.Length != expectedEntryCount + 2 ||
            segments[segments.Length - 1].Length != 0 ||
            segments[segments.Length - 2].Length != 0)
            throw new InvalidOperationException(
                "The native Unicode environment block segment count is invalid.");

        var roundTrip = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < expectedEntryCount; index++)
        {
            string segment = segments[index];
            int separator = segment.IndexOf('=');
            if (separator <= 0)
                throw new InvalidOperationException(
                    "The native Unicode environment block contains a malformed entry.");
            string name = segment.Substring(0, separator);
            string value = segment.Substring(separator + 1);
            if (String.IsNullOrEmpty(value) ||
                roundTrip.ContainsKey(name))
                throw new InvalidOperationException(
                    "The native Unicode environment block contains an empty or duplicate entry.");
            roundTrip.Add(name, value);
        }
        if (roundTrip.Count != environment.Count)
            throw new InvalidOperationException(
                "The native Unicode environment block entry count changed.");
        foreach (var pair in environment)
        {
            string value;
            if (!roundTrip.TryGetValue(pair.Key, out value) ||
                !String.Equals(value, pair.Value, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The native Unicode environment block does not equal the approved command.");
        }
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path.Substring(4);
        return path;
    }

    private static string GetFinalPath(IntPtr handle, string context)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(
            handle,
            buffer,
            (uint)buffer.Capacity,
            0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " final-path resolution failed.");
        return Path.GetFullPath(NormalizeFinalPath(buffer.ToString()));
    }

    private static void AssertExactFinalPath(
        IntPtr handle,
        string expectedPath,
        string context)
    {
        string expected = Path.GetFullPath(expectedPath);
        string actual = GetFinalPath(handle, context);
        if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                context + " resolved outside its exact approved path.");
    }

    private static void AssertRegularSingleLinkFile(
        IntPtr handle,
        string context)
    {
        FILE_ATTRIBUTE_TAG_INFO tag;
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out tag,
                (uint)Marshal.SizeOf(typeof(FILE_ATTRIBUTE_TAG_INFO))))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " attribute-tag query failed.");
        if ((tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
            (tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            throw new InvalidOperationException(
                context + " is not a regular non-reparse file.");
        BY_HANDLE_FILE_INFORMATION identity;
        if (!GetFileInformationByHandle(handle, out identity))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                context + " object-identity query failed.");
        if (identity.NumberOfLinks != 1)
            throw new InvalidOperationException(
                context + " is not a single-link output object.");
        ulong fileId =
            ((ulong)identity.FileIndexHigh << 32) |
            identity.FileIndexLow;
        if (fileId == 0)
            throw new InvalidOperationException(
                context + " has an invalid object identity.");
    }

    private static IntPtr CreateInheritedOutput(
        string evidenceDirectory,
        string outputPath,
        string requiredFileName)
    {
        string evidence = Path.GetFullPath(evidenceDirectory).TrimEnd(
            Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(outputPath);
        if (!String.Equals(
                Path.GetDirectoryName(full),
                evidence,
                StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(
                Path.GetFileName(full),
                requiredFileName,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "A child output path escaped the exact evidence root.");

        var attributes = new SECURITY_ATTRIBUTES();
        attributes.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
        attributes.bInheritHandle = true;
        IntPtr handle = CreateFile(
            full,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ,
            ref attributes,
            CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "A child output file could not be created exactly once.");
        try
        {
            AssertExactFinalPath(handle, full, "A child output file");
            AssertRegularSingleLinkFile(handle, "A child output file");
            return handle;
        }
        catch
        {
            CloseHandle(handle);
            throw;
        }
    }

    private static IntPtr OpenInheritedNullInput()
    {
        var attributes = new SECURITY_ATTRIBUTES();
        attributes.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
        attributes.bInheritHandle = true;
        IntPtr handle = CreateFile(
            "NUL",
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            ref attributes,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The child NUL input handle could not be opened.");
        return handle;
    }

    private static IntPtr CreateHandleList(
        IntPtr standardInput,
        IntPtr standardOutput,
        IntPtr standardError,
        out IntPtr values)
    {
        values = IntPtr.Zero;
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The inherited-handle attribute-list size query failed.");
        IntPtr list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The inherited-handle attribute list could not be initialized.");
            values = Marshal.AllocHGlobal(IntPtr.Size * 3);
            try
            {
                Marshal.WriteIntPtr(values, 0, standardInput);
                Marshal.WriteIntPtr(values, IntPtr.Size, standardOutput);
                Marshal.WriteIntPtr(values, IntPtr.Size * 2, standardError);
                if (!UpdateProcThreadAttribute(
                        list,
                        0,
                        new IntPtr(PROC_THREAD_ATTRIBUTE_HANDLE_LIST),
                        values,
                        new IntPtr(IntPtr.Size * 3),
                        IntPtr.Zero,
                        IntPtr.Zero))
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The inherited-handle allowlist could not be installed.");
            }
            catch
            {
                Marshal.FreeHGlobal(values);
                values = IntPtr.Zero;
                throw;
            }
            return list;
        }
        catch
        {
            Marshal.FreeHGlobal(list);
            throw;
        }
    }

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
        byte[] environmentBlockBytes,
        string expectedEnvironmentBlockSha256,
        int expectedEnvironmentEntryCount,
        SafeHandle evidenceDirectoryLease,
        string evidenceDirectory,
        string stdoutPath,
        string stderrPath,
        int timeoutMilliseconds)
    {
        IntPtr job = IntPtr.Zero;
        IntPtr process = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr standardInput = IntPtr.Zero;
        IntPtr standardOutput = IntPtr.Zero;
        IntPtr standardError = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr attributeValues = IntPtr.Zero;
        bool assigned = false;
        bool evidenceLeaseReferenceAdded = false;
        IntPtr evidenceDirectoryHandle = IntPtr.Zero;
        try
        {
            ValidateEnvironmentBlock(
                environment,
                environmentBlockBytes,
                expectedEnvironmentBlockSha256,
                expectedEnvironmentEntryCount);

            if (evidenceDirectoryLease == null ||
                evidenceDirectoryLease.IsClosed ||
                evidenceDirectoryLease.IsInvalid)
                throw new ArgumentNullException("evidenceDirectoryLease");
            evidenceDirectoryLease.DangerousAddRef(
                ref evidenceLeaseReferenceAdded);
            evidenceDirectoryHandle = evidenceDirectoryLease.DangerousGetHandle();
            AssertExactFinalPath(
                evidenceDirectoryHandle,
                evidenceDirectory,
                "The evidence directory lease");
            standardOutput = CreateInheritedOutput(
                evidenceDirectory,
                stdoutPath,
                "restore.stdout.log");
            standardError = CreateInheritedOutput(
                evidenceDirectory,
                stderrPath,
                "restore.stderr.log");
            standardInput = OpenInheritedNullInput();
            attributeList = CreateHandleList(
                standardInput,
                standardOutput,
                standardError,
                out attributeValues);

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

            environmentBlock = Marshal.AllocHGlobal(environmentBlockBytes.Length);
            Marshal.Copy(
                environmentBlockBytes,
                0,
                environmentBlock,
                environmentBlockBytes.Length);

            var startup = new STARTUPINFOEX();
            startup.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = standardInput;
            startup.StartupInfo.hStdOutput = standardOutput;
            startup.StartupInfo.hStdError = standardError;
            startup.lpAttributeList = attributeList;
            PROCESS_INFORMATION info;
            var mutableCommand = new StringBuilder(commandLine);
            if (!CreateProcess(executable, mutableCommand, IntPtr.Zero, IntPtr.Zero, true,
                    CREATE_NO_WINDOW | CREATE_SUSPENDED |
                    CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
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
            Exception cleanupFailure = null;
            try
            {
                TerminateOwnedProcessTree(
                    job,
                    process,
                    assigned,
                    1223,
                    "One-shot restore cleanup");
            }
            catch (Exception cleanupError)
            {
                cleanupFailure = cleanupError;
            }
            // Closing the configured job first is the final fail-safe for every
            // process that was successfully assigned to this private job.
            if (job != IntPtr.Zero && !CloseHandle(job) && cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore job handle could not be closed.");
            if (thread != IntPtr.Zero && !CloseHandle(thread) && cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore thread handle could not be closed.");
            if (process != IntPtr.Zero && !CloseHandle(process) && cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore process handle could not be closed.");
            if (standardOutput != IntPtr.Zero &&
                standardOutput != InvalidHandleValue &&
                !FlushFileBuffers(standardOutput) &&
                cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore stdout evidence could not be durably flushed.");
            if (standardError != IntPtr.Zero &&
                standardError != InvalidHandleValue &&
                !FlushFileBuffers(standardError) &&
                cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore stderr evidence could not be durably flushed.");
            try
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }
            catch (Exception cleanupError)
            {
                if (cleanupFailure == null) cleanupFailure = cleanupError;
            }
            try
            {
                if (attributeValues != IntPtr.Zero)
                    Marshal.FreeHGlobal(attributeValues);
            }
            catch (Exception cleanupError)
            {
                if (cleanupFailure == null) cleanupFailure = cleanupError;
            }
            if (standardInput != IntPtr.Zero &&
                standardInput != InvalidHandleValue &&
                !CloseHandle(standardInput) &&
                cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore stdin handle could not be closed.");
            if (standardOutput != IntPtr.Zero &&
                standardOutput != InvalidHandleValue &&
                !CloseHandle(standardOutput) &&
                cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore stdout handle could not be closed.");
            if (standardError != IntPtr.Zero &&
                standardError != InvalidHandleValue &&
                !CloseHandle(standardError) &&
                cleanupFailure == null)
                cleanupFailure = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The one-shot restore stderr handle could not be closed.");
            try
            {
                if (environmentBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(environmentBlock);
            }
            catch (Exception cleanupError)
            {
                if (cleanupFailure == null) cleanupFailure = cleanupError;
            }
            try
            {
                if (evidenceLeaseReferenceAdded)
                    evidenceDirectoryLease.DangerousRelease();
            }
            catch (Exception cleanupError)
            {
                if (cleanupFailure == null) cleanupFailure = cleanupError;
            }
            if (cleanupFailure != null)
                throw cleanupFailure;
        }
    }
}
'@

$arguments = @($command.arguments)
$nativeCommandLine = '"' + $dotnet + '" ' +
    (@($arguments | ForEach-Object { ConvertTo-NativeArgument -Value ([string]$_) }) -join ' ')

Add-Type -TypeDefinition $nativeRunnerSource -Language CSharp
$evidenceLease.Validate()
$approvedInputLease.Validate()
$evidenceLease.CreateAnchoredOutput($binlogPath, 'restore.binlog')
$evidenceLease.CreateAnchoredOutput($consoleLogPath, 'restore.console.log')
$env:TEMP = $temp
$env:TMP = $temp
$evidenceTempActivated = $true
Assert-PrelaunchIsolation
Write-NewJsonEvidence -Path $attemptPath -Value ([ordered]@{
        schema = 3
        approvedManifestSha256 = $ApprovedManifestSha256
        executorSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        environmentBlockSha256 = $environmentBlock.sha256
        environmentEntryCount = $environmentBlock.entryCount
        environmentUnicodeByteCount = $environmentBlock.unicodeByteCount
        executionIdentity = $executionIdentity
        attemptedUtc = [DateTime]::UtcNow.ToString('o')
    })

$startedUtc = [DateTime]::UtcNow
try {
    $exitCode = [Rc48SuspendedJobRunner]::Run(
        $dotnet,
        $nativeCommandLine,
        $root,
        $nativeEnvironment,
        $environmentBlock.bytes,
        $environmentBlock.sha256,
        $environmentBlock.entryCount,
        $evidenceLease,
        $evidence,
        $stdoutPath,
        $stderrPath,
        $timeoutSeconds * 1000)
    $approvedInputLease.Validate()
    $binlogBytes = $evidenceLease.FlushAnchoredOutput(
        $binlogPath,
        'restore.binlog')
    $consoleLogBytes = $evidenceLease.FlushAnchoredOutput(
        $consoleLogPath,
        'restore.console.log')
}
    catch {
        $endedUtc = [DateTime]::UtcNow
        Write-NewJsonEvidence -Path $resultPath -Value ([ordered]@{
                schema = 3
                startedUtc = $startedUtc.ToString('o')
                endedUtc = $endedUtc.ToString('o')
                elapsedMilliseconds = [long]($endedUtc - $startedUtc).TotalMilliseconds
                outcome = 'exception'
                exitCode = $null
                environmentBlockSha256 = $environmentBlock.sha256
                executionSid = $executionIdentity.sid
            })
        Write-NewJsonEvidence -Path $failurePath -Value ([ordered]@{
                schema = 3
                startedUtc = $startedUtc.ToString('o')
                failedUtc = $endedUtc.ToString('o')
                kind = $_.Exception.GetType().FullName
                error = $_.Exception.ToString()
                stdoutPath = $stdoutPath
                stdoutExists = [IO.File]::Exists($stdoutPath)
                stdoutBytes = if ([IO.File]::Exists($stdoutPath)) {
                    [long]([IO.FileInfo]::new($stdoutPath).Length)
                } else { $null }
                stderrPath = $stderrPath
                stderrExists = [IO.File]::Exists($stderrPath)
                stderrBytes = if ([IO.File]::Exists($stderrPath)) {
                    [long]([IO.FileInfo]::new($stderrPath).Length)
                } else { $null }
            })
        throw
    }
    $endedUtc = [DateTime]::UtcNow
    if ($exitCode -ne 0) {
        Write-NewJsonEvidence -Path $resultPath -Value ([ordered]@{
                schema = 3
                startedUtc = $startedUtc.ToString('o')
                endedUtc = $endedUtc.ToString('o')
                elapsedMilliseconds = [long]($endedUtc - $startedUtc).TotalMilliseconds
                outcome = 'nonzero-exit'
                exitCode = $exitCode
                environmentBlockSha256 = $environmentBlock.sha256
                executionSid = $executionIdentity.sid
            })
        Write-NewJsonEvidence -Path $failurePath -Value ([ordered]@{
                schema = 3
                startedUtc = $startedUtc.ToString('o')
                failedUtc = $endedUtc.ToString('o')
                kind = 'NativeProcessNonzeroExit'
                error = "The one-shot restore failed with exit code $exitCode."
                exitCode = $exitCode
                stdoutPath = $stdoutPath
                stdoutExists = [IO.File]::Exists($stdoutPath)
                stdoutBytes = if ([IO.File]::Exists($stdoutPath)) {
                    [long]([IO.FileInfo]::new($stdoutPath).Length)
                } else { $null }
                stderrPath = $stderrPath
                stderrExists = [IO.File]::Exists($stderrPath)
                stderrBytes = if ([IO.File]::Exists($stderrPath)) {
                    [long]([IO.FileInfo]::new($stderrPath).Length)
                } else { $null }
            })
        throw "The one-shot restore failed with exit code $exitCode."
    }
    try {
        if (-not (Test-Path -LiteralPath $r3 -PathType Container)) {
            throw 'The one-shot restore returned success without creating the r3 package folder.'
        }
        if ($binlogBytes -le 0 -or $consoleLogBytes -le 0) {
            throw 'The restore logger outputs were empty after durable same-object flush.'
        }
        foreach ($requiredOutput in @($stdoutPath, $stderrPath)) {
            if (-not (Test-Path -LiteralPath $requiredOutput -PathType Leaf)) {
                throw "The one-shot restore output is missing: $requiredOutput"
            }
        }
        Assert-EmptyDirectory -Path $msbuildUserExtensions -Label 'MSBuild user extensions'
        Assert-NuGetPluginDiscoveryIsolation
        Assert-UserProfileIsolation -AllowPostRestoreNuGetArtifacts
        Assert-DotNetCliHomeIsolation

        $sourceAfter = @(Get-ControlledSourceRecords)
        $feedAfter = @(Get-DirectoryRecords -Path $feed)
        $fixedAfter = @($fixedInputs | ForEach-Object { Get-FileRecord -Path $_ })
        $toolchainAfter = Get-BoundedToolchainSnapshot
        Assert-RecordSetEqual -Expected @($baseline.source) -Actual $sourceAfter -Label 'controlled source'
        Assert-RecordSetEqual -Expected @($baseline.feed) -Actual $feedAfter -Label 'offline feed'
        Assert-RecordSetEqual -Expected @($baseline.fixedInputs) -Actual $fixedAfter -Label 'fixed inputs'
        Assert-ToolchainSnapshotEqual `
            -Expected $baseline.toolchain `
            -Actual $toolchainAfter `
            -Label 'toolchain after restore'
        Write-NewJsonEvidence -Path $sourceAfterPath -Value $sourceAfter
        Write-NewJsonEvidence -Path $feedAfterPath -Value $feedAfter
        Write-NewJsonEvidence -Path $fixedAfterPath -Value $fixedAfter
        Write-NewJsonEvidence -Path $toolchainAfterPath -Value $toolchainAfter
    }
    catch {
        $postcheckEndedUtc = [DateTime]::UtcNow
        Write-NewJsonEvidence -Path $resultPath -Value ([ordered]@{
                schema = 3
                startedUtc = $startedUtc.ToString('o')
                endedUtc = $postcheckEndedUtc.ToString('o')
                elapsedMilliseconds = [long]($postcheckEndedUtc - $startedUtc).TotalMilliseconds
                outcome = 'postcheck-exception'
                exitCode = $exitCode
                environmentBlockSha256 = $environmentBlock.sha256
                executionSid = $executionIdentity.sid
            })
        Write-NewJsonEvidence -Path $failurePath -Value ([ordered]@{
                schema = 3
                startedUtc = $startedUtc.ToString('o')
                failedUtc = $postcheckEndedUtc.ToString('o')
                kind = $_.Exception.GetType().FullName
                stage = 'post-restore-validation'
                error = $_.Exception.ToString()
                exitCode = $exitCode
                stdoutPath = $stdoutPath
                stdoutExists = [IO.File]::Exists($stdoutPath)
                stdoutBytes = if ([IO.File]::Exists($stdoutPath)) {
                    [long]([IO.FileInfo]::new($stdoutPath).Length)
                } else { $null }
                stderrPath = $stderrPath
                stderrExists = [IO.File]::Exists($stderrPath)
                stderrBytes = if ([IO.File]::Exists($stderrPath)) {
                    [long]([IO.FileInfo]::new($stderrPath).Length)
                } else { $null }
            })
        throw
    }
    $completedUtc = [DateTime]::UtcNow
    Write-NewJsonEvidence -Path $resultPath -Value ([ordered]@{
            schema = 3
            startedUtc = $startedUtc.ToString('o')
            endedUtc = $completedUtc.ToString('o')
            elapsedMilliseconds = [long]($completedUtc - $startedUtc).TotalMilliseconds
            outcome = 'success'
            exitCode = $exitCode
            environmentBlockSha256 = $environmentBlock.sha256
            executionSid = $executionIdentity.sid
        })

    [pscustomobject]@{
        Result = 'RESTORE_EXIT_ZERO_APPROVED_INPUTS_FROZEN'
        Evidence = $evidence
        ElapsedMilliseconds = [long]($completedUtc - $startedUtc).TotalMilliseconds
        FeedFiles = $feedAfter.Count
        PackageFolder = $r3
        Binlog = $binlogPath
        ConsoleLog = $consoleLogPath
        StandardOutputLog = $stdoutPath
        StandardErrorLog = $stderrPath
    }
}
finally {
    if ($evidenceTempActivated) {
        $env:TEMP = $originalTemp
        $env:TMP = $originalTmp
        $evidenceTempActivated = $false
    }
    try {
        if ($null -ne $approvedInputLease) {
            $approvedInputLease.Validate()
        }
    }
    finally {
        try {
            if ($null -ne $approvedInputLease) {
                $approvedInputLease.Dispose()
                $script:approvedInputLease = $null
            }
        }
        finally {
            if ($null -ne $evidenceLease) {
                try {
                    $evidenceLease.Validate()
                }
                finally {
                    $evidenceLease.Dispose()
                }
            }
        }
    }
}
