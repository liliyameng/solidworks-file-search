$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$executorPath = Join-Path $PSScriptRoot 'Invoke-Rc48R2OneShotRestore.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw "RC48_R2_ONESHOT_STATIC_GATE: $Message" }
}

function Get-SafetyFailures {
    param([Parameter(Mandatory = $true)][string]$Text)

    $failures = [Collections.Generic.List[string]]::new()
    function Require-ExactCount {
        param(
            [Parameter(Mandatory = $true)][string]$Needle,
            [Parameter(Mandatory = $true)][int]$Count,
            [Parameter(Mandatory = $true)][string]$Label
        )
        $actual = ([regex]::Matches(
                $Text,
                [regex]::Escape($Needle),
                [Text.RegularExpressions.RegexOptions]::CultureInvariant)).Count
        if ($actual -ne $Count) {
            $failures.Add("$Label count was $actual, expected $Count")
        }
    }
    function Require-Pattern {
        param(
            [Parameter(Mandatory = $true)][string]$Pattern,
            [Parameter(Mandatory = $true)][string]$Label
        )
        if ($Text -notmatch $Pattern) { $failures.Add("$Label missing") }
    }
    function Reject-Pattern {
        param(
            [Parameter(Mandatory = $true)][string]$Pattern,
            [Parameter(Mandatory = $true)][string]$Label
        )
        if ($Text -match $Pattern) { $failures.Add("$Label present") }
    }

    Require-Pattern -Pattern "(?s)\[ValidateSet\('Freeze', 'Execute'\)\].*?\`$ApprovedManifestSha256 = ''" `
        -Label 'two-phase approval contract'
    Require-ExactCount -Needle '$timeoutSeconds = 600' -Count 1 `
        -Label 'hard timeout'
    Reject-Pattern -Pattern '(?m)^\s*\[(?:int|long)\]\$timeoutSeconds\b' `
        -Label 'overridable timeout parameter'
    Require-ExactCount -Needle "'-target:Restore'" -Count 1 `
        -Label 'single restore target'
    Require-Pattern -Pattern "(?s)\`$arguments\s*=\s*@\(\s*'exec',\s*\`$msbuildDll,\s*\`$graph," `
        -Label 'direct pinned MSBuild.dll invocation'
    Require-ExactCount -Needle '$msbuildDll = ''C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll''' `
        -Count 1 -Label 'pinned MSBuild assembly'
    Require-ExactCount -Needle '[Rc48SuspendedJobRunner]::Run(' -Count 1 `
        -Label 'single native restore invocation'
    Reject-Pattern -Pattern '(?i)\bdotnet\s+restore\b|Start-Process|Process(?:\]::|\.)Start|Process(?:\]::|\.)GetProcesses|Process(?:\]::|\.)GetProcessById|\.Kill\s*\(' `
        -Label 'unsafe process or alternate restore launcher'

    Require-ExactCount -Needle 'private const uint CREATE_SUSPENDED = 0x00000004;' `
        -Count 1 -Label 'suspended creation'
    Require-ExactCount -Needle 'private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;' `
        -Count 1 -Label 'kill-on-close job'
    Require-Pattern -Pattern 'CREATE_NO_WINDOW\s*\|\s*CREATE_SUSPENDED\s*\|\s*CREATE_UNICODE_ENVIRONMENT' `
        -Label 'suspended no-window process creation'
    Require-Pattern -Pattern 'AssignProcessToJobObject\(job, process\)' `
        -Label 'job assignment'
    Require-Pattern -Pattern 'TerminateOwnedProcessTree\(' `
        -Label 'bounded owned-process cleanup'
    Require-Pattern -Pattern '(?s)if \(assigned\).*?TerminateJobObject\(job, exitCode\).*?TerminateProcess\(process, exitCode\)' `
        -Label 'capability-bound termination branches'
    Require-Pattern -Pattern 'QueryInformationJobObject\(' `
        -Label 'job accounting query'
    Require-Pattern -Pattern 'accounting\.ActiveProcesses' `
        -Label 'ACTIVE_PROCESS_ZERO observation'
    Require-Pattern -Pattern 'WaitForJobEmpty\(job, CLEANUP_WAIT_MILLISECONDS, context\)' `
        -Label 'bounded ACTIVE_PROCESS_ZERO wait'
    Reject-Pattern -Pattern '\bOpenProcess\s*\(|CreateToolhelp32Snapshot|Process32First|Process32Next|EnumProcesses' `
        -Label 'PID reopen or process enumeration'
    Require-ExactCount -Needle '[pscustomobject][ordered]@{' -Count 1 `
        -Label 'property-backed frozen file record'
    Require-ExactCount -Needle 'Assert-FileRecordSchema' -Count 4 `
        -Label 'file-record schema validation call graph'
    Require-ExactCount -Needle 'Get-FileRecordByteTotal' -Count 2 `
        -Label 'schema-aware byte aggregation call graph'
    Require-ExactCount -Needle 'throw "$Label record is missing required property: $name"' `
        -Count 1 -Label 'missing record field rejection'
    Require-ExactCount -Needle 'throw "$Label record byte count is not an integer CLR value."' `
        -Count 1 -Label 'non-integer record byte rejection'
    foreach ($integerType in @(
            'System.SByte',
            'System.Byte',
            'System.Int16',
            'System.UInt16',
            'System.Int32',
            'System.UInt32',
            'System.Int64')) {
        Require-ExactCount -Needle "'$integerType'" -Count 1 `
            -Label "allowed integral byte type $integerType"
    }
    Require-ExactCount -Needle '$feedBytes = Get-FileRecordByteTotal -Records $feedRecords -Label ''offline feed''' `
        -Count 1 -Label 'offline feed schema-aware aggregation'
    Reject-Pattern -Pattern 'Measure-Object\s+(?:-Property\s+)?bytes\b' `
        -Label 'OrderedDictionary-incompatible byte aggregation'

    Require-ExactCount -Needle '-property:MSBuildUserExtensionsPath=$msbuildUserExtensions' `
        -Count 1 -Label 'isolated MSBuild user extensions'
    foreach ($property in @(
            '-property:DirectoryBuildPropsPath=$directoryBuildPropsPath',
            '-property:DirectoryBuildTargetsPath=',
            '-property:DirectoryPackagesPropsPath=',
            '-property:ImportDirectoryBuildTargets=false',
            '-property:ImportDirectoryPackagesProps=false',
            '-property:ImportProjectExtensionProps=false',
            '-property:ImportProjectExtensionTargets=false',
            '-property:ExcludeRestorePackageImports=true',
            '-property:MSBuildEnableWorkloadResolver=false')) {
        Require-ExactCount -Needle $property -Count 1 `
            -Label "ancestor/obj import boundary $property"
    }
    foreach ($flag in @(
            'ImportUserLocationsByWildcardBeforeMicrosoftCommonProps',
            'ImportUserLocationsByWildcardAfterMicrosoftCommonProps',
            'ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets',
            'ImportUserLocationsByWildcardAfterMicrosoftCommonTargets',
            'ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets',
            'ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets')) {
        Require-ExactCount -Needle "-property:$flag=false" -Count 1 `
            -Label "$flag freeze"
    }
    Require-Pattern -Pattern 'Assert-EmptyDirectory -Path \$msbuildUserExtensions -Label ''MSBuild user extensions''' `
        -Label 'MSBuild extension emptiness checks'
    if (([regex]::Matches(
                $Text,
                [regex]::Escape(
                    "Assert-EmptyDirectory -Path `$msbuildUserExtensions -Label 'MSBuild user extensions'"))).Count -ne 1) {
        $failures.Add(
            'MSBuild extension directory must be checked again after restore')
    }
    Require-ExactCount -Needle 'Assert-PrelaunchIsolation' -Count 3 `
        -Label 'freeze and execute prelaunch isolation'
    Require-Pattern -Pattern "Label = 'MSBuild user extensions'" `
        -Label 'prelaunch MSBuild extension isolation'
    Require-ExactCount -Needle 'Assert-NuGetPluginDiscoveryIsolation' -Count 3 `
        -Label 'NuGet plugin discovery pre/post isolation'
    Require-ExactCount -Needle 'Assert-UserProfileIsolation' -Count 3 `
        -Label 'USERPROFILE pre/post isolation'
    Require-ExactCount -Needle 'Assert-DotNetCliHomeIsolation' -Count 3 `
        -Label 'DOTNET_CLI_HOME pre/post isolation'
    Require-Pattern -Pattern '(?s)\$expectedProfileDirectories\s*=\s*@\(.*?AppData.*?\$isolatedLocalAppData.*?\$isolatedRoamingAppData.*?\).*?\[IO\.Directory\]::EnumerateDirectories\(\s*\$isolatedUserProfile,\s*''\*'',\s*\[IO\.SearchOption\]::AllDirectories\)' `
        -Label 'exact recursive USERPROFILE directory allowlist'
    Require-ExactCount -Needle "throw 'Isolated USERPROFILE directory tree is not exact.'" `
        -Count 1 -Label 'USERPROFILE directory-tree rejection'
    Require-Pattern -Pattern '(?s)\[IO\.Directory\]::EnumerateFiles\(\s*\$isolatedUserProfile,\s*''\*'',\s*\[IO\.SearchOption\]::AllDirectories\).*?throw ''Isolated USERPROFILE contains an unexpected file\.''' `
        -Label 'USERPROFILE file rejection'
    Require-Pattern -Pattern '\.Equals\(\s*\[IO\.Path\]::GetFullPath\(\$cliDotnet\),\s*\[StringComparison\]::OrdinalIgnoreCase\)' `
        -Label 'case-safe exact DOTNET_CLI_HOME root'

    foreach ($isolatedSetting in @(
            'USERPROFILE = $isolatedUserProfile',
            'LOCALAPPDATA = $isolatedLocalAppData',
            'APPDATA = $isolatedRoamingAppData',
            'NUGET_PACKAGES = $r2',
            'NUGET_HTTP_CACHE_PATH = $httpCache',
            'NUGET_SCRATCH = $nugetScratch',
            'NUGET_PLUGINS_CACHE_PATH = $pluginCache',
            'NUGET_PLUGIN_PATHS = $nugetPluginPaths',
            'NUGET_NETCORE_PLUGIN_PATHS = $nugetNetCorePluginPaths',
            'NUGET_NETFX_PLUGIN_PATHS = $nugetNetFxPluginPaths')) {
        Require-ExactCount -Needle $isolatedSetting -Count 1 `
            -Label "isolated environment $isolatedSetting"
    }
    Reject-Pattern -Pattern '(?i)https?://|\\\.nuget\\packages' `
        -Label 'network or user global package cache'

    Require-Pattern -Pattern '\$actualBaselineHash -cne \$ApprovedManifestSha256' `
        -Label 'independent manifest approval'
    Require-ExactCount -Needle '[IO.FileMode]::CreateNew' -Count 1 `
        -Label 'atomic one-shot marker'
    Require-ExactCount -Needle 'Write-NewJsonEvidence -Path $attemptPath' -Count 1 `
        -Label 'one-shot attempt creation'
    Require-Pattern -Pattern 'approvedManifestSha256 = \$ApprovedManifestSha256' `
        -Label 'attempt marker manifest binding'
    Require-Pattern -Pattern 'Assert-RecordSetEqual -Expected @\(\$baseline\.source\) -Actual \$sourceBefore' `
        -Label 'approved source precheck'
    Require-Pattern -Pattern 'Assert-RecordSetEqual -Expected @\(\$baseline\.feed\) -Actual \$feedBefore' `
        -Label 'approved feed precheck'
    Require-Pattern -Pattern 'Assert-RecordSetEqual -Expected @\(\$baseline\.fixedInputs\) -Actual \$fixedBefore' `
        -Label 'approved fixed-input precheck'
    Require-Pattern -Pattern 'Assert-RecordSetEqual -Expected @\(\$baseline\.source\) -Actual \$sourceAfter' `
        -Label 'source postcheck'
    Require-Pattern -Pattern 'Assert-RecordSetEqual -Expected @\(\$baseline\.feed\) -Actual \$feedAfter' `
        -Label 'feed postcheck'
    Require-Pattern -Pattern 'Assert-RecordSetEqual -Expected @\(\$baseline\.fixedInputs\) -Actual \$fixedAfter' `
        -Label 'fixed-input postcheck'
    Require-Pattern -Pattern 'if \(Test-Path -LiteralPath \$r2\) \{ throw' `
        -Label 'one-shot package folder precondition'
    Require-Pattern -Pattern "if \(\`$Mode -eq 'Freeze'\)" `
        -Label 'case-safe Freeze branch'
    Require-Pattern -Pattern "\^rc48-r2-oneshot-restore-\[0-9\]\{8\}-\[0-9\]\{9\}\`$" `
        -Label 'unique evidence directory'
    Require-Pattern -Pattern "'-noAutoResponse'" -Label 'response-file suppression'
    Require-Pattern -Pattern "'-nodeReuse:false'" -Label 'node reuse suppression'
    Require-Pattern -Pattern "'-maxCpuCount:1'" -Label 'single-node restore'
    Require-Pattern -Pattern "'-property:RestoreNoCache=true'" -Label 'no-cache restore'
    Require-Pattern -Pattern "'-property:RestoreDisableParallel=true'" -Label 'serial restore'

    return @($failures)
}

