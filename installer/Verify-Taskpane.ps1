[CmdletBinding()]
param(
    [string]$DllPath = '',
    [string]$Version = '1.3.16',
    [string]$BinaryVersion = '1.3.16.0',
    [string]$TaskpaneAssetsPath = '',
    [string]$WebAssetsPath = '',
    [switch]$SkipBehaviorHarness,
    [string]$SolidWorksInteropPath = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $root 'artifacts\SwSearch-win-x64\SwSearch.SolidWorksAddin.dll'
}
$DllPath = [IO.Path]::GetFullPath($DllPath)
if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) { throw "Taskpane DLL not found: $DllPath" }
if ([string]::IsNullOrWhiteSpace($TaskpaneAssetsPath)) {
    $TaskpaneAssetsPath = Join-Path (Split-Path -Parent $DllPath) 'TaskpaneAssets'
}
if ([string]::IsNullOrWhiteSpace($WebAssetsPath)) {
    $WebAssetsPath = Join-Path (Split-Path -Parent $DllPath) 'webui'
}
$TaskpaneAssetsPath = [IO.Path]::GetFullPath($TaskpaneAssetsPath)
$WebAssetsPath = [IO.Path]::GetFullPath($WebAssetsPath)
foreach ($asset in @('taskpane.html','app.css','app.js')) {
    if (-not (Test-Path -LiteralPath (Join-Path $WebAssetsPath $asset) -PathType Leaf)) {
        throw "Taskpane Web asset missing: $asset"
    }
}
foreach ($size in @(20,32,40,64,96,128)) {
    if (-not (Test-Path -LiteralPath (Join-Path $TaskpaneAssetsPath "SwSearch-$size.png") -PathType Leaf)) {
        throw "Taskpane icon missing: SwSearch-$size.png"
    }
}

$webViewCorePath = Join-Path (Split-Path -Parent $DllPath) 'Microsoft.Web.WebView2.Core.dll'
$webViewLoaderPath = Join-Path (Split-Path -Parent $DllPath) 'WebView2Loader.dll'
if (-not (Test-Path -LiteralPath $webViewCorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $webViewLoaderPath -PathType Leaf)) {
    throw 'Taskpane WebView2 SDK/Loader payload is incomplete.'
}
$SolidWorksInteropPath = [IO.Path]::GetFullPath($SolidWorksInteropPath)
if (-not (Test-Path -LiteralPath $SolidWorksInteropPath -PathType Leaf) -or
    (Get-Item -LiteralPath $SolidWorksInteropPath).Length -ne 2831960 -or
    (Get-FileHash -LiteralPath $SolidWorksInteropPath -Algorithm SHA256).Hash -cne
        'F3AF048BB4064B8FE7E3BB8E1B5D6E6AAB5DC78C04B3EBAA7927B3EE923F6279') {
    throw 'SOLIDWORKS 2026 official PIA identity is unavailable or changed.'
}

$bytes = [IO.File]::ReadAllBytes($DllPath)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
if ([Text.Encoding]::ASCII.GetString($bytes, $peOffset, 4) -cne "PE`0`0") { throw 'Invalid Taskpane PE.' }
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x8664) { throw 'Taskpane DLL is not AMD64.' }

[void][Reflection.Assembly]::LoadFrom($webViewCorePath)
$assembly = [Reflection.Assembly]::LoadFrom($DllPath)
if ($assembly.GetName().Version.ToString() -cne $BinaryVersion) {
    throw "Taskpane assembly version mismatch: $($assembly.GetName().Version)"
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($DllPath).FileVersion -cne $BinaryVersion) {
    throw 'Taskpane file version mismatch.'
}
$references = @($assembly.GetReferencedAssemblies() | Select-Object -ExpandProperty Name)
if ($references -contains 'SolidWorks.Interop.sldworks') { throw 'SOLIDWORKS PIA must remain embedded.' }
if ($references -notcontains 'Microsoft.Web.WebView2.Core') { throw 'WebView2 Core reference is missing.' }
if ($references -contains 'Microsoft.CSharp') { throw 'RuntimeBinder dependency is forbidden.' }

