// UI helpers: Windows light/dark theme + DPI scaling, cards, sidebar buttons, color wheel,
// plus the playlist model, the snake-chase generator and the Divoom cloud gallery client.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AuraBoxControl
{
    static class Theme
    {
        public static bool Dark;
        public static float Scale = 1f;
        public static Color Back, Surface, Raised, Input, Text, Subtle, Border, Accent, AccentFill, AccentText;
        public static string UiFont = "Segoe UI", IconFont = "Segoe MDL2 Assets";

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static void Init(string mode)
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            var installed = new List<string>();
            foreach (var f in new System.Drawing.Text.InstalledFontCollection().Families) installed.Add(f.Name);
            if (installed.Contains("Segoe UI Variable Text")) UiFont = "Segoe UI Variable Text";
            if (installed.Contains("Segoe Fluent Icons")) IconFont = "Segoe Fluent Icons";
            Load(mode);
        }

        public static int Px(int v) { return (int)Math.Round(v * Scale); }

        public static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && k.GetValue("AppsUseLightTheme") is int && (int)k.GetValue("AppsUseLightTheme") == 0;
            }
            catch (Exception) { return false; }
        }

        static Color SystemAccent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int) { int c = (int)v; return Color.FromArgb(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF); }
                }
            }
            catch (Exception) { }
            return Color.FromArgb(0, 120, 212);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // mode: "system", "light" or "dark"
        public static void Load(string mode)
        {
            Dark = mode == "dark" || (mode != "light" && SystemDark());
            Accent = SystemAccent();
            if (Dark)
            {
                Back = Color.FromArgb(32, 32, 32); Surface = Color.FromArgb(43, 43, 43); Raised = Color.FromArgb(58, 58, 58);
                Input = Color.FromArgb(28, 28, 28); Text = Color.FromArgb(240, 240, 240); Subtle = Color.FromArgb(160, 160, 160);
                Border = Color.FromArgb(68, 68, 68);
                AccentFill = Mix(Accent, Color.White, 0.45); AccentText = Color.Black;
            }
            else
            {
                Back = Color.FromArgb(243, 243, 243); Surface = Color.White; Raised = Color.FromArgb(251, 251, 251);
                Input = Color.White; Text = Color.FromArgb(28, 28, 28); Subtle = Color.FromArgb(100, 100, 100);
                Border = Color.FromArgb(215, 215, 215);
                AccentFill = Accent; AccentText = Color.White;
            }
        }

        // the color a child control should paint behind itself
        public static Color BackOf(Control parent)
        {
            if (parent is Card) return Surface;
            return parent == null ? Back : parent.BackColor;
        }

        // scale a freshly built form for the monitor DPI, then color it
        public static void Prepare(Form f)
        {
            if (Math.Abs(Scale - 1f) > 0.01f)
            {
                var cs = f.ClientSize;
                f.Scale(new SizeF(Scale, Scale));
                f.ClientSize = new Size(Px(cs.Width), Px(cs.Height));
            }
            Apply(f);
        }

        public static void Apply(Control c)
        {
            Style(c);
            foreach (Control child in c.Controls) Apply(child);
            if (c is Form) TitleBar((Form)c);
        }

        static void Style(Control c)
        {
            string tag = c.Tag as string;
            Color behind = BackOf(c.Parent);
            if (c is Form) { c.BackColor = Back; c.ForeColor = Text; }
            else if (c is Card || c is NavButton || c is ColorWheel || c is PixelGrid) { c.ForeColor = Text; c.Invalidate(); }
            else if (c is Button)
            {
                var b = (Button)c;
                bool primary = tag == "primary";
                b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
                b.BackColor = primary ? AccentFill : Raised;
                b.ForeColor = primary ? AccentText : Text;
                b.FlatAppearance.BorderColor = primary ? AccentFill : Border;
                b.FlatAppearance.MouseOverBackColor = Mix(b.BackColor, Text, 0.08);
                b.FlatAppearance.MouseDownBackColor = Mix(b.BackColor, Text, 0.16);
            }
            else if (c is TextBox || c is ListBox || c is ListView || c is NumericUpDown || c is ComboBox)
            {
                c.BackColor = Input; c.ForeColor = Text;
                if (c is TextBox) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                if (c is ListBox) ((ListBox)c).BorderStyle = BorderStyle.FixedSingle;
                if (c is NumericUpDown) ((NumericUpDown)c).BorderStyle = BorderStyle.FixedSingle;
                if (c is ComboBox)
                {
                    // Windows' own dark combo box (dark field, arrow and drop-down) instead of the flat one that stays white
                    ((ComboBox)c).FlatStyle = Dark ? FlatStyle.Standard : FlatStyle.Flat;
                    SetTheme(c, Dark ? "DarkMode_CFD" : "Explorer");
                }
                else DarkScrollbars(c);
                if (c is NumericUpDown && c.Controls.Count > 0) SetTheme(c.Controls[0], Dark ? "DarkMode_Explorer" : "Explorer");   // spin arrows
            }
            else if (c is TrackBar) c.BackColor = behind;
            else if (c is CheckBox || c is RadioButton)
            {
                c.BackColor = Color.Transparent; c.ForeColor = tag == "subtle" ? Subtle : Text;
            }
            else if (c is Label && tag == "dot") c.BackColor = Color.Transparent;
            else if (c is Label)
            {
                c.BackColor = tag == "swatch" ? c.BackColor : Color.Transparent;
                if (tag != "swatch") c.ForeColor = tag == "subtle" ? Subtle : tag == "accent" ? AccentFill : Text;
            }
            else if (c is Panel)
            {
                c.BackColor = tag == "sidebar" ? Mix(Back, Text, Dark ? 0.02 : 0.0) : behind; c.ForeColor = Text;
                if (((Panel)c).AutoScroll) DarkScrollbars(c);
            }
        }

        static void DarkScrollbars(Control c) { SetTheme(c, Dark ? "DarkMode_Explorer" : "Explorer"); }

        static void SetTheme(Control c, string theme)
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, theme, null);
            else c.HandleCreated += delegate { SetWindowTheme(c.Handle, theme, null); };
        }

        static void TitleBar(Form f)
        {
            if (f.IsHandleCreated) SetTitleBar(f);
            else f.HandleCreated += delegate { SetTitleBar(f); };
        }

        static void SetTitleBar(Form f)
        {
            int on = Dark ? 1 : 0;
            try { if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4); }
            catch (Exception) { }
        }

        public static Font Semibold(float size) { return new Font("Segoe UI Semibold", size); }
    }

    // rounded "card" panel with an optional title, like Windows 11 settings groups
    class Card : Panel
    {
        public Card() { DoubleBuffered = true; ResizeRedraw = true; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.BackOf(Parent))) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int r = Theme.Px(8);
            using (var path = Round(new Rectangle(0, 0, Width - 1, Height - 1), r))
            using (var b = new SolidBrush(Theme.Surface))
            using (var p = new Pen(Theme.Border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            if (!string.IsNullOrEmpty(Text))
                using (var f = Theme.Semibold(9.5f))
                    TextRenderer.DrawText(g, Text, f, new Point(Theme.Px(12), Theme.Px(8)), Theme.Text);
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public override string Text { get { return base.Text; } set { base.Text = value; Invalidate(); } }
    }

    // sidebar navigation entry: icon glyph + label, accent pill when selected
    class NavButton : Control
    {
        public string Glyph;
        bool selected, hover;
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public NavButton() { DoubleBuffered = true; Cursor = Cursors.Hand; }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color behind = Theme.BackOf(Parent);
            g.Clear(behind);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (selected || hover)
                using (var path = Card.Round(new Rectangle(0, Theme.Px(2), Width - 1, Height - Theme.Px(4) - 1), Theme.Px(5)))
                using (var b = new SolidBrush(Theme.Mix(behind, Theme.Text, selected ? 0.09 : 0.05)))
                    g.FillPath(b, path);
            if (selected)
                using (var b = new SolidBrush(Theme.AccentFill))
                using (var pill = Card.Round(new Rectangle(0, Height / 2 - Theme.Px(8), Theme.Px(3), Theme.Px(16)), Theme.Px(1)))
                    g.FillPath(b, pill);
            using (var f = new Font(Theme.IconFont, 11f))
                TextRenderer.DrawText(g, Glyph, f, new Rectangle(Theme.Px(12), 0, Theme.Px(24), Height), Theme.Text,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.Px(46), 0, Width - Theme.Px(46), Height), Theme.Text,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    // Hue wheel that snaps to the AuraBox's 7 lit colors: hues around the rim, white in the middle.
    class ColorWheel : Control
    {
        static readonly int[] HueToIndex = { 1, 3, 2, 6, 4, 5 };   // red, yellow, green, cyan, blue, magenta at 0/60/.../300 degrees
        int value = 1;
        public event EventHandler Picked;    // mouse released on a color
        public event EventHandler Changing;  // while dragging

        public ColorWheel() { DoubleBuffered = true; Cursor = Cursors.Hand; Size = new Size(160, 160); }

        public int Value { get { return value; } set { this.value = value; Invalidate(); } }

        float Radius { get { return Math.Min(Width, Height) / 2f - Theme.Px(3); } }
        PointF Center { get { return new PointF(Width / 2f, Height / 2f); } }

        // Six solid pie wedges (one per hue) around a white center disc. The selected wedge pops out to full
        // size with an outline; the others sit slightly smaller, separated by gaps in the background color.
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color behind = Theme.BackOf(Parent);
            g.Clear(behind);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float inner = Radius * 0.36f, gap = Theme.Px(3);
            for (int k = 0; k < 6; k++)
            {
                int color = HueToIndex[k];
                bool sel = color == value;
                float r = sel ? Radius : Radius * 0.9f;
                var rect = new RectangleF(Center.X - r, Center.Y - r, 2 * r, 2 * r);
                float start = -k * 60 - 30;          // GDI+ angles run clockwise; hues run counter-clockwise
                using (var b = new SolidBrush(Proto.Palette[color])) g.FillPie(b, rect.X, rect.Y, rect.Width, rect.Height, start, 60);
                if (sel)
                    using (var path = new GraphicsPath())
                    using (var ring = new Pen(Theme.Text, Theme.Px(2)))
                    {
                        path.AddPie(rect.X, rect.Y, rect.Width, rect.Height, start, 60);
                        g.DrawPath(ring, path);
                    }
            }
            // gaps between wedges
            using (var sep = new Pen(behind, gap))
                for (int k = 0; k < 6; k++)
                {
                    double a = (k * 60 + 30) * Math.PI / 180;
                    g.DrawLine(sep, Center, new PointF(Center.X + (float)(Math.Cos(a) * (Radius + 2)), Center.Y - (float)(Math.Sin(a) * (Radius + 2))));
                }
            // white center (with a gap ring), outlined when selected
            float wr = value == 7 ? inner + Theme.Px(3) : inner;
            using (var ringBg = new SolidBrush(behind))
                g.FillEllipse(ringBg, Center.X - inner - gap, Center.Y - inner - gap, 2 * (inner + gap), 2 * (inner + gap));
            using (var white = new SolidBrush(Color.White))
                g.FillEllipse(white, Center.X - wr, Center.Y - wr, 2 * wr, 2 * wr);
            if (value == 7)
                using (var ring = new Pen(Theme.Text, Theme.Px(2)))
                    g.DrawEllipse(ring, Center.X - wr, Center.Y - wr, 2 * wr, 2 * wr);
            else
                using (var edge = new Pen(Theme.Border, 1))
                    g.DrawEllipse(edge, Center.X - wr, Center.Y - wr, 2 * wr, 2 * wr);
        }

        int Pick(Point p)
        {
            double dx = p.X - Center.X, dy = Center.Y - p.Y, d = Math.Sqrt(dx * dx + dy * dy) / Radius;
            if (d > 1.1) return -1;
            if (d < 0.4) return 7;
            double deg = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            return HueToIndex[(int)Math.Round(deg / 60) % 6];
        }

        bool dragging;
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int v = Pick(e.Location);
            if (v < 0) return;
            dragging = true; Capture = true;
            Set(v);
        }
        protected override void OnMouseMove(MouseEventArgs e) { if (dragging) { int v = Pick(e.Location); if (v > 0) Set(v); } }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false; Capture = false;
            if (Picked != null) Picked(this, EventArgs.Empty);
        }

        void Set(int v)
        {
            if (v == value) return;
            value = v; Invalidate();
            if (Changing != null) Changing(this, EventArgs.Empty);
        }
    }

    // flat dark/light tray menu
    class MenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using (var b = new SolidBrush(e.Item.Selected ? Theme.Raised : Theme.Surface))
                e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Theme.Text; base.OnRenderItemText(e); }
    }

    // a number box that always shows two digits ("07", "00"), for times
    class TwoDigitUpDown : NumericUpDown
    {
        protected override void UpdateEditText() { Text = ((int)Value).ToString("00"); }
    }

    // one entry of the clock/weather/... playlist
    class PlayItem
    {
        public string Id, Name; public bool On; public int Seconds;

        static readonly string[,] All = {
            { "clock", "Clock" }, { "weather", "Weather" }, { "mode1", "Temperature (Built-In Sensor)" }, { "mode2", "Color Light" },
            { "mode3", "Sound Light" }, { "mode4", "Forest" }, { "mode5", "Marquee" }, { "mode6", "Color Pieces" },
            { "snakes", "Snake Chase" }, { "blocks", "Tetrix" }, { "stars", "Hyperspace" }, { "rain", "Rain on a Window" }, { "fire", "Fireplace" }, { "fireworks", "Fireworks" }, { "heart", "Heart Monitor" }, { "balls", "Bouncing Balls" }, { "bricks", "Brick Breaker" }, { "laos", "Laos Flag" },{ "lava", "Lava Lamp" }, { "lavaclock", "Lava Clock" }, { "countdown", "Countdown Timer" }, { "text", "Scrolling Text" }, { "drawing", "Current Drawing" }, { "animation", "Current Animation" }, { "favorite", "Random Favorite" }, { "holiday", "Holiday Designs (in season)" } };

        // sections on the Playlist page for items that are turned off
        public static readonly string[] Groups = { "Clock & Weather", "Animations", "Original Modes", "Your Content" };

        public string Group
        {
            get
            {
                if (Id == "clock" || Id == "weather" || Id == "lavaclock" || Id == "countdown") return Groups[0];
                if (Id.StartsWith("mode")) return Groups[2];
                if (Id == "text" || Id == "drawing" || Id == "animation" || Id == "favorite" || Id == "holiday") return Groups[3];
                return Groups[1];
            }
        }

        public static List<PlayItem> Defaults()
        {
            var list = new List<PlayItem>();
            for (int k = 0; k < All.GetLength(0); k++)
                list.Add(new PlayItem { Id = All[k, 0], Name = All[k, 1], On = k < 2,
                                        Seconds = k == 0 ? 120 : All[k, 0] == "fireworks" ? (int)Fireworks.ShowSeconds :   // the whole show
                                                  All[k, 0] == "countdown" ? Countdown.Minutes * 60 + 10 : 60 });   // and a little of the finish
            return list;
        }

        public static PlayItem Find(List<PlayItem> list, string id)
        {
            foreach (var it in list) if (it.Id == id) return it;
            return null;
        }

        // "id:on:seconds;..." in play order; unknown ids are dropped, new ones appended
        public static List<PlayItem> Parse(string s)
        {
            var defaults = Defaults();
            var list = new List<PlayItem>();
            foreach (string part in s.Split(';'))
            {
                string[] f = part.Split(':');
                int sec;
                if (f.Length != 3 || !int.TryParse(f[2], out sec)) continue;
                var d = Find(defaults, f[0]);
                if (d == null || Find(list, d.Id) != null) continue;
                d.On = f[1] == "1"; d.Seconds = Math.Max(5, sec);
                list.Add(d);
            }
            foreach (var d in defaults) if (Find(list, d.Id) == null) list.Add(d);
            return list;
        }

        public static string Format(List<PlayItem> list)
        {
            var sb = new StringBuilder();
            foreach (var it in list) sb.Append(it.Id).Append(':').Append(it.On ? 1 : 0).Append(':').Append(it.Seconds).Append(';');
            return sb.ToString().TrimEnd(';');
        }
    }

    // Two snakes playing tag on a loop through all 100 pixels. "It" is a little faster; when it
    // reaches the other snake's tail, the caught snake becomes "it".
    class SnakeChase
    {
        readonly List<Point> Path;
        readonly int[] head = new int[2];
        readonly int[] length;
        readonly int[] body = { 2, 5 };     // green, magenta
        int it = 0, tick, flash;

        public SnakeChase() : this(10) { }
        public SnakeChase(int size)
        {
            Path = BuildPath(size);
            length = size == 16 ? new[] { 11, 5 } : new[] { 7, 3 };
            head[1] = Path.Count * 2 / 5;
            N = size;
        }
        readonly int N;

        static List<Point> BuildPath(int n)
        {
            // serpentine through columns 1..n-1, then back up column 0: a closed loop covering every pixel
            var p = new List<Point> { new Point(0, 0) };
            for (int y = 0; y < n; y++)
                for (int i = 0; i < n - 1; i++) p.Add(new Point(y % 2 == 0 ? 1 + i : n - 1 - i, y));
            for (int y = n - 1; y >= 1; y--) p.Add(new Point(0, y));
            return p;
        }

        int Gap(int from, int to) { return (head[to] - length[to] + 1 - head[from] + Path.Count * 2) % Path.Count; }

        void Step(int s)
        {
            head[s] = (head[s] + 1) % Path.Count;
            int other = 1 - s;
            if (s == it && Gap(it, other) <= 1) { it = other; flash = 4; }
        }

        public int[] Next()
        {
            tick++;
            Step(0); Step(1);
            if (tick % 2 == 0) Step(it);    // "it" gets an extra step every other tick
            if (N == 16) { Step(0); Step(1); if (tick % 2 == 0) Step(it); }   // a bigger loop: twice the pace
            var px = new int[N * N];
            for (int s = 0; s < 2; s++)
                for (int k = length[s] - 1; k >= 0; k--)
                {
                    var p = Path[(head[s] - k + Path.Count) % Path.Count];
                    int c = k == 0 ? (s == it ? (flash > 0 && flash % 2 == 0 ? 7 : 1) : 3) : body[s];
                    // at 16 x 16 the tail fades: the last few segments darker
                    if (N == 16 && k > 0 && k >= length[s] - 3) c = s == 0 ? Palette16.DarkGreen : Palette16.Purple;
                    px[p.Y * N + p.X] = c;
                }
            if (flash > 0) flash--;
            return px;
        }
    }

    // Self-playing falling-blocks puzzle: each piece slides and drops into the spot a simple heuristic
    // likes best; full rows flash and clear. If the stack reaches the top, the board flashes and starts over.
    class FallingBlocks
    {
        static readonly string[] Shapes = {
            "0,0 1,0 2,0 3,0", "0,0 1,0 0,1 1,1", "0,0 1,0 2,0 1,1", "1,0 2,0 0,1 1,1",
            "0,0 1,0 1,1 2,1", "0,0 0,1 1,1 2,1", "2,0 0,1 1,1 2,1" };
        static readonly int[] Colors = { 6, 3, 5, 2, 1, 4, 7 };   // I cyan, O yellow, T magenta, S green, Z red, J blue, L white
        // at 16 x 16 (Palette16): the usual colors, with an orange L and a purple T
        static readonly int[] Colors16 = { 6, 3, 10, 2, 1, 4, 8 };
        static readonly List<Point[]>[] Rotations = BuildRotations();
        readonly int N;          // the board is N x N: 10 on the AuraBox, 16 on the Tivoo
        readonly int[] board;

        public FallingBlocks() : this(10) { }
        public FallingBlocks(int size) { N = size; board = new int[N * N]; }
        readonly Random rng = new Random();
        readonly List<int> bag = new List<int>();
        readonly List<int> fullRows = new List<int>();
        Point[] piece; int color, px, py, targetX, flash; bool wiping;

        static List<Point[]>[] BuildRotations()
        {
            var all = new List<Point[]>[Shapes.Length];
            for (int s = 0; s < Shapes.Length; s++)
            {
                var cells = new List<Point>();
                foreach (string xy in Shapes[s].Split(' ')) { string[] f = xy.Split(','); cells.Add(new Point(int.Parse(f[0]), int.Parse(f[1]))); }
                all[s] = new List<Point[]>();
                var seen = new List<string>();
                var cur = cells.ToArray();
                for (int r = 0; r < 4; r++)
                {
                    cur = Normalize(Array.ConvertAll(cur, p => new Point(-p.Y, p.X)));
                    var sorted = (Point[])cur.Clone();
                    Array.Sort(sorted, (a, b) => a.Y != b.Y ? a.Y - b.Y : a.X - b.X);
                    string key = string.Join(" ", Array.ConvertAll(sorted, p => p.X + "," + p.Y));
                    if (!seen.Contains(key)) { seen.Add(key); all[s].Add(cur); }
                }
            }
            return all;
        }

        static Point[] Normalize(Point[] cells)
        {
            int mx = int.MaxValue, my = int.MaxValue;
            foreach (var p in cells) { mx = Math.Min(mx, p.X); my = Math.Min(my, p.Y); }
            return Array.ConvertAll(cells, p => new Point(p.X - mx, p.Y - my));
        }

        static int Width(Point[] c) { int w = 0; foreach (var p in c) w = Math.Max(w, p.X + 1); return w; }
        static int Height(Point[] c) { int h = 0; foreach (var p in c) h = Math.Max(h, p.Y + 1); return h; }

        bool Fits(int[] b, Point[] cells, int x, int y)
        {
            foreach (var p in cells)
            {
                int bx = x + p.X, by = y + p.Y;
                if (bx < 0 || bx >= N || by >= N) return false;
                if (by >= 0 && b[by * N + bx] != 0) return false;
            }
            return true;
        }

        // with rows == null, removes full rows; otherwise only lists them. Returns how many are full.
        int ClearRows(int[] b, List<int> rows)
        {
            int n = 0;
            for (int y = 0; y < N; y++)
            {
                bool full = true;
                for (int x = 0; x < N; x++) if (b[y * N + x] == 0) { full = false; break; }
                if (!full) continue;
                n++;
                if (rows != null) rows.Add(y);
                else { for (int yy = y; yy > 0; yy--) Array.Copy(b, (yy - 1) * N, b, yy * N, N); Array.Clear(b, 0, N); }
            }
            return n;
        }

        // well-known weights for stack height, completed lines, holes and bumpiness
        double Score(int[] b, int lines)
        {
            int agg = 0, holes = 0, bump = 0, prev = -1;
            for (int x = 0; x < N; x++)
            {
                int h = 0;
                bool seen = false;
                for (int y = 0; y < N; y++)
                {
                    if (b[y * N + x] != 0) { if (!seen) { h = N - y; seen = true; } }
                    else if (seen) holes++;
                }
                agg += h;
                if (prev >= 0) bump += Math.Abs(h - prev);
                prev = h;
            }
            return -0.51 * agg + 0.76 * lines - 0.36 * holes - 0.18 * bump;
        }

        void Spawn()
        {
            if (bag.Count == 0) for (int k = 0; k < 7; k++) bag.Add(k);
            int s = bag[rng.Next(bag.Count)];
            bag.Remove(s);
            double best = double.MinValue;
            Point[] bestCells = Rotations[s][0]; int bestX = 0;
            foreach (var cells in Rotations[s])
                for (int x = 0; x + Width(cells) <= N; x++)
                {
                    int y = -Height(cells);
                    while (Fits(board, cells, x, y + 1)) y++;
                    var trial = (int[])board.Clone();
                    bool over = false;
                    foreach (var p in cells) { if (y + p.Y < 0) over = true; else trial[(y + p.Y) * N + x + p.X] = 1; }
                    double score = Score(trial, ClearRows(trial, null)) - (over ? 1000 : 0) + rng.NextDouble() * 0.01;
                    if (score > best) { best = score; bestCells = cells; bestX = x; }
                }
            piece = bestCells; color = (N == 10 ? Colors : Colors16)[s]; targetX = bestX;
            px = Math.Min(N / 2 - 2, N - Width(piece)); py = -Height(piece);
        }

        void Lock()
        {
            bool over = false;
            foreach (var p in piece)
            {
                int y = py + p.Y;
                if (y < 0) over = true; else board[y * N + px + p.X] = color;
            }
            piece = null;
            fullRows.Clear();
            if (over)
            {
                wiping = true;
                for (int y = 0; y < N; y++) fullRows.Add(y);
                flash = 8;
            }
            else if (ClearRows(board, fullRows) > 0) flash = 6;
        }

        public int[] Next()
        {
            if (flash > 0)
            {
                flash--;
                if (flash == 0)
                {
                    if (wiping) { Array.Clear(board, 0, N * N); wiping = false; }
                    else ClearRows(board, null);
                    fullRows.Clear();
                }
            }
            else
            {
                if (piece == null) Spawn();
                int step = Math.Sign(targetX - px);
                if (step != 0 && Fits(board, piece, px + step, py)) px += step;
                if (Fits(board, piece, px, py + 1)) py++;
                else Lock();
            }

            var frame = (int[])board.Clone();
            // a completed line flashes yellow; the whole-board flash before starting over stays white
            foreach (int y in fullRows)
                for (int x = 0; x < N; x++) frame[y * N + x] = flash % 2 == 0 ? (wiping ? 7 : 3) : (wiping ? board[y * N + x] : 0);
            if (piece != null)
                foreach (var p in piece)
                    if (py + p.Y >= 0) frame[(py + p.Y) * N + px + p.X] = color;
            return frame;
        }
    }


    // Colors for 16 x 16 animations: indexes 0-7 are the AuraBox palette (Proto.Palette), the rest only the Tivoo shows.
    // A 16 x 16 animation draws palette indexes; ToRgb turns a frame into the RGB picture the Tivoo takes.
    static class Palette16
    {
        public const int Orange = 8, DarkRed = 9, Purple = 10, Pink = 11, Gray = 12, SkyBlue = 13, DarkGreen = 14, Brown = 15,
                         Navy = 16, Amber = 17, DimGray = 18, DarkOrange = 19, Teal = 20, Lime = 21;
        public static readonly int[] Rgb = {
            0x000000, 0xFF0000, 0x00FF00, 0xFFFF00, 0x0000FF, 0xFF00FF, 0x00FFFF, 0xFFFFFF,
            0xFF8000, 0x800000, 0x8000FF, 0xFF69B4, 0x606060, 0x64A0FF, 0x008000, 0x8C5014,
            0x000078, 0xFFBF00, 0x303030, 0xC04000, 0x008080, 0xA0FF40 };

        public static int[] ToRgb(int[] idx)
        {
            var o = new int[idx.Length];
            for (int k = 0; k < idx.Length; k++) o[k] = idx[k] >= 0 && idx[k] < Rgb.Length ? Rgb[idx[k]] : 0;
            return o;
        }
    }

    // Hyperspace: stars stream out from the center in every direction, speeding up and stretching
    // into streaks (blue far away, cyan closer, white as they rush past). Starts with a short jump.
    class Starfield
    {
        class Star { public double Angle, Dist; }
        readonly List<Star> stars = new List<Star>();
        readonly Random rng = new Random();
        readonly int N;
        readonly double K, C;   // scale from the 10 x 10 design, and the center
        int tick;

        public Starfield() : this(10) { }
        public Starfield(int size)
        {
            N = size; K = size / 10.0; C = size / 2.0;
            for (int k = 0; k < (size == 16 ? 30 : 14); k++) stars.Add(NewStar(rng.NextDouble() * 5 * K));
        }

        Star NewStar(double dist) { return new Star { Angle = rng.NextDouble() * 2 * Math.PI, Dist = dist }; }

        void Plot(int[] px, Star s, double dist, int color, int rank)
        {
            int x = (int)Math.Floor(C + Math.Cos(s.Angle) * dist), y = (int)Math.Floor(C + Math.Sin(s.Angle) * dist);
            if (x >= 0 && x < N && y >= 0 && y < N && Rank(px[y * N + x]) < rank) px[y * N + x] = color;
        }

        // brighter colors win where streaks cross
        static int Rank(int c) { return c == 7 ? 4 : c == 6 ? 3 : c == 4 ? 2 : c == Palette16.Navy ? 1 : 0; }

        public int[] Next()
        {
            tick++;
            double grow = 1.06 + 0.2 * Math.Min(1.0, tick / 25.0);   // the jump: accelerate over the first few seconds
            var px = new int[N * N];
            for (int k = 0; k < stars.Count; k++)
            {
                var s = stars[k];
                double prev = s.Dist;
                s.Dist = Math.Max(0.4, s.Dist) * grow;
                if (s.Dist > 7.5 * K) { stars[k] = NewStar(0.3 + rng.NextDouble() * 0.8 * K); continue; }
                double d = s.Dist / K;   // in 10 x 10 terms
                int head = d < 1.8 ? 4 : d < 3.2 ? 6 : 7;          // palette: blue < cyan < white
                if (d > 2.2) Plot(px, s, (prev + s.Dist) / 2, d > 3.5 ? 6 : 4, d > 3.5 ? 3 : 2);   // streak behind fast stars
                if (d > 4.0) Plot(px, s, prev, 4, 2);
                if (N == 16 && d > 4.0) Plot(px, s, prev - (s.Dist - prev) / 2, Palette16.Navy, 1);   // a longer, fading tail
                Plot(px, s, s.Dist, head, Rank(head));
            }
            return px;
        }
    }

    // Rain on a window: drops slide down at their own pace, sometimes cling for a moment (white), and leave a short trail.
    class RainWindow
    {
        class Drop { public double X, Y, Speed; public int Pause; }
        readonly List<Drop> drops = new List<Drop>();
        readonly Random rng = new Random();
        readonly int N;

        public RainWindow() : this(10) { }
        public RainWindow(int size) { N = size; }

        void Set(int[] px, int x, int y, int c, int rank)
        {
            if (x >= 0 && x < N && y >= 0 && y < N && Rank(px[y * N + x]) < rank) px[y * N + x] = c;
        }
        static int Rank(int c) { return c == 7 ? 4 : c == 6 ? 3 : c == 4 ? 2 : c == Palette16.Navy ? 1 : 0; }

        public int[] Next()
        {
            int most = N == 16 ? 20 : 9;
            if (drops.Count < most && rng.NextDouble() < (N == 16 ? 0.7 : 0.5)) drops.Add(new Drop { X = rng.Next(N), Y = -1, Speed = 0.35 + rng.NextDouble() * 0.8 });
            var px = new int[N * N];
            for (int i = drops.Count - 1; i >= 0; i--)
            {
                var d = drops[i];
                if (d.Pause > 0) d.Pause--;
                else
                {
                    d.Y += d.Speed;
                    if (rng.NextDouble() < 0.06) d.Pause = rng.Next(3, 10);
                    if (rng.NextDouble() < 0.05) d.X = Math.Max(0, Math.Min(N - 1, d.X + rng.Next(2) * 2 - 1));
                }
                if (d.Y >= N + 1) { drops.RemoveAt(i); continue; }
                int x = (int)d.X, y = (int)Math.Floor(d.Y);
                Set(px, x, y - 1, 4, 2); Set(px, x, y - 2, 4, 2);     // blue trail
                if (N == 16) { Set(px, x, y - 3, Palette16.Navy, 1); Set(px, x, y - 4, Palette16.Navy, 1); }   // and a dimmer one behind it
                Set(px, x, y, d.Pause > 0 ? 7 : 6, d.Pause > 0 ? 4 : 3);             // cyan drop, white while it clings
            }
            return px;
        }
    }

    // Fireplace: classic heat-rises fire. Two hidden rows under the screen feed it; each cell averages the heat below it
    // and cools a little, so flames flicker upward and fade from white to yellow to red. The middle burns hottest, and
    // random flares shoot yellow tongues up toward the top.
    class Fireplace
    {
        readonly double[,] heat;
        readonly Random rng = new Random();
        readonly int N;
        readonly double cool;   // heat lost per row: a little less on 16 x 16, so the flames reach about two-thirds of the way up
        // the white-hot core: a 1-pixel white line up the middle, about half the height, that shoots higher when a
        // flare goes up near it (spikeGoal = how much higher it is heading, spike = how much higher it is now)
        int coreX, spike, spikeGoal, spikeRest;   // spikeRest: frames before the core can spike again

        public Fireplace() : this(10) { }
        public Fireplace(int size)
        {
            N = size; heat = new double[N + 2, N]; coreX = N / 2 - 1; cool = size == 16 ? 0.85 : 1;
        }

        public int[] Next()
        {
            int mid0 = N * 3 / 10, mid1 = N - 1 - mid0;   // the hottest middle columns (3-6 on 10 x 10)
            for (int x = 0; x < N; x++)
            {
                double edge = x == 0 || x == N - 1 ? 0.55 : x == 1 || x == N - 2 ? 0.8 : x >= mid0 && x <= mid1 ? 1.15 : 1.0;
                heat[N + 1, x] = (0.75 + rng.NextDouble() * 0.3) * edge;
                heat[N, x] = (0.45 + rng.NextDouble() * 0.6) * edge;
            }
            // a flare: one column burns extra hot for a frame, sending a tall flame up
            for (int f = 0; f < (N == 16 ? 2 : 1); f++)
                if (rng.NextDouble() < 0.35)
                {
                    int fx = 2 + rng.Next(N - 4); heat[N, fx] = heat[N + 1, fx] = 1.6;
                    // now and then the core crests with a flare beside it
                    if (Math.Abs(fx - coreX) <= 1 && spikeGoal == 0 && spike == 0 && spikeRest == 0 && rng.NextDouble() < 0.4) spikeGoal = (3 + rng.Next(2)) * N / 10;
                }
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    double s = heat[y + 1, x] * 2 + heat[y + 1, Math.Max(0, x - 1)] + heat[y + 1, Math.Min(N - 1, x + 1)] + heat[Math.Min(N + 1, y + 2), x];
                    heat[y, x] = Math.Max(0, s / 5 - (0.05 + rng.NextDouble() * 0.06) * cool);
                }
            var px = new int[N * N];
            bool big = N == 16;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    double h = heat[y, x];
                    double white = x >= mid0 && x <= mid1 ? 0.72 : 0.85;   // the center glows white-hot more easily
                    // white-hot, yellow, (orange), red, (embers)
                    px[y * N + x] = h > white ? 7 : h > 0.45 ? 3 : big && h > 0.31 ? Palette16.Orange : h > 0.2 ? 1 : big && h > 0.12 ? Palette16.DarkRed : 0;
                }

            // the core sways a little, flickers in height, and spikes up (then settles) with a flare
            if (rng.NextDouble() < 0.15) coreX = coreX == N / 2 - 1 ? N / 2 : N / 2 - 1;
            if (spike < spikeGoal) spike++;                       // rising to the crest...
            else if (spikeGoal > 0) spikeGoal = 0;                // ...reached it: start settling
            else if (spike > 0) { spike--; if (spike == 0) spikeRest = 12; }
            else if (spikeRest > 0) spikeRest--;
            int tall = Math.Min(N - 1, N / 2 + rng.Next(2) + spike);
            for (int y = N - 1; y > N - 1 - tall; y--) px[y * N + coreX] = 7;
            if (spike >= 2) px[(N - 1 - tall) * N + coreX] = 3;      // a yellow tip on a tall spike
            return px;
        }
    }

    // Fireworks: a 90-second show. It opens with a rocket every few seconds and small bursts, builds to several at
    // once with bigger two-color bursts, and ends in a finale of overlapping bursts with white crackle and one big
    // burst that fills the screen. Then a few dark seconds and it starts over. Timing follows the clock, not the frame
    // rate, so the show lasts 90 seconds at any speed setting (speed only makes the motion smoother). Some rockets
    // burst into a shape (a star, heart, ring or smiley face), and at most 4 rockets or fresh bursts are up at once,
    // so even the finale stays readable. Shapes are pixel pictures: each spark flies to one pixel of it and holds.
    class Fireworks
    {
        public const double ShowSeconds = 90, FinaleAt = 80, BigBoomAt = 86, QuietAt = 87;
        const int MaxAtOnce = 4;           // rockets in the air plus bursts still opening
        const double Opening = 0.8;        // seconds a burst counts as still opening
        class Spark { public double X, Y, Vx, Vy, Age, Life, Apex; public int Color; public bool Rocket, Crackle, Shaped; public int Stage, Shape = -1; }
        readonly List<double> opening = new List<double>();   // ages of the latest bursts
        const double ShapeDrag = 3;        // a shaped spark slows as it nears its place: it gets 95% there in 1 second
        readonly List<Spark> sparks = new List<Spark>();
        readonly Random rng = new Random();
        DateTime last = DateTime.Now;
        double t, owed;    // seconds into the show; rockets owed (launch rate x time)
        bool boomed;       // this show's big final burst has gone off
        static readonly int[] Palette10 = { 1, 2, 3, 4, 5, 6, 7 };
        // 16 x 16 (Palette16): more colors, and sparks that cool through more steps
        static readonly int[] Palette16Colors = { 1, 2, 3, 4, 5, 6, 7, Palette16.Orange, Palette16.Purple, Palette16.Pink, Palette16.SkyBlue };
        readonly int N;          // the screen is N x N: 10 on the AuraBox, 16 on the Tivoo
        readonly double K;       // distances and speeds scale with the screen (shapes stay 9 x 9)
        readonly int[] Palette;

        public Fireworks() : this(10) { }
        public Fireworks(int size) { N = size; K = N / 10.0; Palette = N == 10 ? Palette10 : Palette16Colors; }

        // a spark's color as it cools near the end of its life
        int Cooled(int c)
        {
            if (N == 10) return c == 7 ? 3 : c == 3 || c == 5 ? 1 : c == 6 ? 4 : c;
            switch (c)
            {
                case 7: return 3;
                case 3: return Palette16.Orange;
                case Palette16.Orange: case 1: return Palette16.DarkRed;
                case 5: case Palette16.Pink: return Palette16.Purple;
                case 6: case Palette16.SkyBlue: return 4;
                case 2: return Palette16.DarkGreen;
                case 4: case Palette16.Purple: return Palette16.Navy;
                default: return c;
            }
        }

        public int[] Next()
        {
            var now = DateTime.Now;
            double dt = Math.Min(0.25, (now - last).TotalSeconds);
            last = now;
            return Step(dt);
        }

        // rockets per second at a point in the show: slow, building, finale, then quiet
        static double Rate(double t)
        {
            if (t < 30) return 0.3 + t / 30 * 0.2;
            if (t < 60) return 0.5 + (t - 30) / 30 * 0.7;
            if (t < FinaleAt) return 1.2 + (t - 60) / 20 * 1.0;
            if (t < QuietAt) return 3.0;
            return 0;
        }

        // 0 opening, 1 building, 2 big, 3 finale: sets how large and colorful each burst is
        static int StageAt(double t) { return t < 30 ? 0 : t < 60 ? 1 : t < FinaleAt ? 2 : 3; }

        public int[] Step(double dt)
        {
            t += dt;
            if (t >= ShowSeconds) { t -= ShowSeconds; boomed = false; }
            owed += Rate(t) * dt;
            for (int i = opening.Count - 1; i >= 0; i--) if ((opening[i] += dt) > Opening) opening.RemoveAt(i);
            while (owed >= 1)
            {
                int up = opening.Count;
                foreach (var sp in sparks) if (sp.Rocket) up++;
                if (up >= MaxAtOnce) { owed = 1; break; }   // this rocket waits until there is room
                owed -= 1; Launch();
            }
            if (t >= BigBoomAt && !boomed) { boomed = true; Burst(N * 0.45, N * 0.35, 3, true); }

            for (int i = sparks.Count - 1; i >= 0; i--)
            {
                var s = sparks[i];
                s.Age += dt;
                if (s.Rocket)
                {
                    s.Y += s.Vy * dt;
                    if (s.Y <= s.Apex) { if (s.Shape >= 0) ShapeBurst(s.X, s.Y, s.Shape); else Burst(s.X, s.Y, s.Stage, false); sparks.RemoveAt(i); }
                    continue;
                }
                if (s.Shaped)   // even drag and no gravity until the shape has formed, then it droops as it fades
                {
                    if (s.Age > 1.2) s.Vy += 2 * dt;
                    s.Vx *= Math.Exp(-ShapeDrag * dt); s.Vy *= Math.Exp(-ShapeDrag * dt);
                    s.X += s.Vx * dt; s.Y += s.Vy * dt;
                    if (s.Age >= s.Life || s.Y > N + 0.5) sparks.RemoveAt(i);
                    continue;
                }
                s.Vy += 3.5 * K * dt;                          // gravity, pixels per second per second
                s.Vx *= 1 - 0.6 * dt; s.Vy *= 1 - 0.3 * dt;  // air drag
                s.X += s.Vx * dt; s.Y += s.Vy * dt;
                if (s.Age >= s.Life || s.Y > N + 0.5) sparks.RemoveAt(i);
            }

            var px = new int[N * N];
            foreach (var s in sparks)
            {
                int x = (int)Math.Round(s.X), y = (int)Math.Round(s.Y);
                if (x < 0 || x > N - 1 || y < 0 || y > N - 1) continue;
                if (s.Rocket)
                {
                    if (y + 1 <= N - 1 && px[(y + 1) * N + x] == 0) px[(y + 1) * N + x] = N == 10 ? 1 : Palette16.Orange;   // a trail under the head
                    if (N > 10 && y + 2 <= N - 1 && px[(y + 2) * N + x] == 0) px[(y + 2) * N + x] = Palette16.DarkRed;
                    px[y * N + x] = 3;
                    continue;
                }
                // sparks cool as they die: white goes yellow, yellow red, cyan blue, magenta red
                int c = s.Color;
                if (!s.Crackle && s.Age > s.Life * (s.Shaped ? 0.8 : 0.65)) c = Cooled(c);
                if (N > 10 && !s.Crackle && !s.Shaped && s.Age > s.Life * 0.85) c = Cooled(c);   // a second, darker step
                if (s.Crackle && (int)(s.Age * 20) % 2 == 1) continue;   // crackle twinkles
                px[y * N + x] = c;
            }
            return px;
        }

        void Launch()
        {
            int stage = StageAt(t);
            if (rng.NextDouble() < (stage == 0 ? 0.2 : 0.3))   // a shape needs room, so it bursts near the middle
            {
                sparks.Add(new Spark { Rocket = true, X = N / 2 - 1 + rng.Next(2), Y = N - 0.5, Vy = -(7 + rng.NextDouble() * 2) * K,
                                       Apex = N / 2 - 1 + rng.NextDouble(), Stage = stage, Shape = rng.Next(Shapes.Length) });
                return;
            }
            sparks.Add(new Spark
            {
                Rocket = true, X = 1 + rng.Next(N - 2), Y = N - 0.5, Vy = -(7 + rng.NextDouble() * 2) * K,
                Apex = (stage == 3 ? 1 + rng.NextDouble() * 4 : 1.5 + rng.NextDouble() * 2.5) * K, Stage = stage
            });
        }

        // a shape: every spark flies out toward its own point on the outline, so the outline grows from the center
        void ShapeBurst(double x, double y, int shape)
        {
            opening.Add(0);
            var pts = Shapes[shape];
            int color = new[] { 3, 1, 6, 3 }[shape];   // a yellow star, red heart, cyan ring, yellow smiley
            if (shape == 2) color = Palette[rng.Next(Palette.Length)];
            x = Math.Round(x); y = Math.Max(N / 2 - 1, Math.Min(N / 2, Math.Round(y)));   // on a whole pixel, so the picture lands exactly
            for (int k = 0; k + 1 < pts.Length; k += 2)
                sparks.Add(new Spark { X = x, Y = y, Vx = pts[k] * ShapeDrag, Vy = pts[k + 1] * ShapeDrag, Life = 2 + rng.NextDouble() * 0.2, Color = color, Shaped = true });
        }

        // 9 x 9 pictures; each lit pixel is one spark's place, measured from the middle
        static readonly string[][] ShapeArt = {
            new[] { "....#....", "....#....", "...###...", "#########", ".#######.", "..#####..", "..#####..", ".###.###.", ".##...##." },   // star
            new[] { ".........", ".##...##.", "#..#.#..#", "#...#...#", "#.......#", ".#.....#.", "..#...#..", "...#.#...", "....#...." },   // heart
            new[] { "...###...", ".##...##.", ".#.....#.", "#.......#", "#.......#", "#.......#", ".#.....#.", ".##...##.", "...###..." },   // ring
            new[] { ".........", "..#####..", ".#.....#.", "#..#.#..#", "#.......#", "#.#...#.#", "#..###..#", ".#.....#.", "..#####.." },   // smiley
        };
        static readonly double[][] Shapes = MakeShapes();     // each shape's spark places as x,y offsets (after ShapeArt)

        static double[][] MakeShapes()
        {
            var shapes = new double[ShapeArt.Length][];
            for (int k = 0; k < ShapeArt.Length; k++)
            {
                var pts = new List<double>();
                for (int y = 0; y < 9; y++)
                    for (int x = 0; x < 9; x++)
                        if (ShapeArt[k][y][x] == '#') { pts.Add(x - 4); pts.Add(y - 4); }
                shapes[k] = pts.ToArray();
            }
            return shapes;
        }

        void Burst(double x, double y, int stage, bool big)
        {
            if (!big) opening.Add(0);
            int n = (int)((big ? 28 : new[] { 8, 12, 14, 16 }[stage]) * (N == 10 ? 1 : 1.5));   // more sparks to fill a bigger screen
            double speed = (big ? 5.5 : new[] { 2.6, 3.2, 3.6, 4.0 }[stage]) * K;
            int c1 = Palette[rng.Next(Palette.Length)], c2 = Palette[rng.Next(Palette.Length)];
            if (stage == 0 && !big) c2 = c1;                        // the opening bursts are one color
            double turn = rng.NextDouble() * Math.PI;
            for (int k = 0; k < n; k++)
            {
                double a = turn + 2 * Math.PI * k / n + (rng.NextDouble() - 0.5) * 0.3, v = speed * (0.75 + rng.NextDouble() * 0.35);
                int color = big ? Palette[k % Palette.Length] : k % 2 == 0 ? c1 : c2;
                sparks.Add(new Spark { X = x, Y = y, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v - 0.5 * K, Life = (big ? 1.8 : 1.0) + rng.NextDouble() * 0.5, Color = color });
            }
            if (stage == 3 || big)   // finale bursts crackle with white sparkles
                for (int k = 0; k < (big ? 14 : 5); k++)
                {
                    double a = rng.NextDouble() * 2 * Math.PI, v = speed * 0.5 * rng.NextDouble();
                    sparks.Add(new Spark { X = x, Y = y, Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v, Life = 0.6 + rng.NextDouble() * 0.8, Color = 7, Crackle = true });
                }
        }
    }

    // Countdown timer: two rows of two digits, each changing digit rolling down into place like a flip counter, with a
    // bar down the left edge. It counts down a number of minutes (from each start), or to a set date and time. The rows
    // show the two largest units that fit: minutes over seconds (white, cyan), hours over minutes (yellow, white) or
    // days over hours (magenta, yellow). For a timer the bar drains as time runs out (green, yellow, then red); for a
    // date it is blue and drains over each top unit. Its top pixel blinks each second. The last 9 seconds are one big
    // digit that flashes white as it appears; at zero, rings burst from the middle and the zeros blink.
    class Countdown
    {
        public static volatile int Minutes = 5;              // set from Settings
        public static DateTime Until = DateTime.MinValue;    // set from Settings; MinValue = count down Minutes instead
        const double RollSeconds = 0.3;
        readonly double total;
        readonly DateTime end;
        readonly bool timer;
        // 10 x 10: 3 x 5 digits in rows 0-4 and 5-9; 16 x 16: 5 x 7 digits in rows 0-6 and 9-15
        readonly int N, Band, Bottom, X0, Stride;

        public Countdown() : this(10) { }
        public Countdown(int size)
        {
            N = size;
            bool big = size == 16;
            Band = big ? 7 : 5; Bottom = big ? 9 : 5; X0 = big ? 5 : 3; Stride = big ? 6 : 4;
            timer = Until == DateTime.MinValue;
            total = Minutes * 60;
            end = timer ? DateTime.Now.AddSeconds(total) : Until;
        }

        // the top and bottom numbers, their colors and the bottom unit in seconds, for this much time left
        static void Split(double left, out int top, out int bottom, out int topColor, out int bottomColor, out int unit)
        {
            if (left <= 99 * 60 + 59) { unit = 1; topColor = 7; bottomColor = 6; }
            else if (left <= 99 * 3600 + 59 * 60) { unit = 60; topColor = 3; bottomColor = 7; }
            else { unit = 3600; topColor = 5; bottomColor = 3; }
            int n = (int)Math.Ceiling(left / unit), per = unit == 3600 ? 24 : 60;
            top = Math.Min(99, n / per); bottom = n % per;
        }

        public int[] Next()
        {
            var px = new int[N * N];
            double left = (end - DateTime.Now).TotalSeconds;
            if (left <= 0) return Finished(-left, px);
            int top, bottom, topColor, bottomColor, unit;
            Split(left, out top, out bottom, out topColor, out bottomColor, out unit);
            int secs = (int)Math.Ceiling(left);
            if (secs <= 9)
            {
                Big(px, (char)('0' + secs), secs - left < 0.12 ? 7 : secs > 3 ? 3 : 1);
                return px;
            }
            int n = (int)Math.Ceiling(left / unit);
            double into = n * (double)unit - left;            // how long the bottom number has been up
            int top0, bottom0, c0, c1, unit0;                 // what was up just before it
            Split(left + into + 0.01, out top0, out bottom0, out c0, out c1, out unit0);
            int full = Band + 1;
            double k = unit0 != unit || (timer && secs >= total) || into >= RollSeconds ? full : into / RollSeconds * full;
            Row(px, top.ToString().PadLeft(2), top0.ToString().PadLeft(2), 0, topColor, k);
            Row(px, bottom.ToString("00"), bottom0.ToString("00"), Bottom, bottomColor, k);

            double frac, tick = secs - left;
            int bar;
            if (timer) { frac = left / total; bar = frac > 0.5 ? 2 : frac > 0.2 ? 3 : 1; }
            else { double big = unit == 3600 ? 86400 : unit * 60.0; frac = (left % big) / big; bar = 4; }
            int lit = Math.Max(1, (int)Math.Ceiling(frac * N));
            for (int y = N - lit; y < N; y++)
                if (y > N - lit || tick < 0.5) px[y * N] = bar;
            return px;
        }

        // a 2-digit row, right-aligned; a digit that changed slides down out of its row as the new one slides in on top
        void Row(int[] px, string now, string before, int y0, int color, double k)
        {
            int full = Band + 1;
            for (int i = 0; i < 2; i++)
            {
                int x0 = X0 + i * Stride, off = (int)k;
                if (now[i] == before[i] || off >= full) { Glyph(px, now[i], x0, y0, y0, color); continue; }
                Glyph(px, before[i], x0, y0 + off, y0, color);
                Glyph(px, now[i], x0, y0 - full + off, y0, color);
            }
        }

        // a digit with its top at row y, drawn only inside the band starting at y0 (3 x 5 font, or 5 x 7 on 16 x 16)
        void Glyph(int[] px, char ch, int x0, int y, int y0, int color)
        {
            if (ch == ' ') return;
            if (N == 16)
            {
                string g = ClockArt.Big[ch - '0'];
                for (int n = 0; n < 35; n++)
                {
                    int yy = y + n / 5;
                    if (g[n] == '#' && yy >= y0 && yy < y0 + 7) px[yy * 16 + x0 + n % 5] = color;
                }
                return;
            }
            string f = WeatherArt.Font[ch];
            for (int n = 0; n < 15; n++)
            {
                int yy = y + n / 3;
                if (f[n] == '1' && yy >= y0 && yy < y0 + 5) px[yy * 10 + x0 + n % 3] = color;
            }
        }

        // one digit at double size, centered (6 x 10, or 10 x 14 on 16 x 16)
        void Big(int[] px, char ch, int color)
        {
            if (N == 16)
            {
                string g = ClockArt.Big[ch - '0'];
                for (int n = 0; n < 35; n++)
                    if (g[n] == '#')
                        for (int d = 0; d < 4; d++) px[(1 + n / 5 * 2 + d / 2) * 16 + 3 + n % 5 * 2 + d % 2] = color;
                return;
            }
            string f = WeatherArt.Font[ch];
            for (int n = 0; n < 15; n++)
                if (f[n] == '1')
                    for (int d = 0; d < 4; d++) px[(n / 3 * 2 + d / 2) * 10 + 2 + n % 3 * 2 + d % 2] = color;
        }

        // time's up: a burst of rings from the middle, then the zeros blink red, every 4 seconds
        int[] Finished(double t, int[] px)
        {
            double phase = t % 4, K = N / 10.0, c = (N - 1) / 2.0;
            if (phase < 1.5)
            {
                double r = phase * 5 * K;
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        double d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                        if (Math.Abs(d - r) < 0.6) px[y * N + x] = 3;
                        else if (Math.Abs(d - (r - 1.3 * K)) < 0.6) px[y * N + x] = N == 16 ? Palette16.Orange : 1;
                        else if (Math.Abs(d - (r - 2.6 * K)) < 0.6) px[y * N + x] = N == 16 ? 1 : 5;
                        else if (N == 16 && Math.Abs(d - (r - 3.9 * K)) < 0.6) px[y * N + x] = 5;
                    }
            }
            else if ((int)(phase * 2) % 2 == 1)
            {
                Glyph(px, '0', X0 + Stride, 0, 0, 1);
                Row(px, "00", "00", Bottom, 1, Band + 1);
            }
            return px;
        }
    }

    // Heart monitor: an ECG trace sweeps left to right like a bedside monitor. The bright head leaves a green trace
    // behind and clears a gap just ahead of it. Each beat is a small P wave, the tall QRS spike (always 2 or 3 columns
    // from the left) and a rounded T wave. The trace moves with the clock at the heart rate set by the speed slider,
    // so on a slow link it jumps a few columns at a time, unevenly, like a scope. Now and then the heart misbehaves:
    // a skipped beat, an early wide extra beat (a PVC) with a pause after it, or rarely a flatline (amber, then red)
    // before it picks back up.
    class HeartMonitor
    {
        public static Func<int> Speed = () => 5;   // the speed slider, 1..10 (set from the main window)
        public static int Bpm(int speed) { return 20 + speed * 8; }   // 28 to 100 beats a minute
        // beats as heights above the baseline: P wave, Q dip, R spike, S dip, T wave; and a wide, early PVC.
        // 10 x 10 (AuraBox) and 16 x 16 (Tivoo, taller and with a fading trail) versions.
        static readonly int[] Beat10 = { 0, 1, 2, 1, 0, -2, 6, -3, 0, 0, 1, 2, 3, 2, 1 };
        static readonly int[] Pvc10 = { 0, 4, 6, 5, 2, -1, -3, -3, -2, -1 };
        static readonly int[] Beat16 = { 0, 1, 2, 3, 2, 1, 0, 0, -3, 10, -5, -1, 0, 0, 1, 2, 3, 4, 3, 2, 1 };
        static readonly int[] Pvc16 = { 0, 3, 7, 10, 8, 4, 0, -2, -4, -5, -4, -3, -2, -1 };
        const int Amber = 100, Red = 200;           // added to a queued row: part of a flatline, drawn in that color
        readonly int N, Baseline, Period, Spike, PvcSpike, First;   // Period: a normal beat takes two sweeps; First: spike column
        readonly int[] Beat, Pvc;
        readonly int[] trace;                       // each column's row, or -1 where cleared
        readonly int[] tint;                        // each column's color: green, or amber / red on a flatline
        readonly Queue<int> coming = new Queue<int>();
        readonly Random rng = new Random();
        int head;
        bool lastWasPvc;
        double owed = 1;                            // columns owed (heart rate x time)
        DateTime last = DateTime.Now;
        int[] frame;

        public HeartMonitor() : this(10) { }
        public HeartMonitor(int size)
        {
            N = size; Period = 2 * N; head = N - 1;
            if (N == 10) { Baseline = 6; Beat = Beat10; Pvc = Pvc10; Spike = 6; PvcSpike = 2; First = 2; }
            else { Baseline = 10; Beat = Beat16; Pvc = Pvc16; Spike = 9; PvcSpike = 3; First = 3; }
            trace = new int[N]; tint = new int[N]; frame = new int[N * N];
            for (int x = 0; x < N; x++) trace[x] = -1;
        }

        public int[] Next()
        {
            var now = DateTime.Now;
            double dt = Math.Min(0.5, (now - last).TotalSeconds);
            last = now;
            owed += dt * Bpm(Speed()) / 60.0 * Period;
            int steps = Math.Min(6, (int)owed);
            owed -= steps;
            for (int i = 0; i < steps; i++) Advance();
            if (steps > 0) frame = Draw();
            return frame;
        }

        static int Mod(int a, int n) { return (a % n + n) % n; }

        // flat line until a beat can start where its spike lands at column First or the one after
        void Align(int spikeIndex, int extra, int spikeAt = -1)
        {
            if (spikeAt < 0) spikeAt = First + rng.Next(2);
            int start = Mod(spikeAt - spikeIndex, N);
            for (int k = Mod(start - (head + 1 + coming.Count), N) + extra; k > 0; k--) coming.Enqueue(Baseline);
        }

        void Plan()
        {
            double roll = rng.NextDouble();
            if (!lastWasPvc && roll < 0.003)          // flatline for about 3 seconds' worth of beats, then a beat
            {
                for (int k = 0; k < 3 * Period; k++) coming.Enqueue(Baseline + (k < 3 * Period / 2 ? Amber : Red));   // the alarm worsens
                Align(Spike, 0);
                foreach (int h in Beat) coming.Enqueue(Baseline - h);
                lastWasPvc = false;
                return;
            }
            if (!lastWasPvc && roll < 0.05)           // a beat, then an early extra beat straight after it, then a pause
            {
                Align(Spike, 0, First);               // peaking at First leaves the PVC room to peak one column later, a sweep on
                foreach (int h in Beat) coming.Enqueue(Baseline - h);
                Align(PvcSpike, 0, First + 1);
                foreach (int h in Pvc) coming.Enqueue(Baseline - h);
                lastWasPvc = true;
                return;
            }
            Align(Spike, lastWasPvc || roll < 0.09 ? N : 0);   // a pause after a PVC, or now and then a skipped beat
            foreach (int h in Beat) coming.Enqueue(Baseline - h);
            lastWasPvc = false;
        }

        void Advance()
        {
            if (coming.Count == 0) Plan();
            int v = coming.Dequeue();
            tint[(head + 1) % N] = v >= Red ? 1 : v >= Amber ? 3 : 2;
            head = (head + 1) % N;
            trace[head] = v % 100;
            trace[(head + 1) % N] = trace[(head + 2) % N] = -1;   // the gap ahead of the head
        }

        int[] Draw()
        {
            var px = new int[N * N];
            for (int x = 0; x < N; x++)
            {
                int y = trace[x];
                if (y < 0) continue;
                int c = x == head && tint[x] == 2 ? 7 : tint[x], prev = trace[(x + N - 1) % N];   // a flatline's head keeps its alarm color
                // on the Tivoo the older half of the trace fades to dark green, like a monitor's phosphor
                if (N == 16 && c == 2 && Mod(head - x, N) > N / 2) c = Palette16.DarkGreen;
                // a steep rise is drawn as a line up the column; the fall back from the spike only shows below the
                // baseline, so the spike is one thin stroke and not two side by side
                int from = prev < 0 || Math.Abs(prev - y) <= 1 ? y : prev < y ? Math.Max(prev + 1, Baseline) : prev - 1;
                for (int r = Math.Min(from, y); r <= Math.Max(from, y); r++) px[r * N + x] = c;
            }
            return px;
        }
    }

    // Waving flag: a 9-row flag ripples in a breeze. Each column rides one pixel up or down on a wave that travels
    // from the pole (left) to the free end; the pole column stays put.
    class WavingFlag
    {
        public static readonly string[] Laos = {
            "1111111111", "1111111111", "4444444444", "4444774444", "4447777444", "4444774444", "4444444444", "1111111111", "1111111111" };
        readonly string[] rows;
        readonly int N, amp;
        int t;

        public WavingFlag(string[] rows) { this.rows = rows; N = 10; amp = 1; }

        // 16 x 16: the flag drawn to size (red, blue, red with a white disc), waving up to 2 pixels
        public WavingFlag(string[] rows, int size)
        {
            N = size; amp = size == 16 ? 2 : 1;
            this.rows = size == 16 && rows == Laos ? Laos16() : rows;
        }

        static string[] Laos16()
        {
            var r = new string[14];
            for (int y = 0; y < 14; y++)
            {
                var sb = new StringBuilder();
                for (int x = 0; x < 16; x++)
                {
                    double dx = x - 7.5, dy = y - 6.5;
                    sb.Append(y < 4 || y >= 10 ? '1' : dx * dx + dy * dy <= 5.3 ? '7' : '4');
                }
                r[y] = sb.ToString();
            }
            return r;
        }

        public int[] Next()
        {
            var px = new int[N * N];
            for (int x = 0; x < N; x++)
            {
                double w = Math.Sin(0.8 * x * 10 / N - 2 * Math.PI * t / 12);
                // the pole edge stays put; further out the wave grows
                int d = x == 0 ? 0 : amp == 1 ? (w > 0 ? 1 : 0) : (int)Math.Round((1 + w) * Math.Min(1.0, x / 4.0));
                for (int y = 0; y < rows.Length && y + d < N; y++) px[(y + d) * N + x] = rows[y][x] - '0';
            }
            t++;
            return px;
        }
    }

    // Bouncing balls: three 2x2 balls with gravity, bouncing off the walls and floor and losing a little energy;
    // when one settles it gets a fresh kick.
    class BouncingBalls
    {
        class Ball { public double X, Y, Vx, Vy; public int Color; }
        readonly List<Ball> balls = new List<Ball>();
        readonly Random rng = new Random();
        readonly int N, B;   // screen size, ball size
        readonly double kick;

        public BouncingBalls() : this(10) { }
        public BouncingBalls(int size)
        {
            N = size; B = size == 16 ? 3 : 2; kick = Math.Sqrt(size / 10.0);
            int[] colors = size == 16 ? new[] { 1, 3, 6, Palette16.Orange, Palette16.Lime } : new[] { 1, 3, 6 };
            foreach (int c in colors) balls.Add(new Ball { X = rng.Next(N - B), Y = rng.Next(N / 2), Vx = (rng.NextDouble() - 0.5) * 1.2, Vy = 0, Color = c });
        }

        public int[] Next()
        {
            var px = new int[N * N];
            double max = N - B;
            foreach (var b in balls)
            {
                b.Vy += 0.12;
                b.X += b.Vx; b.Y += b.Vy;
                if (b.X < 0) { b.X = -b.X; b.Vx = -b.Vx; }
                if (b.X > max) { b.X = 2 * max - b.X; b.Vx = -b.Vx; }
                if (b.Y > max)
                {
                    b.Y = max; b.Vy = -b.Vy * 0.82; b.Vx *= 0.97;
                    if (Math.Abs(b.Vy) < 0.35) { b.Vy = -(1.1 + rng.NextDouble() * 0.6) * kick; b.Vx = (rng.Next(2) * 2 - 1) * (0.25 + rng.NextDouble() * 0.4) * kick; }
                }
                int x = (int)Math.Round(b.X), y = (int)Math.Round(b.Y);
                for (int dy = 0; dy < B; dy++)
                    for (int dx = 0; dx < B; dx++)
                    {
                        if (B == 3 && dx != 1 && dy != 1) continue;   // round off the corners of a 3 x 3 ball
                        if (x + dx >= 0 && x + dx < N && y + dy < N && y + dy >= 0) px[(y + dy) * N + x + dx] = b.Color;
                    }
            }
            return px;
        }
    }

    // Brick breaker: a paddle on the bottom row keeps a ball in play and knocks out three rows of two-pixel bricks.
    // A hit brick flashes white; where the ball meets the paddle sets its new angle. Now and then the paddle
    // misses and serves again, and a cleared wall flashes through colors and is rebuilt.
    class BrickBreaker
    {
        // rows 0-3 from the top edge, alternating bricks: magenta/blue, red/magenta, yellow/red, green/cyan
        static readonly int[][] RowColors = { new[] { 5, 4 }, new[] { 1, 5 }, new[] { 3, 1 }, new[] { 2, 6 } };
        // 16 x 16 (Palette16): six rows of rainbow pairs, red, orange, yellow, green, blue, purple
        static readonly int[][] RowColors16 = { new[] { 1, Palette16.DarkRed }, new[] { Palette16.Orange, Palette16.DarkOrange },
            new[] { 3, Palette16.Amber }, new[] { 2, Palette16.DarkGreen }, new[] { Palette16.SkyBlue, 4 }, new[] { Palette16.Purple, 5 } };
        readonly int N;                             // the screen is N x N: 10 on the AuraBox, 16 on the Tivoo
        readonly int[][] rows;                      // brick colors per row from the top
        readonly int[] bricks;                      // brick color per pixel, 0 = empty
        readonly int[] flash;                       // frames left of a hit brick's white flash
        readonly Random rng = new Random();
        int bx, by, dx, dy, paddle, aim, wait, missed, cheer;   // paddle: left pixel of the 3-wide paddle
        int Top { get { return N - 3; } }           // the paddle's rightmost left position
        int Above { get { return N - 2; } }         // the row the ball meets the paddle from
        // misses since the paddle last hit the ball; at MaxMisses it stops missing on purpose and moves twice as fast
        int missStreak;
        const int MaxMisses = 2;

        public BrickBreaker() : this(10) { }
        public BrickBreaker(int size)
        {
            N = size; rows = N == 10 ? RowColors : RowColors16;
            bricks = new int[N * N]; flash = new int[N * N]; paddle = N / 2 - 2;
            Build(); Serve();
        }

        void Build()
        {
            Array.Clear(bricks, 0, N * N);
            for (int r = 0; r < rows.Length; r++)
                for (int x = 0; x < N; x++) bricks[r * N + x] = rows[r][x / 2 % 2];
        }

        // the ball rides on the paddle for a moment, then goes up at a random angle (or, near the end, toward a brick)
        void Serve()
        {
            bx = paddle + 1; by = Above; dy = -1; wait = 6;
            dx = rng.Next(2) == 0 ? -1 : 1;
            if (FewLeft() && StepsToBrick(bx, -dx) < StepsToBrick(bx, dx)) dx = -dx;
            PickAim();
        }

        // which part of the paddle to meet the ball with; once in a while it aims to miss (never near the end)
        void PickAim()
        {
            planned = false;
            aim = rng.Next(3) - 1;
            if (!FewLeft() && missStreak < MaxMisses && rng.NextDouble() < 0.06) aim = 4 * (rng.Next(2) * 2 - 1);
        }

        // Near the end of a wall the paddle plays it smart. The ball leaves the paddle up-left or up-right depending on
        // where it lands, so once it has dropped below the bricks, trace both ways it could go next (walls and bricks
        // included) and meet it with the part of the paddle that sends it toward the nearest brick.
        const int FewBricks = 5;
        bool planned;

        bool FewLeft() { int n = 0; foreach (int c in bricks) if (c != 0) n++; return n <= FewBricks * 2; }   // 2 pixels a brick

        void PlanAim()
        {
            planned = true;
            if (!FewLeft()) return;
            int d, land = LandingX(out d), best = int.MaxValue, bestAim = aim;
            for (int a = -1; a <= 1; a++)
            {
                // where the paddle would sit for this aim, and which way the ball would bounce off it
                int at = Math.Max(0, Math.Min(Top, land - a - 1)), off = land - (at + 1);
                if (off < -1 || off > 1) continue;    // the paddle can't get there (against a wall)
                int steps = StepsToBrick(land, off == 0 ? d : off);
                if (steps < best || (steps == best && rng.Next(2) == 0)) { best = steps; bestAim = a; }
            }
            aim = bestAim;
        }

        // How many steps the ball takes from the paddle (going up, moving dir) before it hits a brick. When it would come
        // back down first, it looks ahead up to "depth" more paddle hits (taking the better way up each time), so an angle
        // that sets up a hit on the next bounce still wins over one that wanders. int.MaxValue: no hit that soon.
        int StepsToBrick(int x, int dir, int depth = 2)
        {
            int y = Above, ddx = dir, ddy = -1;
            for (int s = 1; s <= N * 8; s++)
            {
                if (x + ddx < 0 || x + ddx > N - 1) ddx = -ddx;
                if (y + ddy < 0) ddy = -ddy;
                if (Brick(x + ddx, y) || Brick(x, y + ddy) || Brick(x + ddx, y + ddy)) return s;
                x += ddx; y += ddy;
                if (y >= Above)
                {
                    if (depth == 0) return int.MaxValue;
                    int next = Math.Min(StepsToBrick(x, -1, depth - 1), StepsToBrick(x, 1, depth - 1));
                    return next == int.MaxValue ? int.MaxValue : s + next;
                }
            }
            return int.MaxValue;
        }

        bool Brick(int x, int y) { return x >= 0 && x < N && y >= 0 && y < N && bricks[y * N + x] != 0; }

        void Hit(int x, int y)
        {
            int left = x - x % 2;                 // bricks are two pixels wide
            for (int k = left; k < left + 2; k++) { bricks[y * N + k] = 0; flash[y * N + k] = 2; }
        }

        // where the ball will reach the row above the paddle (walls only)
        int LandingX() { int d; return LandingX(out d); }

        // ...and which way it is moving when it gets there
        int LandingX(out int d)
        {
            int x = bx; d = dx;
            for (int y = by; y < Above; y++) { if (x + d < 0 || x + d > N - 1) d = -d; x += d; }
            return x;
        }

        void Step()
        {
            if (by == Above && dy > 0)
            {
                int nx = bx + dx;
                bool on = (bx >= paddle && bx <= paddle + 2) || (nx >= paddle && nx <= paddle + 2);
                if (on)
                {
                    int off = bx - (paddle + 1);      // edge hits send the ball off toward that side
                    if (off != 0) dx = off < 0 ? -1 : 1;
                    dy = -1; missStreak = 0; PickAim();
                }
                else { by = N - 1; bx = Math.Max(0, Math.Min(N - 1, nx)); missed = 6; missStreak++; return; }
            }
            if (bx + dx < 0 || bx + dx > N - 1) dx = -dx;
            if (by + dy < 0) dy = -dy;
            if (Brick(bx + dx, by)) { Hit(bx + dx, by); dx = -dx; }
            else if (Brick(bx, by + dy)) { Hit(bx, by + dy); dy = -dy; }
            else if (Brick(bx + dx, by + dy)) { Hit(bx + dx, by + dy); dx = -dx; dy = -dy; }
            else { bx += dx; by += dy; }
        }

        public int[] Next()
        {
            var px = new int[N * N];
            if (cheer > 0)
            {
                cheer--;
                for (int k = 0; k < N * N; k++) px[k] = cheer % 2 == 0 ? 0 : 1 + (k / N + cheer) % 7;
                if (cheer == 0) { Build(); Serve(); }
                return px;
            }
            if (missed > 0) { if (--missed == 0) Serve(); }
            else if (wait > 0) { wait--; bx = paddle + 1; }
            else Step();

            // once the ball is below the bricks and falling, near the end, choose the angle to send it back up at
            if (dy > 0 && by >= rows.Length && !planned && missed == 0 && wait == 0) PlanAim();
            // the paddle slides toward where the ball will come down, one pixel per frame
            int mid = N / 2 - 1;
            int center = missed > 0 || wait > 0 ? paddle + 1 : dy > 0 ? LandingX() - aim : mid + (bx - mid) / 2;
            int goal = Math.Max(0, Math.Min(Top, center - 1));
            if (wait == 0 && missed == 0)
                for (int move = missStreak >= MaxMisses ? 2 : 1; move > 0; move--)   // after two misses it hurries
                {
                    if (paddle < goal) paddle++; else if (paddle > goal) paddle--;
                }
            if (wait > 0) bx = paddle + 1;

            bool any = false;
            for (int k = 0; k < N * N; k++)
            {
                if (bricks[k] != 0) { px[k] = bricks[k]; any = true; }
                if (flash[k] > 0) { px[k] = 7; flash[k]--; }
            }
            for (int k = 0; k < 3; k++) px[(N - 1) * N + paddle + k] = 6;
            if (missed == 0 || missed % 2 == 0) px[by * N + bx] = 7;   // a missed ball blinks before the next serve
            if (!any && missed == 0) cheer = 10;
            return px;
        }
    }

    // Lava lamp: three blobs drift on slow, different paths; where their combined "pull" is strong the pixel is lava
    // (yellow core, red body) with a magenta glow at the edges.
    class LavaLamp
    {
        readonly double[] speedX = { 0.53, 0.37, 0.71 }, speedY = { 0.31, 0.47, 0.23 }, phase = { 0, 2.1, 4.2 }, size = { 2.2, 1.8, 2.0 };
        protected double t;
        protected readonly int N;      // the screen is N x N: 10 on the AuraBox, 16 on the Tivoo
        protected readonly double K;   // positions and blob sizes scale with it

        public LavaLamp() : this(10) { }
        public LavaLamp(int size) { N = size; K = N / 10.0; }

        // how strongly the blobs pull on one pixel
        protected double Field(int x, int y)
        {
            double f = 0;
            for (int k = 0; k < 3; k++)
            {
                double bx = (4.5 + 2.6 * Math.Sin(t * speedX[k] + phase[k])) * K, by = (4.5 + 3.8 * Math.Sin(t * speedY[k] + phase[k] * 1.3)) * K;
                double dx = (x - bx) / K, dy = (y - by) / K;
                f += size[k] * size[k] / (dx * dx + dy * dy + 0.3);
            }
            return f;
        }

        // yellow core, red, magenta glow (16 x 16: yellow, orange, dark red)
        protected int Shade(double f) { return f > 2.6 ? 3 : f > 1.2 ? (N == 10 ? 1 : Palette16.Orange) : f > 0.8 ? (N == 10 ? 5 : Palette16.DarkRed) : 0; }

        public virtual int[] Next()
        {
            t += 0.08;
            var px = new int[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++) px[y * N + x] = Shade(Field(x, y));
            return px;
        }
    }

    // Lava clock: the lava lamp, but at every new minute the time bubbles up from the bottom (hours over minutes,
    // each row left or right aligned at random), holds so it can be read, then melts: the digits drip down from the
    // top with glowing trails into a puddle on the bottom, which then bubbles back up into the lamp.
    // Every minute brings a new color theme.
    class LavaClock : LavaLamp
    {
        const double FormSeconds = 4, HoldSeconds = 8, MeltSeconds = 4, PoolSeconds = 2, RiseSeconds = 6;
        static readonly int[][] Themes10 = {                 // core, body, glow (palette indexes)
            new[] { 3, 1, 5 },   // classic: yellow, red, magenta
            new[] { 7, 6, 4 },   // ocean: white, cyan, blue
            new[] { 3, 2, 6 },   // toxic: yellow, green, cyan
            new[] { 7, 5, 4 },   // berry: white, magenta, blue
            new[] { 7, 1, 3 },   // ember: white, red, yellow
        };
        static readonly int[][] Themes16 = {                 // the same themes with the Tivoo's in-between colors (Palette16)
            new[] { 3, Palette16.Orange, Palette16.DarkRed },     // classic: yellow, orange, dark red
            new[] { 7, 6, Palette16.Navy },                       // ocean: white, cyan, navy
            new[] { Palette16.Lime, 2, Palette16.DarkGreen },     // toxic: lime, green, dark green
            new[] { Palette16.Pink, 5, Palette16.Purple },        // berry: pink, magenta, purple
            new[] { 7, Palette16.Amber, Palette16.DarkOrange },   // ember: white, amber, dark orange
        };
        readonly int[][] Themes;
        public static readonly string[] ThemeNames = { "Classic", "Ocean", "Toxic", "Berry", "Ember" };
        public static volatile int FixedTheme = -1;         // -1 = a new theme every minute (set from Settings)
        public static volatile bool Puddle = true;          // melt into a puddle that bubbles back up, or just drip away
        class Bit { public double X, Y, Speed, Delay; public int Color, Top, Len = 1; }

        readonly bool h24;
        readonly Random rng = new Random();
        readonly double[] wobble;                           // uneven, bubbling edge of the rising time
        readonly List<Bit> bubbles = new List<Bit>(), drips = new List<Bit>(), side = new List<Bit>();
        // columns beside the hour and beside the minutes with room for bubbles
        readonly List<int>[] freeColumns = { new List<int>(), new List<int>() };
        // the digit rows: hours from bandTop[0], minutes from bandTop[1], each Band rows tall (3 x 5 digits, or 5 x 7 at 16 x 16)
        readonly int Band;
        readonly int[] bandTop;
        // the melted time pools on the bottom: one stack of colors per column, then breaks into rising blobs
        readonly List<int>[] puddle;
        readonly List<Bit> blobs = new List<Bit>();
        int poolTotal = -1;

        int PoolSize() { int n = 0; foreach (var c in puddle) n += c.Count; return n; }

        int TallestColumn()
        {
            int best = 0;
            for (int x = 1; x < N; x++)
                if (puddle[x].Count > puddle[best].Count || (puddle[x].Count == puddle[best].Count && rng.Next(2) == 0)) best = x;
            return best;
        }

        // liquid finds its level: a column two higher than its neighbor spills its top pixel over
        void Settle()
        {
            int start = rng.Next(N);
            for (int i = 0; i < N; i++)
            {
                int x = (start + i) % N, n = x + (rng.Next(2) == 0 ? -1 : 1);
                if (n < 0 || n > N - 1) continue;
                var a = puddle[x];
                if (a.Count > puddle[n].Count + 1) { puddle[n].Add(a[a.Count - 1]); a.RemoveAt(a.Count - 1); }
            }
        }
        int lastMinute = -1, theme, nextTheme = -1;
        // the next minute's colors seep into the lamp over the end of each minute instead of switching at once:
        // each pixel changes when the blend passes its seed (lower rows first, unevenly)
        const double BlendSeconds = 24;
        readonly double[] seed;
        double blend;
        int[] face;                                         // 1 = hour pixel, 2 = minute pixel
        DateTime morphStart, lastTick = DateTime.Now;
        bool dripping;

        public LavaClock(bool h24) : this(h24, 10) { }
        public LavaClock(bool h24, int size) : base(size)
        {
            this.h24 = h24;
            Themes = N == 10 ? Themes10 : Themes16; theme = rng.Next(Themes.Length);
            Band = N == 10 ? 5 : 7; bandTop = new[] { 0, N - Band };
            wobble = new double[N]; seed = new double[N * N]; puddle = new List<int>[N];
            for (int x = 0; x < N; x++) puddle[x] = new List<int>();
        }

        static int ShadeWith(double f, int[] th) { return f > 2.6 ? th[0] : f > 1.2 ? th[1] : f > 0.8 ? th[2] : 0; }

        // lamp pixel in this minute's colors, or the next minute's once the blend has reached it (the edge wavers a little)
        int Shade(double f, int x, int y, int[] th)
        {
            bool next = blend > 0 && seed[y * N + x] + 0.06 * Math.Sin(t * 1.7 + x * 0.9 + y * 0.6) < blend;
            return ShadeWith(f, next ? Themes[nextTheme] : th);
        }

        void Set(int[] px, int x, int y, int c) { if (x >= 0 && x < N && y >= 0 && y < N) px[y * N + x] = c; }

        int Digit(int at, int[] th) { return face[at] == 1 ? th[0] : face[at] == 2 ? th[1] : 0; }

        public override int[] Next()
        {
            t += 0.08;
            DateTime now = DateTime.Now;
            double dt = Math.Max(0, Math.Min(0.5, (now - lastTick).TotalSeconds));
            lastTick = now;

            int minute = now.Hour * 60 + now.Minute;
            if (minute != lastMinute)                       // also runs right after starting, so the time shows at once
            {
                lastMinute = minute; morphStart = now; dripping = false; drips.Clear();
                foreach (var c in puddle) c.Clear();
                blobs.Clear(); poolTotal = -1;
                bool fixedTheme = FixedTheme >= 0 && FixedTheme < Themes.Length;
                if (fixedTheme) theme = FixedTheme;
                else if (nextTheme >= 0) theme = nextTheme;     // already blended in over the end of the last minute
                else theme = (theme + 1 + rng.Next(Themes.Length - 1)) % Themes.Length;
                nextTheme = fixedTheme ? theme : (theme + 1 + rng.Next(Themes.Length - 1)) % Themes.Length;   // a different theme each minute
                for (int at = 0; at < N * N; at++) seed[at] = 0.55 * rng.NextDouble() + 0.45 * (1 - at / N / (N - 1.0));
                face = N == 10 ? ClockArt.Face(now, h24, 1, 2, rng.Next(2) == 0, rng.Next(2) == 0)
                               : ClockArt.Face16(now, h24, 1, 2, rng.Next(2) == 0, rng.Next(2) == 0);
                // free space beside each row of digits: columns with no digit pixel and at least one column away from one
                side.Clear();
                for (int band = 0; band < 2; band++)
                {
                    freeColumns[band].Clear();
                    for (int x = 0; x < N; x++)
                    {
                        bool near = false;
                        for (int y = bandTop[band]; y < bandTop[band] + Band; y++)
                            for (int dx = -1; dx <= 1; dx++)
                                if (x + dx >= 0 && x + dx < N && face[y * N + x + dx] != 0) near = true;
                        if (!near) freeColumns[band].Add(x);
                    }
                }
                for (int x = 0; x < N; x++) wobble[x] = rng.NextDouble() * 1.6;
            }
            int[] th = Themes[theme];
            double s = (now - morphStart).TotalSeconds;
            double toNextMinute = 60 - now.Second - now.Millisecond / 1000.0;
            blend = nextTheme == theme || s < FormSeconds + HoldSeconds + MeltSeconds + PoolSeconds + RiseSeconds ? 0
                  : Math.Max(0, Math.Min(1, (BlendSeconds - toNextMinute) / BlendSeconds));
            var px = new int[N * N];

            if (s < FormSeconds)
            {
                // the time rises out of the lava from the bottom; the edge bubbles and throws up bright bubbles
                double fill = N + 0.5 - s / FormSeconds * (N + 2.5);
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        double edge = fill + wobble[x];
                        int at = y * N + x;
                        if (y >= edge) px[at] = Digit(at, th);
                        else if (y >= edge - 1) px[at] = rng.NextDouble() < 0.5 ? th[rng.Next(3)] : 7;   // fizzing crest
                        else px[at] = ShadeWith(Field(x, y), th);
                    }
                if (rng.NextDouble() < 0.8)
                {
                    int x = rng.Next(N);
                    bubbles.Add(new Bit { X = x, Y = fill + wobble[x] - 1, Speed = 6 + rng.NextDouble() * 5, Color = rng.NextDouble() < 0.3 ? 7 : th[rng.Next(3)] });
                }
            }
            else if (s < FormSeconds + HoldSeconds)
            {
                for (int at = 0; at < N * N; at++) px[at] = Digit(at, th);   // clean, readable time
                // colored bubbles keep rising in the free space next to the hour and next to the minutes, when there is any
                for (int band = 0; band < 2; band++)
                {
                    var free = freeColumns[band];
                    if (free.Count > 0 && rng.NextDouble() < 0.45)
                        side.Add(new Bit { X = free[rng.Next(free.Count)], Y = bandTop[band] + Band, Top = bandTop[band], Speed = 1.5 + rng.NextDouble() * 2,
                                           Color = rng.NextDouble() < 0.15 ? 7 : th[rng.Next(3)], Len = rng.NextDouble() < 0.35 ? 2 : 1 });
                }
                for (int i = side.Count - 1; i >= 0; i--)
                {
                    var b = side[i];
                    b.Y -= b.Speed * dt;
                    if (b.Y < b.Top - b.Len) { side.RemoveAt(i); continue; }
                    int y = (int)Math.Floor(b.Y);
                    for (int k = 0; k < b.Len; k++)                 // some bubbles are two pixels tall
                        if (y + k >= b.Top && y + k < b.Top + Band) px[(y + k) * N + (int)b.X] = b.Color;
                }
            }
            else
            {
                // melt: digits let go top rows first and fall with gravity, trailing glow, into a puddle on the bottom
                double m = s - FormSeconds - HoldSeconds;
                if (!dripping)
                {
                    dripping = true;
                    for (int at = 0; at < N * N; at++)
                        if (face[at] != 0)
                            drips.Add(new Bit { X = at % N, Y = at / N, Delay = at / N * 0.2 * 10 / N + rng.NextDouble() * 0.5, Color = Digit(at, th) });
                }
                double rise = m - MeltSeconds - PoolSeconds;
                if (!Puddle)
                {
                    // no puddle: the drips fall off the bottom while the lamp swells back in
                    double back = Math.Min(1, m / MeltSeconds);
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++) px[y * N + x] = Shade(Field(x, y) * back, x, y, th);
                }
                else if (rise > 0)
                {
                    // the puddle bubbles back up: blobs break off its surface and float up while the lamp swells back in
                    double back = Math.Min(1, rise / RiseSeconds);
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++) px[y * N + x] = Shade(Field(x, y) * back, x, y, th);
                    if (poolTotal < 0) poolTotal = PoolSize();
                    while (PoolSize() > poolTotal * (1 - back))
                    {
                        int col = TallestColumn();
                        var stack = puddle[col];
                        blobs.Add(new Bit { X = col, Y = N - 1 - (stack.Count - 1), Speed = 1.5 + rng.NextDouble() * 2.5, Color = stack[stack.Count - 1] });
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                else if (rng.NextDouble() < 0.5) Settle();          // the molten digits spread out level
                for (int i = drips.Count - 1; i >= 0; i--)
                {
                    var d = drips[i];
                    if (m > d.Delay) { d.Speed += 14 * dt; d.Y += d.Speed * dt; }
                    int x = (int)d.X, y = (int)Math.Floor(d.Y);
                    var stack = puddle[x];
                    if (Puddle && d.Y >= N - 1 - stack.Count) { stack.Add(d.Color); drips.RemoveAt(i); continue; }   // lands on the puddle
                    if (d.Y > N + 0.5) { drips.RemoveAt(i); continue; }
                    if (d.Speed > 0) { Set(px, x, y - 1, th[2]); if (d.Speed > 4) Set(px, x, y - 2, th[2]); }   // melty trail
                    Set(px, x, y, d.Color);
                }
                for (int x = 0; x < N; x++)
                    for (int k = 0; k < puddle[x].Count; k++)
                    {
                        bool surface = k == puddle[x].Count - 1;
                        Set(px, x, N - 1 - k, surface && rng.NextDouble() < 0.12 ? th[2] : puddle[x][k]);   // shimmering surface
                    }
                for (int i = blobs.Count - 1; i >= 0; i--)
                {
                    var b = blobs[i];
                    b.Y -= b.Speed * dt;
                    if (b.Y < -1) { blobs.RemoveAt(i); continue; }
                    int y = (int)Math.Floor(b.Y);
                    Set(px, (int)b.X, y, b.Color);
                    Set(px, (int)b.X, y + 1, th[2]);                // glowing tail
                }
            }

            // small bubbles always drift up through the dark parts of the lava
            if (s >= FormSeconds + HoldSeconds + MeltSeconds + PoolSeconds && rng.NextDouble() < 0.15)
                bubbles.Add(new Bit { X = rng.Next(N), Y = N, Speed = 2 + rng.NextDouble() * 2, Color = (rng.NextDouble() < blend ? Themes[nextTheme] : th)[rng.Next(3)] });   // late bubbles carry the next colors
            for (int i = bubbles.Count - 1; i >= 0; i--)
            {
                var b = bubbles[i];
                b.Y -= b.Speed * dt;
                if (b.Y < -1) { bubbles.RemoveAt(i); continue; }
                int x = (int)b.X, y = (int)Math.Floor(b.Y);
                bool holding = s >= FormSeconds && s < FormSeconds + HoldSeconds;
                if (!holding && y >= 0 && y < N && px[y * N + x] == 0) px[y * N + x] = b.Color;   // never cover the time
            }
            return px;
        }
    }

    // An owner-drawn list that screen readers and other accessibility tools can still read: each row is reported
    // with its text, headings as plain text (not pickable), and the picked row as selected and focused.
    class AccessibleListBox : ListBox
    {
        public Func<object, string> NameOf = o => o == null ? "" : o.ToString();
        public Func<object, bool> IsHeading = o => false;

        protected override AccessibleObject CreateAccessibilityInstance() { return new ListAccessible(this); }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            if (SelectedIndex >= 0 && IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.Selection, SelectedIndex);
                if (Focused) AccessibilityNotifyClients(AccessibleEvents.Focus, SelectedIndex);
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (SelectedIndex >= 0) AccessibilityNotifyClients(AccessibleEvents.Focus, SelectedIndex);
        }

        class ListAccessible : ControlAccessibleObject
        {
            readonly AccessibleListBox box;
            public ListAccessible(AccessibleListBox b) : base(b) { box = b; }
            public override AccessibleRole Role { get { return AccessibleRole.List; } }
            public override int GetChildCount() { return box.Items.Count; }
            public override AccessibleObject GetChild(int i) { return i >= 0 && i < box.Items.Count ? new RowAccessible(box, i, this) : null; }
            public override AccessibleObject GetSelected() { return GetChild(box.SelectedIndex); }
            public override AccessibleObject GetFocused() { return box.Focused ? GetChild(box.SelectedIndex) : null; }
            public override AccessibleObject HitTest(int x, int y)
            {
                int i = box.IndexFromPoint(box.PointToClient(new Point(x, y)));
                return i >= 0 ? GetChild(i) : base.HitTest(x, y);
            }
        }

        class RowAccessible : AccessibleObject
        {
            readonly AccessibleListBox box; readonly int index; readonly AccessibleObject parent;
            public RowAccessible(AccessibleListBox b, int i, AccessibleObject p) { box = b; index = i; parent = p; }
            bool Heading { get { return box.IsHeading(box.Items[index]); } }
            public override string Name { get { return box.NameOf(box.Items[index]); } set { } }
            public override AccessibleRole Role { get { return Heading ? AccessibleRole.StaticText : AccessibleRole.ListItem; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override Rectangle Bounds { get { return box.RectangleToScreen(box.GetItemRectangle(index)); } }
            public override string DefaultAction { get { return Heading ? "" : "Select"; } }
            public override AccessibleStates State
            {
                get
                {
                    if (Heading) return AccessibleStates.ReadOnly;
                    var s = AccessibleStates.Selectable | AccessibleStates.Focusable;
                    if (box.SelectedIndex == index) s |= AccessibleStates.Selected | (box.Focused ? AccessibleStates.Focused : AccessibleStates.None);
                    if (!box.ClientRectangle.IntersectsWith(box.GetItemRectangle(index))) s |= AccessibleStates.Offscreen;
                    return s;
                }
            }
            public override void DoDefaultAction() { if (!Heading) box.SelectedIndex = index; }
            public override void Select(AccessibleSelection flags)
            {
                if (Heading) return;
                if ((flags & AccessibleSelection.TakeFocus) != 0) box.Focus();
                if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection)) != 0) box.SelectedIndex = index;
            }
            public override AccessibleObject Navigate(AccessibleNavigation dir)
            {
                switch (dir)
                {
                    case AccessibleNavigation.Next: case AccessibleNavigation.Down: return parent.GetChild(index + 1);
                    case AccessibleNavigation.Previous: case AccessibleNavigation.Up: return parent.GetChild(index - 1);
                    default: return null;
                }
            }
        }
    }

    // Everything the Gallery downloads is kept on this PC, in settings\cache: every design's file for good (a design
    // never changes once it is uploaded, and nothing here is ever deleted automatically) and every list page, which is
    // used again for a few hours, so pages already seen open at once, Divoom is asked far less often, and lists already
    // seen still work offline. The page counts the Gallery measures are kept for a day.
    static class DesignCache
    {
        public static readonly TimeSpan ListFresh = TimeSpan.FromHours(6);
        static readonly object gate = new object();
        static Dictionary<string, KeyValuePair<int, DateTime>> totals;

        public static string Dir { get { return Settings.DataFile("cache"); } }

        static string PathFor(string kind, string key)
        {
            string h;
            using (var md5 = System.Security.Cryptography.MD5.Create())
                h = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
            return System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(Dir, kind), h.Substring(0, 2)), h + ".bin");
        }

        // a design's file: saved the first time it is downloaded, read from disk after that
        public static byte[] File(string key, Func<byte[]> download)
        {
            string p = PathFor("designs", key);
            try { if (System.IO.File.Exists(p)) return System.IO.File.ReadAllBytes(p); } catch (Exception) { }
            var d = download();
            Save(p, d);
            return d;
        }

        // the design-file key for a Gallery entry's Id (Divoom 16 x 16 and LaMetric Ids carry their prefix already)
        public static string KeyOf(string entryId)
        {
            return entryId.StartsWith(DivoomCloud16.Prefix) || entryId.StartsWith(LaMetricIcons.Prefix) ? entryId : "classic:" + entryId;
        }

        // a saved design's English name (as the Gallery shows it), kept beside its file; null when there is none yet
        public static string Name(string key)
        {
            try
            {
                string p = System.IO.Path.ChangeExtension(PathFor("designs", key), ".name");
                return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p, Encoding.UTF8) : null;
            }
            catch (Exception) { return null; }
        }

        public static void SetName(string key, string name)
        {
            Save(System.IO.Path.ChangeExtension(PathFor("designs", key), ".name"), Encoding.UTF8.GetBytes(name ?? ""));
        }

        // a list page: the saved copy while it is fresh; otherwise downloaded again, falling back to the saved copy
        // (however old) when Divoom can't be reached
        public static string List(string key, TimeSpan fresh, Func<string> download)
        {
            string p = PathFor("lists", key);
            bool have = false;
            try
            {
                have = System.IO.File.Exists(p);
                if (have && DateTime.Now - System.IO.File.GetLastWriteTime(p) < fresh) return Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(p));
            }
            catch (Exception) { }
            try
            {
                string s = download();
                Save(p, Encoding.UTF8.GetBytes(s));
                return s;
            }
            catch (Exception)
            {
                if (have) try { return Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(p)); } catch (Exception) { }
                throw;
            }
        }

        static void Save(string p, byte[] d)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";   // written whole, then renamed into place
                System.IO.File.WriteAllBytes(tmp, d);
                try { System.IO.File.Copy(tmp, p, true); } finally { System.IO.File.Delete(tmp); }
            }
            catch (Exception) { }   // a full or read-only disk only costs the speed-up
        }

        // page counts measured for the Gallery (slow to find), remembered for a day
        static string TotalsPath { get { return System.IO.Path.Combine(Dir, "totals.txt"); } }

        public static bool TryTotal(string key, out int total)
        {
            total = 0;
            lock (gate)
            {
                LoadTotals();
                KeyValuePair<int, DateTime> t;
                if (!totals.TryGetValue(key, out t) || DateTime.Now - t.Value > TimeSpan.FromDays(1)) return false;
                total = t.Key;
                return true;
            }
        }

        public static void SetTotal(string key, int total)
        {
            lock (gate)
            {
                LoadTotals();
                totals[key] = new KeyValuePair<int, DateTime>(total, DateTime.Now);
                var sb = new StringBuilder();
                foreach (var kv in totals)
                    if (DateTime.Now - kv.Value.Value < TimeSpan.FromDays(1))
                        sb.Append(kv.Key.Replace("\t", " ")).Append('\t').Append(kv.Value.Key).Append('\t').Append(kv.Value.Value.ToString("o")).Append("\r\n");
                try { System.IO.Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(TotalsPath, sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            }
        }

        static void LoadTotals()
        {
            if (totals != null) return;
            totals = new Dictionary<string, KeyValuePair<int, DateTime>>();
            try
            {
                if (!System.IO.File.Exists(TotalsPath)) return;
                foreach (string line in System.IO.File.ReadAllLines(TotalsPath, Encoding.UTF8))
                {
                    string[] f = line.Split('\t');
                    int n; DateTime when;
                    if (f.Length == 3 && int.TryParse(f[1], out n) &&
                        DateTime.TryParse(f[2], null, System.Globalization.DateTimeStyles.RoundtripKind, out when)) totals[f[0]] = new KeyValuePair<int, DateTime>(n, when);
                }
            }
            catch (Exception) { }
        }

        // forget every saved design, list page and page count (they download again when browsed); names given by the
        // user (renames.txt) and favorites are kept
        public static void Clear()
        {
            lock (gate) totals = null;
            foreach (string sub in new[] { "designs", "lists" })
            {
                string d = System.IO.Path.Combine(Dir, sub);
                if (System.IO.Directory.Exists(d)) System.IO.Directory.Delete(d, true);
            }
            if (System.IO.File.Exists(TotalsPath)) System.IO.File.Delete(TotalsPath);
        }

        // how many design files are saved, and their size in bytes
        public static void Usage(out int files, out long bytes)
        {
            files = 0; bytes = 0;
            try
            {
                string d = System.IO.Path.Combine(Dir, "designs");
                if (!System.IO.Directory.Exists(d)) return;
                foreach (var f in new System.IO.DirectoryInfo(d).GetFiles("*.bin", System.IO.SearchOption.AllDirectories)) { files++; bytes += f.Length; }
            }
            catch (Exception) { }
        }
    }

    // LaMetric's public icon gallery: 8x8 community icons (many animated GIFs), no account needed. They're fetched
    // while browsing, like the Divoom cloud; none are shipped with the app.
    static class LaMetricIcons
    {
        const string ListUrl = "https://developer.lametric.com/api/v2/icons";
        public const string Prefix = "lametric:";   // a gallery entry whose Id is a LaMetric GIF address

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "VaVaVoomControl";
            return wc;
        }

        // one page of DivoomCloud.PageSize icons, sorted "popular" or "newest"; pages: how many pages the list has
        public static List<DivoomCloud.Entry> List(string order, int page, out int pages)
        {
            string url = ListUrl + "?page=" + page + "&page_size=" + DivoomCloud.PageSize + "&order=" + order;
            string json = DesignCache.List(url, DesignCache.ListFresh, delegate { using (var wc = Client()) return wc.DownloadString(url); });
            var count = Regex.Match(json, "\"page_count\":(\\d+)");
            pages = count.Success ? int.Parse(count.Groups[1].Value) : 1;
            var list = new List<DivoomCloud.Entry>();
            foreach (Match m in Regex.Matches(json, "\"title\":\"((?:[^\"\\\\]|\\\\.)*)\".*?\"url\":\"((?:[^\"\\\\]|\\\\.)*)\""))
                list.Add(new DivoomCloud.Entry { Id = Prefix + Regex.Unescape(m.Groups[2].Value), Name = Regex.Unescape(m.Groups[1].Value) });
            return list;
        }

        // download an icon and turn it into AuraBox frames: centered on the 10x10 screen (shrunk if it is bigger),
        // each pixel snapped to the box's 8 colors, see-through pixels off
        public static List<int[]> Frames(string url)
        {
            byte[] data = DesignCache.File(Prefix + url, delegate { using (var wc = Client()) return wc.DownloadData(url); });
            var frames = new List<int[]>();
            using (var img = Image.FromStream(new System.IO.MemoryStream(data)))
            {
                var dim = new System.Drawing.Imaging.FrameDimension(img.FrameDimensionsList[0]);
                int count = img.GetFrameCount(dim);
                for (int f = 0; f < count; f++)
                {
                    img.SelectActiveFrame(dim, f);
                    using (var bmp = new Bitmap(img))
                    {
                        double scale = Math.Min(1.0, 10.0 / Math.Max(bmp.Width, bmp.Height));
                        int w = Math.Max(1, (int)(bmp.Width * scale)), h = Math.Max(1, (int)(bmp.Height * scale));
                        int ox = (10 - w) / 2, oy = (10 - h) / 2;
                        var px = new int[100];
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                var c = bmp.GetPixel(Math.Min(bmp.Width - 1, (int)(x / scale)), Math.Min(bmp.Height - 1, (int)(y / scale)));
                                px[(oy + y) * 10 + ox + x] = c.A < 128 ? 0 : Proto.Nearest(c);
                            }
                        frames.Add(px);
                    }
                }
            }
            if (frames.Count == 0) throw new Exception("No pictures in the icon");
            return frames;
        }
    }

    // Divoom's original cloud (Timebox Mini era): 11x11 pictures and animations, no login needed.
    static class DivoomCloud
    {
        const string ListUrl = "https://appin.divoom-gz.com/GetCategoryFileList", FileUrl = "https://f.divoom-gz.com/";
        public const int PageSize = 30;

        public class Entry { public string Id, Name; }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "okhttp/3.12.13";
            return wc;
        }

        // category: 1 featured, 2 popular, 3 new; animations: FileType 1, pictures: FileType 5
        public static List<Entry> List(int category, bool animations, int page)
        {
            int start = page * PageSize + 1;
            string body = Body(category, animations, start, start + PageSize - 1);
            return Entries(DesignCache.List("classic:" + body, DesignCache.ListFresh, () => FetchJson(body)));
        }

        // How many designs a list has. The server's FileListNum is the whole library (the same for every list),
        // so find the last position that still returns a design: double until one is empty, then halve the gap.
        public static int Count(int category, bool animations)
        {
            Func<int, bool> has = n => Fetch(category, animations, n, n).Count > 0;
            int lo = 0, hi = 1;
            while (hi < 1000000 && has(hi)) { lo = hi; hi *= 2; }
            while (hi - lo > 1) { int mid = lo + (hi - lo) / 2; if (has(mid)) lo = mid; else hi = mid; }
            return lo;
        }

        static List<Entry> Fetch(int category, bool animations, int start, int end) { return Entries(FetchJson(Body(category, animations, start, end))); }

        static string Body(int category, bool animations, int start, int end)
        {
            return "{\"Category\":" + category + ",\"Command\":\"GetCategoryFileList\",\"DeviceId\":0,\"EndNum\":" + end +
                   ",\"FileSize\":0,\"FileType\":" + (animations ? 1 : 5) + ",\"StartNum\":" + start + ",\"Token\":0,\"UserId\":0}";
        }

        static string FetchJson(string body)
        {
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
                wc.Encoding = Encoding.UTF8;
                return wc.UploadString(ListUrl, body);
            }
        }

        static List<Entry> Entries(string json)
        {
            var list = new List<Entry>();
            foreach (Match m in Regex.Matches(json, "\"FileId\":\"((?:[^\"\\\\]|\\\\.)*)\",\"FileName\":\"((?:[^\"\\\\]|\\\\.)*)\""))
                list.Add(new Entry { Id = Regex.Unescape(m.Groups[1].Value), Name = Regex.Unescape(m.Groups[2].Value) });
            return list;
        }

        // download and convert to 10x10 AuraBox frames (crop the 11x11 art, snap to the 8 colors)
        public static List<int[]> Frames(string id)
        {
            byte[] d = DesignCache.File("classic:" + id, delegate { using (var wc = Client()) return wc.DownloadData(FileUrl + id); });
            var rgb = new List<byte[]>();
            if (d.Length >= 1 + 363 && d[0] == 1) rgb.Add(Sub(d, 1));
            else if (d.Length >= 4 + 363 && d[0] == 2)
                for (int k = 0; k < d[1] && 4 + (k + 1) * 363 <= d.Length; k++) rgb.Add(Sub(d, 4 + k * 363));
            else throw new Exception("Unsupported Divoom file format " + (d.Length > 0 ? d[0] : -1));

            // drop the edge row/column that loses the fewest lit pixels
            int bestX = 0, bestY = 0, best = int.MaxValue;
            for (int ox = 0; ox < 2; ox++)
                for (int oy = 0; oy < 2; oy++)
                {
                    int lost = 0;
                    foreach (var f in rgb)
                        for (int y = 0; y < 11; y++)
                            for (int x = 0; x < 11; x++)
                                if ((x < ox || x >= ox + 10 || y < oy || y >= oy + 10) && Lit(f, x, y)) lost++;
                    if (lost < best) { best = lost; bestX = ox; bestY = oy; }
                }
            var frames = new List<int[]>();
            foreach (var f in rgb)
            {
                var px = new int[100];
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 10; x++)
                    {
                        int i = ((y + bestY) * 11 + x + bestX) * 3;
                        px[y * 10 + x] = Snap(f[i], f[i + 1], f[i + 2]);
                    }
                frames.Add(px);
            }
            return frames;
        }

        static byte[] Sub(byte[] d, int at) { var b = new byte[363]; Array.Copy(d, at, b, 0, 363); return b; }
        static bool Lit(byte[] f, int x, int y) { int i = (y * 11 + x) * 3; return Math.Max(f[i], Math.Max(f[i + 1], f[i + 2])) >= 60; }

        static int Snap(int r, int g, int b)
        {
            int m = Math.Max(r, Math.Max(g, b));
            if (m < 60) return 0;
            double t = m * 0.55;
            return (r >= t ? 1 : 0) | (g >= t ? 2 : 0) | (b >= t ? 4 : 0);
        }
    }

    // Divoom's newer gallery of 16 x 16 art (the Tivoo's size; https://app.divoom-gz.com, no account needed to browse).
    // Files at f.divoom-gz.com/<FileId>: type 9 = animation [09, frame count, ms per frame (big-endian 16 bits)],
    // type 8 = picture [08]; then AES-128-CBC with the Divoom app's fixed key of 768-byte RGB frames
    // (key from github.com/redphx/apixoo; the frame layout checked against live files on 2026-11-09).
    // Browsing a category and searching need no account; My Likes and My Uploads use DivoomAccount's sign-in.
    static class DivoomCloud16
    {
        const string Api = "https://app.divoom-gz.com/", FileUrl = "https://f.divoom-gz.com/";
        public const string Prefix = "d16:";
        public const int PageSize = 30;
        static readonly Dictionary<string, int> ms = new Dictionary<string, int>();   // FileId -> time per frame

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "VaVaVoomControl/1.0";
            return wc;
        }

        // one of Divoom's JSON commands; fields is the JSON body without its braces. When asked too often Divoom
        // answers "Request data is null" (code 12): wait a moment and ask again, up to twice.
        public static string Post(string command, string fields)
        {
            for (int tries = 0; ; tries++)
            {
                try
                {
                    try { return PostOnce(command, fields); }
                    catch (WebException e) { if (TooMany(e)) throw new BusyException(); throw; }
                }
                catch (BusyException) { if (tries >= 2) throw new Exception("Divoom is busy; try again in a minute"); System.Threading.Thread.Sleep(1500 * (tries + 1)); }
            }
        }

        class BusyException : Exception { }

        // Divoom's "too many requests" (HTTP 429)
        static bool TooMany(WebException e)
        {
            var r = e.Response as HttpWebResponse;
            return r != null && (int)r.StatusCode == 429;
        }

        static string PostOnce(string command, string fields)
        {
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
                wc.Encoding = Encoding.UTF8;
                string json = wc.UploadString(Api + command, "{\"Command\":\"" + command + "\"," + fields + "}");
                var code = Regex.Match(json, "\"ReturnCode\":(-?\\d+)");
                if (code.Success && code.Groups[1].Value == "12") throw new BusyException();
                if (code.Success && code.Groups[1].Value != "0")
                {
                    var msg = Regex.Match(json, "\"ReturnMessage\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                    throw new Exception(msg.Success && msg.Groups[1].Value.Length > 0 ? Regex.Unescape(msg.Groups[1].Value) : "Divoom said no (code " + code.Groups[1].Value + ")");
                }
                return json;
            }
        }

        public static string Json(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        static string Range(int page)
        {
            int start = page * PageSize + 1;
            return "\"StartNum\":" + start + ",\"EndNum\":" + (start + PageSize - 1) + ",\"FileSize\":1,\"FileType\":5,\"Version\":12,\"RefreshIndex\":0";
        }

        // classify: Divoom's category number; sort: 0 newest, 1 most liked; total: how many the list has (Divoom
        // stops counting at 10,000)
        public static List<DivoomCloud.Entry> List(int classify, int sort, int page, out int total)
        {
            return Parse(Cached("GetCategoryFileListV2", Range(page) + ",\"Classify\":" + classify + ",\"FileSort\":" + sort, DesignCache.ListFresh), out total);
        }

        // every 16 x 16 design whose name or tags match the words
        public static List<DivoomCloud.Entry> Search(string words, int page, out int total)
        {
            return Parse(Cached("SearchGalleryV3", Range(page) + ",\"Classify\":0,\"FileSort\":0,\"Keywords\":" + Json(words) + ",\"KeywordsEn\":" + Json(words), DesignCache.ListFresh), out total);
        }

        // the signed-in account's liked designs, or its own uploads
        public static List<DivoomCloud.Entry> Mine(bool likes, int page, out int total)
        {
            if (!DivoomAccount.SignedIn) throw new Exception("Sign in to your Divoom account first (Divoom Account button)");
            // the account's own lists change whenever something is liked or uploaded: kept only briefly (and for offline use)
            return Parse(Cached(likes ? "GetMyLikeListV3" : "GetMyUploadListV3", Range(page) + ",\"Classify\":0,\"FileSort\":0," + DivoomAccount.Auth(), TimeSpan.FromMinutes(10)), out total);
        }

        // a list page from the cache when it is recent, otherwise from Divoom (and saved)
        static string Cached(string command, string fields, TimeSpan fresh)
        {
            return DesignCache.List("d16:" + command + ":" + fields, fresh, () => Post(command, fields));
        }

        static List<DivoomCloud.Entry> Parse(string json, out int total)
        {
            var count = Regex.Match(json, "\"FileListNum\":(\\d+)");
            total = count.Success ? int.Parse(count.Groups[1].Value) : -1;
            var list = new List<DivoomCloud.Entry>();
            foreach (Match m in Regex.Matches(json, @"\{[^{}]*""FileId""[^{}]*\}"))   // one design (its lists hold no objects)
            {
                var id = Regex.Match(m.Value, "\"FileId\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                var name = Regex.Match(m.Value, "\"FileName\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (id.Success && id.Groups[1].Value.Length > 0)
                    list.Add(new DivoomCloud.Entry { Id = Prefix + Regex.Unescape(id.Groups[1].Value), Name = name.Success ? Regex.Unescape(name.Groups[1].Value).Trim() : "" });
            }
            return list;
        }

        // 16 x 16 RGB frames (0xRRGGBB per pixel)
        public static List<int[]> Frames(string id)
        {
            byte[] d = DesignCache.File(Prefix + id, delegate
            {
                for (int tries = 0; ; tries++)   // when Divoom says "too many requests", wait and try again
                {
                    try { using (var wc = Client()) return wc.DownloadData(FileUrl + id); }
                    catch (WebException e) { if (!TooMany(e) || tries >= 3) throw; System.Threading.Thread.Sleep(1500 * (tries + 1)); }
                }
            });
            int at, time = 0;
            if (d.Length > 4 && d[0] == 9) { at = 4; time = d[2] << 8 | d[3]; }
            else if (d.Length > 1 && d[0] == 8) at = 1;
            else throw new Exception("Unsupported Divoom file format " + (d.Length > 0 ? d[0] : -1));
            int n = (d.Length - at) / 16 * 16;
            byte[] plain;
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = Encoding.ASCII.GetBytes("78hrey23y28ogs89"); aes.IV = Encoding.ASCII.GetBytes("1234567890123456");
                aes.Mode = System.Security.Cryptography.CipherMode.CBC; aes.Padding = System.Security.Cryptography.PaddingMode.None;
                using (var dec = aes.CreateDecryptor()) plain = dec.TransformFinalBlock(d, at, n);
            }
            var frames = new List<int[]>();
            for (int f = 0; (f + 1) * 768 <= plain.Length; f++)
            {
                var px = new int[256];
                for (int k = 0; k < 256; k++) { int i = f * 768 + k * 3; px[k] = plain[i] << 16 | plain[i + 1] << 8 | plain[i + 2]; }
                frames.Add(px);
            }
            if (frames.Count == 0) throw new Exception("Empty Divoom file");
            lock (ms) ms[id] = time;
            return frames;
        }

        public static int MsOf(string id) { int t; lock (ms) return ms.TryGetValue(id, out t) ? t : 0; }
    }

    // The Divoom app account (the one the phone app signs in to; github.com/bugzmanov/divoom-minitoo's clauddy-cloud.py
    // shows the calls). Sign-in is email + password, sent as the MD5 of the password like the app does; only the
    // returned UserId and Token are kept (in the settings file), never the password. An account made with an emailed
    // code has no password yet: SendCode emails a code, and SetPassword uses it to choose one (Divoom's "forgot
    // password"), the same as in the phone app.
    static class DivoomAccount
    {
        public static string Email = "";
        public static long UserId, Token;
        public static bool SignedIn { get { return UserId != 0 && Token != 0; } }

        public static string Auth() { return "\"Token\":" + Token + ",\"UserId\":" + UserId; }

        static string Md5(string s)
        {
            var bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];   // as the Android app casts each character
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in md5.ComputeHash(bytes)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static void SignIn(string email, string password)
        {
            int offset = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours);
            string json = DivoomCloud16.Post("UserLogin", "\"Token\":0,\"UserId\":0,\"Email\":" + DivoomCloud16.Json(email) + ",\"Password\":" + DivoomCloud16.Json(Md5(password)) +
                                             ",\"TimeZone\":\"" + (offset >= 0 ? "+" : "") + offset + "\",\"CountryISOCode\":\"US\",\"Language\":\"en\"");
            var uid = Regex.Match(json, "\"UserId\":(\\d+)"); var tok = Regex.Match(json, "\"Token\":(\\d+)");
            if (!uid.Success || !tok.Success || uid.Groups[1].Value == "0") throw new Exception("Divoom signed in but sent no account details");
            Email = email; UserId = long.Parse(uid.Groups[1].Value); Token = long.Parse(tok.Groups[1].Value);
        }

        public static void SignOut() { UserId = 0; Token = 0; }

        public static void SendCode(string email) { DivoomCloud16.Post("SendIdentifyCode", "\"Token\":0,\"UserId\":0,\"Email\":" + DivoomCloud16.Json(email)); }

        public static void SetPassword(string email, int code, string password)
        {
            DivoomCloud16.Post("FindPassword", "\"Token\":0,\"UserId\":0,\"Email\":" + DivoomCloud16.Json(email) + ",\"IdentifyCode\":" + code + ",\"NewPassword\":" + DivoomCloud16.Json(Md5(password)));
        }
    }

    // Gallery names, tidied and in English: Divoom's art is named by people all over the world, often with a date
    // stamp, tags joined by "+", or no name at all. Clean tidies a name; Translate turns a page of names into
    // English in one request (Google's free translator, the one Chrome's extension uses) and remembers each answer
    // in translations.txt in the settings folder, so a name is only ever looked up once. Offline, names stay as
    // they are.
    static class GalleryNames
    {
        public static volatile bool TranslateOn = true;
        // set when the translator (an unofficial Google address) stops answering: names stay as written, and after
        // a failure it isn't asked again for 10 minutes. Cleared when it works again.
        public static volatile string Problem;
        static DateTime retryAfter = DateTime.MinValue;
        static Dictionary<string, string> cache;
        static readonly object gate = new object();

        public static string Clean(string name)
        {
            string n = Regex.Replace(name ?? "", @"[\r\n\t+_]+", " ");
            n = Regex.Replace(n, @"\s+", " ").Trim();
            n = Regex.Replace(n, @"^Haunted\d{4}\s*", "");   // a contest tag Divoom puts in front of many names
            if (IsStamp(n)) return "";
            n = NoYears(n);
            if (n.Length > 0 && char.IsLower(n[0])) n = char.ToUpper(n[0]) + n.Substring(1);
            return n.Length > 40 ? n.Substring(0, 40).TrimEnd() + "…" : n;
        }

        // a name without the years in it ("New Year 2021", "Thanksgiving2022 -Blue Plate Special", "Easter (2020)"):
        // a year is 1950-2039 standing apart from other digits (so the game "2048" keeps its name); the brackets and
        // dashes it leaves behind go too
        public static string NoYears(string name)
        {
            string n = Regex.Replace(name ?? "", @"(?<!\d)(19[5-9]\d|20[0-3]\d)(?!\d)", " ");
            if (n == name) return name;
            n = Regex.Replace(n, @"[\(\[\{]\s*[\)\]\}]", " ");          // brackets left empty
            n = Regex.Replace(n, @"\s+([\-+_.,:;/|])\s*(?=[\-+_.,:;/|]|$)", " ");   // separators left dangling
            n = Regex.Replace(n, @"\s+", " ").Trim();
            n = Regex.Replace(n, @"^[\s\-+_.,:;/|]+|[\s\-+_.,:;/|]+$", "").Trim();
            return n;
        }

        // only a date, a time or numbers, in any language ("06.11.2026 06:59:06", "12.09.26 г. 13:42:04 ч.",
        // "9/8/26 7:08:40 PM"): one- and two-letter words like г., h. or PM don't make it a name
        public static bool IsStamp(string n)
        {
            if (Regex.Matches(n, @"\d").Count < 4) return false;   // a real date or time has several digits ("Po2" is a name)
            if (!Regex.IsMatch(n, @"\d[.:/\-,]\d|\d{6}")) return false;   // and punctuation between them ("2048" is a game)
            string t = Regex.Replace(n, @"(?<!\p{L})\p{L}{1,2}\.?(?!\p{L})", "");
            return Regex.IsMatch(t, @"^[\d\s.:/\-,]*$");
        }

        // every name with letters is looked up once; ones Google finds are already English stay as they are
        static bool NeedsLook(string n)
        {
            foreach (char c in n) if (char.IsLetter(c)) return true;
            return false;
        }

        // Plain-letter names are often English slang or made-up words that Google "translates" anyway ("friend
        // deltarune" became "Friend delta"). For those, a translation that keeps any of the original words is a
        // mangled original, not a translation, so the name is kept as it was.
        static bool Mangled(string original, string english)
        {
            foreach (char c in original) if (c > 0x7F && char.IsLetter(c)) return false;   // other alphabets or accents: trust it
            var words = new HashSet<string>(Regex.Split(original.ToLowerInvariant(), @"[^a-z0-9]+"));
            foreach (var w in Regex.Split(english.ToLowerInvariant(), @"[^a-z0-9]+")) if (w.Length > 1 && words.Contains(w)) return true;
            return false;
        }

        static string CachePath { get { return Settings.DataFile("translations.txt"); } }

        static void LoadCache()
        {
            if (cache != null) return;
            cache = new Dictionary<string, string>();
            try
            {
                if (System.IO.File.Exists(CachePath))
                    foreach (string line in System.IO.File.ReadAllLines(CachePath, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) cache[line.Substring(0, tab)] = line.Substring(tab + 1);
                    }
            }
            catch (Exception) { }
        }

        // cleaned names -> English names (same order); unknown ones looked up together
        public static List<string> Translate(List<string> names) { bool complete; return Translate(names, out complete); }

        // complete: every name is in English now (false when translation is off or the translator didn't answer)
        public static List<string> Translate(List<string> names, out bool complete)
        {
            var result = new List<string>(names);
            complete = false;
            if (!TranslateOn) return result;
            List<string> ask = new List<string>();
            lock (gate)
            {
                LoadCache();
                foreach (var n in names) if (n.Length > 0 && NeedsLook(n) && !cache.ContainsKey(n) && !ask.Contains(n)) ask.Add(n);
            }
            complete = ask.Count == 0;
            if (ask.Count > 0 && DateTime.Now >= retryAfter)
            {
                try
                {
                    var url = new StringBuilder("https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=en");
                    foreach (var n in ask) url.Append("&q=").Append(Uri.EscapeDataString(n));
                    string json;
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    using (var wc = new WebClient()) { wc.Encoding = Encoding.UTF8; wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0"; json = wc.DownloadString(url.ToString()); }
                    // [["english","detected language"],...]; a name already in English (or too short to tell) is kept
                    var found = new List<string>();
                    foreach (Match m in Regex.Matches(json, "\\[\"((?:[^\"\\\\]|\\\\.)*)\",\"([A-Za-z\\-]+)\"\\]"))
                        found.Add(m.Groups[2].Value == "en" ? null : Regex.Unescape(m.Groups[1].Value));
                    if (found.Count < ask.Count) throw new Exception("unexpected answer");
                    Problem = null;
                    complete = true;
                    lock (gate)
                        {
                            var lines = new StringBuilder();
                            for (int i = 0; i < ask.Count; i++)
                            {
                                string en = found[i] == null ? ask[i] : Regex.Replace(found[i], @"^(the|a|an) ", "", RegexOptions.IgnoreCase).Trim();
                                if (found[i] != null && Mangled(ask[i], en)) en = ask[i];
                                en = en.Length > 0 && char.IsLower(en[0]) ? char.ToUpper(en[0]) + en.Substring(1) : en;
                                if (en.Length == 0 || en.Contains("\t")) en = ask[i];
                                cache[ask[i]] = en;
                                lines.Append(ask[i].Replace("\t", " ")).Append('\t').Append(en).Append("\r\n");
                            }
                            try { System.IO.File.AppendAllText(CachePath, lines.ToString(), Encoding.UTF8); } catch (Exception) { }
                        }
                }
                catch (Exception)   // offline, refused or changed: keep the names as they are
                {
                    retryAfter = DateTime.Now.AddMinutes(10);
                    Problem = "English names aren't available right now (the translator didn't answer), so names are shown as written";
                }
            }
            lock (gate)
                for (int i = 0; i < result.Count; i++)
                {
                    string en;
                    if (cache.TryGetValue(result[i], out en)) result[i] = en.Length > 40 ? en.Substring(0, 40).TrimEnd() + "…" : en;
                    if (IsStamp(result[i])) result[i] = "";
                }
            return result;
        }
    }

    // Weather pictures at 16 x 16 for the Tivoo (palette indexes, Palette16), drawn from shapes rather than pixel tables:
    // the same conditions as WeatherArt's 10 x 10 icons, each a short loop of frames, and the temperature in 5 x 7 digits.
    static class WeatherArt16
    {
        const int N = 16, Frames = 8;
        const int White = 7, Yellow = 3, Gray = Palette16.Gray, Dim = Palette16.DimGray;

        static void Put(int[] px, int x, int y, int c) { if (x >= 0 && x < N && y >= 0 && y < N) px[y * N + x] = c; }

        static void Disc(int[] px, double cx, double cy, double r, int c)
        {
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) px[y * N + x] = c;
        }

        // a puffy cloud whose bottom edge is at row bottom: three bumps and a flat base, shaded underneath
        static void Cloud(int[] px, double ox, int bottom, int body, int shade)
        {
            var mask = new int[N * N];
            Disc(mask, ox + 3.5, bottom - 2.5, 2.6, 1);
            Disc(mask, ox + 7.5, bottom - 4.2, 3.6, 1);
            Disc(mask, ox + 11.2, bottom - 2.8, 2.7, 1);
            for (int x = (int)Math.Ceiling(ox + 2); x <= ox + 12; x++)
                for (int y = bottom - 2; y <= bottom; y++) Put(mask, x, y, 1);
            for (int k = 0; k < N * N; k++)
                if (mask[k] != 0) px[k] = k / N >= bottom ? shade : body;   // the underside is darker
        }

        // the sun: a disc with eight rays that turn a little every frame
        static void Sun(int[] px, double cx, double cy, double r, double rayFrom, double rayTo, int frame)
        {
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4 + frame * Math.PI / 16, len = k % 2 == frame % 2 ? rayTo : rayTo - 1;
                for (double d = rayFrom; d <= len; d += 0.5) Put(px, (int)Math.Round(cx + Math.Cos(a) * d), (int)Math.Round(cy + Math.Sin(a) * d), Palette16.Orange);
            }
            Disc(px, cx, cy, r, Palette16.Amber);
            Disc(px, cx, cy, r - 0.9, Yellow);
        }

        // a crescent moon: a disc with a smaller one taken out of it
        static void Moon(int[] px, double cx, double cy, double r)
        {
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    bool inMoon = (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
                    bool bite = (x - cx - r * 0.55) * (x - cx - r * 0.55) + (y - cy + r * 0.35) * (y - cy + r * 0.35) <= r * r * 0.8;
                    if (inMoon && !bite) px[y * N + x] = Yellow;
                }
        }

        static void Stars(int[] px, int frame, int[][] spots)
        {
            for (int i = 0; i < spots.Length; i++)
                Put(px, spots[i][0], spots[i][1], (frame + i * 3) % 8 < 2 ? Dim : (frame + i) % 4 == 0 ? Palette16.SkyBlue : White);   // twinkle
        }

        public static List<int[]> IconFrames(WeatherInfo w)
        {
            string name = WeatherArt.IconName(w);
            var list = new List<int[]>();
            for (int f = 0; f < Frames; f++)
            {
                var px = new int[N * N];
                switch (name)
                {
                    case "sun": Sun(px, 7.5, 7.5, 3.6, 5, 7, f); break;
                    case "moon":
                        Moon(px, 7, 8, 5.2);
                        Stars(px, f, new[] { new[] { 13, 2 }, new[] { 2, 2 }, new[] { 14, 9 }, new[] { 11, 13 } });
                        break;
                    case "partly":
                        Sun(px, 5, 5, 2.6, 3.5, 5, f);
                        Cloud(px, 2 + (f < 4 ? 0 : 1), 13, White, Gray);
                        break;
                    case "partly_night":
                        Moon(px, 5, 5, 3.6);
                        Stars(px, f, new[] { new[] { 13, 1 }, new[] { 10, 3 } });
                        Cloud(px, 2 + (f < 4 ? 0 : 1), 13, White, Gray);
                        break;
                    case "cloud":
                        Cloud(px, -1 + Math.Abs(f - 4) * 0.5, 9, Gray, Dim);            // a darker cloud behind, drifting
                        Cloud(px, 3 - Math.Abs(f - 4) * 0.5, 14, White, Gray);
                        break;
                    case "rain": case "storm":
                    {
                        bool storm = name == "storm", flash = storm && (f == 3 || f == 5);
                        Cloud(px, 1, 8, storm ? (flash ? Gray : Dim) : White, storm ? Palette16.Navy : Gray);
                        for (int d = 0; d < 6; d++)                               // drops fall two rows a frame, in staggered columns
                        {
                            int x = 3 + d * 2, y = 10 + (f * 2 + d * 3) % 7;
                            Put(px, x, y, Palette16.SkyBlue); Put(px, x, y - 1, 4);
                        }
                        if (flash)                                                // a lightning bolt
                            foreach (var b in new[] { new[] { 9, 8 }, new[] { 8, 9 }, new[] { 8, 10 }, new[] { 7, 11 }, new[] { 8, 11 }, new[] { 9, 11 }, new[] { 8, 12 }, new[] { 7, 13 }, new[] { 7, 14 } })
                                Put(px, b[0], b[1], Yellow);
                        break;
                    }
                    case "snow":
                        Cloud(px, 1, 8, White, Gray);
                        for (int d = 0; d < 5; d++)                               // flakes drift down, swaying
                        {
                            int y = 10 + (f + d * 2) % 6, x = 2 + d * 3 + ((f + d) % 4 < 2 ? 0 : 1);
                            Put(px, x, y, White);
                        }
                        break;
                    case "fog":
                        for (int band = 0; band < 5; band++)                      // bands slide back and forth
                        {
                            int y = 3 + band * 2 + band / 2, shift = (band % 2 == 0 ? 1 : -1) * (Math.Abs(f - 4) - 2);
                            for (int x = 2 + shift; x < 14 + shift; x++) Put(px, x, y, band % 2 == 0 ? White : Gray);
                        }
                        break;
                }
                list.Add(px);
            }
            return list;
        }

        // the temperature in 5 x 7 digits with a small degree ring at the top right, colored by how warm it is
        // (3 x 5 digits when it needs three characters); top-left corner at (x0, y0), roomX / roomY = room to move
        public static int[] Temperature(WeatherInfo w, int x0, int y0, out int roomX, out int roomY)
        {
            double f = w.Unit == "F" ? w.Temp : w.Temp * 9 / 5 + 32;
            int color = f < 32 ? White : f < 50 ? 6 : f < 70 ? Palette16.Lime : f < 85 ? Palette16.Amber : f < 95 ? Palette16.Orange : 1;
            string text = ((int)Math.Round(w.Temp)).ToString();
            bool big = text.Length <= 2;
            int cw = big ? 5 : 3, ch = big ? 7 : 5, width = text.Length * (cw + 1) - 1, total = width + 1 + 3;
            roomX = Math.Max(0, N - total); roomY = N - ch;
            var px = new int[N * N];
            int cx = x0;
            foreach (char c in text)
            {
                if (big && c == '-') { for (int x = 1; x < 4; x++) Put(px, cx + x, y0 + 3, color); }
                else if (big) { string g = ClockArt.Big[c - '0']; for (int k = 0; k < 35; k++) if (g[k] == '#') Put(px, cx + k % 5, y0 + k / 5, color); }
                else { string g; if (WeatherArt.Font.TryGetValue(c, out g)) for (int k = 0; k < 15; k++) if (g[k] == '1') Put(px, cx + k % 3, y0 + k / 3, color); }
                cx += cw + 1;
            }
            int dx = x0 + width + 1;
            for (int k = 0; k < 9; k++) if (k != 4) Put(px, dx + k % 3, y0 + k / 3, color);   // degree ring
            return px;
        }
    }
}
