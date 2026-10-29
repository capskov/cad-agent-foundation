// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Bimwright.Dwg.Shared;
#if ACAD2026
using Bimwright.Dwg.Plugin.Execution;
#endif
using Bimwright.Dwg.Plugin.ToolCatalog;
using Bimwright.Dwg.Plugin.Views.Settings;
using Bimwright.Dwg.Plugin.Views.Toast;

[assembly: ExtensionApplication(typeof(Bimwright.Dwg.Plugin.App))]
[assembly: CommandClass(typeof(Bimwright.Dwg.Plugin.App))]

namespace Bimwright.Dwg.Plugin
{
    public class App : IExtensionApplication
    {
        private static ITransportServer _server;
        private static bool _toastEnabled;
        private static bool _wasClientPresent;
        private static bool _idleHooked;
        private static System.DateTime _lastClientActivityUtc = System.DateTime.MinValue;
#if ACAD2026
        private static ProcessExecutionLease _processExecutionLease;
        private static SqlitePluginIntentJournal _pluginJournal;
        private static PluginOwnerIdentity _pluginOwner;
        private static AcadDocumentContextRegistry _documentContextRegistry;
        private static PluginExecutionQueue _pluginExecutionQueue;
        private static bool _d1WritePilotEnabled;
        private static bool _d1PreflightPilotEnabled;
        private static bool _h1PreflightPilotEnabled;
        private static bool _h1WritePilotEnabled;
        internal static bool FoundationWriteEnabled { get; private set; }
        internal static Bimwright.Dwg.Shared.Execution.J10WritePilotPlan J10WritePlan { get; private set; }
        private static int _pluginExecutionMainThreadId;
        private static bool _pluginExecutionQueueHaltReported;
#endif

        // The dwg wire is connect-per-request: every tool call opens a fresh
        // socket, so a raw IsClientConnected edge would fire on each call.
        // Treat the agent as present while a socket is open or a command ran
        // within this window; the "agent connected" toast then fires once per
        // burst of activity instead of once per call.
        private static readonly System.TimeSpan ClientPresenceGap = System.TimeSpan.FromSeconds(30);
        private static SettingsWindow _settings;

        /// <summary>Set at startup; CommandDispatcher fires completion toasts through it.</summary>
        public static McpToastNotifier ToastNotifier { get; private set; }

        internal static bool ToastEnabled => _toastEnabled;
        internal static bool ListenerRunning => _server != null && _server.IsRunning;
        internal static bool ListenerUsesPipe => _server != null && _server.Kind == TransportKind.Pipe;
        internal static int? ListenerPort => ListenerRunning ? _server.Port : null;
#if ACAD2026
        internal static IPluginIntentJournal PluginJournal => _pluginJournal
            ?? throw new System.InvalidOperationException("The plugin execution journal is not initialized.");
        internal static PluginOwnerIdentity PluginOwner => _pluginOwner
            ?? throw new System.InvalidOperationException("The plugin execution owner is not initialized.");
        internal static AcadDocumentContextRegistry DocumentContextRegistry => _documentContextRegistry
            ?? throw new System.InvalidOperationException("The AutoCAD document context registry is not initialized.");
        internal static PluginExecutionQueue PluginExecutionQueue => _pluginExecutionQueue
            ?? throw new System.InvalidOperationException("The plugin execution queue is not initialized.");
        internal static bool D1WritePilotEnabled => _d1WritePilotEnabled;
        internal static bool D1PreflightPilotEnabled => _d1PreflightPilotEnabled;
        internal static bool H1WritePilotEnabled => _h1WritePilotEnabled;
        internal static bool H1PreflightPilotEnabled => _h1PreflightPilotEnabled;

        internal static void EnsurePluginExecutionMainThread()
        {
            if (_pluginExecutionMainThreadId == 0
                || System.Environment.CurrentManagedThreadId != _pluginExecutionMainThreadId)
            {
                throw new System.InvalidOperationException(
                    "Plugin execution queue setup must run on AutoCAD's initialized main thread.");
            }
        }
#endif

        internal static bool ClientRecentlyPresent =>
            _lastClientActivityUtc != System.DateTime.MinValue
            && System.DateTime.UtcNow - _lastClientActivityUtc < ClientPresenceGap;

