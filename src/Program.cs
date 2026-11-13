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
            bool background = args.Any(a => a == "--baggrund");
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
