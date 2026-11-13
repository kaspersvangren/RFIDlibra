using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RFIDlibra
{
    /// <summary>Det, skrankevinduet viser (hentes fra App 4 gange i sekundet).</summary>
    public sealed class WidgetState
    {
        public bool ReaderOk;
        public string ReaderText = "";
        public bool Listening;
        public string BindError;
        public bool StaffConnect;
        public TagView[] Tags = new TagView[0];
    }

    /// <summary>Hvor skrankevinduet får sine data fra (App, eller en testopstilling).</summary>
    public interface IWidgetSource
    {
        WidgetState GetWidgetState();
        string AlarmAll(bool on);
    }

    /// <summary>
    /// Skrankevinduet: lille, uden kanter, altid øverst. Viser bøgerne på læseren og har knapper til
    /// alarm fra/til på dem alle. Tager ikke fokus fra Cicero, når man klikker på det.
    /// Højreklik giver menuen.
    /// </summary>
    public sealed class Widget : Form
    {
        // Farver (samme sprog som brugerfladen: petrol læseplade, hvide etiketter, alarmstrimmel)
        private static readonly Color Bg = Color.FromArgb(0x16, 0x3c, 0x47);
        private static readonly Color BgTop = Color.FromArgb(0x1d, 0x4a, 0x57);
        private static readonly Color Edge = Color.FromArgb(0x0c, 0x25, 0x2c);
        private static readonly Color Ring = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color TextLight = Color.FromArgb(0xf2, 0xf6, 0xf7);
        private static readonly Color TextMuted = Color.FromArgb(0x9f, 0xbb, 0xc2);
        private static readonly Color Paper = Color.FromArgb(0xff, 0xff, 0xff);
        private static readonly Color Ink = Color.FromArgb(0x17, 0x26, 0x2b);
        private static readonly Color InkMuted = Color.FromArgb(0x6a, 0x7a, 0x80);
        private static readonly Color Amber = Color.FromArgb(0xe3, 0x9b, 0x1c);
        private static readonly Color Green = Color.FromArgb(0x2f, 0x9e, 0x63);
        private static readonly Color Grey = Color.FromArgb(0xa5, 0xb0, 0xb3);
        private static readonly Color Red = Color.FromArgb(0xdc, 0x35, 0x45);
        private static readonly Color DotGreen = Color.FromArgb(0x3c, 0xc7, 0x7a);

        private const int BaseWidth = 264;
        private const int MaxLabels = 4;

        private readonly IWidgetSource app;
        private readonly Timer timer = new Timer { Interval = 250 };
        private readonly Timer flashTimer = new Timer { Interval = 30 };
        private readonly ContextMenuStrip menu;
        private float s = 1f;

        private WidgetState st = new WidgetState();
        private string key = "";
        private Dictionary<string, int?> lastAfi = new Dictionary<string, int?>();

        private Rectangle btnOff, btnOn, btnMenu;
        private int hover;               // 0 ingen, 1 alarm fra, 2 alarm til, 3 menu
        private int busy;                // 0 ingen, 1 alarm fra, 2 alarm til
        private string message;          // kort besked efter en fejl
        private DateTime messageUntil;
        private Color flashColor;
        private DateTime flashStart = DateTime.MinValue;
        private Point savedAt;

        public Widget(IWidgetSource app, ContextMenuStrip menu)
        {
            this.app = app;
            this.menu = menu;
            Text = "RFIDlibra";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            Font = new Font("Segoe UI", 9f);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
            Width = Px(BaseWidth);
            Height = Px(160);
            Location = LoadPosition();
            savedAt = Location;

            timer.Tick += (o, e) => Tick();
            flashTimer.Tick += (o, e) => { Invalidate(); if ((DateTime.Now - flashStart).TotalMilliseconds > 900) flashTimer.Stop(); };
            timer.Start();
            Tick();
        }

        private int Px(float v) { return (int)Math.Round(v * s); }

        // ---------- Vinduets opførsel ----------

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

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); } catch { }
        }

        /// <summary>Vis vinduet (fra ikonet ved uret) og sørg for, at det kan ses på skærmen.</summary>
        public void ShowWidget()
        {
            if (!OnScreen(Location)) Location = DefaultPosition();
            Show();
            TopMost = false;
            TopMost = true;
            SavePosition();
        }

        public void HideWidget()
        {
            Hide();
            SavePosition();
        }

        // ---------- Position ----------

        private static bool OnScreen(Point p)
        {
            return Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(new Point(p.X + 40, p.Y + 20)));
        }

        private Point DefaultPosition()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - Width - Px(24), wa.Bottom - Px(240));
        }

        private Point LoadPosition()
        {
            string[] p = AppInfo.Load("vindue.txt").Split(';');
            int x, y;
            if (p.Length >= 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) && OnScreen(new Point(x, y)))
                return new Point(x, y);
            return DefaultPosition();
        }

        /// <summary>Om vinduet var skjult, da programmet sidst kørte.</summary>
        public static bool StartHidden
        {
            get { string[] p = AppInfo.Load("vindue.txt").Split(';'); return p.Length >= 3 && p[2] == "skjult"; }
        }

        private void SavePosition()
        {
            savedAt = Location;
            AppInfo.Save("vindue.txt", Location.X.ToString(CultureInfo.InvariantCulture) + ";" + Location.Y.ToString(CultureInfo.InvariantCulture) + ";" + (Visible ? "vist" : "skjult"));
        }

        // ---------- Opdatering ----------

        private void Tick()
        {
            if (Location != savedAt && (MouseButtons & MouseButtons.Left) == 0) SavePosition();
            if (message != null && DateTime.Now > messageUntil) { message = null; key = ""; }

            WidgetState n = app.GetWidgetState();
            DetectAlarmChange(n);
            string k = n.ReaderOk + "|" + n.ReaderText + "|" + n.Listening + "|" + n.BindError + "|" + n.StaffConnect + "|" + busy + "|" + message + "|"
                + string.Join(",", n.Tags.Select(t => t.Uid + t.Kind + t.Barcode + t.Seq + "/" + t.Num + t.Afi));
            st = n;
            if (k == key) return;
            key = k;
            Arrange();
            Invalidate();
        }

        /// <summary>Blinker grønt eller orange, når alarmen skifter på en bog - uanset om det var Cicero eller knapperne.</summary>
        private void DetectAlarmChange(WidgetState n)
        {
            Dictionary<string, int?> now = new Dictionary<string, int?>();
            int? changedTo = null;
            foreach (TagView t in n.Tags)
            {
                if (t.Kind == "unread") continue;
                now[t.Uid] = t.Afi;
                int? before;
                if (lastAfi.TryGetValue(t.Uid, out before) && before != t.Afi && (t.Afi == TagState.AFI_ON || t.Afi == TagState.AFI_OFF))
                    changedTo = t.Afi;
            }
            lastAfi = now;
            if (changedTo != null) Flash(changedTo == TagState.AFI_ON ? Amber : Green);
        }

        private void Flash(Color c)
        {
            flashColor = c;
            flashStart = DateTime.Now;
            flashTimer.Start();
            Invalidate();
        }

        private bool HasReadTags { get { return st.ReaderOk && st.Tags.Any(t => t.Kind != "unread"); } }

        private string Headline(out Color dot)
        {
            if (st.BindError != null) { dot = Red; return "Cicero kan ikke forbinde"; }
            if (!st.ReaderOk) { dot = Red; return "Ingen læser"; }
            dot = st.Listening ? DotGreen : Amber;
            int n = Items().Count;
            return n == 0 ? "Læseren er tom" : n + " på læseren";
        }

        private string Subline(out Color c)
        {
            c = TextMuted;
            if (message != null) { c = Color.FromArgb(0xff, 0xa0, 0xa8); return message; }
            if (st.BindError != null) return st.BindError;
            if (!st.ReaderOk) return st.ReaderText;
            if (st.StaffConnect) { c = Color.FromArgb(0xf5, 0xc4, 0x6b); return "staffConnect kører og kan tage læseren"; }
            if (!st.Listening) return "Cicero lytter ikke";
            return null;
        }

        /// <summary>En etiket i vinduet: én bog. Alle dele af et sæt samles på én etiket.</summary>
        private sealed class Item
        {
            public string Title;
            public bool Muted;
            public string Alarm;
            public string Parts = "";
            public bool PartsMissing;
            public Color Strip;
        }

        private static string AlarmWord(int? afi)
        {
            return afi == TagState.AFI_ON ? "Alarm til" : afi == TagState.AFI_OFF ? "Alarm fra" : afi == null ? "Alarm ukendt"
                : afi == 0 ? "Ingen alarm sat" : "Alarm 0x" + afi.Value.ToString("X2");
        }

        private static Color StripColor(int? afi)
        {
            return afi == TagState.AFI_ON ? Amber : afi == TagState.AFI_OFF ? Green : Grey;
        }

        private List<Item> Items()
        {
            List<Item> items = new List<Item>();
            var groups = st.Tags
                .OrderBy(t => t.Kind == "danish" && t.Barcode.Length > 0 ? 0 : t.Kind == "unread" ? 2 : 1).ThenBy(t => t.Barcode, StringComparer.Ordinal).ThenBy(t => t.Seq).ThenBy(t => t.Uid)
                .GroupBy(t => t.Kind == "danish" && t.Num > 1 && t.Barcode.Length > 0 ? "sæt:" + t.Barcode : "tag:" + t.Uid);
            foreach (var g in groups)
            {
                TagView t = g.First();
                Item it = new Item();
                switch (t.Kind)
                {
                    case "unread": it.Title = "Læser …"; it.Muted = true; break;
                    case "blank": it.Title = "Tom chip"; it.Muted = true; break;
                    case "unknown": it.Title = "Ukendt indhold"; it.Muted = true; break;
                    default:
                        if (t.Barcode.Length > 0) it.Title = t.Barcode; else { it.Title = "Intet nummer"; it.Muted = true; }
                        break;
                }
                bool sameAlarm = g.All(x => x.Afi == t.Afi);
                it.Alarm = t.Kind == "unread" ? "" : sameAlarm ? AlarmWord(t.Afi) : "Alarm forskellig på delene";
                it.Strip = sameAlarm ? StripColor(t.Afi) : Grey;
                if (t.Kind == "danish" && t.Num > 1)
                {
                    int have = g.Select(x => x.Seq).Distinct().Count();
                    it.Parts = have + " af " + t.Num + " dele";
                    it.PartsMissing = have < t.Num;
                }
                items.Add(it);
            }
            return items;
        }

        // Lodrette mål (ved 96 dpi)
        private const int M = 10, HeadH = 34, SubH = 16, LabelH = 38, LabelGap = 4, MoreH = 20, BtnH = 34;

        private void Arrange()
        {
            Color dummy;
            int y = HeadH + (Subline(out dummy) != null ? SubH : 0) + 2;
            int count = st.ReaderOk ? Items().Count : 0;
            int n = Math.Min(count, MaxLabels);
            if (n == 0) y += LabelH;
            else y += n * LabelH + (n - 1) * LabelGap;
            if (count > MaxLabels) y += MoreH;
            y += M;
            int bw = (BaseWidth - 2 * M - 8) / 2;
            btnOff = new Rectangle(Px(M), Px(y), Px(bw), Px(BtnH));
            btnOn = new Rectangle(Px(M + bw + 8), Px(y), Px(bw), Px(BtnH));
            y += BtnH + M;
            btnMenu = new Rectangle(Px(BaseWidth - 34), Px(6), Px(26), Px(22));
            int h = Px(y);
            if (Height != h) Height = h;
        }

        // ---------- Tegning ----------

        private static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle all = ClientRectangle;

            // Baggrund: læseplade med svage feltringe ude fra hjørnet (som på den store læser)
            using (LinearGradientBrush b = new LinearGradientBrush(all, BgTop, Bg, LinearGradientMode.Vertical)) g.FillRectangle(b, all);
            using (Pen p = new Pen(Ring, Px(1)))
                for (int i = 1; i <= 4; i++)
                {
                    int r = Px(70 * i);
                    g.DrawEllipse(p, all.Right - Px(30) - r, all.Bottom - Px(20) - r, 2 * r, 2 * r);
                }

            // Overskrift
            Color dot;
            string head = Headline(out dot);
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, Px(M + 3), Px(13), Px(9), Px(9));
            using (Font f = new Font("Segoe UI Semibold", 10f))
                TextRenderer.DrawText(g, head, f, new Rectangle(Px(M + 18), Px(6), Px(BaseWidth - 80), Px(24)), TextLight,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Menu-prikker
            if (hover == 3) using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) using (GraphicsPath p = Round(btnMenu, Px(4))) g.FillPath(b, p);
            using (SolidBrush b = new SolidBrush(hover == 3 ? TextLight : TextMuted))
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(b, btnMenu.X + btnMenu.Width / 2f + i * Px(6) - Px(1.6f), btnMenu.Y + btnMenu.Height / 2f - Px(1.6f), Px(3.2f), Px(3.2f));

            int y = HeadH;
            Color subColor;
            string sub = Subline(out subColor);
            if (sub != null)
            {
                using (Font f = new Font("Segoe UI", 8f))
                    TextRenderer.DrawText(g, sub, f, new Rectangle(Px(M + 3), Px(y - 6), Px(BaseWidth - 2 * M - 6), Px(SubH)), subColor,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                y += SubH;
            }
            y += 2;

            // Etiketter
            List<Item> tags = st.ReaderOk ? Items() : new List<Item>();
            if (tags.Count == 0)
            {
                RectangleF r = new RectangleF(Px(M), Px(y), Px(BaseWidth - 2 * M), Px(LabelH));
                using (Pen p = new Pen(Color.FromArgb(70, 255, 255, 255), Px(1)) { DashStyle = DashStyle.Dash })
                using (GraphicsPath gp = Round(r, Px(5))) g.DrawPath(p, gp);
                using (Font f = new Font("Segoe UI", 9f))
                    TextRenderer.DrawText(g, st.ReaderOk ? "Læg en bog på læseren" : "Sæt læseren i USB", f, Rectangle.Round(r), TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using (Font num = new Font("Bahnschrift", 11.5f))
            using (Font small = new Font("Segoe UI", 7.5f))
                for (int i = 0; i < Math.Min(tags.Count, MaxLabels); i++)
                {
                    DrawLabel(g, tags[i], new RectangleF(Px(M), Px(y), Px(BaseWidth - 2 * M), Px(LabelH)), num, small);
                    y += LabelH + LabelGap;
                }
            if (tags.Count > 0) y -= LabelGap;
            if (tags.Count > MaxLabels)
            {
                using (Font f = new Font("Segoe UI", 8.5f))
                    TextRenderer.DrawText(g, "+" + (tags.Count - MaxLabels) + " mere", f, new Rectangle(Px(M), Px(y + 2), Px(BaseWidth - 2 * M), Px(MoreH)), TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // Knapper
            bool enabled = HasReadTags && busy == 0;
            DrawButton(g, btnOff, "Alarm fra", Green, Color.White, enabled, hover == 1, busy == 1);
            DrawButton(g, btnOn, "Alarm til", Amber, Ink, enabled, hover == 2, busy == 2);

            // Kant og blink
            using (Pen p = new Pen(Edge, 1)) g.DrawRectangle(p, 0, 0, all.Width - 1, all.Height - 1);
            double ms = (DateTime.Now - flashStart).TotalMilliseconds;
            if (ms < 900)
            {
                int a = (int)(255 * (1 - ms / 900));
                using (Pen p = new Pen(Color.FromArgb(a, flashColor), Px(4))) g.DrawRectangle(p, Px(2), Px(2), all.Width - Px(4), all.Height - Px(4));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a / 6, flashColor))) g.FillRectangle(b, all);
            }
        }

        private void DrawLabel(Graphics g, Item t, RectangleF r, Font num, Font small)
        {
            using (GraphicsPath p = Round(r, Px(4)))
            {
                using (SolidBrush b = new SolidBrush(Paper)) g.FillPath(b, p);
                Region keep = g.Clip;
                g.SetClip(p);
                using (SolidBrush b = new SolidBrush(t.Strip)) g.FillRectangle(b, r.X, r.Y, Px(6), r.Height);
                g.Clip = keep;
            }
            int x = (int)r.X + Px(16), w = (int)r.Width - Px(24);
            TextRenderer.DrawText(g, t.Title, num, new Rectangle(x, (int)r.Y + Px(2), w, Px(21)), t.Muted ? InkMuted : Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, t.Alarm, small, new Rectangle(x, (int)r.Y + Px(22), w, Px(14)), InkMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (t.Parts.Length > 0)
                using (Font f = t.PartsMissing ? new Font(small, FontStyle.Bold) : null)
                    TextRenderer.DrawText(g, t.Parts, f ?? small, new Rectangle(x, (int)r.Y + Px(22), w, Px(14)), t.PartsMissing ? Color.FromArgb(0xc2, 0x3b, 0x22) : Ink,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawButton(Graphics g, Rectangle r, string text, Color fill, Color ink, bool enabled, bool hot, bool working)
        {
            Color c = !enabled && !working ? Mix(Bg, fill, 0.35f) : hot ? Mix(fill, Color.White, 0.14f) : fill;
            Color tc = !enabled && !working ? Mix(Bg, ink, 0.55f) : ink;
            using (GraphicsPath p = Round(r, Px(5)))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
            using (Font f = new Font("Segoe UI Semibold", 9.5f))
                TextRenderer.DrawText(g, working ? "Vent …" : text, f, r, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---------- Mus ----------

        private int HitTest(Point p)
        {
            if (btnOff.Contains(p)) return 1;
            if (btnOn.Contains(p)) return 2;
            if (btnMenu.Contains(p)) return 3;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            bool clickable = h == 3 || (h != 0 && HasReadTags && busy == 0);
            Cursor = clickable ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != 0) { hover = 0; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { ShowMenu(e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h == 3) { ShowMenu(new Point(btnMenu.Left, btnMenu.Bottom)); return; }
            if (h == 1 || h == 2) { SetAlarm(h == 2); return; }
            // Træk i vinduet
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }

        private void ShowMenu(Point at)
        {
            // Menuen skal have fokus for at lukke igen, når man klikker ved siden af
            SetForegroundWindow(Handle);
            menu.Show(this, at);
        }

        // ---------- Alarm på alle ----------

        private void SetAlarm(bool on)
        {
            if (busy != 0 || !HasReadTags) return;
            busy = on ? 2 : 1;
            key = "";
            Invalidate();
            Task.Run(() => app.AlarmAll(on)).ContinueWith(t =>
            {
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                try { BeginInvoke((Action)(() => AlarmDone(err))); } catch { }
            });
        }

        private void AlarmDone(string err)
        {
            busy = 0;
            if (err != null)
            {
                message = err;
                messageUntil = DateTime.Now.AddSeconds(5);
                Flash(Red);
            }
            key = "";
            Tick();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); flashTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
