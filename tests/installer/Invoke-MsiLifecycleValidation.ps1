[CmdletBinding()]
param(
    [string]$MsiPath,

    [ValidateSet('Clean', 'Upgrade', 'CloseResponding', 'CloseIgnoring')]
    [string]$Scenario = 'Clean',

    [string]$PreviousMsiPath,
    [string]$ResultRoot,

    [string]$ExpectedVersion = '1.1.1',
    [string]$ExpectedMsiSha256 = '418CB6AD9CDC4B78ECDF8F83B7AFEF2E323E7E2E233F3BCD18C7FC4983465DFA',
    [string]$ProductCode = '{25A0D431-5981-43D8-A996-8CAB7E77BA84}',
    [string]$UpgradeCode = '{D9158356-29B3-44D6-A15A-ACDCE8328703}',

    [string]$PreviousVersion = '1.0.1',
    [string]$PreviousMsiSha256 = '55B6F988249B65822FFF7E683334BD5533722AA7FE5B1A843B9F616C81AAFE77',
    [string]$PreviousProductCode = '{AED45CC2-3804-4203-9A2A-88F60D71671F}',

    [int]$ExpectedPayloadFileCount = 483,
    [int]$ExpectedManifestEntryCount = 482,
    [string]$ExpectedManifestSha256 = '0D4F0DC3BCCA92BE8D708F9157A03D6CAB5D13B1546326D77B955E4E8C3EF7D8',

    [string]$HelperPath,
    [string]$ExpectedHelperSha256,

    [string]$ProtectedUserSid,

    [string]$ExpectedAccountName,

    [string]$ExpectedUserSid,

    [string]$ManifestPath,

    [string]$Nonce,

    [switch]$LibraryOnly,

    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:Checks = New-Object System.Collections.Generic.List[object]
$script:ObservedForbiddenProcesses = New-Object System.Collections.Generic.List[object]
$script:Installer = New-Object -ComObject WindowsInstaller.Installer
$script:InstallCompleted = $false
$script:PreviousInstallCompleted = $false
$script:AnyInstallAttempted = $false
$script:TransactionStillRunning = $false
$script:UnmanagedChildStillRunning = $false
$script:ScenarioSkipped = $false
$script:SkipReason = $null
$script:HelperProcessId = $null
$script:HelperProcessCreationTime = $null
$script:HelperExecutablePath = $null
$script:HelperCommandLine = $null
$script:HelperOwnerSid = $null
$script:HelperSessionId = $null
$script:HelperMode = $null
$script:AllowedProcessIds = @{}
$script:OutstandingProcesses = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )

    $baseName = [string]$Name
    $duplicateCount = @($script:Checks | Where-Object { $_.Name -eq $baseName -or ([string]$_.Name).StartsWith($baseName + ' [repeat ', [StringComparison]::Ordinal) }).Count
    $uniqueName = if ($duplicateCount -eq 0) { $baseName } else { "$baseName [repeat $($duplicateCount + 1)]" }
    $script:Checks.Add([pscustomobject]@{
        Name = $uniqueName
        Passed = [bool]$Passed
        Detail = [string]$Detail
        Time = [DateTimeOffset]::Now.ToString('o')
    })
    $prefix = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Output "[$prefix] $uniqueName - $Detail"
}

function Assert-Check {
    param(
        [bool]$Condition,
        [string]$Name,
        [string]$Detail
    )

    Add-Check -Name $Name -Passed $Condition -Detail $Detail
    if (-not $Condition) {
        throw "$Name failed: $Detail"
    }
}

function Normalize-GuidText {
    param([string]$Value)
    return ('{' + ([Guid]::Parse($Value).ToString().ToUpperInvariant()) + '}')
}

function Get-ProductState {
    param([string]$Code)
    try {
        return [int]$script:Installer.ProductState((Normalize-GuidText $Code))
    }
    catch {
        return -1
    }
}

function Get-ProductInfo {
    param(
        [string]$Code,
        [string]$Property
    )
    try {
        return [string]$script:Installer.ProductInfo((Normalize-GuidText $Code), $Property)
    }
    catch {
        return $null
    }
}

function Get-RelatedProducts {
    param([string]$Code)
    try {
        return @($script:Installer.RelatedProducts((Normalize-GuidText $Code))) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { Normalize-GuidText ([string]$_) } |
            Sort-Object -Unique
    }
    catch {
        return @()
    }
}

function Get-MsiProperty {
    param(
        [string]$Path,
        [string]$Property
    )

    if ($Property -notmatch '^[A-Za-z0-9_]+$') {
        throw "Unsafe MSI property name: $Property"
    }
    $database = $script:Installer.OpenDatabase($Path, 0)
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Property'")
    try {
        $view.Execute() | Out-Null
        $record = $view.Fetch()
        if ($null -eq $record) { return $null }
        return [string]$record.StringData(1)
    }
    finally {
        $view.Close() | Out-Null
    }
}

function Assert-MsiIdentity {
    param(
        [string]$Path,
        [string]$Version,
        [string]$Code,
        [string]$SharedUpgradeCode,
        [string]$Sha256,
        [string]$Label
    )

    Assert-Check (Test-Path -LiteralPath $Path -PathType Leaf) "$Label exists" $Path
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Assert-Check ($actualHash -eq $Sha256.ToUpperInvariant()) "$Label SHA256" $actualHash
    Assert-Check ((Get-MsiProperty $Path 'ProductVersion') -eq $Version) "$Label ProductVersion" $Version
    Assert-Check ((Normalize-GuidText (Get-MsiProperty $Path 'ProductCode')) -eq (Normalize-GuidText $Code)) "$Label ProductCode" $Code
    Assert-Check ((Normalize-GuidText (Get-MsiProperty $Path 'UpgradeCode')) -eq (Normalize-GuidText $SharedUpgradeCode)) "$Label UpgradeCode" $SharedUpgradeCode
}

function Get-MsiProcesses {
    $rows = @()
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" -ErrorAction SilentlyContinue)) {
        $parentName = $null
        if ($process.ParentProcessId) {
            $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ParentProcessId)" -ErrorAction SilentlyContinue
            if ($parent) { $parentName = [string]$parent.Name }
        }
        $rows += [pscustomobject]@{
            ProcessId = [int]$process.ProcessId
            ParentProcessId = [int]$process.ParentProcessId
            ParentName = $parentName
            SessionId = [int]$process.SessionId
            CreationDate = $process.CreationDate
            CommandLine = $process.CommandLine
        }
    }
    return @($rows)
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

function Get-ExactProcessIdentity {
    param([int]$ProcessId)
    $row = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $row) { return $null }
    return [pscustomobject]@{
        ProcessId = [int]$row.ProcessId
        Name = [string]$row.Name
        ExecutablePath = [string]$row.ExecutablePath
        CommandLine = [string]$row.CommandLine
        CreationDate = if ($row.CreationDate) { ([DateTimeOffset]$row.CreationDate).ToString('o') } else { $null }
        SessionId = [int]$row.SessionId
        OwnerSid = Get-ProcessOwnerSid -Process $row
    }
}

function Get-DirectoryContentSnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [pscustomobject]@{ Exists = $false; Directories = @(); Files = @(); Hash = $null }
    }
    $prefix = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    $directories = @(Get-ChildItem -LiteralPath $Path -Directory -Recurse -Force | ForEach-Object { $_.FullName.Substring($prefix.Length).Replace('\', '/') } | Sort-Object)
    $files = @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force | ForEach-Object {
        [pscustomobject]@{
            RelativePath = $_.FullName.Substring($prefix.Length).Replace('\', '/')
            Length = [long]$_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    } | Sort-Object RelativePath)
    $json = [ordered]@{ Directories = $directories; Files = $files } | ConvertTo-Json -Depth 5 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]$json)
        $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '')
    }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ Exists = $true; Directories = $directories; Files = $files; Hash = $hash }
}

function Convert-RegistrySnapshotValue {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [byte[]]) { return [Convert]::ToBase64String($Value) }
    if ($Value -is [string[]]) { return @($Value) }
    return $Value
}

