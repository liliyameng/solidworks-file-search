Set-StrictMode -Version Latest

function Get-SwSearchControlledSourceSnapshot {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($directoryName in @('src', 'tests', 'installer', 'scripts', 'docs')) {
        $directory = Join-Path $rootPath $directoryName
        if (Test-Path -LiteralPath $directory -PathType Container) {
            foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File) {
                $relative = $file.FullName.Substring($rootPath.Length).TrimStart('\', '/')
                $normalized = '\' + $relative.Replace('/', '\') + '\'
                if ($normalized -match '\\(?:bin|obj|TestResults|\.build)\\') { continue }
                $files.Add($file)
            }
        }
    }
    foreach ($name in @('SolidWorksSearch.sln', 'Directory.Build.props', 'Directory.Build.targets', 'global.json', 'NuGet.config', 'README.md')) {
        $path = Join-Path $rootPath $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add((Get-Item -LiteralPath $path)) }
    }
    foreach ($file in Get-ChildItem -LiteralPath $rootPath -File -Filter '*.ps1') { $files.Add($file) }

    # obj remains excluded by default. Only the exact, review-locked restore graph
    # inputs are promoted into the controlled set; no other mutable obj output is
    # treated as trusted candidate input.
    $restoreLockPath = Join-Path $rootPath 'installer\restore-assets.lock.json'
    $installerDirectory = Join-Path $rootPath 'installer'
    if ((Test-Path -LiteralPath $installerDirectory -PathType Container) -and
        -not (Test-Path -LiteralPath $restoreLockPath -PathType Leaf)) {
        throw "Frozen restore graph lock is missing: $restoreLockPath"
    }
    if (Test-Path -LiteralPath $restoreLockPath -PathType Leaf) {
        $restoreLock = Get-Content -LiteralPath $restoreLockPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($restoreLock.schema -ne 2) { throw 'Frozen restore graph lock schema is invalid.' }
        foreach ($project in @($restoreLock.projects)) {
            foreach ($relativePath in @(
                [string]$project.assets,
                [string]$project.dgspec,
                [string]$project.props,
                [string]$project.targets,
                [string]$project.cache)) {
                if ([IO.Path]::IsPathRooted($relativePath)) {
                    throw "Frozen restore graph path must be workspace-relative: $relativePath"
                }
                $path = [IO.Path]::GetFullPath((Join-Path $rootPath $relativePath))
                if (-not $path.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                    throw "Frozen restore graph input is missing or escaped the workspace: $relativePath"
                }
                $files.Add((Get-Item -LiteralPath $path))
            }
        }
    }

    return @($files | Sort-Object FullName -Unique | ForEach-Object {
        [pscustomobject]@{
            RelativePath = $_.FullName.Substring($rootPath.Length).TrimStart('\', '/').Replace('\', '/')
            FullName = $_.FullName
            LastWriteTimeUtc = $_.LastWriteTimeUtc
            SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}

function Compare-SwSearchControlledSourceSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][object[]]$Baseline,
        [Parameter(Mandatory = $true)][DateTime]$CandidateLastWriteTimeUtc
    )

    $current = @(Get-SwSearchControlledSourceSnapshot -Root $Root)
    $baselineByPath = @{}
    foreach ($entry in $Baseline) { $baselineByPath[[string]$entry.RelativePath] = $entry }
    $currentByPath = @{}
    foreach ($entry in $current) { $currentByPath[[string]$entry.RelativePath] = $entry }
    $added = @($current | Where-Object { -not $baselineByPath.ContainsKey([string]$_.RelativePath) })
    $missing = @($Baseline | Where-Object { -not $currentByPath.ContainsKey([string]$_.RelativePath) })
    $changed = @($current | Where-Object {
        $baselineByPath.ContainsKey([string]$_.RelativePath) -and
        [string]$baselineByPath[[string]$_.RelativePath].SHA256 -cne [string]$_.SHA256
    })
    $later = @($current | Where-Object { $_.LastWriteTimeUtc -gt $CandidateLastWriteTimeUtc })
    [pscustomobject]@{
        ControlledFileCount = $current.Count
        AddedCount = $added.Count
        MissingCount = $missing.Count
        HashChangedCount = $changed.Count
        ModifiedAfterCandidateCount = $later.Count
        Added = @($added | ForEach-Object { $_.RelativePath })
        Missing = @($missing | ForEach-Object { $_.RelativePath })
        Changed = @($changed | ForEach-Object { $_.RelativePath })
        ModifiedAfterCandidate = @($later | ForEach-Object { $_.RelativePath })
        Current = $current
    }
}

