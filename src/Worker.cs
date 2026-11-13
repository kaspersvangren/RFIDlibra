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
        private string lastRaw;

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
                        SetStatus("Forbundet: " + reader.DeviceName + ", firmware " + reader.Firmware
                            + (reader.Advanced ? " (Advanced)" : " (Standard)"));
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
                        changed = true;
                    }
                    foreach (Reader.Seen s in seen)
                    {
                        if (tags.ContainsKey(s.Key)) continue;
                        log.Write("Nyt tag: " + s.Key + " (TR-TYPE 0x" + s.TrType.ToString("X2") + ", DSFID 0x" + s.Dsfid.ToString("X2") + ")");
                        tags[s.Key] = ReadTag(s);
                        changed = true;
                        if (!reader.IsOpen) break;
                    }
                    if (changed) Publish();

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

        /// <summary>Prøver alle læsemåder på de tags, der ligger på læseren, og skriver resultatet i loggen.</summary>
        private void SelfTest(List<Reader.Seen> seen)
        {
            log.Write("===== SELVTEST =====");
            log.Write("Læser: " + reader.DeviceName + ", familie " + reader.Family + ", ID " + reader.DeviceId
                + ", firmware " + reader.Firmware + ", " + (reader.Advanced ? "Advanced" : "Standard") + "-rammer, adresse 0x" + reader.Address.ToString("X2"));
            log.Write("feusb.dll: " + usb.Path + " (version " + usb.DllVersion() + ")");
            if (seen.Count == 0)
            {
                log.Write("Ingen tags på læseren - læg en bog på og tryk igen.");
                log.Write("===== SELVTEST SLUT =====");
                return;
            }
            Reader.Seen s = seen[0];
            log.Write("Tester på tag " + s.Key + (seen.Count > 1 ? " (der ligger " + seen.Count + " tags - kun det første testes)" : ""));
            int? afi = reader.ReadAfi(s.Uid);
            log.Write("Resultat systeminfo/AFI: " + (afi == null ? "FEJL" : "OK, AFI 0x" + afi.Value.ToString("X2")));
            foreach (int n in new[] { 1, 2, 4, 8, 9, 16, 28 })
            {
                if (!reader.IsOpen) { log.Write("Forbindelsen er tabt - stopper testen."); break; }
                byte[] d = reader.ReadBlocksOnce(s.Uid, 0, n);
                log.Write("Resultat " + n + " blok(ke) på én gang: " + (d == null ? "FEJL" : "OK"));
                if (d == null && !reader.IsOpen) break;
            }
            log.Write("===== SELVTEST SLUT =====");
        }

        private void Sleep(int ms)
        {
            for (int i = 0; i < ms && running; i += 50) Thread.Sleep(50);
        }
    }
}
