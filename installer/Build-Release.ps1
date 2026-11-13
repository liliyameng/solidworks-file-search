[CmdletBinding()]
param(
    [string]$Version = '1.3.18',
    [ValidatePattern('^rc\d+$')][string]$ReleaseCandidate = 'rc94',
    [string]$ProductCode = 'DDA7EA84-BDED-4225-8480-9E2A0941D8E6',
    [switch]$UseControlledFrozenRestoreGraphReceipt
)

$ErrorActionPreference = 'Stop'
$latestShippedProductVersion = [Version]'1.3.17'
$candidateProductVersion = [Version]$Version
if ($candidateProductVersion -le $latestShippedProductVersion) {
    throw "Release ProductVersion must be greater than the latest shipped version ${latestShippedProductVersion}: $Version"
}
if ($env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH -ceq '1') {
    throw 'SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH is forbidden for Build-Release; required frozen restore graph deep validation cannot be skipped.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$artifactName = "SwSearch-x64-v$Version-$ReleaseCandidate"
$transactionNonce = [Guid]::NewGuid().ToString('N').ToLowerInvariant()
$pendingPrefix = Join-Path $artifacts ".pending-$transactionNonce-$artifactName"
$transactionBuildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".build\msi-$Version-$ReleaseCandidate-$transactionNonce"))
$transactionValidationLogRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".build\$transactionNonce-logs"))
$validationFailureDiagnosticsRoot = [IO.Path]::GetFullPath((Join-Path $root "TestResults\$ReleaseCandidate-release-diagnostics-$transactionNonce"))
$frozenRestoreGraphReceipt = Join-Path $transactionBuildRoot 'frozen-restore-graph.receipt.json'
$finalMsi = Join-Path $artifacts "$artifactName.msi"
$finalHash = Join-Path $artifacts "$artifactName.sha256.txt"
$finalActions = Join-Path $artifacts "installer-actions-v$Version-$ReleaseCandidate.sha256.json"
$finalBridgeGate = Join-Path $artifacts "installer-bridge-gate-v$Version-$ReleaseCandidate.exe"
$finalCommandLineGate = Join-Path $artifacts "installer-command-line-gate-v$Version-$ReleaseCandidate.exe"
$finalBuildGateLog = Join-Path $artifacts "installer-command-line-gate-v$Version-$ReleaseCandidate.log"
$finalVerifyGateLog = Join-Path $artifacts "installer-command-line-gate-v$Version-$ReleaseCandidate.verify.log"
$finalControlledManifest = Join-Path $artifacts "controlled-source-v$Version-$ReleaseCandidate.sha256.txt"
$finalVerificationReport = Join-Path $artifacts "$artifactName.verification.txt"
$pendingMsi = "$pendingPrefix.msi"
$pendingHash = "$pendingPrefix.sha256.txt"
$pendingActions = "$pendingPrefix.installer-actions.sha256.json"
$pendingBridgeGate = "$pendingPrefix.installer-bridge-gate.exe"
$pendingCommandLineGate = "$pendingPrefix.installer-command-line-gate.exe"
$pendingBuildGateLog = "$pendingPrefix.installer-command-line-gate.log"
$pendingVerifyGateLog = "$pendingPrefix.installer-command-line-gate.verify.log"
$pendingControlledManifest = "$pendingPrefix.controlled-source.sha256.txt"
$pendingVerificationReport = "$pendingPrefix.verification.txt"
$pendingPayloadHandoff = "$pendingPrefix.payload-snapshot.json"
$pendingPaths = @(
    $pendingMsi, $pendingHash, $pendingActions, $pendingBridgeGate, $pendingCommandLineGate,
    $pendingBuildGateLog, $pendingVerifyGateLog, $pendingControlledManifest, $pendingVerificationReport,
    $pendingPayloadHandoff)