function Assert-SwSearchControlledSourceFrozen {
    param([Parameter(Mandatory = $true)]$Result)
    if ($Result.AddedCount -ne 0 -or $Result.MissingCount -ne 0 -or
        $Result.HashChangedCount -ne 0 -or $Result.ModifiedAfterCandidateCount -ne 0) {
        $details = @(
            "added=$($Result.AddedCount):$($Result.Added -join ',')",
            "missing=$($Result.MissingCount):$($Result.Missing -join ',')",
            "changed=$($Result.HashChangedCount):$($Result.Changed -join ',')",
            "afterCandidate=$($Result.ModifiedAfterCandidateCount):$($Result.ModifiedAfterCandidate -join ',')"
        ) -join '; '
        throw "CONTROLLED_SOURCE_FREEZE_FAILED. Candidate must be deprecated and rebuilt under a new RC identity. $details"
    }
}

function Write-SwSearchControlledSourceManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
        [Parameter(Mandatory = $true)][DateTime]$CandidateLastWriteTimeUtc
    )
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# SwSearch controlled source SHA256 manifest')
    $lines.Add('# CandidateLastWriteTimeUtc=' + $CandidateLastWriteTimeUtc.ToString('o'))
    $lines.Add('# ControlledFileCount=' + $Snapshot.Count)
    $lines.Add('# ModifiedAfterCandidateCount=0')
    foreach ($entry in $Snapshot | Sort-Object RelativePath) {
        $lines.Add(([string]$entry.SHA256) + ' *' + ([string]$entry.RelativePath))
    }
    [IO.File]::WriteAllLines([IO.Path]::GetFullPath($Path), $lines, [Text.UTF8Encoding]::new($false))
}

