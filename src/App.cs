using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;

namespace RFIDlibra
{
    public static class AppInfo
    {
        public const string Version = "0.6.0";

        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RFIDlibra");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string TokenFile { get { return Path.Combine(DataDir, "nøgle.txt"); } }

        public static string Load(string name)
        {
            try { string f = Path.Combine(DataDir, name); return File.Exists(f) ? File.ReadAllText(f).Trim() : ""; } catch { return ""; }
        }

        public static void Save(string name, string value)
        {
            try { File.WriteAllText(Path.Combine(DataDir, name), value); } catch { }
        }
    }

    /// <summary>
    /// Programmet: ikon ved uret, Cicero-serveren, læserens arbejdstråd og brugerfladen.
    /// Der er intet fast vindue; brugerfladen åbnes i et Edge-vindue fra ikonet.
    /// </summary>
    public sealed class App : ApplicationContext
    {
        public readonly Log Log = new Log();
        public Worker Worker { get; private set; }

        private readonly string token;
        private readonly CiceroServer server;
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToolStripMenuItem autostartItem;
        private readonly Timer timer = new Timer { Interval = 1000 };
        private Feusb usb;
        private string usbError;
        private volatile bool staffConnect;
        private DateTime lastServiceCheck = DateTime.MinValue;
        private volatile string mode = "IDLE";
        private string iconKey;
        private volatile string library;
        private readonly Control invoker = new Control();   // til at køre ting på brugerfladens tråd (COM, menuer)

        public App(bool background)
        {
            byte[] b = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            token = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
            AppInfo.Save("nøgle.txt", token);
            library = AppInfo.Load("library.txt");
            invoker.CreateControl();

            Log.Write("RFIDlibra " + AppInfo.Version + " startet" + (background ? " i baggrunden" : "") + ". Windows " + Environment.OSVersion.Version);
            if (Log.FilePath != null) Log.Write("Loggen gemmes også her: " + Log.FilePath);

            server = new CiceroServer(Log, new Hub(this));
            server.Ui = new UiServer(this, token);
            server.Start();

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Åbn RFIDlibra", null, (s, e) => Open(""));
            menu.Items.Add("Værktøj til tags", null, (s, e) => Open("vaerktoej"));
            menu.Items.Add("Fejlfinding", null, (s, e) => Open("fejlfinding"));
            menu.Items.Add(new ToolStripSeparator());
            autostartItem = new ToolStripMenuItem("Start med Windows") { Checked = Launcher.AutostartEnabled };
            autostartItem.Click += (s, e) => MessageBox.Show(SetAutostart(!Launcher.AutostartEnabled), "RFIDlibra");
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Afslut", null, (s, e) => Quit());
            menu.Items[0].Font = new Font(menu.Items[0].Font, FontStyle.Bold);
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => Open("");
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Open(""); };
            tray.Text = "RFIDlibra";
            UpdateTray();
            tray.Visible = true;

            CheckServices();
            StartReader();

            timer.Tick += (s, e) => { UpdateTray(); if ((DateTime.Now - lastServiceCheck).TotalSeconds > 10) CheckServices(); };
            timer.Start();

            if (!background) Open("");
        }

        public void Open(string tab)
        {
            Launcher.OpenUi(token, tab, Log);
        }

        // ---------- Læseren ----------

        private void StartReader()
        {
            if (usb == null)
            {
                try
                {
                    string saved = AppInfo.Load("feusb.txt");
                    List<Feusb.Candidate> found = Feusb.FindCandidates(saved);
                    Feusb.Candidate chosen = found.FirstOrDefault(c => c.Usable && saved.Length > 0 && string.Equals(c.Path, saved, StringComparison.OrdinalIgnoreCase))
                        ?? found.FirstOrDefault(c => c.Usable);
                    if (chosen == null)
                    {
                        usbError = "FEIG's feusb.dll blev ikke fundet. Bibliothecas Tag Manager skal være installeret.";
                        Log.Write("FEJL: " + usbError);
                        return;
                    }
                    usb = Feusb.Load(chosen.Path);
                    Log.Write("Bruger " + usb.Path + " (version " + usb.DllVersion() + ")");
                }
                catch (Exception e)
                {
                    usbError = "feusb.dll kunne ikke bruges: " + e.Message;
                    Log.Write("FEJL: " + usbError);
                    return;
                }
            }
            Worker w = new Worker(usb, Log) { Server = server, LibraryForNewChips = library, Mode = mode };
            Worker = w;
            w.Start();
        }

        public void Reconnect()
        {
            Worker w = Worker;
            if (w != null) w.Stop();
            Log.Write("Forbinder til læseren igen.");
            StartReader();
        }

        public void SelfTest()
        {
            Worker w = Worker;
            if (w != null) w.RequestSelfTest();
        }

        public void SetLogRaw(bool on)
        {
            Worker w = Worker;
            if (w != null) w.LogRaw = on;
        }

        public void SetLibrary(string lib)
        {
            lib = lib.Trim();
            library = lib;
            AppInfo.Save("library.txt", lib);
            Worker w = Worker;
            if (w != null) w.LibraryForNewChips = lib;
        }

