// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Bimwright.Dwg.Server
{
    public class PluginClient
    {
        private readonly Func<DiscoveryInfo> _discoveryProvider;
        private readonly TimeSpan _timeout;
        private static readonly TimeSpan ProductionTimeout = TimeSpan.FromSeconds(30);

        public PluginClient(Func<DiscoveryInfo> discoveryProvider)
            : this(discoveryProvider, ProductionTimeout)
        {
        }

        internal PluginClient(Func<DiscoveryInfo> discoveryProvider, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive and supported by every transport phase.");
            _discoveryProvider = discoveryProvider;
            _timeout = timeout;
        }

        public static PluginClient FromDiscoveryFile()
        {
            return new PluginClient(() => AuthToken.Resolve(ServerState.Config?.Target));
        }

        public async Task<McpResponse> SendAsync(string cmd, object parameters, string requestId = null)
        {
            DiscoveryInfo discovery;
            try
            {
                discovery = _discoveryProvider();
            }
            catch (Exception ex)
            {
                return Error(Guid.NewGuid().ToString("N"), "plugin discovery error: " + ex.Message);
            }

            return await SendAsync(discovery, cmd, parameters, requestId).ConfigureAwait(false);
        }

        /// <summary>
        /// Sends to an already selected plugin endpoint. Bound-context job
        /// operations use this overload so later target selection cannot reroute
        /// a request to a different AutoCAD instance.
        /// </summary>
        public async Task<McpResponse> SendAsync(DiscoveryInfo discovery, string cmd, object parameters, string requestId = null)
        {
            if (discovery == null)
                return Error(Guid.NewGuid().ToString("N"), "plugin discovery is unavailable");

            var request = new McpRequest
            {
                Id = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId,
                Cmd = cmd,
                Params = parameters,
                Auth = discovery.Token
            };
            var json = JsonConvert.SerializeObject(request);

            try
            {
                McpResponse response;
                if (string.Equals(discovery.Transport, "pipe", StringComparison.OrdinalIgnoreCase))
                {
                    string pipeName = discovery.PipeName;
                    if (IsControlCommand(cmd))
                    {
                        if (string.IsNullOrWhiteSpace(discovery.ControlPipeName))
                            return Error(request.Id, "plugin independent control pipe is unavailable");
                        pipeName = discovery.ControlPipeName;
                    }

                    response = await SendPipeAsync(discovery, pipeName, request.Id, json, _timeout);
                }
                else
                {
                    if (IsControlCommand(cmd))
                        return Error(request.Id, "plugin independent control endpoint is unavailable for this transport");
                    response = await SendTcpAsync(discovery, request.Id, json, _timeout);
                }
                if (response == null)
                    return Error(request.Id, "plugin returned an empty response");
                if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
                    return Error(request.Id, "plugin response ID did not match the request");
                return response;
            }
            catch (Exception ex)
            {
                return Error(request.Id, $"plugin communication error: {ex.Message}");
            }
        }

        private static async Task<McpResponse> SendTcpAsync(DiscoveryInfo discovery, string requestId, string json, TimeSpan timeout)
        {
            using var tcp = new TcpClient();
            var host = string.IsNullOrWhiteSpace(discovery.Host) ? "127.0.0.1" : discovery.Host;
            var connectTask = tcp.ConnectAsync(host, discovery.Port ?? 0);
            if (await Task.WhenAny(connectTask, Task.Delay(timeout)) != connectTask)
                return Error(requestId, "plugin connect timeout");
            await connectTask;

            using var stream = tcp.GetStream();
            return await SendStreamAsync(stream, requestId, json, timeout);
        }

        private static bool IsControlCommand(string cmd) =>
            string.Equals(cmd, "job_get", StringComparison.Ordinal)
            || string.Equals(cmd, "job_cancel", StringComparison.Ordinal)
            || string.Equals(cmd, "job_get_historical", StringComparison.Ordinal)
            || string.Equals(cmd, "job_write_finalize", StringComparison.Ordinal);

        private static async Task<McpResponse> SendPipeAsync(DiscoveryInfo discovery, string pipeName, string requestId, string json, TimeSpan timeout)
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            int connectTimeoutMilliseconds = (int)Math.Ceiling(timeout.TotalMilliseconds);
            var connectTask = pipe.ConnectAsync(connectTimeoutMilliseconds);
            if (await Task.WhenAny(connectTask, Task.Delay(timeout)) != connectTask)
                return Error(requestId, "plugin pipe connect timeout");
            await connectTask;

            return await SendStreamAsync(pipe, requestId, json, timeout);
        }

        private static async Task<McpResponse> SendStreamAsync(Stream stream, string requestId, string json, TimeSpan timeout)
        {
            // Do not let the writer/reader dispose the underlying transport stream.
            // Both wrap the same NamedPipeClientStream/NetworkStream; disposing it
            // twice throws "Cannot access a closed pipe" on the second Dispose and
            // masks an otherwise-successful response.
            var utf8 = new UTF8Encoding(false);
            using var writer = new StreamWriter(stream, utf8, -1, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, utf8, true, -1, leaveOpen: true);

            await writer.WriteLineAsync(json);
            var readTask = reader.ReadLineAsync();
            if (await Task.WhenAny(readTask, Task.Delay(timeout)) != readTask)
                return Error(requestId, "plugin read timeout");

            var line = await readTask;
            if (line == null) return Error(requestId, "plugin closed connection");
            return JsonConvert.DeserializeObject<McpResponse>(line);
        }

        private static McpResponse Error(string id, string message) =>
            new McpResponse { Id = id, Ok = false, Result = null, Error = message };
    }

    public class DiscoveryInfo
    {
        [JsonProperty("schema_version")] public int SchemaVersion { get; set; }
        [JsonProperty("acad_year")] public int AcadYear { get; set; }
        [JsonProperty("target")] public string Target { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("transport")] public string Transport { get; set; } = "tcp";
        [JsonProperty("host")] public string Host { get; set; } = "127.0.0.1";
        // Pipe transport (AutoCAD 2025-2027) writes "port": null in the discovery
        // file. Deserializing null into a non-nullable int throws inside
        // AuthToken.TryReadJson's try/catch, which is swallowed and reported as
        // "invalid" discovery file, causing the file to be deleted on every call.
        [JsonProperty("port")] public int? Port { get; set; }
        [JsonProperty("pipe_name")] public string PipeName { get; set; }
        [JsonProperty("control_pipe_name")] public string ControlPipeName { get; set; }
        [JsonProperty("pipe_path")] public string PipePath { set => PipeName = value; }
        [JsonProperty("auth_token")] public string Token { get; set; }
        [JsonProperty("pid")] public int Pid { get; set; }
        [JsonProperty("process_name")] public string ProcessName { get; set; }
        [JsonProperty("started_at_utc")] public DateTime? StartedAtUtc { get; set; }
        [JsonIgnore] public string DiscoveryFile { get; set; }

        public DiscoveryInfo Snapshot() => new DiscoveryInfo
        {
            SchemaVersion = SchemaVersion,
            AcadYear = AcadYear,
            Target = Target,
            Version = Version,
            Transport = Transport,
            Host = Host,
            Port = Port,
            PipeName = PipeName,
            ControlPipeName = ControlPipeName,
            Token = Token,
            Pid = Pid,
            ProcessName = ProcessName,
            StartedAtUtc = StartedAtUtc,
            DiscoveryFile = DiscoveryFile
        };
    }
}
