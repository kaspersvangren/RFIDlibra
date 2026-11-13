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
        public int Version;
        public int SeqNum;
        public int NumItems;
        public string Barcode;
        public string Country;
        public string Library;
        public bool CrcOk;

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
            byte[] crc = Crc16(b);
            t.CrcOk = crc[0] == b[19] && crc[1] == b[20];
            return t;
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
