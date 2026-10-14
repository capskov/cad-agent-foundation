// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bimwright.Dwg.Shared;
#if ACAD2026
using Bimwright.Dwg.Plugin.Execution;
#endif

namespace Bimwright.Dwg.Plugin
{
    public class PipeTransportServer : ITransportServer
    {
        private readonly string _target;
        private readonly string _pipeName;
        private readonly object _startStopGate = new object();
        private readonly PipeListenerLifecycle<NamedPipeServerStream> _lifecycle =
            new PipeListenerLifecycle<NamedPipeServerStream>();
#if ACAD2026
        private readonly PipeListenerLifecycle<NamedPipeServerStream> _controlLifecycle =
            new PipeListenerLifecycle<NamedPipeServerStream>();
        private string _controlPipeName;
#endif

        public PipeTransportServer(string target, string pipeName = null)
        {
            _target = string.IsNullOrWhiteSpace(target) ? "2025" : target;
            _pipeName = string.IsNullOrWhiteSpace(pipeName)
                ? "BimwrightDwg-" + _target + "-" + Process.GetCurrentProcess().Id
                : pipeName;
        }

        public int? Port => null;
        public string PipeName => _pipeName;
        public string ControlPipeName
        {
            get
            {
#if ACAD2026
                return _controlPipeName;
#else
                return null;
#endif
            }
        }
        public string AuthToken { get; private set; }
        public bool IsRunning => _lifecycle.IsRunning;
        public bool IsClientConnected
        {
            get
            {
                try { return _lifecycle.Active?.IsConnected ?? false; }
                catch { return false; }
            }
        }
        public DateTime? LastCommandTime { get; private set; }
        public TransportKind Kind => TransportKind.Pipe;
        public string ConnectionInfo => $"Pipe:{_pipeName}";

        public void Start()
        {
            lock (_startStopGate)
            {
                long generation = _lifecycle.Start();
                long controlGeneration = 0;
                try
                {
                    string authToken = Guid.NewGuid().ToString("N");
                    var dispatcher = new CommandDispatcher(authToken);
#if ACAD2026
                    controlGeneration = _controlLifecycle.Start();
                    string controlPipeName = _pipeName + "-control-" + Guid.NewGuid().ToString("N");
#endif
                    bool started = _lifecycle.RunIfCurrent(generation, () =>
                    {
#if ACAD2026
                        bool controlStarted = _controlLifecycle.RunIfCurrent(controlGeneration, () =>
                        {
                            AuthToken = authToken;
                            _controlPipeName = controlPipeName;
                            WriteDiscoveryFile();

                            var controlAcceptThread = new Thread(() =>
                                ControlAcceptLoop(generation, controlGeneration, controlPipeName, dispatcher))
                                { IsBackground = true, Name = "BimwrightDwg-ControlPipeAccept" };
                            controlAcceptThread.Start();
                        });
                        if (!controlStarted)
                            throw new InvalidOperationException("The control pipe listener was stopped while starting.");
#else
                        AuthToken = authToken;
                        WriteDiscoveryFile();
#endif
                        var acceptThread = new Thread(() => AcceptLoop(generation, dispatcher))
                            { IsBackground = true, Name = "BimwrightDwg-PipeAccept" };
                        acceptThread.Start();
                    });
                    if (!started)
                        throw new InvalidOperationException("The pipe listener was stopped while starting.");
                }
                catch
                {
#if ACAD2026
                    try { _controlLifecycle.Stop(controlGeneration, _ => { }, () => { }); } catch { }
#endif
                    try { _lifecycle.Stop(generation, _ => { }, DeleteDiscoveryFile); } catch { }
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_startStopGate)
            {
                // Each lifecycle gate serializes Stop against its own pipe
                // creation and publication. Disposing the control instance
                // releases a blocked client read without joining its thread.
#if ACAD2026
                try { _controlLifecycle.Stop(_ => { }); } catch { }
#endif
                try { _lifecycle.Stop(_ => { }, DeleteDiscoveryFile); } catch { }
            }
        }

#if ACAD2026
        /// <summary>Runs at most one queued read-only job on AutoCAD's Idle thread.</summary>
        internal PluginExecutionQueuePumpResult PumpOneQueueJob()
        {
            if (!_lifecycle.IsRunning)
                return null;
            App.EnsurePluginExecutionMainThread();
            return App.PluginExecutionQueue.Pump(DateTimeOffset.UtcNow);
        }
#endif

        private void AcceptLoop(long generation, CommandDispatcher dispatcher)
        {
            while (_lifecycle.IsCurrent(generation))
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = _lifecycle.CreateAndPublish(generation, () => new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous), _ => { });
                    if (pipe == null)
                        return;

                    pipe.WaitForConnection();
                    HandleClient(pipe, generation, dispatcher);
                }
                catch
                {
                    if (!_lifecycle.IsCurrent(generation))
                        return;
                }
                finally
                {
                    if (pipe != null)
                        _lifecycle.Release(generation, pipe, _ => { });
                }
            }
        }