$binding = [Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$addin = $assembly.GetType('SwSearch.SolidWorksAddin.SwAddin', $true)
$contract = $assembly.GetType('SwSearch.SolidWorksAddin.ISwAddin', $true)
$taskpane = $assembly.GetType('SwSearch.SolidWorksAddin.TaskpaneControl', $true)
$shellHost = $assembly.GetType('SwSearch.SolidWorksAddin.TaskpaneShellResultsHost', $true)
$shellTarget = $assembly.GetType('SwSearch.SolidWorksAddin.ITaskpaneShellResultsTarget', $true)
$explorerBrowser = $assembly.GetType('SwSearch.SolidWorksAddin.IExplorerBrowser', $true)
$resultsFolder = $assembly.GetType('SwSearch.SolidWorksAddin.IResultsFolder', $true)
$shellItem = $assembly.GetType('SwSearch.SolidWorksAddin.IShellItem', $true)
$eventContract = $assembly.GetType('SwSearch.SolidWorksAddin.IExplorerBrowserEvents', $true)
$eventSink = $assembly.GetType('SwSearch.SolidWorksAddin.ExplorerBrowserEventSink', $true)
$compositionHost = $assembly.GetType('SwSearch.SolidWorksAddin.TaskpaneCompositionWebViewHost', $true)
$bridgePolicy = $assembly.GetType('SwSearch.SolidWorksAddin.TaskpaneWebBridgePolicy', $true)
$mouseInput = $assembly.GetType('SwSearch.SolidWorksAddin.TaskpaneMouseInput', $true)

if ($addin.GUID.ToString('D').ToUpperInvariant() -cne 'F3A7CC42-C6E5-4CF8-96D6-2D1F1AA1B61D') {
    throw 'Taskpane COM class GUID changed.'
}
if ($contract.GUID.ToString('D').ToUpperInvariant() -cne 'DA306A0D-EAC5-4406-8610-B1DA805D9270') {
    throw 'ISwAddin GUID changed.'
}
if (-not ($addin.GetInterfaces() -contains $contract)) { throw 'SwAddin no longer implements ISwAddin.' }
if (-not ($shellHost.GetInterfaces() -contains $shellTarget)) { throw 'ExplorerBrowser host is not the production result target.' }
if (-not [Runtime.InteropServices.Marshal]::IsTypeVisibleFromCom($eventContract) -or
    -not [Runtime.InteropServices.Marshal]::IsTypeVisibleFromCom($eventSink)) {
    throw 'ExplorerBrowser event contract/sink is not visible from COM in the fixed DLL.'
}
if ($explorerBrowser.GUID.ToString('D').ToUpperInvariant() -cne 'DFD3B6B5-C10C-4BE9-85F6-A66969F402F6' -or
    $resultsFolder.GUID.ToString('D').ToUpperInvariant() -cne '96E5AE6D-6AE1-4B1C-900C-C6480EAA8828' -or
    $shellItem.GUID.ToString('D').ToUpperInvariant() -cne '43826D1E-E718-42EE-BC55-A1E261C37BFE') {
    throw 'ExplorerBrowser/IResultsFolder/IShellItem ABI GUID changed.'
}
$controllerField = $compositionHost.GetField('_compositionController', $binding)
if (-not $controllerField -or
    $controllerField.FieldType.FullName -cne 'Microsoft.Web.WebView2.Core.CoreWebView2CompositionController') {
    throw 'Top toolbar is not hosted by CompositionController.'
}

$sourceRoot = Join-Path $root 'src\SwSearch.SolidWorksAddin'
$controlSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'TaskpaneControl.cs') -Raw -Encoding UTF8
$hostSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'TaskpaneShellResultsHost.cs') -Raw -Encoding UTF8
$compositionSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'TaskpaneCompositionWebViewHost.cs') -Raw -Encoding UTF8
$addinSource = Get-Content -LiteralPath (Join-Path $sourceRoot 'SwAddin.cs') -Raw -Encoding UTF8
$allSource = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
}) -join "`n"