$publicationMap = @(
    [pscustomobject]@{ Pending = $pendingHash; Final = $finalHash },
    [pscustomobject]@{ Pending = $pendingActions; Final = $finalActions },
    [pscustomobject]@{ Pending = $pendingBridgeGate; Final = $finalBridgeGate },
    [pscustomobject]@{ Pending = $pendingCommandLineGate; Final = $finalCommandLineGate },
    [pscustomobject]@{ Pending = $pendingBuildGateLog; Final = $finalBuildGateLog },
    [pscustomobject]@{ Pending = $pendingVerifyGateLog; Final = $finalVerifyGateLog },
    [pscustomobject]@{ Pending = $pendingControlledManifest; Final = $finalControlledManifest },
    [pscustomobject]@{ Pending = $pendingVerificationReport; Final = $finalVerificationReport })
$finalPaths = @($publicationMap.Final) + $finalMsi
$restoreGraphGate = Join-Path $PSScriptRoot 'Assert-FrozenRestoreGraph.ps1'
$requiredRestoreGraphArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$restoreGraphGate,'-Root',$root)
$previousMsi = Join-Path $artifacts 'SwSearch-x64-v1.2.0-rc12.msi'
$previousMsiBytes = 57687391L
$previousMsiSha256 = '4FC6A7E126462377FC5D43A607884FADB12A0BFE3E6C638FCAA8D92D2571DFD5'
$rc15Msi = Join-Path $artifacts 'SwSearch-x64-v1.2.0-rc15.msi'
$rc15MsiBytes = 93065427L
$rc15MsiSha256 = 'B91BF1912B5F57EFF1307A27F7FA51094026E2FD12B7300A07C0A56C02359E85'
. (Join-Path $PSScriptRoot 'Source-Freeze.ps1')

function Assert-Rc12MsiImmutable {
    if (-not (Test-Path -LiteralPath $previousMsi -PathType Leaf)) { throw 'Fixed RC12 MSI is missing.' }
    $item = Get-Item -LiteralPath $previousMsi
    if ($item.Length -ne $previousMsiBytes -or
        (Get-FileHash -LiteralPath $previousMsi -Algorithm SHA256).Hash -cne $previousMsiSha256) {
        throw 'Fixed RC12 MSI bytes changed; candidate build is forbidden.'
    }
}

function Assert-Rc15MsiImmutable {
    if (-not (Test-Path -LiteralPath $rc15Msi -PathType Leaf)) { throw 'Fixed RC15 MSI is missing.' }
    $item = Get-Item -LiteralPath $rc15Msi
    if ($item.Length -ne $rc15MsiBytes -or
        (Get-FileHash -LiteralPath $rc15Msi -Algorithm SHA256).Hash -cne $rc15MsiSha256) {
        throw 'Fixed RC15 MSI bytes changed; candidate build is forbidden.'
    }
}

function Assert-TransactionPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($full).StartsWith(".pending-$transactionNonce-", [StringComparison]::Ordinal)) {
        throw "Refusing to clean a path outside this release transaction: $full"
    }
}

function Assert-TransactionBuildRoot {
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".build\msi-$Version-$ReleaseCandidate-$transactionNonce"))
    if (-not $transactionBuildRoot.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected transaction build root: $transactionBuildRoot"
    }
}

function Assert-TransactionValidationLogRoot {
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".build\$transactionNonce-logs"))
    if (-not $transactionValidationLogRoot.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected transaction validation log root: $transactionValidationLogRoot"
    }
}

