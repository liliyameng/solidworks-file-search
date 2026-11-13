$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'installer\Payload-Manifest.ps1')

function Write-FixturePayload {
    param([string]$Directory,[string]$Text)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        New-Item -ItemType Directory -Path $Directory | Out-Null
    }
    $file = Join-Path $Directory 'payload.bin'
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    [IO.File]::WriteAllBytes($file, $bytes)
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        (Join-Path $Directory 'SHA256SUMS.txt'),
        "$hash $($bytes.Length) *payload.bin`r`n",
        [Text.UTF8Encoding]::new($false))
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
$testRoot = Join-Path $tempBase ('swsearch-payload-snapshot-' + [Guid]::NewGuid().ToString('N'))
if (-not $testRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($testRoot) -notmatch '^swsearch-payload-snapshot-[0-9a-f]{32}$') {
    throw 'Fixture root identity is invalid.'
}
$shared = Join-Path $testRoot 'shared'
$snapshot = Join-Path $testRoot 'snapshot'
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Write-FixturePayload -Directory $shared -Text 'payload-A'
    $frozen = New-SwSearchPayloadSnapshot -PayloadRoot $shared `
        -ManifestPath (Join-Path $shared 'SHA256SUMS.txt') -SnapshotRoot $snapshot
    $snapshotFileHashA = (Get-FileHash -LiteralPath (Join-Path $snapshot 'payload.bin') -Algorithm SHA256).Hash

    # Replace the shared directory's complete, self-consistent A identity with B.
    Write-FixturePayload -Directory $shared -Text 'payload-B'
    $sharedB = Test-SwSearchPayloadManifest -PayloadRoot $shared `
        -ManifestPath (Join-Path $shared 'SHA256SUMS.txt')
    if ($sharedB.EntriesDigest -ceq $frozen.EntriesDigest) { throw 'A/B fixture identities unexpectedly match.' }
    $unchanged = Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $snapshot `
        -ManifestSha256 $frozen.ManifestSha256 -EntriesDigest $frozen.EntriesDigest `
        -FileCount $frozen.FileCount
    if ((Get-FileHash -LiteralPath (Join-Path $snapshot 'payload.bin') -Algorithm SHA256).Hash -cne $snapshotFileHashA -or
        $unchanged.EntriesDigest -cne $frozen.EntriesDigest) {
        throw 'Shared A-to-B replacement changed the frozen transaction snapshot.'
    }

    [IO.File]::WriteAllText((Join-Path $snapshot 'payload.bin'), 'snapshot-tamper', [Text.UTF8Encoding]::new($false))
    $tamperRejected = $false
    try {
        $null = Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $snapshot `
            -ManifestSha256 $frozen.ManifestSha256 -EntriesDigest $frozen.EntriesDigest `
            -FileCount $frozen.FileCount
    }
    catch { $tamperRejected = $true }
    if (-not $tamperRejected) { throw 'Snapshot-internal replacement was not rejected.' }
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
