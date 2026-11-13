using System;
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

        /// <summary>Største antal blokke, der er lykkedes at læse i ét hug (0 = ikke afprøvet endnu).</summary>
        public int BlocksPerRead = 8;

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
            Close();
            int h = usb.ScanAndOpenFirst();
            if (h <= 0)
            {
                log.Write("Ingen læser åbnet: " + usb.ErrorText(h));
                return false;
            }
            handle = h;
            ApplyTimeout();
            DeviceName = usb.ScanListPara("DeviceName");
            Family = usb.ScanListPara("FamilyName");
            DeviceId = usb.ScanListPara("Device-ID");
            log.Write("Læser fundet: " + DeviceName + " (familie " + Family + ", ID " + DeviceId + "), ventetid " + TimeoutMs + " ms");

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

        /// <summary>Hvor længe feusb venter på svar (ms). PR101 svarer normalt på 12-60 ms.</summary>
        public int TimeoutMs = 400;

        /// <summary>Hvordan tabte svar blev reddet: ved nyt forsøg på samme forbindelse, efter genåbning, eller slet ikke.</summary>
        public int RescuedSameHandle, RescuedReopen, GaveUp;

        /// <summary>Samlet tid for seneste Send, inklusive eventuelle nye forsøg.</summary>
        public long LastMs;

        private void ApplyTimeout()
        {
            int r = usb.SetTimeout(handle, TimeoutMs);
            if (r < 0) log.Write("  Kunne ikke sætte ventetid til " + TimeoutMs + " ms: " + usb.ErrorText(r));
        }

        /// <summary>
        /// Sender én kommando. PR101 med firmware 1.7 taber af og til et svar på USB; så prøves der
        /// først igen på samme forbindelse, derefter efter en genåbning (højst 3 forsøg i alt).
        /// Returnerer null ved fejl eller uforståeligt svar.
        /// </summary>
        public Protocol.Response Send(byte cmd, byte[] data, string what)
        {
            if (!IsOpen) return null;
            byte[] frame = Protocol.Build(Advanced, Address, cmd, data);
            Stopwatch total = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (!IsOpen) break;
                Stopwatch sw = Stopwatch.StartNew();
                int err;
                byte[] rec = usb.Transceive(handle, frame, out err);
                long ms = sw.ElapsedMilliseconds;
                Transfers++;
                if (rec != null)
                {
                    Protocol.Response r = Protocol.Parse(rec);
                    if (LogRaw || attempt > 1)
                        log.Write("  " + what + ": SENDT " + Protocol.Hex(frame) + "  SVAR " + Protocol.Hex(rec) + "  (" + ms + " ms)");
                    if (r == null)
                    {
                        log.Write("  " + what + ": svaret kunne ikke fortolkes - rydder op.");
                        Reopen();
                        continue;
                    }
                    if (r.Cmd != cmd)
                    {
                        log.Write("  " + what + ": svar på en anden kommando (0x" + r.Cmd.ToString("X2") + ") - rydder op.");
                        Reopen();
                        continue;
                    }
                    if (!r.CrcOk) log.Write("  " + what + ": advarsel - CRC i svaret passer ikke.");
                    if (attempt == 2) RescuedSameHandle++;
                    if (attempt == 3) RescuedReopen++;
                    LastMs = total.ElapsedMilliseconds;
                    if (attempt > 1) log.Write("  " + what + ": reddet ved forsøg " + attempt + " (i alt " + LastMs + " ms)");
                    return r;
                }

                log.Write("  " + what + ": SENDT " + Protocol.Hex(frame) + "  -> USB-FEJL " + usb.ErrorText(err) + " efter " + ms + " ms (forsøg " + attempt + ")");
                if (err != Feusb.ERR_TIMEOUT)
                {
                    OtherErrors++;
                    log.Write("  Forbindelsen til læseren er tabt.");
                    Close();
                    break;
                }
                Timeouts++;
                if (attempt == 2) Reopen();
            }
            GaveUp++;
            LastMs = total.ElapsedMilliseconds;
            log.Write("  " + what + ": opgivet (i alt " + LastMs + " ms)");
            return null;
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
                ApplyTimeout();
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

        /// <summary>
        /// Læser blok 0-7 (de 32 bytes i den danske datamodel). Starter med det antal blokke pr. kald,
        /// der virkede sidst, og halverer, hvis læseren ikke kan klare det.
        /// </summary>
        public byte[] ReadDanish(byte[] uid, out string method)
        {
            method = null;
            int[] tries = { 8, 4, 2, 1 };
            foreach (int chunk in tries.Where(c => c <= BlocksPerRead))
            {
                byte[] all = new byte[32];
                bool ok = true;
                for (int first = 0; first < 8 && ok; first += chunk)
                {
                    byte[] part = ReadBlocksOnce(uid, first, chunk);
                    if (part == null || part.Length != chunk * 4) { ok = false; break; }
                    Array.Copy(part, 0, all, first * 4, part.Length);
                }
                if (!IsOpen) return null;
                if (ok)
                {
                    if (chunk != BlocksPerRead) log.Write("Læseren klarer " + chunk + " blok(ke) ad gangen - det bruges fremover.");
                    BlocksPerRead = chunk;
                    method = chunk == 8 ? "8 blokke på én gang" : chunk + " blok(ke) ad gangen";
                    return all;
                }
                log.Write("  " + chunk + " blok(ke) ad gangen virkede ikke - prøver mindre bidder.");
            }
            return null;
        }
    }
}