        public void Initialize()
        {
            try
            {
                StartServerInternal();
                WriteLine($"Bimwright DWG loaded and listening on {DescribeTransport(_server)}.");
            }
            catch (System.Exception ex)
            {
                WriteLine($"Bimwright DWG loaded; auto-start failed ({ex.Message}). Run MCPSTART to retry.");
            }
        }

        public void Terminate()
        {
            if (_idleHooked)
            {
                try { Application.Idle -= OnIdleToast; } catch { }
                _idleHooked = false;
            }
            try { _server?.Stop(); } catch { }
            try { ToolCatalogStore.Clear(); } catch { }
            try { CloseSettings(); } catch { }
            try { ToastNotifier?.Shutdown(); } catch { }
            try { DocumentInvoker.Shutdown(); } catch { }
            try { View.DrawingRevision.Shutdown(); } catch { }
            // Keep the OS lease and journal rooted for the AutoCAD process lifetime.
            // Terminate/Stop do not prove all transport and CAD callbacks have drained;
            // releasing here could let another plugin generation race an old handler.
        }

        [CommandMethod("MCPSTART", CommandFlags.Session)]
        public static void McpStart()
        {
            if (_server != null && _server.IsRunning)
            {
                WriteLine($"Bimwright DWG already running on {DescribeTransport(_server)}.");
                return;
            }
            try
            {
                StartServerInternal();
                WriteLine($"Bimwright DWG listening on {DescribeTransport(_server)}.");
            }
            catch (System.Exception ex)
            {
                WriteLine($"Bimwright DWG start failed: {ex.Message}");
            }
        }

        [CommandMethod("MCPSTOP", CommandFlags.Session)]
        public static void McpStop()
        {
            if (_server == null || !_server.IsRunning)
            {
                WriteLine("Bimwright DWG not running.");
                return;
            }
            StopListener(closeSettings: true);
        }

        [CommandMethod("MCPTOAST", CommandFlags.Session)]
        public static void McpToast()
        {
            SetToastEnabled(!_toastEnabled);
        }

        [CommandMethod("MCPSETTINGS", CommandFlags.Session)]
        public static void McpSettings()
        {
            ShowOrFocusSettings();
        }

        internal static void SetToastEnabled(bool enabled)
        {
            _toastEnabled = enabled;
            var saved = PluginSettings.SaveEnableToast(enabled);
            WriteLine(enabled
                ? "Toast notifications enabled (saved)."
                : "Toast notifications disabled (saved).");
            if (!saved)
                WriteLine("Toast preference could not be saved; this session is still using the new state.");
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(PluginSettings.EnvEnableToast)))
                WriteLine($"{PluginSettings.EnvEnableToast} is set and re-applies at next launch.");
            ToastNotifier?.OnToastEnabledChanged(enabled, saved);
        }

        internal static void StartListener()
        {
            McpStart();
        }

        internal static void StopListener(bool closeSettings)
        {
            if (_server != null && _server.IsRunning)
            {
                _server.Stop();
                _server = null;
            }
            ToolCatalogStore.Clear();
            ToastNotifier?.DismissAll();
            if (closeSettings)
                CloseSettings();
            WriteLine("Bimwright DWG stopped.");
        }

        internal static void ShowOrFocusSettings()
        {
            if (_settings != null)
            {
                try { _settings.Activate(); } catch { }
                return;
            }

            var window = new SettingsWindow();
            window.Closed += (_, __) =>
            {
                if (ReferenceEquals(_settings, window))
                    _settings = null;
            };
            _settings = window;
            try
            {
                Application.ShowModelessWindow(window);
            }
            catch (System.Exception ex)
            {
                _settings = null;
                try { window.Close(); } catch { }
                WriteLine("Settings could not open: " + ex.Message);
            }
        }

        internal static void CloseSettings()
        {
            var window = _settings;
            _settings = null;
            if (window == null)
                return;
            try { window.Close(); } catch { }
        }

        [CommandMethod("MCPENABLECODE", CommandFlags.Session)]
        public static void McpEnableCode()
        {
            CommandDispatcher.SetSendCodeEnabled(true);
            WriteLine("send_code re-enabled for this AutoCAD session.");
        }

        [CommandMethod("MCPDISABLECODE", CommandFlags.Session)]
        public static void McpDisableCode()
        {
            CommandDispatcher.SetSendCodeEnabled(false);
            WriteLine("send_code disabled for this AutoCAD session.");
        }

        private static void StartServerInternal()
        {
            ITransportServer candidate = null;
            try
            {
                // Capture and validate the app root before plugin state, journal,
                // listener, discovery, or settings/log I/O can begin.
                BimwrightAppRoot.Initialize();
                ToolCatalogStore.Clear();
#if ACAD2026
                InitializeExecutionRuntime();
                InitializeDocumentContextRegistry();
                InitializePluginExecutionQueue();
#endif
                DocumentInvoker.Initialize();
                InitializeToast();
                CommandDispatcher.SetSendCodeEnabled(true);
#if ACAD2025_OR_GREATER
                candidate = new PipeTransportServer(PluginTarget.AutoCadYear);
#else
                candidate = new TcpTransportServer(PluginTarget.AutoCadYear);
#endif
                candidate.Start();
                _server = candidate;
            }
            catch
            {
                try { candidate?.Stop(); } catch { }
                _server = null;
                if (_idleHooked)
                {
                    try { Application.Idle -= OnIdleToast; } catch { }
                    _idleHooked = false;
                }
                try { ToastNotifier?.Shutdown(); } catch { }
                ToastNotifier = null;
                try { DocumentInvoker.Shutdown(); } catch { }
                // A failed restart may follow MCPSTOP while an earlier transport
                // callback is still unwinding. Keep any acquired lease fail-closed.
                throw;
            }
        }

        /// <summary>
        /// Establish process-wide ownership and recover the plugin journal before
        /// publishing discovery or accepting any request. The lock directory is
        /// deliberately stable across plugin generations and AutoCAD years in
        /// the pinned app root; it must never come from a client request.
        /// </summary>