Assert-True -Condition (Test-Path -LiteralPath $executorPath -PathType Leaf) `
    -Message "executor missing: $executorPath"
$executorText = Get-Content -LiteralPath $executorPath -Raw -Encoding UTF8

$tokens = $null
$parseErrors = $null
$executorAst = [Management.Automation.Language.Parser]::ParseFile(
    $executorPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True -Condition ($parseErrors.Count -eq 0) `
    -Message ('PowerShell parse failed: ' + (($parseErrors | ForEach-Object Message) -join '; '))

$baselineFailures = @(Get-SafetyFailures -Text $executorText)
Assert-True -Condition ($baselineFailures.Count -eq 0) `
    -Message ('safe executor rejected: ' + ($baselineFailures -join '; '))

$nativeMatch = [regex]::Match(
    $executorText,
    "(?s)\`$nativeRunnerSource = @'\r?\n(?<source>.*?)\r?\n'@")
Assert-True -Condition $nativeMatch.Success -Message 'embedded native runner was not found'
$nativeSource = $nativeMatch.Groups['source'].Value
$assignIndex = $nativeSource.IndexOf(
    'if (!AssignProcessToJobObject(job, process))',
    [StringComparison]::Ordinal)
$resumeIndex = $nativeSource.IndexOf(
    'if (ResumeThread(thread) == UInt32.MaxValue)',
    [StringComparison]::Ordinal)
$finallyIndex = $nativeSource.LastIndexOf(
    'finally',
    [StringComparison]::Ordinal)
$closeJobIndex = $nativeSource.IndexOf(
    'if (job != IntPtr.Zero) CloseHandle(job);',
    $finallyIndex,
    [StringComparison]::Ordinal)
$closeProcessIndex = $nativeSource.IndexOf(
    'if (process != IntPtr.Zero) CloseHandle(process);',
    $finallyIndex,
    [StringComparison]::Ordinal)
Assert-True -Condition (
    $assignIndex -ge 0 -and
    $resumeIndex -gt $assignIndex) `
    -Message 'process must be assigned to the private job before its first thread resumes'