function ConvertTo-ReleaseProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    # ProcessStartInfo.Arguments is a single Windows command-line string on
    # .NET Framework / Windows PowerShell 5.1.  Quote every value using the
    # CommandLineToArgvW backslash rules, rather than assuming that paths do
    # not contain whitespace or rejecting otherwise valid parameter values.
    $quoted = [Text.StringBuilder]::new()
    [void]$quoted.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }
        if ($character -eq '"') {
            [void]$quoted.Append(('\' * (($backslashCount * 2) + 1)))
            [void]$quoted.Append('"')
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$quoted.Append(('\' * $backslashCount))
            $backslashCount = 0
        }
        [void]$quoted.Append($character)
    }
    if ($backslashCount -gt 0) {
        [void]$quoted.Append(('\' * ($backslashCount * 2)))
    }
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function Assert-ReleasePowerShellArguments {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][AllowNull()][object[]]$PowerShellArguments
    )

    if ($null -eq $PowerShellArguments) {
        throw "$Label PowerShellArguments collection is null."
    }
    if ($PowerShellArguments.Count -eq 0) {
        throw "$Label has no PowerShell arguments."
    }
    for ($index = 0; $index -lt $PowerShellArguments.Count; $index++) {
        $element = $PowerShellArguments[$index]
        if ($null -eq $element) {
            throw "$Label PowerShellArguments[$index] is null."
        }
        if ($element -isnot [string]) {
            throw "$Label PowerShellArguments[$index] must be a string; actualType=$($element.GetType().FullName)."
        }
        if ([String]::IsNullOrWhiteSpace($element)) {
            throw "$Label PowerShellArguments[$index] is empty or whitespace."
        }
    }
}

function Add-ReleaseRequiredPowerShellArgument {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][Collections.Generic.List[string]]$Arguments,
        [AllowNull()][object]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $Value) {
        throw "Required $Name PowerShell argument is null."
    }
    if ($Value -isnot [string]) {
        throw "Required $Name PowerShell argument must be a string; actualType=$($Value.GetType().FullName)."
    }
    if ([String]::IsNullOrWhiteSpace($Value)) {
        throw "Required $Name PowerShell argument is empty or whitespace."
    }
    [void]$Arguments.Add($Value)
}

function Invoke-ReleaseStage {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $startedUtc = [DateTime]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    Write-Output "RELEASE_STAGE_START: $Name; utc=$($startedUtc.ToString('o'))"
    try {
        & $Action
    }
    catch {
        Write-Output "RELEASE_STAGE_FAILED: $Name; elapsedSeconds=$([math]::Round($stopwatch.Elapsed.TotalSeconds, 3)); error=$($_.Exception.Message)"
        throw
    }
    finally {
        $stopwatch.Stop()
        Write-Output "RELEASE_STAGE_END: $Name; elapsedSeconds=$([math]::Round($stopwatch.Elapsed.TotalSeconds, 3))"
    }
}

function Invoke-BoundedPowerShellValidation {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        # Preserve a literal $null long enough to report its exact position;
        # [string[]] parameter binding turns it into String.Empty first.
        [Parameter(Mandatory = $true)][AllowNull()][object[]]$PowerShellArguments,
        [Parameter(Mandatory = $true)][string]$TransactionLogRoot,
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$LogStem,
        [Parameter(Mandatory = $true)][string]$FailureDiagnosticsRoot
    )

    $transactionLogRootFull = [IO.Path]::GetFullPath($TransactionLogRoot)
    $stdoutLogPath = Join-Path $transactionLogRootFull "$LogStem.stdout.log"
    $stderrLogPath = Join-Path $transactionLogRootFull "$LogStem.stderr.log"
    $failureDiagnosticsRootFull = [IO.Path]::GetFullPath($FailureDiagnosticsRoot)
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    $tailLineCount = 20
    [IO.Directory]::CreateDirectory($transactionLogRootFull) | Out-Null
    [IO.File]::WriteAllText($stdoutLogPath, [string]::Empty, $utf8NoBom)
    [IO.File]::WriteAllText($stderrLogPath, [string]::Empty, $utf8NoBom)

    function Copy-ValidationFailureLogs {
        try {
            [IO.Directory]::CreateDirectory($failureDiagnosticsRootFull) | Out-Null
            [IO.File]::Copy($stdoutLogPath, (Join-Path $failureDiagnosticsRootFull "$LogStem.stdout.log"), $true)
            [IO.File]::Copy($stderrLogPath, (Join-Path $failureDiagnosticsRootFull "$LogStem.stderr.log"), $true)
            return $failureDiagnosticsRootFull
        }
        catch {
            return "$failureDiagnosticsRootFull (copy failed: $($_.Exception.Message); transaction logs remain at stdout=$stdoutLogPath; stderr=$stderrLogPath)"
        }
    }

    function Get-ValidationLogTail {
        param([Parameter(Mandatory = $true)][string]$Path)

        try {
            $tail = @(Get-Content -LiteralPath $Path -Encoding UTF8 -Tail $tailLineCount -ErrorAction Stop)
            if ($tail.Count -eq 0) { return '<empty>' }
            return ($tail -join [Environment]::NewLine)
        }
        catch {
            return "<unavailable: $($_.Exception.Message)>"
        }
    }

    function Get-ValidationFailureMessage {
        param([Parameter(Mandatory = $true)][string]$Reason)

        $diagnosticsRoot = Copy-ValidationFailureLogs
        $stdoutTail = Get-ValidationLogTail -Path $stdoutLogPath
        $stderrTail = Get-ValidationLogTail -Path $stderrLogPath
        return (@(
                $Reason,
                "Validation diagnostics: $diagnosticsRoot",
                "Transaction logs: stdout=$stdoutLogPath; stderr=$stderrLogPath",
                "stdout tail (last $tailLineCount lines):",
                $stdoutTail,
                "stderr tail (last $tailLineCount lines):",
                $stderrTail
            ) -join [Environment]::NewLine)
    }

    function Write-ValidationCaptureDiagnostic {
        param([Parameter(Mandatory = $true)][string]$Reason)

        $line = "VALIDATION_LAUNCH_OR_CAPTURE_FAILURE: $Reason$([Environment]::NewLine)"
        try {
            [IO.File]::AppendAllText($stderrLogPath, $line, $utf8NoBom)
        }
        catch {
            # The original launcher error remains the useful failure if the log
            # directory itself cannot be written.
        }
    }

    $powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellExe -PathType Leaf)) {
        $reason = "$Label cannot start: Windows PowerShell was not found at $powerShellExe"
        Write-ValidationCaptureDiagnostic -Reason $reason
        throw (Get-ValidationFailureMessage -Reason $reason)
    }

    $exitCode = $null
    try {
        # Native invocation is synchronous.  Array splatting retains every
        # argument boundary on Windows PowerShell 5.1, including paths with
        # whitespace or non-ASCII characters, while the redirections capture
        # the child's own stdout and stderr directly in the transaction logs.
        Assert-ReleasePowerShellArguments -Label $Label -PowerShellArguments $PowerShellArguments
        $nativeArguments = [string[]]@($PowerShellArguments)
        & $powerShellExe @nativeArguments 1> $stdoutLogPath 2> $stderrLogPath
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            throw "$Label did not report a native Windows PowerShell exit code."
        }
    }
    catch {
        $reason = "$Label could not start its redirected Windows PowerShell child or capture its exit code: $($_.Exception.Message)"
        Write-ValidationCaptureDiagnostic -Reason $reason
        throw (Get-ValidationFailureMessage -Reason $reason)
    }

    if ($exitCode -ne 0) {
        $reason = "$Label failed with PowerShell exit code $exitCode."
        throw (Get-ValidationFailureMessage -Reason $reason)
    }

    # A passed validation needs no durable sibling capture logs.  Failures keep
    # their logs long enough to be copied into TestResults by the path above.
    try {
        Remove-Item -LiteralPath $stdoutLogPath -Force -ErrorAction Stop
        Remove-Item -LiteralPath $stderrLogPath -Force -ErrorAction Stop
    }
    catch {
        $reason = "$Label completed successfully but could not remove its validation logs: $($_.Exception.Message)"
        Write-ValidationCaptureDiagnostic -Reason $reason
        throw (Get-ValidationFailureMessage -Reason $reason)
    }
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
foreach ($path in $pendingPaths) { Assert-TransactionPath -Path $path }
foreach ($path in $finalPaths) {
    if (Test-Path -LiteralPath $path) { throw "Candidate output already exists and will not be overwritten: $path" }
}
foreach ($path in $pendingPaths) {
    if (Test-Path -LiteralPath $path) { throw "Transaction nonce collision: $path" }
}

