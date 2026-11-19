using System;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace RFIDlibra
{
    /// <summary>
    /// Tjekker (hvis brugeren har slået det til) om der er en nyere udgivelse på GitHub. Programmet opdaterer aldrig sig selv -
    /// det viser kun en besked. "Vis ikke igen" gælder den ene version.
    /// Indstillinger pr. bruger: opdatering.txt (til/fra) og opdatering-skjult.txt (versioner, der ikke skal vises igen).
    /// </summary>
    public sealed class UpdateCheck
    {
        public const string RepoUrl = "https://github.com/kaspersvangren/RFIDlibra";
        private const string ApiUrl = "https://api.github.com/repos/kaspersvangren/RFIDlibra/releases/latest";

        private readonly Log log;
        private readonly Control invoker;
        private DateTime nextCheck = DateTime.Now.AddSeconds(15);
        private int running;
        private UpdateNotice notice;

        /// <summary>Sidste resultat til Indstillinger, fx "Du har den nyeste version (tjekket 09:55)".</summary>
        public string Status { get; private set; }

        public UpdateCheck(Log log, Control invoker)
        {
            this.log = log;
            this.invoker = invoker;
            Status = "";
        }

        public bool Enabled
        {
            get { return AppInfo.Load("opdatering.txt") == "til"; }
            set
            {
                AppInfo.Save("opdatering.txt", value ? "til" : "fra");
                log.Write(value ? "Tjek efter nye versioner er slået til." : "Tjek efter nye versioner er slået fra.");
                if (value) nextCheck = DateTime.Now; else Status = "";
            }
        }

        /// <summary>Kaldes hvert sekund fra programmets timer.</summary>
        public void Tick()
        {
            if (!Enabled || DateTime.Now < nextCheck) return;
            nextCheck = DateTime.Now.AddHours(24);
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            new Thread(Run) { IsBackground = true, Name = "Opdateringstjek" }.Start();
        }

        private void Run()
        {
            try
            {
                string json;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "RFIDlibra/" + AppInfo.Version;
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    if (wc.Proxy != null) wc.Proxy.Credentials = CredentialCache.DefaultCredentials;
                    wc.Encoding = System.Text.Encoding.UTF8;
                    json = wc.DownloadString(ApiUrl);
                }
                string tag = Field(json, "tag_name");
                string url = Field(json, "html_url") ?? RepoUrl + "/releases/latest";
                Version latest = Parse(tag), mine = Parse(AppInfo.Version);
                string when = DateTime.Now.ToString("HH:mm");
                if (latest == null || mine == null) { Status = "Kunne ikke læse svaret fra GitHub (" + when + ")"; return; }
                if (latest <= mine) { Status = "Du har den nyeste version (tjekket " + when + ")"; return; }
                string v = latest.ToString(3);
                Status = "Version " + v + " er klar (tjekket " + when + ")";
                log.Write("Ny version på GitHub: " + v + " (du har " + AppInfo.Version + ").");
                if (Hidden(v)) return;
                invoker.BeginInvoke((Action)(() => Show(v, url)));
            }
            catch (Exception e)
            {
                Status = "Kunne ikke nå GitHub (" + DateTime.Now.ToString("HH:mm") + ")";
                log.Write("Tjek efter ny version: kunne ikke nå GitHub - " + e.Message);
                nextCheck = DateTime.Now.AddHours(1);
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }

        private static string Field(string json, string name)
        {
            Match m = Regex.Match(json ?? "", "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static Version Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            Match m = Regex.Match(s, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        private static bool Hidden(string v)
        {
            foreach (string line in AppInfo.Load("opdatering-skjult.txt").Split('\n'))
                if (line.Trim() == v) return true;
            return false;
        }

        private void Hide(string v)
        {
            string old = AppInfo.Load("opdatering-skjult.txt");
            AppInfo.Save("opdatering-skjult.txt", (old.Length > 0 ? old + "\n" : "") + v);
            log.Write("Beskeden om version " + v + " vises ikke igen.");
        }

        private void Show(string v, string url)
        {
            if (notice != null && !notice.IsDisposed) notice.Close();
            notice = new UpdateNotice(v, url, () => Hide(v));
            notice.Show();
        }
    }

    /// <summary>Lille besked nederst til højre, i samme stil som skrankevinduet. Tager ikke fokus fra Cicero.</summary>
    public sealed class UpdateNotice : Form
    {
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly string version;
        private readonly float s;

        public UpdateNotice(string version, string url, Action hide)
        {
            this.version = version;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Look.Petrol;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
            ClientSize = new Size((int)(330 * s), (int)(112 * s));

            FlatBtn see = Btn("Se hvad der er nyt", Look.Light, Look.Ink);
            FlatBtn later = Btn("Senere", Color.Transparent, Look.TextLight);
            FlatBtn never = Btn("Vis ikke igen", Color.Transparent, Look.TextLight);
            later.Border = never.Border = Look.TextMuted;
            int y = (int)(70 * s), h = (int)(28 * s), m = (int)(14 * s), gap = (int)(8 * s);
            see.SetBounds(m, y, (int)(132 * s), h);
            later.SetBounds(see.Right + gap, y, (int)(70 * s), h);
            never.SetBounds(later.Right + gap, y, ClientSize.Width - m - later.Right - gap, h);
            see.Click += (o, e) => { try { System.Diagnostics.Process.Start(url); } catch { } Close(); };
            later.Click += (o, e) => Close();
            never.Click += (o, e) => { hide(); Close(); };
            Controls.AddRange(new Control[] { see, later, never });

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - (int)(12 * s), wa.Bottom - Height - (int)(12 * s));
        }

        private FlatBtn Btn(string text, Color fill, Color ink)
        {
            return new FlatBtn { Text = text, Fill = fill, Ink = ink, BackColor = Look.Petrol, Font = Font };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int m = (int)(14 * s);
            using (Font f = new Font("Segoe UI Semibold", 10.5f))
                TextRenderer.DrawText(e.Graphics, "RFIDlibra " + version + " er klar", f, new Point(m, (int)(12 * s)), Look.TextLight, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, "Du har " + AppInfo.Version + ". Den nye version hentes fra GitHub.", Font,
                new Point(m, (int)(38 * s)), Look.TextMuted, TextFormatFlags.NoPadding);
        }
    }
}