function Get-RegistryTreeState {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [ordered]@{ Path = $Path; Exists = $false; Keys = @() } }
    $rows = @((Get-Item -LiteralPath $Path -ErrorAction Stop)) + @(Get-ChildItem -LiteralPath $Path -Recurse -ErrorAction Stop)
    $keys = @($rows | ForEach-Object {
        $key = $_
        $values = @($key.GetValueNames() | Sort-Object | ForEach-Object {
            $name = [string]$_
            [ordered]@{
                Name = $name
                Kind = [string]$key.GetValueKind($name)
                Value = Convert-RegistrySnapshotValue ($key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames))
            }
        })
        [ordered]@{ Name = [string]$key.Name; Values = $values }
    } | Sort-Object { $_.Name })
    return [ordered]@{ Path = $Path; Exists = $true; Keys = $keys }
}

function Get-PathAclState {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [ordered]@{ Path = $Path; Exists = $false; Owner = $null; Sddl = $null } }
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    return [ordered]@{
        Path = $Path
        Exists = $true
        Owner = [string]$acl.Owner
        Sddl = [string]$acl.Sddl
    }
}

function Get-CompleteInstallationStateSnapshot {
    param([string]$InstallDirectory)
    $currentCode = Normalize-GuidText $ProductCode
    $previousCode = Normalize-GuidText $PreviousProductCode
    $registryPaths = @(
        'HKCU:\Software\SwSearch\Installer',
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$previousCode",
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$currentCode",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$previousCode",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$currentCode",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$previousCode",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$currentCode",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$($script:ValidationIdentity.User.Value)\Products"
    )
    foreach ($extension in @('.sldprt','.sldasm','.slddrw')) {
        $registryPaths += "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\SwSearch.ViewProperties"
    }
    $installSnapshot = Get-DirectoryContentSnapshot -Path $InstallDirectory
    $installAcls = @()
    if (Test-Path -LiteralPath $InstallDirectory -PathType Container) {
        $installAcls = @((Get-Item -LiteralPath $InstallDirectory -Force) + @(Get-ChildItem -LiteralPath $InstallDirectory -Directory -Force -Recurse) | ForEach-Object { Get-PathAclState $_.FullName } | Sort-Object Path)
    }
    $startMenuSnapshot = Get-DirectoryContentSnapshot -Path $startMenuDirectory
    $state = [ordered]@{
        PreviousProductState = Get-ProductState $previousCode
        PreviousVersion = Get-ProductInfo $previousCode 'VersionString'
        CurrentProductState = Get-ProductState $currentCode
        CurrentVersion = Get-ProductInfo $currentCode 'VersionString'
        RelatedProducts = @(Get-RelatedProducts $UpgradeCode)
        PreviousArp = @(Get-UninstallEntries $previousCode)
        CurrentArp = @(Get-UninstallEntries $currentCode)
        InstallDirectory = $installSnapshot
        InstallDirectoryAcls = $installAcls
        StartMenu = $startMenuSnapshot
        StartMenuAcl = Get-PathAclState $startMenuDirectory
        ContextAndInstallerRegistry = @($registryPaths | ForEach-Object { Get-RegistryTreeState $_ })
    }
    $json = $state | ConvertTo-Json -Depth 30 -Compress
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($json)))).Replace('-', '') }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ State = $state; Hash = $hash }
}

function Assert-SuccessfulUpgradeLog {
    param([string]$LogPath)
    $text = Get-Content -LiteralPath $LogPath -Raw -ErrorAction Stop
    $closeIndex = $text.IndexOf('Wix4CloseApplications', [StringComparison]::OrdinalIgnoreCase)
    $removeIndex = $text.IndexOf('RemoveExistingProducts', [StringComparison]::OrdinalIgnoreCase)
    Assert-Check ($closeIndex -ge 0) 'Upgrade log contains CloseApplications action' $LogPath
    Assert-Check ($removeIndex -ge 0) 'Upgrade log contains RemoveExistingProducts action' $LogPath
    Assert-Check ($closeIndex -lt $removeIndex) 'CloseApplications precedes RemoveExistingProducts in upgrade log' "CloseIndex=$closeIndex; RemoveIndex=$removeIndex"
    Assert-Check ($text -match '(?im)^.*RemoveExistingProducts.*Return value 1') 'RemoveExistingProducts completed successfully' $LogPath
}

function Assert-RejectedUpgradeLog {
    param([string]$LogPath)
    $text = Get-Content -LiteralPath $LogPath -Raw -ErrorAction Stop
    Assert-Check ($text -match 'FailIfSwSearchStillRunning' -and $text -match 'Return value 3') 'Rejected upgrade has explicit Type 19 failure evidence' $LogPath
    Assert-Check ($text -notmatch '(?im)^.*Action start.*InstallValidate') 'Rejected upgrade stopped before InstallValidate' $LogPath
    Assert-Check ($text -notmatch '(?im)^.*Action start.*InstallInitialize') 'Rejected upgrade stopped before InstallInitialize' $LogPath
    Assert-Check ($text -notmatch '(?im)^.*Action start.*RemoveExistingProducts') 'Rejected upgrade never ran RemoveExistingProducts' $LogPath
}

