using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RFIDlibra
{
    /// <summary>
    /// Tynd indpakning af FEIG's feusb.dll (USB-delen af FEIG's SDK).
    /// Vi bruger filer, Bibliotheca allerede har installeret, og leverer dem ikke selv.
    /// Funktionerne findes med GetProcAddress, så vi selv kan vælge, hvilken feusb.dll der indlæses
    /// (fx den gamle 5.0 eller FeUsbOLD 4.2.6 fra den gamle Bibliotheca-software).
    /// FEIG erklærer funktionerne som CALLBACK (stdcall); på 64-bit er der kun én kaldkonvention.
    /// </summary>
    public sealed class Feusb
    {
        public const int SCAN_FIRST = 0x00000001;
        public const string INTERFACE = "OBID-RCI2";
        public const int ERR_TIMEOUT = -1130;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr hFile, uint flags);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetDLLVersionFn(byte[] version);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetErrorTextFn(int error, byte[] text);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ScanAndOpenFn(int scanOpt, IntPtr searchOpt);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CloseDeviceFn(int devHnd);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ClearScanListFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetScanListParaFn(int index, byte[] para, byte[] value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetDeviceParaFn(int devHnd, byte[] para, byte[] value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TransceiveFn(int devHnd, byte[] iface, int dir, byte[] send, int sendLen, byte[] rec, int recLen);

        private readonly GetDLLVersionFn getDllVersion;
        private readonly GetErrorTextFn getErrorText;
        private readonly ScanAndOpenFn scanAndOpen;
        private readonly CloseDeviceFn closeDevice;
        private readonly ClearScanListFn clearScanList;
        private readonly GetScanListParaFn getScanListPara;
        private readonly TransceiveFn transceive;
        private readonly SetDeviceParaFn setDevicePara;

        public string Path { get; private set; }

        private Feusb(string path, IntPtr module)
        {
            Path = path;
            getDllVersion = Get<GetDLLVersionFn>(module, "FEUSB_GetDLLVersion");
            getErrorText = Get<GetErrorTextFn>(module, "FEUSB_GetErrorText");
            scanAndOpen = Get<ScanAndOpenFn>(module, "FEUSB_ScanAndOpen");
            closeDevice = Get<CloseDeviceFn>(module, "FEUSB_CloseDevice");
            clearScanList = Get<ClearScanListFn>(module, "FEUSB_ClearScanList");
            getScanListPara = Get<GetScanListParaFn>(module, "FEUSB_GetScanListPara");
            transceive = Get<TransceiveFn>(module, "FEUSB_Transceive");
            setDevicePara = Get<SetDeviceParaFn>(module, "FEUSB_SetDevicePara");
        }

        /// <summary>Sætter, hvor længe feusb venter på svar fra læseren (FEIG-parameteren "Timeout", i ms).</summary>
        public int SetTimeout(int handle, int ms)
        {
            return setDevicePara(handle, Z("Timeout"), Z(ms.ToString()));
        }

        private static T Get<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException("filen mangler funktionen " + name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        /// <summary>En feusb-fil, programmet har fundet på disken.</summary>
        public sealed class Candidate
        {
            public string Path;
            public string Version;
            public int Bits;      // 32, 64 eller 0 (ukendt)
            public bool Usable { get { return Bits == (Environment.Is64BitProcess ? 64 : 32); } }

            public override string ToString()
            {
                string where = Path;
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrEmpty(pf86) && where.StartsWith(pf86, StringComparison.OrdinalIgnoreCase))
                    where = where.Substring(pf86.Length).TrimStart('\\');
                string s = System.IO.Path.GetFileName(Path) + " " + Version + " (" + (Bits == 0 ? "?" : Bits.ToString()) + "-bit) - " + where;
                if (!Usable) s += Bits == 64 ? "  [kræver RFIDlibra.exe]" : Bits == 32 ? "  [kræver RFIDlibra-32bit.exe]" : "  [kan ikke bruges]";
                return s;
            }
        }

        /// <summary>Mapper vi leder efter feusb-filer i.</summary>
        private static IEnumerable<string> SearchDirs()
        {
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrEmpty(pf86)) pf86 = @"C:\Program Files (x86)";
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            yield return exeDir;
            yield return System.IO.Path.Combine(exeDir, "RFIDIF");
            if (!string.IsNullOrEmpty(desk))
            {
                yield return System.IO.Path.Combine(desk, "RFIDIF");
                yield return System.IO.Path.Combine(desk, "RFIDIFServer");
            }
            yield return System.IO.Path.Combine(pf86, @"bibliotheca\Common\TagManager(PM)\x64");
            yield return System.IO.Path.Combine(pf86, @"bibliotheca\Common\TagManager(PM)\x86");
            yield return System.IO.Path.Combine(pf86, @"Bibliotheca RFID\RFIDIF");
            yield return System.IO.Path.Combine(pf86, @"Bibliotheca RFID\RFIDIFServer");
        }

        public static List<Candidate> FindCandidates(string extra)
        {
            List<Candidate> list = new List<Candidate>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> files = new List<string>();
            if (!string.IsNullOrEmpty(extra)) files.Add(extra);
            foreach (string d in SearchDirs())
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (string f in Directory.GetFiles(d, "feusb*.dll")) files.Add(f);
                }
                catch { }
            }
            foreach (string f in files)
            {
                string full;
                try { full = System.IO.Path.GetFullPath(f); } catch { continue; }
                if (!File.Exists(full) || !seen.Add(full)) continue;
                Candidate c = new Candidate { Path = full, Bits = PeBits(full) };
                try { c.Version = FileVersionInfo.GetVersionInfo(full).FileVersion ?? "?"; } catch { c.Version = "?"; }
                list.Add(c);
            }
            return list;
        }

        /// <summary>Læser i filens PE-hoved, om den er 32- eller 64-bit.</summary>
        public static int PeBits(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    fs.Position = pe + 4;
                    ushort machine = br.ReadUInt16();
                    if (machine == 0x014C) return 32;
                    if (machine == 0x8664) return 64;
                    return 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        public static Feusb Load(string path)
        {
            IntPtr m = LoadLibraryEx(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (m == IntPtr.Zero)
                throw new IOException("Kunne ikke indlæse " + path + " (Windows-fejl " + Marshal.GetLastWin32Error() + ")");
            return new Feusb(path, m);
        }

        private static string Str(byte[] b)
        {
            int n = Array.IndexOf(b, (byte)0);
            if (n < 0) n = b.Length;
            return Encoding.Default.GetString(b, 0, n).Trim();
        }

        private static byte[] Z(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);
            return b;
        }

        public string DllVersion()
        {
            byte[] b = new byte[256];
            getDllVersion(b);
            return Str(b);
        }

        public string ErrorText(int error)
        {
            byte[] b = new byte[512];
            try
            {
                getErrorText(error, b);
                string s = Str(b);
                return string.IsNullOrEmpty(s) ? error.ToString() : error + " (" + s + ")";
            }
            catch
            {
                return error.ToString();
            }
        }

        /// <summary>Finder den første FEIG-læser på USB og åbner den. Returnerer handle (&gt;0) eller fejlkode (&lt;0).</summary>
        public int ScanAndOpenFirst()
        {
            clearScanList();
            return scanAndOpen(SCAN_FIRST, IntPtr.Zero);
        }

        public string ScanListPara(string name)
        {
            byte[] v = new byte[256];
            int r = getScanListPara(0, Z(name), v);
            return r < 0 ? "?" : Str(v);
        }

        public void Close(int handle)
        {
            if (handle > 0) closeDevice(handle);
        }

        /// <summary>Sender en hel protokolramme og returnerer svarrammen. Negativ værdi i 'error' ved fejl.</summary>
        public byte[] Transceive(int handle, byte[] frame, out int error)
        {
            byte[] rec = new byte[2048];
            int n = transceive(handle, Z(INTERFACE), 0, frame, frame.Length, rec, rec.Length);
            if (n < 0) { error = n; return null; }
            error = 0;
            Array.Resize(ref rec, n);
            return rec;
        }
    }
}