foreach ($removedFile in @(
    'TaskpaneNativeResultsView.cs','TaskpaneShellFileDragService.cs','TaskpaneSparseResultCache.cs',
    'TaskpaneFileProbe.cs','TaskpaneColumnSettings.cs')) {
    if (Test-Path -LiteralPath (Join-Path $sourceRoot $removedFile)) {
        throw "Removed RC43 result/drag source still exists: $removedFile"
    }
}
foreach ($forbidden in @(
    'TaskpaneNativeResultsView','TaskpaneShellFileDragService','TaskpanePreparedFileDrag',
    'ItemDrag','SHDoDragDrop','DoDragDrop','armExternalFileDrag','rowToken','IDropSource')) {
    if ($allSource.Contains($forbidden)) { throw "Removed Taskpane drag/list path remains: $forbidden" }
}
foreach ($required in @(
    'new TaskpaneShellResultsHost()','private int _searchMode = 3;','PopulationPageSize = 500',
    'while (hasMore)','_shellResults.Replace','_shellResults.Append','TryAcceptLibraryPage',
    'TaskpaneLibrarySettings.FilterToRoots','BeginNewResultSet','CancelBefore','BrokerProtocol.RetryConnection')) {
    if (-not $controlSource.Contains($required)) { throw "RC48 Taskpane production contract missing: $required" }
}
foreach ($required in @(
    'ClsidExplorerBrowser','ExplorerOptions = 0x1u | 0x40u | 0x80u',
    'FillFromObject(null, NoDropTarget)','typeof(IResultsFolder).GUID','CoMarshalInterThreadInterfaceInStream',
    'CoGetInterfaceAndReleaseStream','SHCreateItemFromParsingName','folder.AddItem(item)',
    'command.Generation != _latestGeneration()','BlockingCollection<ShellPopulationCommand>',
    'new ExplorerBrowserEventSink','_browser.Advise(_eventSink','TaskCompletionSource<ShellPopulationResult>',
    '_completion.TrySetResult','ReadyWaitHandle','ExitedWaitHandle','worker.RequestStop()',
    'CoWaitForMultipleHandles','RegisterDeferredTeardown','browser.Unadvise',
    'objectWithSite.SetSite(null)','browser.Destroy()','SIGDN_FILESYSPATH')) {
    if (-not $hostSource.Contains($required)) { throw "ExplorerBrowser lifecycle/population contract missing: $required" }
}
foreach ($required in @(
    'void BeginShutdown(Action completed)','_shellResults.BeginShutdown(ShellShutdownAttemptCompleted)',
    '_webHost.BeginShutdown(WebViewShutdownCompleted)','WebViewShutdownCompleted()',
    '_webViewShutdownCompleted','_shellShutdownCompleted','TryCompleteParentShutdownOnUi',
    'ShutdownAndWaitOnUi','WaitWithComDispatch','base.Dispose(true)',
    'ParentDisposeCompletedForTesting','SignalShutdownTerminal','_shutdownAttemptEvent',
    'ShutdownOwnerRetrySliceMilliseconds','Stopwatch deadline = Stopwatch.StartNew()',
    'ShutdownDispatchWaitForTesting','the monotonic terminal always wins',
    'ShellShutdownAttemptCompleted(ShellShutdownResult result)','if (result == null || !result.Succeeded)')) {
    if (-not $controlSource.Contains($required)) { throw "Taskpane parent shutdown handshake missing: $required" }
}
foreach ($required in @(
    'MarshaledInterfaceStream','CoReleaseMarshalData','CompleteFailedStart',
    'BeforeWorkerThreadStartForTesting','WorkerCoInitializeForTesting',
    'TaskCompletionSource<Exception>','CompletePendingRelease','StatePendingDispatch',
    'MarshalReleasePostForTesting','if (!completion.Task.Wait(MarshalReleaseDispatchWaitMilliseconds)) return false',
    'if (_marshaledResultsStream != null && !_marshaledResultsStream.ReleaseUnconsumed())')) {
    if (-not $hostSource.Contains($required)) { throw "Shell marshal/start failure lifecycle missing: $required" }
}
foreach ($required in @(
    'TaskpaneWebViewInitializationLifetime','TaskpaneWebViewInitializationLease',
    'InitializeCompositionAsync(int epoch)','_initializationLifetime.IsCurrent(epoch)',
    '_initializationLifetime.TryPublish(','PublishInitialization',
    '_initializationLifetime.Invalidate();','ReleaseOrRetainInitialization(initialization)',
    'controller.RootVisualTarget = null','controller.Close()',
    'InitializationFactoryForTesting','CreateForTesting',
    'OnHandleDestroyed(EventArgs e)','BeginEpoch()','IsCurrentHandle(epoch, ownerHandle)',
    'RecreateHandleForTesting',
    'internal void BeginShutdown(Action completed)','TrySignalShutdownTerminal',
    '_initializationInFlight','_shutdownResourcesReleased',
    '_pendingInitializationReleases','TryRelease(out Exception failure)',
    '_shutdownResourcesReleased = released',
    '_closeControllerForTesting','_releaseCompositionForTesting',
    '_releaseInProgress','owner-STA reentry was deferred')) {
    if (-not $compositionSource.Contains($required)) {
        throw "WebView in-flight shutdown lifecycle missing: $required"
    }
}
if ($compositionSource.Contains('_environment = await') -or
    $compositionSource.Contains('_compositionController = await')) {
    throw 'WebView async initialization publishes host fields before the shutdown epoch gate.'
}
$webLease = $compositionSource.IndexOf(
    'internal sealed class TaskpaneWebViewInitializationLease', [StringComparison]::Ordinal)
