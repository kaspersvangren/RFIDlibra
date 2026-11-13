using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RFIDlibra
{
    /// <summary>
    /// Tynd indpakning af FEIG's feusb.dll (USB-delen af FEIG's SDK).
    /// Vi bruger den fil, Bibliotheca allerede har installeret, og leverer den ikke selv.
    /// Funktionerne findes med GetProcAddress, så vi selv kan vælge, hvilken feusb.dll der indlæses.
    /// Programmet kører altid som 64-bit, så kaldkonventionen er entydig.
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
        private delegate int TransceiveFn(int devHnd, byte[] iface, int dir, byte[] send, int sendLen, byte[] rec, int recLen);

        private readonly GetDLLVersionFn getDllVersion;
        private readonly GetErrorTextFn getErrorText;
        private readonly ScanAndOpenFn scanAndOpen;
        private readonly CloseDeviceFn closeDevice;
        private readonly ClearScanListFn clearScanList;
        private readonly GetScanListParaFn getScanListPara;
        private readonly TransceiveFn transceive;

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
        }

        private static T Get<T>(IntPtr module, string name) where T : class
        {
            IntPtr p = GetProcAddress(module, name);
            if (p == IntPtr.Zero) throw new EntryPointNotFoundException("feusb.dll mangler funktionen " + name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        /// <summary>Steder vi leder efter feusb.dll, i prioriteret rækkefølge.</summary>
        public static string[] Candidates()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrEmpty(pf86)) pf86 = @"C:\Program Files (x86)";
            return new[]
            {
                System.IO.Path.Combine(exeDir, "feusb.dll"),
                System.IO.Path.Combine(pf86, @"bibliotheca\Common\TagManager(PM)\x64\feusb.dll"),
            };
        }

        public static Feusb Load(Action<string> log)
        {
            if (!Environment.Is64BitProcess)
                throw new InvalidOperationException("Programmet skal køre som 64-bit.");
            foreach (string c in Candidates())
            {
                if (!File.Exists(c)) { log("feusb.dll ikke fundet her: " + c); continue; }
                IntPtr m = LoadLibraryEx(c, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                if (m == IntPtr.Zero)
                {
                    log("Kunne ikke indlæse " + c + " (Windows-fejl " + Marshal.GetLastWin32Error() + ")");
                    continue;
                }
                return new Feusb(c, m);
            }
            throw new FileNotFoundException("feusb.dll blev ikke fundet. Er Bibliothecas Tag Manager installeret?");
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
