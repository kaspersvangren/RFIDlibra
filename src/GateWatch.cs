using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RFIDlibra
{
    /// <summary>Indstillingerne til gatealarmer. Gemmes pr. bruger, eller for hele pc'en i C:\ProgramData\RFIDlibra.</summary>
    public sealed class GateSettings
    {
        public const string FileName = "porte.txt";

        public bool Enabled;
        public string Server = "";        // fx 192.168.130.31:6008
        public string Sip2Url = "";
        public string Sip2User = "";
        public string Sip2Password = "";  // ukrypteret i hukommelsen, krypteret i filen
        public string Institution = "";
        public Dictionary<int, string> Names = new Dictionary<int, string>();
        public string Source = "";        // "bruger", "pc" eller ""

        public static string UserFile { get { return Path.Combine(AppInfo.DataDir, FileName); } }
        public static string MachineFile { get { return Path.Combine(AllUsers.MachineDataDir, FileName); } }

        public string Name(int gate)
        {
            string n;
            return Names.TryGetValue(gate, out n) && n.Trim().Length > 0 ? n.Trim() : "Port " + gate;
        }

        public static GateSettings Load()
        {
            GateSettings s = new GateSettings();
            string file = File.Exists(UserFile) ? UserFile : File.Exists(MachineFile) ? MachineFile : null;
            if (file == null) return s;
            s.Source = file == UserFile ? "bruger" : "pc";
            try
            {
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    int gate;
                    if (k == "til") s.Enabled = v == "1";
                    else if (k == "server") s.Server = v;
                    else if (k == "sip2url") s.Sip2Url = v;
                    else if (k == "sip2bruger") s.Sip2User = v;
                    else if (k == "sip2kode") s.Sip2Password = Unprotect(v);
                    else if (k == "institution") s.Institution = v;
                    else if (k.StartsWith("navn") && int.TryParse(k.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out gate)) s.Names[gate] = v;
                }
            }
            catch { }
            return s;
        }

        public string Serialize()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("til=").Append(Enabled ? "1" : "0").Append("\r\n");
            sb.Append("server=").Append(OneLine(Server)).Append("\r\n");
            foreach (KeyValuePair<int, string> kv in Names.OrderBy(k => k.Key))
                if (kv.Value.Trim().Length > 0) sb.Append("navn").Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('=').Append(OneLine(kv.Value)).Append("\r\n");
            sb.Append("sip2url=").Append(OneLine(Sip2Url)).Append("\r\n");
            sb.Append("sip2bruger=").Append(OneLine(Sip2User)).Append("\r\n");
            sb.Append("sip2kode=").Append(Protect(Sip2Password)).Append("\r\n");
            sb.Append("institution=").Append(OneLine(Institution)).Append("\r\n");
            return sb.ToString();
        }

        public void SaveForUser()
        {
            File.WriteAllText(UserFile, Serialize(), new UTF8Encoding(false));
            Source = "bruger";
        }

        private static string OneLine(string v) { return (v ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); }

        // Adgangskoden krypteres med Windows (DPAPI) for hele pc'en, så den også virker, når den er gemt til alle brugere.
        // Den kan kun læses på den pc, hvor den er gemt.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RFIDlibra-SIP2");

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine)); }
            catch { return ""; }
        }

        private static string Unprotect(string b64)
        {
            if (string.IsNullOrEmpty(b64)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.LocalMachine)); }
            catch { return ""; }
        }
    }

    /// <summary>Én alarm fra en gate.</summary>
    public sealed class GateAlarm
    {
        public int Seq;
        public DateTime Time;
        public int Gate;
        public string Barcode = "";
        public string Title = "";
        public string Author = "";
        public string StatusText = "Slår op …";
        public bool? Loaned;              // null: ikke slået op endnu, eller status ukendt
        public bool Done;                 // opslaget er færdigt (eller opgivet)
        public DateTime? ShownAt;         // hvornår lampen begyndte at blinke for denne alarm

        public bool Red { get { return Loaned != true; } }
        public string TitleLine { get { return Author.Length > 0 ? Title + " · " + Author : Title; } }
    }

    public enum LampState { Offline, Idle, Yellow, Red }

    public sealed class LampView
    {
        public int Gate;
        public string Name;
        public LampState State;
        public List<GateAlarm> Recent = new List<GateAlarm>();
    }

    /// <summary>Det, skrankevinduet viser om gatealarmerne.</summary>
    public sealed class GateView
    {
        public bool Connected;
        public string StatusText = "";
        public List<LampView> Lamps = new List<LampView>();
        public bool Blinking { get { return Lamps.Any(l => l.State == LampState.Red || l.State == LampState.Yellow); } }
        public bool AnyRed { get { return Lamps.Any(l => l.State == LampState.Red); } }
    }

    /// <summary>
    /// Lytter på gatetrackeren (Bibliothecas server, som RFIDif også bruger) og slår alarmerne op i Cicero med SIP2.
    /// Sender aldrig noget til gatetrackeren - forbinder kun og læser.
    /// </summary>
    public sealed class GateWatch
    {
        public const int BlinkSeconds = 20;
        private const int MaxAlarms = 500;
        private const int WaitForLookupMs = 2000;

        private readonly Log log;
        private readonly object sync = new object();
        private readonly List<GateAlarm> alarms = new List<GateAlarm>();
        private readonly HashSet<string> seenIds = new HashSet<string>();
        private List<int> gates = new List<int> { 1, 2, 3, 4 };
        private GateSettings settings;
        private Sip2Client sip2 = new Sip2Client();
        private Thread thread;
        private volatile bool running;
        private volatile TcpClient client;
        private volatile bool connected;
        private volatile string status = "";
        private int seq;
        private int generation;

        public GateWatch(Log log)
        {
            this.log = log;
            settings = GateSettings.Load();
            ApplySip2();
        }

        public GateSettings Settings { get { lock (sync) return settings; } }
        public bool Connected { get { return connected; } }
        public string Status { get { return status; } }
        public Sip2Client Sip2 { get { return sip2; } }

        /// <summary>Udfylder SIP2-adresse og institution ud fra biblioteksnummeret, hvis de ikke er sat.</summary>
        public static void Defaults(GateSettings s, string library)
        {
            string lib = new string((library ?? "").Where(char.IsDigit).ToArray());
            if (lib.Length == 0) return;
            if (s.Institution.Length == 0) s.Institution = "DK-" + lib;
            if (s.Sip2Url.Length == 0) s.Sip2Url = "https://cicero-fbs.com/rest/sip2/DK-" + lib;
        }

        private void ApplySip2()
        {
            Sip2Client c = new Sip2Client { Url = settings.Sip2Url, User = settings.Sip2User, Password = settings.Sip2Password, Institution = settings.Institution };
            sip2 = c;
        }

        public void Start()
        {
            lock (sync)
            {
                if (!settings.Enabled || settings.Server.Length == 0) { status = settings.Enabled ? "Skriv gatetrackerens adresse" : ""; return; }
                running = true;
                int gen = ++generation;
                thread = new Thread(() => Run(gen)) { IsBackground = true, Name = "Gatetracker" };
                thread.Start();
            }
        }

        public void Stop()
        {
            running = false;
            lock (sync) generation++;
            TcpClient c = client;
            if (c != null) try { c.Close(); } catch { }
            connected = false;
        }

        /// <summary>Nye indstillinger: genstarter lytningen, hvis noget vigtigt er ændret.</summary>
        public void Apply(GateSettings s)
        {
            bool restart;
            lock (sync)
            {
                restart = s.Enabled != settings.Enabled || s.Server != settings.Server || (s.Enabled && !running);
                settings = s;
                ApplySip2();
            }
            if (restart)
            {
                Stop();
                if (s.Enabled) Start();
                else { status = ""; log.Write("Gatealarmer er slået fra."); }
            }
        }

        // ---------- Forbindelsen ----------

        private bool Current(int gen) { lock (sync) return running && gen == generation; }

        private void Run(int gen)
        {
            string server;
            lock (sync) server = settings.Server;
            string host = server;
            int port = 6008;
            int c = server.LastIndexOf(':');
            if (c > 0 && int.TryParse(server.Substring(c + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) host = server.Substring(0, c);
            else port = 6008;
            bool loggedFail = false;

            while (Current(gen))
            {
                TcpClient tcp = new TcpClient();
                try
                {
                    status = "Forbinder til " + host + ":" + port + " …";
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) throw new IOException("svarer ikke");
                    tcp.EndConnect(ar);
                    client = tcp;
                    connected = true;
                    loggedFail = false;
                    status = "Forbundet til gatetrackeren";
                    log.Write("Gatealarmer: forbundet til gatetrackeren " + host + ":" + port + " (lytter kun).");
                    Listen(tcp, gen);
                    if (Current(gen)) log.Write("Gatealarmer: gatetrackeren lukkede forbindelsen - prøver igen om 10 sekunder.");
                }
                catch (Exception e)
                {
                    if (!Current(gen)) break;
                    string why = e is SocketException ? ((SocketException)e).SocketErrorCode.ToString() : e.Message;
                    if (connected) log.Write("Gatealarmer: forbindelsen til gatetrackeren blev afbrudt (" + why + ") - prøver igen om 10 sekunder.");
                    else if (!loggedFail) log.Write("Gatealarmer: kan ikke forbinde til " + host + ":" + port + " (" + why + ") - prøver igen hvert 10. sekund.");
                    loggedFail = true;
                    status = "Ingen forbindelse til " + host + ":" + port + " (" + why + ")";
                }
                finally
                {
                    if (status.StartsWith("Forbundet")) status = "Forbindelsen til gatetrackeren blev afbrudt – prøver igen";
                    connected = false;
                    client = null;
                    try { tcp.Close(); } catch { }
                }
                for (int i = 0; i < 100 && Current(gen); i++) Thread.Sleep(100);
            }
        }

        private void Listen(TcpClient tcp, int gen)
        {
            NetworkStream ns = tcp.GetStream();
            ns.ReadTimeout = 60000;   // gatetrackeren sender status hvert sekund; intet i et minut = død forbindelse
            byte[] buf = new byte[8192];
            MemoryStream msg = new MemoryStream();
            while (Current(gen))
            {
                int n = ns.Read(buf, 0, buf.Length);
                if (n <= 0) return;
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (b == 0x0D) { Handle(msg.ToArray()); msg.SetLength(0); }
                    else if (msg.Length < 65536) msg.WriteByte(b);
                }
            }
        }

        private static string Decode(byte[] b)
        {
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(b); }
        }

        /// <summary>Én besked: 0x01 navn 0x02 felt 0x02 felt … (0x0D er allerede fjernet).</summary>
        private void Handle(byte[] raw)
        {
            int start = Array.IndexOf(raw, (byte)0x01);
            if (start < 0) return;
            string[] parts = Decode(raw.Skip(start + 1).ToArray()).Split('\x02');
            string name = parts[0];
            if (name == "UpdatePeopleCounter" && parts.Length > 2)
            {
                List<int> g = new List<int>();
                foreach (string x in parts[2].Split(';')) { int v; if (int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0 && v < 100) g.Add(v); }
                if (g.Count > 0) lock (sync) if (!g.SequenceEqual(gates)) gates = g;
                return;
            }
            if (name != "Add") return;

            Dictionary<string, string> f = new Dictionary<string, string>();
            foreach (string p in parts.Skip(1))
            {
                int i = p.IndexOf('=');
                if (i > 0) f[p.Substring(0, i)] = p.Substring(i + 1);
            }
            string v2;
            if (f.TryGetValue("Old", out v2) && v2 == "yes") return;   // gamle alarmer, som gatetrackeren sender igen
            int gate;
            if (!f.TryGetValue("GateNo", out v2) || !int.TryParse(v2, NumberStyles.Integer, CultureInfo.InvariantCulture, out gate)) return;
            string id = (f.TryGetValue("LogId", out v2) ? v2 : "") + "/" + (f.TryGetValue("DateTime", out v2) ? v2 : "");
            string barcode = f.TryGetValue("Id", out v2) ? v2.Trim() : "";
            if (barcode.Length == 0 && f.TryGetValue("LMS.ItemId", out v2)) barcode = v2.Trim();
            string title = f.TryGetValue("LMS.ItemTitle", out v2) && v2.Trim().Length > 0 ? v2.Trim() : f.TryGetValue("Title", out v2) ? v2.Trim() : "";

            GateAlarm a;
            lock (sync)
            {
                if (!seenIds.Add(id)) return;
                if (seenIds.Count > 5000) seenIds.Clear();
                if (!gates.Contains(gate)) { gates.Add(gate); gates.Sort(); }
                a = new GateAlarm { Seq = ++seq, Time = DateTime.Now, Gate = gate, Barcode = barcode, Title = title.Length > 0 ? title : "Ukendt titel" };
                alarms.Insert(0, a);
                if (alarms.Count > MaxAlarms) alarms.RemoveAt(alarms.Count - 1);
            }
            ThreadPool.QueueUserWorkItem(_ => LookUp(a));
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(WaitForLookupMs); lock (sync) if (a.ShownAt == null) { a.ShownAt = DateTime.Now; if (!a.Done) a.StatusText = "Slår stadig op …"; } });
        }

        private void LookUp(GateAlarm a)
        {
            Sip2Client c;
            string gateName;
            lock (sync) { c = sip2; gateName = settings.Name(a.Gate); }
            Sip2Client.Item it = a.Barcode.Length > 0 ? c.Lookup(a.Barcode) : new Sip2Client.Item { Error = "intet materialenummer" };
            lock (sync)
            {
                if (it.Error == null)
                {
                    a.Loaned = it.Loaned;
                    a.StatusText = Sip2Client.CodeText(it.Code);
                    if (it.Title.Length > 0) { a.Title = it.Title; a.Author = it.Author; }
                    else SplitAuthor(a);
                }
                else
                {
                    a.Loaned = null;
                    a.StatusText = "Status ukendt";
                    SplitAuthor(a);
                }
                a.Done = true;
                if (a.ShownAt == null) a.ShownAt = DateTime.Now;
            }
            log.Write("Gatealarm " + gateName + ": " + a.Barcode + " " + a.TitleLine + " – " + a.StatusText
                + (it.Error != null ? " (" + it.Error + ")" : ""));
        }

        /// <summary>Gatetrackerens titel er "Titel, Efternavn, Fornavn" - forfatteren skilles fra, når Cicero ikke svarer.</summary>
        private static void SplitAuthor(GateAlarm a)
        {
            if (a.Author.Length > 0) return;
            string[] p = a.Title.Split(new[] { ", " }, StringSplitOptions.None);
            if (p.Length >= 3) { a.Author = p[p.Length - 2] + ", " + p[p.Length - 1]; a.Title = string.Join(", ", p.Take(p.Length - 2)); }
        }

        // ---------- Til skrankevinduet og listen ----------

        public GateView View()
        {
            GateView v = new GateView { Connected = connected, StatusText = status };
            DateTime now = DateTime.Now;
            lock (sync)
            {
                foreach (int g in gates)
                {
                    LampView l = new LampView { Gate = g, Name = settings.Name(g) };
                    l.Recent = alarms.Where(a => a.Gate == g).Take(3).ToList();
                    l.State = connected ? LampState.Idle : LampState.Offline;
                    // Blink: 20 sekunder fra den nyeste alarm; en ny alarm forlænger. Rød vinder over gul i samme omgang.
                    List<GateAlarm> shown = alarms.Where(a => a.Gate == g && a.ShownAt != null && (now - a.ShownAt.Value).TotalSeconds < 600)
                        .OrderByDescending(a => a.ShownAt.Value).ToList();
                    if (shown.Count > 0 && (now - shown[0].ShownAt.Value).TotalSeconds < BlinkSeconds)
                    {
                        bool red = shown[0].Red;
                        for (int i = 1; i < shown.Count && (shown[i - 1].ShownAt.Value - shown[i].ShownAt.Value).TotalSeconds < BlinkSeconds; i++)
                            red |= shown[i].Red;
                        l.State = red ? LampState.Red : LampState.Yellow;
                    }
                    v.Lamps.Add(l);
                }
            }
            return v;
        }

        public List<GateAlarm> Alarms()
        {
            lock (sync) return alarms.ToList();
        }

        public List<int> Gates()
        {
            lock (sync) return gates.ToList();
        }

        public int AlarmCount { get { lock (sync) return alarms.Count; } }
    }
}
