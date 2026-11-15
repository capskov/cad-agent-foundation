// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
//
// The control channel carries the four task-governance commands (job_get, job_cancel,
// job_get_historical, job_write_finalize). On AutoCAD 2026 it is a sibling pipe of the
// pipe data channel. AutoCAD 2022 keeps TCP as the data channel, so this listener is
// hosted independently and advertised through discovery instead of being implied by
// the transport kind.
//
// net48 has no PipeOptions.CurrentUserOnly, so the same guarantee is expressed as an
// explicit DACL -- see ServerPipeFactory. Refusing to lose the restriction is a
// requirement, not an implementation detail.
#if ACAD2026
using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bimwright.Dwg.Plugin.Execution;

namespace Bimwright.Dwg.Plugin
{
    internal sealed class ControlPipeListener
    {
        private readonly PipeListenerLifecycle<NamedPipeServerStream> _lifecycle =
            new PipeListenerLifecycle<NamedPipeServerStream>();
        private CommandDispatcher _dispatcher;
        private string _pipeName;

        /// <summary>Advertised in `control_pipe_name`; null while stopped.</summary>
        public string PipeName => _pipeName;

        public bool IsRunning => _lifecycle.IsRunning;

        public void Start(string baseName, CommandDispatcher dispatcher)
        {
            if (string.IsNullOrWhiteSpace(baseName))
                throw new ArgumentException("A control pipe needs a base name.", nameof(baseName));
            if (dispatcher == null)
                throw new ArgumentNullException(nameof(dispatcher));

            _dispatcher = dispatcher;
            _pipeName = baseName + "-control-" + Guid.NewGuid().ToString("N");

            long generation = _lifecycle.Start();
            string pipeName = _pipeName;
            bool started = _lifecycle.RunIfCurrent(generation, () =>
            {
                var acceptThread = new Thread(() => AcceptLoop(generation, pipeName))
                {
                    IsBackground = true,
                    Name = "BimwrightDwg-ControlPipeAccept"
                };
                acceptThread.Start();
            });
            if (!started)
                throw new InvalidOperationException("The control pipe listener was stopped while starting.");
        }

        public void Stop()
        {
            try { _lifecycle.Stop(_ => { }); } catch { }
            _pipeName = null;
        }

        private void AcceptLoop(long generation, string pipeName)
        {
            AcceptLoopAsync(generation, pipeName).GetAwaiter().GetResult();
        }

        private async Task AcceptLoopAsync(long generation, string pipeName)
        {
            while (_lifecycle.IsCurrent(generation))
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = _lifecycle.CreateAndPublish(
                        generation,
                        () => ServerPipeFactory.Create(pipeName, 1),
                        _ => { });
                    if (pipe == null)
                        return;

                    // One instance serves every request in sequence. Recreating it per
                    // connection -- as the 2026 sibling listener does -- leaves a window
                    // in which a client's open() fails with "pipe not found", which the
                    // agent sees as a flaky control channel rather than a real error.
                    // Clients that connect exactly during the Disconnect hand-off see a
                    // transient broken pipe; the server retries those control calls.
                    bool healthy = true;
                    while (healthy && _lifecycle.IsCurrent(generation))
                    {
                        try
                        {
                            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                            await HandleControlClientAsync(pipe, generation).ConfigureAwait(false);
                            // Disconnect discards unflushed bytes: wait until the client
                            // has read the response, otherwise the peer sees a broken
                            // pipe just after a successful control call.
                            try { pipe.WaitForPipeDrain(); } catch { }
                            if (pipe.IsConnected)
                                pipe.Disconnect();
                        }
                        catch
                        {
                            // A client that hangs up mid-frame or misses its read deadline
                            // must not take the listener down; recreate the instance instead.
                            healthy = false;
                        }
                    }
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

                if (!_lifecycle.IsCurrent(generation))
                    return;
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        private async Task HandleControlClientAsync(PipeStream pipe, long generation)
        {
            string requestLine = await PipeControlFrameProtocol.ReadFrameAsync(pipe).ConfigureAwait(false);
            if (requestLine == null || !_lifecycle.IsCurrent(generation))
                return;

            string responseJson;
            try
            {
                responseJson = _dispatcher.DispatchControl(requestLine);
            }
            catch (Exception ex)
            {
                responseJson = CommandDispatcher.ErrorJson(null, $"dispatch error: {ex.Message}");
            }

            if (Encoding.UTF8.GetByteCount(responseJson) > PipeControlFrameProtocol.MaxResponseFrameBytes)
                responseJson = CommandDispatcher.ErrorJson(null, "control_response_too_large");

            await PipeControlFrameProtocol.WriteFrameAsync(pipe, responseJson).ConfigureAwait(false);
            try { await pipe.FlushAsync().ConfigureAwait(false); } catch { }
        }
    }
}
#endif