Assert-True -Condition (
    $finallyIndex -ge 0 -and
    $closeJobIndex -gt $finallyIndex -and
    $closeProcessIndex -gt $closeJobIndex) `
    -Message 'finally must close the kill-on-close job before releasing the original process handle'

$approvalIndex = $executorText.IndexOf(
    'if ($actualBaselineHash -cne $ApprovedManifestSha256)',
    [StringComparison]::Ordinal)
$attemptIndex = $executorText.IndexOf(
    'Write-NewJsonEvidence -Path $attemptPath',
    [StringComparison]::Ordinal)
$addTypeIndex = $executorText.IndexOf(
    'Add-Type -TypeDefinition $nativeRunnerSource',
    [StringComparison]::Ordinal)
$runIndex = $executorText.IndexOf(
    '[Rc48SuspendedJobRunner]::Run(',
    [StringComparison]::Ordinal)
Assert-True -Condition (
    $approvalIndex -ge 0 -and
    $attemptIndex -gt $approvalIndex -and
    $addTypeIndex -gt $attemptIndex -and
    $runIndex -gt $addTypeIndex) `
    -Message 'attempt marker must be created after approval validation and before any native launch path'

$compileEvidence = Join-Path $root (
    'TestResults\rc48-r2-oneshot-static-' + [Guid]::NewGuid().ToString('N'))
