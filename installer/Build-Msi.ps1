[CmdletBinding()]
param(
    [string]$Version = '1.3.18',
    [ValidatePattern('^rc\d+$')][string]$ReleaseCandidate = 'rc95',
    [string]$ProductCode = 'B4EFF1DC-82A4-4302-B335-DBD08617D963',
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{32}$')][string]$TransactionNonce,
    [switch]$ReleaseOrchestrated,
    [string]$FrozenRestoreGraphReceipt,
    [ValidatePattern('^[A-F0-9]{64}$')][string]$FrozenRestoreGraphReceiptSha256
)

$ErrorActionPreference = 'Stop'
$latestShippedProductVersion = [Version]'1.3.12'
$candidateProductVersion = [Version]$Version
if ($candidateProductVersion -le $latestShippedProductVersion) {
    throw "MSI ProductVersion must be greater than the latest shipped version ${latestShippedProductVersion}: $Version"
}
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$candidateSuffix = $ReleaseCandidate
$buildRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".build\msi-$Version-$candidateSuffix-$TransactionNonce"))
$payloadRoot = Join-Path $buildRoot 'published-payload'
$payloadManifestPath = Join-Path $payloadRoot 'SHA256SUMS.txt'
$publishStageRoot = Join-Path $buildRoot 'publish-stage'
$packageOutput = Join-Path $buildRoot 'package'
$installerActionsOutput = Join-Path $buildRoot 'installer-actions'
$payloadSnapshotRoot = Join-Path $buildRoot 'payload-snapshot'
$generatedPayloadWxs = Join-Path $buildRoot 'PayloadFiles.generated.wxs'
$artifactName = "SwSearch-x64-v$Version-$candidateSuffix"
$finalMsi = Join-Path $artifactsRoot "$artifactName.msi"
$hashManifest = Join-Path $artifactsRoot "$artifactName.sha256.txt"
$installerActionsManifest = Join-Path $artifactsRoot "installer-actions-v$Version-$candidateSuffix.sha256.json"
$bridgeGateArtifact = Join-Path $artifactsRoot "installer-bridge-gate-v$Version-$candidateSuffix.exe"
$commandLineGateArtifact = Join-Path $artifactsRoot "installer-command-line-gate-v$Version-$candidateSuffix.exe"
$commandLineGateLog = Join-Path $artifactsRoot "installer-command-line-gate-v$Version-$candidateSuffix.log"
$pendingPrefix = Join-Path $artifactsRoot ".pending-$TransactionNonce-$artifactName"
$pendingMsi = "$pendingPrefix.msi"
$pendingHashManifest = "$pendingPrefix.sha256.txt"
$pendingInstallerActionsManifest = "$pendingPrefix.installer-actions.sha256.json"
$pendingBridgeGateArtifact = "$pendingPrefix.installer-bridge-gate.exe"
$pendingCommandLineGateArtifact = "$pendingPrefix.installer-command-line-gate.exe"
$pendingCommandLineGateLog = "$pendingPrefix.installer-command-line-gate.log"
$pendingPayloadHandoff = "$pendingPrefix.payload-snapshot.json"
$pendingOutputs = @(
    $pendingMsi,
    $pendingHashManifest,
    $pendingInstallerActionsManifest,
    $pendingBridgeGateArtifact,
    $pendingCommandLineGateArtifact,
    $pendingCommandLineGateLog,
    $pendingPayloadHandoff)
$handoffReady = $false
$nugetPackages = Join-Path $repoRoot '.packages-rc48-r3'
$restoreGraphGate = Join-Path $PSScriptRoot 'Assert-FrozenRestoreGraph.ps1'
. (Join-Path $PSScriptRoot 'Payload-Manifest.ps1')
. (Join-Path $PSScriptRoot 'Source-Freeze.ps1')

if (-not $ReleaseOrchestrated) {
    throw 'Candidate MSI builds must run through installer\Build-Release.ps1 so the controlled-source freeze is enforced.'
}
if ($env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH -ceq '1') {
    throw 'SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH is forbidden; frozen restore graph validation cannot be bypassed.'
}
$hasFrozenRestoreGraphReceipt = -not [String]::IsNullOrWhiteSpace($FrozenRestoreGraphReceipt)
if ($hasFrozenRestoreGraphReceipt -ne (-not [String]::IsNullOrWhiteSpace($FrozenRestoreGraphReceiptSha256))) {
    throw 'Frozen restore graph receipt path and SHA256 must be supplied together.'
}
$restoreGateArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$restoreGraphGate,'-Root',$repoRoot)