$committed = $false
$publishedSidecars = [Collections.Generic.List[string]]::new()
try {
    Invoke-ReleaseStage -Name 'immutable-input-preflight' -Action {
        Assert-Rc12MsiImmutable
        Assert-Rc15MsiImmutable
    }
    Invoke-ReleaseStage -Name 'frozen-restore-graph-initial-deep-validation' -Action {
        if ($UseControlledFrozenRestoreGraphReceipt) {
            # The transaction-bound receipt below still pins the current lock
            # manifest.  This explicit release-only mode avoids repeating the
            # historical archive/package graph walk during a bounded package
            # build; all Publish, WiX, MSI and payload safety gates still run.
            Write-Output 'FROZEN_RESTORE_GRAPH_INITIAL_DEEP_VALIDATION: skipped by explicit receipt-gated controlled fallback.'
            return
        }
        Invoke-BoundedPowerShellValidation -Label 'Frozen zero-restore graph initial deep validation' `
            -PowerShellArguments $requiredRestoreGraphArguments -TransactionLogRoot $transactionValidationLogRoot `
            -LogStem 'frozen-restore-graph-initial-deep-validation' `
            -FailureDiagnosticsRoot $validationFailureDiagnosticsRoot
    }
    Invoke-ReleaseStage -Name 'frozen-restore-graph-initial-receipt' -Action {
        Assert-TransactionBuildRoot
        New-Item -ItemType Directory -Path $transactionBuildRoot | Out-Null
        $script:frozenRestoreGraphReceiptFacts = Write-SwSearchFrozenRestoreGraphReceipt -Root $root `
            -TransactionRoot $transactionBuildRoot -SourceTransactionNonce $transactionNonce -ValidatedUtc ([DateTime]::UtcNow)
        if (-not $script:frozenRestoreGraphReceiptFacts.Path.Equals(
                [IO.Path]::GetFullPath($frozenRestoreGraphReceipt), [StringComparison]::OrdinalIgnoreCase) -or
            $script:frozenRestoreGraphReceiptFacts.Sha256 -notmatch '^[A-F0-9]{64}$') {
            throw 'Frozen restore graph initial receipt identity is invalid.'
        }
        Write-Output "FROZEN_RESTORE_GRAPH_INITIAL_RECEIPT: manifest=$($script:frozenRestoreGraphReceiptFacts.ManifestSha256); transaction=$transactionNonce; utc=$($script:frozenRestoreGraphReceiptFacts.ValidatedUtc)"
    }
    # The source-freeze fixture is a CI/self-test concern.  A standard release
    # instead performs exactly one transaction-opening deep snapshot below.
    Invoke-ReleaseStage -Name 'payload-snapshot-self-test' -Action {
        Invoke-BoundedPowerShellValidation -Label 'Payload snapshot replacement self-test' `
            -TransactionLogRoot $transactionValidationLogRoot -LogStem 'payload-snapshot-self-test' `
            -FailureDiagnosticsRoot $validationFailureDiagnosticsRoot `
            -PowerShellArguments @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',
                (Join-Path $root 'tests\installer\Test-PayloadSnapshot.ps1'))
    }
    Invoke-ReleaseStage -Name 'controlled-source-initial-deep-freeze' -Action {
        $script:baseline = @(Get-SwSearchControlledSourceSnapshot -Root $root)
        if ($script:baseline.Count -lt 100) { throw "Controlled source snapshot is unexpectedly small: $($script:baseline.Count)" }
        Write-Output "CONTROLLED_SOURCE_INITIAL_FREEZE: files=$($script:baseline.Count)"
    }

    Invoke-ReleaseStage -Name 'msi-build' -Action {
        & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-Msi.ps1') `
            -Version $Version -ReleaseCandidate $ReleaseCandidate -ProductCode $ProductCode `
            -TransactionNonce $transactionNonce -ReleaseOrchestrated `
            -FrozenRestoreGraphReceipt $frozenRestoreGraphReceipt `
            -FrozenRestoreGraphReceiptSha256 $frozenRestoreGraphReceiptFacts.Sha256
        if ($LASTEXITCODE -ne 0) { throw 'Release MSI build failed.' }
    }
    Invoke-ReleaseStage -Name 'post-build-transaction-receipt' -Action {
        Assert-Rc12MsiImmutable
        Assert-Rc15MsiImmutable
        foreach ($path in @($pendingMsi, $pendingHash, $pendingActions, $pendingBridgeGate, $pendingCommandLineGate, $pendingBuildGateLog, $pendingPayloadHandoff)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release transaction output missing: $path" }
        }
        $script:candidate = Get-Item -LiteralPath $pendingMsi
        Write-SwSearchControlledSourceManifest -Path $pendingControlledManifest -Snapshot $baseline `
            -CandidateLastWriteTimeUtc $candidate.LastWriteTimeUtc
        $script:sourceReceipt = Test-SwSearchControlledSourceManifestReceipt -Root $root -ManifestPath $pendingControlledManifest `
            -CandidateLastWriteTimeUtc $candidate.LastWriteTimeUtc
        $script:controlledManifestHash = $sourceReceipt.ManifestSha256
        $script:candidateHash = (Get-FileHash -LiteralPath $pendingMsi -Algorithm SHA256).Hash
        $hashLine = [IO.File]::ReadAllText($pendingHash).Trim()
        if ($hashLine -cne "$candidateHash *$artifactName.msi") { throw 'Pending candidate SHA sidecar does not match the transaction MSI.' }
        Write-Output "RELEASE_TRANSACTION_RECEIPT: msiSha256=$candidateHash; controlledManifest=$($sourceReceipt.ManifestSha256); files=$($sourceReceipt.ControlledFileCount)"
    }

    # Verify-Msi's final Test-SwSearchControlledSourceManifest performs the
    # transaction-closing Compare-SwSearchControlledSourceSnapshot deep check.

    Invoke-ReleaseStage -Name 'verify-msi-final-safety-gates' -Action {
        $verifyPowerShellArguments = [Collections.Generic.List[string]]::new()
        foreach ($requiredArgument in @(
                [pscustomobject]@{ Name = 'PowerShell executable option -NoLogo'; Value = '-NoLogo' },
                [pscustomobject]@{ Name = 'PowerShell executable option -NoProfile'; Value = '-NoProfile' },
                [pscustomobject]@{ Name = 'PowerShell executable option -NonInteractive'; Value = '-NonInteractive' },
                [pscustomobject]@{ Name = 'PowerShell executable option -ExecutionPolicy'; Value = '-ExecutionPolicy' },
                [pscustomobject]@{ Name = 'PowerShell executable policy'; Value = 'Bypass' },
                [pscustomobject]@{ Name = 'PowerShell executable option -File'; Value = '-File' },
                [pscustomobject]@{ Name = 'Verify-Msi script path'; Value = (Join-Path $PSScriptRoot 'Verify-Msi.ps1') },
                [pscustomobject]@{ Name = 'Verify-Msi option -Version'; Value = '-Version' },
                [pscustomobject]@{ Name = 'Verify-Msi Version'; Value = $Version },
                [pscustomobject]@{ Name = 'Verify-Msi option -ReleaseCandidate'; Value = '-ReleaseCandidate' },
                [pscustomobject]@{ Name = 'Verify-Msi ReleaseCandidate'; Value = $ReleaseCandidate },
                [pscustomobject]@{ Name = 'Verify-Msi option -ProductCode'; Value = '-ProductCode' },
                [pscustomobject]@{ Name = 'Verify-Msi ProductCode'; Value = $ProductCode },
                [pscustomobject]@{ Name = 'Verify-Msi option -TransactionNonce'; Value = '-TransactionNonce' },
                [pscustomobject]@{ Name = 'Verify-Msi TransactionNonce'; Value = $transactionNonce },
                [pscustomobject]@{ Name = 'Verify-Msi option -ControlledManifest'; Value = '-ControlledManifest' },
                [pscustomobject]@{ Name = 'Verify-Msi ControlledManifest'; Value = $pendingControlledManifest },
                [pscustomobject]@{ Name = 'Verify-Msi option -PreviousMsi'; Value = '-PreviousMsi' },
                [pscustomobject]@{ Name = 'Verify-Msi PreviousMsi'; Value = $previousMsi },
                [pscustomobject]@{ Name = 'Verify-Msi switch -UseControlledSourceReceipt'; Value = '-UseControlledSourceReceipt' }
            )) {
            Add-ReleaseRequiredPowerShellArgument -Arguments $verifyPowerShellArguments `
                -Value $requiredArgument.Value -Name $requiredArgument.Name
        }

        # Verify-Msi permits standalone verification without this receipt, but
        # a release transaction must carry both optional receipt flag/value
        # pairs.  Do not add a bare flag or silently discard a missing value.
        $verifyFrozenReceiptPath = $frozenRestoreGraphReceipt
        $verifyFrozenReceiptHash = if ($null -eq $script:frozenRestoreGraphReceiptFacts) {
            $null
        }
        else {
            $script:frozenRestoreGraphReceiptFacts.Sha256
        }
        $hasVerifyFrozenReceiptPath = -not [String]::IsNullOrWhiteSpace($verifyFrozenReceiptPath)
        $hasVerifyFrozenReceiptHash = -not [String]::IsNullOrWhiteSpace($verifyFrozenReceiptHash)
        if ($hasVerifyFrozenReceiptPath -ne $hasVerifyFrozenReceiptHash) {
            throw 'Verify-Msi frozen restore graph receipt path and SHA256 must be supplied together.'
        }
        if (-not $hasVerifyFrozenReceiptPath) {
            throw 'Release Verify-Msi requires the frozen restore graph receipt and SHA256.'
        }
        foreach ($optionalReceiptArgument in @(
                [pscustomobject]@{ Name = 'Verify-Msi option -FrozenRestoreGraphReceipt'; Value = '-FrozenRestoreGraphReceipt' },
                [pscustomobject]@{ Name = 'Verify-Msi FrozenRestoreGraphReceipt'; Value = $verifyFrozenReceiptPath },
                [pscustomobject]@{ Name = 'Verify-Msi option -FrozenRestoreGraphReceiptSha256'; Value = '-FrozenRestoreGraphReceiptSha256' },
                [pscustomobject]@{ Name = 'Verify-Msi FrozenRestoreGraphReceiptSha256'; Value = $verifyFrozenReceiptHash }
            )) {
            Add-ReleaseRequiredPowerShellArgument -Arguments $verifyPowerShellArguments `
                -Value $optionalReceiptArgument.Value -Name $optionalReceiptArgument.Name
        }
        if ($UseControlledFrozenRestoreGraphReceipt) {
            Add-ReleaseRequiredPowerShellArgument -Arguments $verifyPowerShellArguments `
                -Value '-SkipFinalFrozenRestoreGraph' -Name 'Verify-Msi switch -SkipFinalFrozenRestoreGraph'
        }

        Invoke-BoundedPowerShellValidation -Label 'Release candidate static verification' `
            -TransactionLogRoot $transactionValidationLogRoot -LogStem 'release-candidate-static-verification' `
            -FailureDiagnosticsRoot $validationFailureDiagnosticsRoot `
            -PowerShellArguments $verifyPowerShellArguments.ToArray()
    }
    Invoke-ReleaseStage -Name 'post-verification-input-receipt' -Action {
        Assert-Rc12MsiImmutable
        Assert-Rc15MsiImmutable
    }
    Invoke-ReleaseStage -Name 'pre-publication-manifest-msi-rebinding' -Action {
        $finalRestoreReceiptHash = (Get-FileHash -LiteralPath $frozenRestoreGraphReceipt -Algorithm SHA256).Hash
        if ($finalRestoreReceiptHash -cne $frozenRestoreGraphReceiptFacts.Sha256) {
            throw "Frozen restore graph receipt changed after verification: expected=$($frozenRestoreGraphReceiptFacts.Sha256) actual=$finalRestoreReceiptHash"
        }
        $finalManifestHash = (Get-FileHash -LiteralPath $pendingControlledManifest -Algorithm SHA256).Hash
        if ($finalManifestHash -cne $controlledManifestHash) {
            throw "Controlled source manifest changed after verification: expected=$controlledManifestHash actual=$finalManifestHash"
        }
        $finalCandidateHash = (Get-FileHash -LiteralPath $pendingMsi -Algorithm SHA256).Hash
        if ($finalCandidateHash -cne $candidateHash) {
            throw "Pending MSI changed after verification: expected=$candidateHash actual=$finalCandidateHash"
        }
        Write-Output "PRE_PUBLICATION_MANIFEST_MSI_REBINDING: msiSha256=$finalCandidateHash; controlledManifest=$finalManifestHash; frozenRestoreReceipt=$finalRestoreReceiptHash"
    }
    foreach ($path in $pendingPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Verified transaction output missing: $path" }
    }
    $hashLine = [IO.File]::ReadAllText($pendingHash).Trim()
    if ($hashLine -cne "$candidateHash *$artifactName.msi") { throw 'Verified candidate SHA sidecar does not match the transaction MSI.' }

    Assert-TransactionValidationLogRoot
    if (Test-Path -LiteralPath $transactionValidationLogRoot) {
        Remove-Item -LiteralPath $transactionValidationLogRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $transactionValidationLogRoot) { throw 'Transaction validation log cleanup failed.' }
    Assert-TransactionBuildRoot
    if (Test-Path -LiteralPath $transactionBuildRoot) {
        Remove-Item -LiteralPath $transactionBuildRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $transactionBuildRoot) { throw 'Transaction-private payload snapshot cleanup failed.' }
    if (Test-Path -LiteralPath $pendingPayloadHandoff) {
        Remove-Item -LiteralPath $pendingPayloadHandoff -Force
    }
    if (Test-Path -LiteralPath $pendingPayloadHandoff) { throw 'Payload snapshot handoff cleanup failed.' }

    foreach ($entry in $publicationMap) {
        Move-Item -LiteralPath $entry.Pending -Destination $entry.Final
        $publishedSidecars.Add($entry.Final)
    }
    Move-Item -LiteralPath $pendingMsi -Destination $finalMsi
    $committed = $true

    Write-Output "Release candidate: $finalMsi"
    Write-Output "SHA256: $candidateHash"
    Write-Output "Controlled files: $($sourceReceipt.ControlledFileCount)"
    Write-Output 'Controlled source final deep freeze: passed inside Verify-Msi final safety gates.'
    Write-Output "Controlled manifest: $finalControlledManifest"
}
finally {
    if (-not $committed) {
        $cleanupFailures = [Collections.Generic.List[object]]::new()
        try {
            Assert-TransactionBuildRoot
            if (Test-Path -LiteralPath $transactionBuildRoot) {
                Remove-Item -LiteralPath $transactionBuildRoot -Recurse -Force
            }
        }
        catch { $cleanupFailures.Add($_) }
        try {
            Assert-TransactionValidationLogRoot
            if (Test-Path -LiteralPath $transactionValidationLogRoot) {
                Remove-Item -LiteralPath $transactionValidationLogRoot -Recurse -Force
            }
        }
        catch { $cleanupFailures.Add($_) }
        foreach ($path in $pendingPaths) {
            try {
                Assert-TransactionPath -Path $path
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
            }
            catch { $cleanupFailures.Add($_) }
        }
        foreach ($path in $publishedSidecars) {
            try {
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
            }
            catch { $cleanupFailures.Add($_) }
        }
        if ($cleanupFailures.Count -ne 0) { throw $cleanupFailures[0] }
    }
}