$webTryRelease = $compositionSource.IndexOf(
    'internal bool TryRelease(out Exception failure)', $webLease, [StringComparison]::Ordinal)
$webReentryCheck = $compositionSource.IndexOf(
    'if (_releaseInProgress)', $webTryRelease, [StringComparison]::Ordinal)
$webReentryMark = $compositionSource.IndexOf(
    '_releaseInProgress = true;', $webReentryCheck, [StringComparison]::Ordinal)
$webControllerClose = $compositionSource.IndexOf(
    'controller.Close();', $webReentryMark, [StringComparison]::Ordinal)
$webControllerReleased = $compositionSource.IndexOf(
    '_controllerClosed = true;', $webControllerClose, [StringComparison]::Ordinal)
$webCompositionRelease = $compositionSource.IndexOf(
    'if (releaseComposition != null) releaseComposition();',
    $webControllerReleased, [StringComparison]::Ordinal)
$webLeaseReleased = $compositionSource.IndexOf(
    '_released = true;', $webCompositionRelease, [StringComparison]::Ordinal)
if (-not (0 -le $webLease -and $webLease -lt $webTryRelease -and
    $webTryRelease -lt $webReentryCheck -and
    $webReentryCheck -lt $webReentryMark -and
    $webReentryMark -lt $webControllerClose -and
    $webControllerClose -lt $webControllerReleased -and
    $webControllerReleased -lt $webCompositionRelease -and
    $webCompositionRelease -lt $webLeaseReleased)) {
    throw 'WebView lease can clear ownership or report release before Controller Close and composition release succeed.'
}
$webLeaseSource = $compositionSource.Substring($webLease)
if ($webLeaseSource.Contains('catch { }')) {
    throw 'WebView lease still swallows a cleanup exception and can prove a false terminal.'
}
$webShutdown = $compositionSource.IndexOf('internal void BeginShutdown(Action completed)', [StringComparison]::Ordinal)
$webInvalidate = $compositionSource.IndexOf('_initializationLifetime.Invalidate();', $webShutdown, [StringComparison]::Ordinal)
$webRelease = $compositionSource.IndexOf('ReleasePublishedResources();', $webInvalidate, [StringComparison]::Ordinal)
$webTerminal = $compositionSource.IndexOf('TrySignalShutdownTerminal();', $webRelease, [StringComparison]::Ordinal)
$webDispose = $compositionSource.IndexOf('protected override void Dispose(bool disposing)', [StringComparison]::Ordinal)
$webDisposeHandshake = $compositionSource.IndexOf('BeginShutdown(delegate', $webDispose, [StringComparison]::Ordinal)
if (-not (0 -le $webShutdown -and $webShutdown -lt $webInvalidate -and
    $webInvalidate -lt $webRelease -and $webRelease -lt $webTerminal -and
    $webTerminal -lt $webDispose -and $webDispose -lt $webDisposeHandshake)) {
    throw 'WebView shutdown does not invalidate, release, await in-flight creation, then defer base Dispose.'
}
$navigationStart = $compositionSource.IndexOf('core.NavigationStarting +=', [StringComparison]::Ordinal)
$frameStart = $compositionSource.IndexOf('core.FrameNavigationStarting +=', $navigationStart, [StringComparison]::Ordinal)
$newWindowStart = $compositionSource.IndexOf('core.NewWindowRequested +=', $frameStart, [StringComparison]::Ordinal)
$downloadStart = $compositionSource.IndexOf('core.DownloadStarting +=', $newWindowStart, [StringComparison]::Ordinal)
$permissionStart = $compositionSource.IndexOf('core.PermissionRequested +=', $downloadStart, [StringComparison]::Ordinal)
$messageStart = $compositionSource.IndexOf('core.WebMessageReceived +=', $permissionStart, [StringComparison]::Ordinal)
if (-not (0 -le $navigationStart -and $navigationStart -lt $frameStart -and
    $frameStart -lt $newWindowStart -and $newWindowStart -lt $downloadStart -and
    $downloadStart -lt $permissionStart -and $permissionStart -lt $messageStart)) {
    throw 'WebView fail-closed security event wiring is incomplete.'
}
$securityEvents = $compositionSource.Substring($navigationStart, $messageStart - $navigationStart)
foreach ($required in @('!_initializationLifetime.IsCurrent(epoch)','args.Cancel = true',
    'args.Handled = true','CoreWebView2PermissionState.Deny')) {
    if (-not $securityEvents.Contains($required)) {
        throw "WebView stale security event is not fail-closed: $required"
    }
}
if ($hostSource.Contains('using (ManualResetEvent completed') -or
    $hostSource.Contains('Interlocked.Exchange(ref _stream, IntPtr.Zero)')) {
    throw 'Marshal release still captures a disposable late-callback signal or loses raw-stream ownership before release.'
}
$disconnectCore = $addinSource.IndexOf('internal static bool DisconnectTaskpaneOnUi(', [StringComparison]::Ordinal)
$controlDispose = $addinSource.IndexOf('control.Dispose()', $disconnectCore, [StringComparison]::Ordinal)
$controlWait = $addinSource.IndexOf('control.ShutdownAndWaitOnUi(timeoutMilliseconds)', $controlDispose, [StringComparison]::Ordinal)
$deleteTaskpane = $addinSource.IndexOf('return deleteTaskpane()', $controlWait, [StringComparison]::Ordinal)
if (-not (0 -le $disconnectCore -and $disconnectCore -lt $controlDispose -and
    $controlDispose -lt $controlWait -and $controlWait -lt $deleteTaskpane)) {
    throw 'SwAddin does not synchronously defer ITaskpaneView.DeleteView until the complete TaskpaneControl terminal.'
}
$disconnectCatch = $addinSource.IndexOf('catch (Exception exception)',
    $addinSource.IndexOf('public bool DisconnectFromSW()', [StringComparison]::Ordinal), [StringComparison]::Ordinal)
