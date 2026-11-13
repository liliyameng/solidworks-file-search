using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwSearch.SolidWorksAddin;

namespace SwSearch.Taskpane.BehaviorHarness
{
    internal static class Program
    {
        private static int _passed;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args != null && args.Length == 1 &&
                    String.Equals(args[0], "--rc73-column-contract", StringComparison.Ordinal))
                {
                    Run("explorer-browser-column-contract", ExplorerBrowserColumnContract);
                    Console.WriteLine("RC73 Shell column contract: 1/1 PASS");
                    return 0;
                }

                Run("event-sink-ccw", EventSinkCcw);
                Run("shell-item-roundtrip", ShellItemRoundTrip);
                Run("explorer-browser-cross-directory", ExplorerBrowserCrossDirectory);
                Run("cancelled-ticket-worker-survives", CancelledTicketWorkerSurvives);
                Run("worker-death-fails-fast", WorkerDeathFailsFast);
                Run("deferred-teardown", DeferredTeardown);
                Run("parent-dispose-deleteview-order", ParentDisposeDeleteViewOrder);
                Run("thread-start-failure-releases-stream", ThreadStartFailureReleasesStream);
                Run("coinitialize-failure-releases-stream", CoInitializeFailureReleasesStream);
                Run("failure-callback-cannot-break-worker-terminal", FailureCallbackCannotBreakWorkerTerminal);
                Run("critical-finalize-failures-retry-on-owner-sta", FinalizeStageFailuresCompleteOnOwnerSta);
                Run("marshal-release-timeout-late-callback-safe", MarshalReleaseTimeoutLateCallbackSafe);
                Run("marshal-release-rejected-post-retry-safe", MarshalReleaseRejectedPostRetrySafe);
                Run("no-handle-shutdown-terminal", NoHandleShutdownTerminal);
                Run("webview-inflight-dispose-rejects-late-publish", WebViewInFlightDisposeRejectsLatePublish);
                Run("webview-inflight-parent-gates-deleteview", WebViewInFlightParentGatesDeleteView);
                Run("webview-published-owner-cleans-once", WebViewPublishedOwnerCleansOnce);
                Run("webview-release-reentry-is-deferred", WebViewReleaseReentryIsDeferred);
                Run("webview-published-release-failures-retry", WebViewPublishedReleaseFailuresRetry);
                Run("webview-handle-generation-reinitializes", WebViewHandleGenerationReinitializes);
                Run("disconnect-waits-for-shell-terminal", DisconnectWaitsForShellTerminal);
                Run("disconnect-timeout-is-bounded", DisconnectTimeoutIsBounded);
                Run("disconnect-targets-only-explicit-owner", DisconnectTargetsOnlyExplicitOwner);
                Run("ui-post-failure-recovers-on-owner-sta", UiPostFailureIsBoundedAndRecoverable);
                Run("repeated-shutdown-is-idempotent", RepeatedShutdownIsIdempotent);
                Run("query-interaction-wire", QueryInteractionWire);
                Run("broker-response-timeout-policy", BrokerResponseTimeoutPolicy);
                Run("search-diagnostics-are-private", SearchDiagnosticsArePrivate);
                Run("diagnostics-root-hash-is-stable-and-distinct", DiagnosticsRootHashIsStableAndDistinct);
                Run("diagnostics-file-writes-are-serialized", DiagnosticsFileWritesAreSerialized);
                Run("user-query-origins", UserQueryOrigins);
                Run("automatic-query-origins", AutomaticQueryOrigins);
                Run("explicit-app-exit-suppresses-broker-start", ExplicitAppExitSuppressesBrokerStart);
                Run("all-pages-no-500-cap", AllPagesNoCap);
                Run("latest-generation-wins", LatestGenerationWins);
                Run("library-fail-closed", LibraryFailClosed);
                Run("fixed-library-web-contract", FixedLibraryWebContract);
                Run("bridge-message-contract", BridgeMessageContract);
                Run("filter-to-roots-boundary-semantics", FilterToRootsBoundarySemantics);
                Run("repeat-create-destroy", RepeatCreateDestroy);
                Console.WriteLine("RC71 Taskpane behavior: " + _passed + "/40 PASS");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void EventSinkCcw()
        {
            Require(Marshal.IsTypeVisibleFromCom(typeof(IExplorerBrowserEvents)),
                "IExplorerBrowserEvents is not visible to COM.");
            Require(Marshal.IsTypeVisibleFromCom(typeof(ExplorerBrowserEventSink)),
                "ExplorerBrowserEventSink is not visible to COM.");
            ExplorerBrowserEventSink sink = new ExplorerBrowserEventSink(
                delegate { return 0; }, delegate { return 0; }, delegate { return 0; }, delegate { return 0; });
            IntPtr pointer = Marshal.GetComInterfaceForObject(sink, typeof(IExplorerBrowserEvents));
            try { Require(pointer != IntPtr.Zero, "CCW QueryInterface for IExplorerBrowserEvents failed."); }
            finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
        }

        private static void ShellItemRoundTrip()
        {
            string root = NewTempRoot();
            try
            {
                string path = Path.Combine(root, "中文 零件 (A)&1.SLDPRT");
                File.WriteAllText(path, "fixture");
                IShellItem item = null;
                Guid iid = typeof(IShellItem).GUID;
                int hr = ShellResultsNative.SHCreateItemFromParsingName(path, null, ref iid, out item);
                Require(hr >= 0 && item != null, "SHCreateItemFromParsingName failed: 0x" + hr.ToString("X8"));
                try
                {
                    string observed = ShellPathPolicy.ReadFileSystemPath(item);
                    Require(String.Equals(Path.GetFullPath(path), observed, StringComparison.OrdinalIgnoreCase),
                        "SIGDN_FILESYSPATH changed the original path.");
                }
                finally
                {
                    if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                }
            }
            finally { DeleteTempRoot(root); }
        }

        private static void ExplorerBrowserCrossDirectory()
        {
            string root = NewTempRoot();
            TaskpaneShellResultsHost host = null;
            Form form = null;
            try
            {
                string firstDirectory = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
                string secondDirectory = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
                string first = Path.Combine(firstDirectory, "First.SLDPRT");
                string second = Path.Combine(secondDirectory, "Second.SLDASM");
                File.WriteAllText(first, "fixture");
                File.WriteAllText(second, "fixture");

                bool ready = false;
                string failure = null;
                host = new TaskpaneShellResultsHost();
                host.HostReady += delegate { ready = true; };
                host.HostFailure += delegate(object sender, ShellResultsFailureEventArgs args)
                {
                    failure = (failure == null ? String.Empty : failure + " | ") + args.Stage + ": " +
                        (args.Exception == null ? "unknown" : args.Exception.ToString());
                };
                form = new Form
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Left = -32000,
                    Top = -32000,
                    Width = 640,
                    Height = 420
                };
                form.Controls.Add(host);
                form.Show();
                Require(PumpUntil(delegate { return ready; }, TimeSpan.FromSeconds(5)),
                    "ExplorerBrowser/IResultsFolder did not become ready. " + failure);

                using (ShellPopulationTicket ticket = host.Replace(44, new[] { first, second }))
                {
                    Require(PumpUntil(delegate { return ticket.IsCompleted; }, TimeSpan.FromSeconds(5)),
                        "Cross-directory IResultsFolder population did not complete.");
                    Require(ticket.Applied && ticket.Added == 2 && ticket.Skipped == 0,
                        "Cross-directory real Shell items were not both accepted.");
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                using (ShellPopulationTicket missing = host.Append(44,
                    new[] { Path.Combine(root, "missing.SLDASM") }))
                {
                    stopwatch.Stop();
                    Require(stopwatch.ElapsedMilliseconds < 50,
                        "A missing/offline item blocked the UI queue call.");
                    Require(PumpUntil(delegate { return missing.IsCompleted; }, TimeSpan.FromSeconds(5)),
                        "A missing item stranded the population worker.");
                }
            }
            finally
            {
                if (host != null)
                {
                    host.Dispose();
                    Require(PumpUntil(delegate { return host.OwnedResourceCountForTesting == 0; },
                            TimeSpan.FromSeconds(5)),
                        "ExplorerBrowser lifecycle leaked an owned COM resource.");
                }
                if (form != null) form.Close();
                if (form != null) form.Dispose();
                DeleteTempRoot(root);
            }
        }

        private static void ExplorerBrowserColumnContract()
        {
            TaskpaneShellResultsHost host = null;
            Form form = null;
            try
            {
                int[] requested = new[] { 311, 157, 433 };
                host = new TaskpaneShellResultsHost();
                host.ConfigureColumns(requested);
                bool ready = false;
                string failure = null;
                host.HostReady += delegate { ready = true; };
                host.HostFailure += delegate(object sender, ShellResultsFailureEventArgs args)
                {
                    failure = (failure == null ? String.Empty : failure + " | ") + args.Stage + ": " +
                        (args.Exception == null ? "unknown" : args.Exception.ToString());
                };
                form = NewHiddenForm();
                form.Controls.Add(host);
                form.Show();
                Require(PumpUntil(delegate { return ready; }, TimeSpan.FromSeconds(5)),
                    "ExplorerBrowser did not expose the strict column contract. " + failure);

                int[] observed = host.ReadConfiguredColumnWidthsForTesting();
                Require(observed.Length == requested.Length,
                    "ExplorerBrowser returned the wrong visible-column count.");
                for (int index = 0; index < requested.Length; index++)
                    Require(observed[index] == requested[index],
                        "ExplorerBrowser column width did not round-trip at index " + index + ".");
            }
            finally
            {
                if (host != null)
                {
                    host.Dispose();
                    Require(PumpUntil(delegate { return host.OwnedResourceCountForTesting == 0; },
                            TimeSpan.FromSeconds(5)),
                        "ExplorerBrowser column gate leaked an owned COM resource.");
                }
                if (form != null) form.Close();
                if (form != null) form.Dispose();
            }
        }

