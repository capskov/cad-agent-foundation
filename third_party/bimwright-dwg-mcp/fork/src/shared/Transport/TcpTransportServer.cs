// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bimwright.Dwg.Shared;

namespace Bimwright.Dwg.Plugin
{
    public class TcpTransportServer : ITransportServer
    {
        private readonly string _target;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _activeClients;
        private CommandDispatcher _dispatcher;

        public TcpTransportServer(string target = "2024")
        {
            _target = string.IsNullOrWhiteSpace(target) ? "2024" : target;
        }

        public int? Port { get; private set; }
        public string PipeName => null;
        public string AuthToken { get; private set; }
        public bool IsRunning => _running;
        public bool IsClientConnected => _activeClients > 0;
        public DateTime? LastCommandTime { get; private set; }
        public TransportKind Kind => TransportKind.Tcp;
        public string ConnectionInfo => $"TCP:{Port}";

        public void Start()
        {
            AuthToken = Guid.NewGuid().ToString("N");
            _dispatcher = new CommandDispatcher(AuthToken);

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            WriteDiscoveryFiles();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "BimwrightDwg-TcpAccept" };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            DeleteDiscoveryFiles();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }
                var t = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "BimwrightDwg-TcpClient" };
                t.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            Interlocked.Increment(ref _activeClients);
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        LastCommandTime = DateTime.UtcNow;
                        string responseJson;
                        try
                        {
                            responseJson = _dispatcher.Dispatch(line);
                        }
                        catch (Exception ex)
                        {
                            responseJson = CommandDispatcher.ErrorJson(null, $"dispatch error: {ex.Message}");
                        }
                        writer.WriteLine(responseJson);
                    }
                }
            }
            catch
            {
                // Client disconnect or IO error; accept loop continues.
            }
            finally
            {
                Interlocked.Decrement(ref _activeClients);
            }
        }

        private void WriteDiscoveryFiles()
        {
            using (new DiscoveryFileMutex(_target))
            {
                var root = DiscoveryRoot;
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "Dwg"));

                if (_target == "2024")
                {
                    WriteAtomic(LegacyDiscoveryPath,
                        $"{Port}\n{AuthToken}\n{Process.GetCurrentProcess().Id}\n");
                }

                var info = new
                {
                    schema_version = 2,
                    acad_year = int.Parse(_target),
                    transport = "tcp",
                    host = "127.0.0.1",
                    port = Port,
                    pipe_name = (string)null,
                    auth_token = AuthToken,
                    pid = Process.GetCurrentProcess().Id,
                    process_name = Process.GetCurrentProcess().ProcessName,
                    started_at_utc = DateTime.UtcNow
                };
                WriteAtomic(JsonDiscoveryPath, JsonConvert.SerializeObject(info, Formatting.Indented));
            }
        }

        private void DeleteDiscoveryFiles()
        {
            try
            {
                using (new DiscoveryFileMutex(_target))
                {
                    DeleteJsonIfOwned(JsonDiscoveryPath);
                    if (_target == "2024")
                        DeleteLegacyIfOwned(LegacyDiscoveryPath);
                }
            }
            catch { }
        }

        private void DeleteJsonIfOwned(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                JObject current = JObject.Parse(File.ReadAllText(path));
                if ((int?)current["pid"] == Process.GetCurrentProcess().Id
                    && string.Equals((string)current["auth_token"], AuthToken, StringComparison.Ordinal))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        private void DeleteLegacyIfOwned(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                string[] lines = File.ReadAllLines(path);
                if (lines.Length >= 3
                    && string.Equals(lines[1].Trim(), AuthToken, StringComparison.Ordinal)
                    && int.TryParse(lines[2].Trim(), out int pid)
                    && pid == Process.GetCurrentProcess().Id)
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        private static void WriteAtomic(string destinationPath, string content)
        {
            string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
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

        private string JsonDiscoveryPath => Path.Combine(DiscoveryRoot, "Dwg", "acad-" + _target + ".json");

        private static string DiscoveryRoot => BimwrightAppRoot.Current.TransportRoot;

        private static string LegacyDiscoveryPath =>
            Path.Combine(DiscoveryRoot, "portAcad24.txt");
    }
}
