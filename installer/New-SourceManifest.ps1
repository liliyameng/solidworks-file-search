[CmdletBinding()]
param(
    [string]$Version = '1.2.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$outputPath = Join-Path $artifactsRoot "SwSearch-v$Version-source-manifest.sha256"

$rootFiles = @(
    '.gitignore',
    'Directory.Build.props',
    'README.md',
    'SolidWorksSearch.sln'
)
$sourceRoots = @('docs', 'installer', 'src', 'tests')
$excludedDirectoryNames = @(
    '.git', '.packages', '.packages-rc48', '.packages-rc48-r2', '.packages-rc48-r3', '.build', 'artifacts', 'bin', 'obj', 'TestResults'
)
$excludedExtensions = @(
    '.7z', '.cache', '.coverage', '.db', '.dll', '.exe', '.log', '.msi',
    '.pdb', '.suo', '.tmp', '.trx', '.user', '.zip'
)

function Test-ManifestInput {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)

    $relativePath = $File.FullName.Substring($repoRoot.Length).TrimStart('\', '/')
    $segments = $relativePath -split '[\\/]'
    if (@($segments | Where-Object { $_ -in $excludedDirectoryNames }).Count -ne 0) {
        return $false
    }
    if ($File.Extension.ToLowerInvariant() -in $excludedExtensions) {
        return $false
    }
    if ($relativePath -match '^tests[\\/]installer[\\/]sandbox[\\/]bundle(?:-|[\\/]|$)') {
        return $false
    }
    return $true
}

$inputs = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($relativePath in $rootFiles) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required manifest input is missing: $relativePath"
    }
    $inputs.Add((Get-Item -LiteralPath $path))
}
foreach ($relativeRoot in $sourceRoots) {
    $path = Join-Path $repoRoot $relativeRoot
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required manifest source root is missing: $relativeRoot"
    }
    foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File | Where-Object { Test-ManifestInput $_ }) {
        $inputs.Add($file)
    }
}

$uniqueInputs = @($inputs | Sort-Object FullName -Unique)
if ($uniqueInputs.Count -eq 0) {
    throw 'Source manifest input set is empty.'
}

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
$generatedUtc = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# SwSearch frozen source manifest')
$lines.Add("# Version: $Version")
$lines.Add("# Generated-UTC: $generatedUtc")
$lines.Add('# Algorithm: SHA256')
$lines.Add('# Include rule: root .gitignore/Directory.Build.props/README.md/SolidWorksSearch.sln plus recursive docs/, installer/, src/, tests/.')
$lines.Add('# Exclude rule: .git/.packages/.packages-rc48/.packages-rc48-r2/.packages-rc48-r3/.build/artifacts/bin/obj/TestResults, generated tests/installer/sandbox/bundle*, and binary/archive/cache/temp extensions (.exe/.dll/.msi/.7z/.zip/.pdb/.db/.log/.tmp/.trx/.coverage/.cache/.user/.suo).')
$lines.Add("# File-Count: $($uniqueInputs.Count)")
foreach ($file in $uniqueInputs) {
    $relativePath = $file.FullName.Substring($repoRoot.Length).TrimStart('\', '/').Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $lines.Add("$hash *$relativePath")
}

[System.IO.File]::WriteAllLines($outputPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Output "Source manifest: $outputPath"
Write-Output "Generated UTC: $generatedUtc"
Write-Output "Files: $($uniqueInputs.Count)"
