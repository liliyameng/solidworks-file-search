$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$executorPath = Join-Path $PSScriptRoot 'Invoke-Rc48R3OneShotRestore.ps1'
$graphConfigGatePath = Join-Path $PSScriptRoot 'Test-Rc48R3RestoreGraphConfigStatic.ps1'
$testResultsRoot = [IO.Path]::GetFullPath((Join-Path $root 'TestResults')).TrimEnd('\')

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw "RC48_R3_ONESHOT_STATIC_GATE: $Message" }
}

function Get-OccurrenceCount {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle
    )
    ([regex]::Matches(
            $Text,
            [regex]::Escape($Needle),
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)).Count
}

function Get-EmbeddedSource {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$VariableName
    )
    $match = [regex]::Match(
        $Text,
        "(?s)\`$$([regex]::Escape($VariableName)) = @'\r?\n(?<source>.*?)\r?\n'@")
    Assert-True -Condition $match.Success -Message "embedded source missing: $VariableName"
    $match.Groups['source'].Value
}

function Get-FunctionAst {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Language.ScriptBlockAst]$Ast,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $matches = @($Ast.FindAll(
            {
                param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq $Name
            },
            $true))
    Assert-True -Condition ($matches.Count -eq 1) `
        -Message "expected exactly one executor function named $Name"
    $matches[0]
}

function Import-ExecutorFunctions {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Language.ScriptBlockAst]$Ast,
        [Parameter(Mandatory = $true)][string[]]$Names
    )
    foreach ($name in $Names) {
        $definition = Get-FunctionAst -Ast $Ast -Name $name
        $bodyText = $definition.Body.Extent.Text.Trim()
        Assert-True -Condition (
            $bodyText.StartsWith('{', [StringComparison]::Ordinal) -and
            $bodyText.EndsWith('}', [StringComparison]::Ordinal)) `
            -Message "executor function body is malformed: $name"
        $bodyText = $bodyText.Substring(1, $bodyText.Length - 2)
        Set-Item `
            -LiteralPath "Function:script:$name" `
            -Value ([ScriptBlock]::Create($bodyText))
    }
}

function Move-DirectoryAfterLeaseRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $lastError = $null
    for ($attempt = 1; $attempt -le 100; $attempt++) {
        try {
            [IO.Directory]::Move($Source, $Destination)
            return $attempt
        }
        catch [IO.IOException] {
            $lastError = $_.Exception
        }
        catch [UnauthorizedAccessException] {
            $lastError = $_.Exception
        }
        Start-Sleep -Milliseconds 20
    }
    throw "$Label remained non-renamable after lease release: $($lastError.Message)"
}

