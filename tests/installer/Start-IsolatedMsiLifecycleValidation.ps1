[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$Execute,
    [switch]$ConfirmIsolatedSystemChanges,
    [string]$RecoverFromJournal,
    [switch]$LibraryOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# This orchestrator intentionally has no MSI/path override parameters. The
# package identities and workspace are frozen so a mistyped command cannot
# validate or uninstall a different product.
$script:ExpectedWorkspace = 'C:\Users\liliy\Documents\solidworks插件'
$script:ProtectedUserSid = 'S-1-5-21-6737918-3018883656-3777255787-1001'
$script:CurrentMsiRelativePath = 'artifacts\SwSearch-x64-v1.1.1.msi'
$script:CurrentMsiSha256 = '418CB6AD9CDC4B78ECDF8F83B7AFEF2E323E7E2E233F3BCD18C7FC4983465DFA'
$script:CurrentVersion = '1.1.1'
$script:CurrentProductCode = '{25A0D431-5981-43D8-A996-8CAB7E77BA84}'
$script:UpgradeCode = '{D9158356-29B3-44D6-A15A-ACDCE8328703}'
$script:PreviousMsiRelativePath = 'artifacts\quarantine\superseded-pre-1.1.0\SwSearch-x64-v1.0.1.msi'
$script:PreviousMsiSha256 = '55B6F988249B65822FFF7E683334BD5533722AA7FE5B1A843B9F616C81AAFE77'
$script:PreviousVersion = '1.0.1'
$script:PreviousProductCode = '{AED45CC2-3804-4203-9A2A-88F60D71671F}'
$script:ExpectedPayloadFileCount = 483
$script:ExpectedManifestEntryCount = 482
$script:ExpectedManifestSha256 = '0D4F0DC3BCCA92BE8D708F9157A03D6CAB5D13B1546326D77B955E4E8C3EF7D8'
$script:LifecycleSourceSha256 = '9C2A40382BC677DE48759577AFA131D2F6326DE46FBED97B3CF0B232B09E456C'
$script:HelperSourceSha256 = '3638AEE0B3551155166E8DDEC27EE76A557AFF75C19E82FE3220820FFEE91719'
$script:TrustedBootstrapRoot = 'C:\ProgramData\SwSearch\ValidationBootstrap'
$script:Installer = New-Object -ComObject WindowsInstaller.Installer
$script:PreflightChecks = New-Object System.Collections.Generic.List[object]
$script:ScenarioResults = New-Object System.Collections.Generic.List[object]
$script:CreatedAccounts = New-Object System.Collections.Generic.List[object]
$script:OutstandingControllers = New-Object System.Collections.Generic.List[object]
$script:RunRoot = $null
$script:StagingRoot = $null
$script:ManualCleanupRequired = $false
$script:FatalError = $null
$script:JournalPath = $null
$script:Journal = $null

function Normalize-GuidText {
    param([string]$Value)
    return ('{' + ([Guid]::Parse($Value).ToString().ToUpperInvariant()) + '}')
}

function Write-AtomicUtf8Json {
    param([string]$Path, $Value)
    $temporary = "$Path.tmp-$([Guid]::NewGuid().ToString('N'))"
    $encoding = New-Object Text.UTF8Encoding($true)
    $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $writer = New-Object IO.StreamWriter($stream, $encoding)
        try { $writer.Write(($Value | ConvertTo-Json -Depth 30)); $writer.Flush(); $stream.Flush($true) }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function New-CryptographicHex {
    param([int]$ByteCount)
    $buffer = New-Object byte[] $ByteCount
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer); return ([BitConverter]::ToString($buffer)).Replace('-', '') }
    finally { $rng.Dispose(); [Array]::Clear($buffer, 0, $buffer.Length) }
}

function Initialize-PersistentJournal {
    param([string]$Path, [string]$RunId, [string]$ProtectedSid, [string]$RunRoot, [string]$StagingRoot)
    $script:JournalPath = $Path
    $script:Journal = [ordered]@{
        SchemaVersion = 2
        RunId = $RunId
        ProtectedUserSid = $ProtectedSid
        RunRoot = [IO.Path]::GetFullPath($RunRoot)
        StagingRoot = [IO.Path]::GetFullPath($StagingRoot)
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        UpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Status = 'ACTIVE'
        CurrentStage = 'JournalInitialized'
        Accounts = @()
        Products = [ordered]@{
            Current = [ordered]@{ ProductCode = $script:CurrentProductCode; BaselineState = Get-ProductState $script:CurrentProductCode; InstalledByRun = $false; Removed = $false }
            Previous = [ordered]@{ ProductCode = $script:PreviousProductCode; BaselineState = Get-ProductState $script:PreviousProductCode; InstalledByRun = $false; Removed = $false }
        }
        HelperProcesses = @()
        ControllerProcesses = @()
        Results = @()
        Stages = @([ordered]@{ Name = 'JournalInitialized'; Phase = 'AFTER'; AtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Detail = 'Protected journal created before account/profile/MSI changes.' })
    }
    Write-AtomicUtf8Json -Path $script:JournalPath -Value $script:Journal
}

function Write-JournalStage {
    param([string]$Name, [ValidateSet('BEFORE','AFTER','FAILED')][string]$Phase, $Detail)
    if ($null -eq $script:Journal -or [string]::IsNullOrWhiteSpace($script:JournalPath)) { throw 'Persistent journal is not initialized.' }
    $entry = [ordered]@{ Name = $Name; Phase = $Phase; AtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Detail = $Detail }
    $script:Journal.Stages = @($script:Journal.Stages) + @($entry)
    $script:Journal.CurrentStage = "$Name/$Phase"
    $script:Journal.UpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Write-AtomicUtf8Json -Path $script:JournalPath -Value $script:Journal
}

function Invoke-JournaledChange {
    param([string]$Name, $BeforeDetail, [scriptblock]$Action, [scriptblock]$AfterDetail)
    Write-JournalStage -Name $Name -Phase BEFORE -Detail $BeforeDetail
    try {
        $result = & $Action
        $detail = if ($AfterDetail) { & $AfterDetail $result } else { $null }
        Write-JournalStage -Name $Name -Phase AFTER -Detail $detail
        return $result
    }
    catch {
        Write-JournalStage -Name $Name -Phase FAILED -Detail $_.Exception.Message
        throw
    }
}

function Complete-PersistentJournal {
    param([string]$Status)
    if ($null -eq $script:Journal) { return }
    $script:Journal.Status = $Status
    $script:Journal.CurrentStage = 'Completed'
    $script:Journal.UpdatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Write-AtomicUtf8Json -Path $script:JournalPath -Value $script:Journal
}

function Add-PreflightCheck {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    $script:PreflightChecks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Detail = $Detail
    })
    $prefix = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Output "[$prefix] $Name - $Detail"
}

function Assert-Preflight {
    param([bool]$Condition, [string]$Name, [string]$Detail)
    Add-PreflightCheck -Name $Name -Passed $Condition -Detail $Detail
    if (-not $Condition) { throw "$Name failed: $Detail" }
}

function Get-MsiProperty {
    param([string]$Path, [string]$Property)
    if ($Property -notmatch '^[A-Za-z0-9_]+$') { throw "Unsafe MSI property name: $Property" }
    $database = $script:Installer.OpenDatabase($Path, 0)
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Property'")
    try {
        $view.Execute() | Out-Null
        $record = $view.Fetch()
        if ($null -eq $record) { return $null }
        return [string]$record.StringData(1)
    }
    finally { $view.Close() | Out-Null }
}

function Get-ProductState {
    param([string]$ProductCode)
    try { return [int]$script:Installer.ProductState((Normalize-GuidText $ProductCode)) }
    catch { return -1 }
}

function Get-RelatedProducts {
    param([string]$Code)
    try {
        return @($script:Installer.RelatedProducts((Normalize-GuidText $Code))) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { Normalize-GuidText ([string]$_) } |
            Sort-Object -Unique
    }
    catch { return @() }
}

function Assert-FixedMsiIdentity {
    param(
        [string]$Path,
        [string]$ExpectedSha256,
        [string]$ExpectedVersion,
        [string]$ExpectedProductCode,
        [string]$ExpectedUpgradeCode,
        [string]$Label
    )
    Assert-Preflight (Test-Path -LiteralPath $Path -PathType Leaf) "$Label exists" $Path
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Assert-Preflight ($hash -eq $ExpectedSha256) "$Label SHA256" $hash
    $version = Get-MsiProperty -Path $Path -Property 'ProductVersion'
    $productCode = Normalize-GuidText (Get-MsiProperty -Path $Path -Property 'ProductCode')
    $upgradeCode = Normalize-GuidText (Get-MsiProperty -Path $Path -Property 'UpgradeCode')
    Assert-Preflight ($version -eq $ExpectedVersion) "$Label ProductVersion" $version
    Assert-Preflight ($productCode -eq (Normalize-GuidText $ExpectedProductCode)) "$Label ProductCode" $productCode
    Assert-Preflight ($upgradeCode -eq (Normalize-GuidText $ExpectedUpgradeCode)) "$Label UpgradeCode" $upgradeCode
}

function Get-ProcessOwnerSid {
    param([Microsoft.Management.Infrastructure.CimInstance]$Process)
    try {
        $owner = Invoke-CimMethod -InputObject $Process -MethodName GetOwnerSid -ErrorAction Stop
        if ($owner.ReturnValue -eq 0) { return [string]$owner.Sid }
    }
    catch { }
    return $null
}

