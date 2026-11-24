using System;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RFIDlibra
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            // Administrator-delen af "Installer/fjern for alle brugere" (startet af programmet selv med runas)
            if (args.Length > 0 && (args[0] == AllUsers.InstallArg || args[0] == AllUsers.RemoveArg || args[0] == AllUsers.GateArg))
            {
                Environment.Exit(AllUsers.RunAsAdmin(args));
                return;
            }
            bool background = args.Any(a => a == "--baggrund");
            // Ved login: er RFIDlibra installeret for alle brugere, er det den udgave, der skal køre - ikke en gammel personlig kopi
            if (background && AllUsers.Installed && !AllUsers.RunningFromInstall) return;
            bool created;
            using (Mutex single = new Mutex(true, @"Local\RFIDlibra-en-ad-gangen", out created))
            {
                if (!created)
                {
                    // Kører allerede: vis skrankevinduet i den, der kører
                    if (!background) ShowRunning();
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App(background));
            }
        }

        private static void ShowRunning()
        {
            try
            {
                string token = AppInfo.Load("nøgle.txt");
                if (token.Length == 0) return;
                using (WebClient wc = new WebClient { Proxy = null })
                    wc.UploadString("http://localhost:" + CiceroServer.Port + "/app/api/show?t=" + token, "POST", "");
            }
            catch { }
        }
    }
}