        private static void CancelledTicketWorkerSurvives()
        {
            string root = NewTempRoot();
            TaskpaneShellResultsHost host = null;
            Form form = null;
            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            int addCalls = 0;
            try
            {
                string oldPath = Path.Combine(root, "Old.SLDPRT");
                string newPath = Path.Combine(root, "New.SLDASM");
                File.WriteAllText(oldPath, "fixture");
                File.WriteAllText(newPath, "fixture");
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = delegate(string stage)
                {
                    if (stage != "before-add-item" || Interlocked.Increment(ref addCalls) != 1) return;
                    entered.Set();
                    release.Wait();
                };
                host = CreateReadyHost(out form);
                ShellPopulationTicket oldTicket = host.Replace(43, new[] { oldPath });
                Require(PumpUntil(delegate { return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                    "Old generation did not become active.");
                CancellationTokenSource cancellation = new CancellationTokenSource();
                Task consumer = Task.Factory.StartNew(delegate
                {
                    try { oldTicket.Wait(cancellation.Token); }
                    catch (OperationCanceledException) { }
                });
                cancellation.Cancel();
                Require(consumer.Wait(TimeSpan.FromSeconds(1)), "Cancelled consumer wait did not return.");
                oldTicket.Dispose();
                host.CancelBefore(44);
                release.Set();

                using (ShellPopulationTicket current = host.Replace(44, new[] { newPath }))
                {
                    Require(PumpUntil(delegate { return current.IsCompleted; }, TimeSpan.FromSeconds(5)),
                        "A cancelled/disposed old ticket killed the population worker.");
                    Require(current.Applied && current.Added == 1,
                        "The new generation was not applied after old-ticket cancellation.");
                }
                Require(PumpUntil(delegate { return oldTicket.IsCompleted; }, TimeSpan.FromSeconds(2)),
                    "Producer could not safely complete the disposed old ticket.");
            }
            finally
            {
                release.Set();
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = null;
                if (host != null)
                {
                    host.Dispose();
                    PumpUntil(delegate { return host.ShutdownCompletedForTesting; }, TimeSpan.FromSeconds(5));
                }
                if (form != null) { form.Close(); form.Dispose(); }
                DeleteTempRoot(root);
            }
        }

        private static void WorkerDeathFailsFast()
        {
            ManualResetEventSlim failure = new ManualResetEventSlim(false);
            ShellResultsPopulationWorker worker = new ShellResultsPopulationWorker(
                IntPtr.Zero,
                delegate { return 1; },
                delegate { failure.Set(); },
                delegate { throw new COMException("fixture-unmarshal", unchecked((int)0x80004005)); },
                null);
            worker.Start();
            Require(worker.ExitedWaitHandle.WaitOne(TimeSpan.FromSeconds(2)) && failure.IsSet,
                "Unmarshal failure did not transition the worker to dead.");
            using (ShellPopulationTicket ticket = worker.Append(1, new[] { "C:\\Library\\Late.SLDPRT" }))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                ticket.Wait(CancellationToken.None);
                stopwatch.Stop();
                Require(stopwatch.ElapsedMilliseconds < 50 && ticket.IsCompleted && !ticket.Applied,
                    "A dead worker accepted a ticket that could wait forever.");
            }
            worker.DisposeAfterExit();
        }

        private static void DeferredTeardown()
        {
            string root = NewTempRoot();
            TaskpaneShellResultsHost host = null;
            Form form = null;
            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            try
            {
                string path = Path.Combine(root, "Blocked.SLDPRT");
                File.WriteAllText(path, "fixture");
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = delegate(string stage)
                {
                    if (stage != "before-add-item") return;
                    entered.Set();
                    release.Wait();
                };
                host = CreateReadyHost(out form);
                ShellPopulationTicket ticket = host.Replace(44, new[] { path });
                Require(PumpUntil(delegate { return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                    "Controlled AddItem did not become active.");
                Stopwatch stopwatch = Stopwatch.StartNew();
                host.Dispose();
                stopwatch.Stop();
                Require(stopwatch.ElapsedMilliseconds < 1000,
                    "UI teardown synchronously froze while the worker was active.");
                Require(host.DestroyCallCountForTesting == 0,
                    "ExplorerBrowser.Destroy ran before the worker released its proxy.");
                release.Set();
                Require(PumpUntil(delegate
                {
                    return host.DestroyCallCountForTesting == 1 && host.OwnedResourceCountForTesting == 0;
                }, TimeSpan.FromSeconds(5)), "Deferred teardown did not finish after worker exit.");
                ticket.Dispose();
            }
            finally
            {
                release.Set();
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = null;
                if (host != null && host.OwnedResourceCountForTesting != 0) host.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
                DeleteTempRoot(root);
            }
        }

        private static void ParentDisposeDeleteViewOrder()
        {
            string root = NewTempRoot();
            TaskpaneControl control = null;
            TaskpaneShellResultsHost host = null;
            Form form = null;
            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            bool deleteViewEquivalent = false;
            try
            {
                string path = Path.Combine(root, "ParentGate.SLDPRT");
                File.WriteAllText(path, "fixture");
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = delegate(string stage)
                {
                    if (stage != "before-add-item") return;
                    entered.Set();
                    release.Wait();
                };
                host = new TaskpaneShellResultsHost();
                bool ready = false;
                host.HostReady += delegate { ready = true; };
                control = new TaskpaneControl(
                    null,
                    delegate(LiveSearchRequest request, CancellationToken token)
                    {
                        return Response(request, 0, 0, "None");
                    },
                    TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                    host);
                form = new Form
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Left = -32000,
                    Top = -32000,
                    Width = 640,
                    Height = 420
                };
                form.Controls.Add(control);
                form.Show();
                Require(PumpUntil(delegate { return ready; }, TimeSpan.FromSeconds(5)),
                    "Nested Shell host did not become ready.");
                long stableGeneration = -1;
                DateTime stableSince = DateTime.UtcNow;
                Require(PumpUntil(delegate
                {
                    long current = control.ResultSetGenerationForTesting;
                    if (current != stableGeneration)
                    {
                        stableGeneration = current;
                        stableSince = DateTime.UtcNow;
                    }
                    return current > 0 && DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(500);
                }, TimeSpan.FromSeconds(5)), "Taskpane startup generations did not settle.");
                ShellPopulationTicket ticket = host.Replace(stableGeneration, new[] { path });
                Require(PumpUntil(delegate { return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                    "Controlled AddItem did not become active through TaskpaneControl.");

                Stopwatch stopwatch = Stopwatch.StartNew();
                control.Dispose();
                control.BeginShutdown(delegate
                {
                    Require(control.ParentDisposeCompletedForTesting,
                        "DeleteView-equivalent callback preceded parent base.Dispose.");
                    Require(host.DestroyCallCountForTesting == 1 && host.OwnedResourceCountForTesting == 0,
                        "DeleteView-equivalent callback preceded Shell COM teardown.");
                    deleteViewEquivalent = true;
                });
                stopwatch.Stop();
                Require(stopwatch.ElapsedMilliseconds < 1000,
                    "TaskpaneControl.Dispose synchronously blocked the UI.");
                Require(!control.ParentDisposeCompletedForTesting && !control.IsDisposed,
                    "Parent HWND/control was disposed while AddItem was still active.");
                Require(host.DestroyCallCountForTesting == 0,
                    "ExplorerBrowser was destroyed before the worker released IResultsFolder.");

                release.Set();
                Require(PumpUntil(delegate { return deleteViewEquivalent; }, TimeSpan.FromSeconds(5)),
                    "Host -> parent dispose -> DeleteView-equivalent handshake did not finish.");
                ticket.Dispose();
            }
            finally
            {
                release.Set();
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = null;
                if (control != null && !control.IsDisposed) control.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
                DeleteTempRoot(root);
            }
        }

        private static void ThreadStartFailureReleasesStream()
        {
            TaskpaneShellResultsHost.BeforeWorkerThreadStartForTesting = delegate
            {
                throw new InvalidOperationException("controlled-thread-start-failure");
            };
            try
            {
                AssertWorkerInitializationFailureClosesStream("results-worker-start");
            }
            finally { TaskpaneShellResultsHost.BeforeWorkerThreadStartForTesting = null; }
        }

        private static void CoInitializeFailureReleasesStream()
        {
            TaskpaneShellResultsHost.WorkerCoInitializeForTesting = delegate
            {
                return unchecked((int)0x80004005);
            };
            try
            {
                AssertWorkerInitializationFailureClosesStream("results-worker");
            }
            finally { TaskpaneShellResultsHost.WorkerCoInitializeForTesting = null; }
        }

        private static void AssertWorkerInitializationFailureClosesStream(string expectedStage)
        {
            TaskpaneShellResultsHost host = new TaskpaneShellResultsHost();
            Form form = null;
            string failures = String.Empty;
            host.HostFailure += delegate(object sender, ShellResultsFailureEventArgs args)
            {
                failures += "|" + args.Stage;
            };
            try
            {
                form = new Form
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Left = -32000,
                    Top = -32000,
                    Width = 320,
                    Height = 240
                };
                form.Controls.Add(host);
                form.Show();
                Require(PumpUntil(delegate
                {
                    return host.ShutdownCompletedForTesting &&
                        MarshaledInterfaceStream.OutstandingForTesting == 0;
                }, TimeSpan.FromSeconds(5)),
                    "Worker initialization failure stranded its real marshal stream.");
                Require(failures.IndexOf(expectedStage, StringComparison.Ordinal) >= 0,
                    "Expected worker failure stage was not reported: " + expectedStage + " in " + failures);
                using (ShellPopulationTicket ticket = host.Append(44, new[] { "C:\\Library\\Late.SLDPRT" }))
                {
                    Require(ticket.IsCompleted && !ticket.Applied,
                        "A failed worker accepted a future population ticket.");
                }
            }
            finally
            {
                host.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
            }
        }

        private static void FailureCallbackCannotBreakWorkerTerminal()
        {
            int baseline = MarshaledInterfaceStream.OutstandingForTesting;
            int releases = 0;
            MarshaledInterfaceStream stream = new MarshaledInterfaceStream(
                new IntPtr(44), delegate
                {
                    Interlocked.Increment(ref releases);
                    return true;
                });
            ShellResultsPopulationWorker worker = new ShellResultsPopulationWorker(
                stream,
                delegate { return 44; },
                delegate { throw new InvalidOperationException("controlled-failure-callback"); },
                null,
                null,
                delegate { throw new InvalidOperationException("controlled-thread-start-failure"); },
                null);
            bool threw = false;
            try { worker.Start(); }
            catch (InvalidOperationException) { threw = true; }
            Require(threw, "Controlled Thread.Start failure was not observed.");
            Require(worker.ExitedWaitHandle.WaitOne(0),
                "A throwing failure callback prevented the worker Exited terminal.");
            Require(releases == 1 && MarshaledInterfaceStream.OutstandingForTesting == baseline,
                "A throwing failure callback stranded the marshal stream.");
            Require(!worker.IsAcceptingCommandsForTesting && worker.PendingCommandCountForTesting == 0,
                "A throwing failure callback left the worker queue open.");
            using (ShellPopulationTicket ticket = worker.Append(44, new[] { "C:\\Library\\Late.SLDPRT" }))
            {
                Require(ticket.IsCompleted && !ticket.Applied,
                    "A terminal worker accepted a ticket after start failure.");
            }
            worker.DisposeAfterExit();

            ShellResultsPopulationWorker runFailure = new ShellResultsPopulationWorker(
                IntPtr.Zero,
                delegate { return 44; },
                delegate { throw new InvalidOperationException("controlled-run-failure-callback"); },
                delegate { throw new COMException("controlled-unmarshal", unchecked((int)0x80004005)); },
                null);
            runFailure.Start();
            Require(runFailure.ExitedWaitHandle.WaitOne(TimeSpan.FromSeconds(2)),
                "A throwing run-failure callback prevented the worker Exited terminal.");
            Require(!runFailure.IsAcceptingCommandsForTesting && runFailure.PendingCommandCountForTesting == 0,
                "Run failure did not close the queue and pending tickets.");
            runFailure.DisposeAfterExit();
        }

        private static void FinalizeStageFailuresCompleteOnOwnerSta()
        {
            TaskpaneShellResultsHost host = null;
            TaskpaneControl control = null;
            Form form = null;
            List<string> completedStages = new List<string>();
            string failureStage = null;
            int deleteCalls = 0;
            try
            {
                long generation;
                control = CreateReadyTaskpaneControl(out host, out form, out generation);
                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                string[] expected = new[]
                {
                    "worker-dispose", "unadvise", "unsite", "destroy",
                    "release-results-folder", "release-current-view", "release-browser", "ole-uninitialize"
                };

                TaskpaneShellResultsHost.FinalizeOperationObserverForTesting = delegate(string stage)
                {
                    Require(Thread.CurrentThread.ManagedThreadId == ownerThread,
                        "A critical finalize operation ran outside the owner STA.");
                    completedStages.Add(stage);
                };
                TaskpaneShellResultsHost.FinalizeFailureInjectorForTesting = delegate(string stage)
                {
                    return String.Equals(stage, failureStage, StringComparison.Ordinal)
                        ? new COMException("controlled-production-stage-" + stage, unchecked((int)0x80004005))
                        : null;
                };

                foreach (string stage in expected)
                {
                    failureStage = stage;
                    bool disconnected = SwAddin.DisconnectTaskpaneOnUi(
                        control, delegate { deleteCalls++; return true; }, 1200);
                    Require(!disconnected && deleteCalls == 0,
                        "Critical stage failure was reported as a successful disconnect: " + stage);
                    Require(!control.ParentDisposeCompletedForTesting && !control.IsDisposed,
                        "Critical stage failure released the Taskpane parent: " + stage);
                    Require(!host.ShutdownCompletedForTesting,
                        "Critical stage failure marked the Shell terminal successful: " + stage);
                }

                failureStage = null;
                bool retry = SwAddin.DisconnectTaskpaneOnUi(
                    control, delegate { deleteCalls++; return true; }, 3000);
                Require(retry && deleteCalls == 1,
                    "Owner-STA retry did not complete after all critical failures were removed.");
                Require(host.FinalizeThreadIdForTesting == ownerThread &&
                    host.FinalizeThreadIdForTesting == host.UiThreadIdForTesting,
                    "Shell Finalize did not run on the original UI STA.");
                Require(host.DestroyCallCountForTesting == 1 && host.OwnedResourceCountForTesting == 0,
                    "Critical-stage retries duplicated Destroy or stranded a Shell resource.");
                Require(control.ParentDisposeCompletedForTesting && control.ShutdownSucceededForTesting,
                    "Successful Shell retry did not release the Taskpane parent.");
                Require(completedStages.SequenceEqual(expected),
                    "Successful critical stages repeated or ran out of order: " +
                    String.Join(",", completedStages.ToArray()));
            }
            finally
            {
                TaskpaneShellResultsHost.FinalizeFailureInjectorForTesting = null;
                TaskpaneShellResultsHost.FinalizeOperationObserverForTesting = null;
                if (control != null && !control.IsDisposed) control.Dispose();
                else if (host != null && !host.IsDisposed) host.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
            }
        }

        private static void MarshalReleaseTimeoutLateCallbackSafe()
        {
            int baseline = MarshaledInterfaceStream.OutstandingForTesting;
            int actualReleases = 0;
            Action lateCallback = null;
            object callbackGate = new object();
            TaskpaneShellResultsHost host = null;
            Form form = null;
            string failureStages = String.Empty;
            try
            {
                TaskpaneShellResultsHost.WorkerCoInitializeForTesting = delegate
                {
                    return unchecked((int)0x80004005);
                };
                TaskpaneShellResultsHost.MarshalReleasePostForTesting = delegate(Action callback)
                {
                    lock (callbackGate) lateCallback = callback;
                    return true;
                };
                TaskpaneShellResultsHost.MarshalReleaseCoreObserverForTesting = delegate
                {
                    Interlocked.Increment(ref actualReleases);
                };

                host = new TaskpaneShellResultsHost();
                host.HostFailure += delegate(object sender, ShellResultsFailureEventArgs args)
                {
                    failureStages += "|" + args.Stage;
                };
                form = new Form
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Left = -32000,
                    Top = -32000,
                    Width = 640,
                    Height = 420
                };
                form.Controls.Add(host);
                form.Show();

                Require(PumpUntil(delegate
                {
                    lock (callbackGate) return lateCallback != null;
                }, TimeSpan.FromSeconds(3)), "Marshal release was not dispatched to the owner STA.");
                Require(PumpUntil(delegate
                {
                    return failureStages.IndexOf("results-worker-dispose", StringComparison.Ordinal) >= 0;
                }, TimeSpan.FromSeconds(5)),
                    "A timed-out accepted marshal release did not fail the critical worker-dispose stage.");
                Require(!host.ShutdownCompletedForTesting &&
                    MarshaledInterfaceStream.OutstandingForTesting == baseline + 1 && actualReleases == 0,
                    "Marshal ownership was lost before the accepted late callback executed.");

                Action callbackToRun;
                lock (callbackGate) callbackToRun = lateCallback;
                callbackToRun();
                Require(actualReleases == 1 &&
                    MarshaledInterfaceStream.OutstandingForTesting == baseline,
                    "The accepted late callback did not perform exactly one real marshal release.");

                TaskpaneShellResultsHost.MarshalReleasePostForTesting = null;
                ShellShutdownResult terminal = null;
                host.BeginShutdown(delegate(ShellShutdownResult result) { terminal = result; });
                Require(PumpUntil(delegate
                {
                    return terminal != null && terminal.Succeeded && host.ShutdownCompletedForTesting;
                }, TimeSpan.FromSeconds(3)),
                    "Owner-STA retry did not finish after the pending marshal release completed.");
                Require(host.OwnedResourceCountForTesting == 0 && host.DestroyCallCountForTesting == 1,
                    "Late marshal release retry stranded or duplicated Shell teardown.");
            }
            finally
            {
                TaskpaneShellResultsHost.WorkerCoInitializeForTesting = null;
                TaskpaneShellResultsHost.MarshalReleasePostForTesting = null;
                TaskpaneShellResultsHost.MarshalReleaseCoreObserverForTesting = null;
                if (host != null && !host.IsDisposed) host.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
            }
            Require(MarshaledInterfaceStream.OutstandingForTesting == baseline,
                "Late-callback test left a marshaled interface stream outstanding.");
        }

        private static void NoHandleShutdownTerminal()
        {
            TaskpaneShellResultsHost host = new TaskpaneShellResultsHost();
            host.Dispose();
            Require(host.IsDisposed && host.ShutdownCompletedForTesting &&
                host.OwnedResourceCountForTesting == 0,
                "Dispose before handle creation did not reach a bounded successful terminal.");
            ShellShutdownResult terminal = null;
            host.BeginShutdown(delegate(ShellShutdownResult result) { terminal = result; });
            Require(terminal != null && terminal.Succeeded,
                "No-handle shutdown did not retain its explicit successful terminal result.");
        }

        private static void WebViewInFlightDisposeRejectsLatePublish()
        {
            TaskpaneWebViewInitializationLifetime lifetime =
                new TaskpaneWebViewInitializationLifetime();
            int epoch = lifetime.Capture();
            TaskCompletionSource<DisposalProbe> creation =
                new TaskCompletionSource<DisposalProbe>();
            ManualResetEventSlim readyToPublish = new ManualResetEventSlim(false);
            ManualResetEventSlim continuePublish = new ManualResetEventSlim(false);
            DisposalProbe late = new DisposalProbe();
            int published = 0;
            Task continuation = Task.Factory.StartNew(delegate
            {
                DisposalProbe candidate = creation.Task.Result;
                readyToPublish.Set();
                continuePublish.Wait();
                lifetime.TryPublish(
                    epoch,
                    candidate,
                    delegate { Interlocked.Increment(ref published); },
                    delegate(DisposalProbe rejected) { rejected.Dispose(); });
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

            creation.SetResult(late);
            Require(readyToPublish.Wait(TimeSpan.FromSeconds(2)),
                "Controlled WebView initialization did not reach the pre-publish gate.");
            lifetime.Invalidate();
            continuePublish.Set();
            Require(continuation.Wait(TimeSpan.FromSeconds(2)),
                "Invalidated WebView initialization continuation did not terminate.");
            Require(published == 0 && late.DisposeCount == 1 && !lifetime.IsCurrent(epoch),
                "A late WebView initialization published after Dispose or failed to self-clean.");

            TaskpaneWebViewInitializationLifetime activeLifetime =
                new TaskpaneWebViewInitializationLifetime();
            int activeEpoch = activeLifetime.Capture();
            DisposalProbe active = new DisposalProbe();
            int activePublished = 0;
            Require(activeLifetime.TryPublish(
                    activeEpoch,
                    active,
                    delegate { Interlocked.Increment(ref activePublished); },
                    delegate(DisposalProbe rejected) { rejected.Dispose(); }) &&
                activePublished == 1 && active.DisposeCount == 0,
                "A current WebView initialization could not publish exactly once.");
            active.Dispose(); // represents later host ownership cleanup
            activeLifetime.Invalidate();
            Require(active.DisposeCount == 1,
                "Published WebView initialization ownership was not released exactly once.");

            TaskpaneWebViewInitializationLifetime lockedLifetime =
                new TaskpaneWebViewInitializationLifetime();
            int lockedEpoch = lockedLifetime.Capture();
            DisposalProbe lockedCandidate = new DisposalProbe();
            ManualResetEventSlim publishEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim releasePublish = new ManualResetEventSlim(false);
            ManualResetEventSlim invalidateStarted = new ManualResetEventSlim(false);
            ManualResetEventSlim invalidateCompleted = new ManualResetEventSlim(false);
            int lockedPublished = 0;
            bool lockedAccepted = false;
            Task publisher = Task.Factory.StartNew(delegate
            {
                lockedAccepted = lockedLifetime.TryPublish(
                    lockedEpoch,
                    lockedCandidate,
                    delegate
                    {
                        publishEntered.Set();
                        releasePublish.Wait();
                        Interlocked.Increment(ref lockedPublished);
                    },
                    delegate(DisposalProbe rejected) { rejected.Dispose(); });
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            Require(publishEntered.Wait(TimeSpan.FromSeconds(2)),
                "Controlled WebView publish did not enter the epoch lock.");
            Task invalidator = Task.Factory.StartNew(delegate
            {
                invalidateStarted.Set();
                lockedLifetime.Invalidate();
                invalidateCompleted.Set();
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            Require(invalidateStarted.Wait(TimeSpan.FromSeconds(2)) && !invalidateCompleted.Wait(0),
                "WebView Invalidate bypassed an in-progress atomic publish.");
            releasePublish.Set();
            Require(Task.WaitAll(new[] { publisher, invalidator }, TimeSpan.FromSeconds(2)),
                "WebView publish/Invalidate mutual exclusion did not terminate.");
            Require(lockedAccepted && lockedPublished == 1 &&
                lockedCandidate.DisposeCount == 0 && !lockedLifetime.IsCurrent(lockedEpoch),
                "WebView publish/Invalidate ownership was not serialized.");
            lockedCandidate.Dispose();
        }

        private static void WebViewInFlightParentGatesDeleteView()
        {
            TaskCompletionSource<object> environmentGate = new TaskCompletionSource<object>();
            TaskCompletionSource<object> controllerGate = new TaskCompletionSource<object>();
            ManualResetEventSlim environmentEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim controllerEntered = new ManualResetEventSlim(false);
            TaskpaneControl control = null;
            Form form = null;
            int configured = 0;
            int navigated = 0;
            int closeAttempts = 0;
            int closed = 0;
            int released = 0;
            int releaseThread = 0;
            bool releaseSawParentHandle = false;
            bool allowClose = false;
            IntPtr factoryHandle = IntPtr.Zero;
            int deleteCalls = 0;
            try
            {
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting =
                    async delegate(IntPtr ownerHandle, string assetRoot)
                    {
                        factoryHandle = ownerHandle;
                        environmentEntered.Set();
                        await environmentGate.Task;
                        controllerEntered.Set();
                        await controllerGate.Task;
                        return TaskpaneWebViewInitializationLease.CreateForTesting(
                            delegate(IntPtr ignored) { Interlocked.Increment(ref configured); },
                            delegate(IntPtr ignored) { Interlocked.Increment(ref navigated); },
                            delegate
                            {
                                Interlocked.Increment(ref closeAttempts);
                                releaseThread = Thread.CurrentThread.ManagedThreadId;
                                releaseSawParentHandle = control != null && control.IsHandleCreated;
                                if (!allowClose)
                                    throw new InvalidOperationException("Injected in-flight controller Close failure.");
                                Interlocked.Increment(ref closed);
                            },
                            delegate
                            {
                                Interlocked.Increment(ref released);
                            });
                    };
                RecordingShellTarget shell = new RecordingShellTarget();
                control = new TaskpaneControl(
                    null,
                    delegate(LiveSearchRequest request, CancellationToken token)
                    {
                        return Response(request, 0, 0, "WebInflight");
                    },
                    TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                    shell);
                form = NewHiddenForm();
                form.Controls.Add(control);
                form.Show();
                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                Require(PumpUntil(delegate { return environmentEntered.IsSet; }, TimeSpan.FromSeconds(3)),
                    "Injected WebView environment stage did not start.");

                Stopwatch stopwatch = Stopwatch.StartNew();
                bool first = SwAddin.DisconnectTaskpaneOnUi(
                    control, delegate { deleteCalls++; return true; }, 350);
                stopwatch.Stop();
                Require(!first && deleteCalls == 0 &&
                    !control.ParentDisposeCompletedForTesting &&
                    !control.WebViewShutdownCompletedForTesting &&
                    control.IsHandleCreated && form.IsHandleCreated,
                    "Parent/DeleteView completed while WebView environment creation was in flight.");
                Require(stopwatch.ElapsedMilliseconds >= 300 && stopwatch.ElapsedMilliseconds < 1400,
                    "In-flight WebView parent gate was not bounded.");

                environmentGate.SetResult(null);
                Require(PumpUntil(delegate { return controllerEntered.IsSet; }, TimeSpan.FromSeconds(3)),
                    "Injected WebView controller stage did not start.");
                Require(deleteCalls == 0 && control.IsHandleCreated &&
                    configured == 0 && navigated == 0 && closed == 0 && released == 0,
                    "Parent or fake WebView lease advanced while controller creation was gated.");

                controllerGate.SetResult(null);
                Require(PumpUntil(delegate
                {
                    return closeAttempts != 0 &&
                        !control.WebViewShutdownCompletedForTesting &&
                        !control.ParentDisposeCompletedForTesting;
                }, TimeSpan.FromSeconds(3)),
                    "Late WebView Close failure did not retain a non-terminal owner-STA lease.");
                Require(closed == 0 && released == 0 && deleteCalls == 0 &&
                    control.IsHandleCreated && form.IsHandleCreated,
                    "Failed late WebView Close released ownership or parent HWND state.");
                allowClose = true;
                bool retry = SwAddin.DisconnectTaskpaneOnUi(
                    control,
                    delegate
                    {
                        Require(closed == 1 && released == 1 &&
                            releaseThread == ownerThread &&
                            releaseSawParentHandle,
                            "Late WebView lease did not self-clean once on the owner STA before DeleteView.");
                        Require(control.WebViewShutdownCompletedForTesting &&
                            control.ParentDisposeCompletedForTesting,
                            "DeleteView ran before WebView and parent terminal.");
                        deleteCalls++;
                        return true;
                    },
                    3000);
                Require(retry && deleteCalls == 1 && factoryHandle != IntPtr.Zero &&
                    configured == 0 && navigated == 0 && closeAttempts >= 2 &&
                    closed == 1 && released == 1,
                    "Late WebView controller completion published, navigated, leaked, or blocked recovery. " +
                    "retry=" + retry + " delete=" + deleteCalls + " handle=" + factoryHandle +
                    " configured=" + configured + " navigated=" + navigated +
                    " closed=" + closed + " released=" + released);
            }
            finally
            {
                environmentGate.TrySetResult(null);
                controllerGate.TrySetResult(null);
                allowClose = true;
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting = null;
                if (control != null && !control.IsDisposed)
                {
                    control.ShutdownAndWaitOnUi(2000);
                    control.Dispose();
                }
                if (form != null) { form.Close(); form.Dispose(); }
            }
        }

        private static void WebViewPublishedOwnerCleansOnce()
        {
            TaskpaneControl control = null;
            Form form = null;
            int configured = 0;
            int navigated = 0;
            int closed = 0;
            int released = 0;
            int configureThread = 0;
            int navigateThread = 0;
            int releaseThread = 0;
            bool releaseSawParentHandle = false;
            IntPtr configuredHandle = IntPtr.Zero;
            IntPtr navigatedHandle = IntPtr.Zero;
            int deleteCalls = 0;
            try
            {
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting =
                    delegate(IntPtr ownerHandle, string assetRoot)
                    {
                        return Task.FromResult(TaskpaneWebViewInitializationLease.CreateForTesting(
                            delegate(IntPtr handle)
                            {
                                configuredHandle = handle;
                                configureThread = Thread.CurrentThread.ManagedThreadId;
                                Interlocked.Increment(ref configured);
                            },
                            delegate(IntPtr handle)
                            {
                                navigatedHandle = handle;
                                navigateThread = Thread.CurrentThread.ManagedThreadId;
                                Interlocked.Increment(ref navigated);
                            },
                            delegate
                            {
                                Interlocked.Increment(ref closed);
                                releaseThread = Thread.CurrentThread.ManagedThreadId;
                                releaseSawParentHandle = control != null && control.IsHandleCreated;
                            },
                            delegate
                            {
                                Interlocked.Increment(ref released);
                            }));
                    };
                RecordingShellTarget shell = new RecordingShellTarget();
                control = new TaskpaneControl(
                    null,
                    delegate(LiveSearchRequest request, CancellationToken token)
                    {
                        return Response(request, 0, 0, "WebPublished");
                    },
                    TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                    shell);
                form = NewHiddenForm();
                form.Controls.Add(control);
                form.Show();
                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                Require(PumpUntil(delegate { return navigated == 1; }, TimeSpan.FromSeconds(3)),
                    "Injected WebView lease did not publish and navigate.");
                Require(configured == 1 && configuredHandle == control.WebHostForTesting.Handle &&
                    navigatedHandle == configuredHandle &&
                    configureThread == ownerThread && navigateThread == ownerThread,
                    "Injected WebView lease did not configure/navigate the captured owner HWND on its STA.");

                bool disconnected = SwAddin.DisconnectTaskpaneOnUi(
                    control,
                    delegate
                    {
                        Require(closed == 1 && released == 1 &&
                            releaseThread == ownerThread &&
                            releaseSawParentHandle,
                            "Published WebView lease was not released once on owner STA before DeleteView.");
                        deleteCalls++;
                        return true;
                    },
                    1000);
                Require(disconnected && deleteCalls == 1 && closed == 1 && released == 1 &&
                    control.WebViewShutdownCompletedForTesting &&
                    control.ParentDisposeCompletedForTesting,
                    "Published WebView lease did not reach the complete parent terminal.");
                control.Dispose();
                Require(closed == 1 && released == 1,
                    "Repeated parent Dispose released the published WebView lease more than once.");
            }
            finally
            {
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting = null;
                if (control != null && !control.IsDisposed) control.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
            }
        }

        private static void WebViewReleaseReentryIsDeferred()
        {
            TaskpaneCompositionWebViewHost host = null;
            Form form = null;
            int navigated = 0;
            int closeAttempts = 0;
            int closed = 0;
            int released = 0;
            int reentries = 0;
            int terminalCallbacks = 0;
            int prematureTerminal = 0;
            int wrongThread = 0;
            try
            {
                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting =
                    delegate(IntPtr ownerHandle, string assetRoot)
                    {
                        return Task.FromResult(TaskpaneWebViewInitializationLease.CreateForTesting(
                            delegate(IntPtr ignored) { },
                            delegate(IntPtr ignored) { Interlocked.Increment(ref navigated); },
                            delegate
                            {
                                if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                                    Interlocked.Increment(ref wrongThread);
                                Interlocked.Increment(ref closeAttempts);
                                if (Interlocked.Increment(ref reentries) == 1)
                                {
                                    host.BeginShutdown(delegate
                                    {
                                        Interlocked.Increment(ref terminalCallbacks);
                                    });
                                    if (host.ShutdownCompletedForTesting)
                                        Interlocked.Increment(ref prematureTerminal);
                                }
                                Interlocked.Increment(ref closed);
                            },
                            delegate
                            {
                                if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                                    Interlocked.Increment(ref wrongThread);
                                Interlocked.Increment(ref released);
                            }));
                    };
                host = new TaskpaneCompositionWebViewHost();
                form = NewHiddenForm();
                form.Controls.Add(host);
                form.Show();
                Require(PumpUntil(delegate { return navigated == 1; }, TimeSpan.FromSeconds(3)),
                    "Reentry WebView lease did not publish and navigate.");

                host.BeginShutdown(delegate { Interlocked.Increment(ref terminalCallbacks); });
                Require(host.ShutdownCompletedForTesting &&
                    closeAttempts == 1 && closed == 1 && released == 1 &&
                    reentries == 1 && terminalCallbacks == 2 &&
                    prematureTerminal == 0 && wrongThread == 0,
                    "Owner-STA release reentry recursively closed, double-released, or proved an early terminal.");

                host.Dispose();
                Require(host.IsDisposed && closeAttempts == 1 && released == 1,
                    "Dispose repeated a release stage after reentry recovery.");
            }
            finally
            {
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting = null;
                if (host != null && !host.IsDisposed) host.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
            }
        }

        private static void WebViewPublishedReleaseFailuresRetry()
        {
            TaskpaneControl control = null;
            Form form = null;
            int navigated = 0;
            int closeAttempts = 0;
            int closed = 0;
            int compositionAttempts = 0;
            int released = 0;
            int releaseThread = 0;
            bool releaseSawParentHandle = false;
            bool allowClose = false;
            bool allowCompositionRelease = false;
            int deleteCalls = 0;
            try
            {
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting =
                    delegate(IntPtr ownerHandle, string assetRoot)
                    {
                        return Task.FromResult(TaskpaneWebViewInitializationLease.CreateForTesting(
                            delegate(IntPtr ignored) { },
                            delegate(IntPtr ignored) { Interlocked.Increment(ref navigated); },
                            delegate
                            {
                                Interlocked.Increment(ref closeAttempts);
                                if (!allowClose)
                                    throw new InvalidOperationException(
                                        "Injected published controller Close failure.");
                                Interlocked.Increment(ref closed);
                            },
                            delegate
                            {
                                Interlocked.Increment(ref compositionAttempts);
                                if (!allowCompositionRelease)
                                    throw new InvalidOperationException(
                                        "Injected published composition release failure.");
                                releaseThread = Thread.CurrentThread.ManagedThreadId;
                                releaseSawParentHandle = control != null && control.IsHandleCreated;
                                Interlocked.Increment(ref released);
                            }));
                    };
                RecordingShellTarget shell = new RecordingShellTarget();
                control = new TaskpaneControl(
                    null,
                    delegate(LiveSearchRequest request, CancellationToken token)
                    {
                        return Response(request, 0, 0, "WebReleaseRetry");
                    },
                    TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                    shell);
                form = NewHiddenForm();
                form.Controls.Add(control);
                form.Show();
                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                Require(PumpUntil(delegate { return navigated == 1; }, TimeSpan.FromSeconds(3)),
                    "Injected published WebView lease did not navigate.");

                bool closeFailure = SwAddin.DisconnectTaskpaneOnUi(
                    control, delegate { deleteCalls++; return true; }, 260);
                Require(!closeFailure && deleteCalls == 0 && closeAttempts >= 2 &&
                    closed == 0 && compositionAttempts == 0 && released == 0 &&
                    !control.WebViewShutdownCompletedForTesting &&
                    !control.ParentDisposeCompletedForTesting &&
                    control.IsHandleCreated && form.IsHandleCreated,
                    "Controller Close exception was treated as a Web/parent terminal.");

                int closeAttemptsBeforeRetry = closeAttempts;
                allowClose = true;
                bool compositionFailure = SwAddin.DisconnectTaskpaneOnUi(
                    control, delegate { deleteCalls++; return true; }, 260);
                Require(!compositionFailure && deleteCalls == 0 &&
                    closeAttempts == closeAttemptsBeforeRetry + 1 && closed == 1 &&
                    compositionAttempts >= 2 && released == 0 &&
                    !control.WebViewShutdownCompletedForTesting &&
                    !control.ParentDisposeCompletedForTesting &&
                    control.IsHandleCreated && form.IsHandleCreated,
                    "Composition release exception repeated Close or proved a false terminal.");

                int compositionAttemptsBeforeRetry = compositionAttempts;
                allowCompositionRelease = true;
                bool recovered = SwAddin.DisconnectTaskpaneOnUi(
                    control,
                    delegate
                    {
                        Require(closed == 1 && released == 1 &&
                            closeAttempts == closeAttemptsBeforeRetry + 1 &&
                            compositionAttempts == compositionAttemptsBeforeRetry + 1 &&
                            releaseThread == ownerThread && releaseSawParentHandle,
                            "Retained WebView ownership did not recover once on the owner STA.");
                        Require(control.WebViewShutdownCompletedForTesting &&
                            control.ParentDisposeCompletedForTesting,
                            "DeleteView ran before recovered WebView/parent terminal.");
                        deleteCalls++;
                        return true;
                    },
                    1000);
                Require(recovered && deleteCalls == 1 && closed == 1 && released == 1,
                    "WebView release retry did not recover after both stages became available.");
            }
            finally
            {
                allowClose = true;
                allowCompositionRelease = true;
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting = null;
                if (control != null && !control.IsDisposed)
                {
                    control.ShutdownAndWaitOnUi(1000);
                    control.Dispose();
                }
                if (form != null) { form.Close(); form.Dispose(); }
            }
        }

        private static void WebViewHandleGenerationReinitializes()
        {
            TaskpaneCompositionWebViewHost host = null;
            Form form = null;
            int factories = 0;
            int configured = 0;
            int navigated = 0;
            int closeAttempts = 0;
            int closed = 0;
            int released = 0;
            int wrongThread = 0;
            try
            {
                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting =
                    delegate(IntPtr ownerHandle, string assetRoot)
                    {
                        Interlocked.Increment(ref factories);
                        return Task.FromResult(TaskpaneWebViewInitializationLease.CreateForTesting(
                            delegate(IntPtr handle)
                            {
                                if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                                    Interlocked.Increment(ref wrongThread);
                                Interlocked.Increment(ref configured);
                            },
                            delegate(IntPtr handle)
                            {
                                if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                                    Interlocked.Increment(ref wrongThread);
                                Interlocked.Increment(ref navigated);
                            },
                            delegate
                            {
                                if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                                    Interlocked.Increment(ref wrongThread);
                                int attempt = Interlocked.Increment(ref closeAttempts);
                                if (attempt == 1)
                                    throw new InvalidOperationException(
                                        "Injected old-HWND controller Close failure.");
                                Interlocked.Increment(ref closed);
                            },
                            delegate
                            {
                                if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                                    Interlocked.Increment(ref wrongThread);
                                Interlocked.Increment(ref released);
                            }));
                    };
                host = new TaskpaneCompositionWebViewHost();
                form = NewHiddenForm();
                form.Controls.Add(host);
                form.Show();
                Require(PumpUntil(delegate { return navigated == 1; }, TimeSpan.FromSeconds(3)),
                    "First WebView HWND generation did not initialize.");

                host.RecreateHandleForTesting();
                Require(PumpUntil(delegate { return navigated == 2; }, TimeSpan.FromSeconds(3)),
                    "Replacement WebView HWND generation did not initialize.");
                Require(factories == 2 && configured == 2 && closeAttempts == 2 &&
                    closed == 1 && released == 1 &&
                    wrongThread == 0 && host.IsHandleCreated,
                    "WebView HWND recreation did not retain/retry the old generation before replacement.");

                host.Dispose();
                Require(host.IsDisposed && host.ShutdownCompletedForTesting &&
                    closeAttempts == 3 && closed == 2 && released == 2 && wrongThread == 0,
                    "Final WebView HWND generation did not release exactly once on owner STA.");
            }
            finally
            {
                TaskpaneCompositionWebViewHost.InitializationFactoryForTesting = null;
                if (host != null && !host.IsDisposed) host.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
            }
        }

        private static void MarshalReleaseRejectedPostRetrySafe()
        {
            int baseline = MarshaledInterfaceStream.OutstandingForTesting;
            int rejectedPosts = 0;
            int actualReleases = 0;
            TaskpaneShellResultsHost host = null;
            Form form = null;
            try
            {
                TaskpaneShellResultsHost.WorkerCoInitializeForTesting = delegate
                {
                    return unchecked((int)0x80004005);
                };
                TaskpaneShellResultsHost.MarshalReleasePostForTesting = delegate
                {
                    Interlocked.Increment(ref rejectedPosts);
                    return false;
                };
                TaskpaneShellResultsHost.MarshalReleaseCoreObserverForTesting = delegate
                {
                    Interlocked.Increment(ref actualReleases);
                };

                host = new TaskpaneShellResultsHost();
                form = new Form
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Left = -32000,
                    Top = -32000,
                    Width = 640,
                    Height = 420
                };
                form.Controls.Add(host);
                form.Show();
                Require(PumpUntil(delegate
                {
                    return host.ShutdownCompletedForTesting &&
                        MarshaledInterfaceStream.OutstandingForTesting == baseline;
                }, TimeSpan.FromSeconds(5)),
                    "Rejected marshal-release dispatch did not retain ownership for an owner-STA retry.");
                Require(rejectedPosts == 1 && actualReleases == 1 &&
                    host.OwnedResourceCountForTesting == 0 && host.DestroyCallCountForTesting == 1,
                    "Rejected marshal-release dispatch lost or duplicated the raw stream release.");
            }
            finally
            {
                TaskpaneShellResultsHost.WorkerCoInitializeForTesting = null;
                TaskpaneShellResultsHost.MarshalReleasePostForTesting = null;
                TaskpaneShellResultsHost.MarshalReleaseCoreObserverForTesting = null;
                if (host != null && !host.IsDisposed) host.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
            }
            Require(MarshaledInterfaceStream.OutstandingForTesting == baseline,
                "Rejected-post test left a marshaled interface stream outstanding.");
        }

        private static void DisconnectWaitsForShellTerminal()
        {
            string root = NewTempRoot();
            TaskpaneShellResultsHost host = null;
            TaskpaneControl control = null;
            Form form = null;
            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            bool deleteCalled = false;
            try
            {
                string path = Path.Combine(root, "DisconnectGate.SLDPRT");
                File.WriteAllText(path, "fixture");
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = delegate(string stage)
                {
                    if (stage != "before-add-item") return;
                    entered.Set();
                    release.Wait();
                };
                long generation;
                control = CreateReadyTaskpaneControl(out host, out form, out generation);
                ShellPopulationTicket ticket = host.Replace(generation, new[] { path });
                Require(PumpUntil(delegate { return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                    "Production disconnect fixture did not enter AddItem.");
                TaskpaneControl.ShutdownDispatchWaitForTesting = delegate(
                    WaitHandle[] waits, int timeoutMilliseconds)
                {
                    int result = TaskpaneShellResultsHost.WaitWithComDispatch(
                        waits, timeoutMilliseconds);
                    // Force the historical race deterministically: success sets
                    // completed and attempt, but CoWait reports the attempt index.
                    return waits[0].WaitOne(0) ? 1 : result;
                };
                Task.Factory.StartNew(delegate
                {
                    Thread.Sleep(350);
                    release.Set();
                }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                Stopwatch stopwatch = Stopwatch.StartNew();
                bool disconnected = SwAddin.DisconnectTaskpaneOnUi(
                    control,
                    delegate
                    {
                        Require(control.ParentDisposeCompletedForTesting && control.ShutdownSucceededForTesting,
                            "DeleteView-equivalent ran before parent Control release.");
                        Require(host.ShutdownCompletedForTesting && host.DestroyCallCountForTesting == 1 &&
                            host.OwnedResourceCountForTesting == 0,
                            "DeleteView-equivalent ran before Shell COM terminal.");
                        Require(host.FinalizeThreadIdForTesting == ownerThread,
                            "DeleteView-equivalent observed Finalize on a non-owner thread.");
                        deleteCalled = true;
                        return true;
                    },
                    4000);
                stopwatch.Stop();
                Require(disconnected && deleteCalled,
                    "Disconnect returned before the complete Shell/parent terminal.");
                Require(stopwatch.ElapsedMilliseconds >= 250 && stopwatch.ElapsedMilliseconds < 4000,
                    "Disconnect did not perform a bounded dispatching wait.");
                ticket.Dispose();
            }
            finally
            {
                release.Set();
                TaskpaneControl.ShutdownDispatchWaitForTesting = null;
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = null;
                if (control != null && !control.IsDisposed) control.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
                DeleteTempRoot(root);
            }
        }

        private static void UiPostFailureIsBoundedAndRecoverable()
        {
            string root = NewTempRoot();
            TaskpaneShellResultsHost host = null;
            TaskpaneControl control = null;
            Form form = null;
            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            int deleteCalls = 0;
            int rejectedPosts = 0;
            try
            {
                string path = Path.Combine(root, "PostFailure.SLDASM");
                File.WriteAllText(path, "fixture");
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = delegate(string stage)
                {
                    if (stage != "before-add-item") return;
                    entered.Set();
                    release.Wait();
                };
                long generation;
                control = CreateReadyTaskpaneControl(out host, out form, out generation);
                ShellPopulationTicket ticket = host.Replace(generation, new[] { path });
                Require(PumpUntil(delegate { return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                    "Post-failure fixture did not enter AddItem.");
                TaskpaneShellResultsHost.PostToUiForTesting = delegate
                {
                    Interlocked.Increment(ref rejectedPosts);
                    return false;
                };
                Task.Factory.StartNew(delegate
                {
                    Thread.Sleep(350);
                    release.Set();
                }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

                Stopwatch stopwatch = Stopwatch.StartNew();
                bool first = SwAddin.DisconnectTaskpaneOnUi(
                    control, delegate { deleteCalls++; return true; }, 900);
                stopwatch.Stop();
                Require(first && deleteCalls == 1 && rejectedPosts > 0,
                    "Owner-STA wait did not recover a rejected deferred UI post.");
                Require(stopwatch.ElapsedMilliseconds >= 250 && stopwatch.ElapsedMilliseconds < 900 &&
                    control.ParentDisposeCompletedForTesting &&
                    host.ShutdownCompletedForTesting && host.DestroyCallCountForTesting == 1,
                    "Rejected UI post recovery was unbounded or bypassed the complete terminal.");
                ticket.Dispose();
            }
            finally
            {
                release.Set();
                TaskpaneShellResultsHost.PostToUiForTesting = null;
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = null;
                if (control != null && !control.IsDisposed) control.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
                DeleteTempRoot(root);
            }
        }

        private static void DisconnectTimeoutIsBounded()
        {
            string root = NewTempRoot();
            TaskpaneShellResultsHost host = null;
            TaskpaneControl control = null;
            Form form = null;
            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            int deleteCalls = 0;
            try
            {
                string path = Path.Combine(root, "BoundedTimeout.SLDASM");
                File.WriteAllText(path, "fixture");
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = delegate(string stage)
                {
                    if (stage != "before-add-item") return;
                    entered.Set();
                    release.Wait();
                };
                long generation;
                control = CreateReadyTaskpaneControl(out host, out form, out generation);
                ShellPopulationTicket ticket = host.Replace(generation, new[] { path });
                Require(PumpUntil(delegate { return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                    "Bounded-timeout fixture did not enter AddItem.");

                Stopwatch stopwatch = Stopwatch.StartNew();
                bool first = SwAddin.DisconnectTaskpaneOnUi(
                    control, delegate { deleteCalls++; return true; }, 350);
                stopwatch.Stop();
                Require(!first && deleteCalls == 0,
                    "Disconnect reported success or called DeleteView before a blocked owned worker terminated.");
                Require(stopwatch.ElapsedMilliseconds >= 300 && stopwatch.ElapsedMilliseconds < 1400,
                    "Disconnect timeout was not bounded while the owned worker remained blocked.");
                Require(!host.ShutdownCompletedForTesting &&
                    !control.ParentDisposeCompletedForTesting &&
                    host.DestroyCallCountForTesting == 0,
                    "A timeout destroyed Shell resources or the parent before the owned worker terminal.");

                release.Set();
                bool retry = SwAddin.DisconnectTaskpaneOnUi(
                    control, delegate { deleteCalls++; return true; }, 3000);
                Require(retry && deleteCalls == 1 && control.ParentDisposeCompletedForTesting &&
                    host.ShutdownCompletedForTesting && host.DestroyCallCountForTesting == 1,
                    "Owner-STA retry did not complete after the bounded timeout.");
                ticket.Dispose();
            }
            finally
            {
                release.Set();
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = null;
                if (control != null && !control.IsDisposed) control.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
                DeleteTempRoot(root);
            }
        }

        private static void DisconnectTargetsOnlyExplicitOwner()
        {
            RecordingShellTarget ownedTarget = new RecordingShellTarget();
            RecordingShellTarget unrelatedTarget = new RecordingShellTarget();
            TaskpaneControl owned = null;
            TaskpaneControl unrelated = null;
            int deleteCalls = 0;
            try
            {
                owned = new TaskpaneControl(
                    null,
                    delegate(LiveSearchRequest request, CancellationToken token)
                    {
                        return Response(request, 0, 0, "Owned");
                    },
                    TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                    ownedTarget);
                unrelated = new TaskpaneControl(
                    null,
                    delegate(LiveSearchRequest request, CancellationToken token)
                    {
                        return Response(request, 0, 0, "Unrelated");
                    },
                    TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                    unrelatedTarget);

                bool disconnected = SwAddin.DisconnectTaskpaneOnUi(
                    owned, delegate { deleteCalls++; return true; }, 1000);
                Require(disconnected && deleteCalls == 1 && owned.IsDisposed &&
                    ownedTarget.ShutdownCallCount == 1,
                    "Disconnect did not terminate exactly the explicitly owned Taskpane.");
                Require(!unrelated.IsDisposed && unrelatedTarget.ShutdownCallCount == 0,
                    "Disconnect touched an unrelated Taskpane identity.");
            }
            finally
            {
                if (owned != null && !owned.IsDisposed) owned.Dispose();
                if (unrelated != null && !unrelated.IsDisposed) unrelated.Dispose();
            }
        }

        private static void RepeatedShutdownIsIdempotent()
        {
            string root = NewTempRoot();
            TaskpaneShellResultsHost host = null;
            TaskpaneControl control = null;
            Form form = null;
            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            int callbackCount = 0;
            try
            {
                string path = Path.Combine(root, "RepeatDispose.SLDASM");
                File.WriteAllText(path, "fixture");
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = delegate(string stage)
                {
                    if (stage != "before-add-item") return;
                    entered.Set();
                    release.Wait();
                };
                long generation;
                control = CreateReadyTaskpaneControl(out host, out form, out generation);
                ShellPopulationTicket ticket = host.Replace(generation, new[] { path });
                Require(PumpUntil(delegate { return entered.IsSet; }, TimeSpan.FromSeconds(5)),
                    "Repeat-shutdown fixture did not enter AddItem.");
                for (int index = 0; index < 8; index++)
                    control.BeginShutdown(delegate { Interlocked.Increment(ref callbackCount); });
                control.Dispose();
                control.Dispose();
                Require(host.DestroyCallCountForTesting == 0 && callbackCount == 0,
                    "Repeated shutdown bypassed the active worker gate.");
                release.Set();
                Require(PumpUntil(delegate { return callbackCount == 8; }, TimeSpan.FromSeconds(5)),
                    "Repeated BeginShutdown callbacks did not complete exactly once.");
                control.BeginShutdown(delegate { Interlocked.Increment(ref callbackCount); });
                Require(callbackCount == 9 && host.DestroyCallCountForTesting == 1 &&
                    host.OwnedResourceCountForTesting == 0 && control.ShutdownSucceededForTesting,
                    "Repeated terminal shutdown was not idempotent.");
                ticket.Dispose();
            }
            finally
            {
                release.Set();
                TaskpaneShellResultsHost.WorkerOperationHookForTesting = null;
                if (control != null && !control.IsDisposed) control.Dispose();
                if (form != null) { form.Close(); form.Dispose(); }
                DeleteTempRoot(root);
            }
        }

        private static void ExplicitAppExitSuppressesBrokerStart()
        {
            int starts = 0;
            BrokerPipeTransport transport = new BrokerPipeTransport(
                "swsearch.fixture.app-exit." + Guid.NewGuid().ToString("N"),
                delegate { return "C:\\Fixture\\SwSearch.Broker.exe"; },
                delegate(ProcessStartInfo ignored) { Interlocked.Increment(ref starts); },
                1,
                1,
                1,
                TimeSpan.FromMilliseconds(10),
                1,
                delegate { return true; });
            bool suppressed = false;
            try
            {
                transport.SendWithStart(new BrokerRequest
                {
                    ProtocolVersion = BrokerProtocol.ProtocolVersion,
                    RequestId = Guid.NewGuid(),
                    Operation = 0
                }, CancellationToken.None);
            }
            catch (BrokerAppExitMarkedException)
            {
                suppressed = true;
            }

            Require(suppressed, "An explicit desktop-app exit did not suppress Broker startup.");
            Require(starts == 0, "The taskpane started a Broker after the explicit app-exit marker was active.");
        }

        private static void BrokerResponseTimeoutPolicy()
        {
            BrokerPipeTransport transport = new BrokerPipeTransport(
                "swsearch.fixture.timeout." + Guid.NewGuid().ToString("N"),
                delegate { return "C:\\Fixture\\SwSearch.Broker.exe"; },
                delegate(ProcessStartInfo ignored) { },
                1,
                1,
                2000,
                TimeSpan.FromMilliseconds(10),
                1,
                delegate { return false; },
                12000);

            BrokerRequest initial = BrokerProtocol.CreateSearchRequest(new LiveSearchRequest(
                "fixture", 3, new[] { 0, 1 }, TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, new[] { "C:\\Library" }, true));
            BrokerRequest page = BrokerProtocol.CreateSearchRequest(new LiveSearchRequest(
                "fixture", 3, new[] { 0, 1 }, TaskpaneSearchScope.AllMachine, null, true,
                200, 500, null, null, new[] { "C:\\Library" }, true));
            BrokerRequest ping = new BrokerRequest
            {
                ProtocolVersion = BrokerProtocol.ProtocolVersion,
                RequestId = Guid.NewGuid(),
                Operation = 0
            };

            Require(transport.ResponseTimeoutMillisecondsForTesting(initial) == 12000,
                "The first library result set did not receive the bounded extended response interval.");
            Require(transport.ResponseTimeoutMillisecondsForTesting(page) == 2000,
                "Pagination no longer uses the short response interval.");
            Require(transport.ResponseTimeoutMillisecondsForTesting(ping) == 2000,
                "A non-search Broker operation inherited the extended search interval.");
        }

        private static void SearchDiagnosticsArePrivate()
        {
            string record = null;
            LiveSearchRequest request = new LiveSearchRequest(
                "SECRET-MATERIAL-71", 3, new[] { 0, 1 }, TaskpaneSearchScope.AllMachine,
                null, true, 0, 200, null, null,
                new[] { "C:\\Private\\DesignLibrary" }, true);
            AddinDiagnostics.WriteSearchEvent(request, 7, 2, 8140,
                new TimeoutException("SECRET-PATH-C:\\Private"),
                delegate(string value) { record = value; });

            Require(!String.IsNullOrWhiteSpace(record), "The privacy-safe search diagnostic was not emitted.");
            Require(record.IndexOf("SECRET-MATERIAL-71", StringComparison.Ordinal) < 0 &&
                    record.IndexOf("Private", StringComparison.OrdinalIgnoreCase) < 0,
                "A raw search term or library path leaked into taskpane diagnostics.");
            foreach (string token in new[]
            {
                "client_origin=taskpane", "mode=3", "kinds=0,1", "roots_count=1",
                "normalized_roots_hash=", "term_length=18", "result_count=7",
                "library_rejected_count=2", "latency_ms=8140", "error_code=response-timeout"
            })
                Require(record.IndexOf(token, StringComparison.Ordinal) >= 0,
                    "Privacy-safe search diagnostic token missing: " + token);
        }

        private static void DiagnosticsRootHashIsStableAndDistinct()
        {
            LiveSearchRequest first = new LiveSearchRequest(
                "probe", 3, new[] { 0 }, TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, new[] { "C:\\Library", "C:\\Library\\" }, true);
            LiveSearchRequest second = new LiveSearchRequest(
                "probe", 3, new[] { 0 }, TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, new[] { "C:\\library\\" }, true);
            LiveSearchRequest third = new LiveSearchRequest(
                "probe", 3, new[] { 0 }, TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, new[] { "C:\\Other" }, true);

            string firstRecord = null;
            AddinDiagnostics.WriteSearchEvent(first, 1, 0, 10, null,
                delegate(string value) { firstRecord = value; });
            string secondRecord = null;
            AddinDiagnostics.WriteSearchEvent(second, 1, 0, 10, null,
                delegate(string value) { secondRecord = value; });
            string thirdRecord = null;
            AddinDiagnostics.WriteSearchEvent(third, 1, 0, 10, null,
                delegate(string value) { thirdRecord = value; });
            string repeatedRecord = null;
            AddinDiagnostics.WriteSearchEvent(first, 1, 0, 10, null,
                delegate(string value) { repeatedRecord = value; });

            string firstHash = ExtractToken(firstRecord, "normalized_roots_hash=");
            string secondHash = ExtractToken(secondRecord, "normalized_roots_hash=");
            string thirdHash = ExtractToken(thirdRecord, "normalized_roots_hash=");
            string repeatedHash = ExtractToken(repeatedRecord, "normalized_roots_hash=");
            Require(firstHash != null && firstHash.Length == 16 &&
                    secondHash != null && thirdHash != null && repeatedHash != null,
                "A normalized roots hash was missing or malformed.");
            Require(String.Equals(firstHash, secondHash, StringComparison.Ordinal) &&
                    String.Equals(firstHash, repeatedHash, StringComparison.Ordinal),
                "Equivalent roots with different separators, casing, or call order changed the hash.");
            Require(!String.Equals(firstHash, thirdHash, StringComparison.Ordinal),
                "Distinct normalized roots produced the same hash.");
        }

        private static void DiagnosticsFileWritesAreSerialized()
        {
            string root = NewTempRoot();
            try
            {
                AddinDiagnostics.SetLogDirectoryForTesting(Path.Combine(root, "Logs"));
                string logPath = Path.Combine(root, "Logs", "SolidWorksAddin.log");
                const int threadCount = 8;
                const int recordsPerThread = 20;
                ManualResetEvent start = new ManualResetEvent(false);
                List<Exception> failures = new List<Exception>();
                List<Thread> threads = new List<Thread>();
                for (int i = 0; i < threadCount; i++)
                {
                    int worker = i;
                    Thread thread = new Thread(delegate()
                    {
                        try
                        {
                            start.WaitOne();
                            for (int j = 0; j < recordsPerThread; j++)
                            {
                                AddinDiagnostics.WriteEvent("worker-" + worker, "record-" + j);
                            }
                        }
                        catch (Exception caught)
                        {
                            lock (failures) failures.Add(caught);
                        }
                    });
                    threads.Add(thread);
                    thread.Start();
                }
                start.Set();
                foreach (Thread thread in threads) thread.Join();

                Require(failures.Count == 0, "A concurrent diagnostics write threw.");
                string[] lines = File.ReadAllLines(logPath);
                Require(lines.Length == threadCount * recordsPerThread,
                    "Concurrent diagnostics writes lost or duplicated records.");
                foreach (string line in lines)
                {
                    int stage = line.IndexOf("\tstage=worker-", StringComparison.Ordinal);
                    int eventMarker = line.IndexOf("\tevent=record-", StringComparison.Ordinal);
                    bool complete = stage >= 0 && eventMarker >= 0;
                    if (complete)
                    {
                        complete = line.IndexOf("\tstage=", stage + 1, StringComparison.Ordinal) < 0 &&
                                   line.IndexOf("\tevent=", eventMarker + 1, StringComparison.Ordinal) < 0 &&
                                   line.IndexOf("T", StringComparison.Ordinal) >= 0;
                    }
                    Require(complete, "A concurrent diagnostics line was interleaved: " + line);
                }
            }
            finally
            {
                AddinDiagnostics.SetLogDirectoryForTesting(null);
                DeleteTempRoot(root);
            }
        }

        private static string ExtractToken(string record, string prefix)
        {
            if (String.IsNullOrEmpty(record)) return null;
            int start = record.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) return null;
            int valueStart = start + prefix.Length;
            int end = record.IndexOf('\t', valueStart);
            return end < 0 ? record.Substring(valueStart) : record.Substring(valueStart, end - valueStart);
        }

        private static void QueryInteractionWire()
        {
            LiveSearchRequest legacy = new LiveSearchRequest(
                "legacy", 3, new[] { 0 }, TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, new[] { "C:\\Library" });
            string legacyJson = SerializeBrokerRequest(BrokerProtocol.CreateSearchRequest(legacy));
            Require(legacyJson.IndexOf("\"isInteractive\"", StringComparison.Ordinal) < 0,
                "A legacy-compatible null interaction marker was emitted on the wire.");
            BrokerRequest legacyRoundTrip = DeserializeBrokerRequest(legacyJson);
            Require(legacyRoundTrip.Query != null && !legacyRoundTrip.Query.IsInteractive.HasValue,
                "A missing interaction marker did not deserialize as null.");

            LiveSearchRequest interactive = new LiveSearchRequest(
                "typed", 3, new[] { 0 }, TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, new[] { "C:\\Library" }, true);
            string interactiveJson = SerializeBrokerRequest(BrokerProtocol.CreateSearchRequest(interactive));
            Require(interactiveJson.IndexOf("\"isInteractive\":true", StringComparison.Ordinal) >= 0,
                "An interactive query did not serialize isInteractive=true.");
            BrokerRequest interactiveRoundTrip = DeserializeBrokerRequest(interactiveJson);
            Require(interactiveRoundTrip.Query != null &&
                    interactiveRoundTrip.Query.IsInteractive == true,
                "An interactive query did not round-trip through the Broker DTO.");

            LiveSearchRequest background = new LiveSearchRequest(
                "refresh", 3, new[] { 0 }, TaskpaneSearchScope.AllMachine, null, true,
                0, 200, null, null, new[] { "C:\\Library" }, false);
            string backgroundJson = SerializeBrokerRequest(BrokerProtocol.CreateSearchRequest(background));
            Require(backgroundJson.IndexOf("\"isInteractive\":false", StringComparison.Ordinal) >= 0,
                "A background query did not serialize isInteractive=false.");
            BrokerRequest backgroundRoundTrip = DeserializeBrokerRequest(backgroundJson);
            Require(backgroundRoundTrip.Query != null &&
                    backgroundRoundTrip.Query.IsInteractive == false,
                "A background query did not round-trip through the Broker DTO.");
        }

        private static void UserQueryOrigins()
        {
            List<LiveSearchRequest> requests = new List<LiveSearchRequest>();
            object gate = new object();
            using (TaskpaneControl control = new TaskpaneControl(
                null,
                delegate(LiveSearchRequest request, CancellationToken token)
                {
                    lock (gate) requests.Add(request);
                    return Response(request, 0, 0, "None");
                },
                TaskpaneLibrarySettings.FromSolidWorksValue("C:\\Library"),
                new RecordingShellTarget()))
            {
                string error;
                Require(control.ReceiveWebMessageForTesting(
                        "{\"type\":\"search\",\"payload\":{\"query\":\"typed\",\"immediate\":false}}",
                        out error),
                    "A debounced typed query was rejected: " + error);
                Require(WaitUntil(delegate { lock (gate) return requests.Count >= 1; },
                        TimeSpan.FromSeconds(5)),
                    "A debounced typed query did not reach the production search delegate.");
                Require(control.WaitForSearchIdle(TimeSpan.FromSeconds(5)),
                    "The debounced typed query did not finish.");
                lock (gate)
                {
                    Require(requests[0].IsInteractive == true,
                        "A debounced user keystroke was not marked interactive.");
                }

                Require(control.ReceiveWebMessageForTesting(
                        "{\"type\":\"search\",\"payload\":{\"query\":\"explicit\",\"immediate\":true}}",
                        out error),
                    "An explicit query was rejected: " + error);
                Require(WaitUntil(delegate { lock (gate) return requests.Count >= 2; },
                        TimeSpan.FromSeconds(5)),
                    "An explicit query did not reach the production search delegate.");
                Require(control.WaitForSearchIdle(TimeSpan.FromSeconds(5)),
                    "The explicit query did not finish.");
                lock (gate)
                {
                    Require(requests[requests.Count - 1].IsInteractive == true,
                        "An explicit user search was not marked interactive.");
                }
            }
        }

        private static void AutomaticQueryOrigins()
        {
            List<LiveSearchRequest> initialRequests = new List<LiveSearchRequest>();
            object initialGate = new object();
            RecordingShellTarget initialTarget = new RecordingShellTarget();
            using (TaskpaneControl initial = new TaskpaneControl(
                null,
                delegate(LiveSearchRequest request, CancellationToken token)
                {
                    lock (initialGate) initialRequests.Add(request);
                    return Response(request, 0, 0, "Initial");
                },
                TaskpaneLibrarySettings.FromSolidWorksValue("C:\\Library"),
                initialTarget))
            {
                initialTarget.RaiseReady();
                Require(WaitUntil(delegate { lock (initialGate) return initialRequests.Count >= 1; },
                        TimeSpan.FromSeconds(5)),
                    "The initial cache query did not reach the production search delegate.");
                Require(initial.WaitForSearchIdle(TimeSpan.FromSeconds(5)),
                    "The initial cache query did not finish.");
                lock (initialGate)
                {
                    Require(initialRequests[0].IsInteractive == false,
                        "The automatic initial cache query was marked interactive.");
                }
            }

            List<LiveSearchRequest> requests = new List<LiveSearchRequest>();
            object gate = new object();
            int call = 0;
            using (TaskpaneControl control = new TaskpaneControl(
                null,
                delegate(LiveSearchRequest request, CancellationToken token)
                {
                    int current;
                    lock (gate)
                    {
                        requests.Add(request);
                        current = ++call;
                    }
                    if (current == 1) return Response(request, 201, 200, "Refresh");
                    if (request.Offset > 0)
                    {
                        return new BrokerResponse
                        {
                            Success = true,
                            Hits = new List<SearchHitDto>(),
                            PageReset = true,
                            IndexRevision = 45,
                            PageFingerprint = "reset",
                            State = new BrokerStateDto { Epoch = "rc55", Revision = 49, Phase = 1 }
                        };
                    }
                    return Response(request, 0, 0, "Refreshed");
                },
                TaskpaneLibrarySettings.FromSolidWorksValue("C:\\Library"),
                new RecordingShellTarget()))
            {
                control.SetSearchTextForTesting("refresh");
                Require(WaitUntil(delegate { lock (gate) return requests.Count >= 3; },
                        TimeSpan.FromSeconds(8)),
                    "Pagination did not schedule the page-reset refresh chain.");
                Require(control.WaitForSearchIdle(TimeSpan.FromSeconds(5)),
                    "The page-reset refresh chain did not finish.");
                lock (gate)
                {
                    Require(requests[0].Offset == 0 && requests[0].IsInteractive == true,
                        "The originating user query was not interactive.");
                    Require(requests[1].Offset == 200 && requests[1].IsInteractive == false,
                        "Automatic pagination was marked interactive.");
                    Require(requests[2].Offset == 0 && requests[2].IsInteractive == false,
                        "The page-reset automatic refresh was marked interactive.");
                }

                int beforeRevision;
                lock (gate) beforeRevision = requests.Count;
                control.ApplyRecoveredMonitorStateForTesting(
                    new BrokerStateDto { Epoch = "rc55", Revision = 50, Phase = 1 });
                Require(WaitUntil(delegate { lock (gate) return requests.Count > beforeRevision; },
                        TimeSpan.FromSeconds(5)),
                    "A committed revision did not schedule a replacement query.");
                Require(control.WaitForSearchIdle(TimeSpan.FromSeconds(5)),
                    "The revision replacement query did not finish.");
                lock (gate)
                {
                    Require(requests[requests.Count - 1].IsInteractive == false,
                        "A revision-driven replacement query was marked interactive.");
                }
            }
        }

        private static void AllPagesNoCap()
        {
            const int total = 1251;
            RecordingShellTarget target = new RecordingShellTarget();
            List<int> offsets = new List<int>();
            List<bool?> interactions = new List<bool?>();
            object gate = new object();
            Func<LiveSearchRequest, CancellationToken, BrokerResponse> search = delegate(
                LiveSearchRequest request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    offsets.Add(request.Offset);
                    interactions.Add(request.IsInteractive);
                }
                int count = Math.Min(request.Limit, total - request.Offset);
                return Response(request, total, Math.Max(0, count), "Library");
            };
            using (TaskpaneControl control = new TaskpaneControl(
                null, search, TaskpaneLibrarySettings.FromSolidWorksValue("C:\\Library"), target))
            {
                control.SetSearchTextForTesting("bearing");
                Require(WaitUntil(delegate { return target.Paths.Count == total; }, TimeSpan.FromSeconds(8)),
                    "All-page search did not finish.");
                Require(target.Paths.Count == total, "Result population stopped before TotalCount.");
                Require(control.LoadedCountForTesting == total && control.TotalResultCountForTesting == total,
                    "Taskpane count did not reach the complete result set.");
                int[] observed;
                bool?[] observedInteractions;
                lock (gate)
                {
                    observed = offsets.ToArray();
                    observedInteractions = interactions.ToArray();
                }
                Require(observed.SequenceEqual(new[] { 0, 200, 700, 1200 }),
                    "Paging was capped or used an unexpected sequence: " + String.Join(",", observed));
                Require(observedInteractions.Length == 4 &&
                        observedInteractions[0] == true &&
                        observedInteractions.Skip(1).All(delegate(bool? value) { return value == false; }),
                    "Only the originating user query should be interactive during automatic pagination.");
                Require(target.MaximumBatch <= 500, "A Shell population batch exceeded Broker limit 500.");
            }
        }

        private static void LatestGenerationWins()
        {
            RecordingShellTarget target = new RecordingShellTarget();
            ManualResetEventSlim oldPageStarted = new ManualResetEventSlim(false);
            Func<LiveSearchRequest, CancellationToken, BrokerResponse> search = delegate(
                LiveSearchRequest request, CancellationToken token)
            {
                if (request.Text == "old" && request.Offset != 0)
                {
                    oldPageStarted.Set();
                    token.WaitHandle.WaitOne();
                    token.ThrowIfCancellationRequested();
                }
                int total = request.Text == "old" ? 800 : 3;
                int count = Math.Min(request.Limit, Math.Max(0, total - request.Offset));
                return Response(request, total, count, request.Text == "old" ? "Old" : "New");
            };
            using (TaskpaneControl control = new TaskpaneControl(
                null, search, TaskpaneLibrarySettings.FromSolidWorksValue("C:\\Library"), target))
            {
                control.SetSearchTextForTesting("old");
                Require(oldPageStarted.Wait(TimeSpan.FromSeconds(5)), "Old generation did not reach page two.");
                control.SetSearchTextForTesting("new");
                Require(WaitUntil(delegate { return target.Paths.Count == 3; }, TimeSpan.FromSeconds(8)),
                    "Replacement generation did not finish.");
                Require(target.Paths.Count == 3 && target.Paths.All(delegate(string path)
                {
                    return Path.GetFileName(path).StartsWith("New", StringComparison.Ordinal);
                }), "A cancelled generation appended stale Shell items.");
            }
        }

        private static void LibraryFailClosed()
        {
            int calls = 0;
            RecordingShellTarget target = new RecordingShellTarget();
            using (TaskpaneControl control = new TaskpaneControl(
                null,
                delegate(LiveSearchRequest request, CancellationToken token)
                {
                    calls++;
                    return Response(request, 1, 1, "Unexpected");
                },
                TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                target))
            {
                control.SetSearchTextForTesting("x");
                Thread.Sleep(150);
                Require(calls == 0 && target.Paths.Count == 0 && control.TotalResultCountForTesting == null,
                    "Empty Design Library roots fell back to an all-machine query.");
                Require(control.StatusForTesting.IndexOf("未配置", StringComparison.Ordinal) >= 0,
                    "Empty roots did not produce a clear fail-closed status.");
            }
        }

        private static void FixedLibraryWebContract()
        {
            int calls = 0;
            LiveSearchRequest observed = null;
            RecordingShellTarget target = new RecordingShellTarget();
            using (TaskpaneControl control = new TaskpaneControl(
                null,
                delegate(LiveSearchRequest request, CancellationToken token)
                {
                    Interlocked.Increment(ref calls);
                    observed = request;
                    return Response(request, 0, 0, "None");
                },
                TaskpaneLibrarySettings.FromSolidWorksValue("C:\\Library"),
                target))
            {
                string error;
                string[] forbiddenMessages =
                {
                    "{\"type\":\"mode\",\"payload\":{\"mode\":0}}",
                    "{\"type\":\"selectScope\",\"payload\":{\"scope\":\"folder\",\"rootPath\":\"C:\\\\Other\"}}",
                    "{\"type\":\"browseScope\",\"payload\":{}}",
                    "{\"type\":\"restoreScope\",\"payload\":{}}",
                    "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"mode\":\"all\"}}",
                    "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"scope\":\"folder\"}}",
                    "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"rootPath\":\"C:\\\\Other\"}}",
                    "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"libraryRoots\":[\"C:\\\\Other\"]}}",
                    "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"includeSubdirectories\":false}}"
                };
                foreach (string message in forbiddenMessages)
                    Require(!control.ReceiveWebMessageForTesting(message, out error),
                        "A Taskpane range-control message was accepted: " + message);
                Thread.Sleep(100);
                Require(calls == 0, "A rejected range-control message reached the Broker.");

                Require(control.ReceiveWebMessageForTesting(
                    "{\"type\":\"search\",\"payload\":{\"query\":\"0404-009\",\"immediate\":true," +
                    "\"types\":{\"part\":true,\"assembly\":false,\"drawing\":true}}}", out error),
                    "A normal fixed-Library search was rejected: " + error);
                Require(control.WaitForSearchIdle(TimeSpan.FromSeconds(5)),
                    "Normal fixed-Library search did not complete.");
                Require(calls == 1 && observed != null, "Normal Taskpane search did not reach the Broker exactly once.");
                Require(observed.Mode == 3 && observed.Scope == TaskpaneSearchScope.AllMachine &&
                    String.IsNullOrEmpty(observed.RootPath) && observed.IncludeSubdirectories &&
                    observed.LibraryRoots.SequenceEqual(new[] { "C:\\Library" }, StringComparer.OrdinalIgnoreCase),
                    "Normal Taskpane search escaped the configured Library roots.");
                Require(observed.Kinds.SequenceEqual(new[] { 0, 1 }),
                    "Legacy Taskpane type filters changed the fixed supported kinds.");
                Require(!control.ReceiveWebMessageForTesting(
                    "{\"type\":\"armExternalFileDrag\",\"payload\":{\"schema\":1,\"rowToken\":\"x\"}}",
                    out error), "Removed Web drag command was accepted.");
            }

            TaskpaneSearchRequestFactory factory = new TaskpaneSearchRequestFactory();
            LiveSearchRequest factoryRequest;
            string factoryError;
            Require(!factory.TryCreate("x", 2, new[] { 0 }, TaskpaneSearchScope.AllMachine,
                    String.Empty, true, new[] { "C:\\Library" }, out factoryRequest, out factoryError),
                "Request factory accepted a non-Library mode.");
            Require(!factory.TryCreate("x", 3, new[] { 0 }, TaskpaneSearchScope.Folder,
                    "C:\\Other", true, new[] { "C:\\Library" }, out factoryRequest, out factoryError),
                "Request factory accepted a caller-selected folder scope.");
            Require(factory.TryCreate("x", 3, new[] { 0, 1, 2 }, TaskpaneSearchScope.AllMachine,
                    String.Empty, true, new[] { "C:\\LibraryA", "D:\\LibraryB" },
                    out factoryRequest, out factoryError),
                "Request factory rejected valid multiple Design Library roots: " + factoryError);
            Require(factoryRequest.LibraryRoots.SequenceEqual(
                    new[] { "C:\\LibraryA", "D:\\LibraryB" }, StringComparer.OrdinalIgnoreCase),
                "Request factory did not preserve multiple Design Library roots.");
            Require(factoryRequest.Kinds.SequenceEqual(new[] { 0, 1 }),
                "Request factory did not force the supported document kinds.");
        }

        private static void BridgeMessageContract()
        {
            TaskpaneWebCommand command;
            string error;

            Require(TaskpaneWebBridgePolicy.TryParse(
                    "{\"type\":\"ready\",\"payload\":{\"view\":\"taskpane\"}}", out command, out error) &&
                command.Type == "ready" && command.RequestId == null,
                "ready was rejected or changed: " + error);
            Require(TaskpaneWebBridgePolicy.TryParse(
                    "{\"type\":\"ready\",\"requestId\":\"abc_1.x\",\"payload\":{\"view\":\"taskpane\"}}",
                    out command, out error) && command.RequestId == "abc_1.x",
                "ready requestId did not round-trip: " + error);
            Require(!TaskpaneWebBridgePolicy.TryParse(
                "{\"type\":\"ready\",\"payload\":{\"view\":\"main\"}}", out command, out error),
                "ready accepted a non-taskpane view.");
            Require(!TaskpaneWebBridgePolicy.TryParse(
                "{\"type\":\"ready\",\"payload\":{\"view\":\"taskpane\",\"extra\":1}}", out command, out error),
                "ready accepted an unexpected payload key.");

            Require(TaskpaneWebBridgePolicy.TryParse(
                    "{\"type\":\"search\",\"payload\":{\"query\":\"0404-009\",\"immediate\":true," +
                    "\"types\":{\"part\":true,\"assembly\":false,\"drawing\":true}}}", out command, out error) &&
                command.Type == "search" && command.Text == "0404-009" && command.Immediate,
                "search with legacy type filters was rejected: " + error);
            Require(TaskpaneWebBridgePolicy.TryParse(
                    "{\"type\":\"search\",\"payload\":{\"text\":\"legacy\"}}", out command, out error) &&
                command.Text == "legacy" && !command.Immediate,
                "search text fallback changed: " + error);
            foreach (string forbidden in new[]
            {
                "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"mode\":\"library\"}}",
                "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"scope\":\"folder\"}}",
                "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"rootPath\":\"C:\\\\Other\"}}",
                "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"libraryRoots\":[\"C:\\\\Other\"]}}",
                "{\"type\":\"search\",\"payload\":{\"query\":\"x\",\"includeSubdirectories\":false}}"
            })
                Require(!TaskpaneWebBridgePolicy.TryParse(forbidden, out command, out error),
                    "search accepted a removed range field: " + forbidden);

            Require(TaskpaneWebBridgePolicy.TryParse(
                    "{\"type\":\"retry\",\"payload\":{}}", out command, out error) &&
                command.Type == "retry" && command.Immediate,
                "retry was rejected or changed: " + error);
            Require(TaskpaneWebBridgePolicy.TryParse(
                    "{\"type\":\"retry\",\"payload\":{\"target\":\"broker\"}}", out command, out error) &&
                command.Action == "retry",
                "retry target was rejected or changed: " + error);
            Require(!TaskpaneWebBridgePolicy.TryParse(
                "{\"type\":\"retry\",\"payload\":{\"target\":\"broker\",\"extra\":1}}", out command, out error),
                "retry accepted an unexpected payload key.");

            foreach (string removed in new[]
            {
                "{\"type\":\"mode\",\"payload\":{\"mode\":0}}",
                "{\"type\":\"loadMore\",\"payload\":{\"offset\":0,\"limit\":100}}",
                "{\"type\":\"sort\",\"payload\":{\"column\":\"name\",\"direction\":\"asc\"}}",
                "{\"type\":\"selectScope\",\"payload\":{\"scope\":\"folder\",\"rootPath\":\"C:\\\\Other\"}}",
                "{\"type\":\"columnWidth\",\"payload\":{\"id\":\"name\",\"width\":120}}",
                "{\"type\":\"contextAction\",\"payload\":{\"schema\":1,\"action\":\"open\",\"path\":\"C:\\\\x.SLDPRT\"}}",
                "{\"type\":\"openResult\",\"payload\":{\"path\":\"C:\\\\x.SLDPRT\"}}",
                "{\"type\":\"action\",\"payload\":{\"action\":\"open\",\"resultIndex\":0}}"
            })
                Require(!TaskpaneWebBridgePolicy.TryParse(removed, out command, out error),
                    "A removed Taskpane command was accepted: " + removed);

            Require(!TaskpaneWebBridgePolicy.TryParse(
                "{\"type\":\"search\",\"payload\":{\"query\":\"x\"},\"extra\":1}", out command, out error),
                "An unexpected envelope key was accepted.");
            Require(!TaskpaneWebBridgePolicy.TryParse("not-json", out command, out error),
                "Invalid JSON was accepted.");
            Require(!TaskpaneWebBridgePolicy.TryParse(
                "{\"type\":\"ready\",\"requestId\":\"bad request\"}", out command, out error),
                "An invalid requestId was accepted.");
        }

        private static void FilterToRootsBoundarySemantics()
        {
            List<SearchHitDto> hits = new List<SearchHitDto>();
            hits.Add(new SearchHitDto
            {
                FullPath = "C:\\Library\\Part\\a.SLDPRT",
                FileName = "a.SLDPRT",
                Kind = 0
            });
            hits.Add(new SearchHitDto
            {
                FullPath = "C:\\LIBRARY",
                FileName = "library",
                Kind = 0
            });
            hits.Add(new SearchHitDto
            {
                FullPath = "C:\\LIBRARY\\",
                FileName = "library-root",
                Kind = 0
            });
            hits.Add(new SearchHitDto
            {
                FullPath = "D:\\Design Library\\b.SLDPRT",
                FileName = "b.SLDPRT",
                Kind = 0
            });
            hits.Add(new SearchHitDto
            {
                FullPath = "C:\\Library2\\a.SLDPRT",
                FileName = "sibling.SLDPRT",
                Kind = 0
            });
            hits.Add(new SearchHitDto
            {
                FullPath = "relative\\a.SLDPRT",
                FileName = "relative.SLDPRT",
                Kind = 0
            });
            hits.Add(new SearchHitDto
            {
                FullPath = "\\\\?\\C:\\Library\\a.SLDPRT",
                FileName = "device.SLDPRT",
                Kind = 0
            });
            hits.Add(null);
            hits.Add(new SearchHitDto { FullPath = null, FileName = "null-path", Kind = 0 });

            List<SearchHitDto> accepted = TaskpaneLibrarySettings.FilterToRoots(
                hits,
                new[] { "c:\\library", "C:\\LIBRARY", String.Empty, "  ", "relative", "D:\\Design Library" });
            Require(accepted.Count == 4,
                "FilterToRoots accepted an unexpected set: " + accepted.Count);
            Require(String.Equals(accepted[0].FullPath, "C:\\Library\\Part\\a.SLDPRT", StringComparison.Ordinal) &&
                    String.Equals(accepted[1].FullPath, "C:\\LIBRARY", StringComparison.Ordinal) &&
                    String.Equals(accepted[2].FullPath, "C:\\LIBRARY\\", StringComparison.Ordinal) &&
                    String.Equals(accepted[3].FullPath, "D:\\Design Library\\b.SLDPRT", StringComparison.Ordinal),
                "FilterToRoots changed match order or accepted the wrong descendants.");
        }

        private static void RepeatCreateDestroy()
        {
            for (int index = 0; index < 2; index++)
            {
                TaskpaneShellResultsHost host = new TaskpaneShellResultsHost();
                host.CreateControl();
                Application.DoEvents();
                host.Dispose();
                Require(PumpUntil(delegate { return host.OwnedResourceCountForTesting == 0; },
                        TimeSpan.FromSeconds(5)),
                    "Repeated ExplorerBrowser disposal left owned resources.");
            }
        }

        private static BrokerResponse Response(
            LiveSearchRequest request,
            int total,
            int count,
            string prefix)
        {
            List<SearchHitDto> hits = new List<SearchHitDto>();
            for (int index = 0; index < count; index++)
            {
                int ordinal = request.Offset + index;
                hits.Add(new SearchHitDto
                {
                    FullPath = "C:\\Library\\" + prefix + ordinal.ToString("D6") + ".SLDPRT",
                    FileName = prefix + ordinal.ToString("D6") + ".SLDPRT",
                    Kind = 0
                });
            }
            return new BrokerResponse
            {
                Success = true,
                Hits = hits,
                HasMore = request.Offset + count < total,
                TotalCount = total,
                IndexRevision = 44,
                PageFingerprint = "rc48-fixture",
                State = new BrokerStateDto { Epoch = "rc48", Revision = 48, Phase = 1 }
            };
        }

        private static string SerializeBrokerRequest(BrokerRequest request)
        {
            DataContractJsonSerializer serializer =
                new DataContractJsonSerializer(typeof(BrokerRequest));
            using (MemoryStream buffer = new MemoryStream())
            {
                serializer.WriteObject(buffer, request);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static BrokerRequest DeserializeBrokerRequest(string json)
        {
            DataContractJsonSerializer serializer =
                new DataContractJsonSerializer(typeof(BrokerRequest));
            using (MemoryStream buffer = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (BrokerRequest)serializer.ReadObject(buffer);
            }
        }

        private static bool PumpUntil(Func<bool> predicate, TimeSpan timeout)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (predicate()) return true;
                Application.DoEvents();
                Thread.Sleep(10);
            }
            return predicate();
        }

        private static TaskpaneShellResultsHost CreateReadyHost(out Form form)
        {
            bool ready = false;
            string failure = null;
            TaskpaneShellResultsHost host = new TaskpaneShellResultsHost();
            host.HostReady += delegate { ready = true; };
            host.HostFailure += delegate(object sender, ShellResultsFailureEventArgs args)
            {
                failure = (failure == null ? String.Empty : failure + " | ") + args.Stage + ": " +
                    (args.Exception == null ? "unknown" : args.Exception.ToString());
            };
            form = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Left = -32000,
                Top = -32000,
                Width = 640,
                Height = 420
            };
            form.Controls.Add(host);
            form.Show();
            Require(PumpUntil(delegate { return ready; }, TimeSpan.FromSeconds(5)),
                "ExplorerBrowser worker-ready handshake failed. " + failure);
            return host;
        }

        private static Form NewHiddenForm()
        {
            return new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Left = -32000,
                Top = -32000,
                Width = 640,
                Height = 420
            };
        }

        private static TaskpaneControl CreateReadyTaskpaneControl(
            out TaskpaneShellResultsHost host,
            out Form form,
            out long generation)
        {
            bool ready = false;
            host = new TaskpaneShellResultsHost();
            host.HostReady += delegate { ready = true; };
            TaskpaneControl control = new TaskpaneControl(
                null,
                delegate(LiveSearchRequest request, CancellationToken token)
                {
                    return Response(request, 0, 0, "None");
                },
                TaskpaneLibrarySettings.FromSolidWorksValue(String.Empty),
                host);
            form = new Form
            {
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Left = -32000,
                Top = -32000,
                Width = 640,
                Height = 420
            };
            form.Controls.Add(control);
            form.Show();
            Require(PumpUntil(delegate { return ready; }, TimeSpan.FromSeconds(5)),
                "Nested Shell host did not become ready.");

            long stableGeneration = -1;
            DateTime stableSince = DateTime.UtcNow;
            Require(PumpUntil(delegate
            {
                long current = control.ResultSetGenerationForTesting;
                if (current != stableGeneration)
                {
                    stableGeneration = current;
                    stableSince = DateTime.UtcNow;
                }
                return current > 0 && DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(500);
            }, TimeSpan.FromSeconds(5)), "Taskpane startup generations did not settle.");
            generation = stableGeneration;
            return control;
        }

        private static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (predicate()) return true;
                Thread.Sleep(10);
            }
            return predicate();
        }

        private static string NewTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "swsearch-rc48-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteTempRoot(string root)
        {
            if (String.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            Directory.Delete(root, true);
        }

        private static void Run(string name, Action action)
        {
            action();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class DisposalProbe : IDisposable
        {
            private int _disposeCount;
            internal int DisposeCount { get { return _disposeCount; } }
            public void Dispose() { Interlocked.Increment(ref _disposeCount); }
        }
    }

    internal sealed class RecordingShellTarget : ITaskpaneShellResultsTarget
    {
        private readonly Panel _view = new Panel();
        private readonly object _gate = new object();
        private readonly List<string> _paths = new List<string>();
        private long _generation;
        private EventHandler _hostReady;

        public Control View { get { return _view; } }
        public event EventHandler HostReady
        {
            add { _hostReady += value; }
            remove { _hostReady -= value; }
        }
        public event EventHandler<ShellResultsFailureEventArgs> HostFailure { add { } remove { } }
        internal IList<string> Paths { get { lock (_gate) return _paths.ToArray(); } }
        internal int MaximumBatch { get; private set; }
        internal int ShutdownCallCount { get; private set; }

        internal void RaiseReady()
        {
            EventHandler ready = _hostReady;
            if (ready != null) ready(this, EventArgs.Empty);
        }

        public ShellPopulationTicket Replace(long generation, IEnumerable<string> paths)
        {
            string[] values = paths == null ? new string[0] : paths.ToArray();
            lock (_gate)
            {
                _generation = generation;
                _paths.Clear();
                _paths.AddRange(values);
                MaximumBatch = Math.Max(MaximumBatch, values.Length);
            }
            return ShellPopulationTicket.Completed(generation, true, values.Length, 0);
        }

        public ShellPopulationTicket Append(long generation, IEnumerable<string> paths)
        {
            string[] values = paths == null ? new string[0] : paths.ToArray();
            lock (_gate)
            {
                if (generation != _generation)
                    return ShellPopulationTicket.Completed(generation, false, 0, values.Length);
                _paths.AddRange(values);
                MaximumBatch = Math.Max(MaximumBatch, values.Length);
            }
            return ShellPopulationTicket.Completed(generation, true, values.Length, 0);
        }

        public void CancelBefore(long generation)
        {
            lock (_gate) _generation = generation;
        }

        public void BeginShutdown(Action<ShellShutdownResult> completed)
        {
            ShutdownCallCount++;
            if (completed != null) completed(ShellShutdownResult.Success());
        }
    }
}