$disconnectEnd = $addinSource.IndexOf('internal static bool DisconnectTaskpaneOnUi(', [StringComparison]::Ordinal)
if ($disconnectCatch -lt 0 -or $disconnectEnd -lt 0 -or
    $addinSource.Substring($disconnectCatch, $disconnectEnd - $disconnectCatch).Contains('DeleteTaskpaneAfterControl')) {
    throw 'DisconnectFromSW catch still attempts an unordered DeleteView fallback.'
}
foreach ($forbidden in @('Process.GetProcesses','GetProcessById','ProcessName','.Kill(')) {
    if ($addinSource.Contains($forbidden)) {
        throw "Taskpane Disconnect may target a process instead of its explicit owned references: $forbidden"
    }
}
$stop = $hostSource.IndexOf('worker.RequestStop()', [StringComparison]::Ordinal)
$unadvise = $hostSource.IndexOf('browser.Unadvise', [StringComparison]::Ordinal)
$unsite = $hostSource.IndexOf('objectWithSite.SetSite(null)', $unadvise, [StringComparison]::Ordinal)
$destroy = $hostSource.IndexOf('browser.Destroy()', [StringComparison]::Ordinal)
$release = $hostSource.IndexOf('ReleaseOwnedComCritical(ref _resultsFolder)', [StringComparison]::Ordinal)
if (-not (0 -le $stop -and $stop -lt $unadvise -and $unadvise -lt $unsite -and $unsite -lt $destroy -and $destroy -lt $release)) {
    throw 'ExplorerBrowser destruction order is not request-stop/confirmed-exit -> Unadvise -> Unsite -> Destroy -> release.'
}
if ($hostSource.Contains('_thread.Join(') -or $hostSource.Contains('ManualResetEventSlim') -or
    $hostSource.Contains('Application.DoEvents')) {
    throw 'Taskpane Shell teardown retains a blocking Join, disposable producer signal, or DoEvents loop.'
}
foreach ($required in @(
    'CoWaitDispatchCalls = 0x8u','CoWaitDispatchWindowMessages = 0x10u',
    'FinalizeThreadIdForTesting','TryFinalizeStage','CompleteShutdownAttempt',
    'ShellShutdownResult.Failure','FinalizeFailureInjectorForTesting',
    'FinalizeOperationObserverForTesting','ReleaseOwnedComCritical',
    '_shutdownCallbacks.Contains(completed)',
    '_workerDisposeCompleted','_unadviseCompleted','_unsiteCompleted','_destroyCompleted',
    '_resultsFolderReleaseCompleted','_currentViewReleaseCompleted','_browserReleaseCompleted',
    '_oleUninitializeCompleted','ReportFailureNoThrow','CloseQueueAndTickets','PostToUiForTesting')) {
    if (-not $hostSource.Contains($required)) { throw "RC48 bounded terminal gate missing: $required" }
}
if ($controlSource.Contains('File.Exists') -or $hostSource.Contains('File.Exists')) {
    throw 'Taskpane UI/Shell result population performs a synchronous file existence probe.'
}
if ($controlSource.Contains('MaximumBufferedResults') -or $controlSource.Contains('MaximumResults') -or
    $controlSource.Contains('Take(500)')) {
    throw 'Taskpane retains a product-level result cap.'
}