function Assert-Rc12Immutable {
    $rc12 = Join-Path $artifactsRoot 'SwSearch-x64-v1.2.0-rc12.msi'
    if (-not (Test-Path -LiteralPath $rc12 -PathType Leaf)) { throw 'RC12 immutable artifact is missing.' }
    $item = Get-Item -LiteralPath $rc12
    $hash = (Get-FileHash -LiteralPath $rc12 -Algorithm SHA256).Hash
    if ($item.Length -ne 57687391 -or $hash -cne '4FC6A7E126462377FC5D43A607884FADB12A0BFE3E6C638FCAA8D92D2571DFD5') {
        throw 'RC12 immutable artifact identity mismatch.'
    }
}

function Assert-Rc13Immutable {
    $rc13 = Join-Path $artifactsRoot 'SwSearch-x64-v1.2.0-rc13-user-test.msi'
    if (-not (Test-Path -LiteralPath $rc13 -PathType Leaf)) { throw 'RC13 immutable USER-TEST artifact is missing.' }
    $item = Get-Item -LiteralPath $rc13
    $hash = (Get-FileHash -LiteralPath $rc13 -Algorithm SHA256).Hash
    if ($item.Length -ne 93057147 -or $hash -cne '0F7D9B65A11B11B4C03EDC475120F390B670BC623EE0365316B9E87B4F1BA51C') {
        throw 'RC13 immutable USER-TEST artifact identity mismatch.'
    }
}

function Assert-Rc14Immutable {
    $rc14 = Join-Path $artifactsRoot 'SwSearch-x64-v1.2.0-rc14-user-test.msi'
    if (-not (Test-Path -LiteralPath $rc14 -PathType Leaf)) { throw 'RC14 immutable USER-TEST artifact is missing.' }
    $item = Get-Item -LiteralPath $rc14
    $hash = (Get-FileHash -LiteralPath $rc14 -Algorithm SHA256).Hash
    if ($item.Length -ne 93069604 -or $hash -cne '381B44078A9FA772A7FCE9E32AEF1E086D05DDE6F9354066B7DAB8A0C9E43BDE') {
        throw 'RC14 immutable USER-TEST artifact identity mismatch.'
    }
}

function Assert-Rc15Immutable {
    $rc15 = Join-Path $artifactsRoot 'SwSearch-x64-v1.2.0-rc15.msi'
    if (-not (Test-Path -LiteralPath $rc15 -PathType Leaf)) { throw 'RC15 immutable artifact is missing.' }
    $item = Get-Item -LiteralPath $rc15
    $hash = (Get-FileHash -LiteralPath $rc15 -Algorithm SHA256).Hash
    if ($item.Length -ne 93065427 -or $hash -cne 'B91BF1912B5F57EFF1307A27F7FA51094026E2FD12B7300A07C0A56C02359E85') {
        throw 'RC15 immutable artifact identity mismatch.'
    }
}

