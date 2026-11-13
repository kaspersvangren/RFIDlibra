using System;
using System.Collections.Generic;
using System.Text;

namespace RFIDlibra
{
    /// <summary>
    /// FEIG's "ISO Host"-protokol: rammer med længde, busadresse, kommando, data og CRC16.
    /// Standard-ramme: [LEN][ADR][CMD][DATA..][CRC lo][CRC hi]          (højst 255 bytes)
    /// Advanced-ramme: [0x02][LEN hi][LEN lo][ADR][CMD][DATA..][CRC lo][CRC hi]
    /// Svar har desuden en statusbyte lige efter CMD.
    /// </summary>
    public static class Protocol
    {
        public const byte CMD_SW_VERSION = 0x65;
        public const byte CMD_ISO = 0xB0;

        public const byte ISO_INVENTORY = 0x01;
        public const byte ISO_READ_BLOCKS = 0x23;
        public const byte ISO_SYSINFO = 0x2B;

        public static ushort Crc(byte[] b, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= b[i];
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
            }
            return crc;
        }

        public static byte[] Build(bool advanced, byte adr, byte cmd, byte[] data)
        {
            data = data ?? new byte[0];
            byte[] f;
            int p;
            if (advanced)
            {
                int len = 7 + data.Length;
                f = new byte[len];
                f[0] = 0x02; f[1] = (byte)(len >> 8); f[2] = (byte)len; f[3] = adr; f[4] = cmd;
                p = 5;
            }
            else
            {
                int len = 5 + data.Length;
                if (len > 255) throw new ArgumentException("For lang ramme til standardformat");
                f = new byte[len];
                f[0] = (byte)len; f[1] = adr; f[2] = cmd;
                p = 3;
            }
            Array.Copy(data, 0, f, p, data.Length);
            ushort crc = Crc(f, 0, f.Length - 2);
            f[f.Length - 2] = (byte)crc;
            f[f.Length - 1] = (byte)(crc >> 8);
            return f;
        }

        public sealed class Response
        {
            public bool Advanced;
            public byte Cmd;
            public byte Status;
            public byte[] Data;
            public bool CrcOk;
        }

        /// <summary>Fortolker en svarramme. Returnerer null, hvis den ikke ligner en FEIG-ramme.</summary>
        public static Response Parse(byte[] f)
        {
            if (f == null || f.Length < 6) return null;
            Response r = new Response();
            int hdr;
            int len;
            if (f[0] == 0x02 && f.Length >= 8)
            {
                len = (f[1] << 8) | f[2];
                r.Advanced = true;
                hdr = 5;
                r.Cmd = f[4];
                r.Status = f[5];
            }
            else
            {
                len = f[0];
                hdr = 3;
                r.Cmd = f[2];
                r.Status = f[3];
            }
            if (len > f.Length || len < hdr + 3) return null;
            ushort crc = Crc(f, 0, len - 2);
            r.CrcOk = f[len - 2] == (byte)crc && f[len - 1] == (byte)(crc >> 8);
            int dataStart = hdr + 1;
            int dataLen = len - 2 - dataStart;
            r.Data = new byte[Math.Max(0, dataLen)];
            if (dataLen > 0) Array.Copy(f, dataStart, r.Data, 0, dataLen);
            return r;
        }

        private static readonly Dictionary<byte, string> statusText = new Dictionary<byte, string>
        {
            { 0x00, "OK" },
            { 0x01, "intet tag" },
            { 0x02, "CRC-fejl" },
            { 0x03, "skrivefejl" },
            { 0x04, "adressefejl" },
            { 0x05, "forkert tagtype" },
            { 0x10, "EEPROM-fejl" },
            { 0x11, "forkert parameterlængde" },
            { 0x17, "firmware skal aktiveres" },
            { 0x80, "ukendt kommando" },
            { 0x81, "protokolfejl" },
            { 0x82, "kommando ikke understøttet" },
            { 0x83, "RF-kommunikationsfejl" },
            { 0x84, "RF-advarsel" },
            { 0x92, "ingen gyldige data" },
            { 0x93, "buffer fuld" },
            { 0x94, "flere data" },
            { 0x95, "tagfejl" },
        };

        public static string StatusText(byte s)
        {
            string t;
            return "0x" + s.ToString("X2") + " " + (statusText.TryGetValue(s, out t) ? t : "ukendt status");
        }

        public static string Hex(byte[] b)
        {
            if (b == null) return "(intet)";
            StringBuilder sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            return sb.ToString();
        }

        public static string Uid(byte[] uid)
        {
            return BitConverter.ToString(uid).Replace("-", ":");
        }
    }
}