$tryParse = $bridgePolicy.GetMethod('TryParse', $binding)
$allowedTypesField = $bridgePolicy.GetField('AllowedTypes', $binding)
$allowedTypes = @($allowedTypesField.GetValue($null) | ForEach-Object { [string]$_ } | Sort-Object)
if ($allowedTypes.Count -ne 3 -or ($allowedTypes -join ',') -cne 'ready,retry,search') {
    throw "Taskpane bridge AllowedTypes must be exactly ready/search/retry: $($allowedTypes -join ',')"
}
function Test-BridgeMessage([string]$Json, [bool]$Expected, [string]$Name) {
    $arguments = [object[]]@($Json,$null,$null)
    $actual = [bool]$tryParse.Invoke($null, $arguments)
    if ($actual -ne $Expected) {
        throw "Taskpane bridge '$Name' expected $Expected but was $actual (error=$($arguments[2]))"
    }
}
Test-BridgeMessage '{"type":"ready","payload":{"view":"taskpane"}}' $true 'ready'
Test-BridgeMessage '{"type":"search","payload":{"query":"0404-009","types":{"part":true,"assembly":true,"drawing":true}}}' $true 'search-query-types'
Test-BridgeMessage '{"type":"retry","payload":{}}' $true 'retry'
Test-BridgeMessage '{"type":"search","payload":{"query":"x","mode":"library"}}' $false 'search-mode-forbidden'
Test-BridgeMessage '{"type":"search","payload":{"query":"x","scope":"folder"}}' $false 'search-scope-forbidden'
Test-BridgeMessage '{"type":"search","payload":{"query":"x","rootPath":"C:\\Other"}}' $false 'search-rootPath-forbidden'
Test-BridgeMessage '{"type":"search","payload":{"query":"x","libraryRoots":["C:\\Other"]}}' $false 'search-libraryRoots-forbidden'
Test-BridgeMessage '{"type":"search","payload":{"query":"x","includeSubdirectories":false}}' $false 'search-includeSubdirectories-forbidden'
Test-BridgeMessage '{"type":"mode","payload":{"mode":"all"}}' $false 'mode-command-forbidden'
Test-BridgeMessage '{"type":"selectScope","payload":{"scope":"folder","rootPath":"C:\\Other"}}' $false 'selectScope-command-forbidden'
Test-BridgeMessage '{"type":"browseScope","payload":{}}' $false 'browseScope-command-forbidden'
Test-BridgeMessage '{"type":"restoreScope","payload":{}}' $false 'restoreScope-command-forbidden'
Test-BridgeMessage '{"type":"armExternalFileDrag","payload":{"schema":1,"rowToken":"x"}}' $false 'removed-drag'
Test-BridgeMessage '{"type":"contextAction","payload":{"schema":1,"action":"open","path":"C:\\x.SLDPRT"}}' $false 'removed-result-action'
Test-BridgeMessage '{"type":"exec","payload":{"command":"cmd.exe"}}' $false 'arbitrary-command'