function Get-ExecutorSafetyFailures {
    param([Parameter(Mandatory = $true)][string]$Text)

    $failures = [Collections.Generic.List[string]]::new()
    function Require {
        param([string]$Needle, [string]$Label, [int]$Count = 1)
        $actual = Get-OccurrenceCount -Text $Text -Needle $Needle
        if ($actual -ne $Count) {
            $failures.Add("$Label count was $actual, expected $Count")
        }
    }
    function RequirePattern {
        param([string]$Pattern, [string]$Label)
        if ($Text -notmatch $Pattern) { $failures.Add("$Label missing") }
    }
    function RejectPattern {
        param([string]$Pattern, [string]$Label)
        if ($Text -match $Pattern) { $failures.Add("$Label present") }
    }

    Require -Needle '$timeoutSeconds = 600' -Label 'fixed hard deadline'
    Require -Needle '$r3 = Join-Path $root ''.packages-rc48-r3''' -Label 'fresh R3 package root'
    Require -Needle '$msbuildDll = ''C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll''' `
        -Label 'pinned MSBuild assembly'
    Require -Needle '$msbuildExtensionsPath = ''C:\Program Files\dotnet\sdk\8.0.421''' `
        -Label 'pinned MSBuild extension root'
    Require -Needle '$toolchainSelectionSetId = ''dotnet-sdk-8.0.421-netcore-8.0.27-win-x64-v1''' `
        -Label 'built-in toolchain selection set'
    Require -Needle '$toolchainFileLimit = 5000' -Label 'toolchain file cap'
    Require -Needle '$toolchainDirectoryLimit = 5000' -Label 'toolchain directory cap'
    Require -Needle '$toolchainEntryLimit = 10000' -Label 'toolchain entry cap'
    Require -Needle '$toolchainByteLimit = 600MB' -Label 'toolchain byte cap'
    Require -Needle '$toolchainDepthLimit = 16' -Label 'toolchain depth cap'
    Require -Needle '$approvedInputFileLimit = 20000' -Label 'approved input file cap'
    Require -Needle '$approvedInputDirectoryLimit = 10000' `
        -Label 'approved input directory cap'
    Require -Needle '$approvedInputByteLimit = 2GB' -Label 'approved input byte cap'
    RequirePattern `
        -Pattern '(?s)\$toolchainFileRoots\s*=\s*@\(\s*''C:\\Program Files\\dotnet\\dotnet\.exe''\s*\)\s*\$toolchainDirectoryRoots\s*=\s*@\(\s*''C:\\Program Files\\dotnet\\sdk\\8\.0\.421'',\s*''C:\\Program Files\\dotnet\\host\\fxr'',\s*''C:\\Program Files\\dotnet\\shared\\Microsoft\.NETCore\.App\\8\.0\.27'',\s*''C:\\Program Files\\dotnet\\packs\\Microsoft\.NETCore\.App\.Ref\\8\.0\.27'',\s*''C:\\Program Files\\dotnet\\packs\\Microsoft\.WindowsDesktop\.App\.Ref\\8\.0\.27'',\s*''C:\\Program Files\\dotnet\\packs\\Microsoft\.NETCore\.App\.Host\.win-x64\\8\.0\.27''\s*\)' `
        -Label 'exact one-file six-directory built-in toolchain roots'
    Require -Needle 'DOTNET_ROLL_FORWARD = ''Disable''' -Label 'disabled runtime roll-forward'
    Require -Needle '$Environment.Count -ne 34 -or $ExpectedNames.Count -ne 34' `
        -Label 'exact environment schema'
    Require -Needle '$Environment.Count -ne 34' -Label 'native block environment count' -Count 2
    Require -Needle 'schema = 3' -Label 'schema 3 evidence' -Count 9

    foreach ($argument in @(
            "'-target:Restore'",
            "'-noAutoResponse'",
            "'-nodeReuse:false'",
            "'-maxCpuCount:1'",
            '"-property:MSBuildExtensionsPath=$msbuildExtensionsPath"',
            '"-property:MSBuildUserExtensionsPath=$msbuildUserExtensions"',
            "'-property:DirectoryBuildTargetsPath='",
            "'-property:DirectoryPackagesPropsPath='",
            "'-property:ImportDirectoryBuildTargets=false'",
            "'-property:ImportDirectoryPackagesProps=false'",
            "'-property:ImportProjectExtensionProps=false'",
            "'-property:ImportProjectExtensionTargets=false'",
            "'-property:ExcludeRestorePackageImports=true'",
            "'-property:MSBuildEnableWorkloadResolver=false'",
            "'-property:DisableImplicitNuGetFallbackFolder=true'",
            "'-property:RestoreNoCache=true'",
            "'-property:RestoreDisableParallel=true'",
            "'-property:RestoreForceEvaluate=true'",
            "'-property:RestoreAdditionalProjectSources='",
            "'-property:RestoreFallbackFolders='",
            "'-property:RestoreAdditionalProjectFallbackFolders='",
            "'-property:RestoreAdditionalProjectFallbackFoldersExcludes='",
            "'-property:RestoreRepositoryPath='",
            "'-property:RestoreRepositoryPathOverride='",
            "'-property:RestoreRootConfigDirectory='")) {
        Require -Needle $argument -Label "fixed restore argument $argument"
    }
    foreach ($flag in @(
            'ImportUserLocationsByWildcardBeforeMicrosoftCommonProps',
            'ImportUserLocationsByWildcardAfterMicrosoftCommonProps',
            'ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets',
            'ImportUserLocationsByWildcardAfterMicrosoftCommonTargets',
            'ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets',
            'ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets')) {
        Require -Needle "-property:$flag=false" -Label "$flag disabled"
    }

    Require -Needle 'Add-Type -TypeDefinition $evidenceRootLeaseSource -Language CSharp' `
        -Label 'bootstrap evidence lease compilation'
    Require -Needle '[Rc48EvidenceDirectoryLease]::CreateNew($evidence)' `
        -Label 'atomic evidence root creation'
    Require -Needle '[Rc48EvidenceDirectoryLease]::Acquire($evidence)' `
        -Label 'direct evidence root acquisition'
    Require -Needle '$evidenceLease.AnchorDirectories([string[]]@($requiredDirectory))' `
        -Label 'sequential child anchoring'
    RequirePattern `
        -Pattern '(?s)\$sentinelNames\s*=\s*@\(\s*''8\.0\.421\.dotnetFirstUseSentinel''\s*,\s*''8\.0\.421\.aspNetCertificateSentinel''\s*\)' `
        -Label 'exact two DOTNET_CLI_HOME sentinel names'
    Require -Needle '$script:freezeCreatedSentinelPaths = [Collections.Generic.HashSet[string]]::new(' `
        -Label 'current Freeze sentinel creation set'
    Require -Needle '[IO.FileShare]::ReadWrite' `
        -Label 'one held-sentinel shared-read exception' -Count 1
    RequirePattern `
        -Pattern '(?s)function Get-FileRecord\s*\{.*?\[IO\.FileShare\]::Read,\s*131072,\s*\[IO\.FileOptions\]::SequentialScan\).*?function Get-HeldSentinelRecord' `
        -Label 'ordinary file records retain strict read sharing'
    RequirePattern `
        -Pattern '(?s)\$expectedSentinelPaths\s*=\s*@\(\s*\[IO\.Path\]::GetFullPath\(\(Join-Path \$exactSentinelDirectory ''8\.0\.421\.dotnetFirstUseSentinel''\)\),\s*\[IO\.Path\]::GetFullPath\(\(Join-Path \$exactSentinelDirectory ''8\.0\.421\.aspNetCertificateSentinel''\)\)\s*\)' `
        -Label 'held shared-read exception has exactly two canonical paths'
    RequirePattern `
        -Pattern '(?s)function Get-HeldSentinelRecord\s*\{.*?if \(\$Mode -cne ''Freeze''\).*?\$exactEvidenceRoot = \[IO\.Path\]::GetFullPath\(\$evidence\)\.TrimEnd\(''\\''\).*?\$exactSentinelDirectory = \[IO\.Path\]::GetFullPath\(\$cliDotnet\)\.TrimEnd\(''\\''\).*?\$exactSentinelDirectory\.StartsWith\(\s*\$exactEvidenceRoot \+ ''\\''.*?\$script:freezeCreatedSentinelPaths\.Count -ne 2.*?\$script:freezeCreatedSentinelPaths\.Contains\(\$full\).*?\$script:evidenceLease\.Validate\(\).*?\[IO\.FileShare\]::ReadWrite.*?function Get-BytesSha256' `
        -Label 'held shared-read exception is Freeze-only and evidence-root constrained'
    RequirePattern `
        -Pattern '(?s)foreach \(\$sentinel in \$sentinelNames\)\s*\{.*?Write-NewEmptyEvidenceFile -Path \$sentinelPath.*?\$script:freezeCreatedSentinelPaths\.Add\(\$sentinelPath\).*?\}\s*if \(\$script:freezeCreatedSentinelPaths\.Count -ne 2\).*?\$fixedRecords\s*=\s*@\(.*?if \(\$script:freezeCreatedSentinelPaths\.Contains\(\$fixedInput\)\)\s*\{\s*Get-HeldSentinelRecord -Path \$fixedInput\s*\}\s*else\s*\{\s*Get-FileRecord -Path \$fixedInput' `
        -Label 'only current exact sentinels bypass strict fixed-input reads'
    Require -Needle '[byte[]]$baselineBytes = $evidenceLease.ReadExactFileBytes(' `
        -Label 'same-handle baseline bytes'
    Require -Needle '[byte[]]$commandBytes = $evidenceLease.ReadExactFileBytes(' `
        -Label 'same-handle command bytes'
    Require -Needle '$actualBaselineHash = Get-BytesSha256 -Bytes $baselineBytes' `
        -Label 'baseline SHA from held bytes'
    Require -Needle '$baselineJson = $utf8Strict.GetString($baselineBytes)' `
        -Label 'strict baseline UTF-8'
    Require -Needle '$commandEvidenceJson = $utf8Strict.GetString($commandBytes)' `
        -Label 'strict command UTF-8'
    Require -Needle 'Assert-CanonicalJsonBytes -Actual $baselineBytes -Value $baseline' `
        -Label 'canonical baseline bytes'
    Require -Needle 'Assert-CanonicalJsonBytes -Actual $commandBytes -Value $commandEvidence' `
        -Label 'canonical command bytes'
    Require -Needle '$evidenceLease.WriteNewFile(' -Label 'lease-bound output creation' -Count 2
    Require -Needle '$evidenceLease.CreateAnchoredOutput($binlogPath, ''restore.binlog'')' `
        -Label 'parent binlog pre-anchor'
    Require -Needle '$evidenceLease.CreateAnchoredOutput($consoleLogPath, ''restore.console.log'')' `
        -Label 'parent console pre-anchor'
    Require -Needle '$evidenceLease.FlushAnchoredOutput(' `
        -Label 'same-object logger flush' -Count 2
    Require -Needle 'Write-NewJsonEvidence -Path $attemptPath' -Label 'atomic attempt evidence'
    Require -Needle 'Write-NewJsonEvidence -Path $resultPath' `
        -Label 'atomic result evidence' -Count 4
    Require -Needle 'Write-NewJsonEvidence -Path $failurePath' `
        -Label 'atomic failure evidence' -Count 3
    Require -Needle 'Write-NewJsonEvidence -Path $toolchainAfterPath' `
        -Label 'toolchain post-evidence'
    Require -Needle 'Assert-ToolchainSnapshotEqual `' `
        -Label 'toolchain exact comparison' -Count 3
    Require -Needle '$script:approvedInputLease = Acquire-ApprovedInputLease `' `
        -Label 'approved input lifetime lease acquisition'
    Require -Needle '$approvedInputLease.Validate()' `
        -Label 'approved input lifetime validation' -Count 4
    Require -Needle '$approvedInputLease.Dispose()' `
        -Label 'approved input lifetime release'
    Require -Needle 'Add-Type -TypeDefinition $nativeRunnerSource -Language CSharp' `
        -Label 'native runner compilation'
    Require -Needle '[Rc48SuspendedJobRunner]::Run(' -Label 'single native launch'
    Require -Needle '$env:TEMP = $temp' -Label 'evidence TEMP activation'
    Require -Needle '$env:TMP = $temp' -Label 'evidence TMP activation'
    Require -Needle '$env:TEMP = $originalTemp' -Label 'ambient TEMP restoration'
    Require -Needle '$env:TMP = $originalTmp' -Label 'ambient TMP restoration'
    Require -Needle '$evidenceLease.Dispose()' -Label 'single root lease disposal'

    RequirePattern -Pattern '(?s)\$requiredEvidenceDirectories\s*=\s*@\(\s*\$cliHome,\s*\$cliDotnet,\s*\$temp,\s*\$httpCache,\s*\$nugetScratch,\s*\$pluginCache,\s*\$nugetPluginPaths,\s*\$nugetNetCorePluginPaths,\s*\$nugetNetFxPluginPaths,\s*\$msbuildUserExtensions,\s*\$isolatedUserProfile,\s*\(Join-Path \$isolatedUserProfile ''AppData''\),\s*\$isolatedLocalAppData,\s*\$isolatedRoamingAppData\s*\)' `
        -Label 'exact ordered child directory set'
    RequirePattern -Pattern '(?s)foreach \(\$requiredDirectory in \$requiredEvidenceDirectories\).*?\$evidenceLease\.AnchorDirectories\(\[string\[\]\]@\(\$requiredDirectory\)\)' `
        -Label 'child create-and-anchor loop'
    RequirePattern -Pattern '(?s)function Get-BoundedToolchainSnapshot.*?SearchOption\]::TopDirectoryOnly.*?FILEAttributes\]::ReparsePoint.*?Sort-RecordsByOrdinalPath' `
        -Label 'bounded non-reparse ordinal toolchain traversal'
    RequirePattern -Pattern '(?s)function Get-BoundedToolchainSnapshot.*?\$directories\.Count -gt \$toolchainDirectoryLimit.*?\(\$files\.Count \+ \$directories\.Count\) -gt \$toolchainEntryLimit' `
        -Label 'toolchain directory and total-entry runtime caps'
    RequirePattern -Pattern '(?s)function Assert-ToolchainSnapshotSchema.*?top-level schema is not exact and ordered.*?limit schema is not exact and ordered.*?duplicate directory path.*?directory topology is not ordinally sorted.*?duplicate file path.*?files are not ordinally sorted.*?directoryCount.*?entryCount' `
        -Label 'strict toolchain schema'
    RequirePattern -Pattern '(?s)function Acquire-ApprovedInputLease.*?Get-UniqueApprovedInputRecords.*?Get-ApprovedInputDirectorySets.*?\[Rc48ApprovedInputLease\]::Acquire' `
        -Label 'bounded approved input lifetime lease graph'
    Require -Needle 'Assert-UserProfileIsolation -AllowPostRestoreNuGetArtifacts' `
        -Label 'final postrestore profile allowlist switch'
    RequirePattern -Pattern '(?m)^\s*Assert-UserProfileIsolation\s*$' `
        -Label 'parameterless prelaunch profile isolation call'
    Require -Needle '[IO.DirectoryInfo]::new($currentDirectory).GetFileSystemInfos()' `
        -Label 'profile topology enumeration without a wildcard'
    Require -Needle 'Isolated USERPROFILE contains a reparse point: $fullEntry' `
        -Label 'profile topology reparse rejection'
    RequirePattern `
        -Pattern '(?s)function Assert-UserProfileIsolation\s*\{\s*param\(\[switch\]\$AllowPostRestoreNuGetArtifacts\)\s*if \(\$AllowPostRestoreNuGetArtifacts\) \{\s*\$expectedProfileDirectories = @\(\s*\[IO\.Path\]::GetFullPath\(\(Join-Path \$isolatedUserProfile ''AppData''\)\),\s*\[IO\.Path\]::GetFullPath\(\$isolatedLocalAppData\),\s*\[IO\.Path\]::GetFullPath\(\(Join-Path \$isolatedLocalAppData ''NuGet''\)\),\s*\[IO\.Path\]::GetFullPath\(\(Join-Path \$isolatedLocalAppData ''NuGet\\Migrations''\)\),\s*\[IO\.Path\]::GetFullPath\(\$isolatedRoamingAppData\),\s*\[IO\.Path\]::GetFullPath\(\(Join-Path \$isolatedRoamingAppData ''NuGet''\)\)\s*\) \| Sort-Object\s*\$expectedProfileFiles = @\(\s*\[pscustomobject\]\[ordered\]@\{\s*path = \[IO\.Path\]::GetFullPath\(\(Join-Path \$isolatedLocalAppData ''NuGet\\Migrations\\1''\)\)\s*bytes = \[long\]0\s*sha256 = ''E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855''\s*\},\s*\[pscustomobject\]\[ordered\]@\{\s*path = \[IO\.Path\]::GetFullPath\(\(Join-Path \$isolatedRoamingAppData ''NuGet\\NuGet\.Config''\)\)\s*bytes = \[long\]210\s*sha256 = ''3BDB050540ECDD6FE717CBB9028C0FF9E2AF5E1544AFA71B6BADE80140C7A236''\s*\}\s*\)' `
        -Label 'exact postrestore profile NuGet directory and file records'
    RequirePattern `
        -Pattern '(?s)else \{\s*\$expectedProfileDirectories = @\(\s*\[IO\.Path\]::GetFullPath\(\(Join-Path \$isolatedUserProfile ''AppData''\)\),\s*\[IO\.Path\]::GetFullPath\(\$isolatedLocalAppData\),\s*\[IO\.Path\]::GetFullPath\(\$isolatedRoamingAppData\)\s*\) \| Sort-Object\s*\$expectedProfileFiles = @\(\)\s*\}' `
        -Label 'prelaunch profile tree and file set remain exact and empty'
    RequirePattern `
        -Pattern '(?s)function Assert-UserProfileIsolation.*?Assert-RecordSetEqual.*?-Expected \$expectedProfileFiles.*?-Actual @\(\$actualProfileFiles\).*?-Label ''isolated USERPROFILE''' `
        -Label 'profile files use exact record-set comparison'
    Require -Needle 'Assert-ExactOrdinalProfilePaths' `
        -Label 'profile raw ordinal path gate declaration and calls' -Count 3
    RequirePattern `
        -Pattern '(?s)function Assert-ExactOrdinalProfilePaths\s*\{.*?\[Collections\.Generic\.HashSet\[string\]\]::new\(\s*\[StringComparer\]::OrdinalIgnoreCase\)\s*foreach \(\$actualPath in \$Actual\) \{\s*if \(-not \$actualOrdinalIgnoreCasePaths\.Add\(\$actualPath\)\) \{\s*throw "\$Label contains a case-collision path: \$actualPath"\s*\}\s*\}\s*\[Array\]::Sort\(\$Expected, \[StringComparer\]::Ordinal\)\s*\[Array\]::Sort\(\$Actual, \[StringComparer\]::Ordinal\)\s*if \(\$Actual\.Count -ne \$Expected\.Count\) \{.*?for \(\$index = 0; \$index -lt \$Expected\.Count; \$index\+\+\) \{\s*if \(-not \$Actual\[\$index\]\.Equals\(\s*\$Expected\[\$index\],\s*\[StringComparison\]::Ordinal\)\)' `
        -Label 'profile raw path gate rejects case collisions and compares count and paths ordinally'
    RequirePattern `
        -Pattern '(?s)function Assert-UserProfileIsolation.*?Assert-ExactOrdinalProfilePaths.*?-Expected \(\[string\[\]\]\$expectedProfileDirectories\).*?-Actual \(\[string\[\]\]@\(\$actualProfileDirectories\)\).*?-Label ''isolated USERPROFILE directory paths''.*?\$expectedProfileFilePaths = \[string\[\]\]@\(\s*\$expectedProfileFiles \| ForEach-Object \{ \[string\]\$_\.path \}\).*?\$actualProfileFilePaths = \[string\[\]\]@\(\s*\$actualProfileFiles \| ForEach-Object \{ \[string\]\$_\.path \}\).*?Assert-ExactOrdinalProfilePaths.*?-Expected \$expectedProfileFilePaths.*?-Actual \$actualProfileFilePaths.*?-Label ''isolated USERPROFILE file paths''.*?Assert-RecordSetEqual' `
        -Label 'profile raw directory and file paths are gated before the case-insensitive record map'
    RejectPattern `
        -Pattern '(?is)function Assert-ExactOrdinalProfilePaths\s*\{.*?(?:\*|\bStartsWith\s*\(|\b(?:continue|skip|return)\b).*?function Get-UniqueApprovedInputRecords' `
        -Label 'profile raw ordinal path gate wildcard, prefix, or skip'

    RejectPattern -Pattern '(?i)\bdotnet\s+restore\b|Start-Process|Process(?:\]::|\.)Start|Process(?:\]::|\.)GetProcesses|Process(?:\]::|\.)GetProcessById|\.Kill\s*\(' `
        -Label 'alternate process or restore launcher'
    RejectPattern -Pattern '\bOpenProcess\s*\(|CreateToolhelp32Snapshot|Process32First|Process32Next|EnumProcesses' `
        -Label 'PID reopen or process enumeration'
    RejectPattern -Pattern '(?i)https?://|\\\.nuget\\packages|\.packages-rc48-r2|rc48-r2-oneshot-restore' `
        -Label 'network, user cache, or retired R2 path'
    RejectPattern -Pattern '(?m)^\s*\[IO\.File\]::WriteAll(?:Text|Bytes)\(' `
        -Label 'replaceable parent evidence write'
    RejectPattern -Pattern '\[IO\.FileMode\]::(?:Create|OpenOrCreate)\b' `
        -Label 'overwriting parent evidence mode'
    RejectPattern -Pattern 'Get-FileHash\s+-LiteralPath\s+\$baselinePath|Get-Content\s+-LiteralPath\s+\$baselinePath|Get-FileHash\s+-LiteralPath\s+\$commandPath|Get-Content\s+-LiteralPath\s+\$commandPath' `
        -Label 'double-open approval evidence'
    RejectPattern -Pattern 'FILE_SHARE_DELETE' -Label 'delete sharing'

    $bootstrapIndex = $Text.IndexOf(
        'Add-Type -TypeDefinition $evidenceRootLeaseSource -Language CSharp',
        [StringComparison]::Ordinal)
    $createIndex = $Text.IndexOf(
        '[Rc48EvidenceDirectoryLease]::CreateNew($evidence)',
        [StringComparison]::Ordinal)
    $acquireIndex = $Text.IndexOf(
        '[Rc48EvidenceDirectoryLease]::Acquire($evidence)',
        [StringComparison]::Ordinal)
    $childrenIndex = $Text.IndexOf(
        '$requiredEvidenceDirectories = @(',
        [StringComparison]::Ordinal)
    $baselineIndex = $Text.IndexOf(
        '[byte[]]$baselineBytes = $evidenceLease.ReadExactFileBytes(',
        [StringComparison]::Ordinal)
    $commandIndex = $Text.IndexOf(
        '[byte[]]$commandBytes = $evidenceLease.ReadExactFileBytes(',
        [StringComparison]::Ordinal)
    $inputLeaseIndex = $Text.IndexOf(
        '$script:approvedInputLease = Acquire-ApprovedInputLease `',
        [StringComparison]::Ordinal)
    $nativeIndex = $Text.IndexOf(
        'Add-Type -TypeDefinition $nativeRunnerSource -Language CSharp',
        [StringComparison]::Ordinal)
    $binlogAnchorIndex = $Text.IndexOf(
        '$evidenceLease.CreateAnchoredOutput($binlogPath, ''restore.binlog'')',
        [StringComparison]::Ordinal)
    $tempIndex = $Text.IndexOf('$env:TEMP = $temp', [StringComparison]::Ordinal)
    $attemptIndex = $Text.IndexOf(
        'Write-NewJsonEvidence -Path $attemptPath',
        [StringComparison]::Ordinal)
    $runIndex = $Text.IndexOf('[Rc48SuspendedJobRunner]::Run(', [StringComparison]::Ordinal)
    $flushIndex = $Text.IndexOf(
        '$evidenceLease.FlushAnchoredOutput(',
        [StringComparison]::Ordinal)
    $restoreTempIndex = $Text.LastIndexOf(
        '$env:TEMP = $originalTemp',
        [StringComparison]::Ordinal)
    $disposeIndex = $Text.LastIndexOf(
        '$evidenceLease.Dispose()',
        [StringComparison]::Ordinal)
    $inputDisposeIndex = $Text.LastIndexOf(
        '$approvedInputLease.Dispose()',
        [StringComparison]::Ordinal)
    if (-not (
            $bootstrapIndex -ge 0 -and
            $createIndex -gt $bootstrapIndex -and
            $acquireIndex -gt $createIndex -and
            $childrenIndex -gt $acquireIndex -and
            $baselineIndex -gt $childrenIndex -and
            $commandIndex -gt $baselineIndex -and
            $inputLeaseIndex -gt $commandIndex -and
            $nativeIndex -gt $inputLeaseIndex -and
            $binlogAnchorIndex -gt $nativeIndex -and
            $tempIndex -gt $binlogAnchorIndex -and
            $attemptIndex -gt $tempIndex -and
            $runIndex -gt $attemptIndex -and
            $flushIndex -gt $runIndex -and
            $restoreTempIndex -gt $flushIndex -and
            $inputDisposeIndex -gt $restoreTempIndex -and
            $disposeIndex -gt $restoreTempIndex)) {
        $failures.Add('root/child/read/native/output/TEMP/cleanup order is unsafe')
    }
    if ($bootstrapIndex -gt 0) {
        $prefix = $Text.Substring(0, $bootstrapIndex)
        foreach ($forbiddenPrefix in @(
                'Test-Path -LiteralPath $evidence',
                'Get-Content -LiteralPath $baselinePath',
                'Get-FileHash -LiteralPath $baselinePath',
                '[IO.File]::ReadAllBytes($baselinePath)',
                '[IO.Directory]::CreateDirectory($evidence)',
                '[IO.File]::WriteAllText($baselinePath')) {
            if ($prefix.IndexOf($forbiddenPrefix, [StringComparison]::Ordinal) -ge 0) {
                $failures.Add(
                    "evidence path is accessed before the root lease: $forbiddenPrefix")
            }
        }
    }
    return @($failures)
}

