using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RFIDlibra
{
    /// <summary>Et tag som vist i vinduet.</summary>
    public sealed class TagView
    {
        public string Uid;
        public string Barcode = "";
        public string Part = "";
        public string Library = "";
        public string Alarm = "";
        public string Note = "";
        public string Kind = "unread";   // blank / danish / unknown / unread
        public int Seq, Num;
        public int? Afi;
    }

    /// <summary>
    /// Arbejdstråden: al kontakt med læseren sker herfra. Spørger læseren 4 gange i sekundet, hvilke tags
    /// der ligger på den, læser nye tags og melder dem til Cicero. Det Cicero beder om (alarm, skrivning)
    /// lægges i kø og udføres også her, så intet kolliderer på USB.
    /// </summary>
    public sealed class Worker : CiceroServer.IHub
    {
        private readonly Feusb usb;
        private readonly Log log;
        private Reader reader;
        private Thread thread;
        private volatile bool running;
        private volatile bool selfTestRequested;

        // Kun brugt på arbejdstråden
        private readonly Dictionary<string, TagState> tags = new Dictionary<string, TagState>();     // meldt til Cicero
        private readonly Dictionary<string, TagState> pending = new Dictionary<string, TagState>();  // set, men ikke læst endnu
        private string lastRaw;
        private string baseStatus = "";
        private DateTime lastStats = DateTime.MinValue;
        private string lastLibrary;

        private readonly BlockingCollection<Action> jobs = new BlockingCollection<Action>();
        private readonly Stopwatch uptime = Stopwatch.StartNew();
        private readonly Dictionary<string, long> counters = new Dictionary<string, long>
        {
            { "ReadInvFail", 0 }, { "ReadInvSucc", 0 }, { "ReadTagFail", 0 }, { "ReadTagSucc", 0 },
            { "WriteTagSucc", 0 }, { "WriteTagFail", 0 }, { "WriteAFISucc", 0 }, { "WriteAFIFail", 0 },
        };

        public event Action<string> Status;
        public event Action<List<TagView>> TagsChanged;

        /// <summary>Øjebliksbillede til brugerfladen (sat på arbejdstråden, læst fra serveren).</summary>
        public volatile string TagsJson = "[]";
        public volatile bool ReaderOk;
        public volatile string ReaderText = "Starter ...";
        public volatile string StatsText = "";
        public volatile string ReaderName = "";
        /// <summary>Det samme som TagsJson, men til skrankevinduet.</summary>
        public volatile TagView[] Views = new TagView[0];

        public volatile bool LogRaw = false;

        /// <summary>Serveren, som tags meldes til (sat af vinduet).</summary>
        public CiceroServer Server;

        /// <summary>Biblioteksnummer til nye chips (sat i vinduet). Tags, der allerede har et, beholder det.</summary>
        public volatile string LibraryForNewChips = "";

        private volatile string mode = "IDLE";
        public string Mode { get { return mode; } set { mode = value; } }

        public Worker(Feusb usb, Log log)
        {
            this.usb = usb;
            this.log = log;
        }

        public bool Running { get { return running; } }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "RFID" };
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(4000);
            thread = null;
        }

        public void RequestSelfTest()
        {
            selfTestRequested = true;
        }

        private void SetStatus(string s)
        {
            ReaderText = s;
            ReaderOk = reader != null && reader.IsOpen;
            Action<string> h = Status;
            if (h != null) h(s);
        }

        private void Publish()
        {
            StringBuilder sb = new StringBuilder("[");
            foreach (TagState t in tags.Values) { if (sb.Length > 1) sb.Append(','); sb.Append(t.ToUiJson("")); }
            foreach (TagState t in pending.Values) { if (sb.Length > 1) sb.Append(','); sb.Append(t.ToUiJson("kunne ikke læses endnu")); }
            TagsJson = sb.Append(']').ToString();
            List<TagView> list = new List<TagView>();
            foreach (TagState t in tags.Values) list.Add(View(t, ""));
            foreach (TagState t in pending.Values) list.Add(View(t, "kunne ikke læses endnu (" + t.ReadFails + " forsøg)"));
            Views = list.ToArray();
            Action<List<TagView>> h = TagsChanged;
            if (h != null) h(list);
        }

        private static TagView View(TagState t, string note)
        {
            TagView v = new TagView { Uid = t.Mac, Alarm = t.AlarmText, Note = note, Kind = t.Kind, Afi = t.Afi };
            if (t.Content != null)
            {
                v.Seq = t.Content.SeqNum;
                v.Num = t.Content.NumItems;
                v.Barcode = t.Content.Barcode;
                v.Part = t.Content.Part;
                v.Library = (t.Content.Country + "-" + t.Content.Library).Trim('-');
                if (note.Length == 0) v.Note = t.Content.CrcOk ? "" : "CRC passer ikke";
            }
            return v;
        }

        private void Count(string k)
        {
            lock (counters) counters[k] = counters[k] + 1;
        }

        private void Broadcast(string ev, TagState t)
        {
            CiceroServer s = Server;
            if (s != null) s.Broadcast(ev, t.ToJson());
        }

        // ---------- Arbejdstråden ----------

        private void Loop()
        {
            reader = new Reader(usb, log);
            try
            {
                while (running)
                {
                    reader.LogRaw = LogRaw;
                    if (!reader.IsOpen)
                    {
                        SetStatus("Leder efter læser ...");
                        if (!reader.Open())
                        {
                            SetStatus("Ingen læser fundet - sæt den i USB");
                            RunJobs(3000);
                            continue;
                        }
                        baseStatus = reader.DeviceName + ", firmware " + reader.Firmware;
                        ReaderName = baseStatus;
                        SetStatus("Læseren er klar");
                        foreach (TagState t in tags.Values) Broadcast("removeTag", t);
                        tags.Clear();
                        pending.Clear();
                        lastRaw = null;
                        Publish();
                    }

                    Inventory();

                    if ((DateTime.Now - lastStats).TotalSeconds >= 2)
                    {
                        lastStats = DateTime.Now;
                        StatsText = reader.Stats;
                    }

                    if (selfTestRequested)
                    {
                        selfTestRequested = false;
                        SelfTest(tags.Values.Concat(pending.Values).ToList());
                    }
                    RunJobs(250);
                }
            }
            catch (Exception e)
            {
                log.Write("UVENTET FEJL: " + e);
                SetStatus("Fejl - se loggen");
            }
            finally
            {
                reader.Close();
                running = false;
                Views = new TagView[0];
                TagsJson = "[]";
                SetStatus("Stoppet");
                Action a;
                while (jobs.TryTake(out a)) try { a(); } catch { }
            }
        }

        /// <summary>Udfører ventende opgaver fra Cicero i op til 'ms' millisekunder.</summary>
        private void RunJobs(int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (running)
            {
                int left = ms - (int)sw.ElapsedMilliseconds;
                if (left <= 0) left = 0;
                Action job;
                if (!jobs.TryTake(out job, left)) return;
                job();
            }
        }

        private void Inventory()
        {
            string raw;
            List<Reader.Seen> seen = reader.Inventory(out raw);
            if (seen == null)
            {
                Count("ReadInvFail");
                if (raw != null && raw != lastRaw) log.Write("Inventory gav fejl: " + raw);
                lastRaw = raw;
                return;
            }
            Count("ReadInvSucc");
            if (raw != lastRaw && LogRaw) log.Write("Inventory: " + raw);
            lastRaw = raw;

            bool changed = false;
            HashSet<string> present = new HashSet<string>(seen.Select(s => s.Key));

            // Et tag skal mangle i 2 runder i træk, før det meldes væk - så et enkelt tabt svar ikke giver blink i Cicero
            foreach (TagState t in tags.Values.ToList())
            {
                if (present.Contains(t.Mac)) { t.Misses = 0; continue; }
                if (++t.Misses < 2) continue;
                tags.Remove(t.Mac);
                Broadcast("removeTag", t);
                log.Write("Væk: " + t.Label);
                changed = true;
            }
            foreach (string k in pending.Keys.Where(k => !present.Contains(k)).ToList())
            {
                pending.Remove(k);
                changed = true;
            }

            foreach (Reader.Seen s in seen)
            {
                if (tags.ContainsKey(s.Key)) continue;
                TagState t;
                if (!pending.TryGetValue(s.Key, out t))
                {
                    t = new TagState { Uid = s.Uid, Mac = s.Key, Dsfid = s.Dsfid };
                    pending[s.Key] = t;
                    changed = true;
                }
                if (ReadTag(t))
                {
                    pending.Remove(t.Mac);
                    tags[t.Mac] = t;
                    Broadcast("addTag", t);
                    CiceroServer srv = Server;
                    log.Write("Læst: " + t.Label + ", alarm " + t.AlarmText + (srv != null && srv.ClientCount == 0 ? " (Cicero lytter ikke)" : ""));
                    changed = true;
                }
                else
                {
                    t.ReadFails++;
                    if (t.ReadFails == 1 || t.ReadFails % 10 == 0)
                        log.Write("Tag " + t.Mac + " kunne ikke læses (" + t.ReadFails + ". gang) - prøver igen.");
                    changed = true;
                }
                if (!reader.IsOpen) break;
            }
            if (changed) Publish();
        }

        private bool ReadTag(TagState t)
        {
            t.Afi = reader.ReadAfi(t.Uid);
            string method;
            byte[] data = reader.ReadDanish(t.Uid, out method);
            if (data == null) { Count("ReadTagFail"); return false; }
            Count("ReadTagSucc");
            if (LogRaw) log.Write("Tag " + t.Mac + ": 32 bytes = " + Protocol.Hex(data));
            t.Raw = data;
            t.Content = TagContent.Parse(data);
            if (t.Content != null && t.Content.Library.Length > 0) lastLibrary = t.Content.Library;
            return t.Content != null;
        }

        // ---------- Det Cicero kalder (via serveren, fra serverens tråde) ----------

        private T OnWorker<T>(Func<T> f)
        {
            if (!running) throw new InvalidOperationException("Reader not connected");
            T result = default(T);
            Exception error = null;
            using (ManualResetEventSlim done = new ManualResetEventSlim(false))
            {
                jobs.Add(() =>
                {
                    try { result = f(); }
                    catch (Exception e) { error = e; }
                    finally { try { done.Set(); } catch { } }
                });
                if (!done.Wait(10000)) throw new TimeoutException("Læseren svarede ikke inden for 10 sekunder");
            }
            if (error != null) throw error;
            return result;
        }

        public string InventoryJson()
        {
            return OnWorker(() => TagState.InventoryJson(tags.Values));
        }

        public string StatusJson()
        {
            return OnWorker(() =>
            {
                TimeSpan up = uptime.Elapsed;
                StringBuilder u = new StringBuilder();
                if (up.TotalHours >= 1) u.Append((int)up.TotalHours).Append('h');
                if (up.TotalMinutes >= 1) u.Append(up.Minutes).Append('m');
                u.Append((up.Seconds + up.Milliseconds / 1000.0).ToString("0.000", CultureInfo.InvariantCulture)).Append('s');

                long serial = 0;
                if (reader.DeviceId != null) long.TryParse(reader.DeviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out serial);
                StringBuilder sb = new StringBuilder("{");
                sb.Append("\"Uptime\":").Append(Json.Str(u.ToString()));
                sb.Append(",\"Reader\":{\"StrHandle\":null,\"PortHandle\":0,\"ReaderHandle\":0");
                sb.Append(",\"Serial\":").Append(Json.Str(reader.DeviceId ?? ""));
                sb.Append(",\"IntSerial\":").Append(serial);
                sb.Append(",\"Name\":").Append(Json.Str((reader.DeviceName ?? "FEIG") + " (RFIDlibra " + AppInfo.Version + ")"));
                sb.Append(",\"Family\":").Append(Json.Str(reader.Family ?? ""));
                lock (counters) foreach (KeyValuePair<string, long> kv in counters) sb.Append(',').Append(Json.Str(kv.Key)).Append(':').Append(kv.Value);
                sb.Append("},\"LastInventory\":").Append(TagState.InventoryJson(tags.Values));
                sb.Append(",\"Client\":\"127.0.0.1\",\"Mode\":").Append(Json.Str(mode)).Append('}');
                return sb.ToString();
            });
        }

        public CiceroServer.Result Alarm(bool on)
        {
            return OnWorker(() =>
            {
                int value = on ? TagState.AFI_ON : TagState.AFI_OFF;
                string word = on ? "TIL" : "FRA";
                if (!reader.IsOpen) return new CiceroServer.Result(500, "Reader not connected");
                if (tags.Count == 0)
                {
                    log.Write("Alarm " + word + ": ingen bøger på læseren");
                    return new CiceroServer.Result(400, "Inventory empty");
                }
                foreach (TagState t in tags.Values)
                {
                    string err;
                    if (!reader.WriteAfi(t.Uid, value, out err))
                    {
                        Count("WriteAFIFail");
                        log.Write("Alarm " + word + " FEJLEDE på " + t.Label + ": " + err);
                        Publish();
                        return new CiceroServer.Result(500, "Failed activating alarm on id " + t.Mac + ", err: " + err + " ");
                    }
                    t.Afi = value;
                    Count("WriteAFISucc");
                }
                log.Write("Alarm " + word + ": " + string.Join(", ", tags.Values.Select(x => x.Label)));
                Publish();
                return new CiceroServer.Result(200, "OK");
            });
        }

        public CiceroServer.Result Write(string barcode)
        {
            return OnWorker(() =>
            {
                if (!reader.IsOpen) return new CiceroServer.Result(500, "Reader not connected");
                if (tags.Count == 0) return new CiceroServer.Result(400, "Inventory empty");
                int n = tags.Count, i = 0;
                foreach (TagState t in tags.Values.ToList())
                {
                    i++;
                    string err = WriteContent(t, barcode, n, i);
                    if (err != null)
                    {
                        log.Write("Skriv " + barcode + " FEJLEDE: " + err);
                        Publish();
                        return new CiceroServer.Result(400, "Error writing inventory: " + err);
                    }
                }
                log.Write("Skrevet: " + barcode + " til " + n + " tag(s)");
                Publish();
                return new CiceroServer.Result(200, TagState.InventoryJson(tags.Values), true);
            });
        }

        public CiceroServer.Result WriteTagBarcode(string tagId, string barcode)
        {
            return OnWorker(() =>
            {
                if (!reader.IsOpen) return new CiceroServer.Result(500, "Reader not connected");
                if (tags.Count == 0) return new CiceroServer.Result(400, "Inventory empty");
                TagState t;
                if (!tags.TryGetValue(tagId.ToUpperInvariant(), out t)) return new CiceroServer.Result(400, "Tag not in range: " + tagId);
                int numItems = t.Content != null && t.Content.NumItems > 0 ? t.Content.NumItems : 1;
                int seqNum = t.Content != null && t.Content.SeqNum > 0 ? t.Content.SeqNum : 1;
                string err = WriteContent(t, barcode, numItems, seqNum);
                if (err != null)
                {
                    log.Write("Skriv " + barcode + " FEJLEDE: " + err);
                    Publish();
                    return new CiceroServer.Result(400, "Error writing tag: " + err);
                }
                log.Write("Skrevet: " + barcode + " til " + t.Mac);
                Publish();
                return new CiceroServer.Result(200, t.ToJson(), true);
            });
        }

        /// <summary>Skriver materialenummer m.m. og kontrollerer det. Returnerer null, når det lykkedes, ellers en fejltekst.</summary>
        private string WriteContent(TagState t, string barcode, int numItems, int seqNum)
        {
            // Et tag, der allerede tilhører et bibliotek, beholder det; nye chips får det indstillede nummer
            string library = t.Content != null && t.Content.Library.Length > 0 ? t.Content.Library
                : !string.IsNullOrEmpty(LibraryForNewChips) ? LibraryForNewChips
                : lastLibrary;
            if (string.IsNullOrEmpty(library)) return "biblioteksnummer er ikke sat";
            string country = t.Content != null && t.Content.Country.Length > 0 ? t.Content.Country : TagContent.DefaultCountry;
            byte[] data;
            try { data = TagContent.Build(barcode, numItems, seqNum, country, library); }
            catch (ArgumentException e) { return e.Message; }
            string err;
            if (!reader.WriteDanish(t.Uid, data, out err))
            {
                Count("WriteTagFail");
                return err;
            }
            Count("WriteTagSucc");
            t.Raw = data;
            t.Content = TagContent.Parse(data);
            return null;
        }

        /// <summary>
        /// Cicero er begyndt at lytte (fx skiftet til Aflevering). En rigtig læser melder de bøger,
        /// der allerede ligger på den - det gør vi også.
        /// </summary>
        public void OnClientConnected()
        {
            Task.Delay(200).ContinueWith(_ =>
            {
                if (!running) return;
                jobs.Add(() =>
                {
                    if (tags.Count == 0) return;
                    foreach (TagState t in tags.Values) Broadcast("addTag", t);
                    log.Write("Cicero lytter nu - sendt det, der allerede lå på læseren: " + string.Join(", ", tags.Values.Select(x => x.Label)));
                });
            });
        }

        // ---------- Værktøj til tags (fra brugerfladen) ----------

        public sealed class ToolResult
        {
            public bool Ok;
            public bool NeedsConfirm;
            public string Message;
            public static ToolResult Good(string m) { return new ToolResult { Ok = true, Message = m }; }
            public static ToolResult Bad(string m) { return new ToolResult { Ok = false, Message = m }; }
        }

        private TagState Find(string mac)
        {
            TagState t;
            if (mac != null && tags.TryGetValue(mac.ToUpperInvariant(), out t)) return t;
            return null;
        }

        /// <summary>Indholdet er ændret af værktøjet: Cicero skal se det nye (som når et tag fjernes og lægges på igen).</summary>
        private void Changed(TagState t, string oldJson)
        {
            CiceroServer srv = Server;
            if (srv != null && srv.ClientCount > 0)
            {
                srv.Broadcast("removeTag", oldJson);
                srv.Broadcast("addTag", t.ToJson());
            }
            Publish();
        }

        public ToolResult ToolAlarm(string mac, bool on)
        {
            return OnWorker(() =>
            {
                TagState t = Find(mac);
                if (t == null) return ToolResult.Bad("Tagget ligger ikke på læseren længere");
                string err;
                if (!reader.WriteAfi(t.Uid, on ? TagState.AFI_ON : TagState.AFI_OFF, out err))
                {
                    Count("WriteAFIFail");
                    Publish();
                    return ToolResult.Bad("Alarmen kunne ikke ændres: " + err);
                }
                Count("WriteAFISucc");
                t.Afi = on ? TagState.AFI_ON : TagState.AFI_OFF;
                log.Write("Værktøj: alarm " + (on ? "TIL" : "FRA") + " på " + t.Label);
                Publish();
                return ToolResult.Good(on ? "Alarm til ✓" : "Alarm fra ✓");
            });
        }

        public ToolResult ToolReset(string mac)
        {
            return OnWorker(() =>
            {
                TagState t = Find(mac);
                if (t == null) return ToolResult.Bad("Tagget ligger ikke på læseren længere");
                string old = t.ToJson(), name = t.Label, err;
                byte[] zero = new byte[32];
                if (!reader.WriteDanish(t.Uid, zero, out err))
                {
                    Count("WriteTagFail");
                    return ToolResult.Bad("Chippen kunne ikke nulstilles: " + err);
                }
                Count("WriteTagSucc");
                t.Raw = zero;
                t.Content = TagContent.Parse(zero);
                if (!reader.WriteAfi(t.Uid, 0x00, out err))
                {
                    Changed(t, old);
                    return ToolResult.Bad("Indholdet er slettet, men alarmen kunne ikke nulstilles: " + err);
                }
                t.Afi = 0x00;
                log.Write("Værktøj: nulstillet tag (var " + name + ")");
                Changed(t, old);
                return ToolResult.Good("Nulstillet ✓ - chippen er tom");
            });
        }

        /// <summary>Programmerer en chip og slår alarmen til (som NFClibra). Kræver bekræftelse, hvis chippen har andet indhold.</summary>
        public ToolResult ToolProgram(string mac, string barcode, int seq, int num, string library, bool confirmed)
        {
            return OnWorker(() =>
            {
                TagState t = Find(mac);
                if (t == null) return ToolResult.Bad("Chippen ligger ikke på læseren længere");
                if (string.IsNullOrEmpty(library)) return ToolResult.Bad("Biblioteksnummeret er ikke sat (Indstillinger)");
                if (num < 1 || num > 99 || seq < 1 || seq > num) return ToolResult.Bad("Del " + seq + " af " + num + " giver ikke mening");
                byte[] data;
                try { data = TagContent.Build(barcode, num, seq, TagContent.DefaultCountry, library); }
                catch (ArgumentException e) { return ToolResult.Bad(e.Message); }

                bool same = t.Raw != null && t.Raw.SequenceEqual(data);
                if (t.Kind != "blank" && !same && !confirmed)
                    return new ToolResult { NeedsConfirm = true, Message = t.Kind == "danish" && t.Content.Barcode.Length > 0 ? "materialenummer " + t.Content.Barcode : "andet indhold" };

                string old = t.ToJson(), err;
                if (!reader.WriteDanish(t.Uid, data, out err))
                {
                    Count("WriteTagFail");
                    return ToolResult.Bad("Chippen kunne ikke programmeres: " + err);
                }
                Count("WriteTagSucc");
                t.Raw = data;
                t.Content = TagContent.Parse(data);
                if (reader.WriteAfi(t.Uid, TagState.AFI_ON, out err)) t.Afi = TagState.AFI_ON;
                log.Write("Værktøj: programmeret " + barcode + " del " + seq + "/" + num + " (" + TagContent.DefaultCountry + "-" + library + ")" + (err != null && t.Afi != TagState.AFI_ON ? " - men alarmen kunne ikke slås til: " + err : ""));
                Changed(t, old);
                if (t.Afi != TagState.AFI_ON) return ToolResult.Bad("Programmeret, men alarmen kunne ikke slås til: " + err);
                return ToolResult.Good("Programmeret ✓");
            });
        }

        public void SetLogRaw(bool on)
        {
            LogRaw = on;
        }

        // ---------- Selvtest ----------

        /// <summary>
        /// Afprøver, om PR101 taber færre svar, når der holdes pause mellem kommandoerne.
        /// For hver pause køres 15 "bøger" som ved skranken: alarmstatus + læsning af blok 0-7.
        /// </summary>
        private void SelfTest(List<TagState> seen)
        {
            log.Write("===== SELVTEST =====");
            log.Write("Læser: " + reader.DeviceName + ", familie " + reader.Family + ", ID " + reader.DeviceId
                + ", firmware " + reader.Firmware + ", " + (reader.Advanced ? "Advanced" : "Standard") + "-rammer, adresse 0x" + reader.Address.ToString("X2"));
            log.Write("feusb.dll: " + usb.Path + " (version " + usb.DllVersion() + "), ventetid " + reader.TimeoutMs + " ms (inventory " + reader.InventoryTimeoutMs + " ms), pause " + reader.GapMs + " ms");
            if (seen.Count == 0)
            {
                log.Write("Ingen tags på læseren - læg en bog på og tryk igen.");
                log.Write("===== SELVTEST SLUT =====");
                return;
            }
            TagState s = seen[0];
            log.Write("Tester på tag " + s.Mac + (seen.Count > 1 ? " (der ligger " + seen.Count + " tags - kun det første testes)" : ""));

            int keepGap = reader.GapMs;
            bool keepRaw = reader.LogRaw;
            reader.LogRaw = false;
            List<string> summary = new List<string>();
            const int books = 15;
            try
            {
                foreach (int gap in new[] { 0, 5, 20, 50 })
                {
                    if (!reader.IsOpen) { summary.Add("Forbindelsen er tabt - testen stoppede her."); break; }
                    reader.GapMs = gap;
                    log.Write("--- pause " + gap + " ms ---");
                    int t0 = reader.Transfers, to0 = reader.Timeouts, g0 = reader.GaveUp;
                    int ok = 0;
                    long sum = 0, max = 0;
                    for (int i = 0; i < books && reader.IsOpen; i++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        string m;
                        bool good = reader.ReadAfi(s.Uid) != null && reader.ReadDanish(s.Uid, out m) != null;
                        long ms = sw.ElapsedMilliseconds;
                        if (good) ok++;
                        sum += ms;
                        if (ms > max) max = ms;
                    }
                    int calls = reader.Transfers - t0, lost = reader.Timeouts - to0;
                    summary.Add("pause " + gap.ToString().PadLeft(2) + " ms: " + ok + " af " + books + " bøger OK, tabte svar " + lost + " af " + calls
                        + " (" + (calls > 0 ? (100.0 * lost / calls).ToString("0") : "0") + " %), opgivet " + (reader.GaveUp - g0)
                        + ", pr. bog i snit " + (sum / books) + " ms, længst " + max + " ms");
                }
            }
            finally
            {
                reader.GapMs = keepGap;
                reader.LogRaw = keepRaw;
            }
            log.Write("----- RESULTAT -----");
            foreach (string line in summary) log.Write(line);
            log.Write("Siden start: " + reader.Stats);
            log.Write("===== SELVTEST SLUT =====");
        }
    }
}