$decodeDelta = $mouseInput.GetMethod('DecodeSignedHighWord', $binding)
foreach ($delta in @(120,-120,1,-1)) {
    $packed = ([int64]($delta -band 0xffff) -shl 16)
    $observed = [int]$decodeDelta.Invoke($null, @([IntPtr]::new($packed)))
    if ($observed -ne $delta) { throw "Horizontal wheel signed delta changed: $delta -> $observed" }
}

$taskpaneHtml = Get-Content -LiteralPath (Join-Path $WebAssetsPath 'taskpane.html') -Raw -Encoding UTF8
$taskpaneScript = Get-Content -LiteralPath (Join-Path $WebAssetsPath 'app.js') -Raw -Encoding UTF8
foreach ($token in @("default-src 'none'",'data-view="taskpane"')) {
    if (-not $taskpaneHtml.Contains($token)) { throw "Taskpane local page token missing: $token" }
}
foreach ($token in @('view === "taskpane"','mode mode-fixed','id="modeButton"','library:')) {
    if (-not $taskpaneScript.Contains($token)) { throw "Fixed Library toolbar token missing: $token" }
}
if ($taskpaneScript.Contains('armExternalFileDrag') -or $taskpaneScript.Contains('rowToken') -or
    $taskpaneScript.Contains('locateLibrary')) {
    throw 'Removed Web result/drag path remains in app.js.'
}

if (-not $SkipBehaviorHarness) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw 'CLR4 x64 csc.exe not found.' }
    $harnessSource = Join-Path $root 'tests\SwSearch.Taskpane.BehaviorHarness\Program.cs'
    $behaviorRoot = Join-Path (Join-Path $root 'artifacts') ('.taskpane-rc48-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $behaviorRoot | Out-Null
    try {
        $behaviorDll = Join-Path $behaviorRoot 'SwSearch.SolidWorksAddin.dll'
        $behaviorExe = Join-Path $behaviorRoot 'SwSearch.Taskpane.BehaviorHarness.exe'
        Copy-Item -LiteralPath $DllPath -Destination $behaviorDll
        Copy-Item -LiteralPath $webViewCorePath -Destination (Join-Path $behaviorRoot 'Microsoft.Web.WebView2.Core.dll')
        Copy-Item -LiteralPath $webViewLoaderPath -Destination (Join-Path $behaviorRoot 'WebView2Loader.dll')
        $compilerArguments = @(
            '/nologo','/target:exe','/platform:x64','/optimize+','/warnaserror+',
            "/out:$behaviorExe", "/reference:$behaviorDll",
            '/reference:System.dll','/reference:System.Core.dll','/reference:System.Runtime.Serialization.dll',
            '/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',
            "/reference:$webViewCorePath", "/link:$SolidWorksInteropPath", $harnessSource
        )
        & $csc $compilerArguments
        if ($LASTEXITCODE -ne 0) { throw "RC48 behavior harness compilation failed: $LASTEXITCODE" }
        & $behaviorExe
        if ($LASTEXITCODE -ne 0) { throw "RC48 behavior harness failed: $LASTEXITCODE" }
    }
    finally {
        if (Test-Path -LiteralPath $behaviorRoot) { Remove-Item -LiteralPath $behaviorRoot -Recurse -Force }
    }
}

[pscustomobject]@{
    Result = 'PASS'
    File = $DllPath
    Bytes = $bytes.Length
    SHA256 = (Get-FileHash -LiteralPath $DllPath -Algorithm SHA256).Hash
    Machine = 'AMD64'
    Version = $assembly.GetName().Version.ToString()
    Interop = 'SOLIDWORKS 2026 official PIA embedded; early-bound COM ABI preserved'
    Toolbar = 'Composition WebView2; fixed Library mode; structured message whitelist 6/6 PASS'
    Results = 'CLSID_ExplorerBrowser + IResultsFolder + original IShellItem paths; Shell-owned verbs/drag/drop'
    Population = 'first 200 then serialized 500-page append to TotalCount; generation cancellation; no total cap'
    Behavior = $(if ($SkipBehaviorHarness) { 'static packaging verification; isolated harness intentionally not repeated' } else { 'isolated RC48 Shell/taskpane harness 30/30 PASS' })
    Baseline = 'No SOLIDWORKS process or MSI was started; real in-process Shell drag reception remains manual'
} | Format-List