function Get-NativeSourceFailures {
    param(
        [Parameter(Mandatory = $true)][string]$LeaseSource,
        [Parameter(Mandatory = $true)][string]$NativeSource
    )
    $failures = [Collections.Generic.List[string]]::new()
    function Need {
        param([string]$Text, [string]$Needle, [string]$Label, [int]$Count = 1)
        $actual = Get-OccurrenceCount -Text $Text -Needle $Needle
        if ($actual -ne $Count) {
            $failures.Add("$Label count was $actual, expected $Count")
        }
    }
    function NeedPattern {
        param([string]$Text, [string]$Pattern, [string]$Label)
        if ($Text -notmatch $Pattern) { $failures.Add("$Label missing") }
    }

    Need -Text $LeaseSource -Needle 'FILE_FLAG_OPEN_REPARSE_POINT' `
        -Label 'evidence and approved-input OPEN_REPARSE_POINT' -Count 4
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)OpenExact\(.*?flags \| FILE_FLAG_OPEN_REPARSE_POINT.*?AssertObjectKind\(openedHandle.*?AssertExactFinalPath\(openedHandle' `
        -Label 'exact non-reparse handle opener'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public static Rc48EvidenceDirectoryLease CreateNew\(.*?CreateDirectory\(full.*?return Acquire\(full\)' `
        -Label 'atomic unique root creation'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public void AnchorDirectories\(.*?RequireAnchoredParent.*?OpenExact\(.*?FILE_SHARE_READ \| FILE_SHARE_WRITE,\s*OPEN_EXISTING.*?FILE_FLAG_BACKUP_SEMANTICS.*?heldObjects\.Add' `
        -Label 'held child directory lease'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public byte\[\] ReadExactFileBytes\(.*?GENERIC_READ,\s*FILE_SHARE_READ,\s*OPEN_EXISTING.*?new SafeFileHandle\(input, false\).*?heldObjects\.Add\(new HeldObject\(input' `
        -Label 'same-handle read and lifetime hold'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public void WriteNewFile\(.*?GENERIC_READ \| GENERIC_WRITE,\s*FILE_SHARE_READ,\s*CREATE_NEW.*?stream\.Write\(payload.*?stream\.Flush\(true\).*?heldObjects\.Add' `
        -Label 'CREATE_NEW durable held output'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public void CreateAnchoredOutput\(.*?CREATE_NEW.*?GetIdentity.*?heldObjects\.Add.*?anchoredOutputs\.Add' `
        -Label 'child-owned output pre-anchor'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public long FlushAnchoredOutput\(.*?OPEN_EXISTING.*?SameIdentity.*?FlushFileBuffers.*?GetFileSizeEx' `
        -Label 'same-object child output flush'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)AssertObjectKind\(.*?FILE_ATTRIBUTE_REPARSE_POINT.*?NumberOfLinks != 1' `
        -Label 'reparse and hardlink rejection'
    if ($LeaseSource -match 'FILE_SHARE_DELETE') {
        $failures.Add('evidence lease enables delete sharing')
    }
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public sealed class Rc48ApprovedInputLease.*?MaximumFiles = 20000.*?MaximumDirectories = 10000.*?MaximumBytes = 2147483648L' `
        -Label 'approved input lease built-in caps'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)private static IntPtr OpenExact\(.*?uint shareMode = directory && !immutableDirectory\s*\? FILE_SHARE_READ \| FILE_SHARE_WRITE\s*:\s*FILE_SHARE_READ;.*?flags \| FILE_FLAG_OPEN_REPARSE_POINT' `
        -Label 'approved input immutable/stable share policy'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)private static string ComputeSha256\(IntPtr current.*?new SafeFileHandle\(current, false\).*?sha\.ComputeHash\(stream\).*?stream\.Position != stream\.Length' `
        -Label 'approved input same-handle complete hashing'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)private void HoldFile\(.*?AssertExactObject\(.*?ComputeSha256\(.*?actualBytes != expectedBytes.*?actualSha256.*?heldInputs\.Add' `
        -Label 'approved input same-handle acquire and lifetime hold'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public static Rc48ApprovedInputLease Acquire\(.*?RequireStrictlySortedUnique.*?totalBytes > MaximumBytes.*?HoldDirectory\(path, true\).*?HoldDirectory\(path, false\).*?HoldFile\(.*?lease\.Validate\(\)' `
        -Label 'approved input bounded acquisition'
    NeedPattern -Text $LeaseSource `
        -Pattern '(?s)public void Validate\(\).*?AssertExactObject\(.*?VolumeSerialNumber.*?ComputeSha256\(.*?ExpectedSha256' `
        -Label 'approved input lifetime revalidation'

    Need -Text $NativeSource -Needle 'private const uint CREATE_SUSPENDED = 0x00000004;' `
        -Label 'suspended process creation'
    Need -Text $NativeSource -Needle 'JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE' `
        -Label 'kill-on-close job' -Count 2
    NeedPattern -Text $NativeSource `
        -Pattern '(?s)private static IntPtr CreateInheritedOutput\(.*?GENERIC_READ \| GENERIC_WRITE,\s*FILE_SHARE_READ,\s*ref attributes,\s*CREATE_NEW,\s*FILE_ATTRIBUTE_NORMAL \| FILE_FLAG_OPEN_REPARSE_POINT.*?AssertExactFinalPath.*?AssertRegularSingleLinkFile' `
        -Label 'native CREATE_NEW output anchoring'
    NeedPattern -Text $NativeSource `
        -Pattern '(?s)DangerousAddRef\(.*?DangerousGetHandle\(\).*?AssertExactFinalPath\(.*?CreateInheritedOutput\(.*?restore\.stdout\.log.*?CreateInheritedOutput\(.*?restore\.stderr\.log' `
        -Label 'same root lease spans native outputs'
    NeedPattern -Text $NativeSource `
        -Pattern '(?s)AssignProcessToJobObject\(job, process\).*?assigned = true;.*?ResumeThread\(thread\)' `
        -Label 'job assignment before resume'
    NeedPattern -Text $NativeSource `
        -Pattern '(?s)if \(assigned\).*?TerminateJobObject\(job, exitCode\).*?TerminateProcess\(process, exitCode\)' `
        -Label 'capability-bound cleanup'
    if ($NativeSource -match '\bOpenProcess\s*\(|CreateToolhelp32Snapshot|Process32First|Process32Next|EnumProcesses') {
        $failures.Add('native runner reopens or enumerates processes')
    }
    if ($NativeSource -match 'FILE_SHARE_DELETE') {
        $failures.Add('native output enables delete sharing')
    }

    $cleanupStart = $NativeSource.LastIndexOf(
        'Exception cleanupFailure = null;',
        [StringComparison]::Ordinal)
    if ($cleanupStart -lt 0) {
        $failures.Add('first-error cleanup accumulator missing')
    }
    else {
        $cleanup = $NativeSource.Substring($cleanupStart)
        $orderedTokens = @(
            'TerminateOwnedProcessTree(',
            'CloseHandle(job)',
            'CloseHandle(thread)',
            'CloseHandle(process)',
            'FlushFileBuffers(standardOutput)',
            'FlushFileBuffers(standardError)',
            'DeleteProcThreadAttributeList(attributeList)',
            'Marshal.FreeHGlobal(attributeValues)',
            'CloseHandle(standardInput)',
            'CloseHandle(standardOutput)',
            'CloseHandle(standardError)',
            'Marshal.FreeHGlobal(environmentBlock)',
            'evidenceDirectoryLease.DangerousRelease()',
            'if (cleanupFailure != null)',
            'throw cleanupFailure;')
        $previous = -1
        foreach ($token in $orderedTokens) {
            $current = $cleanup.IndexOf($token, [StringComparison]::Ordinal)
            if ($current -le $previous) {
                $failures.Add("native cleanup token missing or out of order: $token")
                break
            }
            $previous = $current
        }
        if ((Get-OccurrenceCount -Text $cleanup -Needle 'throw cleanupFailure;') -ne 1) {
            $failures.Add('native cleanup must throw the first failure exactly once at the end')
        }
        if ((Get-OccurrenceCount -Text $cleanup `
                    -Needle 'if (cleanupFailure == null) cleanupFailure = cleanupError;') -ne 4 -or
            (Get-OccurrenceCount -Text $cleanup `
                    -Needle 'cleanupFailure = cleanupError;') -ne 5) {
            $failures.Add('native cleanup does not preserve the first managed cleanup failure')
        }
        foreach ($operationPattern in @(
                'if \(job != IntPtr\.Zero && !CloseHandle\(job\) && cleanupFailure == null\)',
                'if \(thread != IntPtr\.Zero && !CloseHandle\(thread\) && cleanupFailure == null\)',
                'if \(process != IntPtr\.Zero && !CloseHandle\(process\) && cleanupFailure == null\)',
                '(?s)if \(standardOutput != IntPtr\.Zero &&\s*standardOutput != InvalidHandleValue &&\s*!FlushFileBuffers\(standardOutput\) &&\s*cleanupFailure == null\)',
                '(?s)if \(standardError != IntPtr\.Zero &&\s*standardError != InvalidHandleValue &&\s*!FlushFileBuffers\(standardError\) &&\s*cleanupFailure == null\)',
                '(?s)if \(standardInput != IntPtr\.Zero &&\s*standardInput != InvalidHandleValue &&\s*!CloseHandle\(standardInput\) &&\s*cleanupFailure == null\)',
                '(?s)if \(standardOutput != IntPtr\.Zero &&\s*standardOutput != InvalidHandleValue &&\s*!CloseHandle\(standardOutput\) &&\s*cleanupFailure == null\)',
                '(?s)if \(standardError != IntPtr\.Zero &&\s*standardError != InvalidHandleValue &&\s*!CloseHandle\(standardError\) &&\s*cleanupFailure == null\)')) {
            if ($cleanup -notmatch $operationPattern) {
                $failures.Add(
                    "native cleanup operation is not attempted before first-error selection: $operationPattern")
            }
        }
        if ($cleanup -match
            'cleanupFailure == null\s*&&\s*!(?:CloseHandle|FlushFileBuffers)') {
            $failures.Add('native cleanup short-circuits an operation after the first failure')
        }
    }
    return @($failures)
}

Assert-True -Condition (Test-Path -LiteralPath $executorPath -PathType Leaf) `
    -Message "executor missing: $executorPath"
Assert-True -Condition (Test-Path -LiteralPath $graphConfigGatePath -PathType Leaf) `
    -Message "graph/config static gate missing: $graphConfigGatePath"

$executorText = Get-Content -LiteralPath $executorPath -Raw -Encoding UTF8
$tokens = $null
$parseErrors = $null
$executorAst = [Management.Automation.Language.Parser]::ParseFile(
    $executorPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True -Condition ($parseErrors.Count -eq 0) `
    -Message ('executor parse failed: ' + (($parseErrors | ForEach-Object Message) -join '; '))

$profileIsolationFunction = Get-FunctionAst `
    -Ast $executorAst `
    -Name 'Assert-UserProfileIsolation'
$profileIsolationText = $profileIsolationFunction.Extent.Text
$profileIsolationParameters = @($profileIsolationFunction.Body.ParamBlock.Parameters)
Assert-True -Condition (
    $profileIsolationParameters.Count -eq 1 -and
    $profileIsolationParameters[0].Extent.Text -ceq
        '[switch]$AllowPostRestoreNuGetArtifacts') `
    -Message 'profile isolation must expose only the explicit postrestore allowlist switch'
Assert-True -Condition ($profileIsolationText -notmatch '\*') `
    -Message 'profile isolation must not use wildcard traversal or matching'
Assert-True -Condition ($profileIsolationText -notmatch '(?i)\bStartsWith\s*\(') `
    -Message 'profile isolation must not use path-prefix matching'
Assert-True -Condition ($profileIsolationText -notmatch '(?i)\b(?:continue|skip|return)\b') `
    -Message 'profile isolation must not skip any topology or record check'
$profileOrdinalPathCalls = @($profileIsolationFunction.Body.FindAll(
        {
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq 'Assert-ExactOrdinalProfilePaths'
        },
        $true))
Assert-True -Condition ($profileOrdinalPathCalls.Count -eq 2) `
    -Message 'profile isolation must gate both directory and file raw paths ordinally'
$profileOrdinalPathFunction = Get-FunctionAst `
    -Ast $executorAst `
    -Name 'Assert-ExactOrdinalProfilePaths'
$profileOrdinalPathText = $profileOrdinalPathFunction.Extent.Text
Assert-True -Condition ($profileOrdinalPathText -notmatch '\*') `
    -Message 'profile raw ordinal path gate must not use wildcard matching'
Assert-True -Condition ($profileOrdinalPathText -notmatch '(?i)\bStartsWith\s*\(') `
    -Message 'profile raw ordinal path gate must not use path-prefix matching'
Assert-True -Condition ($profileOrdinalPathText -notmatch '(?i)\b(?:continue|skip|return)\b') `
    -Message 'profile raw ordinal path gate must not skip any physical path'

$profileIsolationCalls = @($executorAst.FindAll(
        {
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq 'Assert-UserProfileIsolation'
        },
        $true))
Assert-True -Condition ($profileIsolationCalls.Count -eq 2) `
    -Message "profile isolation call count was $($profileIsolationCalls.Count), expected 2"
$prelaunchIsolationFunction = Get-FunctionAst `
    -Ast $executorAst `
    -Name 'Assert-PrelaunchIsolation'
$prelaunchProfileCalls = @($prelaunchIsolationFunction.Body.FindAll(
        {
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -ceq 'Assert-UserProfileIsolation'
        },
        $true))
Assert-True -Condition ($prelaunchProfileCalls.Count -eq 1) `
    -Message 'prelaunch isolation must contain exactly one profile isolation call'
Assert-True -Condition (
    $prelaunchProfileCalls[0].CommandElements.Count -eq 1 -and
    $prelaunchProfileCalls[0].Extent.Text.Trim() -ceq 'Assert-UserProfileIsolation') `
    -Message 'prelaunch profile isolation must not enable a switch or pass an argument'
$finalProfileCalls = @($profileIsolationCalls | Where-Object {
        $_.Extent.StartOffset -gt $prelaunchIsolationFunction.Extent.EndOffset
    })
Assert-True -Condition ($finalProfileCalls.Count -eq 1) `
    -Message 'only one non-prelaunch profile isolation call is permitted'
Assert-True -Condition (
    $finalProfileCalls[0].Extent.Text.Trim() -ceq
        'Assert-UserProfileIsolation -AllowPostRestoreNuGetArtifacts') `
    -Message 'final profile isolation must enable only the postrestore NuGet allowlist switch'
$nativeRunIndex = $executorText.IndexOf(
    '[Rc48SuspendedJobRunner]::Run(',
    [StringComparison]::Ordinal)
Assert-True -Condition (
    $nativeRunIndex -ge 0 -and
    $finalProfileCalls[0].Extent.StartOffset -gt $nativeRunIndex) `
    -Message 'postrestore profile allowlist must run after the native restore invocation'

$leaseSource = Get-EmbeddedSource -Text $executorText -VariableName 'evidenceRootLeaseSource'
$nativeSource = Get-EmbeddedSource -Text $executorText -VariableName 'nativeRunnerSource'
$executorFailures = @(Get-ExecutorSafetyFailures -Text $executorText)
Assert-True -Condition ($executorFailures.Count -eq 0) `
    -Message ('safe executor rejected: ' + ($executorFailures -join '; '))
$nativeFailures = @(Get-NativeSourceFailures -LeaseSource $leaseSource -NativeSource $nativeSource)
Assert-True -Condition ($nativeFailures.Count -eq 0) `
    -Message ('safe embedded native sources rejected: ' + ($nativeFailures -join '; '))

$readCalls = @($executorAst.FindAll(
        {
            param($node)
            $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
            $node.Member.Value -ceq 'ReadExactFileBytes'
        },
        $true))
Assert-True -Condition ($readCalls.Count -eq 2) `
    -Message "approval evidence read call count was $($readCalls.Count), expected 2"

$staticFixtureRoot = Join-Path $testResultsRoot (
    'rc48-r3-oneshot-static-' + [Guid]::NewGuid().ToString('N'))
$staticFixtureRoot = [IO.Path]::GetFullPath($staticFixtureRoot)
Assert-True -Condition (
    $staticFixtureRoot.StartsWith(
        $testResultsRoot + '\',
        [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetFileName($staticFixtureRoot) -cmatch
        '^rc48-r3-oneshot-static-[0-9a-f]{32}$') `
    -Message 'static fixture root escaped TestResults'
Assert-True -Condition (-not (Test-Path -LiteralPath $staticFixtureRoot)) `
    -Message 'static fixture root unexpectedly pre-exists'

$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$gateSucceeded = $false
$lease = $null
$childLease = $null
$graphConfigResult = $null
$fixtureSummary = [ordered]@{}

$junctionSource = @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

public static class Rc48StaticJunctionFixture
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        byte[] input,
        uint inputSize,
        IntPtr output,
        uint outputSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
    }

    public static void Create(string junctionPath, string targetPath)
    {
        string junction = Path.GetFullPath(junctionPath);
        string target = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(junction);
        string substitute = @"\??\" + target;
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] printBytes = Encoding.Unicode.GetBytes(target);
        int pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        byte[] buffer = new byte[16 + pathBufferLength];
        WriteUInt32(buffer, 0, 0xA0000003);
        WriteUInt16(buffer, 4, checked((ushort)(8 + pathBufferLength)));
        WriteUInt16(buffer, 8, 0);
        WriteUInt16(buffer, 10, checked((ushort)substituteBytes.Length));
        WriteUInt16(buffer, 12, checked((ushort)(substituteBytes.Length + 2)));
        WriteUInt16(buffer, 14, checked((ushort)printBytes.Length));
        Buffer.BlockCopy(substituteBytes, 0, buffer, 16, substituteBytes.Length);
        Buffer.BlockCopy(
            printBytes,
            0,
            buffer,
            16 + substituteBytes.Length + 2,
            printBytes.Length);

        IntPtr handle = CreateFile(
            junction,
            GENERIC_WRITE,
            0,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Static junction fixture could not open its new empty directory.");
        try
        {
            uint returned;
            if (!DeviceIoControl(
                    handle,
                    FSCTL_SET_REPARSE_POINT,
                    buffer,
                    (uint)buffer.Length,
                    IntPtr.Zero,
                    0,
                    out returned,
                    IntPtr.Zero))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Static junction fixture could not set its mount-point reparse data.");
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
'@

try {
    New-Item -ItemType Directory -Path $staticFixtureRoot | Out-Null
    $compileTemp = Join-Path $staticFixtureRoot 'compile-temp'
    New-Item -ItemType Directory -Path $compileTemp | Out-Null
    $env:TEMP = $compileTemp
    $env:TMP = $compileTemp

    Add-Type -TypeDefinition $leaseSource -Language CSharp
    Add-Type -TypeDefinition $nativeSource -Language CSharp
    Add-Type -TypeDefinition $junctionSource -Language CSharp
    Assert-True -Condition ($null -ne ('Rc48EvidenceDirectoryLease' -as [type])) `
        -Message 'evidence lease source did not compile'
    Assert-True -Condition ($null -ne ('Rc48SuspendedJobRunner' -as [type])) `
        -Message 'native runner source did not compile'
    Assert-True -Condition ($null -ne ('Rc48StaticJunctionFixture' -as [type])) `
        -Message 'junction fixture source did not compile'

    Import-ExecutorFunctions -Ast $executorAst -Names @(
        'ConvertTo-JsonEvidenceBytes',
        'Assert-CanonicalJsonBytes',
        'Get-BytesSha256',
        'Assert-FileRecordSchema',
        'Get-FileRecordByteTotal',
        'Assert-ToolchainSnapshotSchema',
        'Assert-ToolchainSnapshotEqual',
        'ConvertTo-NativeEnvironment',
        'New-NativeEnvironmentBlock')
    $utf8Strict = [Text.UTF8Encoding]::new($false, $true)

    $leaseRoot = Join-Path $staticFixtureRoot 'lease-root'
    $lease = [Rc48EvidenceDirectoryLease]::CreateNew($leaseRoot)
    $levelOne = Join-Path $leaseRoot 'level-one'
    $levelTwo = Join-Path $levelOne 'level-two'

    $outOfOrderRejected = $false
    New-Item -ItemType Directory -Path $levelTwo | Out-Null
    try {
        $lease.AnchorDirectories([string[]]@($levelTwo))
    }
    catch {
        $outOfOrderRejected = $true
    }
    Assert-True -Condition $outOfOrderRejected `
        -Message 'child lease accepted an unanchored parent'
    $lease.AnchorDirectories([string[]]@($levelOne))
    $lease.AnchorDirectories([string[]]@($levelTwo))
    $lease.Validate()

    $rootRenameRejected = $false
    try {
        [IO.Directory]::Move($leaseRoot, ($leaseRoot + '-moved'))
    }
    catch [IO.IOException] {
        $rootRenameRejected = $true
    }
    Assert-True -Condition $rootRenameRejected `
        -Message 'held root directory could be renamed'
    $childRenameRejected = $false
    try {
        [IO.Directory]::Move($levelTwo, ($levelTwo + '-moved'))
    }
    catch [IO.IOException] {
        $childRenameRejected = $true
    }
    Assert-True -Condition $childRenameRejected `
        -Message 'held child directory could be renamed'

    $approvedPath = Join-Path $levelTwo 'approved-baseline.json'
    $approvedValue = [ordered]@{ schema = 3; value = 'held-bytes' }
    [byte[]]$approvedPayload = ConvertTo-JsonEvidenceBytes -Value $approvedValue
    $lease.WriteNewFile($approvedPath, 'approved-baseline.json', $approvedPayload)
    # Freeze and Execute are separate invocations. Reopen the exact root and
    # child handles so the read fixture models the Execute phase precisely.
    $lease.Dispose()
    $lease = [Rc48EvidenceDirectoryLease]::Acquire($leaseRoot)
    $lease.AnchorDirectories([string[]]@($levelOne))
    $lease.AnchorDirectories([string[]]@($levelTwo))
    [byte[]]$approvedBytes = $lease.ReadExactFileBytes(
        $approvedPath,
        'approved-baseline.json',
        1MB)
    $approvedHash = Get-BytesSha256 -Bytes $approvedBytes
    Assert-True -Condition ($approvedHash -ceq (Get-BytesSha256 -Bytes $approvedPayload)) `
        -Message 'same-handle approval bytes changed before hashing'
    $approvedParsed = $utf8Strict.GetString($approvedBytes) | ConvertFrom-Json -ErrorAction Stop
    Assert-CanonicalJsonBytes -Actual $approvedBytes -Value $approvedParsed -Label 'fixture baseline'
    $approvalRenameRejected = $false
    try {
        [IO.File]::Move($approvedPath, ($approvedPath + '.moved'))
    }
    catch [IO.IOException] {
        $approvalRenameRejected = $true
    }
    Assert-True -Condition $approvalRenameRejected `
        -Message 'held approval input could be replaced or renamed'

    $invalidUtf8Rejected = $false
    try {
        [void]$utf8Strict.GetString([byte[]](0xC3, 0x28))
    }
    catch [Text.DecoderFallbackException] {
        $invalidUtf8Rejected = $true
    }
    Assert-True -Condition $invalidUtf8Rejected `
        -Message 'strict UTF-8 decoder accepted malformed bytes'
    $bomRejected = $false
    $bomPayload = [byte[]](0xEF, 0xBB, 0xBF) + $approvedPayload
    try {
        $bomValue = $utf8Strict.GetString($bomPayload) | ConvertFrom-Json -ErrorAction Stop
        Assert-CanonicalJsonBytes -Actual $bomPayload -Value $bomValue -Label 'BOM fixture'
    }
    catch {
        $bomRejected = $true
    }
    Assert-True -Condition $bomRejected `
        -Message 'canonical JSON gate accepted a UTF-8 BOM'
    $duplicateJsonRejected = $false
    [byte[]]$duplicatePayload = $utf8Strict.GetBytes(
        '{"schema":3,"schema":4}' + [Environment]::NewLine)
    try {
        $duplicateValue = $utf8Strict.GetString($duplicatePayload) |
            ConvertFrom-Json -ErrorAction Stop
        Assert-CanonicalJsonBytes `
            -Actual $duplicatePayload `
            -Value $duplicateValue `
            -Label 'duplicate JSON fixture'
    }
    catch {
        $duplicateJsonRejected = $true
    }
    Assert-True -Condition $duplicateJsonRejected `
        -Message 'canonical JSON gate accepted duplicate/re-serialized properties'

    $atomicPath = Join-Path $levelTwo 'atomic-result.json'
    $lease.WriteNewFile(
        $atomicPath,
        'atomic-result.json',
        $utf8Strict.GetBytes('{}'))
    $replayRejected = $false
    try {
        $lease.WriteNewFile(
            $atomicPath,
            'atomic-result.json',
            $utf8Strict.GetBytes('{}'))
    }
    catch {
        $replayRejected = $true
    }
    Assert-True -Condition $replayRejected `
        -Message 'CREATE_NEW output accepted a replay'

    $preexistingPath = Join-Path $levelTwo 'preexisting.json'
    [IO.File]::WriteAllText($preexistingPath, 'preexisting', $utf8Strict)
    $preexistingRejected = $false
    try {
        $lease.WriteNewFile(
            $preexistingPath,
            'preexisting.json',
            $utf8Strict.GetBytes('{}'))
    }
    catch {
        $preexistingRejected = $true
    }
    Assert-True -Condition $preexistingRejected `
        -Message 'CREATE_NEW output overwrote a pre-existing object'

    $hardlinkSource = Join-Path $staticFixtureRoot 'hardlink-source.json'
    [IO.File]::WriteAllText($hardlinkSource, 'hardlink-source', $utf8Strict)
    $hardlinkPath = Join-Path $levelTwo 'hardlink.json'
    New-Item -ItemType HardLink -Path $hardlinkPath -Target $hardlinkSource | Out-Null
    $hardlinkRejected = $false
    try {
        $lease.WriteNewFile(
            $hardlinkPath,
            'hardlink.json',
            $utf8Strict.GetBytes('{}'))
    }
    catch {
        $hardlinkRejected = $true
    }
    Assert-True -Condition $hardlinkRejected `
        -Message 'CREATE_NEW output accepted a pre-existing hardlink'

    $anchoredLog = Join-Path $levelTwo 'restore.binlog'
    $lease.CreateAnchoredOutput($anchoredLog, 'restore.binlog')
    $writer = [IO.FileStream]::new(
        $anchoredLog,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Write,
        [IO.FileShare]::ReadWrite)
    try {
        $writer.Write([byte[]](1, 2, 3), 0, 3)
        $writer.Flush($true)
    }
    finally {
        $writer.Dispose()
    }
    $anchoredBytes = $lease.FlushAnchoredOutput($anchoredLog, 'restore.binlog')
    Assert-True -Condition ($anchoredBytes -eq 3L) `
        -Message 'same-object anchored output flush returned the wrong size'
    $anchoredRenameRejected = $false
    try {
        [IO.File]::Move($anchoredLog, ($anchoredLog + '.moved'))
    }
    catch [IO.IOException] {
        $anchoredRenameRejected = $true
    }
    Assert-True -Condition $anchoredRenameRejected `
        -Message 'anchored child output could be replaced while held'

    $lease.Dispose()
    $lease = $null
    $releaseMoveAttempts = Move-DirectoryAfterLeaseRelease `
        -Source $leaseRoot `
        -Destination ($leaseRoot + '-released') `
        -Label 'evidence root'
    [void](Move-DirectoryAfterLeaseRelease `
            -Source ($leaseRoot + '-released') `
            -Destination $leaseRoot `
            -Label 'renamed evidence root')

    $preexistingRootRejected = $false
    $preexistingRoot = Join-Path $staticFixtureRoot 'preexisting-root'
    New-Item -ItemType Directory -Path $preexistingRoot | Out-Null
    try {
        $unexpected = [Rc48EvidenceDirectoryLease]::CreateNew($preexistingRoot)
        $unexpected.Dispose()
    }
    catch {
        $preexistingRootRejected = $true
    }
    Assert-True -Condition $preexistingRootRejected `
        -Message 'CreateNew accepted a pre-existing evidence root'

    $junctionTarget = Join-Path $staticFixtureRoot 'junction-target'
    $junctionRoot = Join-Path $staticFixtureRoot 'junction-root'
    New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    [Rc48StaticJunctionFixture]::Create($junctionRoot, $junctionTarget)
    $junctionRootRejected = $false
    try {
        $unexpected = [Rc48EvidenceDirectoryLease]::Acquire($junctionRoot)
        $unexpected.Dispose()
    }
    catch {
        $junctionRootRejected = $true
    }
    Assert-True -Condition $junctionRootRejected `
        -Message 'root lease accepted a mount-point reparse object'
    [IO.Directory]::Delete($junctionRoot, $false)

    $childRoot = Join-Path $staticFixtureRoot 'child-root'
    $childTarget = Join-Path $staticFixtureRoot 'child-target'
    $childLink = Join-Path $childRoot 'child-link'
    New-Item -ItemType Directory -Path $childTarget | Out-Null
    $childLease = [Rc48EvidenceDirectoryLease]::CreateNew($childRoot)
    [Rc48StaticJunctionFixture]::Create($childLink, $childTarget)
    $junctionChildRejected = $false
    try {
        $childLease.AnchorDirectories([string[]]@($childLink))
    }
    catch {
        $junctionChildRejected = $true
    }
    Assert-True -Condition $junctionChildRejected `
        -Message 'child lease accepted a mount-point reparse object'
    $childLease.Dispose()
    $childLease = $null
    [IO.Directory]::Delete($childLink, $false)

    $toolchainSelectionSetId = 'dotnet-sdk-8.0.421-netcore-8.0.27-win-x64-v1'
    $toolchainFileLimit = 5000
    $toolchainDirectoryLimit = 5000
    $toolchainEntryLimit = 10000
    $toolchainByteLimit = 600MB
    $toolchainDepthLimit = 16
    $toolchainFileRoots = @('C:\Program Files\dotnet\dotnet.exe')
    $toolchainDirectoryRoots = @(
        'C:\Program Files\dotnet\sdk\8.0.421',
        'C:\Program Files\dotnet\host\fxr',
        'C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.27',
        'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.27',
        'C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Ref\8.0.27',
        'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Host.win-x64\8.0.27')
    $directoryPaths = [string[]]@($toolchainDirectoryRoots)
    [Array]::Sort($directoryPaths, [StringComparer]::OrdinalIgnoreCase)
    $filePaths = [Collections.Generic.List[string]]::new()
    $filePaths.Add($toolchainFileRoots[0])
    foreach ($directoryRoot in $toolchainDirectoryRoots) {
        $filePaths.Add((Join-Path $directoryRoot 'fixture.dll'))
    }
    $filePathArray = [string[]]@($filePaths)
    [Array]::Sort($filePathArray, [StringComparer]::OrdinalIgnoreCase)
    $toolchainFixture = [pscustomobject][ordered]@{
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
        directories = @($directoryPaths | ForEach-Object {
                [pscustomobject][ordered]@{ path = $_ }
            })
        files = @($filePathArray | ForEach-Object {
                [pscustomobject][ordered]@{
                    path = $_
                    bytes = [long]1
                    sha256 = ('A' * 64)
                }
        })
        fileCount = $filePathArray.Count
        directoryCount = $directoryPaths.Count
        entryCount = $filePathArray.Count + $directoryPaths.Count
        bytes = [long]$filePathArray.Count
    }
    Assert-ToolchainSnapshotSchema -Snapshot $toolchainFixture -Label 'safe synthetic toolchain'
    $toolchainClone = $toolchainFixture | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    Assert-ToolchainSnapshotEqual `
        -Expected $toolchainFixture `
        -Actual $toolchainClone `
        -Label 'identical synthetic toolchain'

    $toolchainMutationsRejected = 0
    $mutations = @(
        {
            param($value)
            $value.files[0].sha256 = ('B' * 64)
        },
        {
            param($value)
            $value.files = @($value.files | Select-Object -Skip 1)
            $value.fileCount = @($value.files).Count
            $value.bytes = [long]@($value.files).Count
        },
        {
            param($value)
            $value.directories[0].path = 'C:\outside-toolchain'
        },
        {
            param($value)
            $value.fileCount = 5001
        },
        {
            param($value)
            $value.bytes = 600MB + 1
        },
        {
            param($value)
            $value.directories[0].path =
                $toolchainDirectoryRoots[0] + ('\deep' * 17)
        },
        {
            param($value)
            $value.directories = @($value.directories[0]) + @($value.directories)
        },
        {
            param($value)
            [Array]::Reverse($value.files)
        },
        {
            param($value)
            $value.limits | Add-Member -NotePropertyName extra -NotePropertyValue 1
        })
    foreach ($mutation in $mutations) {
        $mutated = $toolchainFixture | ConvertTo-Json -Depth 12 | ConvertFrom-Json
        & $mutation $mutated
        try {
            Assert-ToolchainSnapshotEqual `
                -Expected $toolchainFixture `
                -Actual $mutated `
                -Label 'unsafe synthetic toolchain'
        }
        catch {
            $toolchainMutationsRejected++
        }
    }
    Assert-True -Condition ($toolchainMutationsRejected -eq $mutations.Count) `
        -Message (
            "toolchain gate rejected $toolchainMutationsRejected / " +
            "$($mutations.Count) unsafe mutations")

    $environmentNames = @(
        'SystemRoot', 'WINDIR', 'ComSpec', 'PATH', 'PATHEXT',
        'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432',
        'USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'DOTNET_ROOT',
        'DOTNET_MULTILEVEL_LOOKUP', 'DOTNET_ROLL_FORWARD',
        'DOTNET_CLI_HOME', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
        'DOTNET_CLI_TELEMETRY_OPTOUT',
        'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE',
        'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH',
        'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_NOLOGO',
        'TEMP', 'TMP', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
        'NUGET_SCRATCH', 'NUGET_PLUGINS_CACHE_PATH',
        'NUGET_PLUGIN_PATHS', 'NUGET_NETCORE_PLUGIN_PATHS',
        'NUGET_NETFX_PLUGIN_PATHS', 'NUGET_CERT_REVOCATION_MODE',
        'MSBuildSDKsPath', 'MSBUILDDISABLENODEREUSE',
        'MSBUILDUSESERVER')
    $environment = [ordered]@{}
    foreach ($name in $environmentNames) { $environment[$name] = "fixture-$name" }
    $environment['DOTNET_ROLL_FORWARD'] = 'Disable'
    $nativeEnvironment = ConvertTo-NativeEnvironment `
        -Environment $environment `
        -ExpectedNames $environmentNames
    $environmentBlock = New-NativeEnvironmentBlock -Environment $nativeEnvironment
    Assert-True -Condition (
        $nativeEnvironment.Count -eq 34 -and
        $nativeEnvironment['DOTNET_ROLL_FORWARD'] -ceq 'Disable' -and
        $environmentBlock.entryCount -eq 34 -and
        $environmentBlock.sha256 -cmatch '^[A-F0-9]{64}$') `
        -Message 'exact 34-entry native environment fixture failed'

    $unsafeExecutorFixtures = [ordered]@{
        'evidence-access-before-lease' = $executorText.Replace(
            '# This bootstrap compilation intentionally uses the caller''s ambient TEMP/TMP.',
            'if (Test-Path -LiteralPath $evidence) { throw ''unsafe'' }')
        'bootstrap-after-acquire' = $executorText.Replace(
            'Add-Type -TypeDefinition $evidenceRootLeaseSource -Language CSharp',
            '[Rc48EvidenceDirectoryLease]::Acquire($evidence); Add-Type -TypeDefinition $evidenceRootLeaseSource -Language CSharp')
        'child-directory-removed' = $executorText.Replace(
            '        $nugetNetFxPluginPaths,',
            '')
        'child-anchor-disabled' = $executorText.Replace(
            '$evidenceLease.AnchorDirectories([string[]]@($requiredDirectory))',
            '$null = $requiredDirectory')
        'held-sentinel-extra-name' = $executorText.Replace(
            '[IO.Path]::GetFullPath((Join-Path $exactSentinelDirectory ''8.0.421.aspNetCertificateSentinel''))',
            '[IO.Path]::GetFullPath((Join-Path $exactSentinelDirectory ''8.0.421.aspNetCertificateSentinel'')),
        [IO.Path]::GetFullPath((Join-Path $exactSentinelDirectory ''8.0.421.extraSentinel''))')
        'held-sentinel-freeze-guard-disabled' = $executorText.Replace(
            'if ($Mode -cne ''Freeze'') {',
            'if ($Mode -ceq ''Freeze'') {')
        'held-sentinel-root-validation-disabled' = $executorText.Replace(
            '$script:evidenceLease.Validate()',
            '$null = $script:evidenceLease')
        'held-sentinel-marker-route-widened' = $executorText.Replace(
            '$script:freezeCreatedSentinelPaths.Contains($fixedInput)',
            '$true')
        'ordinary-file-record-sharing-relaxed' = $executorText.Replace(
            '[IO.FileShare]::Read,',
            '[IO.FileShare]::ReadWrite,')
        'approval-double-open' = $executorText.Replace(
            '$actualBaselineHash = Get-BytesSha256 -Bytes $baselineBytes',
            '$actualBaselineHash = (Get-FileHash -LiteralPath $baselinePath -Algorithm SHA256).Hash')
        'approval-default-utf8' = $executorText.Replace(
            '$baselineJson = $utf8Strict.GetString($baselineBytes)',
            '$baselineJson = $utf8.GetString($baselineBytes)')
        'parent-writealltext' = $executorText.Replace(
            'Write-NewJsonEvidence -Path $attemptPath',
            '[IO.File]::WriteAllText($attemptPath, ''unsafe''); Write-NewJsonEvidence -Path $attemptPath')
        'parent-overwrite-mode' = $executorText.Replace(
            'CREATE_NEW,',
            'OPEN_EXISTING,')
        'binlog-anchor-removed' = $executorText.Replace(
            '$evidenceLease.CreateAnchoredOutput($binlogPath, ''restore.binlog'')',
            '$null = $binlogPath')
        'logger-flush-removed' = $executorText.Replace(
            '$binlogBytes = $evidenceLease.FlushAnchoredOutput(',
            '$binlogBytes = 1; $null = (')
        'temp-activated-too-early' = $executorText.Replace(
            'Add-Type -TypeDefinition $nativeRunnerSource -Language CSharp',
            '$env:TEMP = $temp; Add-Type -TypeDefinition $nativeRunnerSource -Language CSharp')
        'lease-disposed-before-run' = $executorText.Replace(
            '$exitCode = [Rc48SuspendedJobRunner]::Run(',
            '$evidenceLease.Dispose(); $exitCode = [Rc48SuspendedJobRunner]::Run(')
        'delete-sharing-enabled' = $executorText.Replace(
            'FILE_SHARE_READ | FILE_SHARE_WRITE,',
            'FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,')
        'reparse-open-disabled' = $executorText.Replace(
            'flags | FILE_FLAG_OPEN_REPARSE_POINT,',
            'flags,')
        'final-path-check-disabled' = $executorText.Replace(
            'AssertExactFinalPath(openedHandle, full, context);',
            'return openedHandle;')
        'single-link-check-disabled' = $executorText.Replace(
            'if (fileInformation.NumberOfLinks != 1)',
            'if (false)')
        'toolchain-root-removed' = $executorText.Replace(
            "    'C:\Program Files\dotnet\host\fxr',",
            '')
        'toolchain-cap-weakened' = $executorText.Replace(
            '$toolchainFileLimit = 5000',
            '$toolchainFileLimit = 5001')
        'profile-ordinal-sort-relaxed' = $executorText.Replace(
            '[Array]::Sort($Actual, [StringComparer]::Ordinal)',
            '[Array]::Sort($Actual, [StringComparer]::OrdinalIgnoreCase)')
        'profile-case-collision-rejection-disabled' = $executorText.Replace(
            'if (-not $actualOrdinalIgnoreCasePaths.Add($actualPath)) {',
            'if ($false) {')
        'profile-raw-file-path-gate-disabled' = $executorText.Replace(
            '        -Expected $expectedProfileFilePaths `',
            '        -Expected @() `')
        'profile-ordinal-path-check-skipped' = $executorText.Replace(
            '    for ($index = 0; $index -lt $Expected.Count; $index++) {',
            '    continue; for ($index = 0; $index -lt $Expected.Count; $index++) {')
        'roll-forward-enabled' = $executorText.Replace(
            'DOTNET_ROLL_FORWARD = ''Disable''',
            'DOTNET_ROLL_FORWARD = ''LatestMajor''')
        'environment-count-weakened' = $executorText.Replace(
            '$Environment.Count -ne 34 -or $ExpectedNames.Count -ne 34',
            '$Environment.Count -ne 33 -or $ExpectedNames.Count -ne 33')
        'cleanup-early-throw' = $executorText.Replace(
            'if (attributeList != IntPtr.Zero)',
            'if (cleanupFailure != null) throw cleanupFailure; if (attributeList != IntPtr.Zero)')
    }
    $unsafeExecutorRejected = 0
    $unsafeExecutorEscaped = [Collections.Generic.List[string]]::new()
    foreach ($fixture in $unsafeExecutorFixtures.GetEnumerator()) {
        Assert-True -Condition ($fixture.Value -cne $executorText) `
            -Message "unsafe executor fixture did not mutate source: $($fixture.Key)"
        $fixtureLease = Get-EmbeddedSource `
            -Text ([string]$fixture.Value) `
            -VariableName 'evidenceRootLeaseSource'
        $fixtureNative = Get-EmbeddedSource `
            -Text ([string]$fixture.Value) `
            -VariableName 'nativeRunnerSource'
        $fixtureFailures = @(
            Get-ExecutorSafetyFailures -Text ([string]$fixture.Value)
            Get-NativeSourceFailures -LeaseSource $fixtureLease -NativeSource $fixtureNative)
        if ($fixtureFailures.Count -gt 0) {
            $unsafeExecutorRejected++
        }
        else {
            $unsafeExecutorEscaped.Add([string]$fixture.Key)
        }
    }
    Assert-True -Condition (
        $unsafeExecutorRejected -eq $unsafeExecutorFixtures.Count) `
        -Message (
            "executor gate rejected $unsafeExecutorRejected / " +
            "$($unsafeExecutorFixtures.Count) unsafe mutations; escaped: " +
            ($unsafeExecutorEscaped -join ', '))

    $unsafeCleanup = $nativeSource.Replace(
        'if (attributeList != IntPtr.Zero)',
        'if (cleanupFailure != null) throw cleanupFailure; if (attributeList != IntPtr.Zero)')
    Assert-True -Condition (
        @(Get-NativeSourceFailures -LeaseSource $leaseSource -NativeSource $unsafeCleanup).Count -gt 0) `
        -Message 'native cleanup early-throw fixture escaped'

    $graphConfigOutput = @(& $graphConfigGatePath)
    Assert-True -Condition ($graphConfigOutput.Count -eq 1) `
        -Message 'graph/config static gate returned an unexpected output count'
    $graphConfigResult = [string]$graphConfigOutput[0] |
        ConvertFrom-Json -ErrorAction Stop
    Assert-True -Condition (
        [string]$graphConfigResult.Result -ceq
            'RC48_R3_RESTORE_GRAPH_CONFIG_STATIC_PASS') `
        -Message 'graph/config static gate did not return its exact PASS result'

    $fixtureSummary = [ordered]@{
        RootRenameRejected = $rootRenameRejected
        ChildRenameRejected = $childRenameRejected
        ApprovalRenameRejected = $approvalRenameRejected
        InvalidUtf8Rejected = $invalidUtf8Rejected
        BomRejected = $bomRejected
        DuplicateJsonRejected = $duplicateJsonRejected
        ReplayRejected = $replayRejected
        PreexistingRejected = $preexistingRejected
        HardlinkRejected = $hardlinkRejected
        AnchoredOutputBytes = $anchoredBytes
        AnchoredRenameRejected = $anchoredRenameRejected
        ReleaseMoveAttempts = $releaseMoveAttempts
        PreexistingRootRejected = $preexistingRootRejected
        JunctionRootRejected = $junctionRootRejected
        JunctionChildRejected = $junctionChildRejected
        ToolchainMutationsRejected = $toolchainMutationsRejected
        EnvironmentEntries = $environmentBlock.entryCount
        UnsafeExecutorMutationsRejected = $unsafeExecutorRejected
    }
    $gateSucceeded = $true
}
finally {
    if ($null -ne $childLease) { $childLease.Dispose() }
    if ($null -ne $lease) { $lease.Dispose() }
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    if ($gateSucceeded -and (Test-Path -LiteralPath $staticFixtureRoot)) {
        $verified = [IO.Path]::GetFullPath($staticFixtureRoot)
        if (-not $verified.StartsWith(
                $testResultsRoot + '\',
                [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($verified) -cnotmatch
                '^rc48-r3-oneshot-static-[0-9a-f]{32}$') {
            throw 'Static fixture cleanup target failed its exact safety boundary.'
        }
        Remove-Item -LiteralPath $verified -Recurse -Force
    }
}

[pscustomobject][ordered]@{
    Result = 'RC48_R3_ONESHOT_STATIC_GATE_PASS'
    Executor = $executorPath
    ExecutorSha256 = (Get-FileHash -LiteralPath $executorPath -Algorithm SHA256).Hash
    GraphConfigGate = @($graphConfigResult)[-1].Result
    NativeSourcesCompiled = $true
    ApprovalReadCallCount = $readCalls.Count
    Fixture = [pscustomobject]$fixtureSummary
}