function Get-CurrentIdentityContext {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) { throw 'Current token has no user SID.' }
    $sid = [string]$identity.User.Value
    $account = [string]$identity.User.Translate([Security.Principal.NTAccount]).Value
    $parts = $account.Split('\', 2)
    if ($parts.Count -ne 2) { throw "Cannot resolve SAM account from $account." }
    $local = Get-LocalUser -SID $identity.User -ErrorAction Stop
    if ($null -eq $local -or -not [string]::Equals([string]$local.Sid.Value, $sid, [StringComparison]::OrdinalIgnoreCase) -or -not [string]::Equals([string]$local.Name, $parts[1], [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WindowsIdentity translation and Get-LocalUser SID lookup disagree.'
    }
    $profile = @(Get-CimInstance Win32_UserProfile -Filter "SID='$sid'" -ErrorAction Stop)
    if ($profile.Count -ne 1) { throw "Expected one profile for current SID $sid; found $($profile.Count)." }
    return [pscustomobject]@{
        Identity = $identity
        Sid = $sid
        NtAccount = $account
        SamAccountName = [string]$local.Name
        ProfilePath = [IO.Path]::GetFullPath([string]$profile[0].LocalPath).TrimEnd('\')
    }
}

function Test-AdminProtectedBootstrapPath {
    param([string]$ScriptPath)
    $full = [IO.Path]::GetFullPath($ScriptPath)
    $root = [IO.Path]::GetFullPath($script:TrustedBootstrapRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $base = Join-Path $env:ProgramData 'SwSearch'
    foreach ($path in @($base, $script:TrustedBootstrapRoot, (Split-Path -Parent $full), $full) | Sort-Object -Unique) {
        if (-not (Test-Path -LiteralPath $path)) { return $false }
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        $acl = Get-Acl -LiteralPath $path -ErrorAction Stop
        $ownerSid = try { (New-Object Security.Principal.NTAccount([string]$acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { [string]$acl.Owner }
        if ($ownerSid -notin @('S-1-5-18','S-1-5-32-544',$script:ProtectedUserSid)) { return $false }
        foreach ($rule in $acl.Access) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            $sid = try { $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { [string]$rule.IdentityReference }
            $writeMask = [Security.AccessControl.FileSystemRights]('Write,Modify,FullControl,Delete,ChangePermissions,TakeOwnership,CreateFiles,CreateDirectories,WriteData,AppendData')
            if ($sid -notin @('S-1-5-18','S-1-5-32-544',$script:ProtectedUserSid) -and (($rule.FileSystemRights -band $writeMask) -ne 0)) { return $false }
        }
    }
    return $true
}

function Get-InstallerProcesses {
    $items = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" -ErrorAction SilentlyContinue)) {
        $parent = if ($process.ParentProcessId) {
            Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ParentProcessId)" -ErrorAction SilentlyContinue
        }
        else { $null }
        $items += [pscustomobject]@{
            ProcessId = [int]$process.ProcessId
            ParentProcessId = [int]$process.ParentProcessId
            ParentName = if ($parent) { [string]$parent.Name } else { $null }
            SessionId = [int]$process.SessionId
            CommandLine = [string]$process.CommandLine
            CreationDate = if ($process.CreationDate) { ([DateTimeOffset]$process.CreationDate).ToString('o') } else { $null }
            OwnerSid = Get-ProcessOwnerSid -Process $process
        }
    }
    return @($items)
}

function Get-InstallerBusyState {
    $inProgressPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress'
    $all = @(Get-InstallerProcesses)
    $clients = @($all | Where-Object {
        ($_.SessionId -ne 0 -or -not [string]::Equals($_.ParentName, 'services.exe', [StringComparison]::OrdinalIgnoreCase)) -or
        (-not [string]::IsNullOrWhiteSpace($_.CommandLine) -and $_.CommandLine -match '(?i)(?:^|\s)/(?:i|x|f|p|update|package)(?:\s|$)')
    })
    return [pscustomobject]@{
        IsBusy = ((Test-Path -LiteralPath $inProgressPath) -or $clients.Count -gt 0)
        InProgress = Test-Path -LiteralPath $inProgressPath
        Clients = $clients
        AllMsiexec = $all
    }
}

function Assert-InstallerIdle {
    $state = Get-InstallerBusyState
    $detail = if ($state.IsBusy) { $state | ConvertTo-Json -Depth 6 -Compress } else { 'No InProgress key and no non-service MSI client.' }
    Assert-Preflight (-not $state.IsBusy) 'Windows Installer is idle' $detail
}

function Get-CanonicalHash {
    param([object]$Value)
    $json = $Value | ConvertTo-Json -Depth 20 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]$json)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Convert-RegistryValueForSnapshot {
    param([object]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [byte[]]) { return [Convert]::ToBase64String($Value) }
    if ($Value -is [string[]]) { return @($Value) }
    return $Value
}

function Get-RegistryTreeSnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ Path = $Path; Exists = $false; Keys = @() }
    }
    $keys = @((Get-Item -LiteralPath $Path)) + @(Get-ChildItem -LiteralPath $Path -Recurse -ErrorAction Stop)
    $rows = @($keys | ForEach-Object {
        $key = $_
        $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            $name = [string]$_
            [ordered]@{
                Name = $name
                Kind = [string]$key.GetValueKind($name)
                Value = Convert-RegistryValueForSnapshot ($key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames))
            }
        })
        [ordered]@{ Name = [string]$key.Name; Values = $values }
    } | Sort-Object { $_.Name })
    return [ordered]@{ Path = $Path; Exists = $true; Keys = $rows }
}

function Get-InstallDirectorySnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [ordered]@{ Path = $Path; Exists = $false; Directories = @(); Files = @() }
    }
    $prefix = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $directories = @(Get-ChildItem -LiteralPath $Path -Directory -Force -Recurse | ForEach-Object {
        $_.FullName.Substring($prefix.Length).Replace('\', '/')
    } | Sort-Object)
    $files = @(Get-ChildItem -LiteralPath $Path -File -Force -Recurse | ForEach-Object {
        [ordered]@{
            RelativePath = $_.FullName.Substring($prefix.Length).Replace('\', '/')
            Length = [long]$_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    } | Sort-Object { $_.RelativePath })
    return [ordered]@{ Path = $Path; Exists = $true; Directories = $directories; Files = $files }
}

function Get-ProtectedProcessSnapshot {
    param(
        [string]$OwnerSid,
        [int[]]$OnlyProcessIds
    )
    $protectedNames = @(
        'Codex.exe', 'codex.exe', 'explorer.exe', 'SLDWORKS.exe',
        'SwSearch.App.exe', 'SwSearch.PropertyViewer.exe',
        'SwSearch.Metadata.Worker.exe', 'SwSearch.Indexer.exe'
    )
    $nameFilter = ($protectedNames | ForEach-Object { "Name='$($_.Replace("'", "''"))'" }) -join ' OR '
    $rows = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter $nameFilter -ErrorAction SilentlyContinue)) {
        if ($OnlyProcessIds -and $OnlyProcessIds -notcontains [int]$process.ProcessId) { continue }
        $sid = Get-ProcessOwnerSid -Process $process
        if (-not [string]::Equals($sid, $OwnerSid, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $rows += [ordered]@{
            ProcessId = [int]$process.ProcessId
            Name = [string]$process.Name
            ExecutablePath = [string]$process.ExecutablePath
            CommandLine = [string]$process.CommandLine
            CreationDate = if ($process.CreationDate) { ([DateTimeOffset]$process.CreationDate).ToString('o') } else { $null }
            SessionId = [int]$process.SessionId
            OwnerSid = $sid
        }
    }
    return @($rows | Sort-Object ProcessId)
}

function Get-ProtectedUserSnapshot {
    param(
        [Security.Principal.WindowsIdentity]$Identity,
        [int[]]$ProtectedProcessIds
    )
    $sid = $Identity.User.Value
    $installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\SwSearch'
    $registryPaths = @(
        'HKCU:\Software\SwSearch',
        'HKCU:\Software\Classes\SystemFileAssociations\.sldprt\shell\SwSearch.ViewProperties',
        'HKCU:\Software\Classes\SystemFileAssociations\.sldasm\shell\SwSearch.ViewProperties',
        'HKCU:\Software\Classes\SystemFileAssociations\.slddrw\shell\SwSearch.ViewProperties',
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($script:PreviousProductCode)",
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($script:CurrentProductCode)"
    )
    $processes = Get-ProtectedProcessSnapshot -OwnerSid $sid -OnlyProcessIds $ProtectedProcessIds
    $snapshot = [ordered]@{
        Account = $Identity.Name
        Sid = $sid
        Profile = [IO.Path]::GetFullPath($env:USERPROFILE)
        PreviousProductState = Get-ProductState $script:PreviousProductCode
        CurrentProductState = Get-ProductState $script:CurrentProductCode
        RelatedProducts = @(Get-RelatedProducts $script:UpgradeCode)
        InstallDirectory = Get-InstallDirectorySnapshot -Path $installDirectory
        DataDirectory = Get-InstallDirectorySnapshot -Path (Join-Path $env:LOCALAPPDATA 'SwSearch')
        Registry = @($registryPaths | ForEach-Object { Get-RegistryTreeSnapshot -Path $_ })
        ProtectedProcesses = @($processes)
    }
    return [pscustomobject]@{
        Snapshot = $snapshot
        StableHash = Get-CanonicalHash $snapshot
        ProtectedProcessIds = @($processes | ForEach-Object { [int]$_.ProcessId })
    }
}

function Assert-PathInside {
    param([string]$Candidate, [string]$Parent, [string]$Label)
    $candidateFull = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $candidateFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside its approved parent: $candidateFull"
    }
}

function Set-MinimalRunAcl {
    param([string]$Path, [string]$ProtectedSid, [string[]]$ValidationSids)
    $directoryInfo = Get-Item -LiteralPath $Path -ErrorAction Stop
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sidText in @($ProtectedSid, 'S-1-5-18', 'S-1-5-32-544')) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidText)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)
        $security.AddAccessRule($rule) | Out-Null
    }
    foreach ($sidText in $ValidationSids) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidText)
        $rights = [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize'
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, $rights, $inheritance, $propagation, $allow)
        $security.AddAccessRule($rule) | Out-Null
    }
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier($ProtectedSid)))
    $directoryInfo.SetAccessControl($security)
}

function Set-ScenarioAcl {
    param([string]$Path, [string]$ProtectedSid, [string]$ValidationSid)
    $directoryInfo = Get-Item -LiteralPath $Path -ErrorAction Stop
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sidText in @($ProtectedSid, 'S-1-5-18', 'S-1-5-32-544')) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidText)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, $propagation, $allow)
        $security.AddAccessRule($rule) | Out-Null
    }
    $validationIdentity = New-Object Security.Principal.SecurityIdentifier($ValidationSid)
    $validationRule = New-Object Security.AccessControl.FileSystemAccessRule($validationIdentity, [Security.AccessControl.FileSystemRights]'Modify, Synchronize', $inheritance, $propagation, $allow)
    $security.AddAccessRule($validationRule) | Out-Null
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier($ProtectedSid)))
    $directoryInfo.SetAccessControl($security)
}

function Set-ManifestAcl {
    param([string]$Path, [string]$ProtectedSid, [string]$ValidationSid)
    $security = New-Object Security.AccessControl.FileSecurity
    $security.SetAccessRuleProtection($true, $false)
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sidText in @($ProtectedSid, 'S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sidText)), [Security.AccessControl.FileSystemRights]::FullControl, $allow)
        $security.AddAccessRule($rule) | Out-Null
    }
    $readRule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($ValidationSid)), [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize', $allow)
    $security.AddAccessRule($readRule) | Out-Null
    $security.SetOwner((New-Object Security.Principal.SecurityIdentifier($ProtectedSid)))
    (Get-Item -LiteralPath $Path -ErrorAction Stop).SetAccessControl($security)
}

