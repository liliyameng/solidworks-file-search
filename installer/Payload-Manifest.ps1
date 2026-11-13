Set-StrictMode -Version Latest

function Get-SwSearchPayloadLeafFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "PAYLOAD_REPARSE_OR_NON_DIRECTORY_ROOT: $($rootItem.FullName)"
    }
    $directories = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $directories.Push($rootItem)
    while ($directories.Count -ne 0) {
        $directory = $directories.Pop()
        foreach ($child in @(Get-ChildItem -LiteralPath $directory.FullName -Force)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "PAYLOAD_REPARSE_POINT_FORBIDDEN: $($child.FullName)"
            }
            if ($child.PSIsContainer) { $directories.Push($child) }
            elseif ($child -is [IO.FileInfo]) { $files.Add($child) }
            else { throw "PAYLOAD_NON_FILE_ENTRY_FORBIDDEN: $($child.FullName)" }
        }
    }
    return $files.ToArray()
}

function Get-SwSearchPayloadEntriesDigest {
    param([Parameter(Mandatory = $true)][object[]]$Entries)

    [string[]]$canonical = @($Entries | ForEach-Object {
        "$($_.RelativePath)`0$($_.Length)`0$($_.SHA256)"
    })
    [Array]::Sort($canonical, [StringComparer]::Ordinal)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([String]::Join("`n", $canonical))
        return [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '')
    }
    finally { $algorithm.Dispose() }
}