#if ACAD2026
        private static void InitializeExecutionRuntime()
        {
            if (_processExecutionLease != null && _pluginJournal != null && _pluginOwner != null)
                return;

            string executionRoot = BimwrightAppRoot.Current.UnderDwg("execution");
            string leaseDirectory = System.IO.Path.Combine(executionRoot, "process-leases");
            string databasePath = System.IO.Path.Combine(executionRoot, "plugin-execution.sqlite");

            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
            {
                int processId = process.Id;
                System.DateTimeOffset processStartedAtUtc = process.StartTime.ToUniversalTime();
                ProcessExecutionLease lease = ProcessExecutionLease.Acquire(leaseDirectory, processId, processStartedAtUtc);
                SqlitePluginIntentJournal journal = null;
                try
                {
                    var owner = new PluginOwnerIdentity(
                        System.Guid.NewGuid().ToString("N"),
                        processId,
                        processStartedAtUtc,
                        System.Guid.NewGuid().ToString("N"));
                    journal = new SqlitePluginIntentJournal(
                        databasePath,
                        owner,
                        new FileAndProcessPluginOwnerLivenessProvider(databasePath));
                    journal.Initialize(System.DateTimeOffset.UtcNow);

                    _pluginOwner = owner;
                    _pluginJournal = journal;
                    _processExecutionLease = lease;
                }
                catch
                {
                    try { journal?.Dispose(); } catch { }
                    lease.Dispose();
                    throw;
                }
            }
        }

        /// <summary>
        /// Capture AutoCAD document lifetimes only after process ownership and
        /// journal recovery are ready, and before a transport can publish itself.
        /// Like the process lease, this registry is retained for the AutoCAD
        /// process lifetime so a failed listener restart cannot revive old binds.
        /// </summary>
        private static void InitializeDocumentContextRegistry()
        {
            if (_documentContextRegistry != null)
                return;

            AcadDocumentContextRegistry registry = new AcadDocumentContextRegistry();
            _pluginExecutionMainThreadId = System.Environment.CurrentManagedThreadId;
            _documentContextRegistry = registry;
        }

        /// <summary>
        /// Keep one queue beside the process owner and journal. Listener restarts
        /// must not clear a halted queue or capture an old dispatcher instance.
        /// </summary>
        private static void InitializePluginExecutionQueue()
        {
            if (_pluginExecutionQueue != null)
                return;

            EnsurePluginExecutionMainThread();
            bool preflightOnly = string.Equals(
                System.Environment.GetEnvironmentVariable("BIMWRIGHT_D1_PREFLIGHT_PILOT_ENABLED"),
                "1", System.StringComparison.Ordinal);
            _d1WritePilotEnabled = !preflightOnly && string.Equals(
                System.Environment.GetEnvironmentVariable("BIMWRIGHT_D1_WRITE_PILOT_ENABLED"),
                "1", System.StringComparison.Ordinal);
            _d1PreflightPilotEnabled = preflightOnly || _d1WritePilotEnabled;
            _h1WritePilotEnabled = string.Equals(System.Environment.GetEnvironmentVariable("BIMWRIGHT_H1_WRITE_PILOT_ENABLED"), "1", System.StringComparison.Ordinal);
            string j10PlanFile=System.Environment.GetEnvironmentVariable(Bimwright.Dwg.Shared.Execution.J10WritePilotPlan.NativePlanEnvironment);
            if(!string.IsNullOrWhiteSpace(j10PlanFile)&&!_h1WritePilotEnabled)throw new System.InvalidOperationException("J10 plan requires explicit H1 write enablement.");
            J10WritePlan=string.IsNullOrWhiteSpace(j10PlanFile)?null:Bimwright.Dwg.Shared.Execution.J10WritePilotPlan.Load(j10PlanFile);
            _h1PreflightPilotEnabled = string.Equals(
                System.Environment.GetEnvironmentVariable("BIMWRIGHT_H1_PREFLIGHT_PILOT_ENABLED"), "1", System.StringComparison.Ordinal);
            if ((_h1PreflightPilotEnabled || _h1WritePilotEnabled) && (_d1WritePilotEnabled || _d1PreflightPilotEnabled) || _h1WritePilotEnabled && _h1PreflightPilotEnabled)
                throw new System.InvalidOperationException("H1 preflight must run separately from D1 pilots.");
            FoundationWriteEnabled=string.Equals(System.Environment.GetEnvironmentVariable("BIMWRIGHT_FOUNDATION_WRITE_ENABLED"),"1",System.StringComparison.Ordinal);
            if(FoundationWriteEnabled&&(_d1WritePilotEnabled||_d1PreflightPilotEnabled||_h1WritePilotEnabled||_h1PreflightPilotEnabled))throw new System.InvalidOperationException("Foundation drawing plans require an exclusive profile.");
            PluginExecutionQueue queue;
            if (FoundationWriteEnabled)
            {
                queue=new PluginExecutionQueue(PluginJournal,new[]{"list_layers","get_entity_properties","query_entities","foundation_view"},new[]{Bimwright.Dwg.Shared.Execution.FoundationDrawingPlan.PluginToolName},CommandDispatcher.VerifyExactPluginContext,CommandDispatcher.ExecuteReadOnlyJob,CommandDispatcher.ExecuteFoundationWriteJob);
            }
            else if (_h1WritePilotEnabled)
            {
                _h1PreflightPilotEnabled = true;
                queue = new PluginExecutionQueue(PluginJournal,
                    new[] { "list_layers", "get_entity_properties", "query_entities", "h1_preflight_fixed" },
                    new[] { "h1_fixed_hatch_repair" }, CommandDispatcher.VerifyExactPluginContext,
                    CommandDispatcher.ExecuteReadOnlyJob, CommandDispatcher.ExecuteH1FixedWriteJob);
            }
            else if (_h1PreflightPilotEnabled)
            {
                queue = new PluginExecutionQueue(PluginJournal,
                    new[] { "list_layers", "get_entity_properties", "query_entities", "h1_preflight_fixed" },
                    CommandDispatcher.VerifyExactPluginContext, CommandDispatcher.ExecuteReadOnlyJob);
            }
            else if (_d1WritePilotEnabled)
            {
                queue = new PluginExecutionQueue(
                    PluginJournal,
                    new[] { "list_layers", "get_entity_properties", "query_entities", "d1_preflight_r01" },
                    new[] { "drawing_write" },
                    CommandDispatcher.VerifyExactPluginContext,
                    CommandDispatcher.ExecuteReadOnlyJob,
                    CommandDispatcher.ExecuteD1R01WriteJob);
            }
            else if (_d1PreflightPilotEnabled)
            {
                queue = new PluginExecutionQueue(
                    PluginJournal,
                    new[] { "list_layers", "get_entity_properties", "query_entities", "d1_preflight_r01" },
                    CommandDispatcher.VerifyExactPluginContext,
                    CommandDispatcher.ExecuteReadOnlyJob);
            }
            else
            {
                queue = new PluginExecutionQueue(
                    PluginJournal,
                    new[] { "list_layers", "get_entity_properties", "query_entities" },
                    CommandDispatcher.VerifyExactPluginContext,
                    CommandDispatcher.ExecuteReadOnlyJob);
            }
            _pluginExecutionQueue = queue;
        }

        internal static void ReportPluginExecutionQueueHaltedOnce(string errorCode)
        {
            if (_pluginExecutionQueueHaltReported)
                return;

            _pluginExecutionQueueHaltReported = true;
            WriteLine("Read-only execution queue halted fail-closed (" + errorCode
                + "). New jobs are rejected; status and cancellation remain available. Restart AutoCAD to recover.");
        }