function New-ScenarioManifest {
    param(
        [string]$ScenarioId,
        [string]$Scenario,
        [pscustomobject]$Account,
        [string]$LifecyclePath,
        [string]$LifecycleSha256,
        [string]$CurrentMsi,
        [string]$PreviousMsi,
        [string]$HelperPath,
        [string]$HelperSha256,
        [string]$ResultRoot,
        [string]$ProtectedSid,
        [string]$ManifestDirectory,
        [string]$RunId
    )
    $nonce = New-CryptographicHex 32
    $required = @(
        'Manifest nonce binding','Runtime SID binding','Runtime SAM binding','Runtime profile binding',
        'Protected SID isolation','Staged lifecycle SHA256','Manifest ACL read only','Result ACL single writer',
        'Staged helper SHA256','Current MSI identity',"Scenario contract $Scenario"
    )
    $manifest = [ordered]@{
        SchemaVersion = 2
        RunId = $RunId
        Nonce = $nonce
        ScenarioId = $ScenarioId
        Scenario = $Scenario
        ExpectedUserSid = [string]$Account.Sid
        ExpectedSamAccountName = [string]$Account.Name
        ExpectedProfilePath = [IO.Path]::GetFullPath([string]$Account.ProfilePath).TrimEnd('\')
        ProtectedUserSid = $ProtectedSid
        Staged = [ordered]@{
            Lifecycle = [ordered]@{ Path = [IO.Path]::GetFullPath($LifecyclePath); Sha256 = $LifecycleSha256 }
            CurrentMsi = [ordered]@{ Path = [IO.Path]::GetFullPath($CurrentMsi); Sha256 = $script:CurrentMsiSha256; Version = $script:CurrentVersion; ProductCode = Normalize-GuidText $script:CurrentProductCode; UpgradeCode = Normalize-GuidText $script:UpgradeCode }
            PreviousMsi = if ($Scenario -eq 'Clean') { $null } else { [ordered]@{ Path = [IO.Path]::GetFullPath($PreviousMsi); Sha256 = $script:PreviousMsiSha256; Version = $script:PreviousVersion; ProductCode = Normalize-GuidText $script:PreviousProductCode; UpgradeCode = Normalize-GuidText $script:UpgradeCode } }
            Helper = [ordered]@{ Path = [IO.Path]::GetFullPath($HelperPath); Sha256 = $HelperSha256 }
        }
        ResultRoot = [IO.Path]::GetFullPath($ResultRoot)
        RequiredChecks = $required
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $path = Join-Path $ManifestDirectory ($ScenarioId + '.manifest.json')
    Write-AtomicUtf8Json -Path $path -Value $manifest
    Set-ManifestAcl -Path $path -ProtectedSid $ProtectedSid -ValidationSid $Account.Sid
    return [pscustomobject]@{
        Path = $path
        Nonce = $nonce
        Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        Manifest = $manifest
    }
}

function Assert-ExactObjectProperties {
    param($Object, [string[]]$Expected, [string]$Label)
    if ($null -eq $Object) { throw "$Label is null." }
    $actual = @($Object.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if (($actual -join "`n") -ne ($wanted -join "`n")) { throw "$Label schema mismatch. Expected=$($wanted -join ','); Actual=$($actual -join ',')." }
}

function Assert-StrictChildReport {
    param($Report, $ManifestRecord, [pscustomobject]$Account, [string]$ExpectedScenarioId, [string]$ExpectedScenario)
    $rootProperties = @(
        'SchemaVersion','GeneratedAt','RunId','Nonce','ScenarioId','Scenario','Account','UserSid','ProfilePath','ProtectedUserSid',
        'ManifestPath','ManifestSha256','IsAdministrator','MsiIdentity','PreviousMsiIdentity','InstallDirectory','Status','Passed','Skipped',
        'SkipReason','PrimaryError','CleanupError','TransactionStillRunning','UnmanagedChildStillRunning','ManualCleanupRequired','Helper',
        'OutstandingProcesses','ObservedForbiddenProcesses','Checks'
    )
    Assert-ExactObjectProperties $Report $rootProperties 'Child report'
    if ($Report.SchemaVersion -isnot [int] -or [int]$Report.SchemaVersion -ne 2) { throw 'Child SchemaVersion must be integer 2.' }
    foreach ($property in @('RunId','Nonce','ScenarioId','Scenario','Account','UserSid','ProfilePath','ProtectedUserSid','ManifestPath','ManifestSha256','InstallDirectory','Status')) {
        if ($Report.$property -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Report.$property)) { throw "Child $property must be a non-empty string." }
    }
    foreach ($property in @('Passed','Skipped','IsAdministrator','TransactionStillRunning','UnmanagedChildStillRunning','ManualCleanupRequired')) {
        if ($Report.$property -isnot [bool]) { throw "Child $property must be JSON boolean." }
    }
    if (-not [string]::Equals([string]$Report.RunId, [string]$ManifestRecord.Manifest.RunId, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$Report.Nonce, [string]$ManifestRecord.Nonce, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$Report.ScenarioId, $ExpectedScenarioId, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$Report.Scenario, $ExpectedScenario, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$Report.Account, [string]$Account.Name, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Report.UserSid, [string]$Account.Sid, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Report.ProfilePath, ([IO.Path]::GetFullPath([string]$Account.ProfilePath).TrimEnd('\')), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Report.ProtectedUserSid, $script:ProtectedUserSid, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Report.ManifestPath, [IO.Path]::GetFullPath([string]$ManifestRecord.Path), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Report.ManifestSha256, [string]$ManifestRecord.Sha256, [StringComparison]::Ordinal)) {
        throw 'Child report identity/run/nonce/manifest binding mismatch.'
    }
    Assert-ExactObjectProperties $Report.MsiIdentity @('Path','Sha256','Version','ProductCode','UpgradeCode') 'Child MsiIdentity'
    $expectedMsi = $ManifestRecord.Manifest.Staged.CurrentMsi
    foreach ($property in @('Path','Sha256','Version','ProductCode','UpgradeCode')) {
        if ($Report.MsiIdentity.$property -isnot [string] -or -not [string]::Equals([string]$Report.MsiIdentity.$property, [string]$expectedMsi.$property, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Child MsiIdentity.$property mismatch."
        }
    }
    if ($ExpectedScenario -eq 'Clean') {
        if ($null -ne $Report.PreviousMsiIdentity) { throw 'Clean child must report null PreviousMsiIdentity.' }
    }
    else {
        Assert-ExactObjectProperties $Report.PreviousMsiIdentity @('Path','Sha256','Version','ProductCode','UpgradeCode') 'Child PreviousMsiIdentity'
        foreach ($property in @('Path','Sha256','Version','ProductCode','UpgradeCode')) {
            if ($Report.PreviousMsiIdentity.$property -isnot [string] -or -not [string]::Equals([string]$Report.PreviousMsiIdentity.$property, [string]$ManifestRecord.Manifest.Staged.PreviousMsi.$property, [StringComparison]::OrdinalIgnoreCase)) { throw "Child PreviousMsiIdentity.$property mismatch." }
        }
    }
    Assert-ExactObjectProperties $Report.Helper @('Mode','ProcessId','CreationTime','ExecutablePath','CommandLine','OwnerSid','SessionId') 'Child Helper'
    if ($ExpectedScenario -in @('CloseResponding','CloseIgnoring')) {
        if ($Report.Helper.ProcessId -isnot [int] -or [int]$Report.Helper.ProcessId -le 0 -or $Report.Helper.SessionId -isnot [int] -or
            $Report.Helper.CreationTime -isnot [string] -or $Report.Helper.ExecutablePath -isnot [string] -or $Report.Helper.CommandLine -isnot [string] -or
            -not [string]::Equals([string]$Report.Helper.OwnerSid, [string]$Account.Sid, [StringComparison]::OrdinalIgnoreCase)) { throw 'Child Helper full process identity is incomplete or mismatched.' }
    }
    if ($Report.Checks -isnot [object[]] -or @($Report.Checks).Count -eq 0) { throw 'Child Checks must be a non-empty JSON array.' }
    $seen = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::Ordinal)
    foreach ($check in @($Report.Checks)) {
        Assert-ExactObjectProperties $check @('Name','Passed','Detail','Time') 'Child check'
        if ($check.Name -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$check.Name) -or $check.Passed -isnot [bool] -or $check.Detail -isnot [string] -or $check.Time -isnot [string]) { throw 'Child check has invalid strict types.' }
        if (-not $seen.Add([string]$check.Name)) { throw "Child report contains duplicate check name: $($check.Name)." }
        if ($check.Passed -ne $true) { throw "Child check did not pass: $($check.Name)." }
    }
    foreach ($required in @($ManifestRecord.Manifest.RequiredChecks)) {
        if (-not $seen.Contains([string]$required)) { throw "Child report is missing required check: $required." }
    }
    if ($Report.Status -ne 'PASS' -or $Report.Passed -ne $true -or $Report.Skipped -ne $false -or $Report.ManualCleanupRequired -ne $false -or
        $Report.TransactionStillRunning -ne $false -or $Report.UnmanagedChildStillRunning -ne $false -or $Report.IsAdministrator -ne $false) {
        throw 'Child status booleans violate the mandatory PASS contract.'
    }
    return $true
}

function New-RandomCredential {
    param([string]$AccountName)
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%_-'
    $bytes = New-Object byte[] 40
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) }
    finally { $rng.Dispose() }
    $characters = New-Object char[] 44
    $prefix = 'Aa1!'.ToCharArray()
    [Array]::Copy($prefix, $characters, $prefix.Length)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $characters[$index + 4] = $alphabet[$bytes[$index] % $alphabet.Length]
    }
    $plainText = -join $characters
    try {
        $secure = ConvertTo-SecureString -String $plainText -AsPlainText -Force
        return New-Object Management.Automation.PSCredential("$env:COMPUTERNAME\$AccountName", $secure)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        [Array]::Clear($characters, 0, $characters.Length)
        $plainText = $null
    }
}

function New-IsolatedValidationAccount {
    $suffixBytes = New-Object byte[] 4
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($suffixBytes) }
    finally { $rng.Dispose() }
    $suffix = ([BitConverter]::ToString($suffixBytes)).Replace('-', '')
    [Array]::Clear($suffixBytes, 0, $suffixBytes.Length)
    $name = "SwMsiTest_$suffix"
    if (Get-LocalUser -Name $name -ErrorAction SilentlyContinue) { throw "Random local account collision: $name" }
    $credential = New-RandomCredential -AccountName $name
    $record = [pscustomobject]@{
        Name = $name
        Sid = $null
        Credential = $credential
        CreationAttempted = $false
        Created = $false
        ProfilePath = $null
        ProfileCreated = $false
        UserRemoved = $false
        ProfileRemoved = $false
        RetainedReason = $null
    }
    # Track the planned unique name before creation so even a partial cmdlet
    # failure cannot leave an unrecorded account.
    $script:CreatedAccounts.Add($record)
    $record.CreationAttempted = $true
    if ($script:Journal) {
        $script:Journal.Accounts = @($script:Journal.Accounts) + @([ordered]@{ Name = $name; Sid = $null; ProfilePath = $null; Created = $false; Removed = $false })
        Write-JournalStage -Name "PlanAccount/$name" -Phase BEFORE -Detail @{ Name = $name }
    }
    $user = Invoke-JournaledChange -Name "CreateAccount/$name" -BeforeDetail @{ Name = $name; PasswordPersisted = $false } -Action {
        New-LocalUser -Name $name -Password $credential.Password -Description 'Temporary isolated SwSearch MSI lifecycle validation account' -AccountNeverExpires -PasswordNeverExpires -ErrorAction Stop
    } -AfterDetail { param($createdUser) @{ Name = $name; Sid = [string]$createdUser.Sid.Value } }
    $sid = [string]$user.Sid.Value
    $record.Sid = $sid
    $record.Created = $true
    foreach ($journalAccount in @($script:Journal.Accounts)) {
        if ([string]::Equals([string]$journalAccount.Name, $name, [StringComparison]::OrdinalIgnoreCase)) { $journalAccount.Sid = $sid; $journalAccount.Created = $true }
    }
    Write-JournalStage -Name "RecordAccount/$name" -Phase AFTER -Detail @{ Name = $name; Sid = $sid }

    $usersGroup = Get-LocalGroup -SID 'S-1-5-32-545' -ErrorAction Stop
    $isUsersMember = @(Get-LocalGroupMember -Group $usersGroup.Name -ErrorAction Stop | Where-Object { [string]$_.SID.Value -eq $sid }).Count -gt 0
    if (-not $isUsersMember) {
        Invoke-JournaledChange -Name "AddUsersGroup/$name" -BeforeDetail @{ Name = $name; Sid = $sid; GroupSid = 'S-1-5-32-545' } -Action { Add-LocalGroupMember -Group $usersGroup.Name -Member $name -ErrorAction Stop } -AfterDetail { @{ Added = $true } } | Out-Null
    }
    $administrators = Get-LocalGroup -SID 'S-1-5-32-544' -ErrorAction Stop
    $isAdministrator = @(Get-LocalGroupMember -Group $administrators.Name -ErrorAction Stop | Where-Object { [string]$_.SID.Value -eq $sid }).Count -gt 0
    if ($isAdministrator) { throw "Safety violation: temporary account $name is in Administrators." }

    return $record
}

function Quote-ProcessArgument {
    param([string]$Value)
    if ($null -eq $Value) { return '""' }
    if ($Value.Contains('"')) { throw 'An isolated child argument unexpectedly contains a quote.' }
    return '"' + $Value + '"'
}