function Test-SwSearchPayloadManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )

    $root = [IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\', '/')
    $manifest = [IO.Path]::GetFullPath($ManifestPath)
    $expectedManifest = [IO.Path]::GetFullPath((Join-Path $root 'SHA256SUMS.txt'))
    if (-not $manifest.Equals($expectedManifest, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "PAYLOAD_MANIFEST_PATH_INVALID: $manifest"
    }

    $manifestBytes = [IO.File]::ReadAllBytes($manifest)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $manifestSha256 = [BitConverter]::ToString($algorithm.ComputeHash($manifestBytes)).Replace('-', '') }
    finally { $algorithm.Dispose() }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $manifestText = $strictUtf8.GetString($manifestBytes)
    $lineList = [Collections.Generic.List[string]]::new()
    $reader = [IO.StringReader]::new($manifestText)
    try {
        while (($line = $reader.ReadLine()) -ne $null) { $lineList.Add($line) }
    }
    finally { $reader.Dispose() }
    $lines = $lineList.ToArray()
    if ($lines.Count -eq 0) { throw 'PAYLOAD_MANIFEST_EMPTY' }
    $exactPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $caseFoldedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $entries = [Collections.Generic.List[object]]::new()

    foreach ($line in $lines) {
        if ($line -notmatch '^([A-F0-9]{64}) ([0-9]+) \*(.+)$') {
            throw "PAYLOAD_MANIFEST_FORMAT_INVALID: $line"
        }
        $sha256 = $Matches[1]
        [long]$length = 0
        if (-not [long]::TryParse(
                $Matches[2],
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$length)) {
            throw "PAYLOAD_MANIFEST_LENGTH_INVALID: $line"
        }
        $relativePath = $Matches[3]
        if ([String]::IsNullOrWhiteSpace($relativePath) -or
            $relativePath.Contains('\') -or
            [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.StartsWith('/', [StringComparison]::Ordinal) -or
            $relativePath.IndexOf(':') -ge 0 -or
            @($relativePath.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0) {
            throw "PAYLOAD_MANIFEST_RELATIVE_PATH_INVALID: $relativePath"
        }
        $segments = @($relativePath.Split('/'))
        if ($segments.Count -eq 0 -or
            @($segments | Where-Object { [String]::IsNullOrEmpty($_) -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
            throw "PAYLOAD_MANIFEST_PATH_SEGMENT_INVALID: $relativePath"
        }
        if (-not $exactPaths.Add($relativePath)) {
            throw "PAYLOAD_MANIFEST_DUPLICATE_PATH: $relativePath"
        }
        if (-not $caseFoldedPaths.Add($relativePath)) {
            throw "PAYLOAD_MANIFEST_CASE_COLLISION: $relativePath"
        }

        $fullPath = [IO.Path]::GetFullPath((Join-Path $root $relativePath.Replace('/', '\')))
        if (-not $fullPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.Equals($manifest, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "PAYLOAD_MANIFEST_FILE_MISSING_OR_ESCAPED: $relativePath"
        }
        $item = Get-Item -LiteralPath $fullPath
        if ($item.Length -ne $length) {
            throw "PAYLOAD_MANIFEST_LENGTH_MISMATCH: $relativePath"
        }
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if ($actualHash -cne $sha256) {
            throw "PAYLOAD_MANIFEST_HASH_MISMATCH: $relativePath"
        }
        $entries.Add([pscustomobject]@{
            RelativePath = $relativePath
            FullName = $fullPath
            Length = $length
            SHA256 = $sha256
        })
    }

    $actualExact = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $actualCaseFolded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $actualFiles = @(Get-SwSearchPayloadLeafFiles -Root $root | Where-Object {
        -not $_.FullName.Equals($manifest, [StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($file in $actualFiles) {
        $relativePath = $file.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
        if (-not $actualExact.Add($relativePath) -or -not $actualCaseFolded.Add($relativePath)) {
            throw "PAYLOAD_ACTUAL_PATH_COLLISION: $relativePath"
        }
    }
    if ($actualExact.Count -ne $exactPaths.Count) {
        throw "PAYLOAD_MANIFEST_FILE_SET_COUNT_MISMATCH: manifest=$($exactPaths.Count);actual=$($actualExact.Count)"
    }
    foreach ($relativePath in $exactPaths) {
        if (-not $actualExact.Contains($relativePath)) {
            throw "PAYLOAD_MANIFEST_FILE_SET_MISMATCH: $relativePath"
        }
    }
    foreach ($relativePath in $actualExact) {
        if (-not $exactPaths.Contains($relativePath)) {
            throw "PAYLOAD_MANIFEST_EXTRA_FILE: $relativePath"
        }
    }

    $entryArray = $entries.ToArray()
    [pscustomobject]@{
        ManifestPath = $manifest
        ManifestSha256 = $manifestSha256
        EntriesDigest = Get-SwSearchPayloadEntriesDigest -Entries $entryArray
        FileCount = $entryArray.Count
        Entries = $entryArray
    }
}

function Assert-SwSearchPayloadSnapshotIdentity {
    param(
        [Parameter(Mandatory = $true)][string]$SnapshotRoot,
        [Parameter(Mandatory = $true)][string]$ManifestSha256,
        [Parameter(Mandatory = $true)][string]$EntriesDigest,
        [Parameter(Mandatory = $true)][int]$FileCount
    )

    $manifest = Join-Path $SnapshotRoot 'SHA256SUMS.txt'
    $facts = Test-SwSearchPayloadManifest -PayloadRoot $SnapshotRoot -ManifestPath $manifest
    if ($facts.ManifestSha256 -cne $ManifestSha256 -or
        $facts.EntriesDigest -cne $EntriesDigest -or
        $facts.FileCount -ne $FileCount) {
        throw 'PAYLOAD_SNAPSHOT_FROZEN_IDENTITY_MISMATCH'
    }
    return $facts
}

function New-SwSearchPayloadSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$SnapshotRoot
    )

    $sourceFacts = Test-SwSearchPayloadManifest -PayloadRoot $PayloadRoot -ManifestPath $ManifestPath
    $snapshot = [IO.Path]::GetFullPath($SnapshotRoot)
    if (Test-Path -LiteralPath $snapshot) { throw "PAYLOAD_SNAPSHOT_ALREADY_EXISTS: $snapshot" }
    New-Item -ItemType Directory -Path $snapshot | Out-Null
    $complete = $false
    try {
        [IO.File]::Copy($sourceFacts.ManifestPath, (Join-Path $snapshot 'SHA256SUMS.txt'), $false)
        foreach ($entry in $sourceFacts.Entries) {
            $destination = [IO.Path]::GetFullPath((Join-Path $snapshot $entry.RelativePath.Replace('/', '\')))
            if (-not $destination.StartsWith($snapshot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw "PAYLOAD_SNAPSHOT_DESTINATION_ESCAPED: $($entry.RelativePath)"
            }
            $parent = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
            }
            [IO.File]::Copy($entry.FullName, $destination, $false)
        }
        $snapshotFacts = Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $snapshot `
            -ManifestSha256 $sourceFacts.ManifestSha256 -EntriesDigest $sourceFacts.EntriesDigest `
            -FileCount $sourceFacts.FileCount
        $complete = $true
        return $snapshotFacts
    }
    finally {
        if (-not $complete -and (Test-Path -LiteralPath $snapshot)) {
            Remove-Item -LiteralPath $snapshot -Recurse -Force
        }
    }
}
