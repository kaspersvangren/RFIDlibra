using System;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RFIDlibra
{
    /// <summary>
    /// Snakker med én FEIG-læser via feusb.dll. Bruges kun fra én tråd (arbejdstråden i Worker).
    /// PR101 med firmware 1.7 taber af og til et svar (USB-timeout) - også med Bibliothecas gamle software.
    /// Derfor kort ventetid og automatisk nyt forsøg i Send.
    /// </summary>
    public sealed class Reader
    {
        private readonly Feusb usb;
        private readonly Log log;
        private int handle;

        public bool IsOpen { get { return handle > 0; } }
        public bool Advanced { get; private set; }
        public byte Address { get; private set; }
        public string DeviceName { get; private set; }
        public string DeviceId { get; private set; }
        public string Family { get; private set; }
        public string Firmware { get; private set; }

        public bool LogRaw = true;

        /// <summary>Tællere siden programmet startede: antal kommandoer, timeouts og andre USB-fejl.</summary>
        public int Transfers, Timeouts, OtherErrors;

        public string Stats
        {
            get
            {
                return Transfers + " USB-kald, " + Timeouts + " tabte svar"
                    + (Transfers > 0 ? " (" + (100.0 * Timeouts / Transfers).ToString("0.0") + " %)" : "")
                    + "; reddet " + RescuedSameHandle + " straks + " + RescuedReopen + " efter genåbning, opgivet " + GaveUp
                    + (OtherErrors > 0 ? ", " + OtherErrors + " andre USB-fejl" : "");
            }
        }

        public Reader(Feusb usb, Log log)
        {
            this.usb = usb;
            this.log = log;
            Address = 0xFF;
        }

        public void Close()
        {
            if (handle > 0)
            {
                try { usb.Close(handle); } catch (Exception e) { log.Write("Fejl ved lukning: " + e.Message); }
            }
            handle = 0;
        }

        /// <summary>Finder og åbner læseren og finder ud af, hvilket rammeformat den taler.</summary>
        public bool Open()
        {
            if (minute.Count > 0) WriteMinuteStats();   // statistik for den læser, der lige er forsvundet
            Close();
            int h = usb.ScanAndOpenFirst();
            if (h <= 0)
            {
                log.Write("Ingen læser åbnet: " + usb.ErrorText(h));
                return false;
            }
            handle = h;
            appliedTimeout = -1;
            FirmwareMajor = 0;
            DeviceName = usb.ScanListPara("DeviceName");
            Family = usb.ScanListPara("FamilyName");
            DeviceId = usb.ScanListPara("Device-ID");
            log.Write("Læser fundet: " + DeviceName + " (familie " + Family + ", ID " + DeviceId + "), ventetid " + TimeoutMs + "/" + InventoryTimeoutMs + " ms");

            // Prøv rammeformater og busadresser, til læseren svarer på "hvilken softwareversion har du?"
            bool[] formats = { false, true };
            byte[] addresses = { 0xFF, 0x00 };
            foreach (byte adr in addresses)
            {
                foreach (bool adv in formats)
                {
                    Advanced = adv;
                    Address = adr;
                    Protocol.Response r = Send(Protocol.CMD_SW_VERSION, null, "softwareversion");
                    if (r != null && r.Status == 0x00 && r.Data.Length >= 2)
                    {
                        Firmware = r.Data[0] + "." + r.Data[1];
                        FirmwareMajor = r.Data[0];
                        log.Write("Læseren svarer: " + (adv ? "Advanced" : "Standard") + "-rammer, adresse 0x" + adr.ToString("X2")
                            + ", firmware " + Firmware + " (data: " + Protocol.Hex(r.Data) + ")");
                        return true;
                    }
                    if (!IsOpen) return false;
                }
            }
            log.Write("Læseren svarede ikke på nogen af forsøgene. Send loggen til Claude.");
            Close();
            return false;
        }

        /// <summary>
        /// Hvor længe feusb venter på svar (ms). PR101 svarer på 12-60 ms; inventory med tags tager ~200 ms.
        /// </summary>
        public int TimeoutMs = 150;
        public int InventoryTimeoutMs = 300;   // inventory tager 110-200 ms
        public int WriteTimeoutMs = 500;       // skriv blok 0-7 tager ~110 ms; alarm (AFI) svarer på ~16 ms og bruger TimeoutMs

        /// <summary>Mindste pause (ms) fra et svar til næste kommando sendes. Afprøves i selvtesten.</summary>
        public int GapMs = 5;

        /// <summary>Hvordan tabte svar blev reddet: ved nyt forsøg på samme forbindelse, efter genåbning, eller slet ikke.</summary>
        public int RescuedSameHandle, RescuedReopen, GaveUp;

        /// <summary>Samlet tid for seneste Send, inklusive eventuelle nye forsøg.</summary>
        public long LastMs;

        private int appliedTimeout = -1;
        private long lastInventoryMs;
        private readonly Stopwatch sinceLast = Stopwatch.StartNew();

        private void ApplyTimeout(int ms)
        {
            if (ms == appliedTimeout) return;
            int r = usb.SetTimeout(handle, ms);
            if (r < 0) log.Write("  Kunne ikke sætte ventetid til " + ms + " ms: " + usb.ErrorText(r));
            else appliedTimeout = ms;
        }

        private const int SameHandleTries = 3;
        private const int AfterReopenTries = 2;

        /// <summary>
        /// Sender én kommando. PR101 med firmware 1.7 taber af og til et svar på USB; så prøves der
        /// op til 3 gange på samme forbindelse, derefter genåbnes forbindelsen og der prøves 2 gange til.
        /// Returnerer null ved fejl eller uforståeligt svar.
        /// </summary>
        public Protocol.Response Send(byte cmd, byte[] data, string what)
        {
            return Send(cmd, data, what, 0, SameHandleTries, AfterReopenTries);
        }

        /// <summary>Som Send, men med egen ventetid (0 = standard) og eget antal forsøg.</summary>
        public Protocol.Response Send(byte cmd, byte[] data, string what, int timeoutMs, int sameTries, int reopenTries)
        {
            if (!IsOpen) return null;
            if (cmd == 0x81) throw new InvalidOperationException("Læserens konfiguration ændres aldrig");
            if (cmd == Protocol.CMD_ISO && (data == null || data.Length == 0 || Array.IndexOf(Protocol.AllowedIso, data[0]) < 0))
                throw new InvalidOperationException("ISO-kommando er ikke tilladt");
            byte iso = cmd == Protocol.CMD_ISO ? data[0] : (byte)0;
            // Inventory tager længere tid, jo flere tags der ligger på læseren: ventetiden følger med (mindst 300 ms)
            int timeout = timeoutMs > 0 ? timeoutMs
                : iso == Protocol.ISO_INVENTORY ? (int)Math.Min(2000, Math.Max(InventoryTimeoutMs, lastInventoryMs * 2))
                : iso == Protocol.ISO_WRITE_BLOCKS ? WriteTimeoutMs
                : TimeoutMs;
            string kind = Kind(cmd, iso);
            byte[] frame = Protocol.Build(Advanced, Address, cmd, data);
            Stopwatch total = Stopwatch.StartNew();
            int maxTries = sameTries + reopenTries;
            for (int attempt = 1; attempt <= maxTries; attempt++)
            {
                if (!IsOpen) break;
                ApplyTimeout(timeout);
                long wait = GapMs - sinceLast.ElapsedMilliseconds;
                if (wait > 0) System.Threading.Thread.Sleep((int)wait);

                Stopwatch sw = Stopwatch.StartNew();
                int err;
                byte[] rec = usb.Transceive(handle, frame, out err);
                long ms = sw.ElapsedMilliseconds;
                sinceLast.Restart();
                Transfers++;
                if (rec != null)
                {
                    Protocol.Response r = Protocol.Parse(rec);
                    if (LogRaw || attempt > 1)
                        log.Write("  " + what + ": SENDT " + Protocol.Hex(frame) + "  SVAR " + Protocol.Hex(rec) + "  (" + ms + " ms)");
                    if (r == null || r.Cmd != cmd)
                    {
                        log.Write("  " + what + ": " + (r == null ? "svaret kunne ikke fortolkes" : "svar på en anden kommando (0x" + r.Cmd.ToString("X2") + ")") + " - rydder op.");
                        Reopen();
                        continue;
                    }
                    if (!r.CrcOk) log.Write("  " + what + ": advarsel - CRC i svaret passer ikke.");
                    if (iso == Protocol.ISO_INVENTORY) lastInventoryMs = ms;
                    Count(kind, attempt, ms, false);
                    if (attempt > 1)
                    {
                        if (attempt <= sameTries) RescuedSameHandle++; else RescuedReopen++;
                    }
                    LastMs = total.ElapsedMilliseconds;
                    if (attempt > 1) log.Write("  " + what + ": reddet ved forsøg " + attempt + " (i alt " + LastMs + " ms)");
                    return r;
                }

                if (LogRaw || attempt > 1 || err != Feusb.ERR_TIMEOUT)
                    log.Write("  " + what + ": SENDT " + Protocol.Hex(frame) + "  -> USB-FEJL " + usb.ErrorText(err) + " efter " + ms + " ms (forsøg " + attempt + ")");
                else
                    log.Write("  " + what + ": tabt svar efter " + ms + " ms (forsøg 1)");
                if (err != Feusb.ERR_TIMEOUT)
                {
                    OtherErrors++;
                    log.Write("  Forbindelsen til læseren er tabt.");
                    Close();
                    break;
                }
                Timeouts++;
                // Mange tags på læseren kan gøre inventory langsommere end ventetiden: giv næste forsøg længere tid
                if (iso == Protocol.ISO_INVENTORY) timeout = Math.Min(2000, timeout * 2);
                if (attempt == sameTries) Reopen();
            }
            GaveUp++;
            Count(kind, maxTries, 0, true);
            LastMs = total.ElapsedMilliseconds;
            log.Write("  " + what + ": opgivet (i alt " + LastMs + " ms)");
            return null;
        }

        // ---------- Statistik pr. slags kommando (skrives i loggen hvert minut) ----------

        private sealed class KindStats { public int Calls, LostFirst, Retries, GaveUp; public long Ms; public int Ok; public long MaxMs; }
        private readonly Dictionary<string, KindStats> minute = new Dictionary<string, KindStats>();
        private readonly Stopwatch minuteClock = Stopwatch.StartNew();

        private static string Kind(byte cmd, byte iso)
        {
            if (cmd == Protocol.CMD_ISO)
                switch (iso)
                {
                    case Protocol.ISO_INVENTORY: return "inventory";
                    case Protocol.ISO_READ_BLOCKS: return "læs blokke";
                    case Protocol.ISO_WRITE_BLOCKS: return "skriv blokke";
                    case Protocol.ISO_WRITE_AFI: return "skriv alarm";
                    case Protocol.ISO_SYSINFO: return "systeminfo";
                }
            if (cmd == Protocol.CMD_SW_VERSION) return "softwareversion";
            if (cmd == Protocol.CMD_READ_CFG) return "læs konfiguration";
            return "kommando 0x" + cmd.ToString("X2");
        }

        private void Count(string kind, int attempt, long ms, bool gaveUp)
        {
            KindStats k;
            if (!minute.TryGetValue(kind, out k)) minute[kind] = k = new KindStats();
            k.Calls++;
            if (attempt > 1) { k.LostFirst++; k.Retries += attempt - 1; }
            if (gaveUp) k.GaveUp++;
            else { k.Ok++; k.Ms += ms; if (ms > k.MaxMs) k.MaxMs = ms; }
            if (minuteClock.ElapsedMilliseconds >= 60000) WriteMinuteStats();
        }

        /// <summary>Skriver sidste minuts statistik i loggen, så læsere kan sammenlignes.</summary>
        public void WriteMinuteStats()
        {
            minuteClock.Restart();
            if (minute.Count == 0) return;
            StringBuilder sb = new StringBuilder("Statistik sidste minut (" + DeviceName + ", firmware " + Firmware + "): ");
            bool first = true;
            foreach (KeyValuePair<string, KindStats> e in minute)
            {
                KindStats k = e.Value;
                if (!first) sb.Append("; ");
                first = false;
                sb.Append(e.Key).Append(' ').Append(k.Calls).Append(" kald, ")
                  .Append((100.0 * k.LostFirst / k.Calls).ToString("0")).Append(" % tabt første gang");
                if (k.GaveUp > 0) sb.Append(", ").Append(k.GaveUp).Append(" opgivet");
                if (k.Ok > 0) sb.Append(", svar ").Append(k.Ms / k.Ok).Append(" ms i snit (højst ").Append(k.MaxMs).Append(")");
            }
            log.Write(sb.ToString());
            minute.Clear();
        }

        private static readonly HashSet<string> configLogged = new HashSet<string>();

        /// <summary>
        /// Skriver læserens indstillinger i loggen (én gang pr. læser pr. kørsel), så forskellige læsere kan sammenlignes.
        /// Bruger kun "læs konfiguration" (0x80) - der ændres intet.
        /// </summary>
        public void LogConfiguration()
        {
            if (!IsOpen || configLogged.Contains(DeviceId ?? "")) return;
            configLogged.Add(DeviceId ?? "");
            log.Write("Læserens indstillinger (kun læst, intet ændres) for " + DeviceName + ", firmware " + Firmware + ":");
            for (int blk = 0; blk < 16 && IsOpen; blk++)
            {
                Protocol.Response r = Send(Protocol.CMD_READ_CFG, new byte[] { (byte)blk }, "læs konfiguration " + blk);
                if (r == null) log.Write("  konfiguration " + blk + ": intet svar");
                else if (r.Status != 0x00) log.Write("  konfiguration " + blk + ": " + Protocol.StatusText(r.Status));
                else log.Write("  konfiguration " + blk + ": " + Protocol.Hex(r.Data));
            }
        }

        /// <summary>Genåbner USB-forbindelsen (rydder op efter et tabt svar).</summary>
        private void Reopen()
        {
            bool adv = Advanced;
            byte adr = Address;
            Close();
            System.Threading.Thread.Sleep(50);
            int h = usb.ScanAndOpenFirst();
            if (h > 0)
            {
                handle = h;
                Advanced = adv;
                Address = adr;
                appliedTimeout = -1;
                log.Write("  Forbindelsen er genåbnet.");
            }
            else
            {
                log.Write("  Kunne ikke genåbne: " + usb.ErrorText(h));
            }
        }

        public sealed class Seen
        {
            public byte TrType;
            public byte Dsfid;
            public byte[] Uid;
            public string Key { get { return Protocol.Uid(Uid); } }
        }

        /// <summary>Hvilke tags ligger på læseren lige nu? null betyder fejl (ikke "ingen tags").</summary>
        public List<Seen> Inventory(out string raw)
        {
            raw = null;
            bool keep = LogRaw;
            LogRaw = false; // inventory kører hele tiden - rå data logges kun, når resultatet ændrer sig
            Protocol.Response r;
            try { r = Send(Protocol.CMD_ISO, new byte[] { Protocol.ISO_INVENTORY, 0x00 }, "inventory"); }
            finally { LogRaw = keep; }
            if (r == null) return null;
            raw = Protocol.StatusText(r.Status) + " / " + Protocol.Hex(r.Data);
            List<Seen> list = new List<Seen>();
            if (r.Status == 0x01) return list;
            if (r.Status != 0x00 && r.Status != 0x94 && r.Status != 0x83 && r.Status != 0x84) return null;
            if (r.Data.Length < 1) return list;
            int n = r.Data[0];
            if (r.Data.Length < 1 + 10 * n)
            {
                log.Write("  inventory: uventet svarlængde (" + n + " tags, " + r.Data.Length + " bytes) - ignoreres.");
                return null;
            }
            int p = 1;
            for (int i = 0; i < n && p + 10 <= r.Data.Length; i++, p += 10)
            {
                Seen s = new Seen { TrType = r.Data[p], Dsfid = r.Data[p + 1], Uid = new byte[8] };
                Array.Copy(r.Data, p + 2, s.Uid, 0, 8);
                list.Add(s);
            }
            return list;
        }

        private byte[] Addressed(byte isoCmd, byte[] uid, params byte[] extra)
        {
            byte[] d = new byte[2 + 8 + extra.Length];
            d[0] = isoCmd;
            d[1] = 0x01; // adresseret: kommandoen gælder kun tagget med denne UID
            Array.Copy(uid, 0, d, 2, 8);
            Array.Copy(extra, 0, d, 10, extra.Length);
            return d;
        }

        /// <summary>Læser AFI (alarm) via "Get System Information". null hvis det ikke lykkes.</summary>
        public int? ReadAfi(byte[] uid)
        {
            Protocol.Response r = Send(Protocol.CMD_ISO, Addressed(Protocol.ISO_SYSINFO, uid), "systeminfo/AFI");
            if (r == null) return null;
            if (r.Status != 0x00)
            {
                log.Write("  systeminfo: " + Protocol.StatusText(r.Status));
                return null;
            }
            // Svar: DSFID(1) UID(8) AFI(1) MEM-SIZE(2) IC-REF(1)
            if (r.Data.Length >= 10) return r.Data[9];
            return null;
        }

        /// <summary>
        /// Læser 'count' blokke fra 'first' med "Read Multiple Blocks" i ét hug.
        /// FEIG returnerer [DB-N][DB-SIZE] og for hver blok [sikkerhedsstatus][data], med blokkens bytes i omvendt rækkefølge.
        /// </summary>
        public byte[] ReadBlocksOnce(byte[] uid, int first, int count)
        {
            Protocol.Response r = Send(Protocol.CMD_ISO, Addressed(Protocol.ISO_READ_BLOCKS, uid, (byte)first, (byte)count),
                "læs " + count + " blok(ke) fra " + first);
            if (r == null) return null;
            if (r.Status != 0x00)
            {
                log.Write("  læsning: " + Protocol.StatusText(r.Status));
                return null;
            }
            if (r.Data.Length < 2) return null;
            int n = r.Data[0];
            int size = r.Data[1];
            if (size < 1 || n < count || r.Data.Length < 2 + n * (size + 1))
            {
                log.Write("  læsning: uventet svarlængde (DB-N " + n + ", DB-SIZE " + size + ", " + r.Data.Length + " bytes)");
                return null;
            }
            byte[] outp = new byte[count * size];
            for (int i = 0; i < count; i++)
            {
                int src = 2 + i * (size + 1) + 1;
                for (int k = 0; k < size; k++)
                    outp[i * size + k] = r.Data[src + size - 1 - k];
            }
            return outp;
        }

        /// <summary>Læser blok 0-7 (de 32 bytes i den danske datamodel) i ét hug. Antallet af blokke betyder intet for PR101's tabte svar.</summary>
        public byte[] ReadDanish(byte[] uid, out string method)
        {
            method = "8 blokke på én gang";
            byte[] all = ReadBlocksOnce(uid, 0, 8);
            return all != null && all.Length == 32 ? all : null;
        }

        /// <summary>Skriver AFI (alarm) og læser den igen for at kontrollere den. Prøver op til 3 gange.</summary>
        public bool WriteAfi(byte[] uid, int value, out string error)
        {
            error = null;
            for (int i = 1; i <= 3; i++)
            {
                Protocol.Response r = Send(Protocol.CMD_ISO, Addressed(Protocol.ISO_WRITE_AFI, uid, (byte)value), "skriv alarm 0x" + value.ToString("X2"));
                if (r != null && r.Status != 0x00) error = "læseren svarede " + Protocol.StatusText(r.Status);
                int? check = ReadAfi(uid);
                if (check == value) return true;
                if (check != null) error = "alarmen står stadig som 0x" + check.Value.ToString("X2");
                else if (error == null) error = "kunne ikke kontrollere alarmen";
                if (!IsOpen) break;
                log.Write("  Alarmen blev ikke bekræftet (" + error + ") - prøver igen.");
            }
            return false;
        }

        /// <summary>Ventetid og forsøg ved skrivning: længere ventetid og færre forsøg tæt efter hinanden, så kommandoer ikke hober sig op.</summary>
        public int WriteWaitMs = 1000;
        private const int WriteSameTries = 2, WriteReopenTries = 1;
        /// <summary>Én blok svarer på ca. 16 ms, så her kan der ventes kortere og prøves flere gange.</summary>
        public int BlockWaitMs = 300;
        private const int BlockSameTries = 3, BlockReopenTries = 1;

        /// <summary>Hovedversion af firmwaren (1 på PR101/MR101, 2 på MR102). 0 = ukendt.</summary>
        public int FirmwareMajor { get; private set; }
        /// <summary>Læsere, hvor skrivning af alle 8 blokke på én gang har slået fejl i denne kørsel.</summary>
        private readonly HashSet<string> multiBlockFails = new HashSet<string>();

        /// <summary>
        /// Firmware 1.x (PR101/MR101) når ikke at skrive 8 blokke i ét hug - den stopper efter blok 4 og svarer ikke.
        /// Så der skrives blok for blok med det samme. Det samme gælder en læser, hvor det er slået fejl før.
        /// </summary>
        private bool BlockByBlockOnly
        {
            get { return (FirmwareMajor > 0 && FirmwareMajor < 2) || multiBlockFails.Contains(DeviceId ?? ""); }
        }

        private static byte[] BlockData(byte[] data32, int first, int count)
        {
            byte[] extra = new byte[3 + count * 4];
            extra[0] = (byte)first;
            extra[1] = (byte)count;
            extra[2] = 0x04; // bytes pr. blok
            for (int blk = 0; blk < count; blk++)
                for (int k = 0; k < 4; k++)
                    extra[3 + blk * 4 + k] = data32[(first + blk) * 4 + 3 - k]; // FEIG vil have hver blok i omvendt rækkefølge
            return extra;
        }

        /// <summary>
        /// Skriver blok 0-7 (32 bytes) og læser dem igen for at kontrollere dem.
        /// På nyere læsere prøves alle 8 blokke på én gang (2 gange). Ellers - eller hvis det ikke lykkes - skrives der blok for blok,
        /// med blok 4 og 5 sidst: de indeholder kontrolsummen, så en chip, der kun er skrevet halvt, altid ses som ugyldig.
        /// </summary>
        public bool WriteDanish(byte[] uid, byte[] data32, out string error)
        {
            error = null;
            if (data32 == null || data32.Length != 32) { error = "forkert datalængde"; return false; }
            string m;
            bool multiTried = false;
            if (BlockByBlockOnly)
            {
                log.Write("  Skriver blok for blok (" + (FirmwareMajor > 0 && FirmwareMajor < 2 ? "firmware " + Firmware : "8 blokke på én gang slog fejl før på denne læser") + ").");
            }
            else
            {
                multiTried = true;
                for (int i = 1; i <= 2; i++)
                {
                    Protocol.Response r = Send(Protocol.CMD_ISO, Addressed(Protocol.ISO_WRITE_BLOCKS, uid, BlockData(data32, 0, 8)), "skriv blok 0-7",
                        WriteWaitMs, WriteSameTries, WriteReopenTries);
                    if (r != null && r.Status != 0x00) { error = "læseren svarede " + Protocol.StatusText(r.Status); log.Write("  skriv blok 0-7: " + error); }
                    byte[] check = ReadDanish(uid, out m);
                    if (check != null && BytesEqual(check, data32)) return true;
                    if (check != null) error = "kontrol-læsningen viste andre data";
                    else if (error == null) error = "kunne ikke kontrollere det skrevne";
                    if (!IsOpen) return false;
                    log.Write("  Skrivningen blev ikke bekræftet (" + error + ")" + (i < 2 ? " - prøver igen." : " - skriver nu blok for blok."));
                }
                error = null;
            }

            // Blok for blok, kontrolsummen (blok 4 og 5) sidst. Blokke, der allerede er rigtige, springes over.
            int[] order = { 0, 1, 2, 3, 6, 7, 4, 5 };
            for (int round = 1; round <= 2 && IsOpen; round++)
            {
                byte[] now = ReadDanish(uid, out m);
                int written = 0;
                foreach (int blk in order)
                {
                    if (!IsOpen) break;
                    bool same = now != null;
                    for (int k = 0; k < 4 && same; k++) same = now[blk * 4 + k] == data32[blk * 4 + k];
                    if (same) continue;
                    Protocol.Response r = Send(Protocol.CMD_ISO, Addressed(Protocol.ISO_WRITE_BLOCKS, uid, BlockData(data32, blk, 1)), "skriv blok " + blk,
                        BlockWaitMs, BlockSameTries, BlockReopenTries);
                    if (r == null) { error = "blok " + blk + " blev ikke skrevet"; break; }
                    if (r.Status != 0x00) { error = "blok " + blk + ": læseren svarede " + Protocol.StatusText(r.Status); log.Write("  " + error); break; }
                    written++;
                }
                byte[] check = ReadDanish(uid, out m);
                if (check != null && BytesEqual(check, data32))
                {
                    log.Write("  Skrevet blok for blok (" + written + " blokke) og bekræftet.");
                    error = null;
                    if (multiTried && DeviceId != null && multiBlockFails.Add(DeviceId))
                        log.Write("  Denne læser skriver herefter blok for blok med det samme.");
                    return true;
                }
                if (check != null) error = "kontrol-læsningen viste andre data";
                else if (error == null) error = "kunne ikke kontrollere det skrevne";
                log.Write("  Blok for blok blev ikke bekræftet (" + error + ")" + (round < 2 ? " - prøver igen." : "."));
            }
            error = (error ?? "skrivningen mislykkedes") + ". Chippen kan være delvist skrevet - den ses så som ugyldig. Programmér den igen.";
            return false;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
