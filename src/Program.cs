using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace RFIDlibra
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool background = args.Any(a => a == "--baggrund");
            bool created;
            using (Mutex single = new Mutex(true, @"Local\RFIDlibra-en-ad-gangen", out created))
            {
                if (!created)
                {
                    // Kører allerede: åbn bare vinduet for den, der kører
                    if (!background)
                    {
                        string token = AppInfo.Load("nøgle.txt");
                        if (token.Length > 0) Launcher.OpenUi(token, "", null);
                    }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App(background));
            }
        }
    }
}