function Update-JournalObservedHelpers {
    param([pscustomobject]$Account)
    if ($null -eq $script:Journal -or [string]::IsNullOrWhiteSpace($script:StagingRoot)) { return }
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='SwSearch.App.exe'" -ErrorAction SilentlyContinue)) {
        $ownerSid = Get-ProcessOwnerSid -Process $process
        $path = [string]$process.ExecutablePath
        if (-not [string]::Equals($ownerSid, [string]$Account.Sid, [StringComparison]::OrdinalIgnoreCase) -or [string]::IsNullOrWhiteSpace($path)) { continue }
        $expected = Join-Path $script:StagingRoot 'SwSearch.App.exe'
        if (-not [string]::Equals([IO.Path]::GetFullPath($path), [IO.Path]::GetFullPath($expected), [StringComparison]::OrdinalIgnoreCase)) { continue }
        $creation = if ($process.CreationDate) { ([DateTimeOffset]$process.CreationDate).ToString('o') } else { $null }
        if (@($script:Journal.HelperProcesses | Where-Object { $_.ProcessId -eq [int]$process.ProcessId -and $_.CreationTime -eq $creation }).Count -eq 0) {
            $script:Journal.HelperProcesses = @($script:Journal.HelperProcesses) + @([ordered]@{
                ProcessId = [int]$process.ProcessId; CreationTime = $creation; ExecutablePath = $path; CommandLine = [string]$process.CommandLine; OwnerSid = $ownerSid; SessionId = [int]$process.SessionId
            })
            Write-JournalStage -Name 'ObserveHelperProcess' -Phase AFTER -Detail $script:Journal.HelperProcesses[-1]
        }
    }
}

