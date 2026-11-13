[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Import-SelectedFunctions {
    param([string]$Path, [string[]]$Names)
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Cannot parse fixture source $Path." }
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Names -contains $node.Name }, $true))
    foreach ($name in $Names) {
        if (@($definitions | Where-Object Name -eq $name).Count -ne 1) { throw "Fixture function $name was not found exactly once in $Path." }
    }
    return (($definitions | Sort-Object { [Array]::IndexOf($Names, $_.Name) } | ForEach-Object { $_.Extent.Text }) -join "`r`n`r`n")
}

function Assert-Rejected {
    param([string]$Name, [scriptblock]$Action)
    $rejected = $false
    try { & $Action | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Fixture did not reject: $Name" }
    Write-Output "PASS $Name"
}

$directory = Split-Path -Parent $PSCommandPath
$wrapperPath = Join-Path $directory 'Start-IsolatedMsiLifecycleValidation.ps1'
$childPath = Join-Path $directory 'Invoke-MsiLifecycleValidation.ps1'
$wrapperFunctions = @('Assert-ExactObjectProperties','Assert-StrictChildReport','Assert-SafeTemporaryProfilePath','Get-CanonicalHash','Read-ValidatedRecoveryJournal')
$childFunctions = @('Assert-FileSha256Binding')
. ([scriptblock]::Create((Import-SelectedFunctions -Path $wrapperPath -Names $wrapperFunctions)))
. ([scriptblock]::Create((Import-SelectedFunctions -Path $childPath -Names $childFunctions)))

$script:ProtectedUserSid = 'S-1-5-21-6737918-3018883656-3777255787-1001'
$script:TrustedBootstrapRoot = 'C:\ProgramData\SwSearch\ValidationBootstrap'

$fixtureSid = 'S-1-5-21-1-2-3-1009'
$fixtureProfile = 'C:\Users\SwMsiTest_A1B2C3D4'
$requiredChecks = @('Runtime SID binding','Scenario contract Clean')
$manifest = [ordered]@{
    RunId = '20260714-120000-a1b2c3d4'
    Nonce = ('A' * 64)
    ScenarioId = 'A-Clean'
    Scenario = 'Clean'
    ExpectedUserSid = $fixtureSid
    ExpectedSamAccountName = 'SwMsiTest_A1B2C3D4'
    ExpectedProfilePath = $fixtureProfile
    ProtectedUserSid = $script:ProtectedUserSid
    Staged = [ordered]@{
        CurrentMsi = [ordered]@{ Path = 'C:\stage\current.msi'; Sha256 = ('1' * 64); Version = '1.1.1'; ProductCode = '{25A0D431-5981-43D8-A996-8CAB7E77BA84}'; UpgradeCode = '{D9158356-29B3-44D6-A15A-ACDCE8328703}' }
        PreviousMsi = $null
    }
    RequiredChecks = $requiredChecks
}
$manifestRecord = [pscustomobject]@{ Path = 'C:\stage\A.manifest.json'; Nonce = $manifest.Nonce; Sha256 = ('2' * 64); Manifest = [pscustomobject]$manifest }
$account = [pscustomobject]@{ Name = $manifest.ExpectedSamAccountName; Sid = $fixtureSid; ProfilePath = $fixtureProfile }
$reportTemplate = [ordered]@{
    SchemaVersion = 2
    GeneratedAt = '2026-07-14T12:00:00.0000000+08:00'
    RunId = $manifest.RunId
    Nonce = $manifest.Nonce
    ScenarioId = 'A-Clean'
    Scenario = 'Clean'
    Account = $account.Name
    UserSid = $account.Sid
    ProfilePath = $account.ProfilePath
    ProtectedUserSid = $script:ProtectedUserSid
    ManifestPath = $manifestRecord.Path
    ManifestSha256 = $manifestRecord.Sha256
    IsAdministrator = $false
    MsiIdentity = $manifest.Staged.CurrentMsi
    PreviousMsiIdentity = $null
    InstallDirectory = 'C:\Users\SwMsiTest_A1B2C3D4\AppData\Local\Programs\SwSearch'
    Status = 'PASS'
    Passed = $true
    Skipped = $false
    SkipReason = $null
    PrimaryError = $null
    CleanupError = $null
    TransactionStillRunning = $false
    UnmanagedChildStillRunning = $false
    ManualCleanupRequired = $false
    Helper = [ordered]@{ Mode = $null; ProcessId = $null; CreationTime = $null; ExecutablePath = $null; CommandLine = $null; OwnerSid = $null; SessionId = $null }
    OutstandingProcesses = @()
    ObservedForbiddenProcesses = @()
    Checks = @(
        [ordered]@{ Name = 'Runtime SID binding'; Passed = $true; Detail = 'fixture'; Time = '2026-07-14T12:00:00+08:00' },
        [ordered]@{ Name = 'Scenario contract Clean'; Passed = $true; Detail = 'fixture'; Time = '2026-07-14T12:00:01+08:00' }
    )
}
function New-FixtureReport {
    return (($reportTemplate | ConvertTo-Json -Depth 20) | ConvertFrom-Json)
}

$valid = New-FixtureReport
$null = Assert-StrictChildReport -Report $valid -ManifestRecord $manifestRecord -Account $account -ExpectedScenarioId 'A-Clean' -ExpectedScenario 'Clean'
Write-Output 'PASS strict valid child report baseline'

Assert-Rejected 'forged protected SID in child JSON' {
    $forged = New-FixtureReport
    $forged.UserSid = $script:ProtectedUserSid
    Assert-StrictChildReport -Report $forged -ManifestRecord $manifestRecord -Account $account -ExpectedScenarioId 'A-Clean' -ExpectedScenario 'Clean'
}
Assert-Rejected 'extra child JSON field' {
    $forged = New-FixtureReport
    $forged | Add-Member -NotePropertyName ExtraIdentity -NotePropertyValue 'forged'
    Assert-StrictChildReport -Report $forged -ManifestRecord $manifestRecord -Account $account -ExpectedScenarioId 'A-Clean' -ExpectedScenario 'Clean'
}
Assert-Rejected 'string replacing JSON boolean' {
    $forged = New-FixtureReport
    $forged.Passed = 'true'
    Assert-StrictChildReport -Report $forged -ManifestRecord $manifestRecord -Account $account -ExpectedScenarioId 'A-Clean' -ExpectedScenario 'Clean'
}
Assert-Rejected 'missing mandatory check' {
    $forged = New-FixtureReport
    $forged.Checks = @($forged.Checks | Where-Object Name -ne 'Scenario contract Clean')
    Assert-StrictChildReport -Report $forged -ManifestRecord $manifestRecord -Account $account -ExpectedScenarioId 'A-Clean' -ExpectedScenario 'Clean'
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('SwMsiSecurityFixture-' + [Guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not ([IO.Path]::GetFullPath($fixtureRoot).StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase))) { throw 'Fixture root escaped the system temp directory.' }
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
try {
    foreach ($name in @('Invoke-stage.ps1','Helper-stage.exe')) {
        $path = Join-Path $fixtureRoot $name
        [IO.File]::WriteAllText($path, 'trusted', (New-Object Text.UTF8Encoding($false)))
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        [IO.File]::AppendAllText($path, 'tampered', (New-Object Text.UTF8Encoding($false)))
        Assert-Rejected "tampered staged $name" { Assert-FileSha256Binding -Path $path -ExpectedSha256 $hash -Label $name }
    }

    $profilesRoot = Join-Path $fixtureRoot 'Users'
    $outside = Join-Path $fixtureRoot 'Outside'
    New-Item -ItemType Directory -Path $profilesRoot,$outside | Out-Null
    $profileName = 'SwMsiTest_A1B2C3D4'
    $junction = Join-Path $profilesRoot $profileName
    New-Item -ItemType Junction -Path $junction -Target $outside | Out-Null
    Assert-Rejected 'profile root junction' { Assert-SafeTemporaryProfilePath -ProfilePath $junction -AccountName $profileName -ProfilesRoot $profilesRoot -RejectContainedReparsePoint }
    Remove-Item -LiteralPath $junction -Force
    New-Item -ItemType Directory -Path $junction | Out-Null
    $childJunction = Join-Path $junction 'linked-child'
    New-Item -ItemType Junction -Path $childJunction -Target $outside | Out-Null
    Assert-Rejected 'profile contained junction' { Assert-SafeTemporaryProfilePath -ProfilePath $junction -AccountName $profileName -ProfilesRoot $profilesRoot -RejectContainedReparsePoint }
    Remove-Item -LiteralPath $childJunction -Force

    $fakeJournal = Join-Path $fixtureRoot 'outside-protected-root.journal.json'
    [IO.File]::WriteAllText($fakeJournal, '{}', (New-Object Text.UTF8Encoding($true)))
    Assert-Rejected 'journal recovery outside protected root' { Read-ValidatedRecoveryJournal -Path $fakeJournal }

    $stateA = [ordered]@{ Payload = @('a'); Registry = [ordered]@{ ContextVerb = 'viewer-a'; ProductState = 5 } }
    $stateB = [ordered]@{ Payload = @('a'); Registry = [ordered]@{ ContextVerb = 'viewer-b'; ProductState = 5 } }
    if ((Get-CanonicalHash $stateA) -eq (Get-CanonicalHash $stateB)) { throw 'C2 registry-difference fixture did not affect the canonical state hash.' }
    Write-Output 'PASS C2 registry difference changes complete snapshot hash'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $full = [IO.Path]::GetFullPath($fixtureRoot)
        if (-not $full.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup path escaped temp.' }
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Write-Output 'SECURITY FIXTURES PASS'