#endif

        private static void InitializeToast()
        {
            if (ToastNotifier != null)
                return;

            _toastEnabled = PluginSettings.LoadToastEnabled();
            var host = new McpToastHost();
            ToastNotifier = new McpToastNotifier(host, () => _toastEnabled);
            ToastNotifier.SetInstanceInfo("AutoCAD " + PluginTarget.AutoCadYear);

            // Anchor toasts to the AutoCAD window and reuse the host WPF dispatcher
            // when AutoCAD already runs one (its ribbon is WPF).
            try
            {
                var hwnd = Application.MainWindow != null ? Application.MainWindow.Handle : System.IntPtr.Zero;
                if (hwnd != System.IntPtr.Zero)
                    ToastNotifier.SetOwnerHandle(hwnd);
            }
            catch { }
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
                ToastNotifier.SetHostDispatcher(dispatcher);
            }
            catch { }

            if (!_idleHooked)
            {
                Application.Idle += OnIdleToast;
                _idleHooked = true;
            }
        }

        /// <summary>
        /// Runs on the AutoCAD main thread: flushes toasts held while the frame was
        /// minimized/modal, and toasts when an agent becomes present (first attach
        /// or return after a ClientPresenceGap of silence).
        /// </summary>
        private static void OnIdleToast(object sender, System.EventArgs args)
        {
#if ACAD2026
            if (_server is PipeTransportServer pipeServer && pipeServer.IsRunning)
            {
                try
                {
                    var pumpResult = pipeServer.PumpOneQueueJob();
                    if (pumpResult != null
                        && pumpResult.Disposition == PluginExecutionQueuePumpDisposition.Halted)
                    {
                        ReportPluginExecutionQueueHaltedOnce(pumpResult.ErrorCode);
                    }
                }
                catch (System.Exception ex) { PluginLog.Debug("Plugin execution queue pump failed: " + ex.Message); }
            }
#endif

            var notifier = ToastNotifier;
            if (notifier == null)
                return;

            notifier.SetInstanceInfo("AutoCAD " + PluginTarget.AutoCadYear);
            notifier.FlushPendingIfUsable();

            var now = System.DateTime.UtcNow;
            var active = _server != null && _server.IsRunning
                && (_server.IsClientConnected
                    || (_server.LastCommandTime.HasValue
                        && now - _server.LastCommandTime.Value < ClientPresenceGap));
            if (active)
                _lastClientActivityUtc = now;

            var present = _lastClientActivityUtc != System.DateTime.MinValue
                && now - _lastClientActivityUtc < ClientPresenceGap;
            if (present && !_wasClientPresent)
                notifier.OnClientConnected(_server.ConnectionInfo);
            _wasClientPresent = present;
        }

        private static string DescribeTransport(ITransportServer server)
        {
            if (server == null)
            {
                return "no active transport";
            }

            return server.Kind == TransportKind.Pipe
                ? "pipe " + server.PipeName
                : "port " + server.Port;
        }

        private static void WriteLine(string message)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage($"\n[Bimwright.Dwg] {message}\n");
        }
    }
}
