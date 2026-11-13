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
            Action<string> h = Status;
            if (h != null) h(s);
        }

        private void Publish()
        {
            Action<List<TagView>> h = TagsChanged;
            if (h == null) return;
            List<TagView> list = new List<TagView>();
            foreach (TagState t in tags.Values) list.Add(View(t, ""));
            foreach (TagState t in pending.Values) list.Add(View(t, "kunne ikke læses endnu (" + t.ReadFails + " forsøg)"));
            h(list);
        }

        private static TagView View(TagState t, string note)
        {
            TagView v = new TagView { Uid = t.Mac, Alarm = t.AlarmText, Note = note };
            if (t.Content != null)
            {
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
                            SetStatus("Ingen læser - prøver igen om lidt");
                            RunJobs(3000);
                            continue;
                        }
                        baseStatus = "Læser: " + reader.DeviceName + ", firmware " + reader.Firmware;
                        SetStatus(baseStatus);
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
                        SetStatus(baseStatus + "  |  " + reader.Stats);
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
                sb.Append(",\"Name\":").Append(Json.Str((reader.DeviceName ?? "FEIG") + " (RFIDlibra " + MainForm.Version + ")"));
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