function Start-ProcessAsValidationUser {
    param(
        [pscustomobject]$Account,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$StandardOutputPath,
        [string]$StandardErrorPath,
        [int]$TimeoutSeconds
    )
    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argumentText = ($Arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join ' '
    $parameters = @{
        FilePath = $powerShellPath
        ArgumentList = $argumentText
        Credential = $Account.Credential
        LoadUserProfile = $true
        WindowStyle = 'Hidden'
        WorkingDirectory = $WorkingDirectory
        PassThru = $true
    }
    if ($StandardOutputPath) { $parameters.RedirectStandardOutput = $StandardOutputPath }
    if ($StandardErrorPath) { $parameters.RedirectStandardError = $StandardErrorPath }
    $process = Start-Process @parameters
    if ($null -eq $process) { throw "Unable to start isolated PowerShell for $($Account.Name)." }
    $startedAt = [DateTimeOffset]::Now
    $creationTime = try { $process.StartTime.ToUniversalTime().ToString('o') } catch { $null }
    if ($script:Journal) {
        $controllerProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction Stop
        $controllerIdentity = [ordered]@{ ProcessId = [int]$process.Id; CreationTime = if ($controllerProcess.CreationDate) { ([DateTimeOffset]$controllerProcess.CreationDate).ToString('o') } else { $creationTime }; ExecutablePath = [string]$controllerProcess.ExecutablePath; CommandLine = [string]$controllerProcess.CommandLine; OwnerSid = Get-ProcessOwnerSid -Process $controllerProcess; SessionId = [int]$controllerProcess.SessionId }
        $script:Journal.ControllerProcesses = @($script:Journal.ControllerProcesses) + @($controllerIdentity)
        Write-JournalStage -Name 'ControllerStarted' -Phase AFTER -Detail $controllerIdentity
    }
    $deadline = $startedAt.AddSeconds($TimeoutSeconds)
    while (-not $process.WaitForExit(500)) {
        Update-JournalObservedHelpers -Account $Account
        if ([DateTimeOffset]::Now -gt $deadline) {
            return [pscustomobject]@{
                Process = $process
                ProcessId = [int]$process.Id
                CreationTime = $creationTime
                CommandLine = "$powerShellPath $argumentText"
                ExitCode = $null
                TimedOut = $true
                ElapsedMilliseconds = [long]([DateTimeOffset]::Now - $startedAt).TotalMilliseconds
            }
        }
    }
    $process.WaitForExit()
    return [pscustomobject]@{
        Process = $process
        ProcessId = [int]$process.Id
        CreationTime = $creationTime
        CommandLine = "$powerShellPath $argumentText"
        ExitCode = [int]$process.ExitCode
        TimedOut = $false
        ElapsedMilliseconds = [long]([DateTimeOffset]::Now - $startedAt).TotalMilliseconds
    }
}

function Initialize-IsolatedProfile {
    param([pscustomobject]$Account)
    $result = Invoke-JournaledChange -Name "InitializeProfile/$($Account.Name)" -BeforeDetail @{ Name = $Account.Name; Sid = $Account.Sid; ExpectedProfilePath = "C:\Users\$($Account.Name)" } -Action {
        Start-ProcessAsValidationUser -Account $Account -Arguments @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'exit 0') -WorkingDirectory $script:StagingRoot -StandardOutputPath $null -StandardErrorPath $null -TimeoutSeconds 60
    } -AfterDetail { param($processResult) @{ ProcessId = $processResult.ProcessId; TimedOut = $processResult.TimedOut; ExitCode = $processResult.ExitCode } }
    if ($result.TimedOut) {
        $script:ManualCleanupRequired = $true
        $Account.RetainedReason = "Profile initialization process PID $($result.ProcessId) did not exit; it was not terminated."
        throw $Account.RetainedReason
    }
    if ($result.ExitCode -ne 0) { throw "Profile initialization failed for $($Account.Name), exit=$($result.ExitCode)." }
    $profile = Get-CimInstance Win32_UserProfile -Filter "SID='$($Account.Sid)'" -ErrorAction SilentlyContinue
    if ($null -eq $profile -or [string]::IsNullOrWhiteSpace([string]$profile.LocalPath)) {
        throw "Windows did not create a profile for $($Account.Name)."
    }
    $Account.ProfilePath = [string]$profile.LocalPath
    $Account.ProfileCreated = $true
    Assert-SafeTemporaryProfilePath -ProfilePath $Account.ProfilePath -AccountName $Account.Name -RejectContainedReparsePoint
    if ($script:Journal) {
        foreach ($journalAccount in @($script:Journal.Accounts)) {
            if ([string]::Equals([string]$journalAccount.Sid, [string]$Account.Sid, [StringComparison]::OrdinalIgnoreCase)) { $journalAccount.ProfilePath = [IO.Path]::GetFullPath($Account.ProfilePath).TrimEnd('\') }
        }
        Write-JournalStage -Name "ProfileIdentity/$($Account.Name)" -Phase AFTER -Detail @{ Sid = $Account.Sid; ProfilePath = $Account.ProfilePath }
    }
}

function Assert-SafeTemporaryProfilePath {
    param([string]$ProfilePath, [string]$AccountName, [switch]$RejectContainedReparsePoint, [string]$ProfilesRoot = 'C:\Users')
    if ($AccountName -notmatch '^SwMsiTest_[A-F0-9]{8}$') { throw "Unsafe temporary account name: $AccountName" }
    $full = [IO.Path]::GetFullPath($ProfilePath).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($ProfilesRoot).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath((Join-Path $root $AccountName)).TrimEnd('\')
    if (-not [string]::Equals($full, $expected, [StringComparison]::OrdinalIgnoreCase)) { throw "Profile path is not the exact approved profile-root/SwMsiTest_* path: $full" }
    foreach ($candidate in @($root, $full)) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Profile path contains a reparse/junction boundary: $candidate" }
        }
    }
    if ($RejectContainedReparsePoint -and (Test-Path -LiteralPath $full -PathType Container)) {
        $reparse = @(Get-ChildItem -LiteralPath $full -Force -Recurse -ErrorAction Stop | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($reparse.Count -gt 0) { throw "Temporary profile contains a reparse/junction entry: $($reparse[0].FullName)" }
    }
    return $full
}

function Wait-ForInstallerToBecomeIdle {
    param([int]$TimeoutSeconds = 1800)
    $deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    do {
        $state = Get-InstallerBusyState
        if (-not $state.IsBusy) { return $true }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::Now -lt $deadline)
    return $false
}

function Invoke-IsolatedScenario {
    param(
        [string]$Id,
        [ValidateSet('Clean', 'Upgrade', 'CloseResponding', 'CloseIgnoring')]
        [string]$LifecycleScenario,
        [pscustomobject]$Account,
        [string]$LifecycleScript,
        [string]$CurrentMsi,
        [string]$PreviousMsi,
        [string]$HelperPath,
        [string]$HelperSha256,
        [string]$ProtectedSid,
        [string]$ManifestDirectory,
        [string]$RunId
    )
    $scenarioRoot = Join-Path $script:RunRoot $Id
    Invoke-JournaledChange -Name "PrepareScenario/$Id" -BeforeDetail @{ Path = $scenarioRoot; Sid = $Account.Sid } -Action {
        New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
        Set-ScenarioAcl -Path $scenarioRoot -ProtectedSid $ProtectedSid -ValidationSid $Account.Sid
    } -AfterDetail { @{ Path = $scenarioRoot; Prepared = $true } } | Out-Null
    $manifestRecord = Invoke-JournaledChange -Name "CreateManifest/$Id" -BeforeDetail @{ Scenario = $LifecycleScenario; Sid = $Account.Sid } -Action {
        New-ScenarioManifest -ScenarioId $Id -Scenario $LifecycleScenario -Account $Account -LifecyclePath $LifecycleScript -LifecycleSha256 $script:LifecycleSourceSha256 -CurrentMsi $CurrentMsi -PreviousMsi $PreviousMsi -HelperPath $HelperPath -HelperSha256 $HelperSha256 -ResultRoot $scenarioRoot -ProtectedSid $ProtectedSid -ManifestDirectory $ManifestDirectory -RunId $RunId
    } -AfterDetail { param($createdManifest) @{ Path = $createdManifest.Path; Sha256 = $createdManifest.Sha256 } }
    $stdoutPath = Join-Path $scenarioRoot 'controller.stdout.log'
    $stderrPath = Join-Path $scenarioRoot 'controller.stderr.log'
    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $LifecycleScript,
        '-ManifestPath', $manifestRecord.Path,
        '-Nonce', $manifestRecord.Nonce,
        '-Execute'
    )

    $started = [DateTimeOffset]::Now
    $child = Invoke-JournaledChange -Name "RunScenario/$Id" -BeforeDetail @{ Scenario = $LifecycleScenario; Sid = $Account.Sid; Manifest = $manifestRecord.Path } -Action {
        Start-ProcessAsValidationUser -Account $Account -Arguments $arguments -WorkingDirectory $script:StagingRoot -StandardOutputPath $stdoutPath -StandardErrorPath $stderrPath -TimeoutSeconds 2400
    } -AfterDetail { param($childResult) @{ ProcessId = $childResult.ProcessId; ExitCode = $childResult.ExitCode; TimedOut = $childResult.TimedOut } }
    $reportPath = Join-Path $scenarioRoot 'lifecycle-report.json'
    $report = $null
    if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
        try { $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json }
        catch { }
    }
    $status = 'FAIL'
    $reason = $null
    if ($child.TimedOut) {
        $reason = "Isolated controller PID $($child.ProcessId) exceeded 2400 seconds and was deliberately not terminated."
        $script:OutstandingControllers.Add([pscustomobject]@{
            Process = $child.Process
            ProcessId = $child.ProcessId
            CreationTime = $child.CreationTime
            CommandLine = $child.CommandLine
            AccountSid = $Account.Sid
        })
        $script:ManualCleanupRequired = $true
        $Account.RetainedReason = $reason
    }
    elseif ($null -eq $report) {
        $reason = "Lifecycle report is missing or unreadable; child exit=$($child.ExitCode)."
    }
    else {
        try {
            if ($child.ExitCode -ne 0) { throw "Child exit code was $($child.ExitCode)." }
            $null = Assert-StrictChildReport -Report $report -ManifestRecord $manifestRecord -Account $Account -ExpectedScenarioId $Id -ExpectedScenario $LifecycleScenario
            $status = 'PASS'
        }
        catch {
            $reason = "Strict child report rejection: $($_.Exception.Message)"
        }
    }

    if ($report -and $report.ManualCleanupRequired) {
        $script:ManualCleanupRequired = $true
        $Account.RetainedReason = "Scenario $Id requires manual cleanup."
    }
    if ($report -and $report.Helper -and $report.Helper.ProcessId) {
        $helperProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($report.Helper.ProcessId)" -ErrorAction SilentlyContinue
        $helperStillExists = $false
        if ($helperProcess) {
            $helperCreation = if ($helperProcess.CreationDate) { ([DateTimeOffset]$helperProcess.CreationDate).ToString('o') } else { $null }
            $helperOwner = Get-ProcessOwnerSid -Process $helperProcess
            $helperStillExists =
                [string]::Equals([string]$helperProcess.Name, 'SwSearch.App.exe', [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([string]$helperProcess.ExecutablePath, [string]$report.Helper.ExecutablePath, [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([string]$helperProcess.CommandLine, [string]$report.Helper.CommandLine, [StringComparison]::Ordinal) -and
                [string]::Equals($helperCreation, [string]$report.Helper.CreationTime, [StringComparison]::Ordinal) -and
                [string]::Equals($helperOwner, $Account.Sid, [StringComparison]::OrdinalIgnoreCase)
        }
        if ($helperStillExists) {
            $script:ManualCleanupRequired = $true
            $Account.RetainedReason = "Recorded helper PID $($report.Helper.ProcessId) is still alive; wrapper did not terminate it."
            $status = 'FAIL'
            $reason = $Account.RetainedReason
        }
    }

    $logs = if (Test-Path -LiteralPath $scenarioRoot) {
        @(Get-ChildItem -LiteralPath $scenarioRoot -File -Recurse | ForEach-Object { $_.FullName } | Sort-Object)
    }
    else { @() }
    $result = [pscustomobject]@{
        Id = $Id
        LifecycleScenario = $LifecycleScenario
        Status = $status
        Reason = $reason
        Account = $Account.Name
        UserSid = $Account.Sid
        StartedAt = $started.ToString('o')
        FinishedAt = [DateTimeOffset]::Now.ToString('o')
        ExitCode = $child.ExitCode
        DurationMilliseconds = $child.ElapsedMilliseconds
        ControllerProcess = [ordered]@{
            ProcessId = $child.ProcessId
            CreationTime = $child.CreationTime
            CommandLine = $child.CommandLine
            TimedOut = $child.TimedOut
        }
        ReportPath = $reportPath
        ManifestPath = $manifestRecord.Path
        ManifestSha256 = $manifestRecord.Sha256
        Logs = $logs
    }
    $script:ScenarioResults.Add($result)
    if ($status -eq 'PASS') {
        if ($LifecycleScenario -eq 'Clean') {
            $script:Journal.Products.Current.InstalledByRun = $true
            $script:Journal.Products.Current.Removed = $true
        }
        else {
            $script:Journal.Products.Previous.InstalledByRun = $true
            $script:Journal.Products.Previous.Removed = $true
            if ($LifecycleScenario -ne 'CloseIgnoring') { $script:Journal.Products.Current.InstalledByRun = $true; $script:Journal.Products.Current.Removed = $true }
        }
        Write-JournalStage -Name "ProductStages/$Id" -Phase AFTER -Detail $script:Journal.Products
    }
    $script:Journal.Results = @($script:Journal.Results) + @([ordered]@{ ScenarioId = $Id; Scenario = $LifecycleScenario; Status = $status; ReportPath = $reportPath; ManifestSha256 = $manifestRecord.Sha256 })
    Write-JournalStage -Name "ScenarioResult/$Id" -Phase AFTER -Detail $script:Journal.Results[-1]
    return $result
}

function Remove-IsolatedValidationAccountSafely {
    param([pscustomobject]$Account)
    if ($Account.RetainedReason) { return $false }
    try {
        if (-not $Account.CreationAttempted -or -not $Account.Created -or $Account.Name -notmatch '^SwMsiTest_[A-F0-9]{8}$' -or [string]::IsNullOrWhiteSpace([string]$Account.Sid)) { throw 'Account cleanup authorization record is incomplete.' }
        if ([string]::Equals([string]$Account.Sid, $script:ProtectedUserSid, [StringComparison]::OrdinalIgnoreCase)) { throw 'Protected liliy SID can never be cleaned as a test account.' }
        $profilePath = Assert-SafeTemporaryProfilePath -ProfilePath $Account.ProfilePath -AccountName $Account.Name -RejectContainedReparsePoint
        $installerUserData = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$($Account.Sid)"
        if (Test-Path -LiteralPath $installerUserData) { throw "Windows Installer UserData still exists; profile/account deletion is refused: $installerUserData" }
        $existingUser = Get-LocalUser -Name $Account.Name -ErrorAction SilentlyContinue
        if ($existingUser -and -not [string]::Equals([string]$existingUser.Sid.Value, [string]$Account.Sid, [StringComparison]::OrdinalIgnoreCase)) { throw 'Local account SID mismatch before cleanup.' }
        $profile = Get-CimInstance Win32_UserProfile -Filter "SID='$($Account.Sid)'" -ErrorAction SilentlyContinue
        if ($profile) {
            if (-not [string]::Equals(([IO.Path]::GetFullPath([string]$profile.LocalPath).TrimEnd('\')), $profilePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'CIM profile path differs from the recorded exact path.' }
            if ($profile.Loaded) { throw 'The temporary profile is still loaded.' }
            Invoke-JournaledChange -Name "RemoveProfile/$($Account.Name)" -BeforeDetail @{ Sid = $Account.Sid; ProfilePath = $profilePath } -Action { Remove-CimInstance -InputObject $profile -ErrorAction Stop } -AfterDetail { @{ Removed = $true } } | Out-Null
        }
        $Account.ProfileRemoved = $true
        if ($existingUser) {
            Invoke-JournaledChange -Name "RemoveAccount/$($Account.Name)" -BeforeDetail @{ Sid = $Account.Sid; Name = $Account.Name } -Action { Remove-LocalUser -Name $Account.Name -ErrorAction Stop } -AfterDetail { @{ Removed = $true } } | Out-Null
        }
        $Account.UserRemoved = $true
        $profileListPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$($Account.Sid)"
        $hkuPath = "Registry::HKEY_USERS\$($Account.Sid)"
        $residue = [ordered]@{
            CimProfile = [bool](Get-CimInstance Win32_UserProfile -Filter "SID='$($Account.Sid)'" -ErrorAction SilentlyContinue)
            ProfileList = Test-Path -LiteralPath $profileListPath
            Hku = Test-Path -LiteralPath $hkuPath
            InstallerUserData = Test-Path -LiteralPath $installerUserData
            Account = [bool](Get-LocalUser -Name $Account.Name -ErrorAction SilentlyContinue)
            ProfileDirectory = Test-Path -LiteralPath $profilePath
        }
        if (@($residue.GetEnumerator() | Where-Object { $_.Value }).Count -gt 0) { throw "Post-delete residue remains: $($residue | ConvertTo-Json -Compress)" }
        foreach ($journalAccount in @($script:Journal.Accounts)) {
            if ([string]::Equals([string]$journalAccount.Sid, [string]$Account.Sid, [StringComparison]::OrdinalIgnoreCase)) { $journalAccount.Removed = $true }
        }
        Write-JournalStage -Name "CleanupVerified/$($Account.Name)" -Phase AFTER -Detail @{ Sid = $Account.Sid; AllResidueAbsent = $true }
        return $true
    }
    catch {
        $Account.RetainedReason = "Safe profile/account cleanup failed: $($_.Exception.Message)"
        return $false
    }
}

function Remove-ValidationAclEntries {
    param([string]$Path, [string[]]$ValidationSids)
    $directories = @(Get-ChildItem -LiteralPath $Path -Directory -Force -Recurse -ErrorAction Stop | Sort-Object { $_.FullName.Length } -Descending) + @(Get-Item -LiteralPath $Path)
    foreach ($directory in $directories) {
        $security = Get-Acl -LiteralPath $directory.FullName
        foreach ($sidText in $ValidationSids) {
            if ([string]::IsNullOrWhiteSpace($sidText)) { continue }
            $sid = New-Object Security.Principal.SecurityIdentifier($sidText)
            $security.PurgeAccessRules($sid)
        }
        Set-Acl -LiteralPath $directory.FullName -AclObject $security
    }
}

function Get-ValidationAclResidue {
    param([string]$Path, [string[]]$ValidationSids)
    $sidSet = @{}
    foreach ($sid in $ValidationSids) {
        if (-not [string]::IsNullOrWhiteSpace($sid)) { $sidSet[$sid] = $true }
    }
    $items = @(Get-Item -LiteralPath $Path) + @(Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop)
    $residue = @()
    foreach ($item in $items) {
        $acl = Get-Acl -LiteralPath $item.FullName
        foreach ($rule in $acl.Access) {
            if ($sidSet.ContainsKey([string]$rule.IdentityReference.Value)) {
                $residue += [pscustomobject]@{ Path = $item.FullName; Identity = [string]$rule.IdentityReference.Value; Rights = [string]$rule.FileSystemRights; Inherited = [bool]$rule.IsInherited }
            }
        }
    }
    return @($residue)
}

function Get-ExactProcessIdentity {
    param([int]$ProcessId)
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    return [pscustomobject]@{
        ProcessId = [int]$process.ProcessId
        CreationTime = if ($process.CreationDate) { ([DateTimeOffset]$process.CreationDate).ToString('o') } else { $null }
        ExecutablePath = [string]$process.ExecutablePath
        CommandLine = [string]$process.CommandLine
        OwnerSid = Get-ProcessOwnerSid -Process $process
        SessionId = [int]$process.SessionId
    }
}

function Test-ExactRecordedProcessIdentity {
    param($Actual, $Recorded)
    if ($null -eq $Actual -or $null -eq $Recorded) { return $false }
    return ([int]$Actual.ProcessId -eq [int]$Recorded.ProcessId -and
        [string]::Equals([string]$Actual.CreationTime, [string]$Recorded.CreationTime, [StringComparison]::Ordinal) -and
        [string]::Equals([IO.Path]::GetFullPath([string]$Actual.ExecutablePath), [IO.Path]::GetFullPath([string]$Recorded.ExecutablePath), [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$Actual.CommandLine, [string]$Recorded.CommandLine, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Actual.OwnerSid, [string]$Recorded.OwnerSid, [StringComparison]::OrdinalIgnoreCase) -and
        [int]$Actual.SessionId -eq [int]$Recorded.SessionId)
}

function Read-ValidatedRecoveryJournal {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($script:TrustedBootstrapRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Recovery journal is outside the protected bootstrap root.' }
    if (-not (Test-AdminProtectedBootstrapPath -ScriptPath $full)) { throw 'Recovery journal ACL/owner is not admin-protected.' }
    $journal = Get-Content -LiteralPath $full -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-ExactObjectProperties $journal @('SchemaVersion','RunId','ProtectedUserSid','RunRoot','StagingRoot','CreatedAtUtc','UpdatedAtUtc','Status','CurrentStage','Accounts','Products','HelperProcesses','ControllerProcesses','Results','Stages') 'Recovery journal'
    if ($journal.SchemaVersion -isnot [int] -or [int]$journal.SchemaVersion -ne 2) { throw 'Recovery journal schema version is invalid.' }
    if ([string]$journal.RunId -notmatch '^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$') { throw 'Recovery journal RunId is invalid.' }
    if (-not [string]::Equals([string]$journal.ProtectedUserSid, $script:ProtectedUserSid, [StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery journal protected SID mismatch.' }
    $expectedRunRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $script:ExpectedWorkspace 'tests\installer\results') ([string]$journal.RunId))).TrimEnd('\')
    $expectedStaging = Join-Path $expectedRunRoot 'staging'
    if (-not [string]::Equals([IO.Path]::GetFullPath([string]$journal.RunRoot).TrimEnd('\'), $expectedRunRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$journal.StagingRoot).TrimEnd('\'), $expectedStaging, [StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery journal run/staging path binding is invalid.' }
    if ([string]$journal.Status -in @('COMPLETED','RECOVERED')) { throw 'Completed/recovered journal does not require recovery.' }
    foreach ($account in @($journal.Accounts)) {
        Assert-ExactObjectProperties $account @('Name','Sid','ProfilePath','Created','Removed') 'Recovery account'
        if ([string]$account.Name -notmatch '^SwMsiTest_[A-F0-9]{8}$' -or [string]::IsNullOrWhiteSpace([string]$account.Sid) -or
            [string]::Equals([string]$account.Sid, $script:ProtectedUserSid, [StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery journal contains an unsafe account identity.' }
        $null = Assert-SafeTemporaryProfilePath -ProfilePath ([string]$account.ProfilePath) -AccountName ([string]$account.Name) -RejectContainedReparsePoint
    }
    return [pscustomobject]@{ Path = $full; Journal = $journal }
}

function Invoke-RecoveryFromJournal {
    param([string]$Path)
    Assert-InstallerIdle
    $record = Read-ValidatedRecoveryJournal -Path $Path
    $script:JournalPath = $record.Path
    $script:Journal = $record.Journal
    $script:RunRoot = [string]$record.Journal.RunRoot
    $script:StagingRoot = [string]$record.Journal.StagingRoot
    Write-JournalStage -Name 'RecoveryStart' -Phase BEFORE -Detail @{ CurrentSid = $identityContext.Sid }
    foreach ($controller in @($record.Journal.ControllerProcesses)) {
        $actual = Get-ExactProcessIdentity -ProcessId ([int]$controller.ProcessId)
        if ($actual) {
            if (Test-ExactRecordedProcessIdentity -Actual $actual -Recorded $controller) { throw "Recorded controller PID $($controller.ProcessId) is still alive; recovery refuses to clean while it may own Installer work." }
            throw "Controller PID $($controller.ProcessId) was reused or identity differs; recovery refuses cleanup."
        }
    }
    Assert-InstallerIdle
    foreach ($helper in @($record.Journal.HelperProcesses)) {
        $actual = Get-ExactProcessIdentity -ProcessId ([int]$helper.ProcessId)
        if ($null -eq $actual) { continue }
        if (-not (Test-ExactRecordedProcessIdentity -Actual $actual -Recorded $helper)) { throw "Helper PID $($helper.ProcessId) was reused or identity differs; recovery refuses to stop it." }
        Invoke-JournaledChange -Name "RecoveryStopHelper/$($helper.ProcessId)" -BeforeDetail $helper -Action { Stop-Process -Id ([int]$helper.ProcessId) -Force -ErrorAction Stop; Wait-Process -Id ([int]$helper.ProcessId) -Timeout 10 -ErrorAction SilentlyContinue } -AfterDetail { @{ Stopped = $true } } | Out-Null
    }
    Assert-InstallerIdle
    foreach ($journalAccount in @($record.Journal.Accounts)) {
        $user = Get-LocalUser -Name ([string]$journalAccount.Name) -ErrorAction SilentlyContinue
        if ($user -and -not [string]::Equals([string]$user.Sid.Value, [string]$journalAccount.Sid, [StringComparison]::OrdinalIgnoreCase)) { throw "Recovery account SID mismatch for $($journalAccount.Name)." }
        $account = [pscustomobject]@{ Name = [string]$journalAccount.Name; Sid = [string]$journalAccount.Sid; Credential = $null; CreationAttempted = $true; Created = $true; ProfilePath = [string]$journalAccount.ProfilePath; ProfileCreated = $true; UserRemoved = $false; ProfileRemoved = $false; RetainedReason = $null }
        if (-not (Remove-IsolatedValidationAccountSafely -Account $account)) { throw $account.RetainedReason }
        $journalAccount.Removed = $true
    }
    if (Test-Path -LiteralPath $script:StagingRoot) {
        $reparse = @(Get-ChildItem -LiteralPath $script:StagingRoot -Force -Recurse -ErrorAction Stop | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($reparse.Count -gt 0) { throw "Recovery staging contains reparse/junction content: $($reparse[0].FullName)" }
        Invoke-JournaledChange -Name 'RecoveryRemoveStaging' -BeforeDetail @{ Path = $script:StagingRoot } -Action { Remove-Item -LiteralPath $script:StagingRoot -Recurse -Force -ErrorAction Stop } -AfterDetail { @{ Removed = $true } } | Out-Null
    }
    Remove-ValidationAclEntries -Path $script:RunRoot -ValidationSids @($record.Journal.Accounts | ForEach-Object { [string]$_.Sid })
    $aclResidue = @(Get-ValidationAclResidue -Path $script:RunRoot -ValidationSids @($record.Journal.Accounts | ForEach-Object { [string]$_.Sid }))
    if ($aclResidue.Count -gt 0) { throw 'Recovery left temporary SID ACL entries.' }
    Complete-PersistentJournal -Status 'RECOVERED'
    Write-Output "Recovery PASS: $($record.Path)"
}

function Write-RunSummary {
    param(
        [string]$OverallStatus,
        [object]$BeforeSnapshot,
        [object]$AfterSnapshot,
        [string]$BeforeHash,
        [string]$AfterHash,
        [bool]$ProtectedStateStable,
        [string]$ErrorText
    )
    if (-not $script:RunRoot -or -not (Test-Path -LiteralPath $script:RunRoot -PathType Container)) { return }
    $accountRows = @($script:CreatedAccounts | ForEach-Object {
        [ordered]@{
            Name = $_.Name
            Sid = $_.Sid
            CreationAttempted = $_.CreationAttempted
            Created = $_.Created
            ProfilePath = $_.ProfilePath
            UserRemoved = $_.UserRemoved
            ProfileRemoved = $_.ProfileRemoved
            RetainedReason = $_.RetainedReason
        }
    })
    $outstandingControllerRows = @($script:OutstandingControllers | ForEach-Object {
        $stillRunning = $false
        try { $stillRunning = -not $_.Process.HasExited } catch { }
        [ordered]@{
            ProcessId = $_.ProcessId
            CreationTime = $_.CreationTime
            CommandLine = $_.CommandLine
            AccountSid = $_.AccountSid
            StillRunning = $stillRunning
        }
    })
    $beforePath = Join-Path $script:RunRoot 'protected-user-before.json'
    $afterPath = Join-Path $script:RunRoot 'protected-user-after.json'
    $summary = [ordered]@{
        SchemaVersion = 1
        GeneratedAt = [DateTimeOffset]::Now.ToString('o')
        Status = $OverallStatus
        Workspace = $script:ExpectedWorkspace
        RunRoot = $script:RunRoot
        CurrentMsi = [ordered]@{
            RelativePath = $script:CurrentMsiRelativePath
            Sha256 = $script:CurrentMsiSha256
            Version = $script:CurrentVersion
            ProductCode = $script:CurrentProductCode
            UpgradeCode = $script:UpgradeCode
        }
        PreviousMsi = [ordered]@{
            RelativePath = $script:PreviousMsiRelativePath
            Sha256 = $script:PreviousMsiSha256
            Version = $script:PreviousVersion
            ProductCode = $script:PreviousProductCode
        }
        ProtectedUser = [ordered]@{
            Sid = if ($BeforeSnapshot) { $BeforeSnapshot.Sid } else { $null }
            Account = if ($BeforeSnapshot) { $BeforeSnapshot.Account } else { $null }
            BeforeStableHash = $BeforeHash
            AfterStableHash = $AfterHash
            Stable = $ProtectedStateStable
            BeforeSnapshotPath = if (Test-Path -LiteralPath $beforePath) { $beforePath } else { $null }
            AfterSnapshotPath = if (Test-Path -LiteralPath $afterPath) { $afterPath } else { $null }
        }
        PreflightChecks = @($script:PreflightChecks)
        Scenarios = @($script:ScenarioResults)
        Accounts = $accountRows
        OutstandingControllers = $outstandingControllerRows
        StagingExists = [bool]($script:StagingRoot -and (Test-Path -LiteralPath $script:StagingRoot))
        ManualCleanupRequired = $script:ManualCleanupRequired
        Error = $ErrorText
    }
    $summaryPath = Join-Path $script:RunRoot 'summary.json'
    $summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("最终状态：$OverallStatus")
    $lines.Add("运行目录：$($script:RunRoot)")
    $lines.Add("受保护用户：$($summary.ProtectedUser.Account) ($($summary.ProtectedUser.Sid))")
    $lines.Add("liliy 前稳定哈希：$BeforeHash")
    $lines.Add("liliy 后稳定哈希：$AfterHash")
    $lines.Add("liliy 状态一致：$ProtectedStateStable")
    $lines.Add('')
    $lines.Add('场景结果：')
    foreach ($scenario in $script:ScenarioResults) {
        $lines.Add("- $($scenario.Id)：$($scenario.Status)；exit=$($scenario.ExitCode)；耗时=$($scenario.DurationMilliseconds)ms；日志=$($scenario.ReportPath)；原因=$($scenario.Reason)")
    }
    $lines.Add('')
    $createdAccountCount = @($accountRows | Where-Object { $_.Created }).Count
    if ($script:ManualCleanupRequired) {
        $lines.Add('MANUAL_CLEANUP：存在未能证明安全清理的状态。脚本没有强制终止 msiexec、Codex、Explorer、SOLIDWORKS、CLI 或控制进程；以下逐项记录实际状态。')
        foreach ($account in $script:CreatedAccounts) {
            $lines.Add("- $($account.Name) ($($account.Sid))：Created=$($account.Created)；UserRemoved=$($account.UserRemoved)；ProfileRemoved=$($account.ProfileRemoved)；原因=$($account.RetainedReason)")
        }
        $lines.Add("- staging仍存在：$([bool]($script:StagingRoot -and (Test-Path -LiteralPath $script:StagingRoot)))")
    }
    else {
        $lines.Add("清理：已验证清理 $createdAccountCount 个实际创建的临时 profile/账号、对应产品状态、SID registry/ACL 与 staging；仅保留验证日志。")
    }
    if ($ErrorText) {
        $lines.Add('')
        $lines.Add("错误：$ErrorText")
    }
    $lines | Set-Content -LiteralPath (Join-Path $script:RunRoot 'summary.txt') -Encoding UTF8
}

function Add-SkippedScenario {
    param([string]$Id, [string]$Reason)
    $script:ScenarioResults.Add([pscustomobject]@{
        Id = $Id
        LifecycleScenario = $null
        Status = 'SKIP'
        Reason = $Reason
        Account = $null
        UserSid = $null
        StartedAt = $null
        FinishedAt = [DateTimeOffset]::Now.ToString('o')
        ExitCode = $null
        DurationMilliseconds = 0
        ControllerProcess = $null
        ReportPath = $null
        Logs = @()
    })
}

if ($LibraryOnly) { return }

$identityContext = Get-CurrentIdentityContext
$identity = $identityContext.Identity
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isHighIntegrityAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$workspace = [IO.Path]::GetFullPath($script:ExpectedWorkspace).TrimEnd('\')
$expectedWrapperPath = Join-Path $script:ExpectedWorkspace 'tests\installer\Start-IsolatedMsiLifecycleValidation.ps1'
$currentMsiSource = Join-Path $script:ExpectedWorkspace $script:CurrentMsiRelativePath
$previousMsiSource = Join-Path $script:ExpectedWorkspace $script:PreviousMsiRelativePath
$lifecycleSource = Join-Path $script:ExpectedWorkspace 'tests\installer\Invoke-MsiLifecycleValidation.ps1'
$helperSource = Join-Path $script:ExpectedWorkspace 'tests\installer\SwSearchWindowHelper.cs'
$resultsParent = Join-Path $script:ExpectedWorkspace 'tests\installer\results'
$before = $null
$after = $null
$beforeHash = $null
$afterHash = $null
$stable = $false
$overallStatus = 'FAIL'

try {
    Assert-Preflight ([Environment]::Is64BitProcess) '64-bit PowerShell host' "Is64BitProcess=$([Environment]::Is64BitProcess)"
    Assert-Preflight ([string]::Equals($workspace, $script:ExpectedWorkspace, [StringComparison]::OrdinalIgnoreCase)) 'Exact workspace root' $workspace
    Assert-Preflight ([string]::Equals($identityContext.SamAccountName, 'liliy', [StringComparison]::OrdinalIgnoreCase)) 'Protected SAM resolved from token SID' $identityContext.NtAccount
    Assert-Preflight ([string]::Equals($identityContext.ProfilePath, 'C:\Users\liliy', [StringComparison]::OrdinalIgnoreCase)) 'Protected profile resolved from Win32_UserProfile' $identityContext.ProfilePath
    Assert-Preflight ([string]::Equals($identityContext.Sid, $script:ProtectedUserSid, [StringComparison]::OrdinalIgnoreCase)) 'Exact protected liliy SID' $identityContext.Sid
    Assert-Preflight (Test-Path -LiteralPath $lifecycleSource -PathType Leaf) 'Lifecycle script exists' $lifecycleSource
    Assert-Preflight (Test-Path -LiteralPath $helperSource -PathType Leaf) 'Window helper source exists' $helperSource
    Assert-Preflight ((Get-FileHash -LiteralPath $lifecycleSource -Algorithm SHA256).Hash -eq $script:LifecycleSourceSha256) 'Frozen lifecycle source SHA256' $script:LifecycleSourceSha256
    Assert-Preflight ((Get-FileHash -LiteralPath $helperSource -Algorithm SHA256).Hash -eq $script:HelperSourceSha256) 'Frozen helper source SHA256' $script:HelperSourceSha256
    Assert-FixedMsiIdentity -Path $currentMsiSource -ExpectedSha256 $script:CurrentMsiSha256 -ExpectedVersion $script:CurrentVersion -ExpectedProductCode $script:CurrentProductCode -ExpectedUpgradeCode $script:UpgradeCode -Label 'Current v1.1.1 MSI'
    Assert-FixedMsiIdentity -Path $previousMsiSource -ExpectedSha256 $script:PreviousMsiSha256 -ExpectedVersion $script:PreviousVersion -ExpectedProductCode $script:PreviousProductCode -ExpectedUpgradeCode $script:UpgradeCode -Label 'Previous v1.0.1 MSI'
    Assert-InstallerIdle

    if (-not [string]::IsNullOrWhiteSpace($RecoverFromJournal)) {
        Assert-Preflight (-not $Execute) 'Recovery is mutually exclusive with Execute' 'Execute=False'
        Assert-Preflight $isHighIntegrityAdministrator 'Recovery requires high-integrity administrator token' 'Administrator=True'
        Assert-Preflight $ConfirmIsolatedSystemChanges 'Recovery requires explicit confirmation' 'ConfirmIsolatedSystemChanges=True'
        Assert-Preflight (Test-AdminProtectedBootstrapPath -ScriptPath $PSCommandPath) 'Recovery requires trusted protected bootstrap copy' $PSCommandPath
        Invoke-RecoveryFromJournal -Path $RecoverFromJournal
        return
    }

    $targetNames = @('SwSearch.App.exe', 'SwSearch.PropertyViewer.exe', 'SwSearch.Metadata.Worker.exe', 'SwSearch.Indexer.exe')
    $targetFilter = ($targetNames | ForEach-Object { "Name='$_'" }) -join ' OR '
    $existingTargets = @(Get-CimInstance Win32_Process -Filter $targetFilter -ErrorAction SilentlyContinue | ForEach-Object {
        [pscustomobject]@{ ProcessId = $_.ProcessId; Name = $_.Name; SessionId = $_.SessionId; OwnerSid = Get-ProcessOwnerSid $_ }
    })
    Assert-Preflight ($existingTargets.Count -eq 0) 'No existing SwSearch target process in any session' ($existingTargets | ConvertTo-Json -Compress)

    $oldState = Get-ProductState $script:PreviousProductCode
    $newState = Get-ProductState $script:CurrentProductCode
    $related = @(Get-RelatedProducts $script:UpgradeCode)
    Assert-Preflight ($oldState -eq 5) 'Protected liliy v1.0.1 remains installed' "ProductState=$oldState"
    Assert-Preflight ($newState -eq -1) 'Protected liliy v1.1.1 is not registered' "ProductState=$newState"
    Assert-Preflight ($related.Count -eq 1 -and $related[0] -eq (Normalize-GuidText $script:PreviousProductCode)) 'Protected liliy related-product baseline' ($related -join ',')

    foreach ($commandName in @('New-LocalUser', 'Get-LocalUser', 'Remove-LocalUser', 'Get-LocalGroup', 'Get-LocalGroupMember', 'Add-LocalGroupMember')) {
        Assert-Preflight ($null -ne (Get-Command $commandName -ErrorAction SilentlyContinue)) "Built-in command $commandName" 'Available'
    }
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Assert-Preflight (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf) 'Windows PowerShell host exists' $windowsPowerShell
    Add-PreflightCheck -Name 'High-integrity administrator token' -Passed $isHighIntegrityAdministrator -Detail "Administrator=$isHighIntegrityAdministrator"

    $isDryRun = (-not $Execute) -or $WhatIfPreference
    if ($isDryRun) {
        Write-Output ''
        Write-Output 'DRY RUN ONLY：已完成固定路径、MSI SHA256/ProductCode/UpgradeCode、当前 SID/profile、liliy 1.0.1 状态、全局 SwSearch 进程及 Windows Installer 空闲检查。'
        Write-Output '未创建目录、账号、profile、ACL、helper，未调用 msiexec。真实执行还必须同时给出 -Execute 与 -ConfirmIsolatedSystemChanges，并使用高权限管理员令牌。'
        if (-not $isHighIntegrityAdministrator) {
            Write-Output '当前不是高权限管理员令牌；按设计不会发生任何变更。'
        }
        return
    }

    Assert-Preflight $isHighIntegrityAdministrator 'Execute requires high-integrity administrator token' 'Run Windows PowerShell as administrator.'
    Assert-Preflight (Test-AdminProtectedBootstrapPath -ScriptPath $PSCommandPath) 'Execute requires trusted protected bootstrap copy' $PSCommandPath
    Assert-Preflight $ConfirmIsolatedSystemChanges 'Explicit isolated-system-change confirmation' 'ConfirmIsolatedSystemChanges=True'
    if (-not $PSCmdlet.ShouldProcess('two random SwMsiTest_* standard users and fixed v1.0.1/v1.1.1 per-user MSI lifecycles', 'Create isolated profiles, run silent MSI validation, then remove only test state')) {
        return
    }

    $beforeContainer = Get-ProtectedUserSnapshot -Identity $identity
    $before = $beforeContainer.Snapshot
    $beforeHash = $beforeContainer.StableHash

    $runIdBytes = New-Object byte[] 4
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($runIdBytes) } finally { $rng.Dispose() }
    $runId = ([DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss')) + '-' + ([BitConverter]::ToString($runIdBytes)).Replace('-', '').ToLowerInvariant()
    [Array]::Clear($runIdBytes, 0, $runIdBytes.Length)
    $script:RunRoot = Join-Path $resultsParent $runId
    $script:StagingRoot = Join-Path $script:RunRoot 'staging'
    $script:JournalPath = Join-Path (Split-Path -Parent $PSCommandPath) ($runId + '.journal.json')
    Initialize-PersistentJournal -Path $script:JournalPath -RunId $runId -ProtectedSid $identity.User.Value -RunRoot $script:RunRoot -StagingRoot $script:StagingRoot
    Assert-PathInside -Candidate $script:RunRoot -Parent $resultsParent -Label 'Run root'
    Invoke-JournaledChange -Name 'CreateRunRoot' -BeforeDetail @{ Path = $script:RunRoot } -Action {
        New-Item -ItemType Directory -Path $script:RunRoot -Force | Out-Null
        Set-MinimalRunAcl -Path $script:RunRoot -ProtectedSid $identity.User.Value -ValidationSids @()
        Write-AtomicUtf8Json -Path (Join-Path $script:RunRoot 'protected-user-before.json') -Value $before
    } -AfterDetail { @{ Path = $script:RunRoot; AclProtected = $true } } | Out-Null

    $cleanAccount = New-IsolatedValidationAccount
    $upgradeAccount = New-IsolatedValidationAccount
    Invoke-JournaledChange -Name 'GrantRunReadAccess' -BeforeDetail @{ Sids = @($cleanAccount.Sid, $upgradeAccount.Sid) } -Action {
        Set-MinimalRunAcl -Path $script:RunRoot -ProtectedSid $identity.User.Value -ValidationSids @($cleanAccount.Sid, $upgradeAccount.Sid)
    } -AfterDetail { @{ Applied = $true } } | Out-Null

    Invoke-JournaledChange -Name 'CreateStaging' -BeforeDetail @{ Path = $script:StagingRoot } -Action { New-Item -ItemType Directory -Path $script:StagingRoot -Force | Out-Null } -AfterDetail { @{ Created = $true } } | Out-Null
    Assert-PathInside -Candidate $script:StagingRoot -Parent $script:RunRoot -Label 'Staging root'
    $stagedCurrentMsi = Join-Path $script:StagingRoot 'SwSearch-x64-v1.1.1.msi'
    $stagedPreviousMsi = Join-Path $script:StagingRoot 'SwSearch-x64-v1.0.1.msi'
    $stagedLifecycle = Join-Path $script:StagingRoot 'Invoke-MsiLifecycleValidation.ps1'
    $stagedHelper = Join-Path $script:StagingRoot 'SwSearch.App.exe'
    $manifestDirectory = Join-Path $script:StagingRoot 'manifests'
    Invoke-JournaledChange -Name 'StageTrustedInputs' -BeforeDetail @{ LifecycleSourceSha256 = $script:LifecycleSourceSha256; HelperSourceSha256 = $script:HelperSourceSha256 } -Action {
        Copy-Item -LiteralPath $currentMsiSource -Destination $stagedCurrentMsi -ErrorAction Stop
        Copy-Item -LiteralPath $previousMsiSource -Destination $stagedPreviousMsi -ErrorAction Stop
        Copy-Item -LiteralPath $lifecycleSource -Destination $stagedLifecycle -ErrorAction Stop
        New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
        $helperDefinition = Get-Content -LiteralPath $helperSource -Raw -Encoding UTF8
        Add-Type -TypeDefinition $helperDefinition -Language CSharp -OutputAssembly $stagedHelper -OutputType WindowsApplication -ErrorAction Stop | Out-Null
    } -AfterDetail { @{ StagingRoot = $script:StagingRoot } } | Out-Null
    Assert-Preflight ((Get-FileHash -LiteralPath $stagedCurrentMsi -Algorithm SHA256).Hash -eq $script:CurrentMsiSha256) 'Staged current MSI hash' $script:CurrentMsiSha256
    Assert-Preflight ((Get-FileHash -LiteralPath $stagedPreviousMsi -Algorithm SHA256).Hash -eq $script:PreviousMsiSha256) 'Staged previous MSI hash' $script:PreviousMsiSha256
    Assert-Preflight ((Get-FileHash -LiteralPath $stagedLifecycle -Algorithm SHA256).Hash -eq $script:LifecycleSourceSha256) 'Staged lifecycle hash' $script:LifecycleSourceSha256
    $helperHash = (Get-FileHash -LiteralPath $stagedHelper -Algorithm SHA256).Hash
    Write-JournalStage -Name 'StagedHashes' -Phase AFTER -Detail @{ Lifecycle = $script:LifecycleSourceSha256; Helper = $helperHash; CurrentMsi = $script:CurrentMsiSha256; PreviousMsi = $script:PreviousMsiSha256 }

    Initialize-IsolatedProfile -Account $cleanAccount
    Initialize-IsolatedProfile -Account $upgradeAccount

    $continue = $true
    $a = Invoke-IsolatedScenario -Id 'A-Clean' -LifecycleScenario Clean -Account $cleanAccount -LifecycleScript $stagedLifecycle -CurrentMsi $stagedCurrentMsi -PreviousMsi $stagedPreviousMsi -HelperPath $stagedHelper -HelperSha256 $helperHash -ProtectedSid $identity.User.Value -ManifestDirectory $manifestDirectory -RunId $runId
    if ($a.Status -eq 'FAIL') { $continue = $false }
    if ($continue -and -not (Wait-ForInstallerToBecomeIdle)) {
        $script:ManualCleanupRequired = $true
        $upgradeAccount.RetainedReason = 'Windows Installer did not become idle naturally within 1800 seconds.'
        $continue = $false
    }

    if ($continue) {
        $b = Invoke-IsolatedScenario -Id 'B-Upgrade' -LifecycleScenario Upgrade -Account $upgradeAccount -LifecycleScript $stagedLifecycle -CurrentMsi $stagedCurrentMsi -PreviousMsi $stagedPreviousMsi -HelperPath $stagedHelper -HelperSha256 $helperHash -ProtectedSid $identity.User.Value -ManifestDirectory $manifestDirectory -RunId $runId
        if ($b.Status -eq 'FAIL') { $continue = $false }
    }
    else { Add-SkippedScenario -Id 'B-Upgrade' -Reason 'A previous mandatory scenario failed or could not be proved idle.' }

    if ($continue -and -not (Wait-ForInstallerToBecomeIdle)) {
        $script:ManualCleanupRequired = $true
        $upgradeAccount.RetainedReason = 'Windows Installer did not become idle naturally within 1800 seconds.'
        $continue = $false
    }
    if ($continue) {
        $c1 = Invoke-IsolatedScenario -Id 'C1-WM_CLOSE-Responding' -LifecycleScenario CloseResponding -Account $upgradeAccount -LifecycleScript $stagedLifecycle -CurrentMsi $stagedCurrentMsi -PreviousMsi $stagedPreviousMsi -HelperPath $stagedHelper -HelperSha256 $helperHash -ProtectedSid $identity.User.Value -ManifestDirectory $manifestDirectory -RunId $runId
        if ($c1.Status -eq 'FAIL') { $continue = $false }
    }
    else { Add-SkippedScenario -Id 'C1-WM_CLOSE-Responding' -Reason 'A previous mandatory scenario failed or could not be proved idle.' }

    if ($continue -and -not (Wait-ForInstallerToBecomeIdle)) {
        $script:ManualCleanupRequired = $true
        $upgradeAccount.RetainedReason = 'Windows Installer did not become idle naturally within 1800 seconds.'
        $continue = $false
    }
    if ($continue) {
        $c2 = Invoke-IsolatedScenario -Id 'C2-WM_CLOSE-Ignoring' -LifecycleScenario CloseIgnoring -Account $upgradeAccount -LifecycleScript $stagedLifecycle -CurrentMsi $stagedCurrentMsi -PreviousMsi $stagedPreviousMsi -HelperPath $stagedHelper -HelperSha256 $helperHash -ProtectedSid $identity.User.Value -ManifestDirectory $manifestDirectory -RunId $runId
        if ($c2.Status -eq 'FAIL') { $continue = $false }
    }
    else { Add-SkippedScenario -Id 'C2-WM_CLOSE-Ignoring' -Reason 'A previous mandatory scenario failed or could not be proved idle.' }

    Add-SkippedScenario -Id 'D-ForcedRollback' -Reason 'SKIP：在真实主机上注入断电/进程崩溃等回滚故障不能证明不会影响全局 Windows Installer，因此不执行危险故障注入。'
}
catch {
    $script:FatalError = $_
    Write-Error $_ -ErrorAction Continue
}
finally {
    if ($Execute -and -not $WhatIfPreference -and $script:RunRoot) {
        try {
            foreach ($controller in $script:OutstandingControllers) {
                $controllerDeadline = [DateTimeOffset]::Now.AddSeconds(1800)
                while (-not $controller.Process.WaitForExit(1000) -and [DateTimeOffset]::Now -lt $controllerDeadline) {
                    # Natural completion only. The controller and its children
                    # are never terminated by this wrapper.
                }
                if (-not $controller.Process.HasExited) {
                    $script:ManualCleanupRequired = $true
                }
            }
            if (-not (Wait-ForInstallerToBecomeIdle -TimeoutSeconds 1800)) {
                $script:ManualCleanupRequired = $true
                foreach ($account in $script:CreatedAccounts | Where-Object { -not $_.RetainedReason }) {
                    $account.RetainedReason = 'Windows Installer remained active; account/profile/staging were preserved for manual cleanup.'
                }
            }

            if (-not $script:ManualCleanupRequired) {
                foreach ($account in $script:CreatedAccounts) {
                    if (-not (Remove-IsolatedValidationAccountSafely -Account $account)) {
                        $script:ManualCleanupRequired = $true
                    }
                }
            }

            if (-not $script:ManualCleanupRequired -and $script:StagingRoot -and (Test-Path -LiteralPath $script:StagingRoot)) {
                $expectedStaging = Join-Path $script:RunRoot 'staging'
                Assert-PathInside -Candidate $script:StagingRoot -Parent $script:RunRoot -Label 'Cleanup staging root'
                if (-not [string]::Equals([IO.Path]::GetFullPath($script:StagingRoot), [IO.Path]::GetFullPath($expectedStaging), [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Staging cleanup path mismatch.'
                }
                Invoke-JournaledChange -Name 'RemoveStaging' -BeforeDetail @{ Path = $script:StagingRoot } -Action { Remove-Item -LiteralPath $script:StagingRoot -Recurse -Force -ErrorAction Stop } -AfterDetail { @{ Removed = $true } } | Out-Null
                Invoke-JournaledChange -Name 'PurgeValidationAcls' -BeforeDetail @{ Path = $script:RunRoot; Sids = @($script:CreatedAccounts | ForEach-Object { $_.Sid }) } -Action { Remove-ValidationAclEntries -Path $script:RunRoot -ValidationSids @($script:CreatedAccounts | ForEach-Object { $_.Sid }) } -AfterDetail { @{ Purged = $true } } | Out-Null
                if (Test-Path -LiteralPath $script:StagingRoot) { throw 'Staging directory still exists after cleanup.' }
                $aclResidue = @(Get-ValidationAclResidue -Path $script:RunRoot -ValidationSids @($script:CreatedAccounts | ForEach-Object { $_.Sid }))
                if ($aclResidue.Count -gt 0) { throw "Temporary SID ACL residue remains: $($aclResidue | ConvertTo-Json -Compress)" }
                foreach ($account in $script:CreatedAccounts) {
                    if (-not $account.UserRemoved -or -not $account.ProfileRemoved -or (Get-LocalUser -Name $account.Name -ErrorAction SilentlyContinue)) {
                        throw "Temporary account/profile cleanup was not verified for $($account.Name)."
                    }
                    if (-not [string]::IsNullOrWhiteSpace([string]$account.Sid)) {
                        $profileListPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$($account.Sid)"
                        $installerUserData = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$($account.Sid)"
                        $hkuPath = "Registry::HKEY_USERS\$($account.Sid)"
                        $recordedProfilePath = if ($account.ProfilePath) { [IO.Path]::GetFullPath([string]$account.ProfilePath).TrimEnd('\') } else { "C:\Users\$($account.Name)" }
                        if ((Get-CimInstance Win32_UserProfile -Filter "SID='$($account.Sid)'" -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $profileListPath) -or (Test-Path -LiteralPath $installerUserData) -or (Test-Path -LiteralPath $hkuPath) -or (Test-Path -LiteralPath $recordedProfilePath)) {
                            throw "Temporary SID registry/profile residue remains for $($account.Sid)."
                        }
                    }
                }
            }
        }
        catch {
            $script:ManualCleanupRequired = $true
            if (-not $script:FatalError) { $script:FatalError = $_ }
            foreach ($account in $script:CreatedAccounts | Where-Object { -not $_.RetainedReason }) {
                $account.RetainedReason = "Cleanup exception: $($_.Exception.Message)"
            }
        }

        try {
            if ($before) {
                # Re-enumerate the full protected process set so a newly
                # launched liliy SwSearch/SOLIDWORKS/Codex process is detected,
                # not merely changes to baseline PIDs.
                $afterContainer = Get-ProtectedUserSnapshot -Identity $identity
                $after = $afterContainer.Snapshot
                $afterHash = $afterContainer.StableHash
                $stable = ($beforeHash -eq $afterHash)
                $after | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $script:RunRoot 'protected-user-after.json') -Encoding UTF8
                if (-not $stable) {
                    $script:ManualCleanupRequired = $true
                    if (-not $script:FatalError) { $script:FatalError = [Exception]'Protected liliy state changed during isolated validation.' }
                }
            }
        }
        catch {
            $stable = $false
            $script:ManualCleanupRequired = $true
            if (-not $script:FatalError) { $script:FatalError = $_ }
        }

        $mandatoryScenarioIds = @('A-Clean', 'B-Upgrade', 'C1-WM_CLOSE-Responding', 'C2-WM_CLOSE-Ignoring')
        $mandatoryNotPassed = @($mandatoryScenarioIds | Where-Object {
            $id = $_
            @($script:ScenarioResults | Where-Object { $_.Id -eq $id -and $_.Status -eq 'PASS' }).Count -ne 1
        }).Count
        # C1/C2 may report SKIP when same-session WM_CLOSE coverage cannot be
        # trusted, but that makes this run incomplete and therefore overall FAIL.
        # D is the only scenario whose deliberate SKIP is acceptable.
        $overallStatus = if ($script:FatalError -or $script:ManualCleanupRequired -or -not $stable -or $mandatoryNotPassed -gt 0) { 'FAIL' } else { 'PASS' }
        if ($script:Journal) { Complete-PersistentJournal -Status (if ($overallStatus -eq 'PASS') { 'COMPLETED' } elseif ($script:ManualCleanupRequired) { 'MANUAL_CLEANUP' } else { 'FAILED' }) }
        Write-RunSummary -OverallStatus $overallStatus -BeforeSnapshot $before -AfterSnapshot $after -BeforeHash $beforeHash -AfterHash $afterHash -ProtectedStateStable $stable -ErrorText (if ($script:FatalError) { $script:FatalError.Exception.ToString() } else { $null })
        Write-Output "验证结果：$overallStatus"
        Write-Output "结果目录：$($script:RunRoot)"
    }
}

if ($Execute -and -not $WhatIfPreference -and $overallStatus -ne 'PASS') {
    if (-not $script:RunRoot -and $script:FatalError) { throw $script:FatalError }
    throw "Isolated MSI lifecycle validation did not pass. See $($script:RunRoot)\summary.txt"
}
if (-not $Execute -and $script:FatalError) {
    throw $script:FatalError
}
