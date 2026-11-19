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
    /// <summary>Det, værktøjsvinduet skal bruge (App, eller en testopstilling).</summary>
    public interface IToolBackend : IWidgetSource
    {
        Worker.ToolResult ToolAlarm(string mac, bool on);
        Worker.ToolResult ToolReset(string mac);
        Worker.ToolResult ToolProgram(string mac, string barcode, int seq, int num, string library, bool confirmed);
        string Library { get; }
        void SetLibrary(string lib);
    }

    /// <summary>Flad knap med runde hjørner (tegnes selv, så den passer til resten).</summary>
    public sealed class FlatBtn : Control
    {
        public Color Fill = Color.FromArgb(0x1d, 0x4a, 0x57);
        public Color Ink = Color.White;
        public Color Border = Color.Empty;
        private bool hot, down;

        public FlatBtn()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; OnClick(EventArgs.Empty); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color f = Fill, ink = Ink, b = Border;
            if (!Enabled) { f = Look.Mix(BackColor, Fill, Fill.A == 0 ? 0 : 0.35f); ink = Look.Mix(BackColor, Ink, 0.45f); b = b.IsEmpty ? b : Look.Mix(BackColor, Border, 0.4f); }
            else if (down) f = Look.Mix(Fill, Color.Black, 0.12f);
            else if (hot) f = Fill.A == 0 ? Color.FromArgb(18, 0, 0, 0) : Look.Mix(Fill, Color.White, 0.12f);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (GraphicsPath p = Look.Round(r, Height * 0.16f))
            {
                if (f.A > 0) using (SolidBrush br = new SolidBrush(f)) g.FillPath(br, p);
                if (!b.IsEmpty) using (Pen pen = new Pen(b, 1.2f)) g.DrawPath(pen, p);
            }
            if (Focused && ShowFocusCues)
                using (Pen pen = new Pen(Look.Amber, 2f))
                using (GraphicsPath p = Look.Round(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), Height * 0.16f)) g.DrawPath(pen, p);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Fælles farver og tegnehjælp.</summary>
    public static class Look
    {
        public static readonly Color Petrol = Color.FromArgb(0x1d, 0x4a, 0x57);
        public static readonly Color PetrolDark = Color.FromArgb(0x16, 0x3c, 0x47);
        public static readonly Color Light = Color.FromArgb(0xf3, 0xf5, 0xf4);
        public static readonly Color Ink = Color.FromArgb(0x17, 0x26, 0x2b);
        public static readonly Color InkMuted = Color.FromArgb(0x5b, 0x6b, 0x70);
        public static readonly Color TextLight = Color.FromArgb(0xf2, 0xf6, 0xf7);
        public static readonly Color TextMuted = Color.FromArgb(0x9f, 0xbb, 0xc2);
        public static readonly Color Amber = Color.FromArgb(0xe3, 0x9b, 0x1c);
        public static readonly Color Green = Color.FromArgb(0x2f, 0x9e, 0x63);
        public static readonly Color Grey = Color.FromArgb(0xa5, 0xb0, 0xb3);
        public static readonly Color Red = Color.FromArgb(0xc2, 0x3b, 0x22);
        public static readonly Color DotGreen = Color.FromArgb(0x3c, 0xc7, 0x7a);
        public static readonly Color Select = Color.FromArgb(0x8f, 0xdc, 0xf0);

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(1, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static string AlarmWord(int? afi)
        {
            return afi == TagState.AFI_ON ? "Alarm til" : afi == TagState.AFI_OFF ? "Alarm fra" : afi == null ? "Alarm ukendt"
                : afi == 0 ? "Ingen alarm sat" : "Alarm 0x" + afi.Value.ToString("X2");
        }

        public static Color StripColor(int? afi)
        {
            return afi == TagState.AFI_ON ? Amber : afi == TagState.AFI_OFF ? Green : Grey;
        }

        public static string Title(TagView t)
        {
            switch (t.Kind)
            {
                case "unread": return "Læser …";
                case "blank": return "Tom chip";
                case "unknown": return "Ukendt indhold";
                default: return t.Barcode.Length > 0 ? t.Barcode : "Intet nummer";
            }
        }
    }

    /// <summary>
    /// Værktøj til tags: et fast vindue (kan ikke ændre størrelse, ingen rullebjælke).
    /// Venstre: chippene på læseren og den valgte chip. Højre: programmering - også af hele sæt på én gang.
    /// </summary>
    public sealed class ToolWindow : Form
    {
        private const int W = 700, H = 440, Split = 330;
        private const int MaxLabels = 4, LabelH = 36, LabelGap = 6, LabelsTop = 46;
        private const int CardTop = 238, CardH = 186;
        private const int RX = Split + 24, RW = W - Split - 48;
        private const int BoxTop = 272, BoxH = 152;

        private readonly IToolBackend app;
        private readonly Timer timer = new Timer { Interval = 250 };
        private float s = 1f;
        private WidgetState st = new WidgetState();
        private string key = "";
        private string selected;          // valgt chip (mac)
        private string cardMsg; private bool cardMsgOk; private DateTime cardMsgUntil;
        private bool chipBusy;

        // Højre side
        private readonly Panel inputHost = new Panel();
        private readonly TextBox inBarcode = new TextBox();
        private readonly FlatBtn bMinus = new FlatBtn(), bPlus = new FlatBtn(), bProgram = new FlatBtn();
        private readonly FlatBtn bCancel = new FlatBtn(), bRetry = new FlatBtn();
        private readonly LinkLabel libLink = new LinkLabel();
        // Kortet med den valgte chip
        private readonly FlatBtn bOn = new FlatBtn(), bOff = new FlatBtn(), bReset = new FlatBtn(), bCopy = new FlatBtn();
        private readonly TextBox cardNum = new TextBox();   // materialenummeret i kortet - kan markeres og kopieres
        private string lastAutoBc;                           // nummer, der sidst blev hentet fra en chip ind i skrivefeltet
        private string autoKey;                              // chippene (uid, indhold), som skrivefeltet sidst blev udfyldt efter
        private bool numAuto;                                // antal dele kom fra chippene, ikke fra brugeren
        private HashSet<string> noteUids;                    // chippene, der lå der, da "programmeret"-beskeden kom
        private string busyText;                             // fx "Nulstiller 3 af 7 …"

        private int? numUser;
        private Armed armed;
        private string doneMsg, doneCls, doneText; private DateTime doneUntil;   // besked, når der ikke programmeres

        private sealed class Job { public TagView T; public int Seq; }

        private sealed class Armed
        {
            public string Barcode;
            public int Num;
            public Dictionary<string, int> Done = new Dictionary<string, int>();  // chip -> delnummer
            public List<Job> Batch = new List<Job>();
            public bool Busy, Asking, Confirmed;
            public string Error;
            public HashSet<string> ErrorMacs = new HashSet<string>();
        }

        public ToolWindow(IToolBackend app)
        {
            this.app = app;
            Text = "RFIDlibra – Værktøj til tags";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Look.Light;
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
            ClientSize = new Size(Px(W), Px(H));
            Location = LoadPosition();

            BuildControls();
            timer.Tick += (o, e) => Tick();
            timer.Start();
            Tick();
        }

        private int Px(float v) { return (int)Math.Round(v * s); }
        private Rectangle R(int x, int y, int w, int h) { return new Rectangle(Px(x), Px(y), Px(w), Px(h)); }

        // ---------- Position ----------

        private Point LoadPosition()
        {
            string[] p = AppInfo.Load("vaerktoej.txt").Split(';');
            int x, y;
            if (p.Length >= 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)
                && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(new Point(x + 60, y + 20))))
                return new Point(x, y);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (WindowState == FormWindowState.Normal)
                AppInfo.Save("vaerktoej.txt", Location.X.ToString(CultureInfo.InvariantCulture) + ";" + Location.Y.ToString(CultureInfo.InvariantCulture));
            timer.Stop();
            base.OnFormClosed(e);
        }

        // ---------- Kontroller ----------

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        private void Btn(FlatBtn b, string text, Rectangle r, Color fill, Color ink, Color back, Color border, float pt, EventHandler click)
        {
            b.Text = text; b.Bounds = r; b.Fill = fill; b.Ink = ink; b.BackColor = back; b.Border = border;
            b.Font = new Font("Segoe UI Semibold", pt);
            b.Click += click;
            Controls.Add(b);
        }

        private void BuildControls()
        {
            // Materialenummer
            inputHost.Bounds = R(RX, 82, RW, 42);
            inputHost.BackColor = Color.White;
            inputHost.Paint += (o, e) =>
            {
                Color c = inBarcode.Focused ? Look.Petrol : Color.FromArgb(0xc5, 0xd0, 0xd2);
                using (Pen p = new Pen(c, inBarcode.Focused ? 2 : 1)) e.Graphics.DrawRectangle(p, 0, 0, inputHost.Width - 1, inputHost.Height - 1);
            };
            inputHost.Click += (o, e) => inBarcode.Focus();
            inBarcode.BorderStyle = BorderStyle.None;
            inBarcode.Font = new Font("Bahnschrift", 15f);
            inBarcode.MaxLength = 16;
            inBarcode.BackColor = Color.White;
            inBarcode.ForeColor = Look.Ink;
            inBarcode.Width = inputHost.Width - Px(24);
            inBarcode.Location = new Point(Px(12), (inputHost.Height - inBarcode.PreferredHeight) / 2);
            inBarcode.GotFocus += (o, e) => inputHost.Invalidate();
            inBarcode.LostFocus += (o, e) => inputHost.Invalidate();
            inBarcode.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Arm(); } };
            inputHost.Controls.Add(inBarcode);
            Controls.Add(inputHost);
            inBarcode.HandleCreated += (o, e) => { try { SendMessage(inBarcode.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, "Scan eller skriv"); } catch { } };

            Color line = Color.FromArgb(0xc5, 0xd0, 0xd2);
            Btn(bMinus, "−", R(RX, 158, 38, 38), Color.White, Look.Ink, Look.Light, line, 13f, (o, e) => ChangeNum(-1));
            Btn(bPlus, "+", R(RX + 88, 158, 38, 38), Color.White, Look.Ink, Look.Light, line, 13f, (o, e) => ChangeNum(+1));
            Btn(bProgram, "Programmér", R(RX + RW - 150, 157, 150, 40), Look.Petrol, Color.White, Look.Light, Color.Empty, 11f, (o, e) => Arm());
            Btn(bCancel, "Annullér", R(RX + 16, BoxTop + BoxH - 46, 100, 32), Color.White, Look.Ink, Look.Light, line, 9.5f, (o, e) => Disarm(null));
            Btn(bRetry, "Prøv igen", R(RX + 124, BoxTop + BoxH - 46, 100, 32), Look.Petrol, Color.White, Look.Light, Color.Empty, 9.5f, (o, e) =>
            {
                if (armed != null) { armed.Error = null; armed.ErrorMacs.Clear(); Step(); }
            });

            libLink.AutoSize = true;
            libLink.BackColor = Look.Light;
            libLink.LinkColor = Look.Petrol;
            libLink.ActiveLinkColor = Look.Petrol;
            libLink.Font = new Font("Segoe UI", 9f);
            libLink.LinkClicked += (o, e) => { if (AskLibrary()) Render(); };
            Controls.Add(libLink);

            // Den valgte chip
            int bw = (Split - 32 - 28 - 16) / 3;
            int by = CardTop + CardH - 50;
            Btn(bOn, "Alarm til", R(30, by, bw, 34), Look.Amber, Look.Ink, Color.White, Color.Empty, 9.5f, (o, e) => ChipAction("on"));
            Btn(bOff, "Alarm fra", R(30 + bw + 8, by, bw, 34), Look.Green, Color.White, Color.White, Color.Empty, 9.5f, (o, e) => ChipAction("off"));
            Btn(bCopy, "Kopiér", R(Split - 16 - 14 - 64, CardTop + 14, 64, 26), Color.White, Look.Petrol, Color.White, Color.FromArgb(0xc5, 0xd0, 0xd2), 9f, (o, e) => CopyNumber());
            cardNum.ReadOnly = true;
            cardNum.BorderStyle = BorderStyle.None;
            cardNum.BackColor = Color.White;
            cardNum.ForeColor = Look.Ink;
            cardNum.Font = new Font("Bahnschrift", 17f);
            cardNum.TabStop = false;
            cardNum.Bounds = R(38, CardTop + 12, Split - 32 - 22 - 14 - 64 - 8, 30);
            Controls.Add(cardNum);
            Btn(bReset, "Nulstil", R(30 + 2 * (bw + 8), by, bw, 34), Color.FromArgb(0, 0, 0, 0), Look.Red, Color.White, Color.FromArgb(0xe3, 0xb4, 0xab), 9.5f, (o, e) => ChipAction("reset"));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            inBarcode.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && armed != null && !armed.Busy) { e.Handled = true; Disarm("Programmering annulleret"); }
        }

        // ---------- Data ----------

        private List<TagView> Tags()
        {
            return st.ReaderOk ? st.Tags.OrderBy(t => t.Kind == "unread" ? 1 : 0).ThenBy(t => t.Uid, StringComparer.Ordinal).ToList() : new List<TagView>();
        }

        private List<TagView> Ready() { return Tags().Where(t => t.Kind != "unread").ToList(); }

        private TagView Selected()
        {
            List<TagView> tags = Tags();
            TagView t = tags.FirstOrDefault(x => x.Uid == selected);
            if (t == null && tags.Count == 1) t = tags[0];
            return t;
        }

        /// <summary>Flere chips på læseren, og ingen er valgt alene: kortet og knapperne gælder dem alle.</summary>
        private bool AllMode() { return selected == null && Tags().Count > 1; }

        /// <summary>Materialenummeret, hvis alle chippene er dele af samme sæt - ellers null.</summary>
        private static string SetBarcode(List<TagView> rd)
        {
            if (rd.Count < 2 || rd.Any(t => t.Kind != "danish" || t.Barcode.Length == 0 || t.Num < 2)) return null;
            return rd.Select(t => t.Barcode).Distinct().Count() == 1 && rd.Select(t => t.Num).Distinct().Count() == 1 ? rd[0].Barcode : null;
        }

        private void Tick()
        {
            if (cardMsg != null && DateTime.Now > cardMsgUntil) { cardMsg = null; key = ""; }
            if (doneMsg != null && DateTime.Now > doneUntil) { doneMsg = null; key = ""; }
            WidgetState n = app.GetWidgetState();
            string k = n.ReaderOk + "|" + n.ReaderText + "|" + n.Listening + "|" + app.Library + "|"
                + string.Join(",", n.Tags.Select(t => t.Uid + t.Kind + t.Barcode + t.Seq + "/" + t.Num + t.Afi + t.RawLibrary));
            st = n;
            if (doneMsg != null && noteUids != null && !noteUids.SetEquals(n.ReaderOk ? n.Tags.Select(t => t.Uid) : Enumerable.Empty<string>()))
            {
                doneMsg = null; noteUids = null; key = "";
            }
            if (k != key) { key = k; Render(); }
            else Step();
        }

        // ---------- Antal dele ----------

        private int MinParts() { return Math.Max(1, Ready().Count); }
        private int PartsValue() { return armed != null ? armed.Num : Math.Min(99, Math.Max(numUser ?? 1, MinParts())); }

        private void ChangeNum(int d)
        {
            int v = PartsValue() + d;
            numAuto = false;
            if (d < 0) numUser = v <= MinParts() ? (int?)null : v;
            else numUser = Math.Min(99, v);
            Render();
        }

        private string NumHint()
        {
            int v = PartsValue(), n = Ready().Count;
            if (armed != null) return "";
            if (n > 1) return "De " + n + " chips på læseren bliver del 1–" + n + (v > n ? ". Du bliver bedt om de sidste " + (v - n) + " undervejs." : ".");
            if (v > 1) return n > 0 ? "Chippen bliver del 1. Du bliver bedt om de næste undervejs." : "Læg chippene på læseren, alle på én gang eller én ad gangen.";
            return "";
        }

        // ---------- Opdatering af kontroller ----------

        private void Render()
        {
            TagView sel = Selected();
            if (sel == null) selected = null;
            bool idle = !chipBusy && (armed == null || !armed.Busy);
            string numText = null;
            if (AllMode())
            {
                List<TagView> rd = Ready();
                bool ok = idle && Tags().Count == rd.Count && rd.Count > 0;
                bOn.Enabled = ok && rd.Any(t => t.Afi != TagState.AFI_ON);
                bOff.Enabled = ok && rd.Any(t => t.Afi != TagState.AFI_OFF);
                bReset.Enabled = ok && rd.Any(t => t.Kind != "blank");
                bOn.Visible = bOff.Visible = bReset.Visible = true;
                numText = Tags().Count == rd.Count ? SetBarcode(rd) : null;
            }
            else
            {
                bool chipOk = sel != null && sel.Kind != "unread" && idle;
                bOn.Enabled = chipOk && sel.Afi != TagState.AFI_ON;
                bOff.Enabled = chipOk && sel.Afi != TagState.AFI_OFF;
                bReset.Enabled = chipOk && sel.Kind != "blank";
                bOn.Visible = bOff.Visible = bReset.Visible = sel != null;
                if (sel != null && sel.Kind == "danish" && sel.Barcode.Length > 0) numText = sel.Barcode;
            }
            bCopy.Visible = cardNum.Visible = numText != null;
            if (numText != null && cardNum.Text != numText) cardNum.Text = numText;
            AutoFill();

            int v = PartsValue();
            bMinus.Enabled = armed == null && v > MinParts();
            bPlus.Enabled = armed == null && v < 99;
            bProgram.Enabled = armed == null;
            inBarcode.ReadOnly = armed != null;

            string lib = app.Library;
            string libText = lib.Length > 0 ? "Nye chips får biblioteksnummer DK-" + lib : "Biblioteksnummeret er ikke sat.";
            libLink.Text = lib.Length > 0 ? "skift" : "Sæt det nu";
            Size ts = TextRenderer.MeasureText(libText, libLink.Font);
            libLink.Location = new Point(Px(RX) + ts.Width + Px(2), Px(242));

            Step();
            Invalidate();
        }

        // ---------- Tegning ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Look.Light);
            PaintPad(g);
            PaintRight(g);
        }

        private void PaintPad(Graphics g)
        {
            Rectangle pad = R(0, 0, Split, H);
            using (LinearGradientBrush b = new LinearGradientBrush(pad, Look.Petrol, Look.PetrolDark, LinearGradientMode.Vertical)) g.FillRectangle(b, pad);
            using (Pen p = new Pen(Color.FromArgb(24, 255, 255, 255), Px(1)))
                for (int i = 1; i <= 4; i++)
                {
                    int r = Px(80 * i);
                    g.DrawEllipse(p, Px(Split - 40) - r, Px(150) - r, 2 * r, 2 * r);
                }

            // Overskrift og status
            using (Font f = new Font("Segoe UI Semibold", 11f))
                TextRenderer.DrawText(g, "På læseren", f, R(16, 12, 150, 24), Look.TextLight, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Color dot = !st.ReaderOk ? Color.FromArgb(0xdc, 0x35, 0x45) : st.Listening ? Look.DotGreen : Look.Amber;
            string stat = !st.ReaderOk ? "Ingen læser" : st.Listening ? "Klar" : "Klar, Cicero lytter ikke";
            using (Font f = new Font("Segoe UI", 8.5f))
            {
                Size sz = TextRenderer.MeasureText(stat, f);
                int x = Px(Split - 16) - sz.Width;
                TextRenderer.DrawText(g, stat, f, new Rectangle(x, Px(12), sz.Width, Px(24)), Look.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, x - Px(14), Px(20), Px(8), Px(8));
            }

            // Chippene
            List<TagView> tags = Tags();
            TagView sel = Selected();
            int y = LabelsTop;
            if (tags.Count == 0)
            {
                RectangleF r = new RectangleF(Px(16), Px(y), Px(Split - 32), Px(LabelH * 2));
                using (Pen p = new Pen(Color.FromArgb(70, 255, 255, 255), Px(1)) { DashStyle = DashStyle.Dash })
                using (GraphicsPath gp = Look.Round(r, Px(5))) g.DrawPath(p, gp);
                using (Font f = new Font("Segoe UI", 9.5f))
                    TextRenderer.DrawText(g, st.ReaderOk ? "Læg en chip på læseren" : (st.ReaderText.Length > 0 ? st.ReaderText : "Sæt læseren i USB"), f,
                        Rectangle.Round(r), Look.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            using (Font num = new Font("Bahnschrift", 12f))
            using (Font small = new Font("Segoe UI", 8f))
                for (int i = 0; i < Math.Min(tags.Count, MaxLabels); i++)
                {
                    TagView t = tags[i];
                    RectangleF r = new RectangleF(Px(16), Px(y), Px(Split - 32), Px(LabelH));
                    bool isSel = sel != null && sel.Uid == t.Uid && tags.Count > 1;
                    using (GraphicsPath p = Look.Round(r, Px(4)))
                    {
                        using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, p);
                        Region keep = g.Clip;
                        g.SetClip(p);
                        using (SolidBrush b = new SolidBrush(Look.StripColor(t.Afi))) g.FillRectangle(b, r.X, r.Y, Px(6), r.Height);
                        g.Clip = keep;
                        if (isSel) using (Pen pen = new Pen(Look.Select, Px(3))) g.DrawPath(pen, p);
                    }
                    bool muted = !(t.Kind == "danish" && t.Barcode.Length > 0);
                    int x = (int)r.X + Px(16), w = (int)r.Width - Px(26);
                    TextRenderer.DrawText(g, Look.Title(t), num, new Rectangle(x, (int)r.Y, w, (int)r.Height), muted ? Look.InkMuted : Look.Ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    string right = t.Kind == "danish" && t.Num > 1 ? "Del " + t.Seq + " af " + t.Num : t.Kind == "unread" ? "" : Look.AlarmWord(t.Afi);
                    TextRenderer.DrawText(g, right, small, new Rectangle(x, (int)r.Y, w, (int)r.Height), Look.InkMuted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    y += LabelH + LabelGap;
                }
            if (tags.Count > MaxLabels)
                using (Font f = new Font("Segoe UI", 8.5f))
                    TextRenderer.DrawText(g, "+" + (tags.Count - MaxLabels) + " mere - tag nogle af for at se dem", f, R(16, y - 2, Split - 32, 20), Look.TextMuted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            PaintCard(g, sel, tags.Count);
        }

        private void PaintCard(Graphics g, TagView t, int count)
        {
            RectangleF r = new RectangleF(Px(16), Px(CardTop), Px(Split - 32), Px(CardH));
            if (t == null && count > 1)
            {
                PaintAllCard(g, r);
                return;
            }
            if (t == null)
            {
                using (GraphicsPath p = Look.Round(r, Px(6)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) g.FillPath(b, p);
                using (Font f = new Font("Segoe UI", 9.5f))
                    TextRenderer.DrawText(g, count > 1 ? "Klik på en chip ovenfor for at se den, ændre alarmen eller nulstille den." : "Her ses chippen, når den ligger på læseren.",
                        f, Rectangle.Round(RectangleF.Inflate(r, -Px(24), 0)), Look.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            using (GraphicsPath p = Look.Round(r, Px(6)))
            {
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, p);
                Region keep = g.Clip;
                g.SetClip(p);
                using (SolidBrush b = new SolidBrush(Look.StripColor(t.Afi))) g.FillRectangle(b, r.X, r.Y, Px(8), r.Height);
                g.Clip = keep;
            }
            int x = (int)r.X + Px(22), w = (int)r.Width - Px(36), y = (int)r.Y;
            bool muted = !(t.Kind == "danish" && t.Barcode.Length > 0);
            if (!(t.Kind == "danish" && t.Barcode.Length > 0))
            using (Font f = new Font("Bahnschrift", 17f))
                TextRenderer.DrawText(g, Look.Title(t), f, new Rectangle(x, y + Px(10), w, Px(30)), muted ? Look.InkMuted : Look.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string l1, l2;
            if (t.Kind == "danish")
            {
                l1 = (t.Num > 1 ? "Del " + t.Seq + " af " + t.Num : "Én del") + (t.Library.Length > 0 ? "     " + t.Library : "");
                l2 = Look.AlarmWord(t.Afi) + (t.Note.Length > 0 ? "     " + t.Note : "");
            }
            else if (t.Kind == "unread") { l1 = t.Note.Length > 0 ? t.Note : "Chippen læses …"; l2 = ""; }
            else { l1 = t.Maker; l2 = Look.AlarmWord(t.Afi); }
            using (Font f = new Font("Segoe UI", 9f))
            {
                TextRenderer.DrawText(g, l1, f, new Rectangle(x, y + Px(44), w, Px(18)), Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, l2, f, new Rectangle(x, y + Px(64), w, Px(18)), Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            string msg = chipBusy ? busyText ?? "Vent …" : cardMsg;
            if (msg != null)
                using (Font f = new Font("Segoe UI Semibold", 9f))
                    TextRenderer.DrawText(g, msg, f, new Rectangle(x, y + Px(88), w, Px(36)), chipBusy ? Look.InkMuted : cardMsgOk ? Look.Green : Look.Red,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        /// <summary>Kortet, når det gælder alle chips på læseren (et sæt eller flere materialer).</summary>
        private void PaintAllCard(Graphics g, RectangleF r)
        {
            List<TagView> all = Tags(), rd = Ready();
            bool reading = all.Count > rd.Count;
            int on = rd.Count(t => t.Afi == TagState.AFI_ON), off = rd.Count(t => t.Afi == TagState.AFI_OFF), none = rd.Count(t => t.Afi == 0), other = rd.Count - on - off - none;
            int? stripAfi = on == rd.Count && rd.Count > 0 ? TagState.AFI_ON : off == rd.Count && rd.Count > 0 ? TagState.AFI_OFF : (int?)null;
            using (GraphicsPath p = Look.Round(r, Px(6)))
            {
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillPath(b, p);
                Region keep = g.Clip;
                g.SetClip(p);
                using (SolidBrush b = new SolidBrush(Look.StripColor(stripAfi))) g.FillRectangle(b, r.X, r.Y, Px(8), r.Height);
                g.Clip = keep;
            }
            int x = (int)r.X + Px(22), w = (int)r.Width - Px(36), y = (int)r.Y;
            string bc = reading ? null : SetBarcode(rd);
            string l1, l2;
            if (reading) { l1 = "Chippene læses …"; l2 = ""; }
            else
            {
                if (bc != null)
                {
                    int num = rd[0].Num;
                    List<int> have = rd.Select(t => t.Seq).Distinct().ToList();
                    List<int> miss = Enumerable.Range(1, num).Where(i => !have.Contains(i)).ToList();
                    l1 = miss.Count == 0 ? (num == 2 ? "Begge dele" : "Alle " + num + " dele")
                        : have.Count + " af " + num + " dele – " + (miss.Count == 1 ? "del " + miss[0] + " mangler" : PartsText(miss, num).Replace(" af " + num, "").ToLowerInvariant() + " mangler");
                    if (rd[0].Library.Length > 0) l1 += "     " + rd[0].Library;
                }
                else
                {
                    int mats = rd.Where(t => t.Kind == "danish" && t.Barcode.Length > 0).Select(t => t.Barcode).Distinct().Count();
                    int blank = rd.Count(t => t.Kind == "blank");
                    l1 = (mats == 1 ? "1 materiale" : mats + " materialer") + (blank > 0 ? ", " + blank + (blank == 1 ? " tom chip" : " tomme chips") : "");
                }
                if (on == rd.Count) l2 = "Alarm til på alle";
                else if (off == rd.Count) l2 = "Alarm fra på alle";
                else
                {
                    List<string> parts = new List<string>();
                    if (on > 0) parts.Add("til på " + on);
                    if (off > 0) parts.Add("fra på " + off);
                    if (none > 0) parts.Add("ingen på " + none);
                    if (other > 0) parts.Add("anden/ukendt på " + other);
                    l2 = "Alarm " + string.Join(", ", parts);
                }
            }
            if (bc == null)
                using (Font f = new Font("Bahnschrift", 17f))
                    TextRenderer.DrawText(g, all.Count + " chips på læseren", f, new Rectangle(x, y + Px(10), w, Px(30)), Look.Ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using (Font f = new Font("Segoe UI", 9f))
            {
                TextRenderer.DrawText(g, l1, f, new Rectangle(x, y + Px(44), w, Px(18)), Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, l2, f, new Rectangle(x, y + Px(64), w, Px(18)), Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            string msg = chipBusy ? busyText ?? "Vent …" : cardMsg;
            bool hint = msg == null;
            if (hint) msg = "Knapperne gælder alle " + all.Count + ". Klik på en chip ovenfor for kun at vælge den.";
            using (Font f = new Font(hint ? "Segoe UI" : "Segoe UI Semibold", hint ? 8.5f : 9f))
                TextRenderer.DrawText(g, msg, f, new Rectangle(x, y + Px(88), w, Px(36)), hint || chipBusy ? Look.InkMuted : cardMsgOk ? Look.Green : Look.Red,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        private void PaintRight(Graphics g)
        {
            using (Font f = new Font("Segoe UI Semibold", 13f))
                TextRenderer.DrawText(g, "Programmér", f, R(RX, 20, RW, 28), Look.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (Font f = new Font("Segoe UI", 9f))
            {
                TextRenderer.DrawText(g, "Materialenummer", f, R(RX, 60, RW, 18), Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "Antal dele", f, R(RX, 136, RW, 18), Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, NumHint(), f, R(RX, 204, RW, 34), Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                string lib = app.Library;
                TextRenderer.DrawText(g, lib.Length > 0 ? "Nye chips får biblioteksnummer DK-" + lib : "Biblioteksnummeret er ikke sat.", f,
                    new Rectangle(Px(RX), Px(242), Px(RW), libLink.Height), lib.Length > 0 ? Look.InkMuted : Look.Red, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            using (Font f = new Font("Bahnschrift", 15f))
                TextRenderer.DrawText(g, PartsValue().ToString(), f, R(RX + 38, 158, 50, 38), Look.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            PaintBox(g);
        }

        // Forløbsfeltet: fast plads, så intet flytter sig
        private string boxCls, boxTitle, boxText;

        private void PaintBox(Graphics g)
        {
            Rectangle r = R(RX, BoxTop, RW, BoxH);
            if (boxTitle == null)
            {
                using (Font f = new Font("Segoe UI", 8.5f))
                    TextRenderer.DrawText(g, "Ligger der flere chips på læseren, programmeres de på én gang som del 1, 2, 3 osv. Det er tilfældigt, hvilken chip der bliver hvilken del. "
                        + "Skal fx cd 1 være del 1, så læg chippene på én ad gangen.\n\nAlarmen slås til, når en chip er programmeret. Har en chip allerede indhold, bliver du spurgt, før den overskrives.",
                        f, r, Look.InkMuted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return;
            }
            Color bg = BoxColor();
            using (GraphicsPath p = Look.Round(r, Px(8)))
            using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
            int x = r.X + Px(16), w = r.Width - Px(32), y = r.Y + Px(12);
            using (Font f = new Font("Segoe UI Semibold", 11.5f))
                TextRenderer.DrawText(g, boxTitle, f, new Rectangle(x, y, w, Px(24)), boxCls == "err" ? Look.Red : Look.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y += Px(30);
            Armed a = armed;
            if (a != null && a.Num > 1 && a.Num <= 14)
            {
                float dw = Math.Min(Px(26), (w - (a.Num - 1) * Px(5)) / (float)a.Num);
                HashSet<int> done = new HashSet<int>(a.Done.Values), now = a.Busy ? new HashSet<int>(a.Batch.Select(j => j.Seq)) : new HashSet<int>();
                for (int i = 1; i <= a.Num; i++)
                {
                    Color c = done.Contains(i) ? Look.Green : now.Contains(i) ? Look.Amber : Color.FromArgb(0xc3, 0xcf, 0xd1);
                    using (SolidBrush b = new SolidBrush(c))
                    using (GraphicsPath p = Look.Round(new RectangleF(x + (i - 1) * (dw + Px(5)), y, dw, Px(6)), Px(3))) g.FillPath(b, p);
                }
                y += Px(14);
            }
            using (Font f = new Font("Segoe UI", 9.5f))
                TextRenderer.DrawText(g, boxText ?? "", f, new Rectangle(x, y, w, r.Bottom - y - Px(50)), Look.Ink,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private Color BoxColor()
        {
            return boxCls == "err" ? Color.FromArgb(0xfb, 0xe9, 0xe6) : boxCls == "warn" ? Color.FromArgb(0xfd, 0xf1, 0xdc)
                : boxCls == "ok" ? Color.FromArgb(0xe2, 0xf3, 0xe8) : Color.FromArgb(0xe3, 0xed, 0xef);
        }

        private void ShowBox(string cls, string title, string text)
        {
            bool cancel = armed != null && !armed.Busy, retry = cls == "err" && armed != null && armed.Error != null;
            if (bCancel.Visible != cancel) bCancel.Visible = cancel;
            if (bRetry.Visible != retry) bRetry.Visible = retry;
            if (boxCls == cls && boxTitle == title && boxText == text) return;
            boxCls = cls; boxTitle = title; boxText = text;
            bCancel.BackColor = bRetry.BackColor = BoxColor();
            bCancel.Invalidate(); bRetry.Invalidate();
            Invalidate(R(RX, BoxTop, RW, BoxH));
        }

        private void Note(string cls, string title, string text, int seconds)
        {
            doneCls = cls; doneMsg = title; doneText = text; doneUntil = DateTime.Now.AddSeconds(seconds);
            noteUids = null;
            Step();
        }

        // ---------- Mus: vælg chip ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            List<TagView> tags = Tags();
            for (int i = 0; i < Math.Min(tags.Count, MaxLabels); i++)
                if (R(16, LabelsTop + i * (LabelH + LabelGap), Split - 32, LabelH).Contains(e.Location))
                {
                    // Klik på den valgte chip igen: tilbage til alle chips
                    selected = selected == tags[i].Uid && tags.Count > 1 ? null : tags[i].Uid;
                    cardMsg = null;
                    Render();
                    return;
                }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int n = Math.Min(Tags().Count, MaxLabels);
            bool over = n > 1 && Enumerable.Range(0, n).Any(i => R(16, LabelsTop + i * (LabelH + LabelGap), Split - 32, LabelH).Contains(e.Location));
            Cursor = over ? Cursors.Hand : Cursors.Default;
        }

        // ---------- Den valgte chip ----------

        private async void ChipAction(string what)
        {
            if (AllMode()) { BulkAction(what); return; }
            TagView t = Selected();
            if (t == null || chipBusy) return;
            if (what == "reset" && MessageBox.Show(this, "Nulstil chippen " + Look.Title(t) + "?\n\nMaterialenummer og alarm slettes, så chippen bliver tom.",
                "Nulstil chip", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            chipBusy = true;
            Render();
            string mac = t.Uid;
            Worker.ToolResult r = await Task.Run(() => what == "reset" ? app.ToolReset(mac) : app.ToolAlarm(mac, what == "on"));
            chipBusy = false;
            if (IsDisposed) return;
            cardMsg = r.Message;
            cardMsgOk = r.Ok;
            cardMsgUntil = DateTime.Now.AddSeconds(r.Ok ? 3 : 8);
            key = "";
            Tick();
        }

        /// <summary>Alarm til/fra eller nulstil på alle chips på læseren - et helt sæt eller mange materialer, fx ved kassation.</summary>
        private async void BulkAction(string what)
        {
            List<TagView> rd = Ready();
            if (chipBusy || rd.Count == 0 || Tags().Count != rd.Count) return;
            List<TagView> targets = what == "reset" ? rd.Where(t => t.Kind != "blank").ToList()
                : rd.Where(t => t.Afi != (what == "on" ? TagState.AFI_ON : TagState.AFI_OFF)).ToList();
            if (targets.Count == 0) return;
            if (what == "reset")
            {
                string bc = SetBarcode(rd);
                string which = bc != null ? (rd.Count == 2 ? "begge dele af " + bc : "alle " + rd.Count + " dele af " + bc)
                    : "alle " + targets.Count + " chips";
                if (MessageBox.Show(this, "Nulstil " + which + "?\n\nMaterialenummer og alarm slettes, så chippene bliver tomme.",
                    "Nulstil chips", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }
            string verb = what == "reset" ? "Nulstiller" : what == "on" ? "Slår alarm til på" : "Slår alarm fra på";
            chipBusy = true;
            cardMsg = null;
            List<string> fails = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                busyText = verb + " " + (i + 1) + " af " + targets.Count + " …";
                Render();
                string mac = targets[i].Uid, title = Look.Title(targets[i]);
                Worker.ToolResult r = await Task.Run(() => what == "reset" ? app.ToolReset(mac) : app.ToolAlarm(mac, what == "on"));
                if (IsDisposed) return;
                if (!r.Ok) fails.Add(title + ": " + r.Message);
            }
            chipBusy = false;
            busyText = null;
            int okCount = targets.Count - fails.Count;
            string done = what == "reset" ? "Nulstillet" : what == "on" ? "Alarm slået til på" : "Alarm slået fra på";
            cardMsg = fails.Count == 0 ? done + " " + (targets.Count == 1 ? "1 chip" : targets.Count + " chips") + " ✓"
                : done + " " + okCount + " af " + targets.Count + ". Fejl: " + fails[0] + (fails.Count > 1 ? " (+" + (fails.Count - 1) + " mere)" : "");
            cardMsgOk = fails.Count == 0;
            cardMsgUntil = DateTime.Now.AddSeconds(fails.Count == 0 ? 4 : 12);
            key = "";
            Tick();
        }

        private void CopyNumber()
        {
            TagView t = Selected();
            string num = t != null ? t.Barcode : AllMode() ? SetBarcode(Ready()) : null;
            if (string.IsNullOrEmpty(num)) return;
            try { Clipboard.SetText(num); cardMsg = "Kopieret ✓"; cardMsgOk = true; }
            catch (Exception e) { cardMsg = "Kunne ikke kopiere: " + e.Message; cardMsgOk = false; }
            cardMsgUntil = DateTime.Now.AddSeconds(2);
            Invalidate();
        }

        /// <summary>
        /// Ligger der en chip med materialenummer på læseren, kommer nummeret i skrivefeltet (markeret), og antal dele følger chippen.
        /// Feltet følger altid chippene: skiftes de, skiftes nummer og antal dele også - og tages de af, tømmes feltet.
        /// Det, brugeren selv har skrevet, overskrives aldrig.
        /// </summary>
        private void AutoFill()
        {
            if (armed != null) return;
            List<TagView> rd = Ready();
            if (Tags().Count > rd.Count) return;   // vent, til alle chips er læst
            string k = string.Join(",", rd.Select(t => t.Uid + ":" + t.Kind + ":" + t.Barcode + ":" + t.Seq + "/" + t.Num));
            if (k == autoKey) return;
            autoKey = k;
            string cur = inBarcode.Text.Trim();
            if (cur.Length > 0 && cur != lastAutoBc)
            {
                // Brugerens egen tekst bliver stående - men et antal dele, der kom fra de forrige chips, gør ikke
                if (numAuto) { numUser = null; numAuto = false; }
                lastAutoBc = null;
                return;
            }
            List<TagView> withNum = rd.Where(t => t.Kind == "danish" && t.Barcode.Length > 0).ToList();
            string bc = withNum.Count > 0 && withNum.Select(t => t.Barcode).Distinct().Count() == 1 ? withNum[0].Barcode : null;
            if (bc != null)
            {
                inBarcode.Text = bc;
                int n = withNum[0].Num;
                numUser = n > 1 ? n : (int?)null;
                numAuto = numUser != null;
                if (ContainsFocus || ActiveForm == this) inBarcode.Focus();
                inBarcode.SelectAll();
            }
            else
            {
                if (cur.Length > 0) inBarcode.Text = "";
                numUser = null;
                numAuto = false;
            }
            lastAutoBc = bc;
        }

        // ---------- Biblioteksnummer ----------

        private bool AskLibrary()
        {
            string sug = app.Library;
            if (sug.Length == 0) sug = Ready().Where(t => t.RawLibrary.Length > 0).Select(t => t.RawLibrary).FirstOrDefault() ?? "";
            using (Form d = new Form())
            {
                d.Text = "Biblioteksnummer";
                d.FormBorderStyle = FormBorderStyle.FixedDialog;
                d.MaximizeBox = d.MinimizeBox = false;
                d.ShowInTaskbar = false;
                d.StartPosition = FormStartPosition.CenterParent;
                d.AutoScaleMode = AutoScaleMode.None;
                d.BackColor = Look.Light;
                d.Font = new Font("Segoe UI", 9.5f);
                d.ClientSize = new Size(Px(380), Px(170));
                Label l = new Label { Text = "Nye chips får bibliotekets nummer (ISIL uden DK-). Det huskes til næste gang.", Bounds = R(18, 16, 344, 40), ForeColor = Look.Ink };
                TextBox tb = new TextBox { Text = sug, MaxLength = 9, Bounds = R(18, 62, 200, 30), Font = new Font("Bahnschrift", 13f) };
                FlatBtn ok = new FlatBtn { Text = "Gem", Bounds = R(380 - 18 - 100, 118, 100, 34), BackColor = Look.Light, Font = new Font("Segoe UI Semibold", 9.5f) };
                FlatBtn no = new FlatBtn { Text = "Annullér", Bounds = R(380 - 18 - 208, 118, 100, 34), BackColor = Look.Light, Fill = Color.White, Ink = Look.Ink, Border = Color.FromArgb(0xc5, 0xd0, 0xd2), Font = new Font("Segoe UI Semibold", 9.5f) };
                ok.Click += (o, e) => { d.DialogResult = DialogResult.OK; };
                no.Click += (o, e) => { d.DialogResult = DialogResult.Cancel; };
                tb.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; d.DialogResult = DialogResult.OK; } else if (e.KeyCode == Keys.Escape) d.DialogResult = DialogResult.Cancel; };
                d.Controls.AddRange(new Control[] { l, tb, ok, no });
                d.Shown += (o, e) => { tb.Focus(); tb.SelectAll(); };
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                string v = tb.Text.Trim();
                if (v.Length == 0) return false;
                app.SetLibrary(v);
                return true;
            }
        }

        // ---------- Programmering ----------

        private void Arm()
        {
            if (armed != null) return;
            string bc = inBarcode.Text.Trim();
            if (bc.Length == 0 || bc.Length > 16 || bc.Any(c => c < 0x21 || c > 0x7e))
            {
                Note("err", "Skriv et materialenummer", "Højst 16 tegn, uden mellemrum og specialtegn.", 5);
                inBarcode.Focus();
                return;
            }
            if (app.Library.Length == 0 && !AskLibrary()) return;
            doneMsg = null;
            boxTitle = null;
            armed = new Armed { Barcode = bc, Num = PartsValue() };
            Render();
        }

        private void Disarm(string msg)
        {
            armed = null;
            Render();
            if (msg != null) Note("", msg, "", 3);
        }

        private static string PartsText(IEnumerable<int> seqs, int num)
        {
            List<int> l = seqs.OrderBy(x => x).ToList();
            string list;
            if (l.Count > 2 && l[l.Count - 1] - l[0] == l.Count - 1) list = l[0] + "–" + l[l.Count - 1];
            else if (l.Count == 1) list = l[0].ToString();
            else list = string.Join(", ", l.Take(l.Count - 1)) + " og " + l[l.Count - 1];
            return "Del " + list + " af " + num;
        }

        /// <summary>Næste skridt i programmeringen - kaldes 4 gange i sekundet og efter hver handling.</summary>
        private void Step()
        {
            Armed a = armed;
            if (a == null)
            {
                if (doneMsg != null) ShowBox(doneCls, doneMsg, doneText);
                else ShowBox(null, null, null);
                return;
            }
            if (a.Asking) return;
            if (!st.ReaderOk) { ShowBox("err", "Ingen forbindelse til læseren", st.ReaderText); return; }
            if (a.Busy)
            {
                ShowBox("", a.Batch.Count > 1 ? "Programmerer " + a.Batch.Count + " dele …"
                    : a.Num > 1 ? "Programmerer " + PartsText(a.Batch.Select(j => j.Seq), a.Num).ToLowerInvariant() + " …" : "Programmerer chippen …", "Materialenummer " + a.Barcode);
                return;
            }
            List<TagView> ready = Ready();
            bool reading = Tags().Count > ready.Count;
            List<TagView> fresh = ready.Where(t => !a.Done.ContainsKey(t.Uid)).ToList();
            int left = a.Num - a.Done.Count;
            if (a.Error != null)
            {
                if (fresh.Any(t => a.ErrorMacs.Contains(t.Uid))) { ShowBox("err", "Det lykkedes ikke", a.Error); return; }
                a.Error = null;
            }
            if (reading) { ShowBox("", "Læser chippene …", "Materialenummer " + a.Barcode); return; }
            if (fresh.Count == 0)
            {
                if (a.Done.Count == 0)
                    ShowBox("", a.Num > 1 ? "Læg chippene på læseren" : "Læg chippen på læseren", "Materialenummer " + a.Barcode + (a.Num > 1 ? ", " + a.Num + " dele." : "."));
                else
                    ShowBox("", PartsText(a.Done.Values, a.Num) + " er programmeret",
                        "Læg " + (left == 1 ? "den sidste chip" : "de sidste " + left + " chips") + " på læseren. De andre må gerne blive liggende.");
                return;
            }
            if (fresh.Count > left)
            {
                ShowBox("warn", "Der ligger " + fresh.Count + " nye chips på læseren",
                    "Der mangler kun " + left + (left == 1 ? " del" : " dele") + ". Tag " + (fresh.Count - left) + " af dem af.");
                return;
            }
            ProgramBatch(fresh);
        }

        private static string ContentText(TagView t)
        {
            return t.Kind == "danish" && t.Barcode.Length > 0 ? "materialenummer " + t.Barcode : t.Kind == "unknown" ? "ukendt indhold" : "andet indhold";
        }

        private bool ConfirmOverwrite(Armed a, List<string> what)
        {
            string w = string.Join(", ", what.Distinct());
            string into = a.Barcode + (a.Num > 1 ? " (" + a.Num + " dele)" : "");
            string text = what.Count == 1 && a.Num == 1
                ? "Chippen har allerede " + w + ".\n\nVil du overskrive den med " + into + "?"
                : (what.Count == 1 ? "En af chippene" : what.Count + " af chippene") + " har allerede indhold (" + w + ").\n\nVil du overskrive " + (what.Count == 1 ? "den" : "dem") + " med " + into + "?";
            a.Asking = true;
            bool yes = MessageBox.Show(this, text, "Overskriv?", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            a.Asking = false;
            if (yes) a.Confirmed = true;
            return yes;
        }

        private async void ProgramBatch(List<TagView> fresh)
        {
            Armed a = armed;
            string lib = app.Library;
            HashSet<int> used = new HashSet<int>(a.Done.Values);
            List<int> free = Enumerable.Range(1, a.Num).Where(i => !used.Contains(i)).ToList();
            // En chip, der allerede har samme materialenummer, beholder sit delnummer (så cd 1 forbliver del 1).
            // De øvrige får de ledige numre i rækkefølge.
            List<Job> batch = new List<Job>();
            foreach (TagView t in fresh.OrderBy(t => t.Uid, StringComparer.Ordinal))
                if (t.Kind == "danish" && t.Barcode == a.Barcode && free.Contains(t.Seq) && batch.All(j => j.Seq != t.Seq))
                    batch.Add(new Job { T = t, Seq = t.Seq });
            List<int> rest = free.Where(i => batch.All(j => j.Seq != i)).ToList();
            foreach (TagView t in fresh.OrderBy(t => t.Uid, StringComparer.Ordinal))
                if (batch.All(j => j.T != t)) { batch.Add(new Job { T = t, Seq = rest[0] }); rest.RemoveAt(0); }

            // Én bekræftelse for hele sættet, hvis nogen af chippene har andet indhold
            if (!a.Confirmed)
            {
                List<string> other = batch.Where(b => b.T.Kind != "blank" &&
                    !(b.T.Kind == "danish" && b.T.Barcode == a.Barcode && b.T.Seq == b.Seq && b.T.Num == a.Num && b.T.RawLibrary == lib))
                    .Select(b => ContentText(b.T)).ToList();
                if (other.Count > 0)
                {
                    bool yes = ConfirmOverwrite(a, other);
                    if (armed != a) return;
                    if (!yes) { Disarm("Ikke overskrevet"); return; }
                }
            }

            a.Busy = true;
            a.Batch = batch;
            Render();
            foreach (Job b in batch)
            {
                Job job = b;
                bool conf = a.Confirmed;
                Worker.ToolResult r = await Task.Run(() => app.ToolProgram(job.T.Uid, a.Barcode, job.Seq, a.Num, lib, conf));
                if (armed != a || IsDisposed) return;
                if (r.NeedsConfirm)
                {
                    a.Busy = false;
                    bool yes = ConfirmOverwrite(a, new List<string> { r.Message });
                    if (armed != a) return;
                    if (!yes) { Disarm("Ikke overskrevet"); return; }
                    a.Busy = true;
                    r = await Task.Run(() => app.ToolProgram(job.T.Uid, a.Barcode, job.Seq, a.Num, lib, true));
                    if (armed != a || IsDisposed) return;
                }
                if (!r.Ok)
                {
                    a.Busy = false;
                    a.Error = (a.Num > 1 ? "Del " + job.Seq + ": " : "") + r.Message;
                    a.ErrorMacs = new HashSet<string> { job.T.Uid };
                    Render();
                    return;
                }
                a.Done[job.T.Uid] = job.Seq;
                Invalidate(R(RX, BoxTop, RW, BoxH));
            }
            a.Busy = false;
            if (a.Done.Count >= a.Num)
            {
                armed = null;
                numUser = null;
                numAuto = false;
                inBarcode.Text = "";
                autoKey = null;        // skrivefeltet viser nu det nye nummer fra chippene
                lastAutoBc = null;
                Render();
                Note("ok", a.Num == 2 ? "Begge dele er programmeret ✓" : a.Num > 2 ? "Alle " + a.Num + " dele er programmeret ✓" : "Programmeret ✓",
                    "Materialenummer " + a.Barcode + ". Alarmen er slået til.", 5);
                noteUids = new HashSet<string>(st.ReaderOk ? st.Tags.Select(t => t.Uid) : Enumerable.Empty<string>());
                inBarcode.Focus();
            }
            else Render();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
