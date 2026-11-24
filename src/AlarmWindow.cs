using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RFIDlibra
{
    /// <summary>
    /// Listen over gatealarmer, nyeste øverst. Filter pr. port. Kun i hukommelsen - forsvinder, når RFIDlibra lukkes.
    /// </summary>
    public sealed class AlarmWindow : Form
    {
        private readonly GateWatch gates;
        private readonly Timer timer = new Timer { Interval = 1000 };
        private readonly FlowLayoutPanel chips = new FlowLayoutPanel();
        private readonly ListBox list = new ListBox();
        private readonly Label empty = new Label();
        private readonly Label foot = new Label();
        private readonly Label conn = new Label();
        private float s = 1f;
        private int filter;               // 0 = alle porte
        private string chipKey = "", listKey = "";
        private DateTime footUntil = DateTime.MinValue;
        private const string FootHint = "Dobbeltklik på en alarm for at kopiere materialenummeret. Listen forsvinder, når RFIDlibra lukkes.";

        public AlarmWindow(GateWatch gates, int gate)
        {
            this.gates = gates;
            filter = gate;
            Text = "RFIDlibra – Gatealarmer";
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Look.Light;
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
            ClientSize = new Size(Px(560), Px(520));
            MinimumSize = new Size(Px(420), Px(300));

            chips.Dock = DockStyle.Top;
            chips.Height = Px(48);
            chips.Padding = new Padding(Px(12), Px(10), Px(12), 0);
            chips.BackColor = Look.Light;
            chips.WrapContents = false;
            chips.AutoScroll = false;

            conn.Dock = DockStyle.Top;
            conn.Height = Px(22);
            conn.Padding = new Padding(Px(16), 0, Px(12), 0);
            conn.ForeColor = Look.InkMuted;
            conn.Font = new Font("Segoe UI", 8.5f);
            conn.TextAlign = ContentAlignment.MiddleLeft;

            foot.Dock = DockStyle.Bottom;
            foot.Height = Px(34);
            foot.Padding = new Padding(Px(16), 0, Px(12), 0);
            foot.ForeColor = Look.InkMuted;
            foot.Font = new Font("Segoe UI", 8.5f);
            foot.TextAlign = ContentAlignment.MiddleLeft;
            foot.Text = FootHint;

            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Px(52);
            list.IntegralHeight = false;
            list.BackColor = Color.White;
            list.DrawItem += DrawRow;
            list.DoubleClick += (o, e) => Copy();
            list.KeyDown += (o, e) => { if (e.Control && e.KeyCode == Keys.C) { Copy(); e.Handled = true; } };
            list.MouseDown += (o, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = list.IndexFromPoint(e.Location);
                if (i >= 0) list.SelectedIndex = i;
            };
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add("Kopiér materialenummer", null, (o, e) => Copy());
            cm.Opening += (o, e) => { if (Selected() == null) e.Cancel = true; };
            list.ContextMenuStrip = cm;

            Panel host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Px(12), Px(4), Px(12), 0), BackColor = Look.Light };
            Panel frame = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(1) };
            frame.Paint += (o, e) => { using (Pen p = new Pen(Color.FromArgb(0xd4, 0xdd, 0xdf))) e.Graphics.DrawRectangle(p, 0, 0, frame.Width - 1, frame.Height - 1); };
            empty.Dock = DockStyle.Fill;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.ForeColor = Look.InkMuted;
            empty.BackColor = Color.White;
            empty.Font = new Font("Segoe UI", 10f);
            frame.Controls.Add(list);
            frame.Controls.Add(empty);
            host.Controls.Add(frame);

            Controls.Add(host);
            Controls.Add(foot);
            Controls.Add(conn);
            Controls.Add(chips);

            timer.Tick += (o, e) => UpdateList();
            timer.Start();
            UpdateList();
        }

        private int Px(float v) { return (int)Math.Round(v * s); }

        /// <summary>Viser vinduet filtreret til en port (0 = alle).</summary>
        public void ShowGate(int gate)
        {
            filter = gate;
            chipKey = ""; listKey = "";
            UpdateList();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }

        // ---------- Indhold ----------

        private void UpdateList()
        {
            GateView v = gates.View();
            conn.Text = v.Connected ? "Forbundet til gatetrackeren" : (v.StatusText.Length > 0 ? v.StatusText : "Ikke forbundet til gatetrackeren");
            conn.ForeColor = v.Connected ? Look.InkMuted : Look.Red;
            if (DateTime.Now > footUntil && foot.Text != FootHint) { foot.Text = FootHint; foot.ForeColor = Look.InkMuted; }

            string ck = filter + "|" + string.Join(",", v.Lamps.Select(l => l.Gate + "=" + l.Name));
            if (ck != chipKey) { chipKey = ck; BuildChips(v); }

            List<GateAlarm> all = gates.Alarms();
            List<GateAlarm> shown = filter == 0 ? all : all.Where(a => a.Gate == filter).ToList();
            string lk = filter + "|" + string.Join(",", shown.Select(a => a.Seq + a.StatusText + a.Title));
            if (lk == listKey) { list.Invalidate(); return; }
            listKey = lk;
            GateAlarm keep = Selected();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (GateAlarm a in shown) list.Items.Add(a);
            if (keep != null) { int i = shown.IndexOf(keep); if (i >= 0) list.SelectedIndex = i; }
            list.EndUpdate();
            empty.Visible = shown.Count == 0;
            list.Visible = shown.Count > 0;
            empty.Text = filter == 0 ? "Ingen alarmer, siden RFIDlibra startede." : "Ingen alarmer på " + NameOf(v, filter) + ", siden RFIDlibra startede.";
        }

        private static string NameOf(GateView v, int gate)
        {
            LampView l = v.Lamps.FirstOrDefault(x => x.Gate == gate);
            return l != null ? l.Name : "Port " + gate;
        }

        private void BuildChips(GateView v)
        {
            chips.SuspendLayout();
            foreach (Control c in chips.Controls.Cast<Control>().ToList()) c.Dispose();
            chips.Controls.Clear();
            AddChip("Alle porte", 0);
            foreach (LampView l in v.Lamps) AddChip(l.Name, l.Gate);
            chips.ResumeLayout();
        }

        private void AddChip(string text, int gate)
        {
            bool on = gate == filter;
            FlatBtn b = new FlatBtn
            {
                Text = text,
                Fill = on ? Look.Petrol : Color.White,
                Ink = on ? Color.White : Look.Ink,
                Border = on ? Color.Empty : Color.FromArgb(0xc3, 0xd0, 0xd2),
                BackColor = Look.Light,
                Font = new Font("Segoe UI Semibold", 9f),
                Margin = new Padding(0, 0, Px(6), 0),
            };
            b.Size = new Size(TextRenderer.MeasureText(text, b.Font).Width + Px(26), Px(30));
            b.Click += (o, e) => ShowGate(gate);
            chips.Controls.Add(b);
        }

        private GateAlarm Selected() { return list.SelectedItem as GateAlarm; }

        private void Copy()
        {
            GateAlarm a = Selected();
            if (a == null || a.Barcode.Length == 0) return;
            try
            {
                Clipboard.SetText(a.Barcode);
                foot.Text = "Kopieret: " + a.Barcode;
                foot.ForeColor = Look.Green;
            }
            catch { foot.Text = "Kunne ikke kopiere"; foot.ForeColor = Look.Red; }
            footUntil = DateTime.Now.AddSeconds(3);
        }

        // ---------- Tegning af en række ----------

        public static Color AlarmColor(GateAlarm a)
        {
            if (!a.Done && a.ShownAt == null) return Look.Grey;
            return a.Loaned == true ? Look.Amber : Color.FromArgb(0xdc, 0x35, 0x45);
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            GateAlarm a = (GateAlarm)list.Items[e.Index];
            Graphics g = e.Graphics;
            Rectangle r = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(0xe4, 0xf1, 0xf4) : Color.White)) g.FillRectangle(b, r);
            using (SolidBrush b = new SolidBrush(AlarmColor(a))) g.FillRectangle(b, r.X, r.Y + Px(6), Px(6), r.Height - Px(12));
            using (Pen p = new Pen(Color.FromArgb(0xe8, 0xee, 0xef))) g.DrawLine(p, r.X + Px(16), r.Bottom - 1, r.Right, r.Bottom - 1);

            int x = r.X + Px(18), tw = Px(92);
            string gateName = gates.Settings.Name(a.Gate);
            using (Font f = new Font("Bahnschrift", 11f))
                TextRenderer.DrawText(g, a.Time.ToString("HH:mm", CultureInfo.InvariantCulture), f, new Rectangle(x, r.Y + Px(6), tw, Px(20)), Look.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (Font f = new Font("Segoe UI", 8f))
                TextRenderer.DrawText(g, gateName, f, new Rectangle(x, r.Y + Px(27), tw, Px(16)), Look.InkMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int x2 = x + tw + Px(6), w2 = r.Right - x2 - Px(10);
            using (Font f = new Font("Segoe UI Semibold", 9.5f))
                TextRenderer.DrawText(g, a.TitleLine, f, new Rectangle(x2, r.Y + Px(6), w2, Px(20)), Look.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            Color sc = a.Loaned == true ? Color.FromArgb(0xa9, 0x6c, 0x00) : a.Done || a.ShownAt != null ? Look.Red : Look.InkMuted;
            using (Font fs = new Font("Segoe UI Semibold", 8.5f))
            using (Font fn = new Font("Segoe UI", 8.5f))
            {
                int sw = TextRenderer.MeasureText(g, a.StatusText, fs, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, a.StatusText, fs, new Rectangle(x2, r.Y + Px(27), Math.Min(sw, w2), Px(16)), sc,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (a.Barcode.Length > 0 && sw + Px(14) < w2)
                    TextRenderer.DrawText(g, "·  " + a.Barcode, fn, new Rectangle(x2 + sw + Px(6), r.Y + Px(27), w2 - sw - Px(6), Px(16)), Look.InkMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