$compileTemp = Join-Path $compileEvidence 'temp'
New-Item -ItemType Directory -Path $compileTemp | Out-Null
$env:TEMP = $compileTemp
$env:TMP = $compileTemp
Add-Type -TypeDefinition $nativeSource -Language CSharp
Assert-True -Condition ($null -ne ('Rc48SuspendedJobRunner' -as [type])) `
    -Message 'embedded C# runner did not compile'

$atomicMarkerFixture = Join-Path $compileEvidence 'execute-attempt.fixture'
$markerStream = [IO.File]::Open(
    $atomicMarkerFixture,
    [IO.FileMode]::CreateNew,
    [IO.FileAccess]::Write,
    [IO.FileShare]::None)
$markerStream.Dispose()
$replayRejected = $false
try {
    $markerStream = [IO.File]::Open(
        $atomicMarkerFixture,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    $markerStream.Dispose()
}
catch [IO.IOException] {
    $replayRejected = $true
}
Assert-True -Condition $replayRejected `
    -Message 'FileMode.CreateNew did not reject a repeated execute attempt marker'

$isolationFixtureResult = & {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Language.ScriptBlockAst]$ExecutorAst,
        [Parameter(Mandatory = $true)]
        [string]$FixtureRoot
    )

    foreach ($functionName in @(
            'Assert-EmptyDirectory',
            'Assert-NuGetPluginDiscoveryIsolation',
            'Assert-UserProfileIsolation',
            'Assert-DotNetCliHomeIsolation',
            'Assert-PrelaunchIsolation')) {
        $definition = @(
            $ExecutorAst.FindAll(
                {
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -ceq $functionName
                },
                $true))
        if ($definition.Count -ne 1) {
            throw "Expected exactly one executor function named $functionName."
        }
        Invoke-Expression $definition[0].Extent.Text
    }

    $temp = Join-Path $FixtureRoot 'temp'
    $httpCache = Join-Path $FixtureRoot 'nuget-http-cache'
    $nugetScratch = Join-Path $FixtureRoot 'nuget-scratch'
    $pluginCache = Join-Path $FixtureRoot 'nuget-plugin-cache'
    $nugetPluginPaths = Join-Path $FixtureRoot 'nuget-plugin-paths-empty'
    $nugetNetCorePluginPaths = Join-Path $FixtureRoot 'nuget-netcore-plugin-paths-empty'
    $nugetNetFxPluginPaths = Join-Path $FixtureRoot 'nuget-netfx-plugin-paths-empty'
    $msbuildUserExtensions = Join-Path $FixtureRoot 'msbuild-user-extensions-empty'
    $isolatedUserProfile = Join-Path $FixtureRoot 'user-profile'
    $isolatedLocalAppData = Join-Path $isolatedUserProfile 'AppData\Local'
    $isolatedRoamingAppData = Join-Path $isolatedUserProfile 'AppData\Roaming'
    $cliHome = Join-Path $FixtureRoot 'dotnet-cli-home'
    $cliDotnet = Join-Path $cliHome '.dotnet'
    $sentinelNames = @(
        '8.0.421.dotnetFirstUseSentinel',
        '8.0.421.aspNetCertificateSentinel'
    )

    New-Item -ItemType Directory -Path $cliDotnet, $temp, $httpCache, `
        $nugetScratch, $pluginCache, $nugetPluginPaths, `
        $nugetNetCorePluginPaths, $nugetNetFxPluginPaths, `
        $msbuildUserExtensions, $isolatedLocalAppData, `
        $isolatedRoamingAppData | Out-Null
    foreach ($sentinel in $sentinelNames) {
        [IO.File]::WriteAllText(
            (Join-Path $cliDotnet $sentinel),
            'frozen-fixture',
            [Text.UTF8Encoding]::new($false))
    }

    Assert-PrelaunchIsolation
    $pluginPathRejected = $false
    [IO.File]::WriteAllText(
        (Join-Path $nugetNetCorePluginPaths 'unapproved-plugin.dll'),
        'unapproved',
        [Text.UTF8Encoding]::new($false))
    try {
        Assert-NuGetPluginDiscoveryIsolation
    }
    catch {
        $pluginPathRejected = $true
    }

    $profilePluginRejected = $false
    $profilePlugin = Join-Path $isolatedUserProfile '.nuget\plugins\netcore'
    New-Item -ItemType Directory -Path $profilePlugin | Out-Null
    try {
        Assert-UserProfileIsolation
    }
    catch {
        $profilePluginRejected = $true
    }

    [pscustomobject]@{
        SafeTreeAccepted = $true
        ExplicitPluginRejected = $pluginPathRejected
        UserProfilePluginRejected = $profilePluginRejected
        FixtureRoot = $FixtureRoot
    }
} -ExecutorAst $executorAst -FixtureRoot (Join-Path $compileEvidence 'isolation-fixture')
Assert-True -Condition $isolationFixtureResult.ExplicitPluginRejected `
    -Message 'real explicit NuGet plugin-path injection was not rejected'
Assert-True -Condition $isolationFixtureResult.UserProfilePluginRejected `
    -Message 'real USERPROFILE\.nuget\plugins\netcore injection was not rejected'

