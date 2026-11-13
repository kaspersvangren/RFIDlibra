using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
        public string Method = "";
        public string Note = "";
    }

    /// <summary>
    /// Arbejdstråden: al kontakt med læseren sker herfra. Spørger læseren hvert kvarte sekund,
    /// hvilke tags der ligger på den, og læser indholdet af nye tags.
    /// </summary>
    public sealed class Worker
    {
        private readonly Feusb usb;
        private readonly Log log;
        private Reader reader;
        private Thread thread;
        private volatile bool running;
        private volatile bool selfTestRequested;
        private readonly Dictionary<string, TagView> tags = new Dictionary<string, TagView>();
        private readonly Dictionary<string, int> failedReads = new Dictionary<string, int>();
        private string lastRaw;
        private string baseStatus = "";
        private DateTime lastStats = DateTime.MinValue;

        public event Action<string> Status;
        public event Action<List<TagView>> TagsChanged;

        public volatile bool LogRaw = true;

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
            if (h != null) h(tags.Values.ToList());
        }

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
                            Sleep(3000);
                            continue;
                        }
                        baseStatus = "Forbundet: " + reader.DeviceName + ", firmware " + reader.Firmware
                            + (reader.Advanced ? " (Advanced)" : " (Standard)");
                        SetStatus(baseStatus);
                        tags.Clear();
                        lastRaw = null;
                        Publish();
                    }

                    string raw;
                    List<Reader.Seen> seen = reader.Inventory(out raw);
                    if (seen == null)
                    {
                        if (raw != null && raw != lastRaw) log.Write("Inventory gav fejl: " + raw);
                        lastRaw = raw;
                        Sleep(500);
                        continue;
                    }
                    if (raw != lastRaw && LogRaw) log.Write("Inventory: " + raw);
                    lastRaw = raw;

                    bool changed = false;
                    HashSet<string> present = new HashSet<string>(seen.Select(s => s.Key));
                    foreach (string gone in tags.Keys.Where(k => !present.Contains(k)).ToList())
                    {
                        log.Write("Tag fjernet: " + gone);
                        tags.Remove(gone);
                        failedReads.Remove(gone);
                        changed = true;
                    }
                    foreach (string k in failedReads.Keys.Where(k => !present.Contains(k)).ToList()) failedReads.Remove(k);
                    foreach (Reader.Seen s in seen)
                    {
                        if (tags.ContainsKey(s.Key)) continue;
                        int fails;
                        failedReads.TryGetValue(s.Key, out fails);
                        if (fails == 0)
                            log.Write("Nyt tag: " + s.Key + " (TR-TYPE 0x" + s.TrType.ToString("X2") + ", DSFID 0x" + s.Dsfid.ToString("X2") + ")");
                        TagView v = ReadTag(s);
                        if (v.Barcode.Length > 0 || fails >= 4)
                        {
                            tags[s.Key] = v;
                            failedReads.Remove(s.Key);
                            changed = true;
                        }
                        else
                        {
                            failedReads[s.Key] = fails + 1;
                            log.Write("Tag " + s.Key + ": prøver igen i næste runde (" + (fails + 1) + ". gang).");
                        }
                        if (!reader.IsOpen) break;
                    }
                    if (changed) Publish();

                    if ((DateTime.Now - lastStats).TotalSeconds >= 2)
                    {
                        lastStats = DateTime.Now;
                        SetStatus(baseStatus + "  |  " + reader.Stats);
                    }

                    if (selfTestRequested)
                    {
                        selfTestRequested = false;
                        SelfTest(seen);
                    }
                    Sleep(250);
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
            }
        }

        private TagView ReadTag(Reader.Seen s)
        {
            TagView v = new TagView { Uid = s.Key };
            int? afi = reader.ReadAfi(s.Uid);
            v.Alarm = afi == null ? "?" : afi == 0x07 ? "TIL (0x07)" : afi == 0xC2 ? "FRA (0xC2)" : "0x" + afi.Value.ToString("X2");

            string method;
            byte[] data = reader.ReadDanish(s.Uid, out method);
            if (data == null)
            {
                v.Note = "kunne ikke læse indholdet";
                log.Write("Tag " + s.Key + ": indholdet kunne ikke læses.");
                return v;
            }
            v.Method = method;
            log.Write("Tag " + s.Key + ": 32 bytes = " + Protocol.Hex(data));
            TagContent c = TagContent.Parse(data);
            if (c == null) { v.Note = "for få data"; return v; }
            v.Barcode = c.Barcode;
            v.Part = c.Part;
            v.Library = (c.Country + "-" + c.Library).Trim('-');
            List<string> notes = new List<string>();
            if (c.Version != 0x11) notes.Add("version 0x" + c.Version.ToString("X2"));
            notes.Add(c.CrcOk ? "CRC ok" : "CRC passer ikke");
            v.Note = string.Join(", ", notes);
            log.Write("Tag " + s.Key + ": materialenummer " + c.Barcode + ", del " + c.Part + ", " + v.Library
                + ", alarm " + v.Alarm + ", læst med " + method + ", " + v.Note);
            return v;
        }

        /// <summary>
        /// Afprøver, om PR101 taber færre svar, når der holdes pause mellem kommandoerne.
        /// For hver pause køres 15 "bøger" som ved skranken: alarmstatus + læsning af blok 0-7.
        /// </summary>
        private void SelfTest(List<Reader.Seen> seen)
        {
            log.Write("===== SELVTEST =====");
            log.Write("Læser: " + reader.DeviceName + ", familie " + reader.Family + ", ID " + reader.DeviceId
                + ", firmware " + reader.Firmware + ", " + (reader.Advanced ? "Advanced" : "Standard") + "-rammer, adresse 0x" + reader.Address.ToString("X2"));
            log.Write("feusb.dll: " + usb.Path + " (version " + usb.DllVersion() + "), ventetid " + reader.TimeoutMs + " ms (inventory " + reader.InventoryTimeoutMs + " ms)");
            if (seen.Count == 0)
            {
                log.Write("Ingen tags på læseren - læg en bog på og tryk igen.");
                log.Write("===== SELVTEST SLUT =====");
                return;
            }
            Reader.Seen s = seen[0];
            log.Write("Tester på tag " + s.Key + (seen.Count > 1 ? " (der ligger " + seen.Count + " tags - kun det første testes)" : ""));

            int keepGap = reader.GapMs;
            bool keepRaw = reader.LogRaw;
            reader.LogRaw = false; // kun tabte svar og redninger logges under testen
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
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
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

        private void Sleep(int ms)
        {
            for (int i = 0; i < ms && running; i += 50) Thread.Sleep(50);
        }
    }
}