function Test-SwSearchControlledSourceManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][DateTime]$CandidateLastWriteTimeUtc
    )
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Controlled source manifest missing: $ManifestPath" }
    $lines = @(Get-Content -LiteralPath $ManifestPath -Encoding UTF8)
    $timeLine = $lines | Where-Object { $_.StartsWith('# CandidateLastWriteTimeUtc=', [StringComparison]::Ordinal) } | Select-Object -First 1
    if (-not $timeLine) { throw 'Controlled source manifest candidate time is missing.' }
    $recordedTime = [DateTime]::Parse(
        $timeLine.Substring('# CandidateLastWriteTimeUtc='.Length),
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    if ($recordedTime.Ticks -ne $CandidateLastWriteTimeUtc.ToUniversalTime().Ticks) {
        throw "Controlled source manifest candidate time mismatch: $recordedTime / $CandidateLastWriteTimeUtc"
    }
    $baseline = [Collections.Generic.List[object]]::new()
    foreach ($line in $lines) {
        if ($line.StartsWith('#', [StringComparison]::Ordinal) -or [String]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([A-F0-9]{64}) \*(.+)$') { throw "Invalid controlled source manifest line: $line" }
        $baseline.Add([pscustomobject]@{ RelativePath = $Matches[2]; SHA256 = $Matches[1] })
    }
    $result = Compare-SwSearchControlledSourceSnapshot -Root $Root -Baseline $baseline.ToArray() `
        -CandidateLastWriteTimeUtc $CandidateLastWriteTimeUtc
    Assert-SwSearchControlledSourceFrozen -Result $result
    return $result
}

function Test-SwSearchControlledSourceManifestReceipt {
    <#
    This is deliberately a metadata-only receipt check for the middle of a
    release transaction.  It validates that the transaction is still using the
    original controlled-file manifest and catches files written after the MSI,
    but it does not rehash the controlled tree.  The caller must still run
    Test-SwSearchControlledSourceManifest as its final, full freeze gate.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][DateTime]$CandidateLastWriteTimeUtc
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Controlled source manifest missing: $ManifestPath" }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $lines = @(Get-Content -LiteralPath $ManifestPath -Encoding UTF8)
    $timeLine = $lines | Where-Object { $_.StartsWith('# CandidateLastWriteTimeUtc=', [StringComparison]::Ordinal) } | Select-Object -First 1
    $countLine = $lines | Where-Object { $_.StartsWith('# ControlledFileCount=', [StringComparison]::Ordinal) } | Select-Object -First 1
    if (-not $timeLine -or -not $countLine) { throw 'Controlled source manifest receipt headers are missing.' }
    $recordedTime = [DateTime]::Parse(
        $timeLine.Substring('# CandidateLastWriteTimeUtc='.Length),
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    if ($recordedTime.Ticks -ne $CandidateLastWriteTimeUtc.ToUniversalTime().Ticks) {
        throw "Controlled source manifest candidate time mismatch: $recordedTime / $CandidateLastWriteTimeUtc"
    }
    $recordedCount = 0
    if (-not [int]::TryParse($countLine.Substring('# ControlledFileCount='.Length), [ref]$recordedCount) -or $recordedCount -lt 1) {
        throw 'Controlled source manifest controlled-file count is invalid.'
    }

    $seen = @{}
    $modifiedAfterCandidate = [Collections.Generic.List[string]]::new()
    $entryCount = 0
    foreach ($line in $lines) {
        if ($line.StartsWith('#', [StringComparison]::Ordinal) -or [String]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([A-F0-9]{64}) \*(.+)$') { throw "Invalid controlled source manifest line: $line" }
        $relativePath = $Matches[2]
        if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains('..')) {
            throw "Controlled source manifest path is unsafe: $relativePath"
        }
        if ($seen.ContainsKey($relativePath)) { throw "Controlled source manifest contains a duplicate path: $relativePath" }
        $seen[$relativePath] = $true
        $fullPath = [IO.Path]::GetFullPath((Join-Path $rootPath $relativePath))
        if (-not $fullPath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Controlled source receipt input is missing or escaped the workspace: $relativePath"
        }
        if ((Get-Item -LiteralPath $fullPath).LastWriteTimeUtc -gt $CandidateLastWriteTimeUtc) {
            $modifiedAfterCandidate.Add($relativePath)
        }
        $entryCount++
    }
    if ($entryCount -ne $recordedCount) {
        throw "Controlled source manifest entry count mismatch: expected=$recordedCount actual=$entryCount"
    }
    $result = [pscustomobject]@{
        ControlledFileCount = $entryCount
        AddedCount = 0
        MissingCount = 0
        HashChangedCount = 0
        ModifiedAfterCandidateCount = $modifiedAfterCandidate.Count
        Added = @()
        Missing = @()
        Changed = @()
        ModifiedAfterCandidate = $modifiedAfterCandidate.ToArray()
        ManifestSha256 = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash
    }
    Assert-SwSearchControlledSourceFrozen -Result $result
    return $result
}

function Write-SwSearchFrozenRestoreGraphReceipt {
    <#
    Records a successful *full* Assert-FrozenRestoreGraph run for one release
    transaction.  This receipt is intentionally small: it is not a substitute
    for the final deep check, only a tamper-evident hand-off for the bounded
    middle of the same transaction.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{32}$')][string]$SourceTransactionNonce,
        [Parameter(Mandatory = $true)][DateTime]$ValidatedUtc
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $transactionRootPath = [IO.Path]::GetFullPath($TransactionRoot).TrimEnd('\', '/')
    $installerBuildRoot = [IO.Path]::GetFullPath((Join-Path $rootPath 'installer\.build')).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    if (-not ($transactionRootPath + [IO.Path]::DirectorySeparatorChar).StartsWith(
            $installerBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Frozen restore graph receipt transaction root escaped installer build root: $transactionRootPath"
    }
    $receiptPath = Join-Path $transactionRootPath 'frozen-restore-graph.receipt.json'
    if (Test-Path -LiteralPath $receiptPath) { throw "Frozen restore graph receipt already exists: $receiptPath" }
    $manifestPath = Join-Path $rootPath 'installer\restore-assets.lock.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Frozen restore graph manifest is missing: $manifestPath" }
    $receipt = [ordered]@{
        schema = 1
        sourceTransactionNonce = $SourceTransactionNonce
        manifestPath = 'installer/restore-assets.lock.json'
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        validatedUtc = $ValidatedUtc.ToUniversalTime().ToString('o')
    }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{
        Path = $receiptPath
        Sha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash
        ManifestSha256 = [string]$receipt.manifestSha256
        ValidatedUtc = [string]$receipt.validatedUtc
    }
}

function Test-SwSearchFrozenRestoreGraphReceipt {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{32}$')][string]$SourceTransactionNonce,
        [Parameter(Mandatory = $true)][string]$ReceiptPath,
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-F0-9]{64}$')][string]$ReceiptSha256
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $transactionRootPath = [IO.Path]::GetFullPath($TransactionRoot).TrimEnd('\', '/')
    $expectedReceiptPath = [IO.Path]::GetFullPath((Join-Path $transactionRootPath 'frozen-restore-graph.receipt.json'))
    $actualReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
    if (-not $actualReceiptPath.Equals($expectedReceiptPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Frozen restore graph receipt must be the current transaction path: $expectedReceiptPath"
    }
    $installerBuildRoot = [IO.Path]::GetFullPath((Join-Path $rootPath 'installer\.build')).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    if (-not ($transactionRootPath + [IO.Path]::DirectorySeparatorChar).StartsWith(
            $installerBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Frozen restore graph receipt transaction root escaped installer build root: $transactionRootPath"
    }
    if (-not (Test-Path -LiteralPath $actualReceiptPath -PathType Leaf)) { throw "Frozen restore graph receipt is missing: $actualReceiptPath" }
    $actualReceiptHash = (Get-FileHash -LiteralPath $actualReceiptPath -Algorithm SHA256).Hash
    if ($actualReceiptHash -cne $ReceiptSha256) {
        throw "Frozen restore graph receipt hash mismatch: expected=$ReceiptSha256 actual=$actualReceiptHash"
    }
    try { $receipt = Get-Content -LiteralPath $actualReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Frozen restore graph receipt JSON is invalid: $($_.Exception.Message)" }
    $actualProperties = @($receipt.psobject.Properties.Name | Sort-Object)
    $expectedProperties = @('manifestPath', 'manifestSha256', 'schema', 'sourceTransactionNonce', 'validatedUtc')
    if ($actualProperties.Count -ne $expectedProperties.Count -or
        @(Compare-Object -ReferenceObject $expectedProperties -DifferenceObject $actualProperties).Count -ne 0) {
        throw "Frozen restore graph receipt property set is invalid: $($actualProperties -join ', ')"
    }
    if ([int]$receipt.schema -ne 1 -or
        [string]$receipt.sourceTransactionNonce -cne $SourceTransactionNonce -or
        [string]$receipt.manifestPath -cne 'installer/restore-assets.lock.json' -or
        [string]$receipt.manifestSha256 -notmatch '^[A-F0-9]{64}$') {
        throw 'Frozen restore graph receipt identity is invalid.'
    }
    try { $validatedUtc = [DateTime]::Parse([string]$receipt.validatedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
    catch { throw 'Frozen restore graph receipt validation time is invalid.' }
    $manifestPath = Join-Path $rootPath 'installer\restore-assets.lock.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Frozen restore graph manifest is missing: $manifestPath" }
    $actualManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if ($actualManifestHash -cne [string]$receipt.manifestSha256) {
        throw "Frozen restore graph manifest hash mismatch: expected=$($receipt.manifestSha256) actual=$actualManifestHash"
    }
    return [pscustomobject]@{
        Path = $actualReceiptPath
        Sha256 = $actualReceiptHash
        ManifestSha256 = [string]$receipt.manifestSha256
        ValidatedUtc = $validatedUtc
    }
}