$fileRecordFixtureResult = & {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Language.ScriptBlockAst]$ExecutorAst,
        [Parameter(Mandatory = $true)]
        [string]$FixtureRoot,
        [Parameter(Mandatory = $true)]
        [string]$RealFeedPath
    )

    foreach ($functionName in @(
            'Get-FileRecord',
            'Get-DirectoryRecords',
            'Assert-FileRecordSchema',
            'Get-FileRecordByteTotal')) {
        $definition = @(
            $ExecutorAst.FindAll(
                {
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -ceq $functionName
                },
                $true))
        if ($definition.Count -ne 1) {
            throw "Expected exactly one executor function named $functionName."
        }
        Invoke-Expression $definition[0].Extent.Text
    }

    New-Item -ItemType Directory -Path $FixtureRoot | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $FixtureRoot 'a.bin'), [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes((Join-Path $FixtureRoot 'b.bin'), [byte[]](4, 5, 6, 7, 8))
    $records = @(Get-DirectoryRecords -Path $FixtureRoot)
    $total = Get-FileRecordByteTotal -Records $records -Label 'real fixture feed'
    $realFeedRecords = @(Get-DirectoryRecords -Path $RealFeedPath)
    $realFeedTotal = Get-FileRecordByteTotal `
        -Records $realFeedRecords `
        -Label 'actual frozen offline feed'

    $missingBytesRejected = $false
    $missingBytes = [pscustomobject][ordered]@{
        path = [IO.Path]::GetFullPath((Join-Path $FixtureRoot 'missing.bin'))
        sha256 = ('A' * 64)
    }
    try {
        [void](Get-FileRecordByteTotal -Records @($missingBytes) -Label 'missing-bytes fixture')
    }
    catch {
        $missingBytesRejected = $true
    }

    $invalidByteTypesRejected = 0
    foreach ($invalidByteValue in @(
            $null,
            $true,
            '3',
            [double]3,
            [decimal]3,
            [uint64]3)) {
        $invalidBytes = [pscustomobject][ordered]@{
            path = [IO.Path]::GetFullPath((Join-Path $FixtureRoot 'invalid-bytes.bin'))
            bytes = $invalidByteValue
            sha256 = ('B' * 64)
        }
        try {
            [void](Get-FileRecordByteTotal `
                    -Records @($invalidBytes) `
                    -Label 'invalid-byte-type fixture')
        }
        catch {
            $invalidByteTypesRejected++
        }
    }

    $jsonRecord = [pscustomobject][ordered]@{
        path = [IO.Path]::GetFullPath((Join-Path $FixtureRoot 'json-roundtrip.bin'))
        bytes = [long]3
        sha256 = ('C' * 64)
    } | ConvertTo-Json -Compress | ConvertFrom-Json
    $jsonRoundTripTotal = Get-FileRecordByteTotal `
        -Records @($jsonRecord) `
        -Label 'JSON round-trip fixture'

    [pscustomobject]@{
        RecordCount = $records.Count
        RecordType = $records[0].GetType().FullName
        ByteTotal = $total
        RealFeedRecordCount = $realFeedRecords.Count
        RealFeedByteTotal = $realFeedTotal
        MissingBytesRejected = $missingBytesRejected
        InvalidByteTypesRejected = $invalidByteTypesRejected
        JsonRoundTripByteType = $jsonRecord.bytes.GetType().FullName
        JsonRoundTripByteTotal = $jsonRoundTripTotal
        FixtureRoot = $FixtureRoot
    }
} -ExecutorAst $executorAst `
    -FixtureRoot (Join-Path $compileEvidence 'file-record-fixture') `
    -RealFeedPath (Join-Path $root 'installer\.offline-feed')
