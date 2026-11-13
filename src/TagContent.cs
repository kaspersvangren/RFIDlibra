using System;
using System.Text;

namespace RFIDlibra
{
    /// <summary>
    /// Indholdet af et bibliotekstag i den danske RFID-datamodel (32 bytes = blok 0-7).
    /// Samme fortolkning som i NFClibra og Deichmans go-feig:
    ///  [0]      version (4 bit) + type (4 bit), normalt 0x11
    ///  [1]      antal dele ("NumItems")
    ///  [2]      delnummer ("SeqNum")
    ///  [3:19]   materialenummer, ASCII, nul-udfyldt
    ///  [19:21]  CRC
    ///  [21:23]  landekode ("DK")
    ///  [23:32]  biblioteksnummer (ISIL), ASCII, nul-udfyldt
    /// </summary>
    public sealed class TagContent
    {
        public const string DefaultCountry = "DK";

        public int Version;
        public int SeqNum;
        public int NumItems;
        public string Barcode = "";
        public string Country = "";
        public string Library = "";
        public byte[] Crc;
        public bool CrcOk;

        public static TagContent Empty()
        {
            return new TagContent();
        }

        public static TagContent Parse(byte[] b)
        {
            if (b == null || b.Length < 32) return null;
            TagContent t = new TagContent();
            t.Version = b[0];
            t.NumItems = b[1];
            t.SeqNum = b[2];
            t.Barcode = Ascii(b, 3, 19);
            t.Country = Ascii(b, 21, 23);
            t.Library = Ascii(b, 23, 32);
            t.Crc = new[] { b[19], b[20] };
            byte[] crc = Crc16(b);
            t.CrcOk = crc[0] == b[19] && crc[1] == b[20];
            return t;
        }

        /// <summary>Bygger de 32 bytes, der skrives til blok 0-7, inkl. CRC (som NFClibra og go-feig).</summary>
        public static byte[] Build(string barcode, int numItems, int seqNum, string country, string library)
        {
            if (string.IsNullOrEmpty(barcode) || barcode.Length > 16) throw new ArgumentException("materialenummer skal være 1-16 tegn");
            foreach (char ch in barcode)
                if (ch < 0x20 || ch > 0x7E) throw new ArgumentException("materialenummer må kun indeholde almindelige tegn");
            byte[] bs = new byte[36];
            bs[0] = 0x11;
            bs[1] = (byte)numItems;
            bs[2] = (byte)seqNum;
            Encoding.ASCII.GetBytes(barcode).CopyTo(bs, 3);
            byte[] c = Encoding.ASCII.GetBytes(country.Length > 2 ? country.Substring(0, 2) : country);
            c.CopyTo(bs, 21);
            byte[] l = Encoding.ASCII.GetBytes(library.Length > 9 ? library.Substring(0, 9) : library);
            l.CopyTo(bs, 23);
            byte[] crc = Crc16(bs);
            bs[19] = crc[0];
            bs[20] = crc[1];
            byte[] outp = new byte[32];
            Array.Copy(bs, outp, 32);
            return outp;
        }

        /// <summary>CRC over bytes 0-18 og 21-33 (32 bytes, med to nul-bytes til sidst), som i go-feig og NFClibra.</summary>
        public static byte[] Crc16(byte[] b)
        {
            byte[] cs = new byte[32];
            Array.Copy(b, 0, cs, 0, 19);
            Array.Copy(b, 21, cs, 19, Math.Min(13, b.Length - 21));
            int crc = 0xFFFF;
            foreach (byte v in cs)
            {
                crc ^= v << 8;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                    crc &= 0xFFFF;
                }
            }
            return new[] { (byte)(crc & 0xFF), (byte)(crc >> 8) };
        }

        private static string Ascii(byte[] b, int from, int to)
        {
            return Encoding.GetEncoding(28591).GetString(b, from, to - from).TrimEnd('\0');
        }

        public string Part
        {
            get { return NumItems > 0 ? SeqNum + " af " + NumItems : "?"; }
        }
    }
}
