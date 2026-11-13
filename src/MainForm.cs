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
        public const string Version = "0.3.0";

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
        private readonly ComboBox dllBox = new ComboBox();
        private readonly Button pickDll = new Button();
        private bool filling;

        public MainForm()
        {
            Text = "RFIDlibra " + Version + " (" + (Environment.Is64BitProcess ? "64" : "32") + "-bit) - test af læser";
            Width = 1000;
            Height = 700;
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            FlowLayoutPanel dllRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            Label dllLabel = new Label { Text = "FEIG-fil:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            dllBox.DropDownStyle = ComboBoxStyle.DropDownList;
            dllBox.Width = 720;
            dllBox.SelectedIndexChanged += (s, e) => DllChosen();
            pickDll.Text = "Vælg fil ...";
            pickDll.AutoSize = true;
            pickDll.Click += (s, e) => PickDll();
            dllRow.Controls.AddRange(new Control[] { dllLabel, dllBox, pickDll });
            root.Controls.Add(dllRow);

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

        private static string SettingsFile
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RFIDlibra");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "feusb-" + (Environment.Is64BitProcess ? "64" : "32") + "bit.txt");
            }
        }

        private static string SavedDll()
        {
            try { return File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile).Trim() : null; } catch { return null; }
        }

        private void Startup()
        {
            log.Write("RFIDlibra " + Version + " startet. " + (Environment.Is64BitProcess ? "64" : "32") + "-bit, Windows " + Environment.OSVersion.Version);
            if (log.FilePath != null) log.Write("Loggen gemmes også her: " + log.FilePath);
            WarnIfStaffConnectRuns();

            string saved = SavedDll();
            List<Feusb.Candidate> found = Feusb.FindCandidates(saved);
            log.Write("Fundne FEIG-filer:");
            foreach (Feusb.Candidate c in found) log.Write("  " + c);
            Feusb.Candidate chosen = found.FirstOrDefault(c => c.Usable && saved != null && string.Equals(c.Path, Path.GetFullPath(saved), StringComparison.OrdinalIgnoreCase))
                ?? found.FirstOrDefault(c => c.Usable);

            filling = true;
            foreach (Feusb.Candidate c in found) dllBox.Items.Add(c);
            if (chosen != null) dllBox.SelectedItem = chosen;
            filling = false;

            if (chosen == null)
            {
                log.Write("FEJL: ingen feusb.dll, der passer til denne udgave (" + (Environment.Is64BitProcess ? "64" : "32") + "-bit).");
                status.Text = "Ingen brugbar feusb.dll - se loggen";
                connect.Enabled = false;
                return;
            }
            try
            {
                usb = Feusb.Load(chosen.Path);
                log.Write("Bruger " + usb.Path + " (version " + usb.DllVersion() + ")");
                ToggleConnect();
            }
            catch (Exception e)
            {
                log.Write("FEJL: " + e.Message);
                status.Text = "feusb.dll kunne ikke bruges - se loggen";
                connect.Enabled = false;
            }
        }

        private void DllChosen()
        {
            if (filling) return;
            Feusb.Candidate c = dllBox.SelectedItem as Feusb.Candidate;
            if (c == null || (usb != null && string.Equals(c.Path, usb.Path, StringComparison.OrdinalIgnoreCase))) return;
            UseDll(c.Path, c.Bits);
        }

        private void PickDll()
        {
            using (OpenFileDialog d = new OpenFileDialog { Filter = "FEIG USB (feusb*.dll)|feusb*.dll|Alle DLL-filer|*.dll", Title = "Vælg feusb.dll" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                UseDll(d.FileName, Feusb.PeBits(d.FileName));
            }
        }

        /// <summary>Gemmer valget og genstarter, så den nye fil indlæses i en ren proces.</summary>
        private void UseDll(string path, int bits)
        {
            int mine = Environment.Is64BitProcess ? 64 : 32;
            if (bits != mine)
            {
                MessageBox.Show(this, "Filen er " + (bits == 0 ? "af ukendt type" : bits + "-bit") + ". Den kan kun bruges med "
                    + (bits == 32 ? "RFIDlibra-32bit.exe" : bits == 64 ? "RFIDlibra.exe" : "ingen af udgaverne") + ".", "RFIDlibra");
                filling = true;
                Feusb.Candidate cur = dllBox.Items.Cast<Feusb.Candidate>().FirstOrDefault(x => usb != null && x.Path == usb.Path);
                dllBox.SelectedItem = cur;
                filling = false;
                return;
            }
            try { File.WriteAllText(SettingsFile, path); } catch (Exception e) { log.Write("Kunne ikke gemme valget: " + e.Message); return; }
            log.Write("Valgt: " + path + " - genstarter.");
            if (worker != null) worker.Stop();
            Application.Restart();
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