function Start-TestWindowHelper {
    param(
        [ValidateSet('respond', 'ignore')]
        [string]$Mode,
        [string]$Executable,
        [string]$ReadyPath,
        [string]$EventPath
    )
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        throw "Window helper is missing: $Executable"
    }
    $actualHash = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    Assert-Check ($actualHash -eq $ExpectedHelperSha256.ToUpperInvariant()) 'Window helper SHA256' $actualHash
    Remove-Item -LiteralPath $ReadyPath, $EventPath -Force -ErrorAction SilentlyContinue

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Executable
    $startInfo.Arguments = "--mode $Mode --run-id `"$($script:Manifest.RunId)`" --nonce `"$($script:Manifest.Nonce)`" --ready `"$ReadyPath`" --events `"$EventPath`""
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'Unable to start the window helper.' }
    $script:HelperProcessId = $process.Id
    $script:HelperExecutablePath = $Executable
    $script:HelperMode = $Mode
    $script:AllowedProcessIds[$process.Id] = $true

    $deadline = [DateTimeOffset]::Now.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ReadyPath -PathType Leaf)) {
        if ($process.HasExited) { throw "Window helper exited before readiness; ExitCode=$($process.ExitCode)." }
        if ([DateTimeOffset]::Now -gt $deadline) {
            $script:UnmanagedChildStillRunning = $true
            $processCreationTime = $null
            try { $processCreationTime = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
            $script:OutstandingProcesses.Add([pscustomobject]@{
                Type = 'WindowHelper'
                ProcessId = [int]$process.Id
                CreationTime = $processCreationTime
                ExecutablePath = $Executable
                CommandLine = "$Executable $($startInfo.Arguments)"
                OwnerSid = $script:ValidationIdentity.User.Value
            })
            throw "Window helper did not report readiness; PID=$($process.Id). It was not terminated automatically."
        }
        Start-Sleep -Milliseconds 50
    }

    $ready = Get-Content -LiteralPath $ReadyPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Check ([int]$ready.pid -eq $process.Id) 'Window helper ready PID' "PID=$($process.Id)"
    Assert-Check ([int64]$ready.hwnd -ne 0) 'Window helper top-level HWND' "HWND=$($ready.hwnd)"
    Assert-Check ([string]::Equals([string]$ready.runId, [string]$script:Manifest.RunId, [StringComparison]::Ordinal)) 'Window helper run binding' $ready.runId
    Assert-Check ([string]::Equals([string]$ready.nonce, [string]$script:Manifest.Nonce, [StringComparison]::Ordinal)) 'Window helper nonce binding' $ready.runId
    $processIdentity = Get-ExactProcessIdentity -ProcessId $process.Id
    if ($null -eq $processIdentity) {
        $script:UnmanagedChildStillRunning = $true
        $processCreationTime = $null
        try { $processCreationTime = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
        $script:OutstandingProcesses.Add([pscustomobject]@{
            Type = 'WindowHelperUnidentified'
            ProcessId = [int]$process.Id
            CreationTime = $processCreationTime
            ExecutablePath = $Executable
            CommandLine = "$Executable $($startInfo.Arguments)"
            OwnerSid = $script:ValidationIdentity.User.Value
        })
        throw "Window helper PID $($process.Id) could not be identified and was deliberately not terminated."
    }
    $script:HelperProcessCreationTime = $processIdentity.CreationDate
    $script:HelperExecutablePath = $processIdentity.ExecutablePath
    $script:HelperCommandLine = $processIdentity.CommandLine
    $script:HelperOwnerSid = $processIdentity.OwnerSid
    $script:HelperSessionId = $processIdentity.SessionId
    Assert-Check ($null -ne $processIdentity) 'Window helper process identity' "PID=$($process.Id)"
    Assert-Check ([string]::Equals($processIdentity.Name, 'SwSearch.App.exe', [StringComparison]::OrdinalIgnoreCase)) 'Window helper exact image name' $processIdentity.Name
    Assert-Check ([string]::Equals([IO.Path]::GetFullPath($processIdentity.ExecutablePath), [IO.Path]::GetFullPath($Executable), [StringComparison]::OrdinalIgnoreCase)) 'Window helper exact executable path' $processIdentity.ExecutablePath
    Assert-Check ([string]::Equals($processIdentity.OwnerSid, $script:ValidationIdentity.User.Value, [StringComparison]::OrdinalIgnoreCase)) 'Window helper owner SID' $processIdentity.OwnerSid

    $script:HelperProcessId = $process.Id
    $script:HelperProcessCreationTime = $processIdentity.CreationDate
    $script:HelperExecutablePath = $processIdentity.ExecutablePath
    $script:HelperMode = $Mode
    $script:AllowedProcessIds[$process.Id] = $true
    return [pscustomobject]@{
        Process = $process
        Identity = $processIdentity
        Ready = $ready
        ReadyPath = $ReadyPath
        EventPath = $EventPath
    }
}

function Stop-TestWindowHelperIfSafe {
    if ($null -eq $script:HelperProcessId) { return }
    $processIdentity = Get-ExactProcessIdentity -ProcessId $script:HelperProcessId
    if ($null -eq $processIdentity) { return }
    if ($script:TransactionStillRunning) {
        throw "MANUAL_CLEANUP: MSI is still running; helper PID $($script:HelperProcessId) was deliberately left untouched."
    }
    $sameIdentity =
        [string]::Equals($processIdentity.Name, 'SwSearch.App.exe', [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([IO.Path]::GetFullPath($processIdentity.ExecutablePath), [IO.Path]::GetFullPath($script:HelperExecutablePath), [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($processIdentity.CreationDate, $script:HelperProcessCreationTime, [StringComparison]::Ordinal) -and
        [string]::Equals($processIdentity.CommandLine, $script:HelperCommandLine, [StringComparison]::Ordinal) -and
        [string]::Equals($processIdentity.OwnerSid, $script:ValidationIdentity.User.Value, [StringComparison]::OrdinalIgnoreCase)
    if (-not $sameIdentity) {
        throw "MANUAL_CLEANUP: PID reuse/identity mismatch prevented helper cleanup for PID $($script:HelperProcessId)."
    }
    Stop-Process -Id $script:HelperProcessId -Force -ErrorAction Stop
    try { Wait-Process -Id $script:HelperProcessId -Timeout 10 -ErrorAction SilentlyContinue }
    catch { }
    Assert-Check ($null -eq (Get-ExactProcessIdentity -ProcessId $script:HelperProcessId)) 'Exact helper process cleanup' "PID=$($script:HelperProcessId)"
}

function Assert-NoInstallerActivity {
    param([switch]$AllowServiceHost)

    $inProgressPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress'
    Assert-Check (-not (Test-Path -LiteralPath $inProgressPath)) 'Windows Installer transaction is idle' $inProgressPath

    $processes = @(Get-MsiProcesses)
    if (-not $AllowServiceHost) {
        $detail = if ($processes.Count -eq 0) { 'No msiexec.exe process exists.' } else { ($processes | ConvertTo-Json -Compress) }
        Assert-Check ($processes.Count -eq 0) 'No competing msiexec process' $detail
        return
    }

    $clients = @($processes | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
        $_.CommandLine -match '(?i)(?:^|\s)/(?:i|x|f|p|update|package)(?:\s|$)'
    })
    $clientDetail = if ($clients.Count -eq 0) { 'Only an idle Windows Installer service host may remain.' } else { ($clients | ConvertTo-Json -Compress) }
    Assert-Check ($clients.Count -eq 0) 'No competing MSI client' $clientDetail

    $nonServiceHosts = @($processes | Where-Object {
        $_.SessionId -ne 0 -or
        -not [string]::Equals([string]$_.ParentName, 'services.exe', [StringComparison]::OrdinalIgnoreCase)
    })
    $serviceDetail = if ($nonServiceHosts.Count -eq 0) {
        "Idle service hosts=$($processes.Count); all are Session 0 children of services.exe."
    }
    else { $nonServiceHosts | ConvertTo-Json -Compress }
    Assert-Check ($nonServiceHosts.Count -eq 0) 'Remaining msiexec processes are service hosts' $serviceDetail
}

function Get-ProcessSnapshot {
    $names = @('SwSearch.App', 'SwSearch.PropertyViewer', 'SLDWORKS')
    $rows = @()
    foreach ($name in $names) {
        $rows += @(Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, StartTime)
    }
    return @($rows)
}

function Observe-ForbiddenProcesses {
    param([object[]]$Baseline)

    $baselineKeys = @{}
    foreach ($item in $Baseline) {
        $baselineKeys["$($item.ProcessName):$($item.Id)"] = $true
    }
    foreach ($item in @(Get-ProcessSnapshot)) {
        $key = "$($item.ProcessName):$($item.Id)"
        if (-not $baselineKeys.ContainsKey($key) -and -not $script:AllowedProcessIds.ContainsKey([int]$item.Id)) {
            if (-not ($script:ObservedForbiddenProcesses | Where-Object { $_.Key -eq $key })) {
                $script:ObservedForbiddenProcesses.Add([pscustomobject]@{
                    Key = $key
                    ProcessName = $item.ProcessName
                    Id = $item.Id
                    StartTime = if ($item.StartTime) { $item.StartTime.ToString('o') } else { $null }
                    ObservedAt = [DateTimeOffset]::Now.ToString('o')
                })
            }
        }
    }
}

function Invoke-MsiTransaction {
    param(
        [ValidateSet('Install', 'Uninstall')]
        [string]$Operation,
        [string]$Target,
        [string]$LogPath,
        [object[]]$ProcessBaseline,
        [int]$TimeoutSeconds = 300,
        [int[]]$ExpectedExitCodes = @(0),
        [int]$MaximumElapsedSeconds = 0
    )

    if ($Operation -eq 'Install') {
        $script:AnyInstallAttempted = $true
    }
    $arguments = if ($Operation -eq 'Install') {
        "/i `"$Target`" /qn /norestart REBOOT=ReallySuppress ALLUSERS=2 MSIINSTALLPERUSER=1 INSTALLLEVEL=1 /L*V! `"$LogPath`""
    }
    else {
        "/x `"$Target`" /qn /norestart REBOOT=ReallySuppress /L*V! `"$LogPath`""
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $startInfo.Arguments = $arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "Unable to start $Operation transaction." }

    $deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    while (-not $process.WaitForExit(100)) {
        Observe-ForbiddenProcesses -Baseline $ProcessBaseline
        if ([DateTimeOffset]::Now -gt $deadline) {
            $script:TransactionStillRunning = $true
            $processCreationTime = $null
            try { $processCreationTime = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
            $script:OutstandingProcesses.Add([pscustomobject]@{
                Type = 'msiexec'
                ProcessId = [int]$process.Id
                CreationTime = $processCreationTime
                ExecutablePath = $startInfo.FileName
                CommandLine = "$($startInfo.FileName) $arguments"
                OwnerSid = $script:ValidationIdentity.User.Value
            })
            Add-Check "$Operation completed within timeout" $false "PID $($process.Id) is still running. It was deliberately not terminated."
            throw "$Operation MSI transaction exceeded $TimeoutSeconds seconds; the process was not killed."
        }
    }
    Observe-ForbiddenProcesses -Baseline $ProcessBaseline
    $process.WaitForExit()
    $stopwatch.Stop()
    $exitAccepted = $ExpectedExitCodes -contains [int]$process.ExitCode
    Add-Check "$Operation exit code" $exitAccepted "ExitCode=$($process.ExitCode); Expected=$($ExpectedExitCodes -join ','); PID=$($process.Id); ElapsedMs=$($stopwatch.ElapsedMilliseconds); Log=$LogPath"
    if (-not $exitAccepted) {
        throw "$Operation failed with Windows Installer exit code $($process.ExitCode)."
    }
    if ($MaximumElapsedSeconds -gt 0) {
        Assert-Check ($stopwatch.Elapsed.TotalSeconds -le $MaximumElapsedSeconds) "$Operation bounded duration" "ElapsedSeconds=$([Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)); Maximum=$MaximumElapsedSeconds"
    }
    return [pscustomobject]@{
        ExitCode = [int]$process.ExitCode
        ProcessId = [int]$process.Id
        ElapsedMilliseconds = [long]$stopwatch.ElapsedMilliseconds
        LogPath = $LogPath
    }
}