#if ACAD2026
        private void ControlAcceptLoop(long generation, long controlGeneration, string pipeName,
            CommandDispatcher dispatcher)
        {
            ControlAcceptLoopAsync(generation, controlGeneration, pipeName, dispatcher)
                .GetAwaiter().GetResult();
        }

        private async Task ControlAcceptLoopAsync(long generation, long controlGeneration, string pipeName,
            CommandDispatcher dispatcher)
        {
            while (_lifecycle.IsCurrent(generation) && _controlLifecycle.IsCurrent(controlGeneration))
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = _controlLifecycle.CreateAndPublish(controlGeneration, () => new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly), _ => { });
                    if (pipe == null)
                        return;

                    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                    await HandleControlClientAsync(pipe, generation, controlGeneration, dispatcher)
                        .ConfigureAwait(false);
                }
                catch
                {
                    if (!_lifecycle.IsCurrent(generation) || !_controlLifecycle.IsCurrent(controlGeneration))
                        return;
                }
                finally
                {
                    if (pipe != null)
                        _controlLifecycle.Release(controlGeneration, pipe, _ => { });
                }
            }
        }

        private async Task HandleControlClientAsync(PipeStream pipe, long generation, long controlGeneration,
            CommandDispatcher dispatcher)
        {
            if (!_lifecycle.IsCurrent(generation) || !_controlLifecycle.IsCurrent(controlGeneration))
                return;

            string requestLine = await PipeControlFrameProtocol.ReadFrameAsync(pipe).ConfigureAwait(false);
            if (requestLine == null || !_lifecycle.IsCurrent(generation)
                || !_controlLifecycle.IsCurrent(controlGeneration))
            {
                return;
            }

            LastCommandTime = DateTime.UtcNow;
            string responseJson;
            try
            {
                responseJson = dispatcher.DispatchControl(requestLine);
            }
            catch (Exception ex)
            {
                responseJson = CommandDispatcher.ErrorJson(null, $"dispatch error: {ex.Message}");
            }

            if (Encoding.UTF8.GetByteCount(responseJson) > PipeControlFrameProtocol.MaxResponseFrameBytes)
                responseJson = CommandDispatcher.ErrorJson(null, "control_response_too_large");

            await PipeControlFrameProtocol.WriteFrameAsync(pipe, responseJson).ConfigureAwait(false);
        }
#endif

        private void HandleClient(Stream stream, long generation, CommandDispatcher dispatcher)
        {
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                string line;
                while (_lifecycle.IsCurrent(generation) && (line = reader.ReadLine()) != null)
                {
                    LastCommandTime = DateTime.UtcNow;
                    string responseJson;
                    try
                    {
                        responseJson = dispatcher.Dispatch(line);
                    }
                    catch (Exception ex)
                    {
                        responseJson = CommandDispatcher.ErrorJson(null, $"dispatch error: {ex.Message}");
                    }
                    writer.WriteLine(responseJson);
                }
            }
        }

        private void WriteDiscoveryFile()
        {
            using (new DiscoveryFileMutex(_target))
            {
                Directory.CreateDirectory(Path.Combine(DiscoveryRoot, "Dwg"));
                var info = new
                {
                    schema_version = 2,
                    acad_year = int.Parse(_target),
                    transport = "pipe",
                    port = (int?)null,
                    pipe_name = _pipeName,
                    control_pipe_name = ControlPipeName,
                    auth_token = AuthToken,
                    pid = Process.GetCurrentProcess().Id,
                    process_name = Process.GetCurrentProcess().ProcessName,
                    started_at_utc = DateTime.UtcNow
                };

                string destinationPath = JsonDiscoveryPath;
                string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string discoveryJson = JsonConvert.SerializeObject(info, Formatting.Indented);
                try
                {
                    File.WriteAllText(temporaryPath, discoveryJson, new UTF8Encoding(false));
                    if (File.Exists(destinationPath))
                        File.Replace(temporaryPath, destinationPath, null);
                    else
                        File.Move(temporaryPath, destinationPath);
                }
                finally
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                }
            }
        }

        private void DeleteDiscoveryFile()
        {
            try
            {
                using (new DiscoveryFileMutex(_target))
                {
                    if (!File.Exists(JsonDiscoveryPath))
                        return;

                    JObject current = JObject.Parse(File.ReadAllText(JsonDiscoveryPath));
                    if ((int?)current["pid"] != Process.GetCurrentProcess().Id
                        || !string.Equals((string)current["auth_token"], AuthToken, StringComparison.Ordinal)
                        || !string.Equals((string)current["control_pipe_name"], ControlPipeName, StringComparison.Ordinal))
                    {
                        return;
                    }

                    File.Delete(JsonDiscoveryPath);
                }
            }
            catch { }
            finally
            {
#if ACAD2026
                _controlPipeName = null;
#endif
            }
        }

        private string JsonDiscoveryPath => Path.Combine(DiscoveryRoot, "Dwg", "acad-" + _target + ".json");

        private static string DiscoveryRoot => BimwrightAppRoot.Current.TransportRoot;
    }
}
