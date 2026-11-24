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
        public GateView Gates;           // null, når gatealarmer er slået fra
    }

    /// <summary>Hvor skrankevinduet får sine data fra (App, eller en testopstilling).</summary>
    public interface IWidgetSource
    {
        WidgetState GetWidgetState();
        string AlarmAll(bool on);
        void OpenAlarms(int gate);
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

        private const int BaseWidth = 244, CompactW = 250, CompactH = 34;
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
        private bool compact;
        private readonly ToolStripMenuItem compactItem;
        private readonly ToolTip tip = new ToolTip { ShowAlways = true, InitialDelay = 300, AutoPopDelay = 15000 };
        private string tipText;
        private readonly ToolTip lampTip = new ToolTip { OwnerDraw = true, UseAnimation = false, UseFading = false };
        private Rectangle[] lampRects = new Rectangle[0];   // klik- og museområde pr. lampe
        private int hoverLamp = -1;
        private string lampTipKey;

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
            compact = AppInfo.Load("visning.txt").Trim() == "kompakt";
            compactItem = new ToolStripMenuItem("Kompakt visning") { Checked = compact };
            compactItem.Click += (o, e) => SetCompact(!compact);
            menu.Items.Insert(0, new ToolStripSeparator());
            menu.Items.Insert(0, compactItem);
            Width = Px(BaseWidth);
            Height = Px(160);
            Location = LoadPosition();
            savedAt = Location;

            lampTip.Popup += (o, e) => e.ToolTipSize = LampTipSize();
            lampTip.Draw += (o, e) => DrawLampTip(e.Graphics, e.Bounds);
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

        private void SetCompact(bool on)
        {
            compact = on;
            compactItem.Checked = on;
            AppInfo.Save("visning.txt", on ? "kompakt" : "normal");
            key = "";
            Tick();
            if (!OnScreen(Location)) Location = DefaultPosition();
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
            string k = compact + "|" + n.ReaderOk + "|" + n.ReaderText + "|" + n.Listening + "|" + n.BindError + "|" + n.StaffConnect + "|" + busy + "|" + message + "|"
                + string.Join(",", n.Tags.Select(t => t.Uid + t.Kind + t.Barcode + t.Seq + "/" + t.Num + t.Afi))
                + "|" + LampKey(n.Gates);
            st = n;
            RefreshLampTip();
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
            public List<int> Missing = new List<int>();
            public int Num;
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
                    it.Num = t.Num;
                    HashSet<int> seqs = new HashSet<int>(g.Select(x => x.Seq));
                    it.Missing = Enumerable.Range(1, t.Num).Where(i => !seqs.Contains(i)).ToList();
                }
                items.Add(it);
            }
            // Materialer, der mangler dele, øverst - så de aldrig gemmer sig under "+N mere"
            return items.Where(i => i.PartsMissing).Concat(items.Where(i => !i.PartsMissing)).ToList();
        }

        // Lodrette mål (ved 96 dpi)
        private const int M = 8, HeadH = 30, SubH = 16, LabelH = 34, LabelGap = 4, MoreH = 20, BtnH = 30;
        // Gatelamper: vandrette øverst i normal visning, lodrette i venstre kant i kompakt visning
        private const int LampTop = 7, LampH = 7, LampGap = 5;
        private const int CLampX = 9, CLampW = 5, CLampGap = 3, CLampTop = 7;

        private static int LampBand(int lamps) { return lamps > 0 ? LampTop + LampH + 2 : 0; }
        private static int CompactLampsW(int lamps) { return lamps > 0 ? lamps * (CLampW + CLampGap) + 6 : 0; }

        private void Arrange()
        {
            UpdateTip();
            int lamps = st.Gates != null ? st.Gates.Lamps.Count : 0;
            if (compact)
            {
                int cw = CompactW + CompactLampsW(lamps);
                btnOff = new Rectangle(Px(cw - 8 - 2 * 42 - 4), Px(5), Px(42), Px(24));
                btnOn = new Rectangle(Px(cw - 8 - 42), Px(5), Px(42), Px(24));
                btnMenu = Rectangle.Empty;
                lampRects = new Rectangle[lamps];
                for (int i = 0; i < lamps; i++) lampRects[i] = new Rectangle(Px(CLampX + i * (CLampW + CLampGap) - 1), 0, Px(CLampW + CLampGap), Px(CompactH));
                if (Width != Px(cw)) Width = Px(cw);
                if (Height != Px(CompactH)) Height = Px(CompactH);
                return;
            }
            if (Width != Px(BaseWidth)) Width = Px(BaseWidth);
            int oy = LampBand(lamps);
            lampRects = new Rectangle[lamps];
            if (lamps > 0)
            {
                float lw = (BaseWidth - 2 * M - (lamps - 1) * LampGap) / (float)lamps;
                for (int i = 0; i < lamps; i++)
                    lampRects[i] = new Rectangle(Px(M + i * (lw + LampGap) - LampGap / 2f), 0, Px(lw + LampGap), Px(LampTop + LampH + 4));
            }
            Color dummy;
            int y = oy + HeadH + (Subline(out dummy) != null ? SubH : 0) + 2;
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
            btnMenu = new Rectangle(Px(BaseWidth - 32), Px(oy + 4), Px(26), Px(22));
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

            if (compact) { PaintCompact(g, all); return; }

            int oy = LampBand(st.Gates != null ? st.Gates.Lamps.Count : 0);
            PaintLamps(g);

            // Overskrift
            Color dot;
            string head = Headline(out dot);
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, Px(M + 3), Px(oy + 11), Px(9), Px(9));
            using (Font f = new Font("Segoe UI Semibold", 10f))
                TextRenderer.DrawText(g, head, f, new Rectangle(Px(M + 18), Px(oy + 4), Px(BaseWidth - 76), Px(22)), TextLight,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Menu-prikker
            if (hover == 3) using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) using (GraphicsPath p = Round(btnMenu, Px(4))) g.FillPath(b, p);
            using (SolidBrush b = new SolidBrush(hover == 3 ? TextLight : TextMuted))
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(b, btnMenu.X + btnMenu.Width / 2f + i * Px(6) - Px(1.6f), btnMenu.Y + btnMenu.Height / 2f - Px(1.6f), Px(3.2f), Px(3.2f));

            int y = oy + HeadH;
            Color subColor;
            string sub = Subline(out subColor);
            if (sub != null)
            {
                using (Font f = new Font("Segoe UI", 8f))
                    TextRenderer.DrawText(g, sub, f, new Rectangle(Px(M + 3), Px(y - 5), Px(BaseWidth - 2 * M - 6), Px(SubH)), subColor,
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
            using (Font num = new Font("Bahnschrift", 11f))
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
                    TextRenderer.DrawText(g, "+" + (tags.Count - MaxLabels) + " mere" + MoreMissing(tags), f, new Rectangle(Px(M), Px(y + 2), Px(BaseWidth - 2 * M), Px(MoreH)), TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // Knapper
            bool enabled = HasReadTags && busy == 0;
            DrawButton(g, btnOff, "Alarm fra", Green, Color.White, enabled, hover == 1, busy == 1);
            DrawButton(g, btnOn, "Alarm til", Amber, Ink, enabled, hover == 2, busy == 2);

            PaintEdge(g, all);
        }

        /// <summary>" (1 mangler dele)", hvis nogle af de skjulte materialer mangler dele.</summary>
        private static string MoreMissing(List<Item> tags)
        {
            int n = tags.Skip(MaxLabels).Count(i => i.PartsMissing);
            return n > 0 ? " (" + n + " mangler dele)" : "";
        }

        private void PaintEdge(Graphics g, Rectangle all)
        {
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

        // ---------- Kompakt visning: én smal stribe ----------

        private string CompactText(out Color dot, out Color ink)
        {
            ink = TextLight;
            if (st.BindError != null) { dot = Red; return "Cicero kan ikke forbinde"; }
            if (!st.ReaderOk) { dot = Red; return "Ingen læser"; }
            dot = st.Listening ? DotGreen : Amber;
            Color warn = Color.FromArgb(0xff, 0xa0, 0xa8);
            if (message != null) { ink = warn; return message; }
            List<Item> items = Items();
            int missCount = items.Count(i => i.PartsMissing);
            if (missCount > 1) { ink = warn; return missCount + " sæt mangler dele"; }
            Item miss = items.FirstOrDefault(i => i.PartsMissing);
            if (miss != null)
            {
                ink = warn;
                return miss.Missing.Count == 1 ? "Mangler del " + miss.Missing[0] + " af " + miss.Num : "Mangler " + miss.Missing.Count + " dele";
            }
            if (st.StaffConnect) { ink = Color.FromArgb(0xf5, 0xc4, 0x6b); return "staffConnect kører"; }
            if (items.Count == 0) { ink = TextMuted; return "Læseren er tom"; }
            return items.Count + " på læseren";
        }

        private void PaintCompact(Graphics g, Rectangle all)
        {
            Color dot, ink;
            string text = CompactText(out dot, out ink);
            int lx = CompactLampsW(st.Gates != null ? st.Gates.Lamps.Count : 0);
            PaintLamps(g);
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, Px(lx + 11), Px(13), Px(8), Px(8));
            using (Font f = new Font("Segoe UI Semibold", 9.5f))
                TextRenderer.DrawText(g, text, f, new Rectangle(Px(lx + 25), 0, btnOff.Left - Px(lx + 29), all.Height), ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            bool enabled = HasReadTags && busy == 0;
            DrawButton(g, btnOff, "Fra", Green, Color.White, enabled, hover == 1, busy == 1, 9f);
            DrawButton(g, btnOn, "Til", Amber, Ink, enabled, hover == 2, busy == 2, 9f);
            PaintEdge(g, all);
        }

        /// <summary>I kompakt visning viser musen over striben bøgerne på læseren.</summary>
        private void UpdateTip()
        {
            string t = null;
            if (compact)
            {
                if (st.BindError != null) t = st.BindError;
                else if (!st.ReaderOk) t = st.ReaderText;
                else
                {
                    List<Item> items = Items();
                    t = items.Count == 0 ? "Læg en bog på læseren" : string.Join("\n", items.Select(i => i.Title + "  –  " + i.Alarm + (i.Parts.Length > 0 ? ", " + i.Parts : "")));
                    if (st.StaffConnect) t += "\n\nstaffConnect kører og kan tage læseren";
                }
                t += "\n\nHøjreklik for menuen";
            }
            if (t == tipText) return;
            tipText = t;
            tip.SetToolTip(this, t);
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
            TextRenderer.DrawText(g, t.Title, num, new Rectangle(x, (int)r.Y + Px(1), w, Px(19)), t.Muted ? InkMuted : Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, t.Alarm, small, new Rectangle(x, (int)r.Y + Px(19), w, Px(13)), InkMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (t.Parts.Length > 0)
                using (Font f = t.PartsMissing ? new Font(small, FontStyle.Bold) : null)
                    TextRenderer.DrawText(g, t.Parts, f ?? small, new Rectangle(x, (int)r.Y + Px(19), w, Px(13)), t.PartsMissing ? Color.FromArgb(0xc2, 0x3b, 0x22) : Ink,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawButton(Graphics g, Rectangle r, string text, Color fill, Color ink, bool enabled, bool hot, bool working, float pt = 9.5f)
        {
            Color c = !enabled && !working ? Mix(Bg, fill, 0.35f) : hot ? Mix(fill, Color.White, 0.14f) : fill;
            Color tc = !enabled && !working ? Mix(Bg, ink, 0.55f) : ink;
            using (GraphicsPath p = Round(r, Px(5)))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
            using (Font f = new Font("Segoe UI Semibold", pt))
                TextRenderer.DrawText(g, working ? (compact ? "…" : "Vent …") : text, f, r, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ---------- Gatelamper ----------

        private static readonly Color LampRed = Color.FromArgb(0xff, 0x4d, 0x5e);
        private static readonly Color LampYellow = Color.FromArgb(0xf7, 0xb7, 0x2b);

        /// <summary>Rød blinker hurtigt, gul langsomt.</summary>
        private static bool Lit(LampState state)
        {
            long ms = Environment.TickCount & int.MaxValue;
            return state == LampState.Red ? (ms / 250) % 2 == 0 : (ms / 600) % 2 == 0;
        }

        private static string LampKey(GateView g)
        {
            if (g == null) return "";
            return g.Connected + ":" + string.Join(",", g.Lamps.Select(l => l.Gate + l.Name + l.State
                + (l.State == LampState.Red || l.State == LampState.Yellow ? (Lit(l.State) ? "+" : "-") : "")));
        }

        private Color LampColor(LampView l, out bool glow)
        {
            glow = false;
            switch (l.State)
            {
                case LampState.Red: glow = Lit(l.State); return glow ? LampRed : Mix(Bg, LampRed, 0.32f);
                case LampState.Yellow: glow = Lit(l.State); return glow ? LampYellow : Mix(Bg, LampYellow, 0.32f);
                case LampState.Idle: return Mix(Bg, Green, 0.85f);
                default: return Mix(Bg, Grey, 0.38f);
            }
        }

        private RectangleF LampBar(int i, int count)
        {
            if (compact) return new RectangleF(Px(CLampX + i * (CLampW + CLampGap)), Px(CLampTop), Px(CLampW), Px(CompactH - 2 * CLampTop));
            float lw = (BaseWidth - 2 * M - (count - 1) * LampGap) / (float)count;
            return new RectangleF(Px(M + i * (lw + LampGap)), Px(LampTop), Px(lw), Px(LampH));
        }

        private void PaintLamps(Graphics g)
        {
            if (st.Gates == null) return;
            List<LampView> lamps = st.Gates.Lamps;
            for (int i = 0; i < lamps.Count; i++)
            {
                RectangleF r = LampBar(i, lamps.Count);
                bool glow;
                Color c = LampColor(lamps[i], out glow);
                float rad = Math.Min(r.Width, r.Height) / 2f;
                if (glow)
                {
                    RectangleF gr = RectangleF.Inflate(r, Px(2.5f), Px(2.5f));
                    using (GraphicsPath p = Round(gr, rad + Px(2.5f)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(80, c))) g.FillPath(b, p);
                }
                using (GraphicsPath p = Round(r, rad))
                {
                    using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
                    if (i == hoverLamp) using (Pen pen = new Pen(Color.FromArgb(150, 255, 255, 255), Px(1))) g.DrawPath(pen, p);
                }
            }
        }

        private int LampAt(Point p)
        {
            for (int i = 0; i < lampRects.Length; i++) if (lampRects[i].Contains(p)) return i;
            return -1;
        }

        private LampView HoveredLamp
        {
            get { return st.Gates != null && hoverLamp >= 0 && hoverLamp < st.Gates.Lamps.Count ? st.Gates.Lamps[hoverLamp] : null; }
        }

        private string TipKeyFor(LampView l)
        {
            if (l == null) return null;
            return l.Gate + l.Name + st.Gates.Connected + string.Join(",", l.Recent.Select(a => a.Seq + a.StatusText + a.Title));
        }

        private void SetHoverLamp(int i)
        {
            if (i == hoverLamp) return;
            hoverLamp = i;
            Invalidate();
            if (i < 0) { lampTip.Hide(this); lampTipKey = null; tip.Active = true; return; }
            tip.Hide(this);
            tip.Active = false;
            ShowLampTip();
        }

        private void ShowLampTip()
        {
            LampView l = HoveredLamp;
            if (l == null) return;
            lampTipKey = TipKeyFor(l);
            Size sz = LampTipSize();
            Rectangle lr = lampRects[hoverLamp];
            Point at = compact ? new Point(lr.X, Height + Px(4)) : new Point(lr.X, lr.Bottom + Px(2));
            Point scr = PointToScreen(at);
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (scr.Y + sz.Height > wa.Bottom) scr.Y = PointToScreen(Point.Empty).Y - sz.Height - Px(4);
            if (scr.X + sz.Width > wa.Right) scr.X = wa.Right - sz.Width;
            if (scr.X < wa.Left) scr.X = wa.Left;
            Point c = PointToClient(scr);
            lampTip.Show(" ", this, c.X, c.Y, 30000);
        }

        /// <summary>Opdaterer musens forklaring, hvis der er kommet en ny alarm, mens musen står over lampen.</summary>
        private void RefreshLampTip()
        {
            if (hoverLamp < 0) return;
            if (st.Gates == null || hoverLamp >= lampRects.Length) { SetHoverLamp(-1); return; }
            string k = TipKeyFor(HoveredLamp);
            if (k != lampTipKey) ShowLampTip();
        }

        private const int TipW = 320, TipPad = 10, TipHead = 22, TipRow = 38, TipFoot = 20;

        private Size LampTipSize()
        {
            LampView l = HoveredLamp;
            int rows = l == null ? 0 : l.Recent.Count;
            int h = TipPad + TipHead + (rows == 0 ? 20 : rows * TipRow) + TipFoot + TipPad - 4;
            return new Size(Px(TipW), Px(h));
        }

        private void DrawLampTip(Graphics g, Rectangle b)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush br = new SolidBrush(Paper)) g.FillRectangle(br, b);
            using (Pen p = new Pen(Edge)) g.DrawRectangle(p, b.X, b.Y, b.Width - 1, b.Height - 1);
            LampView l = HoveredLamp;
            if (l == null) return;
            int x = Px(TipPad + 2), w = b.Width - Px(2 * TipPad + 4), y = Px(TipPad);
            using (Font f = new Font("Segoe UI Semibold", 10f))
                TextRenderer.DrawText(g, l.Name, f, new Rectangle(x, y, w, Px(TipHead - 2)), Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += Px(TipHead);
            using (Font small = new Font("Segoe UI", 8.5f))
            using (Font bold = new Font("Segoe UI Semibold", 9f))
            using (Font time = new Font("Bahnschrift", 9.5f))
            {
                if (l.Recent.Count == 0)
                {
                    string none = st.Gates.Connected ? "Ingen alarmer, siden RFIDlibra startede" : "Ingen forbindelse til gatetrackeren";
                    TextRenderer.DrawText(g, none, small, new Rectangle(x, y, w, Px(18)), InkMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    y += Px(20);
                }
                foreach (GateAlarm a in l.Recent)
                {
                    Color c = AlarmWindow.AlarmColor(a);
                    using (SolidBrush br = new SolidBrush(c)) g.FillEllipse(br, x, y + Px(5), Px(9), Px(9));
                    int tx = x + Px(16), tw = Px(40);
                    TextRenderer.DrawText(g, a.Time.ToString("HH:mm", CultureInfo.InvariantCulture), time, new Rectangle(tx, y, tw, Px(18)), Ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    int x2 = tx + tw, w2 = b.Width - x2 - Px(TipPad);
                    TextRenderer.DrawText(g, a.TitleLine, bold, new Rectangle(x2, y, w2, Px(18)), Ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    Color sc = a.Loaned == true ? Color.FromArgb(0xa9, 0x6c, 0x00) : a.Done || a.ShownAt != null ? Color.FromArgb(0xc2, 0x3b, 0x22) : InkMuted;
                    string line2 = a.StatusText + (a.Barcode.Length > 0 ? "  ·  " + a.Barcode : "");
                    TextRenderer.DrawText(g, line2, small, new Rectangle(x2, y + Px(17), w2, Px(16)), sc,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    y += Px(TipRow);
                }
                TextRenderer.DrawText(g, "Dobbeltklik for alle alarmer", small, new Rectangle(x, y, w, Px(TipFoot)), InkMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // ---------- Mus ----------

        private int HitTest(Point p)
        {
            if (btnOff.Contains(p)) return 1;
            if (btnOn.Contains(p)) return 2;
            if (!btnMenu.IsEmpty && btnMenu.Contains(p)) return 3;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHoverLamp(LampAt(e.Location));
            int h = HitTest(e.Location);
            bool clickable = h == 3 || (h != 0 && HasReadTags && busy == 0) || hoverLamp >= 0;
            Cursor = clickable ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHoverLamp(-1);
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
            int lamp = LampAt(e.Location);
            if (lamp >= 0 && st.Gates != null && lamp < st.Gates.Lamps.Count)
            {
                if (e.Clicks >= 2) { SetHoverLamp(-1); app.OpenAlarms(st.Gates.Lamps[lamp].Gate); }
                return;   // lamperne trækker ikke i vinduet, så dobbeltklik kan nås
            }
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
            if (disposing) { timer.Dispose(); flashTimer.Dispose(); tip.Dispose(); lampTip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
