$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'installer\Source-Freeze.ps1')
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('SwSearch-Freeze-Test-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $temporary 'src\Fixture\obj') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $temporary 'installer') | Out-Null
    $source = Join-Path $temporary 'src\Fixture\fixture.cs'
    [IO.File]::WriteAllText($source, 'before', [Text.UTF8Encoding]::new($false))
    $generated = [ordered]@{
        assets = 'src/Fixture/obj/project.assets.json'
        dgspec = 'src/Fixture/obj/Fixture.csproj.nuget.dgspec.json'
        props = 'src/Fixture/obj/Fixture.csproj.nuget.g.props'
        targets = 'src/Fixture/obj/Fixture.csproj.nuget.g.targets'
        cache = 'src/Fixture/obj/project.nuget.cache'
    }
    foreach ($entry in $generated.GetEnumerator()) {
        $path = Join-Path $temporary ($entry.Value.Replace('/', '\'))
        [IO.File]::WriteAllText($path, $entry.Key, [Text.UTF8Encoding]::new($false))
    }
    $lock = [ordered]@{
        schema = 2
        projects = @([ordered]@{
            assets = $generated.assets
            dgspec = $generated.dgspec
            props = $generated.props
            targets = $generated.targets
            cache = $generated.cache
        })
    }
    $lockPath = Join-Path $temporary 'installer\restore-assets.lock.json'
    [IO.File]::WriteAllText(
        $lockPath,
        ($lock | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))
    $baseline = @(Get-SwSearchControlledSourceSnapshot -Root $temporary)
    if ($baseline.Count -ne 7) { throw "Freeze fixture snapshot mismatch: $($baseline.Count)" }
    foreach ($expected in @(
            'installer/restore-assets.lock.json',
            $generated.assets,
            $generated.dgspec,
            $generated.props,
            $generated.targets,
            $generated.cache)) {
        if (@($baseline | Where-Object { $_.RelativePath -ceq $expected }).Count -ne 1) {
            throw "Freeze fixture did not promote the schema-2 generated input: $expected"
        }
    }
    $candidateTime = [DateTime]::UtcNow.AddSeconds(1)
    $clean = Compare-SwSearchControlledSourceSnapshot -Root $temporary -Baseline $baseline `
        -CandidateLastWriteTimeUtc $candidateTime
    Assert-SwSearchControlledSourceFrozen -Result $clean

    [IO.File]::WriteAllText($source, 'after', [Text.UTF8Encoding]::new($false))
    (Get-Item -LiteralPath $source).LastWriteTimeUtc = $candidateTime.AddSeconds(1)
    $changed = Compare-SwSearchControlledSourceSnapshot -Root $temporary -Baseline $baseline `
        -CandidateLastWriteTimeUtc $candidateTime
    if ($changed.HashChangedCount -ne 1 -or $changed.ModifiedAfterCandidateCount -ne 1) {
        throw 'Freeze gate did not detect the controlled post-candidate edit.'
    }
    try {
        Assert-SwSearchControlledSourceFrozen -Result $changed
        throw 'Freeze gate accepted a controlled post-candidate edit.'
    }
    catch {
        if (-not $_.Exception.Message.Contains('CONTROLLED_SOURCE_FREEZE_FAILED')) { throw }
    }

    [IO.File]::WriteAllText(
        (Join-Path $temporary ($generated.cache.Replace('/', '\'))),
        'cache-after',
        [Text.UTF8Encoding]::new($false))
    $generatedChanged = Compare-SwSearchControlledSourceSnapshot -Root $temporary -Baseline $baseline `
        -CandidateLastWriteTimeUtc ([DateTime]::UtcNow.AddMinutes(1))
    if ($generatedChanged.HashChangedCount -ne 2 -or
        @($generatedChanged.Changed | Where-Object { $_ -ceq $generated.cache }).Count -ne 1) {
        throw 'Freeze gate did not detect the schema-2 generated-input edit.'
    }
    Write-Output 'PASS controlled-source-freeze-schema2-positive-and-negative'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