        public string SetAutostart(bool on)
        {
            if (invoker.InvokeRequired) return (string)invoker.Invoke((Func<string>)(() => SetAutostart(on)));
            string msg;
            try { msg = on ? Launcher.EnableAutostart() : Launcher.DisableAutostart(); }
            catch (Exception e) { msg = "Det lykkedes ikke: " + e.Message; }
            Log.Write(msg);
            autostartItem.Checked = Launcher.AutostartEnabled;
            return msg;
        }

        private void CheckServices()
        {
            lastServiceCheck = DateTime.Now;
            try
            {
                bool running = false;
                foreach (ServiceController sc in ServiceController.GetServices())
                {
                    string n = (sc.DisplayName + " " + sc.ServiceName).ToLowerInvariant();
                    if ((n.Contains("staffconnect") || n.Contains("arbiter")) && sc.Status == ServiceControllerStatus.Running) running = true;
                }
                if (running && !staffConnect) Log.Write("ADVARSEL: staffConnect link kører på denne pc og kan tage læseren fra RFIDlibra.");
                staffConnect = running;
            }
            catch { }
        }

        // ---------- Tilstand til brugerfladen ----------

        public string StateJson()
        {
            Worker w = Worker;
            bool readerOk = w != null && w.Running && w.ReaderOk;
            string readerText = usbError ?? (w == null ? "Starter ..." : w.ReaderText);
            StringBuilder sb = new StringBuilder("{");
            sb.Append("\"version\":").Append(Json.Str(AppInfo.Version));
            sb.Append(",\"reader\":{\"ok\":").Append(readerOk ? "true" : "false");
            sb.Append(",\"text\":").Append(Json.Str(readerText));
            sb.Append(",\"name\":").Append(Json.Str(w != null ? w.ReaderName : ""));
            sb.Append(",\"stats\":").Append(Json.Str(w != null ? w.StatsText : ""));
            sb.Append(",\"feusb\":").Append(Json.Str(usb != null ? usb.Path : ""));
            sb.Append("},\"cicero\":{\"listening\":").Append(server.ClientCount > 0 ? "true" : "false");
            sb.Append(",\"error\":").Append(Json.Str(server.BindError));
            sb.Append("},\"staffConnect\":").Append(staffConnect ? "true" : "false");
            sb.Append(",\"tags\":").Append(w != null && readerOk ? w.TagsJson : "[]");
            sb.Append(",\"settings\":{\"library\":").Append(Json.Str(library));
            sb.Append(",\"autostart\":").Append(Launcher.AutostartEnabled ? "true" : "false");
            sb.Append(",\"logRaw\":").Append(w != null && w.LogRaw ? "true" : "false");
            sb.Append("}}");
            return sb.ToString();
        }

        // ---------- Ikonet ved uret ----------

        private void UpdateTray()
        {
            Worker w = Worker;
            bool readerOk = w != null && w.Running && w.ReaderOk;
            bool listening = server.ClientCount > 0;
            string key;
            Color c;
            string text;
            if (server.BindError != null || !readerOk)
            {
                key = "red"; c = Color.FromArgb(220, 53, 69);
                text = server.BindError ?? usbError ?? (w != null ? w.ReaderText : "Ingen læser");
            }
            else if (!listening)
            {
                key = "amber"; c = Color.FromArgb(232, 160, 32);
                text = "Læseren er klar - Cicero lytter ikke";
            }
            else
            {
                key = "green"; c = Color.FromArgb(40, 167, 92);
                text = "Læseren er klar - Cicero lytter";
            }
            string tip = "RFIDlibra: " + text;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (key == iconKey) return;
            iconKey = key;
            Icon old = tray.Icon;
            tray.Icon = MakeIcon(c);
            if (old != null) old.Dispose();
        }

        /// <summary>Tegner ikonet: farvet cirkel (status) med to hvide "radiobølger".</summary>
        private static Icon MakeIcon(Color c)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 30, 30);
                    using (Pen p = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawArc(p, 7, 7, 18, 18, -50, 100);
                        g.DrawArc(p, 2, 2, 28, 28, -50, 100);
                    }
                    using (SolidBrush w = new SolidBrush(Color.White)) g.FillEllipse(w, 9, 13, 6, 6);
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        private void Quit()
        {
            timer.Stop();
            Log.Write("Afslutter.");
            Worker w = Worker;
            if (w != null) w.Stop();
            server.Stop();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }

        // ---------- Cicero ----------

        /// <summary>Sender Ciceros forespørgsler videre til den aktuelle arbejdstråd.</summary>
        private sealed class Hub : CiceroServer.IHub
        {
            private readonly App app;
            public Hub(App app) { this.app = app; }

            private Worker Current
            {
                get { Worker w = app.Worker; if (w == null || !w.Running) throw new InvalidOperationException("Reader not connected"); return w; }
            }

            public string StatusJson() { return Current.StatusJson(); }
            public string InventoryJson() { Worker w = app.Worker; return w != null && w.Running ? w.InventoryJson() : "{}"; }
            public CiceroServer.Result Alarm(bool on) { return Current.Alarm(on); }
            public CiceroServer.Result Write(string barcode) { return Current.Write(barcode); }
            public CiceroServer.Result WriteTagBarcode(string tagId, string barcode) { return Current.WriteTagBarcode(tagId, barcode); }
            public void OnClientConnected() { Worker w = app.Worker; if (w != null && w.Running) w.OnClientConnected(); }
            public string Mode { get { return app.mode; } set { app.mode = value; Worker w = app.Worker; if (w != null) w.Mode = value; } }
        }
    }
}
