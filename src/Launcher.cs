using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RFIDlibra
{
    /// <summary>Åbner værktøj, indstillinger og fejlfinding i hver sit rene Edge-vindue, og styrer "Start med Windows" (genvej i Start-mappen).</summary>
    public static class Launcher
    {
        public static string UiUrl(string token, string page)
        {
            return "http://localhost:" + CiceroServer.Port + "/app?t=" + token + (string.IsNullOrEmpty(page) ? "" : "#" + page);
        }

        private static string WindowSize(string page)
        {
            switch (page)
            {
                case "vaerktoej": return "1060,800";
                case "indstillinger": return "760,720";
                case "fejlfinding": return "1000,740";
                default: return "1060,800";
            }
        }

        private static string FindEdge()
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            };
            foreach (string r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                string p = Path.Combine(r, @"Microsoft\Edge\Application\msedge.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ---------- Kun ét vindue pr. side ----------

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static readonly string[] Pages = { "indstillinger", "fejlfinding", "vaerktoej", "laeser" };

        /// <summary>
        /// Lukker programmets egne Edge-vinduer (Indstillinger, Fejlfinding ...), når RFIDlibra afsluttes.
        /// Kun vinduer, hvis titel er præcis sidens titel - et almindeligt Edge-vindue har " - Microsoft Edge" efter titlen
        /// og røres derfor ikke, så brugerens andre faner aldrig lukkes.
        /// </summary>
        public static int CloseAll()
        {
            int closed = 0;
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    StringBuilder cls = new StringBuilder(64);
                    GetClassName(h, cls, cls.Capacity);
                    if (!cls.ToString().StartsWith("Chrome_WidgetWin")) return true;
                    StringBuilder t = new StringBuilder(256);
                    GetWindowText(h, t, t.Capacity);
                    string title = t.ToString();
                    foreach (string p in Pages)
                        if (title == PageTitle(p)) { PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); closed++; break; }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { }
            return closed;
        }

        /// <summary>Vinduestitlerne, siderne sætter selv (ui/index.html: "RFIDlibra – " + sidens navn).</summary>
        private static string PageTitle(string page)
        {
            switch (page)
            {
                case "indstillinger": return "RFIDlibra – Indstillinger";
                case "fejlfinding": return "RFIDlibra – Fejlfinding";
                case "vaerktoej": return "RFIDlibra – Værktøj til tags";
                case "laeser": return "RFIDlibra – På læseren";
                default: return null;
            }
        }

        private static readonly Dictionary<string, DateTime> lastLaunch = new Dictionary<string, DateTime>();

        /// <summary>Finder et åbent Edge-vindue med sidens titel og henter det frem. true hvis det fandtes.</summary>
        private static bool BringToFront(string page)
        {
            string title = PageTitle(page);
            if (title == null) return false;
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    StringBuilder cls = new StringBuilder(64);
                    GetClassName(h, cls, cls.Capacity);
                    if (!cls.ToString().StartsWith("Chrome_WidgetWin")) return true;   // Edge-vinduer
                    StringBuilder t = new StringBuilder(256);
                    GetWindowText(h, t, t.Capacity);
                    if (!t.ToString().StartsWith(title)) return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);
            }
            catch (Exception) { return false; }   // fx uden for Windows
            if (found == IntPtr.Zero) return false;
            if (IsIconic(found)) ShowWindow(found, 9);   // SW_RESTORE
            SetForegroundWindow(found);
            return true;
        }

        /// <summary>
        /// Edge i "app"-tilstand: et almindeligt vindue uden adresselinje og faner. Ellers standardbrowseren.
        /// Er siden allerede åben, hentes det vindue frem i stedet for at åbne et nyt.
        /// </summary>
        public static void OpenUi(string token, string page, Log log)
        {
            string key = page ?? "";
            if (BringToFront(page)) return;
            DateTime last;
            // Lige åbnet, men siden har ikke nået at sætte sin titel endnu: åbn ikke en til
            if (lastLaunch.TryGetValue(key, out last) && (DateTime.Now - last).TotalSeconds < 4) return;
            lastLaunch[key] = DateTime.Now;
            string url = UiUrl(token, page);
            try
            {
                string edge = FindEdge();
                if (edge != null)
                {
                    Process.Start(new ProcessStartInfo(edge, "--app=\"" + url + "\" --window-size=" + WindowSize(page)) { UseShellExecute = false });
                    return;
                }
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                if (log != null) log.Write("Kunne ikke åbne vinduet: " + e.Message);
            }
        }

        // ---------- Start med Windows ----------

        private static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "RFIDlibra.lnk"); }
        }

        /// <summary>Fast sted, programmet lægges, når det skal starte med Windows (kræver ikke administrator).</summary>
        public static string InstallPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\RFIDlibra\RFIDlibra.exe"); }
        }

        public static bool AutostartEnabled
        {
            get { return File.Exists(ShortcutPath); }
        }

        /// <summary>
        /// Slår "Start med Windows" til: programmet kopieres til et fast sted, og der lægges en genvej i Start-mappen,
        /// som starter det i baggrunden (skrankevinduet og ikonet ved uret). Returnerer en besked til brugeren.
        /// </summary>
        public static string EnableAutostart()
        {
            string me = Assembly.GetExecutingAssembly().Location;
            string target = InstallPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!string.Equals(Path.GetFullPath(me), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(me, target, true);

            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "--baggrund" });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "RFIDlibra - FEIG-læser til Cicero" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            return "RFIDlibra starter nu med Windows (ligger i " + Path.GetDirectoryName(target) + ")";
        }

        public static string DisableAutostart()
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return "RFIDlibra starter ikke længere med Windows";
        }
    }
}