Assert-True -Condition ($fileRecordFixtureResult.RecordCount -eq 2) `
    -Message 'production Get-DirectoryRecords did not return two real fixture records'
Assert-True -Condition (
    $fileRecordFixtureResult.RecordType -ceq 'System.Management.Automation.PSCustomObject') `
    -Message "production file record is not a PSCustomObject: $($fileRecordFixtureResult.RecordType)"
Assert-True -Condition ($fileRecordFixtureResult.ByteTotal -eq 8L) `
    -Message "production file-record byte aggregation was $($fileRecordFixtureResult.ByteTotal), expected 8"
Assert-True -Condition (
    $fileRecordFixtureResult.RealFeedRecordCount -eq 50 -and
    $fileRecordFixtureResult.RealFeedByteTotal -eq 194634460L) `
    -Message (
        "actual offline feed aggregate mismatch: " +
        "$($fileRecordFixtureResult.RealFeedRecordCount) files / " +
        "$($fileRecordFixtureResult.RealFeedByteTotal) bytes")
Assert-True -Condition $fileRecordFixtureResult.MissingBytesRejected `
    -Message 'production file-record schema accepted a record with no bytes property'
Assert-True -Condition ($fileRecordFixtureResult.InvalidByteTypesRejected -eq 6) `
    -Message (
        "production file-record schema rejected " +
        "$($fileRecordFixtureResult.InvalidByteTypesRejected) / 6 invalid byte types")
