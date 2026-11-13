[CmdletBinding()]
param(
    [string]$Version = '1.3.18',
    [ValidatePattern('^rc\d+$')][string]$ReleaseCandidate = 'rc94',
    [string]$ProductCode = 'DDA7EA84-BDED-4225-8480-9E2A0941D8E6',
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{32}$')][string]$TransactionNonce,
    [Parameter(Mandatory = $true)][string]$PreviousMsi,
    [Parameter(Mandatory = $true)][string]$ControlledManifest,
    [switch]$UseControlledSourceReceipt,
    [string]$FrozenRestoreGraphReceipt,
    [ValidatePattern('^[A-F0-9]{64}$')][string]$FrozenRestoreGraphReceiptSha256,
    [switch]$SkipFinalFrozenRestoreGraph
)

$ErrorActionPreference='Stop'
$latestShippedProductVersion = [Version]'1.3.12'
$candidateProductVersion = [Version]$Version
if($candidateProductVersion -le $latestShippedProductVersion){
    throw "MSI ProductVersion must be greater than the latest shipped version ${latestShippedProductVersion}: $Version"
}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts=Join-Path $root 'artifacts'
$candidateSuffix=$ReleaseCandidate
# The artifact family tracks the actual MSI ProductVersion for each RC delivery.
$artifactName="SwSearch-x64-v$Version-$candidateSuffix"
$pendingPrefix=Join-Path $artifacts ".pending-$TransactionNonce-$artifactName"
$msi="$pendingPrefix.msi"
$PayloadHandoff="$pendingPrefix.payload-snapshot.json"
$expectedTransactionBuildRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".build\msi-$Version-$candidateSuffix-$TransactionNonce"))
$expectedPayloadSnapshotRoot=Join-Path $expectedTransactionBuildRoot 'payload-snapshot'
$payload=$null
$payloadManifest=$null
$inspection=Join-Path $artifacts ".verify-$TransactionNonce-$artifactName"
$decompiled=Join-Path $inspection 'decompiled.wxs'
$export=Join-Path $inspection 'export'
$report="$pendingPrefix.verification.txt"
$reportWriting="$pendingPrefix.verification.writing"
$InstallerActionsManifest="$pendingPrefix.installer-actions.sha256.json"
$BridgeGate="$pendingPrefix.installer-bridge-gate.exe"
$CommandLineGate="$pendingPrefix.installer-command-line-gate.exe"
$commandLineGateVerifyLog="$pendingPrefix.installer-command-line-gate.verify.log"
$wix=Join-Path $root '.packages-rc48-r3\wixtoolset.sdk\5.0.2\tools\net472\x64\wix.exe'
$dtf=Join-Path $root '.packages-rc48-r3\wixtoolset.sdk\5.0.2\tools\net472\WixToolset.Dtf.WindowsInstaller.dll'
$upgradeCode='{D9158356-29B3-44D6-A15A-ACDCE8328703}'
$addinClsid='{F3A7CC42-C6E5-4CF8-96D6-2D1F1AA1B61D}'
$expectedPreviousMsi=Join-Path $artifacts 'SwSearch-x64-v1.2.0-rc12.msi'
$expectedPreviousSha='4FC6A7E126462377FC5D43A607884FADB12A0BFE3E6C638FCAA8D92D2571DFD5'
$expectedPreviousBytes=57687391L
$expectedPreviousProductCode='{49D3B604-E741-4D2A-975B-D1E84C0A8528}'
$expectedRc15Msi=Join-Path $artifacts 'SwSearch-x64-v1.2.0-rc15.msi'
$expectedRc15Sha='B91BF1912B5F57EFF1307A27F7FA51094026E2FD12B7300A07C0A56C02359E85'
$expectedRc15Bytes=93065427L
$expectedRc15ProductCode='{A6CCBEB3-300D-4D55-AEAD-B7AE975D6473}'
$parsedProductCode=[Guid]::Parse($ProductCode)
if($parsedProductCode -eq [Guid]::Empty){ throw 'ProductCode must not be empty.' }
$ProductCode=$parsedProductCode.ToString('D').ToUpperInvariant()
$retiredProductCodes=@(
    'AED45CC2-3804-4203-9A2A-88F60D71671F','EB9F07BF-6F63-4E4C-97DC-9C5756B30F35',
    '25A0D431-5981-43D8-A996-8CAB7E77BA84','B5764302-2832-4E7F-A85F-83A7A4BF6C90',
    '1DFE62DD-BF2D-4BE2-894D-A9E2BA44897B',
    'E1C70D94-47BD-4CE3-92BC-EF408A3F3258','EC25790F-EF5C-4CA9-99FE-D6A814A102BA',
    'A28EF355-7779-4702-9858-0625AD922DCB','4D93BE01-220C-48F6-B0B7-35B224199841',
    '8A49EE33-05F5-43C7-8A92-77F56D0019B0','032E26AF-8146-4458-ACBE-4D3BDCB2D279',
    '7810FBC5-E94C-415D-9233-7CAB0D7D0039','A0A485ED-004A-4CA2-B6AF-44C8C6BFC114',
    '717380CD-67C3-4E62-B0F3-0DA1ACFD9E2D','AB60D15A-FF90-46BA-9F11-269AD9F60631',
    'FA322723-1158-42D0-880E-6B91CCCB4B82','49D3B604-E741-4D2A-975B-D1E84C0A8528',
    '054C91ED-FAB3-43C6-864A-47D8ACEFEEF6',
    'FB93C5D8-BEAD-4C29-8A55-3751FAF1B1A5','A88433F1-0E4F-43BD-9DF8-ADB10410E508',
    'A6CCBEB3-300D-4D55-AEAD-B7AE975D6473','A5D7B1CD-53CB-4D8F-99BA-3135F06C08BD',
    'B0EADF5A-B418-4466-BA65-73D956F47FF6','BC970457-7B46-4720-B6A7-38DE18099051',
    '33B51BB6-44B5-44EF-B2ED-78A1032C5A25','E1C2A658-3AB8-4F89-A331-6C8EFDB4F112',
    '7B11289D-584C-4E8C-A72E-6C731038975D','9B27108D-EE89-4D2A-BE15-08431C009BDD',
    'A4B5188B-BB8F-4050-851F-525D0B9819FD','D1DA13CA-70A4-4E4E-A8B6-4DD078656D82',
    'D3229D39-D127-4EA2-BB33-3E1338D88D53','823D4B5E-0AD2-4A25-8E17-E685ACD79870',
    '7231A572-AFBC-4461-85BC-6326EF37A924','D448ED2A-BFC2-4923-8857-EEA9BA50F03C',
    '6670BD30-6799-4072-AB15-90FC475522F4','ED006593-C117-470E-97A8-5DCC4F5461E6',
    '3F1C278E-118B-406C-A9CC-C3B096938C69','C066B810-2F8F-4EE4-B762-8A70F6F71035',
    '668BE7C8-01EE-4A87-BD09-26308D222CD8','B4878F23-2593-4BFB-A3BA-B585876C86FC',
    'EA7DA896-F1BB-4EDE-85FB-C21244AF88A2','B8B4EE5A-B779-4F61-934D-FE582DCB67FE',
    '77969206-D300-4D06-A2A7-9D513836D2B6','89543BBB-3325-402F-ACE1-4394D39A2F81',
    '050BF38C-7D08-427B-8EC9-C8DB9D19EA1E','FA373B6F-05DB-41CB-A97F-E5BE35466058',
    'CB20D1F4-AA14-4518-BFAE-423C6315E9FD','23336225-6550-4621-B804-0444797F620E',
    'A55D5C1E-1D56-4F7F-9E5A-4F2802DD7B44','7328AB61-578D-47EC-83AE-01A35882D33E',
    '3B6A5C7D-4E92-4B01-A7F6-9D2C815E46A0','6F2D9A8C-31B7-4E65-92C4-5A780D1F47B3',
    '6E45A916-D3A3-495B-BCE4-2B2BD000F237','C628FD2E-2F28-4F6A-83C9-798186AEAEC5',
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
    'E5A97259-595F-4C23-8F9D-B5F423E7B69A','9C84757E-1683-47A7-8A75-F1247FC155F2',
    'E7C4F307-DF19-49E0-B5EB-D2B3262FFC8F'
) | ForEach-Object { [Guid]::Parse($_) }
$restoreGraphGate=Join-Path $PSScriptRoot 'Assert-FrozenRestoreGraph.ps1'
. (Join-Path $PSScriptRoot 'Source-Freeze.ps1')
. (Join-Path $PSScriptRoot 'Payload-Manifest.ps1')
$hasFrozenRestoreGraphReceipt=-not [String]::IsNullOrWhiteSpace($FrozenRestoreGraphReceipt)
if($hasFrozenRestoreGraphReceipt -ne (-not [String]::IsNullOrWhiteSpace($FrozenRestoreGraphReceiptSha256))){
    throw 'Frozen restore graph receipt path and SHA256 must be supplied together.'
}
if($SkipFinalFrozenRestoreGraph -and -not $hasFrozenRestoreGraphReceipt){
    throw 'SkipFinalFrozenRestoreGraph requires the transaction-bound frozen restore graph receipt and SHA256.'
}
$finalFrozenRestoreGraphGateReport = if($SkipFinalFrozenRestoreGraph){
    'Frozen restore graph: final deep archive/package walk skipped only by explicit receipt-gated controlled fallback; transaction receipt identity and current lock manifest hash were verified'
}else{
    'Frozen restore graph: final deep archive/package walk passed'
}
if($env:SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH -ceq '1'){
    throw 'SWSEARCH_SKIP_FROZEN_RESTORE_GRAPH is forbidden; frozen restore graph validation cannot be bypassed.'
}
function Get-StableHashHex([string]$Text){
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-','') }
    finally { $algorithm.Dispose() }
}
function Assert-FixedFile([string]$Path,[long]$Bytes,[string]$Sha256,[string]$Label){
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf)){ throw "$Label missing: $Path" }
    $item=Get-Item -LiteralPath $Path
    if($item.Length -ne $Bytes){ throw "$Label length mismatch: $($item.Length)" }
    $hash=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if($hash -cne $Sha256){ throw "$Label SHA256 mismatch: $hash" }
}
function Assert-ExactJsonProperties([object]$Value,[string[]]$Expected,[string]$Label){
    if($null -eq $Value){ throw "$Label is missing." }
    $actual=@($Value.psobject.Properties.Name | Sort-Object)
    $wanted=@($Expected | Sort-Object)
    if($actual.Count -ne $wanted.Count -or @(Compare-Object -ReferenceObject $wanted -DifferenceObject $actual).Count -ne 0){
        throw "$Label property set mismatch: $($actual -join ', ')"
    }
}
function Assert-AddinUpgradeTableModel(
    [hashtable]$Sequence,
    [object[]]$RegistryRows,
    [hashtable]$ComponentRows,
    [object[]]$FeatureComponentRows,
    [string]$AddinClsid,
    [object]$ExpectedKeyPathRow){
    if(-not $Sequence.ContainsKey('FindRelatedProducts') -or
       -not $Sequence.ContainsKey('CaptureSwSearchTrustedContext') -or
       -not $Sequence.ContainsKey('InstallValidate') -or
       -not ($Sequence.FindRelatedProducts.Sequence -lt $Sequence.CaptureSwSearchTrustedContext.Sequence -and
             $Sequence.CaptureSwSearchTrustedContext.Sequence -lt $Sequence.InstallValidate.Sequence)){
        throw 'MSI_ADDIN_MODEL_FIND_RELATED_PRODUCTS_ORDER'
    }
    $inproc=@($RegistryRows | Where-Object {
        $_.Key -ceq "Software\Classes\CLSID\$AddinClsid\InprocServer32" -and $null -eq $_.Name
    })
    if($inproc.Count -ne 1 -or $inproc[0].Root -ne 2 -or
       $inproc[0].Value -cne '[System64Folder]mscoree.dll' -or
       $inproc[0].Component -cne 'SolidWorksAddinMachineRegistration'){
        throw 'MSI_ADDIN_MODEL_INPROC_ROOT_OR_VALUE'
    }
    if(-not $ComponentRows.ContainsKey('SolidWorksAddinMachineRegistration')){
        throw 'MSI_ADDIN_MODEL_COMPONENT_MISSING'
    }
    $component=$ComponentRows.SolidWorksAddinMachineRegistration
    if($component.KeyPath -cne $ExpectedKeyPathRow.Id){ throw 'MSI_ADDIN_MODEL_KEYPATH_CHANGED' }
    $keyPathRows=@($RegistryRows | Where-Object { $_.Id -ceq $component.KeyPath })
    if($keyPathRows.Count -ne 1 -or $keyPathRows[0].Root -ne 2 -or
       $keyPathRows[0].Component -cne 'SolidWorksAddinMachineRegistration' -or
       $keyPathRows[0].Key -cne "SOFTWARE\SolidWorks\Addins\$AddinClsid" -or
       $null -ne $keyPathRows[0].Name -or $keyPathRows[0].Value -cne '#0' -or
       $keyPathRows[0].Id -cne $ExpectedKeyPathRow.Id -or
       $keyPathRows[0].Root -ne $ExpectedKeyPathRow.Root -or
       $keyPathRows[0].Key -cne $ExpectedKeyPathRow.Key -or
       $keyPathRows[0].Name -ne $ExpectedKeyPathRow.Name -or
       $keyPathRows[0].Value -cne $ExpectedKeyPathRow.Value -or
       $keyPathRows[0].Component -cne $ExpectedKeyPathRow.Component){
        throw 'MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE'
    }
    $references=@($FeatureComponentRows | Where-Object { $_.Component -ceq 'SolidWorksAddinMachineRegistration' })
    if($references.Count -ne 1 -or $references[0].Feature -cne 'MainFeature'){
        throw 'MSI_ADDIN_MODEL_FEATURE_REFERENCE'
    }
}
function Test-AddinUpgradeTableModelSelfTest {
    $clsid='{F3A7CC42-C6E5-4CF8-96D6-2D1F1AA1B61D}'
    $sequence=@{
        FindRelatedProducts=[pscustomobject]@{Sequence=25}
        CaptureSwSearchTrustedContext=[pscustomobject]@{Sequence=1100}
        InstallValidate=[pscustomobject]@{Sequence=1400}
    }
    $registry=@(
        [pscustomobject]@{Id='AddinKey';Root=2;Key="SOFTWARE\SolidWorks\Addins\$clsid";Name=$null;Value='#0';Component='SolidWorksAddinMachineRegistration'},
        [pscustomobject]@{Id='Inproc';Root=2;Key="Software\Classes\CLSID\$clsid\InprocServer32";Name=$null;Value='[System64Folder]mscoree.dll';Component='SolidWorksAddinMachineRegistration'}
    )
    $components=@{SolidWorksAddinMachineRegistration=[pscustomobject]@{KeyPath='AddinKey'}}
    $features=@([pscustomobject]@{Feature='MainFeature';Component='SolidWorksAddinMachineRegistration'})
    $expectedKeyPath=$registry[0]
    Assert-AddinUpgradeTableModel $sequence $registry $components $features $clsid $expectedKeyPath
    function Assert-Rejected([string]$Expected,[scriptblock]$Fixture){
        $message=$null
        try { & $Fixture }
        catch { $message=$_.Exception.Message }
        if($message -cne $Expected){ throw "MSI add-in model unsafe fixture escaped: expected=$Expected actual=$message" }
    }
    $wrongRoot=@($registry | ForEach-Object { [pscustomobject]@{Id=$_.Id;Root=if($_.Id -ceq 'Inproc'){1}else{$_.Root};Key=$_.Key;Name=$_.Name;Value=$_.Value;Component=$_.Component} })
    Assert-Rejected 'MSI_ADDIN_MODEL_INPROC_ROOT_OR_VALUE' { Assert-AddinUpgradeTableModel $sequence $wrongRoot $components $features $clsid $expectedKeyPath }
    $wrongOrder=@{
        FindRelatedProducts=[pscustomobject]@{Sequence=1200}
        CaptureSwSearchTrustedContext=[pscustomobject]@{Sequence=1100}
        InstallValidate=[pscustomobject]@{Sequence=1400}
    }
    Assert-Rejected 'MSI_ADDIN_MODEL_FIND_RELATED_PRODUCTS_ORDER' { Assert-AddinUpgradeTableModel $wrongOrder $registry $components $features $clsid $expectedKeyPath }
    $redirected=@{SolidWorksAddinMachineRegistration=[pscustomobject]@{KeyPath='OtherKey'}}
    Assert-Rejected 'MSI_ADDIN_MODEL_KEYPATH_CHANGED' { Assert-AddinUpgradeTableModel $sequence $registry $redirected $features $clsid $expectedKeyPath }
    $orphanedKeyPath=@($registry | ForEach-Object { [pscustomobject]@{Id=if($_.Id -ceq 'AddinKey'){'OtherKey'}else{$_.Id};Root=$_.Root;Key=$_.Key;Name=$_.Name;Value=$_.Value;Component=$_.Component} })
    Assert-Rejected 'MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $orphanedKeyPath $components $features $clsid $expectedKeyPath }
    $wrongValue=@($registry | ForEach-Object { [pscustomobject]@{Id=$_.Id;Root=$_.Root;Key=$_.Key;Name=$_.Name;Value=if($_.Id -ceq 'AddinKey'){'#1'}else{$_.Value};Component=$_.Component} })
    Assert-Rejected 'MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $wrongValue $components $features $clsid $expectedKeyPath }
    $wrongKeyPathRoot=@($registry | ForEach-Object { [pscustomobject]@{Id=$_.Id;Root=if($_.Id -ceq 'AddinKey'){1}else{$_.Root};Key=$_.Key;Name=$_.Name;Value=$_.Value;Component=$_.Component} })
    Assert-Rejected 'MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $wrongKeyPathRoot $components $features $clsid $expectedKeyPath }
    $wrongKeyPathKey=@($registry | ForEach-Object { [pscustomobject]@{Id=$_.Id;Root=$_.Root;Key=if($_.Id -ceq 'AddinKey'){"SOFTWARE\SolidWorks\Addins\Sibling"}else{$_.Key};Name=$_.Name;Value=$_.Value;Component=$_.Component} })
    Assert-Rejected 'MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $wrongKeyPathKey $components $features $clsid $expectedKeyPath }
    $wrongKeyPathName=@($registry | ForEach-Object { [pscustomobject]@{Id=$_.Id;Root=$_.Root;Key=$_.Key;Name=if($_.Id -ceq 'AddinKey'){'Title'}else{$_.Name};Value=$_.Value;Component=$_.Component} })
    Assert-Rejected 'MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $wrongKeyPathName $components $features $clsid $expectedKeyPath }
    $wrongKeyPathComponent=@($registry | ForEach-Object { [pscustomobject]@{Id=$_.Id;Root=$_.Root;Key=$_.Key;Name=$_.Name;Value=$_.Value;Component=if($_.Id -ceq 'AddinKey'){'OtherComponent'}else{$_.Component}} })
    Assert-Rejected 'MSI_ADDIN_MODEL_KEYPATH_REGISTRY_BACK_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $wrongKeyPathComponent $components $features $clsid $expectedKeyPath }
    Assert-Rejected 'MSI_ADDIN_MODEL_FEATURE_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $registry $components @() $clsid $expectedKeyPath }
    $multiple=@($features + [pscustomobject]@{Feature='OtherFeature';Component='SolidWorksAddinMachineRegistration'})
    Assert-Rejected 'MSI_ADDIN_MODEL_FEATURE_REFERENCE' { Assert-AddinUpgradeTableModel $sequence $registry $components $multiple $clsid $expectedKeyPath }
}
Test-AddinUpgradeTableModelSelfTest
function Get-PeImageFacts([string]$Path){
    $peFile=Get-Item -LiteralPath $Path
    if($peFile.Length -le 0 -or $peFile.Length -gt 16MB){ throw "PE image length is outside the bridge gate bound: $Path" }
    $data=[IO.File]::ReadAllBytes($Path)
    if($data.Length -lt 512 -or [BitConverter]::ToUInt16($data,0) -ne 0x5A4D){ throw "PE DOS header invalid: $Path" }
    $pe=[BitConverter]::ToInt32($data,0x3C)
    if($pe -lt 0x40 -or $pe + 24 -gt $data.Length -or [BitConverter]::ToUInt32($data,$pe) -ne 0x00004550){ throw "PE signature invalid: $Path" }
    $machine=[BitConverter]::ToUInt16($data,$pe+4)
    $sectionCount=[BitConverter]::ToUInt16($data,$pe+6)
    $optionalSize=[BitConverter]::ToUInt16($data,$pe+20)
    $optional=$pe+24
    if($machine -ne 0x8664 -or [BitConverter]::ToUInt16($data,$optional) -ne 0x20B){ throw "PE image is not AMD64 PE32+: $Path" }
    $sectionTable=$optional+$optionalSize
    if($sectionCount -lt 1 -or $sectionCount -gt 96 -or $sectionTable+($sectionCount*40) -gt $data.Length){ throw "PE section table invalid: $Path" }
    $sections=@()
    for($index=0;$index -lt $sectionCount;$index++){
        $offset=$sectionTable+($index*40)
        $sections += [pscustomobject]@{
            VirtualSize=[BitConverter]::ToUInt32($data,$offset+8)
            VirtualAddress=[BitConverter]::ToUInt32($data,$offset+12)
            RawSize=[BitConverter]::ToUInt32($data,$offset+16)
            RawAddress=[BitConverter]::ToUInt32($data,$offset+20)
        }
    }
    function Convert-Rva([uint32]$Rva){
        foreach($section in $sections){
            $extent=[Math]::Max([long]$section.VirtualSize,[long]$section.RawSize)
            if([long]$Rva -ge [long]$section.VirtualAddress -and [long]$Rva -lt [long]$section.VirtualAddress+$extent){
                $result=[long]$section.RawAddress+([long]$Rva-[long]$section.VirtualAddress)
                if($result -lt 0 -or $result -ge $data.Length){ throw "PE RVA escaped file: $Path" }
                return [int]$result
            }
        }
        throw "PE RVA is not mapped: $Path / $Rva"
    }
    function Read-AsciiZ([int]$Offset){
        if($Offset -lt 0 -or $Offset -ge $data.Length){ throw "PE string offset invalid: $Path" }
        $end=$Offset
        while($end -lt $data.Length -and $data[$end] -ne 0 -and $end-$Offset -le 512){ $end++ }
        if($end -ge $data.Length -or $end-$Offset -gt 512){ throw "PE string is unterminated: $Path" }
        return [Text.Encoding]::ASCII.GetString($data,$Offset,$end-$Offset)
    }
    $exportRva=[BitConverter]::ToUInt32($data,$optional+112)
    $importRva=[BitConverter]::ToUInt32($data,$optional+120)
    $exports=@()
    if($exportRva -ne 0){
        $exportOffset=Convert-Rva $exportRva
        if($exportOffset+40 -gt $data.Length){ throw "PE export directory invalid: $Path" }
        $nameCount=[BitConverter]::ToUInt32($data,$exportOffset+24)
        $namesRva=[BitConverter]::ToUInt32($data,$exportOffset+32)
        if($nameCount -gt 4096){ throw "PE export count invalid: $Path" }
        if($nameCount -gt 0){
            $namesOffset=Convert-Rva $namesRva
            for($index=0;$index -lt $nameCount;$index++){
                $nameRva=[BitConverter]::ToUInt32($data,$namesOffset+($index*4))
                $exports += Read-AsciiZ (Convert-Rva $nameRva)
            }
        }
    }
    $imports=@()
    if($importRva -ne 0){
        $descriptor=Convert-Rva $importRva
        for($index=0;$index -lt 256;$index++){
            $offset=$descriptor+($index*20)
            if($offset+20 -gt $data.Length){ throw "PE import directory invalid: $Path" }
            $original=[BitConverter]::ToUInt32($data,$offset)
            $nameRva=[BitConverter]::ToUInt32($data,$offset+12)
            $firstThunk=[BitConverter]::ToUInt32($data,$offset+16)
            if($original -eq 0 -and $nameRva -eq 0 -and $firstThunk -eq 0){ break }
            if($nameRva -eq 0){ throw "PE import name missing: $Path" }
            $imports += Read-AsciiZ (Convert-Rva $nameRva)
        }
    }
    [pscustomobject]@{ Machine=$machine; Exports=@($exports); Imports=@($imports | Sort-Object -Unique) }
}
function Invoke-BoundedIsolatedProcess([string]$FilePath,[string[]]$Arguments,[int]$TimeoutMilliseconds,[string]$Label){
    $quoted=@($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' })
    $startInfo=[Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=$FilePath
    $startInfo.Arguments=$quoted -join ' '
    $startInfo.UseShellExecute=$false
    $startInfo.CreateNoWindow=$true
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$startInfo
    if(-not $process.Start()){ throw "$Label could not be started." }
    try{
        if(-not $process.WaitForExit($TimeoutMilliseconds)){
            $process.Kill()
            throw "$Label exceeded its strict timeout."
        }
        $process.WaitForExit()
        if($process.ExitCode -ne 0){ throw "$Label failed with exit code $($process.ExitCode)." }
    }finally{ $process.Dispose() }
}
function Invoke-BoundedInstallerCommandLineGate(
    [string]$GatePath,
    [string]$MsiPath,
    [int]$TimeoutMilliseconds,
    [string]$Label){
    $internalProbeBudgetMilliseconds=4*5000
    if($TimeoutMilliseconds -le $internalProbeBudgetMilliseconds){
        throw "$Label hard deadline must exceed the native gate internal probe budget."
    }
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $runRoot=Join-Path $tempRoot ('swsearch-msi-command-line-gate-'+[Guid]::NewGuid().ToString('N'))
    if(-not $runRoot.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or
       [IO.Path]::GetFileName($runRoot) -notmatch '^swsearch-msi-command-line-gate-[0-9a-f]{32}$'){
        throw "$Label temp root identity is invalid."
    }
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $startInfo=[Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=$GatePath
    $startInfo.Arguments='"--verify-msi" "'+$MsiPath.Replace('"','\"')+'"'
    $startInfo.UseShellExecute=$false
    $startInfo.CreateNoWindow=$true
    $startInfo.WorkingDirectory=$runRoot
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$startInfo
    $started=$false
    try{
        if(-not $process.Start()){ throw "$Label could not be started." }
        $started=$true
        if(-not $process.WaitForExit($TimeoutMilliseconds)){
            # The native gate puts every self-created probe in a
            # KILL_ON_JOB_CLOSE Job; killing this root closes that tree.
            $process.Kill()
            if(-not $process.WaitForExit(5000)){
                throw "$Label process tree did not close after hard deadline."
            }
            throw "$Label exceeded its strict timeout."
        }
        if($process.ExitCode -ne 0){ throw "$Label failed with exit code $($process.ExitCode)." }
    }finally{
        try{
            if($started -and -not $process.HasExited){
                $process.Kill()
                if(-not $process.WaitForExit(5000)){
                    throw "$Label self-created process tree cleanup failed."
                }
            }
        }finally{
            $process.Dispose()
            if(Test-Path -LiteralPath $runRoot){
                Remove-Item -LiteralPath $runRoot -Recurse -Force
            }
        }
    }
}
$expectedControlledManifest="$pendingPrefix.controlled-source.sha256.txt"
if(-not [IO.Path]::GetFullPath($ControlledManifest).Equals([IO.Path]::GetFullPath($expectedControlledManifest),[StringComparison]::OrdinalIgnoreCase)){
    throw "ControlledManifest must be the current transaction path: $expectedControlledManifest"
}
foreach($path in @($msi,$InstallerActionsManifest,$BridgeGate,$CommandLineGate,$commandLineGateVerifyLog,$report,$reportWriting,$ControlledManifest,$inspection,$PayloadHandoff)){
    $full=[IO.Path]::GetFullPath($path)
    if(-not $full.StartsWith($artifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
        throw "Verification transaction path escaped artifacts: $full"
    }
}
if(Test-Path -LiteralPath $inspection){ throw "Verification inspection directory already exists: $inspection" }
foreach($path in @($report,$reportWriting,$commandLineGateVerifyLog)){
    if(Test-Path -LiteralPath $path){ throw "Verification output already exists: $path" }
}

$verificationComplete=$false
try{
foreach($path in @($msi,$PayloadHandoff,$wix,$dtf)){ if(-not (Test-Path -LiteralPath $path)){ throw "Missing static verification input: $path" } }
$payloadHandoffData=Get-Content -LiteralPath $PayloadHandoff -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-ExactJsonProperties $payloadHandoffData @('schema','transactionNonce','snapshotRoot','manifestSha256','entriesDigest','fileCount') 'Payload snapshot handoff'
if($payloadHandoffData.schema -ne 1 -or
   [string]$payloadHandoffData.transactionNonce -cne $TransactionNonce -or
   -not [IO.Path]::GetFullPath([string]$payloadHandoffData.snapshotRoot).Equals([IO.Path]::GetFullPath($expectedPayloadSnapshotRoot),[StringComparison]::OrdinalIgnoreCase) -or
   [string]$payloadHandoffData.manifestSha256 -notmatch '^[A-F0-9]{64}$' -or
   [string]$payloadHandoffData.entriesDigest -notmatch '^[A-F0-9]{64}$' -or
   [int]$payloadHandoffData.fileCount -lt 1){
    throw 'Payload snapshot handoff identity is invalid.'
}
$payload=[IO.Path]::GetFullPath([string]$payloadHandoffData.snapshotRoot)
$payloadManifest=Join-Path $payload 'SHA256SUMS.txt'
$snapshotManifestSha256=[string]$payloadHandoffData.manifestSha256
$snapshotEntriesDigest=[string]$payloadHandoffData.entriesDigest
$snapshotFileCount=[int]$payloadHandoffData.fileCount
$null=Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $payload `
    -ManifestSha256 $snapshotManifestSha256 -EntriesDigest $snapshotEntriesDigest `
    -FileCount $snapshotFileCount
$restoreGateArguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$restoreGraphGate,'-Root',$root)
if($hasFrozenRestoreGraphReceipt){
    $validatedRestoreReceipt=Test-SwSearchFrozenRestoreGraphReceipt -Root $root -TransactionRoot $expectedTransactionBuildRoot `
        -SourceTransactionNonce $TransactionNonce -ReceiptPath $FrozenRestoreGraphReceipt `
        -ReceiptSha256 $FrozenRestoreGraphReceiptSha256
    Write-Output "FROZEN_RESTORE_GRAPH_RECEIPT: manifest=$($validatedRestoreReceipt.ManifestSha256); utc=$($validatedRestoreReceipt.ValidatedUtc.ToString('o'))"
}else{
    & powershell @restoreGateArguments
    if($LASTEXITCODE -ne 0){ throw 'Frozen zero-restore graph preflight failed during MSI verification.' }
}
$PreviousMsi=[IO.Path]::GetFullPath($PreviousMsi)
if(-not $PreviousMsi.Equals([IO.Path]::GetFullPath($expectedPreviousMsi),[StringComparison]::OrdinalIgnoreCase)){
    throw "PreviousMsi must be the fixed RC12 artifact: $expectedPreviousMsi"
}
Assert-FixedFile $PreviousMsi $expectedPreviousBytes $expectedPreviousSha 'RC12 immutable pre-verification gate'
Assert-FixedFile $expectedRc15Msi $expectedRc15Bytes $expectedRc15Sha 'RC15 immutable pre-verification gate'
if(-not (Test-Path -LiteralPath $InstallerActionsManifest -PathType Leaf)){ throw "Installer action manifest missing: $InstallerActionsManifest" }
if(-not (Test-Path -LiteralPath $BridgeGate -PathType Leaf)){ throw "Isolated bridge gate missing: $BridgeGate" }
if(-not (Test-Path -LiteralPath $CommandLineGate -PathType Leaf)){ throw "Installer command-line gate missing: $CommandLineGate" }
$actionManifest=Get-Content -LiteralPath $InstallerActionsManifest -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-ExactJsonProperties $actionManifest @('schema','runtime','coordinator','bridge','bridgeGate','commandLineGate') 'Installer action manifest'
Assert-ExactJsonProperties $actionManifest.coordinator @('file','bytes','sha256') 'Coordinator manifest entry'
Assert-ExactJsonProperties $actionManifest.bridge @('file','bytes','sha256') 'Bridge manifest entry'
Assert-ExactJsonProperties $actionManifest.bridgeGate @('file','bytes','sha256') 'Bridge gate manifest entry'
Assert-ExactJsonProperties $actionManifest.commandLineGate @('file','bytes','sha256') 'Command-line gate manifest entry'
if($actionManifest.schema -ne 2 -or $actionManifest.runtime -cne 'win-x64'){ throw 'Installer action manifest schema/runtime mismatch.' }
if($actionManifest.coordinator.file -cne 'SwSearch.InstallerCoordinator.exe' -or
   $actionManifest.bridge.file -cne 'SwSearch.InstallerMsiBridge.dll'){
    throw 'Installer action manifest file names are not exact.'
}
if($actionManifest.bridgeGate.file -cne 'SwSearch.InstallerMsiBridgeGate.exe'){ throw 'Installer bridge gate manifest identity mismatch.' }
if($actionManifest.commandLineGate.file -cne 'SwSearch.InstallerMsiCommandLineGate.exe'){ throw 'Installer command-line gate manifest identity mismatch.' }
Assert-FixedFile $BridgeGate ([long]$actionManifest.bridgeGate.bytes) ([string]$actionManifest.bridgeGate.sha256) 'Isolated bridge gate'
Assert-FixedFile $CommandLineGate ([long]$actionManifest.commandLineGate.bytes) ([string]$actionManifest.commandLineGate.sha256) 'Installer command-line gate'
$candidateInfo=Get-Item -LiteralPath $msi
if($UseControlledSourceReceipt){
    # Build-Release already performed the transaction's first full source
    # snapshot.  Avoid hashing it again here; the unconditional final full
    # freeze below remains the publication safety gate.
    $initialFreeze=Test-SwSearchControlledSourceManifestReceipt -Root $root -ManifestPath $ControlledManifest `
        -CandidateLastWriteTimeUtc $candidateInfo.LastWriteTimeUtc
    Write-Output "CONTROLLED_SOURCE_RECEIPT: files=$($initialFreeze.ControlledFileCount); manifest=$($initialFreeze.ManifestSha256)"
}else{
    $initialFreeze=Test-SwSearchControlledSourceManifest -Root $root -ManifestPath $ControlledManifest `
        -CandidateLastWriteTimeUtc $candidateInfo.LastWriteTimeUtc
}

$bytes=[IO.File]::ReadAllBytes($msi)
$ole=[byte[]](0xD0,0xCF,0x11,0xE0,0xA1,0xB1,0x1A,0xE1)
for($i=0;$i -lt $ole.Length;$i++){ if($bytes[$i] -ne $ole[$i]){ throw 'MSI OLE header is invalid.' } }
New-Item -ItemType Directory -Path $inspection | Out-Null
& $wix msi decompile $msi -o $decompiled -x $export
if($LASTEXITCODE -ne 0){ throw 'WiX decompile/CAB export failed.' }
$xml=Get-Content -LiteralPath $decompiled -Raw -Encoding UTF8
foreach($required in @(
    "ProductCode=`"{$ProductCode}`"", "UpgradeCode=`"$upgradeCode`"", "Version=`"$Version`"",
    '<StandardDirectory Id="ProgramFiles64Folder">', 'SwSearch.Broker.exe', 'SwSearch.PropertyConfig.dll', 'SwSearch.SolidWorksAddin.dll',
    "SOFTWARE\SolidWorks\Addins\$addinClsid", "Software\SolidWorks\AddInsStartup\$addinClsid",
    'SwSearch.SearchHere', 'LEGACY_PERUSER_INSTALL', 'WindowsBuild &lt; 22000')){
    if(-not $xml.Contains($required)){ throw "MSI structure missing: $required" }
}
foreach($forbidden in @('SwSearch.Indexer.exe','SldWorks.Application','TerminateProcess','taskkill','Stop-Process','.7z','CurrentVersion\Run','.pfx','.p12','.pvk')){
    if($xml.IndexOf($forbidden,[StringComparison]::OrdinalIgnoreCase) -ge 0){ throw "Forbidden or unavailable MSI content found: $forbidden" }
}

Add-Type -Path $dtf
$previousDatabase=[WixToolset.Dtf.WindowsInstaller.Database]::new($PreviousMsi,[WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly)
try{
    $previousProperties=@{}
    $view=$previousDatabase.OpenView('SELECT `Property`,`Value` FROM `Property`'); $view.Execute()
    while($record=$view.Fetch()){ $previousProperties[$record.GetString(1)]=$record.GetString(2); $record.Dispose() }; $view.Dispose()
    if($previousProperties.ProductCode -cne $expectedPreviousProductCode -or
       $previousProperties.UpgradeCode -cne $upgradeCode -or
       $previousProperties.ProductVersion -cne '1.2.0' -or
       $previousProperties.ALLUSERS -cne '1'){
        throw 'RC12 immutable MSI table identity mismatch.'
    }
    $previousFileRows=0
    $view=$previousDatabase.OpenView('SELECT `File` FROM `File`'); $view.Execute()
    while($record=$view.Fetch()){ $previousFileRows++; $record.Dispose() }; $view.Dispose()
    if($previousFileRows -ne 493){ throw "RC12 immutable File table count mismatch: $previousFileRows" }
}finally{ $previousDatabase.Dispose() }

$rc15ComponentId=$null
$rc15ComponentAttributes=$null
$rc15ComponentKeyPath=$null
$rc15KeyPathRegistryRow=$null
$rc15Database=[WixToolset.Dtf.WindowsInstaller.Database]::new($expectedRc15Msi,[WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly)
try{
    $rc15Properties=@{}
    $view=$rc15Database.OpenView('SELECT `Property`,`Value` FROM `Property`'); $view.Execute()
    while($record=$view.Fetch()){ $rc15Properties[$record.GetString(1)]=$record.GetString(2); $record.Dispose() }; $view.Dispose()
    if($rc15Properties.ProductCode -cne $expectedRc15ProductCode -or
       $rc15Properties.UpgradeCode -cne $upgradeCode -or
       $rc15Properties.ProductVersion -cne '1.2.0' -or
       $rc15Properties.ALLUSERS -cne '1'){
        throw 'RC15 immutable MSI table identity mismatch.'
    }
    $rc15FileRows=0
    $view=$rc15Database.OpenView('SELECT `File` FROM `File`'); $view.Execute()
    while($record=$view.Fetch()){ $rc15FileRows++; $record.Dispose() }; $view.Dispose()
    if($rc15FileRows -ne 493){ throw "RC15 immutable File table count mismatch: $rc15FileRows" }

    $view=$rc15Database.OpenView("SELECT `ComponentId`,`Directory_`,`Attributes`,`KeyPath` FROM `Component` WHERE `Component`='SolidWorksAddinMachineRegistration'")
    $view.Execute(); $record=$view.Fetch()
    if($null -eq $record){ throw 'RC15 add-in registry component is missing.' }
    $rc15ComponentId=$record.GetString(1)
    $rc15ComponentDirectory=$record.GetString(2)
    $rc15ComponentAttributes=$record.GetInteger(3)
    $rc15ComponentKeyPath=$record.GetString(4)
    $record.Dispose(); $view.Dispose()
    if($rc15ComponentId -cne '{803F2915-9AAE-4B38-861B-A90D723FF571}' -or
       $rc15ComponentDirectory -cne 'INSTALLFOLDER' -or
       ($rc15ComponentAttributes -band 0x100) -ne 0x100 -or
       [string]::IsNullOrWhiteSpace($rc15ComponentKeyPath)){
        throw 'RC15 add-in registry component baseline is not stable/64-bit.'
    }
    $rc15RegistryRows=@()
    $view=$rc15Database.OpenView('SELECT `Registry`,`Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`'); $view.Execute()
    while($record=$view.Fetch()){
        $rc15RegistryRows += [pscustomobject]@{
            Id=$record.GetString(1);Root=$record.GetInteger(2);Key=$record.GetString(3)
            Name=if($record.IsNull(4)){$null}else{$record.GetString(4)}
            Value=if($record.IsNull(5)){$null}else{$record.GetString(5)}
            Component=$record.GetString(6)
        }
        $record.Dispose()
    }; $view.Dispose()
    $rc15KeyPathRows=@($rc15RegistryRows | Where-Object { $_.Id -ceq $rc15ComponentKeyPath })
    if($rc15KeyPathRows.Count -ne 1){ throw 'RC15 add-in Registry key path row is not unique.' }
    $rc15KeyPathRegistryRow=$rc15KeyPathRows[0]
    if($rc15KeyPathRegistryRow.Root -ne 2 -or
       $rc15KeyPathRegistryRow.Key -cne "SOFTWARE\SolidWorks\Addins\$addinClsid" -or
       $null -ne $rc15KeyPathRegistryRow.Name -or
       $rc15KeyPathRegistryRow.Value -cne '#0' -or
       $rc15KeyPathRegistryRow.Component -cne 'SolidWorksAddinMachineRegistration'){
        throw 'RC15 add-in Registry key path six-tuple baseline changed.'
    }
    $rc15InprocRows=@($rc15RegistryRows | Where-Object {
        $_.Component -ceq 'SolidWorksAddinMachineRegistration' -and
        $_.Key -ceq "Software\Classes\CLSID\$addinClsid\InprocServer32" -and
        $null -eq $_.Name
    })
    if($rc15InprocRows.Count -ne 1 -or
       $rc15InprocRows[0].Value -cne '[WindowsFolder]Microsoft.NET\Framework64\v4.0.30319\mscoree.dll'){
        throw 'RC15 broken Framework64 COM registration baseline changed unexpectedly.'
    }
}finally{ $rc15Database.Dispose() }

$database=[WixToolset.Dtf.WindowsInstaller.Database]::new($msi,[WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly)
try{
    $properties=@{}
    $view=$database.OpenView('SELECT `Property`,`Value` FROM `Property`'); $view.Execute()
    while($record=$view.Fetch()){ $properties[$record.GetString(1)]=$record.GetString(2); $record.Dispose() }; $view.Dispose()
    if($properties.ProductVersion -cne $Version){ throw "ProductVersion mismatch: $($properties.ProductVersion)" }
    if($properties.ProductCode -cne "{$ProductCode}"){ throw "ProductCode mismatch: $($properties.ProductCode)" }
    if($retiredProductCodes -contains [Guid]::Parse($properties.ProductCode)){
        throw "$($ReleaseCandidate.ToUpperInvariant()) must not reuse a retired ProductCode: $($properties.ProductCode)"
    }
    if($properties.UpgradeCode -cne $upgradeCode){ throw "UpgradeCode mismatch: $($properties.UpgradeCode)" }
    if($properties.ALLUSERS -ne '1'){ throw 'Package is not per-machine (ALLUSERS=1).' }
    if($properties.ContainsKey('MSIRESTARTMANAGERCONTROL') -and $properties.MSIRESTARTMANAGERCONTROL -ceq 'Disable'){ throw 'Restart Manager must remain enabled so locked App files are handled before InstallFiles.' }
    if($properties.REBOOT -ne 'ReallySuppress'){ throw 'Installer must not force an automatic reboot.' }
    $hidden=@([string]$properties.MsiHiddenProperties -split ';')
    foreach($propertyName in @('SWSEARCHTRUSTEDSID','SWSEARCHTRUSTEDSESSIONID','SWSEARCHSESSIONNONCE','SWSEARCHDEADLINETICK','SWSEARCHINSTALLACTION','SWSEARCHTRUSTEDROOTPOLICY','SWSEARCHCONTEXTREADY','SWSEARCHMACHINECONTEXTREADY','SwSearchPrivilegedHandle')){
        if($hidden -notcontains $propertyName){ throw "Sensitive installer context is not hidden: $propertyName" }
    }

    $upgradeRows=@()
    $view=$database.OpenView('SELECT `UpgradeCode`,`VersionMin`,`VersionMax`,`Language`,`Attributes`,`Remove`,`ActionProperty` FROM `Upgrade`'); $view.Execute()
    while($record=$view.Fetch()){
        $upgradeRows += [pscustomobject]@{
            UpgradeCode=$record.GetString(1)
            VersionMin=if($record.IsNull(2)){$null}else{$record.GetString(2)}
            VersionMax=if($record.IsNull(3)){$null}else{$record.GetString(3)}
            Language=if($record.IsNull(4)){$null}else{$record.GetString(4)}
            Attributes=$record.GetInteger(5)
            Remove=if($record.IsNull(6)){$null}else{$record.GetString(6)}
            ActionProperty=$record.GetString(7)
        }
        $record.Dispose()
    }; $view.Dispose()
    if($upgradeRows.Count -ne 2){ throw "Upgrade table exact row count mismatch: $($upgradeRows.Count)" }
    $upgradeDetected=@($upgradeRows | Where-Object ActionProperty -ceq 'WIX_UPGRADE_DETECTED')
    $downgrade=@($upgradeRows | Where-Object ActionProperty -ceq 'WIX_DOWNGRADE_DETECTED')
    if($upgradeDetected.Count -ne 1 -or
       $upgradeDetected[0].UpgradeCode -cne $upgradeCode -or
       $null -ne $upgradeDetected[0].VersionMin -or
       $upgradeDetected[0].VersionMax -cne $Version -or
       $upgradeDetected[0].Language -cne '2052' -or
       $upgradeDetected[0].Attributes -ne 1 -or
       $null -ne $upgradeDetected[0].Remove){
        throw 'Upgrade table must detect all older products below the current ProductVersion.'
    }
    if($downgrade.Count -ne 1 -or
       $downgrade[0].UpgradeCode -cne $upgradeCode -or
       $downgrade[0].VersionMin -cne $Version -or
       $null -ne $downgrade[0].VersionMax -or
       $downgrade[0].Language -cne '2052' -or
       $downgrade[0].Attributes -ne 2 -or
       $null -ne $downgrade[0].Remove){
        throw 'Upgrade table downgrade protection changed.'
    }

    $binaryRows=@{}
    $binaryGateDirectory=Join-Path $inspection 'binary-entry-gate'
    New-Item -ItemType Directory -Path $binaryGateDirectory | Out-Null
    $view=$database.OpenView('SELECT `Name`,`Data` FROM `Binary`'); $view.Execute()
    while($record=$view.Fetch()){
        $name=$record.GetString(1)
        $stream=$record.GetStream(2)
        try{
            $fileName=switch($name){
                'SwSearchInstallerCoordinator' { 'SwSearch.InstallerCoordinator.exe' }
                'SwSearchInstallerMsiBridge' { 'SwSearch.InstallerMsiBridge.dll' }
                default { 'Unexpected_' + (Get-StableHashHex $name) + '.bin' }
            }
            $extractedPath=Join-Path $binaryGateDirectory $fileName
            $outputStream=[IO.File]::Open($extractedPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try{ $stream.CopyTo($outputStream) }finally{ $outputStream.Dispose() }
            $extractedInfo=Get-Item -LiteralPath $extractedPath
            $hash=(Get-FileHash -LiteralPath $extractedPath -Algorithm SHA256).Hash
            $binaryRows[$name]=[pscustomobject]@{ Name=$name; Bytes=$extractedInfo.Length; SHA256=$hash; Path=$extractedPath }
        }finally{ $stream.Dispose(); $record.Dispose() }
    }; $view.Dispose()
    $expectedBinaryNames=@(
        'SwSearchInstallerCoordinator','SwSearchInstallerMsiBridge',
        'Wix4UtilCA_X64','WixUiCa_X64','WixUI_Bmp_Banner','WixUI_Bmp_Dialog',
        'WixUI_Ico_Exclam','WixUI_Ico_Info','WixUI_Bmp_New','WixUI_Bmp_Up'
    )
    if($binaryRows.Count -ne $expectedBinaryNames.Count -or @($binaryRows.Keys | Where-Object { $_ -notin $expectedBinaryNames }).Count -ne 0){
        throw "Binary table exact-set mismatch: $($binaryRows.Keys -join ', ')"
    }
    $expectedBinaryHashes=@{
        SwSearchInstallerCoordinator=[string]$actionManifest.coordinator.sha256
        SwSearchInstallerMsiBridge=[string]$actionManifest.bridge.sha256
    }
    foreach($binaryName in $expectedBinaryHashes.Keys){
        if(-not $binaryRows.ContainsKey($binaryName)){ throw "Embedded installer Binary row missing: $binaryName" }
        if($binaryRows[$binaryName].SHA256 -cne $expectedBinaryHashes[$binaryName]){ throw "Embedded installer Binary hash mismatch: $binaryName" }
    }
    if($binaryRows.SwSearchInstallerCoordinator.Bytes -ne [long]$actionManifest.coordinator.bytes -or
       $binaryRows.SwSearchInstallerMsiBridge.Bytes -ne [long]$actionManifest.bridge.bytes){
        throw 'Embedded installer Binary length mismatch.'
    }
    $coordinatorEntry=[string]$binaryRows.SwSearchInstallerCoordinator.Path
    $bridgeEntry=[string]$binaryRows.SwSearchInstallerMsiBridge.Path
    $bridgeFacts=Get-PeImageFacts $bridgeEntry
    if($bridgeFacts.Machine -ne 0x8664 -or
       $bridgeFacts.Exports -notcontains 'CaptureTrustedInstallerContext' -or
       $bridgeFacts.Exports -notcontains 'CheckSwSearchDotNet8' -or
       $bridgeFacts.Exports -notcontains 'SwSearchInstallerTokenPolicySelfTest'){
        throw 'Extracted MSI bridge is not the expected x64 exported entry point.'
    }
    $allowedBridgeImports=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($name in @('KERNEL32.dll','ADVAPI32.dll','bcrypt.dll','CRYPT32.dll','MSI.dll')){ [void]$allowedBridgeImports.Add($name) }
    foreach($name in @($bridgeFacts.Imports)){
        if(-not $allowedBridgeImports.Contains([string]$name)){ throw "MSI bridge import is outside the allowlist: $name" }
    }
    foreach($requiredImport in @('ADVAPI32.dll','bcrypt.dll','CRYPT32.dll','MSI.dll')){
        if(@($bridgeFacts.Imports) -notcontains $requiredImport){ throw "MSI bridge required import is missing: $requiredImport" }
    }
    $entryHashesBefore=@{
        Coordinator=(Get-FileHash -LiteralPath $coordinatorEntry -Algorithm SHA256).Hash
        Bridge=(Get-FileHash -LiteralPath $bridgeEntry -Algorithm SHA256).Hash
        BridgeGate=(Get-FileHash -LiteralPath $BridgeGate -Algorithm SHA256).Hash
        CommandLineGate=(Get-FileHash -LiteralPath $CommandLineGate -Algorithm SHA256).Hash
    }
    Invoke-BoundedIsolatedProcess $coordinatorEntry @('--self-test') 15000 'Extracted coordinator self-test'
    Invoke-BoundedIsolatedProcess $BridgeGate @($bridgeEntry,$entryHashesBefore.Bridge) 15000 'Extracted bridge invalid-handle fail-closed gate'
    $entryHashesAfter=@{
        Coordinator=(Get-FileHash -LiteralPath $coordinatorEntry -Algorithm SHA256).Hash
        Bridge=(Get-FileHash -LiteralPath $bridgeEntry -Algorithm SHA256).Hash
        BridgeGate=(Get-FileHash -LiteralPath $BridgeGate -Algorithm SHA256).Hash
        CommandLineGate=(Get-FileHash -LiteralPath $CommandLineGate -Algorithm SHA256).Hash
    }
    foreach($name in $entryHashesBefore.Keys){
        if($entryHashesBefore[$name] -cne $entryHashesAfter[$name]){ throw "Installer Binary entry gate changed bytes: $name" }
    }

    $customActions=@{}
    $view=$database.OpenView('SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`'); $view.Execute()
    while($record=$view.Fetch()){
        $action=$record.GetString(1)
        $customActions[$action]=[pscustomobject]@{
            Action=$action; Type=$record.GetInteger(2)
            Source=if($record.IsNull(3)){$null}else{$record.GetString(3)}
            Target=if($record.IsNull(4)){$null}else{$record.GetString(4)}
        }
        $record.Dispose()
    }; $view.Dispose()
    if($customActions.ContainsKey('FailIfSwSearchStillRunning')){ throw 'Fixed four-component fatal custom action must not exist.' }
    function Assert-CustomActionType([string]$Name,[int]$Base,[int]$Required,[int]$Allowed,[int]$ReturnBits){
        if(-not $customActions.ContainsKey($Name)){ throw "CustomAction missing: $Name" }
        $type=[int]$customActions[$Name].Type
        if(($type -band 0x3F) -ne $Base -or ($type -band $Required) -ne $Required -or ($type -band $Allowed) -ne $type){
            throw "CustomAction type/context mismatch: $Name type=$type"
        }
        if(($type -band 0xC0) -ne $ReturnBits){ throw "CustomAction return policy mismatch: $Name type=$type expectedReturnBits=$ReturnBits" }
    }
    Assert-CustomActionType 'CheckSwSearchDotNet8' 1 0 0x1 0
    Assert-CustomActionType 'CaptureSwSearchTrustedContext' 1 0 0x1 0
    Assert-CustomActionType 'SwSearchUserIpc' 2 0x2040 0x2042 0x40
    Assert-CustomActionType 'SetSwSearchPrivilegedHandleData' 51 0x2000 0x2033 0
    Assert-CustomActionType 'SwSearchPrivilegedHandle' 2 0x2C40 0x2C42 0x40
    if($customActions.CheckSwSearchDotNet8.Source -cne 'SwSearchInstallerMsiBridge' -or
       $customActions.CheckSwSearchDotNet8.Target -cne 'CheckSwSearchDotNet8'){
        throw 'Native .NET 8 prerequisite custom action is cross-wired.'
    }
    if($customActions.CaptureSwSearchTrustedContext.Source -cne 'SwSearchInstallerMsiBridge' -or
       $customActions.CaptureSwSearchTrustedContext.Target -cne 'CaptureTrustedInstallerContext'){
        throw 'Trusted context bridge entry point is not bound to its Binary row.'
    }
    $expectedUserTarget='--user-ipc --upgrade-code "{D9158356-29B3-44D6-A15A-ACDCE8328703}" --current-root "[SWSEARCHTRUSTEDROOTPOLICY]" --log "[TempFolder]SwSearch-InstallerCoordinator.log" --install-user-sid "[SWSEARCHTRUSTEDSID]" --install-session-id "[SWSEARCHTRUSTEDSESSIONID]" --instance-nonce "[SWSEARCHSESSIONNONCE]" --install-action "[SWSEARCHINSTALLACTION]" --deadline-tick "[SWSEARCHDEADLINETICK]"'
    if($customActions.SwSearchUserIpc.Source -cne 'SwSearchInstallerCoordinator' -or
       $customActions.SwSearchUserIpc.Target -cne $expectedUserTarget){
        throw 'Immediate coordinator must be the isolated user-ipc entry point.'
    }
    if($customActions.SwSearchPrivilegedHandle.Source -cne 'SwSearchInstallerCoordinator' -or
       $customActions.SwSearchPrivilegedHandle.Target -cne '--privileged-handle --custom-action-data "[SwSearchPrivilegedHandle]"'){
        throw 'Deferred coordinator must be the isolated privileged-handle CAD entry point.'
    }
    $expectedCad='v=2;user=0;sid=;session=;upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703};nonce=;root=[SWSEARCHTRUSTEDROOTPOLICY];action=[SWSEARCHINSTALLACTION];deadline=[SWSEARCHDEADLINETICK]'
    if($customActions.SetSwSearchPrivilegedHandleData.Source -cne 'SwSearchPrivilegedHandle' -or
       $customActions.SetSwSearchPrivilegedHandleData.Target -cne $expectedCad){
        throw 'Privileged CustomActionData source/target is not exact.'
    }
    foreach($cadToken in @('v=2','user=0','sid=','session=','upgrade={D9158356-29B3-44D6-A15A-ACDCE8328703}','nonce=','root=[SWSEARCHTRUSTEDROOTPOLICY]','action=[SWSEARCHINSTALLACTION]','deadline=[SWSEARCHDEADLINETICK]')){
        if($customActions.SetSwSearchPrivilegedHandleData.Target.IndexOf($cadToken,[StringComparison]::Ordinal) -lt 0){ throw "CustomActionData token missing: $cadToken" }
    }

    $sequence=@{}
    $view=$database.OpenView('SELECT `Action`,`Condition`,`Sequence` FROM `InstallExecuteSequence`'); $view.Execute()
    while($record=$view.Fetch()){
        $action=$record.GetString(1)
        $sequence[$action]=[pscustomobject]@{ Condition=if($record.IsNull(2)){$null}else{$record.GetString(2)}; Sequence=$record.GetInteger(3) }
        $record.Dispose()
    }; $view.Dispose()
    foreach($action in @('CheckSwSearchDotNet8','LaunchConditions','FindRelatedProducts','CaptureSwSearchTrustedContext','SwSearchUserIpc','SetSwSearchPrivilegedHandleData','SwSearchPrivilegedHandle','InstallValidate','InstallInitialize','InstallFiles','WriteRegistryValues','InstallExecute','RemoveExistingProducts','InstallFinalize')){
        if(-not $sequence.ContainsKey($action)){ throw "InstallExecuteSequence row missing: $action" }
    }
    if(-not ($sequence.FindRelatedProducts.Sequence -lt $sequence.CaptureSwSearchTrustedContext.Sequence -and
             $sequence.CaptureSwSearchTrustedContext.Sequence -lt $sequence.SwSearchUserIpc.Sequence -and
             $sequence.SwSearchUserIpc.Sequence -lt $sequence.InstallValidate.Sequence)){
        throw 'Current-user capture/IPC actions must run before InstallValidate.'
    }
    if(-not ($sequence.SetSwSearchPrivilegedHandleData.Sequence -lt $sequence.InstallInitialize.Sequence -and
             $sequence.InstallInitialize.Sequence + 1 -eq $sequence.SwSearchPrivilegedHandle.Sequence -and
             $sequence.SwSearchPrivilegedHandle.Sequence -lt $sequence.InstallFiles.Sequence -and
             $sequence.SwSearchPrivilegedHandle.Sequence -lt $sequence.RemoveExistingProducts.Sequence)){
        throw 'Privileged CAD must precede InstallInitialize; deferred action must immediately follow it and precede InstallFiles/RemoveExistingProducts.'
    }
    if(-not ($sequence.InstallFiles.Sequence -lt $sequence.WriteRegistryValues.Sequence -and
             $sequence.WriteRegistryValues.Sequence -lt $sequence.InstallExecute.Sequence -and
             $sequence.InstallExecute.Sequence -lt $sequence.RemoveExistingProducts.Sequence -and
             $sequence.RemoveExistingProducts.Sequence -lt $sequence.InstallFinalize.Sequence)){
        throw 'Same-version major upgrade must write RC19 files/registry before removing RC15 and finalize afterward.'
    }
    if($sequence.CheckSwSearchDotNet8.Sequence -ge $sequence.LaunchConditions.Sequence){ throw 'Native .NET 8 check must precede execute LaunchConditions.' }
    $lifecycleCondition='NOT MSIPATCHREMOVE AND NOT UPGRADINGPRODUCTCODE AND (NOT Installed OR REINSTALL OR REMOVE~="ALL" OR (Installed AND NOT REMOVE~="ALL"))'
    $userContextCondition='SWSEARCHCONTEXTREADY = "1" AND ' + $lifecycleCondition
    $machineContextCondition='SWSEARCHMACHINECONTEXTREADY = "1" AND ' + $lifecycleCondition
    if($sequence.SwSearchUserIpc.Condition -cne $userContextCondition -or
       $sequence.SetSwSearchPrivilegedHandleData.Condition -cne $machineContextCondition -or
       $sequence.SwSearchPrivilegedHandle.Condition -cne $machineContextCondition){
        throw 'User and machine installer context conditions are not safely separated.'
    }
    if($sequence.CaptureSwSearchTrustedContext.Condition -cne $lifecycleCondition){
        throw 'Trusted-context capture condition matrix changed.'
    }

    # The native gate re-reads these exact final-MSI Type 51/Type 2 rows and
    # verifies Windows Installer formatting plus Win32 argv boundaries. Its
    # deferred property token is provisional until package_semantics review
    # confirms the actual deferred/no-impersonate visibility contract.
    $commandGateHashBefore=(Get-FileHash -LiteralPath $CommandLineGate -Algorithm SHA256).Hash
    $commandGateMsiHashBefore=(Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
    Invoke-BoundedInstallerCommandLineGate $CommandLineGate $msi 30000 `
        'Final MSI privileged command-line gate'
    $commandGateHashAfter=(Get-FileHash -LiteralPath $CommandLineGate -Algorithm SHA256).Hash
    $commandGateMsiHashAfter=(Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
    if($commandGateHashBefore -cne $commandGateHashAfter -or
       $commandGateMsiHashBefore -cne $commandGateMsiHashAfter){
        throw 'Final MSI command-line gate changed its executable or candidate bytes.'
    }
    [IO.File]::WriteAllLines($commandLineGateVerifyLog,@(
        'SwSearch final MSI command-line verification',
        'Mode: verify-msi',
        "GateSHA256Before: $commandGateHashBefore",
        "GateSHA256After: $commandGateHashAfter",
        "MsiSHA256Before: $commandGateMsiHashBefore",
        "MsiSHA256After: $commandGateMsiHashAfter",
        "Type51Source: $($customActions.SetSwSearchPrivilegedHandleData.Source)",
        "Type51Target: $($customActions.SetSwSearchPrivilegedHandleData.Target)",
        "Type2Source: $($customActions.SwSearchPrivilegedHandle.Source)",
        "Type2Target: $($customActions.SwSearchPrivilegedHandle.Target)",
        'Result: PASS'
    ),[Text.UTF8Encoding]::new($false))

    $registry=@()
    $view=$database.OpenView('SELECT `Registry`,`Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`'); $view.Execute()
    while($record=$view.Fetch()){
        $registry += [pscustomobject]@{ Id=$record.GetString(1); Root=$record.GetInteger(2); Key=$record.GetString(3); Name=if($record.IsNull(4)){$null}else{$record.GetString(4)}; Value=if($record.IsNull(5)){$null}else{$record.GetString(5)}; Component=$record.GetString(6) }
        $record.Dispose()
    }; $view.Dispose()
    foreach($key in @(
        "SOFTWARE\SolidWorks\Addins\$addinClsid",
        "Software\Classes\CLSID\$addinClsid\InprocServer32",
        "Software\SolidWorks\AddInsStartup\$addinClsid",
        'Software\Classes\Directory\shell\SwSearch.SearchHere\command',
        'Software\Classes\Directory\Background\shell\SwSearch.SearchHere\command')){
        if(-not ($registry | Where-Object Key -ceq $key)){ throw "Registry table missing: $key" }
    }
    $comRows=@($registry | Where-Object Key -like "Software\Classes\CLSID\$addinClsid\InprocServer32*")
    foreach($valueName in @('Class','Assembly','RuntimeVersion','CodeBase')){ if(-not ($comRows | Where-Object Name -ceq $valueName)){ throw "Managed COM registration missing value: $valueName" } }
    if(-not ($registry | Where-Object { $_.Key -ceq "SOFTWARE\SolidWorks\Addins\$addinClsid" -and $_.Name -ceq 'Title' })){ throw 'SOLIDWORKS Add-in Title is missing.' }

    $inprocDefault=@($registry | Where-Object {
        $_.Key -ceq "Software\Classes\CLSID\$addinClsid\InprocServer32" -and $null -eq $_.Name
    })
    if($inprocDefault.Count -ne 1 -or
       $inprocDefault[0].Root -ne 2 -or
       $inprocDefault[0].Value -cne '[System64Folder]mscoree.dll' -or
       $inprocDefault[0].Component -cne 'SolidWorksAddinMachineRegistration'){
        throw '64-bit CLR COM InprocServer32 must be the exact System64 mscoree.dll formatted path.'
    }
    if(@($registry | Where-Object {
        $_.Value -and $_.Value.IndexOf('Framework64\v4.0.30319\mscoree.dll',[StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count -ne 0){
        throw 'Obsolete/nonexistent Framework64 mscoree.dll registration remains in the MSI Registry table.'
    }
    $systemMscoree=Join-Path $env:WINDIR 'System32\mscoree.dll'
    if(-not (Test-Path -LiteralPath $systemMscoree -PathType Leaf)){ throw 'Formatted System64 mscoree.dll target does not exist on the build host.' }
    if((Get-PeImageFacts $systemMscoree).Machine -ne 0x8664){ throw 'Formatted System64 mscoree.dll target is not AMD64.' }

    $conditions=@{}
    $componentRows=@{}
    $view=$database.OpenView('SELECT `Component`,`ComponentId`,`Directory_`,`Attributes`,`Condition`,`KeyPath` FROM `Component`'); $view.Execute()
    while($record=$view.Fetch()){
        $name=$record.GetString(1)
        $componentRows[$name]=[pscustomobject]@{
            ComponentId=if($record.IsNull(2)){$null}else{$record.GetString(2)}
            Directory=$record.GetString(3)
            Attributes=$record.GetInteger(4)
            Condition=if($record.IsNull(5)){$null}else{$record.GetString(5)}
            KeyPath=if($record.IsNull(6)){$null}else{$record.GetString(6)}
        }
        $conditions[$name]=$componentRows[$name].Condition
        $record.Dispose()
    }; $view.Dispose()
    $legacyPayloadComponents=@(
        [pscustomobject]@{
            Id='PayloadFile_F09390D3638883F5817AA572'; ComponentId='{F09390D3-6388-83F5-817A-A57223873C84}'
            KeyPath='regG83UU7JrIdz9hur35.oeX_hpfpU'; Name='F_F09390D3638883F5817AA57223873C848CBE8426'
        },
        [pscustomobject]@{
            Id='PayloadFile_878F2264C2CF97C26E7A6A0D'; ComponentId='{878F2264-C2CF-97C2-6E7A-6A0D06BBDE1D}'
            KeyPath='regogoG8n0Z.UhtEJ56fEocm7coZ.s'; Name='F_878F2264C2CF97C26E7A6A0D06BBDE1D08E532A1'
        },
        [pscustomobject]@{
            Id='PayloadFile_113F2D491E1B31F10B8A2C8C'; ComponentId='{113F2D49-1E1B-31F1-0B8A-2C8CB0B5BA83}'
            KeyPath='regtxNje__.h9bp6p2QlQQGdFxLcMY'; Name='F_113F2D491E1B31F10B8A2C8CB0B5BA83A0052582'
        }
    )
    foreach($expected in $legacyPayloadComponents){
        if(-not $componentRows.ContainsKey($expected.Id)){ throw "Frozen legacy payload component is missing: $($expected.Id)" }
        $component=$componentRows[$expected.Id]
        if($component.ComponentId -cne $expected.ComponentId -or
           $component.Directory -cne 'INSTALLFOLDER' -or
           $component.Attributes -ne 260 -or
           $component.KeyPath -cne $expected.KeyPath){
            throw "Frozen legacy payload Component six-tuple changed: $($expected.Id)"
        }
        $registryRow=@($registry | Where-Object Id -ceq $expected.KeyPath)
        if($registryRow.Count -ne 1 -or
           $registryRow[0].Root -ne 1 -or
           $registryRow[0].Key -cne 'Software\SwSearch\Installer\Payload' -or
           $registryRow[0].Name -cne $expected.Name -or
           $registryRow[0].Value -cne '#1' -or
           $registryRow[0].Component -cne $expected.Id){
            throw "Frozen legacy payload HKCU Registry KeyPath changed: $($expected.Id)"
        }
    }
    if(-not $componentRows.ContainsKey('SolidWorksAddinMachineRegistration')){ throw 'Add-in registry component row is missing.' }
    $addinComponent=$componentRows.SolidWorksAddinMachineRegistration
    if($addinComponent.ComponentId -cne $rc15ComponentId -or
       $addinComponent.Directory -cne 'INSTALLFOLDER' -or
       $addinComponent.Attributes -ne $rc15ComponentAttributes -or
       ($addinComponent.Attributes -band 0x100) -ne 0x100 -or
       [string]::IsNullOrWhiteSpace($addinComponent.KeyPath) -or
       $addinComponent.KeyPath -cne $rc15ComponentKeyPath){
        throw 'RC19 add-in registry component identity/64-bit key path is not upgrade-stable.'
    }
    if(-not $componentRows.ContainsKey('SwSearchProductFileAnchor')){ throw 'Per-machine product file anchor component is missing.' }
    $productAnchor=$componentRows.SwSearchProductFileAnchor
    if($productAnchor.ComponentId -cne '{6D2AD688-FFB4-4B79-99A7-AB07DE8291D6}' -or
       $productAnchor.Directory -cne 'INSTALLFOLDER' -or
       ($productAnchor.Attributes -band 0x100) -ne 0x100 -or
       ($productAnchor.Attributes -band 0x4) -ne 0 -or
       $productAnchor.KeyPath -cne 'SwSearchProductAnchorFile'){
        throw 'Per-machine product anchor must retain its stable GUID and true File KeyPath.'
    }
    $anchorFileRows=@()
    $view=$database.OpenView("SELECT `File`,`FileName` FROM `File` WHERE `File`='SwSearchProductAnchorFile'"); $view.Execute()
    while($record=$view.Fetch()){
        $anchorFileName=$record.GetString(2)
        if($anchorFileName.Contains('|')){ $anchorFileName=$anchorFileName.Split('|')[-1] }
        $anchorFileRows += [pscustomobject]@{Id=$record.GetString(1);Name=$anchorFileName}
        $record.Dispose()
    }; $view.Dispose()
    if($anchorFileRows.Count -ne 1 -or $anchorFileRows[0].Name -cne 'SwSearch.InstallAnchor.dat'){
        throw 'Per-machine product anchor File row is missing or renamed.'
    }
    $featureComponents=@()
    $view=$database.OpenView('SELECT `Feature_`,`Component_` FROM `FeatureComponents`'); $view.Execute()
    while($record=$view.Fetch()){
        $featureComponents += [pscustomobject]@{Feature=$record.GetString(1);Component=$record.GetString(2)}
        $record.Dispose()
    }; $view.Dispose()
    if(@($featureComponents | Where-Object { $_.Feature -ceq 'MainFeature' -and $_.Component -ceq 'SwSearchProductFileAnchor' }).Count -ne 1){
        throw 'Per-machine product file anchor is not attached exactly once to MainFeature.'
    }
    Assert-AddinUpgradeTableModel $sequence $registry $componentRows $featureComponents $addinClsid $rc15KeyPathRegistryRow
    foreach($component in @('ContextMenuFolder','ContextMenuFolderBackground')){ if($conditions[$component] -cne 'WindowsBuild < 22000'){ throw "$component must remain old-Windows-only fallback." } }

    # v1.3 removes engineering-drawing support.  An upgrade must clean only
    # the product-owned legacy verb, while a fresh install must not create a
    # drawing context-menu component or any drawing registry values.
    $legacyDrawingVerbKey='Software\Classes\SystemFileAssociations\.slddrw\shell\SwSearch.ViewProperties'
    if($componentRows.ContainsKey('ContextMenuSlddrw')){
        throw 'Engineering-drawing context-menu component must not be installed.'
    }
    if(-not $componentRows.ContainsKey('LegacyDrawingPropertyVerbCleanup')){
        throw 'Major-upgrade legacy drawing-verb cleanup component is missing.'
    }
    $legacyDrawingCleanup=$componentRows.LegacyDrawingPropertyVerbCleanup
    if($legacyDrawingCleanup.ComponentId -cne '{CFF2D75B-6BA5-49F0-8D58-9986D1952D42}' -or
       $legacyDrawingCleanup.Directory -cne 'INSTALLFOLDER' -or
       $legacyDrawingCleanup.Condition -cne 'WIX_UPGRADE_DETECTED'){
        throw 'Major-upgrade legacy drawing-verb cleanup component identity/condition changed.'
    }
    if(@($featureComponents | Where-Object { $_.Feature -ceq 'MainFeature' -and $_.Component -ceq 'LegacyDrawingPropertyVerbCleanup' }).Count -ne 1){
        throw 'Major-upgrade legacy drawing-verb cleanup is not attached exactly once to MainFeature.'
    }
    if(@($registry | Where-Object { $_.Key -like "$legacyDrawingVerbKey*" }).Count -ne 0){
        throw 'Engineering-drawing registry values must not be installed.'
    }
    try{
        $removeRegistry=@()
        $view=$database.OpenView('SELECT `RemoveRegistry`,`Root`,`Key`,`Name`,`Component_` FROM `RemoveRegistry`'); $view.Execute()
        while($record=$view.Fetch()){
            $removeRegistry += [pscustomobject]@{
                Id=$record.GetString(1);Root=$record.GetInteger(2);Key=$record.GetString(3)
                Name=if($record.IsNull(4)){$null}else{$record.GetString(4)};Component=$record.GetString(5)
            }
            $record.Dispose()
        }; $view.Dispose()
    }catch{
        throw "Major-upgrade legacy drawing-verb cleanup table is missing or unreadable: $($_.Exception.Message)"
    }
    $legacyDrawingRemovals=@($removeRegistry | Where-Object {
        $_.Root -eq 1 -and $_.Key -ceq $legacyDrawingVerbKey -and $_.Name -ceq '-' -and
        $_.Component -ceq 'LegacyDrawingPropertyVerbCleanup'
    })
    if($legacyDrawingRemovals.Count -ne 1){
        throw 'Major-upgrade legacy drawing-verb cleanup must remove exactly the owned HKCU verb key.'
    }

    $launch=@()
    $view=$database.OpenView('SELECT `Condition`,`Description` FROM `LaunchCondition`'); $view.Execute()
    while($record=$view.Fetch()){ $launch += $record.GetString(1); $record.Dispose() }; $view.Dispose()
    if($launch -notcontains 'Installed OR NOT LEGACY_PERUSER_INSTALL'){ throw 'Cross-context v1.1.x migration block is missing.' }
    if($launch -notcontains 'SWSEARCHDOTNETCORE8 = "1"' -or $launch -notcontains 'SWSEARCHWINDOWSDESKTOP8 = "1"'){ throw 'x64 .NET 8 Core/Desktop launch conditions are missing.' }

    $uiSequence=@{}
    $view=$database.OpenView('SELECT `Action`,`Condition`,`Sequence` FROM `InstallUISequence`'); $view.Execute()
    while($record=$view.Fetch()){
        $uiSequence[$record.GetString(1)]=[pscustomobject]@{Condition=if($record.IsNull(2)){$null}else{$record.GetString(2)};Sequence=$record.GetInteger(3)}
        $record.Dispose()
    }; $view.Dispose()
    if(-not $uiSequence.ContainsKey('CheckSwSearchDotNet8') -or -not $uiSequence.ContainsKey('LaunchConditions') -or
       $uiSequence.CheckSwSearchDotNet8.Sequence -ge $uiSequence.LaunchConditions.Sequence){ throw 'Native .NET 8 check must precede UI LaunchConditions.' }

    $close=@()
    $view=$database.OpenView('SELECT `CloseApplication`,`Target`,`Attributes`,`Property`,`TerminateExitCode` FROM `Wix4CloseApplication`'); $view.Execute()
    while($record=$view.Fetch()){
        $close += [pscustomobject]@{ Id=$record.GetString(1); Target=$record.GetString(2); Attributes=$record.GetInteger(3); Property=$record.GetString(4); Terminate=if($record.IsNull(5)){$null}else{$record.GetInteger(5)} }
        $record.Dispose()
    }; $view.Dispose()
    if(@($close).Count -ne 1 -or -not ($close | Where-Object Target -ceq 'SLDWORKS.exe')){ throw 'Only the detect-only SOLIDWORKS CloseApplication row is allowed; App/Viewer use Restart Manager and Broker/Worker use the coordinator.' }
    foreach($forbiddenTarget in @('SwSearch.App.exe','SwSearch.PropertyViewer.exe','SwSearch.Broker.exe','SwSearch.Metadata.Worker.exe')){ if($close | Where-Object Target -ceq $forbiddenTarget){ throw "Blind name-based CloseApplication remains: $forbiddenTarget" } }
    if($close | Where-Object { $null -ne $_.Terminate }){ throw 'Installer must never terminate a process.' }
    $solidworks=$close | Where-Object Target -ceq 'SLDWORKS.exe'
    if(($solidworks.Attributes -band 1) -ne 0){ throw 'Installer must detect but not close SOLIDWORKS.' }

    $forbiddenRuntimeNames=@('coreclr.dll','clrjit.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll','PresentationFramework.Aero.dll','PresentationFramework.Aero2.dll','PresentationFramework.AeroLite.dll','PresentationFramework.Classic.dll','PresentationCore.dll','WindowsBase.dll','System.Xaml.dll','vcruntime140_cor3.dll','Microsoft.NETCore.App.deps.json','Microsoft.WindowsDesktop.App.deps.json')
    $view=$database.OpenView('SELECT `File`,`FileName`,`Version` FROM `File`'); $view.Execute(); $fileRows=0; $versionedFirstPartyRows=0
    while($record=$view.Fetch()){
        $fileRows++
        $fileName=$record.GetString(2)
        if($fileName.Contains('|')){ $fileName=$fileName.Split('|')[-1] }
        if($forbiddenRuntimeNames -contains $fileName){ throw ".NET runtime/framework file leaked into MSI File/CAB: $fileName" }
        if($fileName -match '(?i)(InstallerCoordinator|InstallerMsiBridge|InstallerMsiCommandLineGate|WatchdogHelper|NativeHelper|\.pdb$)'){ throw "Installer-private binary leaked into File/CAB payload: $fileName" }
        if($fileName -match '^SwSearch\..*\.(?:dll|exe)$'){
            $fileVersion=if($record.IsNull(3)){$null}else{$record.GetString(3)}
            if($fileVersion -cne "$Version.0"){ throw "MSI File table cannot force $ReleaseCandidate replacement: $fileName version=$fileVersion" }
            $versionedFirstPartyRows++
        }
        $record.Dispose()
    }; $view.Dispose()
    if($versionedFirstPartyRows -ne 17){ throw "MSI File table first-party version coverage mismatch: $versionedFirstPartyRows" }
    if($fileRows -lt 40){ throw "RC19 framework-dependent File table is unexpectedly small: $fileRows" }

    $media=@()
    $view=$database.OpenView('SELECT `DiskId`,`LastSequence`,`Cabinet` FROM `Media`'); $view.Execute()
    while($record=$view.Fetch()){
        $media += [pscustomobject]@{
            DiskId=$record.GetInteger(1); LastSequence=$record.GetInteger(2)
            Cabinet=if($record.IsNull(3)){$null}else{$record.GetString(3)}
        }
        $record.Dispose()
    }; $view.Dispose()
    if($media.Count -ne 1 -or -not $media[0].Cabinet.StartsWith('#',[StringComparison]::Ordinal)){
        throw 'Media table must contain exactly one embedded CAB.'
    }
}finally{ $database.Dispose() }

$payloadFiles=@(Get-ChildItem -LiteralPath $payload -Recurse -File)
$payloadDrawingFiles=@($payloadFiles | Where-Object { $_.Extension -ieq '.slddrw' })
if($payloadDrawingFiles.Count -ne 0){ throw "Engineering-drawing files leaked into product payload: $($payloadDrawingFiles.FullName -join ', ')" }
$payloadRuntimeLeaks=@($payloadFiles | Where-Object { $forbiddenRuntimeNames -contains $_.Name })
if($payloadRuntimeLeaks.Count -ne 0){ throw ".NET runtime/framework file leaked into payload: $($payloadRuntimeLeaks.Name -join ', ')" }
$payloadPrivateLeaks=@($payloadFiles | Where-Object {
    $_.Name -match '(?i)(InstallerCoordinator|InstallerMsiBridge|InstallerMsiCommandLineGate|WatchdogHelper|NativeHelper|\.pdb$)' -or
    $_.FullName -match '(?i)[\\/](?:tests?|TestResults)[\\/]'
})
if($payloadPrivateLeaks.Count -ne 0){ throw "Installer-private content leaked into publish payload: $($payloadPrivateLeaks.FullName -join ', ')" }
$anchorSource=Join-Path $PSScriptRoot 'SwSearch.InstallAnchor.dat'
if(-not (Test-Path -LiteralPath $anchorSource -PathType Leaf)){ throw 'Independent product anchor source is missing.' }
if($payloadFiles | Where-Object Name -ceq 'SwSearch.InstallAnchor.dat'){ throw 'Independent product anchor must not be copied into the publish payload.' }
$exported=@(Get-ChildItem -LiteralPath (Join-Path $export 'File') -File)
$expectedMsiFiles=$payloadFiles.Count + 1
if($fileRows -ne $expectedMsiFiles -or $exported.Count -ne $expectedMsiFiles){ throw "MSI/payload+anchor/CAB file count mismatch: $fileRows/$expectedMsiFiles/$($exported.Count)" }
$verifiedCabIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$taskpaneCabFile=$null
$taskpaneAssetCabFiles=@{}
$taskpaneWebCabFiles=@{}
$webViewCoreCabFile=$null
$webViewLoaderCabFile=$null
$appCabFile=$null
$contractsCabFile=$null
foreach($payloadFile in $payloadFiles){
    $relativePath=$payloadFile.FullName.Substring($payload.Length).TrimStart('\','/').Replace('\','/')
    $normalized=$relativePath.ToLowerInvariant()
    $stableHash=Get-StableHashHex "SwSearch.PayloadFile|$normalized"
    $cabId=switch($normalized){
        'swsearch.moderncontextmenu.cer' { 'SwSearchModernMenuCertificateFile' }
        'swsearch.moderncontextmenu.msix' { 'SwSearchModernMenuPackageFile' }
        'swsearch.moderncontextmenuregistration.exe' { 'SwSearchModernMenuRegistrationFile' }
        default { 'PayloadFile_' + $stableHash.Substring(0,24) + '_File' }
    }
    $cabFile=Join-Path (Join-Path $export 'File') $cabId
    if(-not (Test-Path -LiteralPath $cabFile -PathType Leaf)){ throw "Embedded CAB path mapping missing: $relativePath -> $cabId" }
    $payloadHash=(Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash
    $cabHash=(Get-FileHash -LiteralPath $cabFile -Algorithm SHA256).Hash
    if($payloadHash -cne $cabHash){ throw "Embedded CAB path hash mismatch: $relativePath -> $cabId" }
    if(-not $verifiedCabIds.Add($cabId)){ throw "Duplicate CAB path mapping: $relativePath -> $cabId" }
    if($relativePath -ceq 'SwSearch.SolidWorksAddin.dll'){ $taskpaneCabFile=$cabFile }
    if($relativePath -ceq 'Microsoft.Web.WebView2.Core.dll'){ $webViewCoreCabFile=$cabFile }
    if($relativePath -ceq 'WebView2Loader.dll'){ $webViewLoaderCabFile=$cabFile }
    if($relativePath -like 'TaskpaneAssets/SwSearch-*.png'){
        $taskpaneAssetCabFiles[[IO.Path]::GetFileName($relativePath)]=$cabFile
    }
    if($relativePath -like 'webui/*'){
        $taskpaneWebCabFiles[[IO.Path]::GetFileName($relativePath)]=$cabFile
    }
    if($relativePath -ceq 'SwSearch.App.dll'){ $appCabFile=$cabFile }
    if($relativePath -ceq 'SwSearch.Contracts.dll'){ $contractsCabFile=$cabFile }
}
$anchorCabFile=Join-Path (Join-Path $export 'File') 'SwSearchProductAnchorFile'
if(-not (Test-Path -LiteralPath $anchorCabFile -PathType Leaf)){ throw 'Independent product anchor is missing from the embedded CAB.' }
$anchorSourceHash=(Get-FileHash -LiteralPath $anchorSource -Algorithm SHA256).Hash
$anchorCabHash=(Get-FileHash -LiteralPath $anchorCabFile -Algorithm SHA256).Hash
if($anchorSourceHash -cne $anchorCabHash){ throw 'Independent product anchor CAB hash does not match installer/SwSearch.InstallAnchor.dat.' }
if(-not $verifiedCabIds.Add('SwSearchProductAnchorFile')){ throw 'Independent product anchor CAB id is duplicated.' }
$unexpectedCabIds=@($exported | Where-Object { -not $verifiedCabIds.Contains($_.Name) })
if($unexpectedCabIds.Count -ne 0){ throw "Embedded CAB contains unmapped files: $($unexpectedCabIds.Name -join ', ')" }
$cabHashes=@($verifiedCabIds)
$null=Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $payload `
    -ManifestSha256 $snapshotManifestSha256 -EntriesDigest $snapshotEntriesDigest `
    -FileCount $snapshotFileCount

$binaries=@(Get-ChildItem -LiteralPath $payload -File | Where-Object { $_.Name -like 'SwSearch.*' -and $_.Extension -in @('.dll','.exe') })
if($binaries.Count -ne 17){ throw "Expected 17 first-party binaries; found $($binaries.Count)." }
foreach($binary in $binaries){
    $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($binary.FullName)
    if($info.FileVersion -cne "$Version.0" -or $info.ProductVersion -cne $Version){ throw "Version mismatch: $($binary.Name)" }
}
foreach($depsName in @('SwSearch.App.deps.json','SwSearch.PropertyViewer.deps.json')){
    $deps=Get-Content -LiteralPath (Join-Path $payload $depsName) -Raw -Encoding UTF8
    foreach($required in @('SwSearch.Indexer/','Microsoft.Data.Sqlite/8.0.28','SQLitePCLRaw.core/2.1.11')){ if(-not $deps.Contains($required)){ throw "Read-only cache dependency missing in ${depsName}: $required" } }
    foreach($forbidden in @('SwSearch.Metadata.Worker/','ClosedXML/','DocumentFormat.OpenXml/')){ if($deps.Contains($forbidden)){ throw "UI read-only cache boundary violation in ${depsName}: $forbidden" } }
}
foreach($requiredSource in @(
    'src\SwSearch.ModernContextMenu\ExplorerCommand.cpp',
    'src\SwSearch.ModernContextMenu\PackageRegistration.cpp',
    'src\SwSearch.ModernContextMenu\AppxManifest.xml.in',
    'installer\Build-ModernContextMenu.ps1',
    'docs\MODERN-CONTEXT-MENU-BUILD.md')){
    if(-not (Test-Path -LiteralPath (Join-Path $root $requiredSource) -PathType Leaf)){ throw "Modern menu production source missing: $requiredSource" }
}

if(-not $taskpaneCabFile -or -not $webViewCoreCabFile -or -not $webViewLoaderCabFile -or
   -not $appCabFile -or -not $contractsCabFile){
    throw 'Embedded App/Contracts/Taskpane/WebView2 paths were not resolved from the MSI CAB.'
}
$candidateTaskpaneDirectory=Join-Path $inspection 'candidate-taskpane'
New-Item -ItemType Directory -Force -Path $candidateTaskpaneDirectory | Out-Null
$candidateTaskpaneDll=Join-Path $candidateTaskpaneDirectory 'SwSearch.SolidWorksAddin.dll'
Copy-Item -LiteralPath $taskpaneCabFile -Destination $candidateTaskpaneDll
Copy-Item -LiteralPath $webViewCoreCabFile -Destination (Join-Path $candidateTaskpaneDirectory 'Microsoft.Web.WebView2.Core.dll')
Copy-Item -LiteralPath $webViewLoaderCabFile -Destination (Join-Path $candidateTaskpaneDirectory 'WebView2Loader.dll')
$candidateTaskpaneAssets=Join-Path $inspection 'candidate-taskpane-assets'
New-Item -ItemType Directory -Force -Path $candidateTaskpaneAssets | Out-Null
foreach($size in @(20,32,40,64,96,128)){
    $name="SwSearch-$size.png"
    if(-not $taskpaneAssetCabFiles.ContainsKey($name)){ throw "Embedded Taskpane asset was not resolved from the MSI CAB: $name" }
    Copy-Item -LiteralPath $taskpaneAssetCabFiles[$name] -Destination (Join-Path $candidateTaskpaneAssets $name)
}
$candidateTaskpaneWebAssets=Join-Path $inspection 'candidate-taskpane-webui'
New-Item -ItemType Directory -Force -Path $candidateTaskpaneWebAssets | Out-Null
foreach($name in @('taskpane.html','app.css','app.js')){
    if(-not $taskpaneWebCabFiles.ContainsKey($name)){ throw "Embedded Taskpane web asset was not resolved from the MSI CAB: $name" }
    Copy-Item -LiteralPath $taskpaneWebCabFiles[$name] -Destination (Join-Path $candidateTaskpaneWebAssets $name)
}
$candidateGateDirectory=Join-Path $inspection 'candidate-app-gate'
New-Item -ItemType Directory -Force -Path $candidateGateDirectory | Out-Null
$candidateGateApp=Join-Path $candidateGateDirectory 'SwSearch.App.dll'
$candidateGateContracts=Join-Path $candidateGateDirectory 'SwSearch.Contracts.dll'
Copy-Item -LiteralPath $appCabFile -Destination $candidateGateApp
Copy-Item -LiteralPath $contractsCabFile -Destination $candidateGateContracts
if((Get-FileHash -LiteralPath $candidateGateApp -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $appCabFile -Algorithm SHA256).Hash -or
   (Get-FileHash -LiteralPath $candidateGateContracts -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $contractsCabFile -Algorithm SHA256).Hash){
    throw 'Candidate App/Contracts CAB probe copies changed bytes.'
}
if($env:SWSEARCH_SKIP_CANDIDATE_IL_GATE -ceq '1'){
    throw 'SWSEARCH_SKIP_CANDIDATE_IL_GATE is forbidden; candidate IL gate cannot be bypassed.'
}
& dotnet run --project (Join-Path $root 'tests\SwSearch.CandidateIlGate\SwSearch.CandidateIlGate.csproj') `
    -c Release --no-build --no-restore -- $candidateGateApp $candidateGateContracts $payload
if($LASTEXITCODE -ne 0){ throw 'Candidate App scope/start synchronous-I/O and lexical-path IL gate failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'installer\Verify-Taskpane.ps1') `
    -DllPath $candidateTaskpaneDll -Version $Version -BinaryVersion "$Version.0" -SkipBehaviorHarness `
    -TaskpaneAssetsPath $candidateTaskpaneAssets `
    -WebAssetsPath $candidateTaskpaneWebAssets | Out-Null
if($LASTEXITCODE -ne 0){ throw 'SOLIDWORKS Taskpane ABI verification failed.' }

$summary=[WixToolset.Dtf.WindowsInstaller.SummaryInfo]::new($msi,$false)
try{ if($summary.Template -cne 'x64;2052'){ throw "Summary template mismatch: $($summary.Template)" } }finally{ $summary.Dispose() }
Assert-FixedFile $PreviousMsi $expectedPreviousBytes $expectedPreviousSha 'RC12 immutable post-verification gate'
Assert-FixedFile $expectedRc15Msi $expectedRc15Bytes $expectedRc15Sha 'RC15 immutable post-verification gate'
$msiInfo=Get-Item -LiteralPath $msi
$msiHash=(Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
$signature=(Get-AuthenticodeSignature -LiteralPath $msi).Status
$lines=@(
    "SwSearch $Version $($ReleaseCandidate.ToUpperInvariant()) static MSI verification: PASS",
    "MSI: $msi",
    "Bytes: $($msiInfo.Length)",
    "SHA256: $msiHash",
    "Authenticode: $signature",
    "ProductCode: {$ProductCode}",
    "UpgradeCode: $upgradeCode",
    'Scope: per-machine; ProgramFiles64; legacy per-user install is diagnostically blocked',
    "File rows / path-mapped CAB hashes: $fileRows / $($cabHashes.Count)",
    "Installer Binary streams: coordinator=$($binaryRows.SwSearchInstallerCoordinator.Bytes)/$($binaryRows.SwSearchInstallerCoordinator.SHA256); bridge=$($binaryRows.SwSearchInstallerMsiBridge.Bytes)/$($binaryRows.SwSearchInstallerMsiBridge.SHA256)",
    'Installer Binary entry gates: exact Binary set 10 (2 SwSearch actions + 8 fixed WiX UI/util resources); extracted coordinator self-test, isolated x64 bridge export/import/invalid-handle gate, and final-MSI command-line gate passed with pre/post hashes unchanged',
    "Installer command-line table target: $($customActions.SwSearchPrivilegedHandle.Target)",
    "Installer command-line gate log: $commandLineGateVerifyLog",
    "Previous RC12 MSI immutable: bytes=$expectedPreviousBytes; sha256=$expectedPreviousSha; ProductCode=$expectedPreviousProductCode; UpgradeCode=$upgradeCode; Version=1.2.0; ALLUSERS=1; FileRows=493",
    'Previous RC12 sidecars: only current hashes are observed here unless a separately frozen historical sidecar baseline is supplied; no blanket historical-sidecar immutability claim is made',
    "Retired RC15 MSI immutable: bytes=$expectedRc15Bytes; sha256=$expectedRc15Sha; ProductCode=$expectedRc15ProductCode; UpgradeCode=$upgradeCode; Version=1.2.0; FileRows=493",
    "Major-upgrade migration: Upgrade table targets $Version; $($ReleaseCandidate.ToUpperInvariant()) File table pins all first-party binaries to $Version.0 before RemoveExistingProducts; stable 64-bit registry component identity prevents old uninstall from deleting replacement values",
    'Installer coordination: native .NET 8 check precedes UI/execute LaunchConditions; user IPC covers install/repair/maintenance/uninstall before InstallValidate; deferred strict-handle phase precedes InstallFiles',
    'FilesInUse: Restart Manager enabled; bounded cooperative close precedes InstallValidate and remaining App locks are handled with Retry/Cancel before InstallFiles; no App/Viewer blind name termination',
    'Runtime payload: framework-dependent App/Broker/Worker/PropertyViewer/coordinator; .NET runtime/framework File/CAB rows 0',
    'Installer-private payload boundary: Coordinator/Bridge/CommandLineGate/WatchdogHelper/NativeHelper/tests/PDB File and CAB rows 0',
    'App startup: actual CAB App.OnStartup/window construction recursive IL has synchronous File/Directory/settings I/O and blocking waits 0; headless production background-settings bootstrap returns under 50 ms; defaults display first and late generations are guarded',
    'Folder scope: actual CAB App/Contracts and CLR4 Taskpane pass shared lexical matrix 50/50, including exact COM/LPT superscript 1/2/3 rejection and positive Unicode controls; offline or missing paths retain recursive cache scope',
    'SOLIDWORKS Add-in: transaction-staged/payload DLL passed x64 CLR4 COM/ABI and compiled behavior harness 22/22; byte-identical embedded CAB DLL separately passed static x64 CLR4 COM/ABI/bridge-policy verification; SOLIDWORKS 2026 in-process drag reception remains manual',
    'UI cache readers: App/PropertyViewer use fixed read-only SQLite dependencies; worker/xlsx parser dependencies excluded',
    'Modern Windows 11 context menu: production source/lifecycle present; signed MSIX payload intentionally absent because toolchain/signing authorization is blocked',
    'Classic folder verbs: conditioned to WindowsBuild < 22000 only',
    "Controlled source files / modified after candidate: $($initialFreeze.ControlledFileCount) / $($initialFreeze.ModifiedAfterCandidateCount)",
    $finalFrozenRestoreGraphGateReport,
    'Installer/product/msiexec/SOLIDWORKS/Everything/Explorer launched: no'
)
$finalFreeze=Test-SwSearchControlledSourceManifest -Root $root -ManifestPath $ControlledManifest `
    -CandidateLastWriteTimeUtc $candidateInfo.LastWriteTimeUtc
Write-Output "FINAL_CONTROLLED_SOURCE_FREEZE: files=$($finalFreeze.ControlledFileCount); modifiedAfterCandidate=$($finalFreeze.ModifiedAfterCandidateCount)"
$null=Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $payload `
    -ManifestSha256 $snapshotManifestSha256 -EntriesDigest $snapshotEntriesDigest `
    -FileCount $snapshotFileCount
Assert-FixedFile $PreviousMsi $expectedPreviousBytes $expectedPreviousSha 'RC12 immutable final gate'
Assert-FixedFile $expectedRc15Msi $expectedRc15Bytes $expectedRc15Sha 'RC15 immutable final gate'
if($SkipFinalFrozenRestoreGraph){
    Write-Output 'FINAL_FROZEN_RESTORE_GRAPH_DEEP_VALIDATION: skipped by explicit receipt-gated controlled fallback.'
}else{
    & powershell @restoreGateArguments
    if($LASTEXITCODE -ne 0){ throw 'Frozen zero-restore graph changed during MSI verification.' }
}
$null=Assert-SwSearchPayloadSnapshotIdentity -SnapshotRoot $payload `
    -ManifestSha256 $snapshotManifestSha256 -EntriesDigest $snapshotEntriesDigest `
    -FileCount $snapshotFileCount
if(Test-Path -LiteralPath $inspection){
    Remove-Item -LiteralPath $inspection -Recurse -Force
}
if(Test-Path -LiteralPath $inspection){ throw 'Verification inspection cleanup did not complete.' }
[IO.File]::WriteAllLines($reportWriting,$lines,[Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $reportWriting -Destination $report
$verificationComplete=$true
$lines
}
finally{
    $cleanupFailure=$null
    try{
        if(Test-Path -LiteralPath $inspection){ Remove-Item -LiteralPath $inspection -Recurse -Force }
        if(Test-Path -LiteralPath $inspection){ throw 'Verification inspection cleanup did not complete.' }
        if(Test-Path -LiteralPath $reportWriting){ Remove-Item -LiteralPath $reportWriting -Force }
        if(-not $verificationComplete -and (Test-Path -LiteralPath $report)){ Remove-Item -LiteralPath $report -Force }
    }catch{
        $cleanupFailure=$_
    }
    if($null -ne $cleanupFailure){ throw $cleanupFailure }
}