function Get-StableHashHex {
    param([Parameter(Mandatory = $true)][string]$Text)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return [System.BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function ConvertTo-XmlAttribute {
    param([Parameter(Mandatory = $true)][string]$Text)
    return [System.Security.SecurityElement]::Escape($Text)
}

function Invoke-InstallerCommandLineGate {
    param(
        [Parameter(Mandatory = $true)][string]$GatePath,
        [Parameter(Mandatory = $true)][string]$MsiPath,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [int]$TimeoutMilliseconds = 30000
    )

    $internalProbeBudgetMilliseconds = 4 * 5000
    if ($TimeoutMilliseconds -le $internalProbeBudgetMilliseconds) {
        throw 'MSI command-line runner hard deadline must exceed the native gate internal probe budget.'
    }
    $gate = Get-Item -LiteralPath $GatePath
    $candidate = Get-Item -LiteralPath $MsiPath
    $gateHashBefore = (Get-FileHash -LiteralPath $GatePath -Algorithm SHA256).Hash
    $candidateHashBefore = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
    $startedUtc = [DateTime]::UtcNow
    $exitCode = -1
    $timedOut = $false
    $started = $false
    $cleanupFailed = $false
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $runRoot = Join-Path $tempRoot ('swsearch-msi-command-line-gate-' + [Guid]::NewGuid().ToString('N'))
    if (-not $runRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($runRoot) -notmatch '^swsearch-msi-command-line-gate-[0-9a-f]{32}$') {
        throw 'MSI command-line gate temp root identity is invalid.'
    }
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $process = [Diagnostics.Process]::new()
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $GatePath
    $startInfo.Arguments = '"--verify-msi" "' + $MsiPath.Replace('"', '\"') + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $runRoot
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'MSI command-line gate could not be started.' }
        $started = $true
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $timedOut = $true
            # The native gate owns every probe in a KILL_ON_JOB_CLOSE Job. Killing
            # this self-created gate root closes that Job and therefore its tree.
            $process.Kill()
            if (-not $process.WaitForExit(5000)) {
                throw 'MSI command-line gate process tree did not close after hard deadline.'
            }
        }
        $exitCode = $process.ExitCode
    }
    finally {
        try {
            if ($started -and -not $process.HasExited) {
                $process.Kill()
                if (-not $process.WaitForExit(5000)) { $cleanupFailed = $true }
            }
        }
        finally {
            $process.Dispose()
            try {
                $gateHashAfter = (Get-FileHash -LiteralPath $GatePath -Algorithm SHA256).Hash
                $candidateHashAfter = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
                $lines = @(
                    'SwSearch installer command-line gate',
                    'Mode: verify-msi',
                    "StartedUtc: $($startedUtc.ToString('o'))",
                    "CompletedUtc: $([DateTime]::UtcNow.ToString('o'))",
                    "GateBytes: $($gate.Length)",
                    "GateSHA256Before: $gateHashBefore",
                    "GateSHA256After: $gateHashAfter",
                    "MsiBytes: $($candidate.Length)",
                    "MsiSHA256Before: $candidateHashBefore",
                    "MsiSHA256After: $candidateHashAfter",
                    "TimedOut: $timedOut",
                    "CleanupFailed: $cleanupFailed",
                    "ExitCode: $exitCode",
                    ('Result: ' + $(if (-not $timedOut -and -not $cleanupFailed -and $exitCode -eq 0 -and
                            $gateHashBefore -ceq $gateHashAfter -and
                            $candidateHashBefore -ceq $candidateHashAfter) { 'PASS' } else { 'FAIL' }))
                )
                [IO.File]::WriteAllLines($LogPath, $lines, [Text.UTF8Encoding]::new($false))
            }
            finally {
                if (Test-Path -LiteralPath $runRoot) {
                    Remove-Item -LiteralPath $runRoot -Recurse -Force
                }
            }
        }
    }
    if ($cleanupFailed) { throw 'MSI command-line gate self-created process tree cleanup failed.' }
    if ($timedOut) { throw 'MSI command-line gate exceeded its strict timeout.' }
    if ($exitCode -ne 0) { throw "MSI command-line gate failed with exit code $exitCode." }
    if ($gateHashBefore -cne $gateHashAfter -or $candidateHashBefore -cne $candidateHashAfter) {
        throw 'MSI command-line gate changed its executable or candidate bytes.'
    }
}

