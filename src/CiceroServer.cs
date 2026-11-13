using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RFIDlibra
{
    /// <summary>
    /// Lille HTTP-server på localhost:1667, der svarer som Deichmans go-feig - samme protokol som NFClibra.
    /// Cicero Mobile (valgt RFID-leverandør "Deichman") taler med den præcis som med en FEIG-læser.
    ///
    /// Sikkerhed: kun forespørgsler fra Cicero Mobile (Origin https://cicero.systematic.com), og kun rettet
    /// til localhost, får svar. Serveren lytter kun på pc'ens egen loopback-adresse.
    /// </summary>
    public sealed class CiceroServer
    {
        public const int Port = 1667;
        public static readonly string[] AllowedOrigins = { "https://cicero.systematic.com" };
        private static readonly string[] LocalHosts = { "localhost", "127.0.0.1", "[::1]" };

        private const int MaxSseClients = 4;
        private const int MaxHeaderBytes = 16384;
        private const int RequestDeadlineMs = 5000;

        /// <summary>Det, serveren skal bruge fra resten af programmet. Kaldes fra serverens tråde.</summary>
        public interface IHub
        {
            string StatusJson();
            string InventoryJson();
            Result Alarm(bool on);
            Result Write(string barcode);
            Result WriteTagBarcode(string tagId, string barcode);
            void OnClientConnected();
            string Mode { get; set; }
        }

        public sealed class Result
        {
            public int Code;
            public string Body;
            public bool Json;
            public Result(int code, string body, bool json = false) { Code = code; Body = body; Json = json; }
        }

        private sealed class SseClient
        {
            public Stream Out;
            public readonly object Sync = new object();
        }

        private readonly Log log;
        private readonly IHub hub;
        private readonly List<SseClient> clients = new List<SseClient>();
        private readonly List<TcpListener> listeners = new List<TcpListener>();
        private volatile bool running;

        public string BindError { get; private set; }
        public event Action ClientsChanged;

        public CiceroServer(Log log, IHub hub)
        {
            this.log = log;
            this.hub = hub;
        }

        public int ClientCount
        {
            get { lock (clients) return clients.Count; }
        }

        public void Start()
        {
            running = true;
            foreach (IPAddress addr in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                try
                {
                    TcpListener l = new TcpListener(addr, Port);
                    // Ingen "exclusive address use": så kan programmet genstartes, selvom gamle forbindelser står i TIME_WAIT.
                    // (Kun på Linux/Mono, hvor testene køres, skal genbrug slås til eksplicit.)
                    if (Environment.OSVersion.Platform == PlatformID.Unix)
                        l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    l.Start();
                    listeners.Add(l);
                    log.Write("Cicero-forbindelse: lytter på " + (addr.AddressFamily == AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1") + ":" + Port);
                    Thread t = new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "http-" + addr };
                    t.Start();
                }
                catch (Exception e)
                {
                    // IPv4 er den, Cicero bruger; mangler IPv6, er det ikke en fejl
                    if (addr.Equals(IPAddress.Loopback))
                    {
                        BindError = "Port " + Port + " er optaget af et andet program";
                        log.Write("Cicero-forbindelse: kunne ikke lytte på 127.0.0.1:" + Port + " (" + e.Message + ")");
                    }
                }
            }
            Thread ping = new Thread(() =>
            {
                while (running)
                {
                    Thread.Sleep(15000);
                    Send(": ping\n\n");
                }
            }) { IsBackground = true, Name = "sse-ping" };
            ping.Start();
        }

        public void Stop()
        {
            running = false;
            foreach (TcpListener l in listeners) try { l.Stop(); } catch { }
            lock (clients)
            {
                foreach (SseClient c in clients) try { c.Out.Close(); } catch { }
                clients.Clear();
            }
        }

        public void Broadcast(string ev, string data)
        {
            Send("event: " + ev + "\ndata: " + data + "\n\n");
        }

        private void Send(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            SseClient[] all;
            lock (clients) all = clients.ToArray();
            foreach (SseClient c in all)
            {
                try
                {
                    lock (c.Sync)
                    {
                        c.Out.Write(bytes, 0, bytes.Length);
                        c.Out.Flush();
                    }
                }
                catch
                {
                    lock (clients) clients.Remove(c);
                }
            }
        }

        private void AcceptLoop(TcpListener l)
        {
            while (running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { if (!running) return; continue; }
                ThreadPool.QueueUserWorkItem(_ => Handle(c));
            }
        }

        private sealed class Request
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Query = new Dictionary<string, string>();
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }


        private void Handle(TcpClient tcp)
        {
            try
            {
                tcp.ReceiveTimeout = RequestDeadlineMs;
                tcp.SendTimeout = RequestDeadlineMs;
                NetworkStream ns = tcp.GetStream();
                Request req = ReadRequest(ns);
                if (req == null) { tcp.Close(); return; }
                string origin;
                req.Headers.TryGetValue("Origin", out origin);
                string host;
                req.Headers.TryGetValue("Host", out host);
                host = HostOnly(host ?? "");

                if (origin == null || !AllowedOrigins.Contains(origin) || !LocalHosts.Contains(host))
                {
                    log.Write("Afvist forespørgsel fra " + (origin ?? "ukendt afsender") + " til " + req.Path);
                    Respond(ns, 403, "text/plain", "Forbidden", null, null);
                    tcp.Close();
                    return;
                }

                if (req.Method == "OPTIONS")
                {
                    List<string> extra = new List<string>();
                    string m, h;
                    extra.Add("Access-Control-Allow-Methods: " + (req.Headers.TryGetValue("Access-Control-Request-Method", out m) ? m : "GET, POST, HEAD"));
                    extra.Add("Access-Control-Max-Age: 600");
                    if (req.Headers.TryGetValue("Access-Control-Request-Headers", out h)) extra.Add("Access-Control-Allow-Headers: " + h);
                    if (req.Headers.ContainsKey("Access-Control-Request-Private-Network")) extra.Add("Access-Control-Allow-Private-Network: true");
                    Respond(ns, 204, "text/plain", "", origin, extra);
                    tcp.Close();
                    return;
                }

                if (req.Path == "/events")
                {
                    ServeEvents(tcp, ns, origin);
                    return;
                }

                CiceroServer.Result r = Route(req);
                if (req.Path == "/.status")
                    log.Write("Cicero → /.status (test forbindelse)");
                else
                {
                    string q = req.Query.Count == 0 ? "" : "?" + string.Join("&", req.Query.Select(kv =>
                        kv.Key == "barcode" || kv.Key == "tagid" ? kv.Key + "=" + Trunc(kv.Value, 32) : kv.Key + "=…"));
                    log.Write("Cicero → " + req.Method + " " + req.Path + q + " → " + r.Code);
                }
                Respond(ns, r.Code, r.Json ? "application/json" : "text/plain; charset=utf-8", r.Body, origin, null);
                tcp.Close();
            }
            catch (Exception e)
            {
                log.Write("HTTP-fejl: " + e.Message);
                try { tcp.Close(); } catch { }
            }
        }

        private static string Trunc(string s, int n)
        {
            return s.Length > n ? s.Substring(0, n) : s;
        }

        private CiceroServer.Result Route(Request req)
        {
            try
            {
                switch (req.Path)
                {
                    case "/.status": return new Result(200, hub.StatusJson(), true);
                    case "/scan": return new Result(200, hub.InventoryJson(), true);
                    case "/start": hub.Mode = "SCAN"; return new Result(200, "");
                    case "/stop": hub.Mode = "IDLE"; return new Result(200, "");
                    case "/alarmOn": return hub.Alarm(true);
                    case "/alarmOff": return hub.Alarm(false);
                    case "/write":
                        {
                            string bc;
                            if (!req.Query.TryGetValue("barcode", out bc) || bc.Length == 0) return new Result(400, "Url Param 'barcode' is missing");
                            return hub.Write(bc);
                        }
                    case "/writetagbarcode":
                        {
                            string id, bc;
                            if (!req.Query.TryGetValue("tagid", out id) || id.Length == 0) return new Result(400, "Url Param 'tagid' is missing");
                            if (!req.Query.TryGetValue("barcode", out bc) || bc.Length == 0) return new Result(400, "Url Param 'barcode' is missing");
                            return hub.WriteTagBarcode(id, bc);
                        }
                    default: return new Result(404, "404 page not found");
                }
            }
            catch (Exception e)
            {
                log.Write("Fejl ved " + req.Path + ": " + e.Message);
                return new Result(500, e.Message);
            }
        }

        private void ServeEvents(TcpClient tcp, NetworkStream ns, string origin)
        {
            if (ClientCount >= MaxSseClients)
            {
                Respond(ns, 503, "text/plain", "Too many listeners", origin, null);
                tcp.Close();
                return;
            }
            StringBuilder head = new StringBuilder();
            head.Append("HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: text/event-stream\r\n");
            head.Append("Cache-Control: no-cache\r\n");
            head.Append("Connection: keep-alive\r\n");
            foreach (string h in Cors(origin)) head.Append(h).Append("\r\n");
            head.Append("\r\n");
            SseClient client = new SseClient { Out = ns };
            byte[] hb = Encoding.UTF8.GetBytes(head.ToString());
            lock (client.Sync)
            {
                ns.Write(hb, 0, hb.Length);
                ns.Flush();
            }
            int count;
            lock (clients) { clients.Add(client); count = clients.Count; }
            // Cicero åbner og lukker forbindelsen ved hvert skærmskift - log kun, når den begynder/holder op med at lytte
            if (count == 1) log.Write("Cicero lytter (RFID slået til)");
            RaiseClientsChanged();
            hub.OnClientConnected();
            try
            {
                tcp.ReceiveTimeout = 0;
                byte[] buf = new byte[256];
                while (ns.Read(buf, 0, buf.Length) > 0) { }
            }
            catch { }
            finally
            {
                lock (clients) { clients.Remove(client); count = clients.Count; }
                try { tcp.Close(); } catch { }
                if (count == 0) log.Write("Cicero lytter ikke længere (RFID slået fra eller side uden RFID)");
                RaiseClientsChanged();
            }
        }

        private void RaiseClientsChanged()
        {
            Action h = ClientsChanged;
            if (h != null) try { h(); } catch { }
        }

        private static IEnumerable<string> Cors(string origin)
        {
            if (origin == null) yield break;
            yield return "Access-Control-Allow-Origin: " + origin;
            yield return "Access-Control-Allow-Credentials: true";
            yield return "Vary: Origin";
        }

        private static void Respond(Stream outp, int code, string type, string body, string origin, IEnumerable<string> extra)
        {
            byte[] b = Encoding.UTF8.GetBytes(body ?? "");
            string reason;
            switch (code)
            {
                case 200: reason = "OK"; break;
                case 204: reason = "No Content"; break;
                case 400: reason = "Bad Request"; break;
                case 403: reason = "Forbidden"; break;
                case 404: reason = "Not Found"; break;
                case 500: reason = "Internal Server Error"; break;
                case 503: reason = "Service Unavailable"; break;
                default: reason = "Status"; break;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            if (code != 204)
            {
                sb.Append("Content-Type: ").Append(type).Append("\r\n");
                sb.Append("Content-Length: ").Append(b.Length).Append("\r\n");
            }
            sb.Append("Connection: close\r\n");
            foreach (string h in Cors(origin)) sb.Append(h).Append("\r\n");
            if (extra != null) foreach (string h in extra) sb.Append(h).Append("\r\n");
            sb.Append("\r\n");
            byte[] hb = Encoding.UTF8.GetBytes(sb.ToString());
            outp.Write(hb, 0, hb.Length);
            if (code != 204) outp.Write(b, 0, b.Length);
            outp.Flush();
        }

        private static string HostOnly(string h)
        {
            h = h.Trim().ToLowerInvariant();
            if (h.StartsWith("[")) { int e = h.IndexOf(']'); return e > 0 ? h.Substring(0, e + 1) : h; }
            int c = h.IndexOf(':');
            return c >= 0 ? h.Substring(0, c) : h;
        }

        private static Request ReadRequest(Stream inp)
        {
            int budget = MaxHeaderBytes;
            string first = ReadLine(inp, ref budget);
            if (first == null) return null;
            string[] parts = first.Split(' ');
            if (parts.Length < 2) return null;
            Request req = new Request { Method = parts[0].ToUpperInvariant() };
            int lines = 0;
            while (true)
            {
                string line = ReadLine(inp, ref budget);
                if (line == null || line.Length == 0) break;
                if (++lines > 64) throw new IOException("for mange headere");
                int i = line.IndexOf(':');
                if (i > 0) req.Headers[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
            string lenS;
            int len = 0;
            if (req.Headers.TryGetValue("Content-Length", out lenS)) int.TryParse(lenS, out len);
            if (len < 0 || len > 65536) throw new IOException("for stor forespørgsel");
            byte[] skip = new byte[1024];
            while (len > 0)
            {
                int n = inp.Read(skip, 0, Math.Min(len, skip.Length));
                if (n <= 0) break;
                len -= n;
            }

            string target = parts[1];
            int q = target.IndexOf('?');
            string path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target).TrimEnd('/');
            req.Path = path.Length == 0 ? "/" : path;
            if (q >= 0)
            {
                foreach (string pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int e = pair.IndexOf('=');
                    string k = e >= 0 ? pair.Substring(0, e) : pair;
                    string v = e >= 0 ? pair.Substring(e + 1) : "";
                    req.Query[Decode(k)] = Decode(v);
                }
            }
            return req;
        }

        private static string Decode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace("+", "%20")); } catch { return s; }
        }

        private static string ReadLine(Stream inp, ref int budget)
        {
            MemoryStream buf = new MemoryStream();
            while (true)
            {
                int c = inp.ReadByte();
                if (c == -1) return buf.Length == 0 ? null : Encoding.GetEncoding(28591).GetString(buf.ToArray());
                if (c == '\n') break;
                if (c != '\r') buf.WriteByte((byte)c);
                if (--budget < 0) throw new IOException("for store headere");
            }
            return Encoding.GetEncoding(28591).GetString(buf.ToArray());
        }
    }
}