function Invoke-ConsoleProbe {
    param(
        [string]$Executable,
        [string]$Arguments,
        [string]$Label,
        [int]$TimeoutSeconds = 20
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Executable
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "Unable to start $Label." }
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $script:UnmanagedChildStillRunning = $true
        $processCreationTime = $null
        try { $processCreationTime = $process.StartTime.ToUniversalTime().ToString('o') } catch { }
        $script:OutstandingProcesses.Add([pscustomobject]@{
            Type = 'CLIProbe'
            ProcessId = [int]$process.Id
            CreationTime = $processCreationTime
            ExecutablePath = $Executable
            CommandLine = "$Executable $Arguments"
            OwnerSid = $script:ValidationIdentity.User.Value
        })
        Add-Check "$Label completed within timeout" $false "PID $($process.Id) is still running. It was deliberately not terminated."
        throw "$Label exceeded $TimeoutSeconds seconds; the child process was not killed."
    }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    Assert-Check ($process.ExitCode -eq 0) "$Label exit code" "ExitCode=$($process.ExitCode); stderr=$stderr"
    return [pscustomobject]@{ StdOut = $stdout; StdErr = $stderr; ExitCode = $process.ExitCode }
}

function Get-RegistryDefaultValue {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Item -LiteralPath $Path).GetValue('', $null, 'DoNotExpandEnvironmentNames')
}

function Get-UninstallEntries {
    param([string]$Code)
    $normalized = Normalize-GuidText $Code
    $paths = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$normalized",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$normalized",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$normalized"
    )
    return @($paths | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        $values = Get-ItemProperty -LiteralPath $_
        [pscustomobject]@{
            Path = $_
            DisplayName = $values.DisplayName
            DisplayVersion = $values.DisplayVersion
            UninstallString = $values.UninstallString
        }
    })
}

function Assert-InstalledPayload {
    param([string]$InstallDirectory)

    foreach ($name in @('SwSearch.App.exe', 'SwSearch.Indexer.exe', 'SwSearch.Metadata.Worker.exe', 'SwSearch.PropertyViewer.exe', 'SHA256SUMS.txt')) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $InstallDirectory $name) -PathType Leaf) "Installed file $name" $InstallDirectory
    }

    $manifestPath = Join-Path $InstallDirectory 'SHA256SUMS.txt'
    $manifestLines = @(Get-Content -LiteralPath $manifestPath -Encoding UTF8 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Assert-Check ($manifestLines.Count -eq $ExpectedManifestEntryCount) 'Installed payload manifest entry count' "Entries=$($manifestLines.Count); Expected=$ExpectedManifestEntryCount"
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    Assert-Check ($manifestHash -eq $ExpectedManifestSha256.ToUpperInvariant()) 'Installed payload manifest SHA256' $manifestHash
    $installPrefix = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\') + '\'
    foreach ($line in $manifestLines) {
        if ($line -notmatch '^([A-Fa-f0-9]{64}) \*(.+)$') {
            throw "Malformed SHA256SUMS line: $line"
        }
        $expectedHash = $matches[1].ToUpperInvariant()
        $relative = $matches[2].Replace('/', '\')
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('\') -contains '..') {
            throw "Unsafe manifest path: $relative"
        }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $InstallDirectory $relative))
        if (-not $fullPath.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Manifest path escaped install directory: $relative"
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Manifest file is missing: $relative"
        }
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "Installed file hash differs: $relative"
        }
    }
    $installedFiles = @(Get-ChildItem -LiteralPath $InstallDirectory -File -Force -Recurse)
    Assert-Check ($installedFiles.Count -eq $ExpectedPayloadFileCount) 'Installed payload file count' "Files=$($installedFiles.Count); Expected=$ExpectedPayloadFileCount"
    Add-Check 'Installed payload hashes' $true "Verified $($manifestLines.Count) manifest entries plus the manifest itself ($ExpectedPayloadFileCount files total)."
}

function Assert-ContextMenuRegistration {
    param([string]$InstallDirectory)

    $viewer = Join-Path $InstallDirectory 'SwSearch.PropertyViewer.exe'
    foreach ($extension in @('.sldprt', '.sldasm', '.slddrw')) {
        $root = "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\SwSearch.ViewProperties"
        $commandPath = Join-Path $root 'command'
        Assert-Check (Test-Path -LiteralPath $root) "Context menu $extension" $root
        $command = [string](Get-RegistryDefaultValue $commandPath)
        $expected = "`"$viewer`" `"%1`""
        Assert-Check ($command -eq $expected) "Context command $extension" $command
    }
}

function Assert-NoInstalledResidue {
    param(
        [string]$InstallDirectory,
        [string[]]$ProductCodes,
        [bool]$DataDirectoryExistedBefore
    )

    $remainingFiles = if (Test-Path -LiteralPath $InstallDirectory) {
        @(Get-ChildItem -LiteralPath $InstallDirectory -Force -Recurse -ErrorAction SilentlyContinue)
    }
    else { @() }
    Assert-Check ($remainingFiles.Count -eq 0) 'Install directory residue' "Remaining entries=$($remainingFiles.Count); Path=$InstallDirectory"

    foreach ($extension in @('.sldprt', '.sldasm', '.slddrw')) {
        $root = "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\SwSearch.ViewProperties"
        Assert-Check (-not (Test-Path -LiteralPath $root)) "Removed context menu $extension" $root
    }
    Assert-Check (-not (Test-Path -LiteralPath 'HKCU:\Software\SwSearch\Installer')) 'Removed installer marker' 'HKCU\Software\SwSearch\Installer'

    foreach ($code in $ProductCodes) {
        Assert-Check ((Get-ProductState $code) -eq -1) "Removed product state $code" "State=$(Get-ProductState $code)"
        Assert-Check ((Get-UninstallEntries $code).Count -eq 0) "Removed uninstall registration $code" 'No ARP entry remains.'
    }

    $dataDirectory = Join-Path $env:LOCALAPPDATA 'SwSearch'
    if (-not $DataDirectoryExistedBefore) {
        Assert-Check (-not (Test-Path -LiteralPath $dataDirectory)) 'No cache created by validation probes' $dataDirectory
    }
}

function Assert-CurrentInstallation {
    Assert-Check ((Get-ProductState $ProductCode) -eq 5) 'Current product registration' "State=$(Get-ProductState $ProductCode)"
    Assert-Check ((Get-ProductInfo $ProductCode 'VersionString') -eq $ExpectedVersion) 'Registered product version' (Get-ProductInfo $ProductCode 'VersionString')
    if ($isUpgradeScenario) {
        Assert-Check ((Get-ProductState $PreviousProductCode) -eq -1) 'Previous ProductCode removed by major upgrade' "State=$(Get-ProductState $PreviousProductCode)"
    }

    $uninstallEntries = @(Get-UninstallEntries $ProductCode)
    Assert-Check ($uninstallEntries.Count -ge 1) 'Add/Remove Programs registration' ($uninstallEntries | ConvertTo-Json -Compress)
    Assert-Check (@($uninstallEntries | Where-Object { $_.DisplayVersion -eq $ExpectedVersion }).Count -ge 1) 'ARP display version' $ExpectedVersion
    Assert-InstalledPayload -InstallDirectory $installDirectory
    Assert-ContextMenuRegistration -InstallDirectory $installDirectory
    Assert-Check (Test-Path -LiteralPath $startMenuDirectory -PathType Container) 'Start menu folder installed' $startMenuDirectory
    Assert-Check (-not (Test-Path -LiteralPath $desktopShortcut)) 'Optional desktop shortcut not installed at level 1' $desktopShortcut
    Assert-Check ($script:ObservedForbiddenProcesses.Count -eq 0) 'MSI launched no unexpected App, PropertyViewer, or SOLIDWORKS process' ($script:ObservedForbiddenProcesses | ConvertTo-Json -Compress)

    $indexerResult = Invoke-ConsoleProbe -Executable (Join-Path $installDirectory 'SwSearch.Indexer.exe') -Arguments '--help' -Label 'Indexer --help'
    Assert-Check ($indexerResult.StdOut -match [regex]::Escape("SwSearch.Indexer $ExpectedVersion")) 'Indexer version output' $indexerResult.StdOut.Trim()

    $workerResult = Invoke-ConsoleProbe -Executable (Join-Path $installDirectory 'SwSearch.Metadata.Worker.exe') -Arguments '--probe' -Label 'Metadata Worker --probe'
    $capability = $workerResult.StdOut | ConvertFrom-Json
    Assert-Check ($capability.code -eq 'NOAPI_AVAILABLE') 'Built-in metadata capability' $capability.code
    Assert-Check ($capability.details.externalRuntime -eq 'none') 'No external runtime required' $capability.details.externalRuntime
    Assert-Check ($capability.details.solidWorksApplication -eq 'not-started') 'SOLIDWORKS was not started by probe' $capability.details.solidWorksApplication
    Assert-Check (@(Get-Process -Name 'SwSearch.Indexer','SwSearch.Metadata.Worker' -ErrorAction SilentlyContinue).Count -eq 0) 'CLI probes exited cleanly' 'No Indexer or Worker process remains.'
    Observe-ForbiddenProcesses -Baseline $baselineProcesses
    Assert-Check ($script:ObservedForbiddenProcesses.Count -eq 0) 'No unexpected forbidden process observed during probes' ($script:ObservedForbiddenProcesses | ConvertTo-Json -Compress)
}

