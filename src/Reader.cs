using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RFIDlibra
{
    /// <summary>
    /// Snakker med én FEIG-læser via feusb.dll. Bruges kun fra én tråd (arbejdstråden i Worker).
    /// Testudgaven er bygget til at finde ud af, hvad en gammel PR101 (firmware 1.x) kan klare,
    /// så alt rå trafik kan logges.
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
            DeviceName = usb.ScanListPara("DeviceName");
            Family = usb.ScanListPara("FamilyName");
            DeviceId = usb.ScanListPara("Device-ID");
            log.Write("Læser fundet: " + DeviceName + " (familie " + Family + ", ID " + DeviceId + ")");

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

        /// <summary>Sender én kommando. Returnerer null ved USB-fejl eller uforståeligt svar.</summary>
        public Protocol.Response Send(byte cmd, byte[] data, string what)
        {
            if (!IsOpen) return null;
            byte[] frame = Protocol.Build(Advanced, Address, cmd, data);
            Stopwatch sw = Stopwatch.StartNew();
            int err;
            byte[] rec = usb.Transceive(handle, frame, out err);
            long ms = sw.ElapsedMilliseconds;
            if (rec == null)
            {
                log.Write("  " + what + ": SENDT " + Protocol.Hex(frame) + "  -> USB-FEJL " + usb.ErrorText(err) + " efter " + ms + " ms");
                if (err == Feusb.ERR_TIMEOUT)
                {
                    // Som Bibliothecas software: efter timeout er USB-forbindelsen ubrugelig, så den åbnes igen.
                    log.Write("  Timeout - lukker og genåbner forbindelsen.");
                    ReopenQuietly();
                }
                return null;
            }
            Protocol.Response r = Protocol.Parse(rec);
            if (LogRaw)
                log.Write("  " + what + ": SENDT " + Protocol.Hex(frame) + "  SVAR " + Protocol.Hex(rec) + "  (" + ms + " ms)");
            if (r == null)
            {
                log.Write("  " + what + ": svaret kunne ikke fortolkes.");
                return null;
            }
            if (!r.CrcOk) log.Write("  " + what + ": advarsel - CRC i svaret passer ikke.");
            return r;
        }

        private void ReopenQuietly()
        {
            bool adv = Advanced;
            byte adr = Address;
            Close();
            System.Threading.Thread.Sleep(300);
            int h = usb.ScanAndOpenFirst();
            if (h > 0)
            {
                handle = h;
                Advanced = adv;
                Address = adr;
                log.Write("  Forbindelsen er åben igen.");
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
