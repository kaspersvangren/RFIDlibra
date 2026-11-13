using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Windows.Forms;

namespace RFIDlibra
{
    public sealed class MainForm : Form
    {
        public const string Version = "0.1.0";

        private readonly Log log = new Log();
        private Feusb usb;
        private Worker worker;

        private readonly Label status = new Label();
        private readonly Button connect = new Button();
        private readonly Button selfTest = new Button();
        private readonly CheckBox raw = new CheckBox();
        private readonly ListView list = new ListView();
        private readonly TextBox logBox = new TextBox();
        private readonly Button copyLog = new Button();
        private readonly Button saveLog = new Button();

        public MainForm()
        {
            Text = "RFIDlibra " + Version + " - test af læser";
            Width = 1000;
            Height = 700;
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            connect.Text = "Forbind";
            connect.AutoSize = true;
            connect.Click += (s, e) => ToggleConnect();
            selfTest.Text = "Selvtest af læsning";
            selfTest.AutoSize = true;
            selfTest.Enabled = false;
            selfTest.Click += (s, e) => { if (worker != null) worker.RequestSelfTest(); };
            raw.Text = "Rå data i loggen";
            raw.Checked = true;
            raw.AutoSize = true;
            raw.Padding = new Padding(8, 6, 0, 0);
            raw.CheckedChanged += (s, e) => { if (worker != null) worker.LogRaw = raw.Checked; };
            status.AutoSize = true;
            status.Padding = new Padding(12, 7, 0, 0);
            status.Font = new Font(Font, FontStyle.Bold);
            status.Text = "Ikke forbundet";
            top.Controls.AddRange(new Control[] { connect, selfTest, raw, status });
            root.Controls.Add(top);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.Columns.Add("UID", 170);
            list.Columns.Add("Materialenummer", 140);
            list.Columns.Add("Del", 60);
            list.Columns.Add("Bibliotek", 110);
            list.Columns.Add("Alarm", 100);
            list.Columns.Add("Læst med", 150);
            list.Columns.Add("Bemærkning", 200);
            root.Controls.Add(list);

            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Both;
            logBox.WordWrap = false;
            logBox.Font = new Font("Consolas", 9f);
            root.Controls.Add(logBox);

            FlowLayoutPanel bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            copyLog.Text = "Kopiér log";
            copyLog.AutoSize = true;
            copyLog.Click += (s, e) => { try { Clipboard.SetText(log.Text); } catch { } };
            saveLog.Text = "Gem log på skrivebordet";
            saveLog.AutoSize = true;
            saveLog.Click += (s, e) => SaveLog();
            bottom.Controls.AddRange(new Control[] { copyLog, saveLog });
            root.Controls.Add(bottom);

            log.Line += OnLogLine;
            Load += (s, e) => Startup();
            FormClosing += (s, e) => { if (worker != null) worker.Stop(); };
        }

        private void Startup()
        {
            log.Write("RFIDlibra " + Version + " startet. 64-bit: " + Environment.Is64BitProcess + ", Windows " + Environment.OSVersion.Version);
            if (log.FilePath != null) log.Write("Loggen gemmes også her: " + log.FilePath);
            WarnIfStaffConnectRuns();
            try
            {
                usb = Feusb.Load(log.Write);
                log.Write("feusb.dll indlæst fra " + usb.Path + " (version " + usb.DllVersion() + ")");
                ToggleConnect();
            }
            catch (Exception e)
            {
                log.Write("FEJL: " + e.Message);
                status.Text = "feusb.dll mangler - se loggen";
                connect.Enabled = false;
            }
        }

        private void WarnIfStaffConnectRuns()
        {
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                {
                    string n = (sc.DisplayName + " " + sc.ServiceName).ToLowerInvariant();
                    if ((n.Contains("staffconnect") || n.Contains("arbiter")) && sc.Status == ServiceControllerStatus.Running)
                    {
                        log.Write("ADVARSEL: tjenesten \"" + sc.DisplayName + "\" kører. Den holder fast i læseren, så testen ikke kan bruge den.");
                        log.Write("         Stop den i Tjenester (services.msc) under testen, og start den igen bagefter.");
                    }
                }
            }
            catch (Exception e)
            {
                log.Write("Kunne ikke tjekke tjenester: " + e.Message);
            }
        }

        private void ToggleConnect()
        {
            if (usb == null) return;
            if (worker != null && worker.Running)
            {
                worker.Stop();
                worker = null;
                connect.Text = "Forbind";
                selfTest.Enabled = false;
                status.Text = "Ikke forbundet";
                ShowTags(new List<TagView>());
                log.Write("Afbrudt.");
                return;
            }
            worker = new Worker(usb, log) { LogRaw = raw.Checked };
            worker.Status += s => UI(() => status.Text = s);
            worker.TagsChanged += t => UI(() => ShowTags(t));
            worker.Start();
            connect.Text = "Afbryd";
            selfTest.Enabled = true;
        }

        private void ShowTags(List<TagView> tags)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (TagView t in tags.OrderBy(x => x.Barcode).ThenBy(x => x.Uid))
            {
                ListViewItem it = new ListViewItem(new[] { t.Uid, t.Barcode, t.Part, t.Library, t.Alarm, t.Method, t.Note });
                list.Items.Add(it);
            }
            list.EndUpdate();
        }

        private void OnLogLine(string line)
        {
            UI(() =>
            {
                if (logBox.TextLength > 300000) logBox.Text = logBox.Text.Substring(100000);
                logBox.AppendText(line + Environment.NewLine);
            });
        }

        private void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a); else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void SaveLog()
        {
            try
            {
                string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "RFIDlibra-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(f, log.Text);
                MessageBox.Show(this, "Loggen er gemt:\n" + f, "RFIDlibra");
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Kunne ikke gemme loggen: " + e.Message, "RFIDlibra");
            }
        }
    }
}