function Assert-ExactJsonProperties {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($null -eq $Object) { throw "$Label is null." }
    $actual = @($Object.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if (($actual -join "`n") -ne ($wanted -join "`n")) {
        throw "$Label property schema mismatch. Expected=$($wanted -join ','); Actual=$($actual -join ',')."
    }
}

function Assert-JsonString {
    param($Value, [string]$Label, [string]$Pattern = '^.+$')
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Value) -or [string]$Value -notmatch $Pattern) {
        throw "$Label must be a non-empty string matching $Pattern."
    }
}

function Assert-FileSha256Binding {
    param([string]$Path, [string]$ExpectedSha256, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label file is missing: $Path" }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actual, $ExpectedSha256, [StringComparison]::Ordinal)) { throw "$Label SHA256 mismatch; expected=$ExpectedSha256 actual=$actual." }
    return $true
}

function Get-ResolvedValidationIdentity {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $current.User) { throw 'The current Windows token has no user SID.' }
    $sid = [string]$current.User.Value
    $ntAccount = [string]$current.User.Translate([Security.Principal.NTAccount]).Value
    $parts = $ntAccount.Split('\', 2)
    if ($parts.Count -ne 2) { throw "Unable to resolve a SAM name from token identity $ntAccount." }
    if (-not [string]::Equals($parts[0], $env:COMPUTERNAME, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Validation token is not a local machine account: $ntAccount."
    }
    $localUser = Get-LocalUser -SID $current.User -ErrorAction Stop
    if ($null -eq $localUser -or -not [string]::Equals([string]$localUser.Sid.Value, $sid, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Get-LocalUser could not bind the token SID $sid to exactly one local account."
    }
    if (-not [string]::Equals([string]$localUser.Name, $parts[1], [StringComparison]::OrdinalIgnoreCase)) {
        throw "SID translation and Get-LocalUser disagree: $ntAccount versus $($localUser.Name)."
    }
    $profiles = @(Get-CimInstance Win32_UserProfile -Filter "SID='$sid'" -ErrorAction Stop)
    if ($profiles.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$profiles[0].LocalPath)) {
        throw "The token SID $sid does not have exactly one materialized Win32_UserProfile."
    }
    return [pscustomobject]@{
        Identity = $current
        Sid = $sid
        NtAccount = $ntAccount
        SamAccountName = [string]$localUser.Name
        ProfilePath = [IO.Path]::GetFullPath([string]$profiles[0].LocalPath).TrimEnd('\')
    }
}

function Get-WriteRightsMask {
    return [Security.AccessControl.FileSystemRights]('Write,Modify,FullControl,Delete,ChangePermissions,TakeOwnership,CreateFiles,CreateDirectories,WriteData,AppendData,WriteAttributes,WriteExtendedAttributes,DeleteSubdirectoriesAndFiles')
}

function Test-AclReadOnlyForSid {
    param([string]$Path, [string]$Sid)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $sidObject = New-Object Security.Principal.SecurityIdentifier($Sid)
    $token = [Security.Principal.WindowsIdentity]::GetCurrent()
    $tokenSids = @($sidObject.Value) + @($token.Groups | ForEach-Object { $_.Value })
    $writeMask = Get-WriteRightsMask
    foreach ($rule in $acl.Access) {
        $ruleSid = try { $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { [string]$rule.IdentityReference }
        if ($tokenSids -contains $ruleSid -and $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and (($rule.FileSystemRights -band $writeMask) -ne 0)) {
            return $false
        }
    }
    $ownerSid = try { $acl.Owner | ForEach-Object { (New-Object Security.Principal.NTAccount($_)).Translate([Security.Principal.SecurityIdentifier]).Value } } catch { [string]$acl.Owner }
    return $ownerSid -in @('S-1-5-18', 'S-1-5-32-544', $script:Manifest.ProtectedUserSid)
}

function Test-ResultAclSingleValidationWriter {
    param([string]$Path, [string]$ValidationSid)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $writeMask = Get-WriteRightsMask
    $unprivilegedWriters = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or (($rule.FileSystemRights -band $writeMask) -eq 0)) { continue }
        $sid = try { $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { [string]$rule.IdentityReference }
        if ($sid -notin @('S-1-5-18', 'S-1-5-32-544', $script:Manifest.ProtectedUserSid)) { $null = $unprivilegedWriters.Add($sid) }
    }
    return ($unprivilegedWriters.Count -eq 1 -and $unprivilegedWriters.Contains($ValidationSid))
}

function Read-AndValidateRunManifest {
    param([string]$Path, [string]$PresentedNonce)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Execute requires a wrapper-generated run manifest.' }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $raw = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8 -ErrorAction Stop
    $manifest = $raw | ConvertFrom-Json -ErrorAction Stop
    Assert-ExactJsonProperties $manifest @('SchemaVersion','RunId','Nonce','ScenarioId','Scenario','ExpectedUserSid','ExpectedSamAccountName','ExpectedProfilePath','ProtectedUserSid','Staged','ResultRoot','RequiredChecks','CreatedAtUtc') 'Run manifest'
    if ($manifest.SchemaVersion -isnot [int] -or [int]$manifest.SchemaVersion -ne 2) { throw 'Run manifest SchemaVersion must be integer 2.' }
    foreach ($property in @('RunId','Nonce','ScenarioId','Scenario','ExpectedUserSid','ExpectedSamAccountName','ExpectedProfilePath','ProtectedUserSid','ResultRoot','CreatedAtUtc')) {
        Assert-JsonString $manifest.$property "Manifest.$property"
    }
    Assert-JsonString $manifest.RunId 'Manifest.RunId' '^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$'
    Assert-JsonString $manifest.Nonce 'Manifest.Nonce' '^[A-F0-9]{64}$'
    if (-not [string]::Equals([string]$manifest.Nonce, $PresentedNonce, [StringComparison]::Ordinal)) { throw 'Presented nonce does not match the protected run manifest.' }
    if ([string]::Equals([string]$manifest.ExpectedUserSid, 'S-1-5-21-6737918-3018883656-3777255787-1001', [StringComparison]::OrdinalIgnoreCase)) { throw 'Protected liliy SID is permanently forbidden as a validation identity.' }
    if (-not [string]::Equals([string]$manifest.ProtectedUserSid, 'S-1-5-21-6737918-3018883656-3777255787-1001', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest protected SID is not the frozen liliy SID.' }
    if ([string]$manifest.ExpectedSamAccountName -notmatch '^SwMsiTest_[A-F0-9]{8}$') { throw 'Manifest SAM account name is outside the dedicated validation namespace.' }
    if ([string]$manifest.Scenario -notin @('Clean','Upgrade','CloseResponding','CloseIgnoring')) { throw 'Manifest scenario is invalid.' }
    if ($manifest.RequiredChecks -isnot [object[]] -or @($manifest.RequiredChecks).Count -lt 2) { throw 'Manifest RequiredChecks must be a non-empty JSON array.' }
    foreach ($item in @($manifest.RequiredChecks)) { Assert-JsonString $item 'Manifest.RequiredChecks item' }
    if (@($manifest.RequiredChecks | Sort-Object -Unique).Count -ne @($manifest.RequiredChecks).Count) { throw 'Manifest RequiredChecks contains duplicates.' }
    Assert-ExactJsonProperties $manifest.Staged @('Lifecycle','CurrentMsi','PreviousMsi','Helper') 'Manifest.Staged'
    foreach ($leafName in @('Lifecycle','CurrentMsi','Helper')) {
        $leaf = $manifest.Staged.$leafName
        $expectedProperties = if ($leafName -eq 'CurrentMsi') { @('Path','Sha256','Version','ProductCode','UpgradeCode') } else { @('Path','Sha256') }
        Assert-ExactJsonProperties $leaf $expectedProperties "Manifest.Staged.$leafName"
        foreach ($property in $expectedProperties) { Assert-JsonString $leaf.$property "Manifest.Staged.$leafName.$property" }
        Assert-JsonString $leaf.Sha256 "Manifest.Staged.$leafName.Sha256" '^[A-F0-9]{64}$'
    }
    if ($null -ne $manifest.Staged.PreviousMsi) {
        Assert-ExactJsonProperties $manifest.Staged.PreviousMsi @('Path','Sha256','Version','ProductCode','UpgradeCode') 'Manifest.Staged.PreviousMsi'
        foreach ($property in @('Path','Sha256','Version','ProductCode','UpgradeCode')) { Assert-JsonString $manifest.Staged.PreviousMsi.$property "Manifest.Staged.PreviousMsi.$property" }
    }
    $manifestDirectory = Split-Path -Parent $fullPath
    $stagingRoot = Split-Path -Parent $manifestDirectory
    $expectedScript = [IO.Path]::GetFullPath([string]$manifest.Staged.Lifecycle.Path)
    if (-not $expectedScript.StartsWith(([IO.Path]::GetFullPath($stagingRoot).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest lifecycle path escaped staging.' }
    if (-not [string]::Equals([IO.Path]::GetFullPath($PSCommandPath), $expectedScript, [StringComparison]::OrdinalIgnoreCase)) { throw 'Direct source-path -Execute is forbidden; only the staged lifecycle script may execute.' }
    $null = Assert-FileSha256Binding -Path $expectedScript -ExpectedSha256 ([string]$manifest.Staged.Lifecycle.Sha256) -Label 'Staged lifecycle script'
    if (-not (Test-AclReadOnlyForSid -Path $fullPath -Sid ([string]$manifest.ExpectedUserSid))) { throw 'Manifest ACL is writable by the validation token or is not admin-owned.' }
    $manifest | Add-Member -NotePropertyName ManifestPath -NotePropertyValue $fullPath
    $manifest | Add-Member -NotePropertyName ManifestSha256 -NotePropertyValue ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash)
    return $manifest
}

function Write-AtomicUtf8Json {
    param([string]$Path, $Value)
    $temporary = "$Path.tmp-$([Guid]::NewGuid().ToString('N'))"
    $bytes = New-Object Text.UTF8Encoding($true)
    $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try {
        $writer = New-Object IO.StreamWriter($stream, $bytes)
        try { $writer.Write(($Value | ConvertTo-Json -Depth 20)); $writer.Flush(); $stream.Flush($true) }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

if ($LibraryOnly) { return }

if ($Execute) {
    $script:Manifest = Read-AndValidateRunManifest -Path $ManifestPath -PresentedNonce $Nonce
    $runtime = Get-ResolvedValidationIdentity
    $Scenario = [string]$script:Manifest.Scenario
    $ExpectedAccountName = [string]$script:Manifest.ExpectedSamAccountName
    $ExpectedUserSid = [string]$script:Manifest.ExpectedUserSid
    $ProtectedUserSid = [string]$script:Manifest.ProtectedUserSid
    $ResultRoot = [string]$script:Manifest.ResultRoot
    $MsiPath = [string]$script:Manifest.Staged.CurrentMsi.Path
    $ExpectedVersion = [string]$script:Manifest.Staged.CurrentMsi.Version
    $ExpectedMsiSha256 = [string]$script:Manifest.Staged.CurrentMsi.Sha256
    $ProductCode = [string]$script:Manifest.Staged.CurrentMsi.ProductCode
    $UpgradeCode = [string]$script:Manifest.Staged.CurrentMsi.UpgradeCode
    if ($null -ne $script:Manifest.Staged.PreviousMsi) {
        $PreviousMsiPath = [string]$script:Manifest.Staged.PreviousMsi.Path
        $PreviousVersion = [string]$script:Manifest.Staged.PreviousMsi.Version
        $PreviousMsiSha256 = [string]$script:Manifest.Staged.PreviousMsi.Sha256
        $PreviousProductCode = [string]$script:Manifest.Staged.PreviousMsi.ProductCode
    }
    $HelperPath = [string]$script:Manifest.Staged.Helper.Path
    $ExpectedHelperSha256 = [string]$script:Manifest.Staged.Helper.Sha256
}
elseif ([string]::IsNullOrWhiteSpace($MsiPath) -or [string]::IsNullOrWhiteSpace($ResultRoot)) {
    Write-Output 'DRY RUN ONLY: Invoke-MsiLifecycleValidation requires a wrapper-generated manifest for -Execute.'
    return
}

$MsiPath = [IO.Path]::GetFullPath($MsiPath)
$ResultRoot = [IO.Path]::GetFullPath($ResultRoot)
$isUpgradeScenario = $Scenario -in @('Upgrade', 'CloseResponding', 'CloseIgnoring')
$isWindowScenario = $Scenario -in @('CloseResponding', 'CloseIgnoring')
if ($isUpgradeScenario) {
    if ([string]::IsNullOrWhiteSpace($PreviousMsiPath)) {
        throw 'PreviousMsiPath is required for upgrade and close-behavior scenarios.'
    }
    $PreviousMsiPath = [IO.Path]::GetFullPath($PreviousMsiPath)
}
if ($isWindowScenario) {
    if ([string]::IsNullOrWhiteSpace($HelperPath) -or [string]::IsNullOrWhiteSpace($ExpectedHelperSha256)) {
        throw 'HelperPath and ExpectedHelperSha256 are required for close-behavior scenarios.'
    }
    $HelperPath = [IO.Path]::GetFullPath($HelperPath)
}

$ProductCode = Normalize-GuidText $ProductCode
$UpgradeCode = Normalize-GuidText $UpgradeCode
$PreviousProductCode = Normalize-GuidText $PreviousProductCode
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\SwSearch'
$dataDirectoryExistedBefore = Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'SwSearch')
$productDisplayName = 'SOLIDWORKS ' + [string]([char[]]@(0x6587, 0x4EF6, 0x68C0, 0x7D22))
$startMenuDirectory = Join-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') $productDisplayName
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) ($productDisplayName + '.lnk')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$script:ValidationIdentity = $identity
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$baselineProcesses = @(Get-ProcessSnapshot)
$baselineRelatedProducts = @(Get-RelatedProducts $UpgradeCode)
$baselineCurrentProductState = Get-ProductState $ProductCode
$baselinePreviousProductState = Get-ProductState $PreviousProductCode

Write-Output "Scenario: $Scenario"
Write-Output "Account: $($identity.Name) ($($identity.User.Value))"
Write-Output "Install directory: $installDirectory"
Write-Output "Result directory: $ResultRoot"

if (-not $Execute) {
    Write-Output 'DRY RUN ONLY: no msiexec command was started.'
    Write-Output 'Pass -Execute only from the exact dedicated standard local account and SID supplied in ExpectedAccountName/ExpectedUserSid.'
    return
}

$primaryError = $null
$cleanupError = $null
try {
    Assert-Check ($ExpectedAccountName -like 'SwMsiTest_*') 'Authorized account name pattern' $ExpectedAccountName
    Assert-Check ([string]::Equals([string]$script:Manifest.Nonce, $Nonce, [StringComparison]::Ordinal)) 'Manifest nonce binding' $script:Manifest.RunId
    Assert-Check ([string]::Equals($runtime.Sid, $ExpectedUserSid, [StringComparison]::OrdinalIgnoreCase)) 'Runtime SID binding' $runtime.Sid
    Assert-Check ([string]::Equals($runtime.SamAccountName, $ExpectedAccountName, [StringComparison]::OrdinalIgnoreCase)) 'Runtime SAM binding' $runtime.NtAccount
    Assert-Check ([string]::Equals($runtime.ProfilePath, ([IO.Path]::GetFullPath([string]$script:Manifest.ExpectedProfilePath).TrimEnd('\')), [StringComparison]::OrdinalIgnoreCase)) 'Runtime profile binding' $runtime.ProfilePath
    Assert-Check (-not [string]::Equals($runtime.Sid, 'S-1-5-21-6737918-3018883656-3777255787-1001', [StringComparison]::OrdinalIgnoreCase)) 'Protected SID isolation' "Validation=$($runtime.Sid); Protected=$ProtectedUserSid"
    Assert-Check ([string]::Equals((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash, [string]$script:Manifest.Staged.Lifecycle.Sha256, [StringComparison]::Ordinal)) 'Staged lifecycle SHA256' $script:Manifest.Staged.Lifecycle.Sha256
    Assert-Check (Test-AclReadOnlyForSid -Path $script:Manifest.ManifestPath -Sid $runtime.Sid) 'Manifest ACL read only' $script:Manifest.ManifestPath
    Assert-Check (Test-ResultAclSingleValidationWriter -Path $ResultRoot -ValidationSid $runtime.Sid) 'Result ACL single writer' $ResultRoot
    Assert-Check ([string]::Equals((Get-FileHash -LiteralPath $HelperPath -Algorithm SHA256).Hash, [string]$script:Manifest.Staged.Helper.Sha256, [StringComparison]::Ordinal)) 'Staged helper SHA256' $script:Manifest.Staged.Helper.Sha256
    Assert-Check (-not $isAdministrator) 'Standard-user validation token' "Administrator=$isAdministrator"
    Assert-NoInstallerActivity -AllowServiceHost
    Assert-Check ($baselineRelatedProducts.Count -eq 0) 'No related product visible in validation account' ($baselineRelatedProducts -join ',')
    Assert-Check (-not (Test-Path -LiteralPath $installDirectory)) 'Clean install directory baseline' $installDirectory
    Assert-Check (-not (Test-Path -LiteralPath 'HKCU:\Software\SwSearch\Installer')) 'Clean installer registry baseline' 'HKCU\Software\SwSearch\Installer'
    foreach ($extension in @('.sldprt', '.sldasm', '.slddrw')) {
        $root = "HKCU:\Software\Classes\SystemFileAssociations\$extension\shell\SwSearch.ViewProperties"
        Assert-Check (-not (Test-Path -LiteralPath $root)) "Clean context baseline $extension" $root
    }

    Assert-MsiIdentity -Path $MsiPath -Version $ExpectedVersion -Code $ProductCode -SharedUpgradeCode $UpgradeCode -Sha256 $ExpectedMsiSha256 -Label 'Current MSI'
    Add-Check 'Current MSI identity' $true "$ExpectedVersion $ProductCode $ExpectedMsiSha256"
    if ($isUpgradeScenario) {
        Assert-MsiIdentity -Path $PreviousMsiPath -Version $PreviousVersion -Code $PreviousProductCode -SharedUpgradeCode $UpgradeCode -Sha256 $PreviousMsiSha256 -Label 'Previous MSI'
    }

    New-Item -ItemType Directory -Force -Path $ResultRoot | Out-Null

    if ($isUpgradeScenario) {
        $previousLog = Join-Path $ResultRoot '01-previous-install.verbose.log'
        Invoke-MsiTransaction -Operation Install -Target $PreviousMsiPath -LogPath $previousLog -ProcessBaseline $baselineProcesses
        $script:PreviousInstallCompleted = $true
        Assert-Check ((Get-ProductState $PreviousProductCode) -eq 5) 'Previous version installed for upgrade test' "State=$(Get-ProductState $PreviousProductCode)"
        Assert-NoInstallerActivity -AllowServiceHost
    }

    $installLog = Join-Path $ResultRoot (if ($isUpgradeScenario) { '02-current-upgrade.verbose.log' } else { '01-current-install.verbose.log' })

    if (-not $isWindowScenario) {
        Invoke-MsiTransaction -Operation Install -Target $MsiPath -LogPath $installLog -ProcessBaseline $baselineProcesses | Out-Null
        $script:InstallCompleted = $true
        if ($Scenario -eq 'Upgrade') { Assert-SuccessfulUpgradeLog -LogPath $installLog }
        Assert-CurrentInstallation
    }
    else {
        $oldSnapshot = Get-CompleteInstallationStateSnapshot -InstallDirectory $installDirectory
        Assert-Check $oldSnapshot.State.InstallDirectory.Exists 'Previous payload snapshot exists' $installDirectory
        Write-AtomicUtf8Json -Path (Join-Path $ResultRoot 'old-state-before.json') -Value $oldSnapshot.State
        $helperMode = if ($Scenario -eq 'CloseResponding') { 'respond' } else { 'ignore' }
        $readyPath = Join-Path $ResultRoot "helper-$helperMode-ready.json"
        $eventPath = Join-Path $ResultRoot "helper-$helperMode-events.log"
        $helper = Start-TestWindowHelper -Mode $helperMode -Executable $HelperPath -ReadyPath $readyPath -EventPath $eventPath

        $sameSession = ([int]$helper.Identity.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId)
        if (-not $sameSession) {
            $script:ScenarioSkipped = $true
            $script:SkipReason = "Helper session $($helper.Identity.SessionId) differs from lifecycle session $([Diagnostics.Process]::GetCurrentProcess().SessionId); WM_CLOSE coverage is not trustworthy."
            Add-Check 'CloseApplication session topology' $true "SKIP: $($script:SkipReason)"
        }
        elseif ($Scenario -eq 'CloseResponding') {
            Invoke-MsiTransaction -Operation Install -Target $MsiPath -LogPath $installLog -ProcessBaseline $baselineProcesses | Out-Null
            $script:InstallCompleted = $true
            Assert-SuccessfulUpgradeLog -LogPath $installLog
            Assert-Check ($helper.Process.WaitForExit(5000)) 'Responding helper exited after WM_CLOSE' "PID=$($helper.Process.Id)"
            Assert-Check (Test-Path -LiteralPath $eventPath) 'Responding helper WM_CLOSE evidence' $eventPath
            Assert-Check ((Get-Content -LiteralPath $eventPath -Raw -Encoding UTF8) -match 'WM_CLOSE') 'Responding helper received WM_CLOSE' (Get-Content -LiteralPath $eventPath -Raw -Encoding UTF8)
            Assert-CurrentInstallation
        }
        else {
            $failure = Invoke-MsiTransaction -Operation Install -Target $MsiPath -LogPath $installLog -ProcessBaseline $baselineProcesses -TimeoutSeconds 25 -ExpectedExitCodes @(1603) -MaximumElapsedSeconds 25
            $survivingHelper = Get-ExactProcessIdentity -ProcessId $helper.Process.Id
            $sameSurvivingIdentity = ($null -ne $survivingHelper -and
                [string]::Equals([string]$survivingHelper.Name, 'SwSearch.App.exe', [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([IO.Path]::GetFullPath([string]$survivingHelper.ExecutablePath), [IO.Path]::GetFullPath([string]$script:HelperExecutablePath), [StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals([string]$survivingHelper.CreationDate, [string]$script:HelperProcessCreationTime, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$survivingHelper.CommandLine, [string]$script:HelperCommandLine, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$survivingHelper.OwnerSid, [string]$script:HelperOwnerSid, [StringComparison]::OrdinalIgnoreCase))
            Assert-Check $sameSurvivingIdentity 'Ignoring helper full identity survives rejected upgrade' "PID=$($helper.Process.Id)"
            Assert-Check (Test-Path -LiteralPath $eventPath) 'Ignoring helper WM_CLOSE evidence' $eventPath
            Assert-Check ((Get-Content -LiteralPath $eventPath -Raw -Encoding UTF8) -match 'WM_CLOSE') 'Ignoring helper received and ignored WM_CLOSE' (Get-Content -LiteralPath $eventPath -Raw -Encoding UTF8)
            Assert-Check ((Get-ProductState $PreviousProductCode) -eq 5) 'Rejected upgrade preserved previous product registration' "State=$(Get-ProductState $PreviousProductCode)"
            Assert-Check ((Get-ProductState $ProductCode) -eq -1) 'Rejected upgrade did not register current product' "State=$(Get-ProductState $ProductCode)"
            $afterRejectedSnapshot = Get-CompleteInstallationStateSnapshot -InstallDirectory $installDirectory
            Write-AtomicUtf8Json -Path (Join-Path $ResultRoot 'old-state-after-rejection.json') -Value $afterRejectedSnapshot.State
            Assert-Check ($afterRejectedSnapshot.Hash -eq $oldSnapshot.Hash) 'C2 complete old-state snapshot equality' "Before=$($oldSnapshot.Hash); After=$($afterRejectedSnapshot.Hash)"
            Assert-Check ((Get-ProductState $ProductCode) -eq -1 -and @(Get-UninstallEntries $ProductCode).Count -eq 0) 'C2 current version remains unregistered' "State=$(Get-ProductState $ProductCode); ARP=$(@(Get-UninstallEntries $ProductCode).Count)"
            Assert-RejectedUpgradeLog -LogPath $installLog
        }
    }
}
catch {
    $primaryError = $_
    Add-Check 'Lifecycle validation' $false $_.Exception.Message
}
finally {
    if (-not $script:TransactionStillRunning -and -not $script:UnmanagedChildStillRunning) {
        try {
            Stop-TestWindowHelperIfSafe
            Assert-NoInstallerActivity -AllowServiceHost
            if ($script:AnyInstallAttempted -and $baselineCurrentProductState -ne 5 -and (Get-ProductState $ProductCode) -eq 5) {
                $uninstallLog = Join-Path $ResultRoot (if ($isUpgradeScenario) { '03-current-uninstall.verbose.log' } else { '02-current-uninstall.verbose.log' })
                Invoke-MsiTransaction -Operation Uninstall -Target $ProductCode -LogPath $uninstallLog -ProcessBaseline $baselineProcesses
            }
            if ($script:AnyInstallAttempted -and $baselinePreviousProductState -ne 5 -and (Get-ProductState $PreviousProductCode) -eq 5) {
                $oldUninstallLog = Join-Path $ResultRoot '04-previous-cleanup.verbose.log'
                Invoke-MsiTransaction -Operation Uninstall -Target $PreviousProductCode -LogPath $oldUninstallLog -ProcessBaseline $baselineProcesses
            }
            if ($script:AnyInstallAttempted) {
                $managedProductCodes = @()
                if ($baselineCurrentProductState -ne 5) { $managedProductCodes += $ProductCode }
                if ($baselinePreviousProductState -ne 5) { $managedProductCodes += $PreviousProductCode }
                Assert-NoInstalledResidue -InstallDirectory $installDirectory -ProductCodes $managedProductCodes -DataDirectoryExistedBefore $dataDirectoryExistedBefore
                Assert-Check (-not (Test-Path -LiteralPath $startMenuDirectory)) 'Removed start menu folder' $startMenuDirectory
                Assert-Check (-not (Test-Path -LiteralPath $desktopShortcut)) 'No desktop shortcut residue' $desktopShortcut
                Observe-ForbiddenProcesses -Baseline $baselineProcesses
                Assert-Check ($script:ObservedForbiddenProcesses.Count -eq 0) 'No forbidden process observed in full lifecycle' ($script:ObservedForbiddenProcesses | ConvertTo-Json -Compress)
            }
            else {
                Add-Check 'Cleanup skipped safely' $true 'No install transaction was started; no pre-existing product was touched.'
            }
        }
        catch {
            $cleanupError = $_
            Add-Check 'Cleanup and residue validation' $false $_.Exception.Message
        }
    }
    else {
        $reason = if ($script:TransactionStillRunning) {
            'An MSI transaction is still running.'
        }
        else {
            'A CLI probe/helper child exceeded its timeout and may still be using installed files.'
        }
        Add-Check 'Automatic cleanup skipped' $false "MANUAL_CLEANUP: $reason No second msiexec was started and no non-helper process was terminated."
    }

    if (-not (Test-Path -LiteralPath $ResultRoot)) {
        New-Item -ItemType Directory -Force -Path $ResultRoot | Out-Null
    }
    $manualCleanupRequired = ($script:TransactionStillRunning -or $script:UnmanagedChildStillRunning -or $null -ne $cleanupError)
    $preContractFailures = @($script:Checks | Where-Object { $_.Passed -ne $true }).Count
    $contractPassed = ($null -eq $primaryError -and $null -eq $cleanupError -and -not $manualCleanupRequired -and -not $script:ScenarioSkipped -and $preContractFailures -eq 0)
    Add-Check "Scenario contract $Scenario" $contractPassed "Scenario=$Scenario; PreContractFailures=$preContractFailures; ManualCleanup=$manualCleanupRequired; Skipped=$($script:ScenarioSkipped)"
    $missingRequired = @($script:Manifest.RequiredChecks | Where-Object {
        $requiredName = [string]$_
        @($script:Checks | Where-Object { [string]::Equals($_.Name, $requiredName, [StringComparison]::Ordinal) -and $_.Passed -eq $true }).Count -ne 1
    })
    if ($missingRequired.Count -gt 0) {
        Add-Check 'Required check contract' $false "Missing, duplicated, or failed: $($missingRequired -join ', ')"
    }
    else {
        Add-Check 'Required check contract' $true 'Every manifest-required check exists exactly once and passed.'
    }
    $passed = (-not $manualCleanupRequired -and -not $script:ScenarioSkipped -and @($script:Checks | Where-Object { $_.Passed -ne $true }).Count -eq 0)
    $report = [ordered]@{
        SchemaVersion = 2
        GeneratedAt = [DateTimeOffset]::Now.ToString('o')
        RunId = [string]$script:Manifest.RunId
        Nonce = [string]$script:Manifest.Nonce
        ScenarioId = [string]$script:Manifest.ScenarioId
        Scenario = $Scenario
        Account = $runtime.SamAccountName
        UserSid = $runtime.Sid
        ProfilePath = $runtime.ProfilePath
        ProtectedUserSid = $ProtectedUserSid
        ManifestPath = $script:Manifest.ManifestPath
        ManifestSha256 = $script:Manifest.ManifestSha256
        IsAdministrator = $isAdministrator
        MsiIdentity = [ordered]@{
            Path = $MsiPath
            Sha256 = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
            Version = $ExpectedVersion
            ProductCode = $ProductCode
            UpgradeCode = $UpgradeCode
        }
        PreviousMsiIdentity = if ($isUpgradeScenario) {
            [ordered]@{
                Path = $PreviousMsiPath
                Sha256 = (Get-FileHash -LiteralPath $PreviousMsiPath -Algorithm SHA256).Hash
                Version = $PreviousVersion
                ProductCode = $PreviousProductCode
                UpgradeCode = $UpgradeCode
            }
        } else { $null }
        InstallDirectory = $installDirectory
        Status = if ($passed) { 'PASS' } elseif ($script:ScenarioSkipped) { 'SKIP' } else { 'FAIL' }
        Passed = [bool]$passed
        Skipped = $script:ScenarioSkipped
        SkipReason = $script:SkipReason
        PrimaryError = if ($primaryError) { $primaryError.Exception.ToString() } else { $null }
        CleanupError = if ($cleanupError) { $cleanupError.Exception.ToString() } else { $null }
        TransactionStillRunning = $script:TransactionStillRunning
        UnmanagedChildStillRunning = $script:UnmanagedChildStillRunning
        ManualCleanupRequired = [bool]$manualCleanupRequired
        Helper = [ordered]@{
            Mode = $script:HelperMode
            ProcessId = $script:HelperProcessId
            CreationTime = $script:HelperProcessCreationTime
            ExecutablePath = $script:HelperExecutablePath
            CommandLine = $script:HelperCommandLine
            OwnerSid = $script:HelperOwnerSid
            SessionId = $script:HelperSessionId
        }
        OutstandingProcesses = @($script:OutstandingProcesses)
        ObservedForbiddenProcesses = @($script:ObservedForbiddenProcesses)
        Checks = @($script:Checks)
    }
    Write-AtomicUtf8Json -Path (Join-Path $ResultRoot 'lifecycle-report.json') -Value $report
}

if ($primaryError) { throw $primaryError }
if ($cleanupError) { throw $cleanupError }
if ($script:ScenarioSkipped) {
    Write-Output "Lifecycle validation skipped safely: $($script:SkipReason)"
}
else {
    Write-Output "Lifecycle validation passed. Report: $(Join-Path $ResultRoot 'lifecycle-report.json')"
}