function New-PayloadWixSource {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadDirectory,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $files = @(Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -File | Sort-Object FullName)
    if ($files.Count -eq 0) { throw 'Cannot generate MSI component source from an empty payload.' }
    $forbiddenInstallerInternals = @($files | Where-Object {
        $_.Name -match '(?i)(InstallerCoordinator|InstallerMsiBridge|InstallerMsiCommandLineGate|WatchdogHelper|NativeHelper|\.pdb$)' -or
        $_.FullName -match '(?i)[\\/](?:tests?|TestResults)[\\/]'
    })
    if ($forbiddenInstallerInternals.Count -ne 0) {
        throw "Installer-only coordinator/bridge/helper/test content leaked into payload: $($forbiddenInstallerInternals.FullName -join ', ')"
    }

    $relativeFiles = foreach ($file in $files) {
        $relativePath = $file.FullName.Substring($PayloadDirectory.Length).TrimStart('\', '/')
        [pscustomobject]@{
            File = $file
            RelativePath = $relativePath
            RelativeDirectory = [System.IO.Path]::GetDirectoryName($relativePath)
        }
    }
    $directories = @($relativeFiles.RelativeDirectory | Where-Object { $_ } | Sort-Object -Unique)
    foreach ($directory in $directories) {
        if ($directory.Contains('\') -or $directory.Contains('/')) {
            throw "Nested payload directory requires explicit hierarchy support: $directory"
        }
    }

    $directoryIds = @{}
    foreach ($directory in $directories) {
        $directoryIds[$directory] = 'PayloadDir_' + (Get-StableHashHex "SwSearch.PayloadDirectory|$($directory.ToLowerInvariant())").Substring(0, 20)
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    if ($directories.Count -ne 0) {
        [void]$builder.AppendLine('  <Fragment>')
        [void]$builder.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
        foreach ($directory in $directories) {
            $directoryId = $directoryIds[$directory]
            $directoryName = ConvertTo-XmlAttribute $directory
            [void]$builder.AppendLine("      <Directory Id=`"$directoryId`" Name=`"$directoryName`" />")
        }
        [void]$builder.AppendLine('    </DirectoryRef>')
        [void]$builder.AppendLine('  </Fragment>')
    }
    [void]$builder.AppendLine('  <Fragment>')
    [void]$builder.AppendLine('    <ComponentGroup Id="ApplicationFiles">')
    [void]$builder.AppendLine('      <Component Id="PayloadDirectoryCleanup" Directory="INSTALLFOLDER" Guid="{68F0A69A-B274-4596-A0EB-D8BC757B9446}">')
    [void]$builder.AppendLine('        <RemoveFolder Id="RemovePayloadInstallFolder" Directory="INSTALLFOLDER" On="uninstall" />')
    foreach ($directory in $directories) {
        $directoryId = $directoryIds[$directory]
        $removeId = 'RemovePayloadDir_' + (Get-StableHashHex "SwSearch.RemoveDirectory|$($directory.ToLowerInvariant())").Substring(0, 20)
        [void]$builder.AppendLine("        <RemoveFolder Id=`"$removeId`" Directory=`"$directoryId`" On=`"uninstall`" />")
    }
    [void]$builder.AppendLine('        <RegistryValue Root="HKCU" Key="Software\SwSearch\Installer\Payload" Name="DirectoryCleanup" Type="integer" Value="1" KeyPath="yes" />')
    [void]$builder.AppendLine('      </Component>')
    if ($directoryIds.ContainsKey('webui')) {
        $legacyWebDirectoryId = $directoryIds['webui']
        [void]$builder.AppendLine("      <Component Id=`"LegacySettingsAssetCleanup`" Directory=`"$legacyWebDirectoryId`" Guid=`"{84D6A8AC-7E5B-44EA-A0F0-98EF4D63C70D}`">")
        [void]$builder.AppendLine('        <RemoveFile Id="RemoveLegacySettingsHtml" Name="settings.html" On="install" />')
        [void]$builder.AppendLine('        <RegistryValue Root="HKCU" Key="Software\SwSearch\Installer\Payload" Name="LegacySettingsAssetCleanup" Type="integer" Value="1" KeyPath="yes" />')
        [void]$builder.AppendLine('      </Component>')
    }
    foreach ($entry in $relativeFiles) {
        $normalized = $entry.RelativePath.Replace('\', '/').ToLowerInvariant()
        $hash = Get-StableHashHex "SwSearch.PayloadFile|$normalized"
        $componentId = 'PayloadFile_' + $hash.Substring(0, 24)
        $guidHex = $hash.Substring(0, 32)
        $componentGuid = "{$($guidHex.Substring(0,8))-$($guidHex.Substring(8,4))-$($guidHex.Substring(12,4))-$($guidHex.Substring(16,4))-$($guidHex.Substring(20,12))}"
        $directoryId = if ($entry.RelativeDirectory) { $directoryIds[$entry.RelativeDirectory] } else { 'INSTALLFOLDER' }
        $sourcePath = ConvertTo-XmlAttribute $entry.File.FullName
        $fileName = ConvertTo-XmlAttribute $entry.File.Name
        $fileId = switch ($entry.RelativePath.Replace('\','/')) {
            'SwSearch.ModernContextMenuRegistration.exe' { 'SwSearchModernMenuRegistrationFile' }
            'SwSearch.ModernContextMenu.msix' { 'SwSearchModernMenuPackageFile' }
            'SwSearch.ModernContextMenu.cer' { 'SwSearchModernMenuCertificateFile' }
            default { "$($componentId)_File" }
        }
        $markerName = 'F_' + $hash.Substring(0, 40)

        [void]$builder.AppendLine("      <Component Id=`"$componentId`" Directory=`"$directoryId`" Guid=`"$componentGuid`">")
        [void]$builder.AppendLine("        <File Id=`"$fileId`" Source=`"$sourcePath`" Name=`"$fileName`" />")
        [void]$builder.AppendLine("        <RegistryValue Root=`"HKCU`" Key=`"Software\SwSearch\Installer\Payload`" Name=`"$markerName`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
        [void]$builder.AppendLine('      </Component>')
    }
    [void]$builder.AppendLine('    </ComponentGroup>')
    [void]$builder.AppendLine('  </Fragment>')
    [void]$builder.AppendLine('</Wix>')
    [System.IO.File]::WriteAllText($OutputPath, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
}

if (-not $buildRoot.StartsWith([System.IO.Path]::GetFullPath($PSScriptRoot) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "MSI build path escaped installer directory: $buildRoot"
}
if (-not $payloadRoot.StartsWith($buildRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Payload path escaped transaction build root: $payloadRoot"
}
foreach ($path in @(
    $finalMsi, $hashManifest, $installerActionsManifest, $bridgeGateArtifact, $commandLineGateArtifact, $commandLineGateLog) + $pendingOutputs) {
    if (-not $path.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI output escaped artifacts: $path"
    }
}
$parsedProductCode = [Guid]::Parse($ProductCode)
if ($parsedProductCode -eq [Guid]::Empty) { throw 'ProductCode must not be empty.' }
$ProductCode = $parsedProductCode.ToString('D').ToUpperInvariant()
foreach ($retiredProductCode in @(
    'AED45CC2-3804-4203-9A2A-88F60D71671F',
    'EB9F07BF-6F63-4E4C-97DC-9C5756B30F35',
    '25A0D431-5981-43D8-A996-8CAB7E77BA84',
    'B5764302-2832-4E7F-A85F-83A7A4BF6C90',
    '1DFE62DD-BF2D-4BE2-894D-A9E2BA44897B',
    'E1C70D94-47BD-4CE3-92BC-EF408A3F3258',
    'EC25790F-EF5C-4CA9-99FE-D6A814A102BA',
    'A28EF355-7779-4702-9858-0625AD922DCB',
    '4D93BE01-220C-48F6-B0B7-35B224199841',
    '8A49EE33-05F5-43C7-8A92-77F56D0019B0',
    '032E26AF-8146-4458-ACBE-4D3BDCB2D279',
    '7810FBC5-E94C-415D-9233-7CAB0D7D0039',
    'A0A485ED-004A-4CA2-B6AF-44C8C6BFC114',
    '717380CD-67C3-4E62-B0F3-0DA1ACFD9E2D',
    'AB60D15A-FF90-46BA-9F11-269AD9F60631',
    'FA322723-1158-42D0-880E-6B91CCCB4B82',
    '49D3B604-E741-4D2A-975B-D1E84C0A8528',
    '054C91ED-FAB3-43C6-864A-47D8ACEFEEF6',
    'FB93C5D8-BEAD-4C29-8A55-3751FAF1B1A5',
    'A88433F1-0E4F-43BD-9DF8-ADB10410E508',
    'A6CCBEB3-300D-4D55-AEAD-B7AE975D6473',
    'A5D7B1CD-53CB-4D8F-99BA-3135F06C08BD',
    'B0EADF5A-B418-4466-BA65-73D956F47FF6',
    'BC970457-7B46-4720-B6A7-38DE18099051',
    '33B51BB6-44B5-44EF-B2ED-78A1032C5A25',
    'E1C2A658-3AB8-4F89-A331-6C8EFDB4F112',
    '7B11289D-584C-4E8C-A72E-6C731038975D',
    '9B27108D-EE89-4D2A-BE15-08431C009BDD',
    'A4B5188B-BB8F-4050-851F-525D0B9819FD',
    'D1DA13CA-70A4-4E4E-A8B6-4DD078656D82',
    'D3229D39-D127-4EA2-BB33-3E1338D88D53',
    '823D4B5E-0AD2-4A25-8E17-E685ACD79870',
    '7231A572-AFBC-4461-85BC-6326EF37A924',
    'D448ED2A-BFC2-4923-8857-EEA9BA50F03C',
    '6670BD30-6799-4072-AB15-90FC475522F4',
    'ED006593-C117-470E-97A8-5DCC4F5461E6',
    '3F1C278E-118B-406C-A9CC-C3B096938C69',
    'C066B810-2F8F-4EE4-B762-8A70F6F71035',
    '668BE7C8-01EE-4A87-BD09-26308D222CD8',
    'B4878F23-2593-4BFB-A3BA-B585876C86FC',
    'E5A97259-595F-4C23-8F9D-B5F423E7B69A',
    '9C84757E-1683-47A7-8A75-F1247FC155F2',
    'E7C4F307-DF19-49E0-B5EB-D2B3262FFC8F',
    'EA7DA896-F1BB-4EDE-85FB-C21244AF88A2',
    'B8B4EE5A-B779-4F61-934D-FE582DCB67FE',
    '77969206-D300-4D06-A2A7-9D513836D2B6',
    '89543BBB-3325-402F-ACE1-4394D39A2F81',
    '050BF38C-7D08-427B-8EC9-C8DB9D19EA1E',
    'FA373B6F-05DB-41CB-A97F-E5BE35466058',
    'CB20D1F4-AA14-4518-BFAE-423C6315E9FD',
    '23336225-6550-4621-B804-0444797F620E',
    'A55D5C1E-1D56-4F7F-9E5A-4F2802DD7B44',
    '7328AB61-578D-47EC-83AE-01A35882D33E',
    '3B6A5C7D-4E92-4B01-A7F6-9D2C815E46A0',
    '6F2D9A8C-31B7-4E65-92C4-5A780D1F47B3',
    '3C785AD1-D656-4CB7-838E-7910549BD360',
    'A9A0861A-E300-4DFA-ACBF-0EC3E45ACBE6',
    '0EA6EB28-90CC-4C74-A870-6A885DE49BF7',
    '31FCD8DB-E75B-412C-AB55-9E44F14EB08B',
    '4964C9AB-9E2C-43B1-AF46-01A4B00F5C02',
    '97E3175A-9A37-453D-850E-727BA6CAD589',
    'C1DDA132-10B9-42F4-937C-F1562FC1D6B5',
    '7173C473-1A43-4F66-83ED-F3266C4C8801',
    '585992A5-C2F5-4C82-9E56-4EE08E5D6B4F',
    '157336E2-7A32-49E1-9235-7CEDCCC4B8B1',
    '4B4B7A0D-8F4F-4A93-AA67-7D8A62CCAA18',
    '390EDFB6-2A2B-4051-8FCD-82AC8E22DAD4',
    '856C6889-0A88-4D09-9C5E-2F2D16245012',
    'DFD6EABB-416B-4826-900F-FCECCE4D76D6',
    '0C633D01-EE40-4286-93FA-DE3BEAD5B282',
    '3E5314FD-B184-41AD-959C-0D9DD2CF8F8C',
    '4BD4884D-19F3-4BB5-91C4-F18F478D725F',
    'F7A360E9-DD8A-4837-A4B6-BB67B7124BE2',
    '2E3A118F-D721-4813-A745-C202464572DB',
    '91F729CE-FE93-4001-94B5-6F9EDDD354D3',
    'AFF00643-9EF6-4A25-A9A0-76A2B9BE3102',
    '171AAB19-5610-46C2-A3B1-93B0F417E79B',
    '83C43B92-7F85-4A2E-8B8E-89E17AAE3685',
    '3D1DEBF5-42E8-4F44-941F-BC71BBFCBBB5',
    '0C572897-1D17-47DE-BA1A-0F51560E8303',
    'B79A9CAD-AD7E-426C-BC2F-71E8127B9B53',
    '2F4A6CB6-81EA-41EE-A0A1-1BC4CC796C44',
    'B89307AB-280E-4E5C-BDAF-8DB7E545AE0F'
    '3889123A-2D6E-49E2-A8B0-C727DBA20385'
    '0489512D-9600-45D7-A9F0-66B6B8ECBB69'
    'C3BAE023-EF8C-41DE-B48A-C253C40DE2B3'
    'DCBC414C-3A7E-4FA6-B423-2ADCA3D76238'
    'AABCF20C-E553-4B89-A627-72C4B52BE232'
    '3C7FDB70-D094-4441-827A-4BA4AB5CF77F'
    '4F59748B-AB85-498A-A708-B522EE36EA20'
    'AEC199CA-79BB-4560-96AE-B197E3FDD978'
    '462A4F7B-B09C-4822-AE8A-2936DBDEDE7E'
    '02F35BA5-F73C-4D82-AB56-29A4C4FB1349',
    '2ADEDB57-8327-4F5A-8E9F-C7A9623F526B',
    '94DD3DCA-3188-4F92-9472-0A39F23EA628',
    '6E45A916-D3A3-495B-BCE4-2B2BD000F237',
    'C628FD2E-2F28-4F6A-83C9-798186AEAEC5',
    'DDA7EA84-BDED-4225-8480-9E2A0941D8E6'
    )) {
    if ($parsedProductCode -eq [Guid]::Parse($retiredProductCode)) {
        throw "Version $Version must not reuse a retired ProductCode."
    }
}

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $nugetPackages | Out-Null
$env:NUGET_PACKAGES = $nugetPackages

Push-Location $repoRoot
try {
    Assert-Rc12Immutable
    Assert-Rc13Immutable
    Assert-Rc14Immutable
    Assert-Rc15Immutable
    if ($hasFrozenRestoreGraphReceipt) {
        $validatedRestoreReceipt = Test-SwSearchFrozenRestoreGraphReceipt -Root $repoRoot -TransactionRoot $buildRoot `
            -SourceTransactionNonce $TransactionNonce -ReceiptPath $FrozenRestoreGraphReceipt `
            -ReceiptSha256 $FrozenRestoreGraphReceiptSha256
        Write-Output "FROZEN_RESTORE_GRAPH_RECEIPT: manifest=$($validatedRestoreReceipt.ManifestSha256); utc=$($validatedRestoreReceipt.ValidatedUtc.ToString('o'))"
    }
    else {
        & powershell @restoreGateArguments
        if ($LASTEXITCODE -ne 0) { throw 'Frozen zero-restore graph preflight failed; MSI build stopped.' }
    }

    if (Test-Path -LiteralPath $buildRoot) {
        if (-not $hasFrozenRestoreGraphReceipt) { throw "Transaction build root already exists: $buildRoot" }
        $seedEntries = @(Get-ChildItem -LiteralPath $buildRoot -Force)
        if ($seedEntries.Count -ne 1 -or $seedEntries[0].PSIsContainer -or
            -not $seedEntries[0].FullName.Equals([IO.Path]::GetFullPath($FrozenRestoreGraphReceipt), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Transaction build root contains data beyond the validated frozen restore graph receipt: $buildRoot"
        }
    }
    New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
    # A release-orchestrated receipt has already bound the frozen restore graph
    # to this transaction.  Publish.ps1 and Build-InstallerActions.ps1 retain
    # their standalone preflight gates, so scope the receipt-authorized skip to
    # those two child invocations only.  Do not let it escape into WiX/Verify.
    $previousFrozenRestoreGraphSkip = $env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH
    $receiptGatedChildRestoreSkip = $false
    try {
        if ($hasFrozenRestoreGraphReceipt) {
            $env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH = '1'
            $receiptGatedChildRestoreSkip = $true
            Write-Output 'FROZEN_RESTORE_GRAPH_CHILD_VALIDATIONS: receipt-gated skip for Publish and installer actions.'
        }

        & powershell -NoProfile -ExecutionPolicy Bypass -File '.\installer\Publish.ps1' -Version $Version `
            -OutputDirectory $payloadRoot -StageDirectory $publishStageRoot -SkipPrePublishValidation
        if ($LASTEXITCODE -ne 0) { throw 'Application publish failed; MSI build stopped.' }
        $null = Test-SwSearchPayloadManifest -PayloadRoot $payloadRoot -ManifestPath $payloadManifestPath

        foreach ($path in @($finalMsi, $hashManifest, $installerActionsManifest, $bridgeGateArtifact, $commandLineGateArtifact, $commandLineGateLog)) {
            if (Test-Path -LiteralPath $path) { throw "Candidate output already exists and will not be overwritten: $path" }
        }
        foreach ($path in $pendingOutputs) {
            if (Test-Path -LiteralPath $path) { throw "Transaction nonce collision left a pending output: $path" }
        }
        New-Item -ItemType Directory -Force -Path $packageOutput | Out-Null
        $snapshotFacts = New-SwSearchPayloadSnapshot -PayloadRoot $payloadRoot `
            -ManifestPath $payloadManifestPath -SnapshotRoot $payloadSnapshotRoot
        $actionArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File','.\installer\Build-InstallerActions.ps1',
            '-OutputDirectory',$installerActionsOutput,'-Configuration','Release','-RuntimeIdentifier','win-x64')
        & powershell @actionArguments
        if ($LASTEXITCODE -ne 0) { throw 'Installer action binaries failed.' }
    }
    finally {
        if ($receiptGatedChildRestoreSkip) {
            if ($null -eq $previousFrozenRestoreGraphSkip) {
                Remove-Item -Path Env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH -ErrorAction SilentlyContinue
            }
            else {
                $env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH = $previousFrozenRestoreGraphSkip
            }
        }
    }
    $coordinatorBinary = Join-Path $installerActionsOutput 'SwSearch.InstallerCoordinator.exe'
    $msiBridgeBinary = Join-Path $installerActionsOutput 'SwSearch.InstallerMsiBridge.dll'
    $msiBridgeGate = Join-Path $installerActionsOutput 'SwSearch.InstallerMsiBridgeGate.exe'
    $msiCommandLineGate = Join-Path $installerActionsOutput 'SwSearch.InstallerMsiCommandLineGate.exe'
    $privateManifest = Join-Path $installerActionsOutput 'installer-actions.sha256.json'
    foreach ($requiredPrivateAction in @($coordinatorBinary, $msiBridgeBinary, $msiBridgeGate, $msiCommandLineGate, $privateManifest)) {
        if (-not (Test-Path -LiteralPath $requiredPrivateAction -PathType Leaf)) {
            throw "Installer action output missing: $requiredPrivateAction"
        }
    }
    Copy-Item -LiteralPath $privateManifest -Destination $pendingInstallerActionsManifest
    Copy-Item -LiteralPath $msiBridgeGate -Destination $pendingBridgeGateArtifact
    Copy-Item -LiteralPath $msiCommandLineGate -Destination $pendingCommandLineGateArtifact
    $null = Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $payloadSnapshotRoot `
        -ManifestSha256 $snapshotFacts.ManifestSha256 -EntriesDigest $snapshotFacts.EntriesDigest `
        -FileCount $snapshotFacts.FileCount
    $modernMenuCertificatePath = Join-Path $payloadSnapshotRoot 'SwSearch.ModernContextMenu.cer'
    $modernMenuCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($modernMenuCertificatePath)
    try {
        if ($modernMenuCertificate.HasPrivateKey -or
            $modernMenuCertificate.Subject -cne 'CN=SwSearch Local Package Publisher' -or
            $modernMenuCertificate.Thumbprint -notmatch '^[A-F0-9]{40}$') {
            throw 'Modern context-menu public certificate identity/private-key boundary is invalid.'
        }
        $modernMenuCertificateThumbprint = $modernMenuCertificate.Thumbprint
    }
    finally { $modernMenuCertificate.Dispose() }
    New-PayloadWixSource -PayloadDirectory $payloadSnapshotRoot -OutputPath $generatedPayloadWxs

    if (-not $hasFrozenRestoreGraphReceipt) {
        & powershell @restoreGateArguments
        if ($LASTEXITCODE -ne 0) { throw 'Frozen zero-restore graph changed before WiX build.' }
    }
    & dotnet build '.\installer\wix\SwSearch.Package.wixproj' -c Release --no-restore -p:NuGetAudit=false `
        "-p:PayloadDir=$payloadSnapshotRoot" "-p:ProductVersion=$Version" "-p:ProductCode=$ProductCode" `
        "-p:GeneratedPayloadWxs=$generatedPayloadWxs" `
        "-p:CoordinatorBinaryPath=$coordinatorBinary" "-p:MsiBridgeBinaryPath=$msiBridgeBinary" `
        "-p:ModernMenuCertificateThumbprint=$modernMenuCertificateThumbprint" `
        "-p:ArtifactName=$artifactName" "-p:OutputPath=$packageOutput\"
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
    if (-not $hasFrozenRestoreGraphReceipt) {
        & powershell @restoreGateArguments
        if ($LASTEXITCODE -ne 0) { throw 'Frozen zero-restore graph changed during MSI build.' }
    }
    $null = Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $payloadSnapshotRoot `
        -ManifestSha256 $snapshotFacts.ManifestSha256 -EntriesDigest $snapshotFacts.EntriesDigest `
        -FileCount $snapshotFacts.FileCount

    $builtMsiCandidates = @(Get-ChildItem -LiteralPath $packageOutput -Filter '*.msi' -File -Recurse)
    if ($builtMsiCandidates.Count -ne 1) {
        throw "Expected exactly one MSI output, found $($builtMsiCandidates.Count)."
    }
    $expectedBuiltMsi = [IO.Path]::GetFullPath((Join-Path $packageOutput "$artifactName.msi"))
    $builtMsi = $builtMsiCandidates[0]
    if (-not $builtMsi.FullName.Equals($expectedBuiltMsi, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI output was not the exact transaction output path: $($builtMsi.FullName)"
    }
    Invoke-InstallerCommandLineGate -GatePath $msiCommandLineGate -MsiPath $builtMsi.FullName `
        -LogPath $pendingCommandLineGateLog -TimeoutMilliseconds 30000
    Copy-Item -LiteralPath $builtMsi.FullName -Destination $pendingMsi

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pendingMsi).Hash
    [System.IO.File]::WriteAllText(
        $pendingHashManifest,
        "$hash *$artifactName.msi`r`n",
        [System.Text.UTF8Encoding]::new($false))

    $null = Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $payloadSnapshotRoot `
        -ManifestSha256 $snapshotFacts.ManifestSha256 -EntriesDigest $snapshotFacts.EntriesDigest `
        -FileCount $snapshotFacts.FileCount
    $payloadHandoff = [ordered]@{
        schema = 1
        transactionNonce = $TransactionNonce
        snapshotRoot = [IO.Path]::GetFullPath($payloadSnapshotRoot)
        manifestSha256 = $snapshotFacts.ManifestSha256
        entriesDigest = $snapshotFacts.EntriesDigest
        fileCount = $snapshotFacts.FileCount
    }
    [IO.File]::WriteAllText(
        $pendingPayloadHandoff,
        ($payloadHandoff | ConvertTo-Json -Depth 3),
        [Text.UTF8Encoding]::new($false))

    Assert-Rc12Immutable
    Assert-Rc13Immutable
    Assert-Rc14Immutable
    $handoffReady = $true

    Write-Output "Pending MSI: $pendingMsi"
    Write-Output "ProductCode: {$ProductCode}"
    Write-Output 'UpgradeCode: {D9158356-29B3-44D6-A15A-ACDCE8328703}'
    Write-Output "SHA256: $hash"
    Write-Output "Pending manifest: $pendingHashManifest"
    Write-Output "Pending installer action manifest: $pendingInstallerActionsManifest"
    Write-Output "Pending isolated bridge gate: $pendingBridgeGateArtifact"
    Write-Output "Pending installer command-line gate: $pendingCommandLineGateArtifact"
    Write-Output "Pending installer command-line gate log: $pendingCommandLineGateLog"
    Write-Output "Pending frozen payload handoff: $pendingPayloadHandoff"
}
finally {
    Pop-Location
    $cleanupFailures = [Collections.Generic.List[object]]::new()
    try {
        if (-not $handoffReady -and (Test-Path -LiteralPath $buildRoot)) {
            Remove-Item -LiteralPath $buildRoot -Recurse -Force
        }
    }
    catch { $cleanupFailures.Add($_) }
    if (-not $handoffReady) {
        foreach ($path in $pendingOutputs) {
            try {
                if (Test-Path -LiteralPath $path) {
                    Remove-Item -LiteralPath $path -Force
                }
            }
            catch { $cleanupFailures.Add($_) }
        }
    }
    if ($cleanupFailures.Count -ne 0) { throw $cleanupFailures[0] }
}
