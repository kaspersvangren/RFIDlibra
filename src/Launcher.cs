using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace RFIDlibra
{
    /// <summary>Åbner brugerfladen i et rent Edge-vindue, og styrer "Start med Windows" (genvej i Start-mappen).</summary>
    public static class Launcher
    {
        public static string UiUrl(string token, string tab)
        {
            return "http://localhost:" + CiceroServer.Port + "/app?t=" + token + (string.IsNullOrEmpty(tab) ? "" : "#" + tab);
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

        /// <summary>Edge i "app"-tilstand: et almindeligt vindue uden adresselinje og faner. Ellers standardbrowseren.</summary>
        public static void OpenUi(string token, string tab, Log log)
        {
            string url = UiUrl(token, tab);
            try
            {
                string edge = FindEdge();
                if (edge != null)
                {
                    Process.Start(new ProcessStartInfo(edge, "--app=\"" + url + "\" --window-size=1180,820") { UseShellExecute = false });
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
        /// som starter det i baggrunden (kun ikonet ved uret). Returnerer en besked til brugeren.
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