Assert-True -Condition (
    $fileRecordFixtureResult.JsonRoundTripByteType -ceq 'System.Int32' -and
    $fileRecordFixtureResult.JsonRoundTripByteTotal -eq 3L) `
    -Message (
        "JSON round-trip record mismatch: " +
        "$($fileRecordFixtureResult.JsonRoundTripByteType) / " +
        "$($fileRecordFixtureResult.JsonRoundTripByteTotal)")

$unsafeFixtures = [ordered]@{
    'ambient-msbuild-user-extensions' = $executorText.Replace(
        '-property:MSBuildUserExtensionsPath=$msbuildUserExtensions',
        '-property:MSBuildUserExtensionsPath=$env:MSBuildUserExtensionsPath')
    'non-suspended-create' = $executorText.Replace(
        'private const uint CREATE_SUSPENDED = 0x00000004;',
        'private const uint CREATE_SUSPENDED = 0;')
    'no-job-tree-termination' = $executorText.Replace(
        'bool jobTerminationRequested = TerminateJobObject(job, exitCode);',
        'bool jobTerminationRequested = TerminateProcess(process, exitCode);')
    'no-active-process-zero-wait' = $executorText.Replace(
        'if (WaitForJobEmpty(job, CLEANUP_WAIT_MILLISECONDS, context))',
        'if (true)')
    'overridable-or-longer-timeout' = $executorText.Replace(
        '$timeoutSeconds = 600',
        '$timeoutSeconds = 601')
    'manifest-approval-bypass' = $executorText.Replace(
        '$actualBaselineHash -cne $ApprovedManifestSha256',
        '$false')
    'retryable-attempt-marker' = $executorText.Replace(
        '[IO.FileMode]::CreateNew',
        '[IO.FileMode]::Create')
    'ancestor-packages-import' = $executorText.Replace(
        '-property:ImportDirectoryPackagesProps=false',
        '-property:ImportDirectoryPackagesProps=true')
    'ancestor-build-targets-import' = $executorText.Replace(
        '-property:ImportDirectoryBuildTargets=false',
        '-property:ImportDirectoryBuildTargets=true')
    'ambient-directory-build-props' = $executorText.Replace(
        '-property:DirectoryBuildPropsPath=$directoryBuildPropsPath',
        '-property:DirectoryBuildPropsPath=$env:DirectoryBuildPropsPath')
    'old-obj-import' = $executorText.Replace(
        '-property:ImportProjectExtensionProps=false',
        '-property:ImportProjectExtensionProps=true')
    'old-obj-target-import' = $executorText.Replace(
        '-property:ImportProjectExtensionTargets=false',
        '-property:ImportProjectExtensionTargets=true')
    'workload-resolver-enabled' = $executorText.Replace(
        '-property:MSBuildEnableWorkloadResolver=false',
        '-property:MSBuildEnableWorkloadResolver=true')
    'unfixed-sdk-resolution' = $executorText.Replace(
        "'exec',`n        `$msbuildDll,",
        "'msbuild',")
    'real-user-profile' = $executorText.Replace(
        'USERPROFILE = $isolatedUserProfile',
        'USERPROFILE = $env:USERPROFILE')
    'user-profile-tree-not-exact' = $executorText.Replace(
        "throw 'Isolated USERPROFILE directory tree is not exact.'",
        'return')
    'nuget-plugin-discovery-env-removed' = $executorText.Replace(
        'NUGET_NETCORE_PLUGIN_PATHS = $nugetNetCorePluginPaths',
        'NUGET_NETCORE_PLUGIN_PATHS = $env:NUGET_NETCORE_PLUGIN_PATHS')
    'nuget-plugin-postcheck-removed' = $executorText.Replace(
        'Assert-NuGetPluginDiscoveryIsolation',
        'Assert-DisabledNuGetPluginDiscoveryIsolation')
    'ordered-dictionary-file-record' = $executorText.Replace(
        '[pscustomobject][ordered]@{',
        '[ordered]@{')
    'missing-bytes-schema-bypass' = $executorText.Replace(
        'throw "$Label record is missing required property: $name"',
        'continue')
    'non-integer-bytes-schema-bypass' = $executorText.Replace(
        'throw "$Label record byte count is not an integer CLR value."',
        'continue')
    'legacy-measure-byte-aggregation' = $executorText.Replace(
        '$feedBytes = Get-FileRecordByteTotal -Records $feedRecords -Label ''offline feed''',
        '$feedBytes = [long](($feedRecords | Measure-Object bytes -Sum).Sum)')
    'process-enumeration' = $executorText + [Environment]::NewLine +
        '[Diagnostics.Process]::GetProcesses()' + [Environment]::NewLine
}

$unsafeResults = [Collections.Generic.List[object]]::new()
foreach ($fixture in $unsafeFixtures.GetEnumerator()) {
    Assert-True -Condition ($fixture.Value -cne $executorText) `
        -Message "unsafe fixture did not mutate source: $($fixture.Key)"
    $failures = @(Get-SafetyFailures -Text ([string]$fixture.Value))
    Assert-True -Condition ($failures.Count -gt 0) `
        -Message "unsafe fixture escaped the gate: $($fixture.Key)"
    $unsafeResults.Add([pscustomobject]@{
            Fixture = $fixture.Key
            Rejected = $true
            FailureCount = $failures.Count
        })
}

[pscustomobject]@{
    Result = 'RC48_R2_ONESHOT_STATIC_GATE_PASS'
    Executor = $executorPath
    ExecutorSha256 = (Get-FileHash -LiteralPath $executorPath -Algorithm SHA256).Hash
    NativeRunnerCompiled = $true
    AtomicMarkerReplayRejected = $replayRejected
    ExplicitPluginInjectionRejected = $isolationFixtureResult.ExplicitPluginRejected
    UserProfilePluginInjectionRejected = $isolationFixtureResult.UserProfilePluginRejected
    FileRecordType = $fileRecordFixtureResult.RecordType
    FileRecordByteTotal = $fileRecordFixtureResult.ByteTotal
    RealFeedRecordCount = $fileRecordFixtureResult.RealFeedRecordCount
    RealFeedByteTotal = $fileRecordFixtureResult.RealFeedByteTotal
    MissingBytesRecordRejected = $fileRecordFixtureResult.MissingBytesRejected
    InvalidByteTypesRejected = $fileRecordFixtureResult.InvalidByteTypesRejected
    JsonRoundTripByteType = $fileRecordFixtureResult.JsonRoundTripByteType
    JsonRoundTripByteTotal = $fileRecordFixtureResult.JsonRoundTripByteTotal
    UnsafeFixturesRejected = $unsafeResults.Count
    CompileEvidence = $compileEvidence
}
$unsafeResults
