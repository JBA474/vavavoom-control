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
            { "snakes", "Snake Chase" }, { "blocks", "Tetrix" }, { "stars", "Hyperspace" }, { "rain", "Rain on a Window" }, { "fire", "Fireplace" }, { "fireworks", "Fireworks" }, { "heart", "Heart Monitor" }, { "balls", "Bouncing Balls" }, { "bricks", "Brick Breaker" }, { "laos", "Laos Flag" },{ "lava", "Lava Lamp" }, { "mandelbrot", "Mandelbrot Zoom" }, { "miner", "Gem Miner" }, { "swarm", "Star Swarm" }, { "aquarium", "Aquarium" }, { "aquaclock", "Aquarium Clock" }, { "slots", "Slot Machine" }, { "robot", "Retro Robot" }, { "jellyfish", "Jellyfish" }, { "nextholiday", "Days to Next Holiday" }, { "lavaclock", "Lava Clock" }, { "countdown", "Countdown Timer" }, { "text", "Scrolling Text" }, { "drawing", "Current Drawing" }, { "animation", "Current Animation" }, { "favorite", "Random Favorite" }, { "holiday", "Holiday Designs (in season)" } };

        // sections on the Playlist page for items that are turned off
        public static readonly string[] Groups = { "Clock & Weather", "Animations", "Original Modes", "Your Content" };

        public string Group
        {
            get
            {
                if (Id == "clock" || Id == "weather" || Id == "lavaclock" || Id == "aquaclock" || Id == "countdown") return Groups[0];
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

    // Mandelbrot Zoom: starts on the whole Mandelbrot set, dives into the edge toward one of its famous spots (spirals,
    // seahorses, tiny copies of the set) with the colors slowly cycling, then pulls back out and heads for another.
    // A dive stops early when the picture runs out of detail (inside a tiny copy, say). Next() gives RGB colors at
    // 16 x 16 (each pixel averaged from 3 x 3 samples) and palette indexes at 10 x 10.
    class Mandelbrot
    {
        static readonly double[,] Spots = {
            { -0.743643887037151, 0.131825904205330 },    // seahorse valley
            { -0.77568377, 0.13646737 },                   // double spiral
            { -0.10109636384562, 0.95628651080914 },       // branch tip
            { 0.001643721971153, -0.822467633298876 },     // lacy branches
            { -1.25066, 0.02012 },                         // the neck between the big bulbs
            { -0.761574, -0.0847596 },                     // spiral arms
            { -1.768778833, -0.001738996 },                // out along the spike
            { 0.360240443437614, -0.641313061064803 },     // curls on the lower edge
            { -0.16070135, 1.0375665 },                    // spirals near the top
            { 0.2925755, -0.0149977 },                     // elephant valley
            { -0.0452407411, 0.9868162204352258 },         // top spirals
        };
        // full-strength colors around a loop: blue, cyan, yellow, orange, magenta (and back to blue)
        static readonly int[] Gradient = { 0x0020FF, 0x00D8FF, 0xFFE000, 0xFF4800, 0xE000B0 };
        static readonly int[] Bands10 = { 4, 6, 7, 3, 1, 5 };   // 10 x 10: blue, cyan, white, yellow, red, magenta

        const double HomeX = -0.6, HomeY = 0, Wide = 3.2, Deep = 2e-7;
        const int Linger = 25;   // frames held at the deepest point and on the whole set

        readonly int N, S;   // screen size; samples across each pixel
        readonly Random rng = new Random();
        const double DiveRate = 0.03, OutRate = 0.06;   // zoom per frame (natural log of the width change) at full pace
        int spot = -1, stage, wait;   // stage 0: whole set, 1: diving, 2: holding deep, 3: pulling out
        double width = Wide, phase, rate, low = -1;   // rate: the zoom speed now, easing toward each stage's pace; low: the color base

        public Mandelbrot() : this(10) { }
        public Mandelbrot(int size) { N = size; S = size == 16 ? 4 : 1; wait = Linger; }

        public int[] Next()
        {
            // where the picture goes next
            // the zoom speeds up and slows down gently instead of starting and stopping at full pace, and eases off
            // as it nears the deepest point or the whole set
            if (stage == 0 && --wait <= 0) { spot = NewSpot(); stage = 1; }
            else if (stage == 2 && --wait <= 0) stage = 3;
            double target = stage == 1 ? DiveRate * Math.Min(1, 0.15 + Math.Log(width / Deep) / 2)
                          : stage == 3 ? -OutRate * Math.Min(1, 0.08 + Math.Log(Wide / width) / 2) : 0;
            rate += (target - rate) * 0.08;
            width = Math.Min(Wide, width * Math.Exp(-rate));
            if (stage == 1 && width <= Deep) { stage = 2; wait = Linger; }
            if (stage == 3 && width >= Wide * 0.999) { width = Wide; rate = 0; stage = 0; wait = Linger; }
            phase += 0.006;

            // the view closes in on the spot: the center slides from the whole set's middle to the spot as it zooms
            double tx = spot < 0 ? HomeX : Spots[spot, 0], ty = spot < 0 ? HomeY : Spots[spot, 1];
            double pull = (width / Wide) * (width / Wide);
            double cx = tx + (HomeX - tx) * pull, cy = ty + (HomeY - ty) * pull;
            int limit = (int)Math.Min(1500, 80 + 60 * Math.Log10(Wide / width));

            var mu = new double[N * N * S * S];
            int inside = 0;
            double lo = double.MaxValue, hi = 0;
            double step = width / (N * S);
            for (int j = 0; j < N * S; j++)
                for (int i = 0; i < N * S; i++)
                {
                    double v = Escape(cx + (i - N * S / 2.0 + 0.5) * step, cy + (j - N * S / 2.0 + 0.5) * step, limit);
                    mu[j * N * S + i] = v;
                    if (v < 0) inside++;
                    else { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                }

            // out of detail (mostly inside a tiny copy of the set, or a plain smooth blend): stop diving and hold here
            if (stage == 1 && width < 2e-3 && (inside > mu.Length * 0.55 || hi - lo < 8)) { stage = 2; wait = Linger; }

            // the colors are measured from the lowest count on screen so deep views stay colorful; that count jumps
            // from frame to frame, so the base follows it smoothly instead of shifting every band at once
            if (lo <= hi) low = low < 0 ? lo : low + (lo - low) * 0.1;
            lo = Math.Max(0, low);

            var px = new int[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    if (S == 1) { px[y * N + x] = Band(mu[y * N + x], lo); continue; }
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < S; dy++)
                        for (int dx = 0; dx < S; dx++)
                        {
                            int c = Color(mu[(y * S + dy) * N * S + x * S + dx], lo);
                            r += c >> 16 & 0xFF; g += c >> 8 & 0xFF; b += c & 0xFF;
                        }
                    int n = S * S;
                    px[y * N + x] = (r / n) << 16 | (g / n) << 8 | (b / n);
                }
            return px;
        }

        int NewSpot()
        {
            int k;
            do k = rng.Next(Spots.GetLength(0)); while (k == spot && Spots.GetLength(0) > 1);
            return k;
        }

        // smooth escape count for c = (x, y); -1 inside the set
        static double Escape(double x, double y, int limit)
        {
            // the two biggest bulbs are inside: skip the full count there
            double q = (x - 0.25) * (x - 0.25) + y * y;
            if (q * (q + (x - 0.25)) <= 0.25 * y * y || (x + 1) * (x + 1) + y * y <= 0.0625) return -1;
            double zx = 0, zy = 0;
            for (int n = 0; n < limit; n++)
            {
                double xx = zx * zx, yy = zy * zy;
                if (xx + yy > 256) return n + 1 - Math.Log(Math.Log(xx + yy) / 2 / Math.Log(2)) / Math.Log(2);
                zy = 2 * zx * zy + y; zx = xx - yy + x;
            }
            return -1;
        }

        // where an escape count sits on the color cycle, measured from the frame's lowest count so deep views stay colorful
        double Spot(double v, double lo) { return Math.Log(1 + Math.Max(0, v - lo)) * 0.45 + phase; }

        int Color(double v, double lo)
        {
            if (v < 0) return 0;
            double t = Spot(v, lo) * Gradient.Length;
            t -= Math.Floor(t / Gradient.Length) * Gradient.Length;
            int a = (int)t, b = (a + 1) % Gradient.Length;
            double f = t - a;
            int ca = Gradient[a], cb = Gradient[b], o = 0;
            for (int sh = 0; sh <= 16; sh += 8)
                o |= (int)Math.Round((ca >> sh & 0xFF) * (1 - f) + (cb >> sh & 0xFF) * f) << sh;
            return o;
        }

        int Band(double v, double lo)
        {
            if (v < 0) return 0;
            double t = Spot(v, lo) * 1.3;   // 10 x 10 has no in-between colors: one band per step
            return Bands10[(int)(t - Math.Floor(t / Bands10.Length) * Bands10.Length) % Bands10.Length];
        }
    }

    // Gem Miner: a digging game that plays itself. A miner in a yellow hard hat tunnels down through layers of earth
    // after buried gems while glow worms crawl through the tunnels after him. His lamp zaps a worm down a straight
    // tunnel, and a boulder left hanging over a tunnel wobbles, falls and squashes whatever is below. Once every gem
    // is dug out the tunnels sparkle and a new level starts with more worms. A worm that catches him starts the level over.
    // The map is a grid of cells: 8 x 8 cells of 2 x 2 pixels on the Tivoo (so the miner, worms, gems and boulders
    // are little sprites, and they slide from cell to cell), 10 x 10 single pixels on the AuraBox.
    // Next() gives palette indexes: 0..7 at 10 x 10, Palette16 at 16 x 16.
    class GemMiner
    {
        const int Tunnel = 0, Soil = 1, Gem = 2;
        class Worm { public int X, Y, Dx = 1, Dy, Dead, Moved, FromX, FromY; }
        class Rock { public int X, Y, Wobble, Moved, FromY; public bool Falling; }

        readonly int N, C, G, Sky;   // screen pixels; pixels per cell; cells across; rows of sky (in cells)
        readonly int Pace;           // ticks between the miner's steps (worms are slower)
        readonly Random rng = new Random();
        readonly int[][] layers;     // each earth layer: its color and its pebbles' color
        int[,] ground;
        readonly List<Worm> worms = new List<Worm>();
        readonly List<Rock> rocks = new List<Rock>();
        int mx, my, faceX = 1, faceY, fromX, fromY, moved, tick, level, levelTicks, hurt, cheer, glint, charge, seed;
        int zapX, zapY, zapDx, zapDy, zapLen, zap;   // the lamp beam being drawn

        public GemMiner() : this(10) { }
        public GemMiner(int size)
        {
            N = size;
            if (N == 16)
            {
                C = 2; Sky = 1; Pace = 3;
                layers = new[] {
                    new[] { Palette16.Brown, Palette16.DarkOrange }, new[] { Palette16.DarkOrange, Palette16.Amber },
                    new[] { Palette16.DarkRed, Palette16.Brown }, new[] { Palette16.Purple, Palette16.Navy } };
            }
            else
            {
                C = 1; Sky = 0; Pace = 2;
                layers = new[] { new[] { 3, 3 }, new[] { 1, 1 }, new[] { 4, 4 } };
            }
            G = N / C;
            NewLevel();
        }

        bool Big { get { return C > 1; } }
        bool In(int x, int y) { return x >= 0 && x < G && y >= Sky && y < G; }
        Rock RockAt(int x, int y) { foreach (var r in rocks) if (r.X == x && r.Y == y) return r; return null; }
        Worm WormAt(int x, int y) { foreach (var w in worms) if (w.Dead == 0 && w.X == x && w.Y == y) return w; return null; }
        bool Open(int x, int y) { return In(x, y) && ground[x, y] == Tunnel && RockAt(x, y) == null; }

        void NewLevel()
        {
            ground = new int[G, G];
            for (int y = Sky; y < G; y++) for (int x = 0; x < G; x++) ground[x, y] = Soil;
            worms.Clear(); rocks.Clear();
            mx = fromX = G / 2; my = fromY = Sky; ground[mx, my] = Tunnel;
            levelTicks = 0; hurt = 0; zap = 0; seed = rng.Next();
            int depth = G - Sky;
            // gems in the lower part, apart from each other
            for (int k = 0, tries = 0; k < (Big ? 5 : 3) && tries < 500; tries++)
            {
                int x = rng.Next(G), y = Sky + depth / 3 + rng.Next(depth - depth / 3);
                if (ground[x, y] != Soil || Near(x, y, Gem, Big ? 1 : 2)) continue;
                ground[x, y] = Gem; k++;
            }
            // boulders in the middle rows, not over the miner's start
            for (int k = 0, tries = 0; k < (Big ? 3 : 3) && tries < 500; tries++)
            {
                int x = rng.Next(G), y = Sky + 1 + rng.Next(depth - 3);
                if (ground[x, y] != Soil || Math.Abs(x - mx) < 2 || RockAt(x, y) != null || RockAt(x, y - 1) != null || RockAt(x, y + 1) != null) continue;
                ground[x, y] = Tunnel; rocks.Add(new Rock { X = x, Y = y, FromY = y });
                k++;
            }
            // caves, each with a worm
            int caves = Math.Min(1 + level, Big ? 3 : 3);
            for (int k = 0, tries = 0; k < caves && tries < 500; tries++)
            {
                int len = 2 + rng.Next(2), y = Sky + 2 + rng.Next(depth - 2), x = rng.Next(G - len + 1);
                bool ok = true;
                for (int i = x; i < x + len; i++) if (ground[i, y] != Soil || RockAt(i, y) != null || RockAt(i, y - 1) != null) ok = false;
                if (!ok || Math.Abs(y - my) < 2) continue;
                for (int i = x; i < x + len; i++) ground[i, y] = Tunnel;
                int wx = x + rng.Next(len);
                worms.Add(new Worm { X = wx, Y = y, FromX = wx, FromY = y });
                k++;
            }
        }

        bool Near(int x, int y, int kind, int d)
        {
            for (int j = y - d; j <= y + d; j++) for (int i = x - d; i <= x + d; i++) if (In(i, j) && ground[i, j] == kind) return true;
            return false;
        }

        static readonly int[] DX = { 1, -1, 0, 0 }, DY = { 0, 0, 1, -1 };

        public int[] Next()
        {
            tick++; levelTicks++;
            if (zap > 0) zap--;
            if (glint > 0) glint--;
            if (charge > 0) charge--;
            if (cheer > 0) { if (--cheer == 0) { level++; NewLevel(); } return Draw(); }
            if (hurt > 0) { if (--hurt == 0) NewLevel(); return Draw(); }
            if (levelTicks > 2000) { NewLevel(); return Draw(); }   // a level that drags on starts fresh

            foreach (var w in worms) if (w.Dead > 0) w.Dead--;
            worms.RemoveAll(w => w.Dead == 1);
            if (tick % (Big ? 2 : 1) == 0) MoveRocks();
            if (tick % Pace == 0) MoveMiner();
            int wormEvery = Pace * Math.Max(3, 6 - level / 2) / 2;
            if (tick % wormEvery == 0) foreach (var w in worms) if (w.Dead == 0) MoveWorm(w);
            if (hurt == 0 && WormAt(mx, my) != null) hurt = 16;
            return Draw();
        }

        void MoveRocks()
        {
            foreach (var r in rocks.ToArray())
            {
                if (!r.Falling)   // dug out from below: it wobbles, then drops
                {
                    if (Open(r.X, r.Y + 1)) { if (++r.Wobble >= 6) r.Falling = true; }
                    else r.Wobble = 0;
                    continue;
                }
                if (In(r.X, r.Y + 1) && ground[r.X, r.Y + 1] == Tunnel && RockAt(r.X, r.Y + 1) == null)
                {
                    r.FromY = r.Y; r.Y++; r.Moved = tick;
                    var w = WormAt(r.X, r.Y); if (w != null) w.Dead = 9;
                    if (r.X == mx && r.Y == my && hurt == 0) hurt = 16;
                }
                else rocks.Remove(r);   // landed: it crumbles away
            }
        }

        // a boulder that's loose (or soon will be) above (x, y) with nothing solid between
        bool Overhead(int x, int y)
        {
            for (int j = y - 1; j >= Sky; j--)
            {
                var r = RockAt(x, j);
                if (r != null) return r.Falling || r.Wobble > 0 || j == y - 1;
                if (ground[x, j] != Tunnel) return false;
            }
            return false;
        }

        void MoveMiner()
        {
            // a worm straight down a tunnel within reach: zap it (the lamp needs a while to recharge)
            for (int d = 0; d < 4 && charge == 0; d++)
                for (int s = 1; s <= (Big ? 4 : 5); s++)
                {
                    int x = mx + DX[d] * s, y = my + DY[d] * s;
                    if (!Open(x, y)) break;
                    var w = WormAt(x, y);
                    if (w == null) continue;
                    w.Dead = 9; zap = 4; zapX = mx; zapY = my; zapDx = DX[d]; zapDy = DY[d]; zapLen = s; charge = (Big ? 5 : 7) * Pace;
                    faceX = DX[d]; faceY = DY[d];
                    return;
                }

            // a loose boulder overhead: step out from under it first
            if (Overhead(mx, my))
            {
                for (int d = 0; d < 4; d++)
                {
                    int x = mx + DX[d], y = my + DY[d];
                    if (!In(x, y) || RockAt(x, y) != null || WormAt(x, y) != null || Overhead(x, y)) continue;
                    Step(x, y);
                    return;
                }
            }

            // a worm on his trail and soil under a boulder right next to him: dig under it to lure the worm beneath
            foreach (var r in rocks)
            {
                int sx = r.X, sy = r.Y + 1;
                if (r.Wobble > 0 || !In(sx, sy) || ground[sx, sy] != Soil || Math.Abs(sx - mx) + Math.Abs(sy - my) != 1) continue;
                bool chased = false;
                foreach (var w in worms)
                    if (w.Dead == 0 && Math.Abs(w.X - mx) + Math.Abs(w.Y - my) <= (Big ? 4 : 6) && TunnelStep(w.X, w.Y) >= 0) chased = true;
                if (!chased) continue;
                Step(sx, sy);
                return;
            }

            // otherwise one step toward the nearest gem, preferring old tunnels and staying out from under boulders
            int[] cost = new int[G * G], prev = new int[G * G];
            bool[] done = new bool[G * G];
            for (int k = 0; k < cost.Length; k++) { cost[k] = int.MaxValue; prev[k] = -1; }
            cost[my * G + mx] = 0;
            int goal = -1;
            while (true)
            {
                int best = -1;
                for (int k = 0; k < cost.Length; k++) if (!done[k] && cost[k] != int.MaxValue && (best < 0 || cost[k] < cost[best])) best = k;
                if (best < 0) break;
                done[best] = true;
                int bx = best % G, by = best / G;
                if (ground[bx, by] == Gem) { goal = best; break; }
                for (int d = 0; d < 4; d++)
                {
                    int x = bx + DX[d], y = by + DY[d];
                    if (!In(x, y) || RockAt(x, y) != null) continue;
                    int c = ground[x, y] == Soil ? 3 : 1;
                    if (Overhead(x, y)) c += 40;
                    foreach (var w in worms) if (w.Dead == 0 && Math.Abs(w.X - x) + Math.Abs(w.Y - y) <= 1) c += 30;
                    int k2 = y * G + x;
                    if (cost[best] + c < cost[k2]) { cost[k2] = cost[best] + c; prev[k2] = best; }
                }
            }
            if (goal < 0) { cheer = 18; return; }   // no gems left
            int step = goal;
            while (prev[step] != my * G + mx) step = prev[step];
            Step(step % G, step / G);
        }

        // the miner moves to (x, y), digging it out and picking up a gem there
        void Step(int x, int y)
        {
            faceX = x - mx; faceY = y - my;
            fromX = mx; fromY = my; moved = tick;
            mx = x; my = y;
            if (ground[x, y] == Gem)
            {
                glint = 6;
                ground[x, y] = Tunnel;
                bool any = false;
                foreach (int g in ground) if (g == Gem) any = true;
                if (!any) cheer = 18;
            }
            ground[x, y] = Tunnel;
        }

        void MoveWorm(Worm w)
        {
            // through the tunnels toward the miner when there's a way, most of the time
            int dir = rng.Next(10) < 8 ? TunnelStep(w.X, w.Y) : -1;
            if (dir < 0)
            {
                if (!Open(w.X + w.Dx, w.Y + w.Dy) || rng.Next(6) == 0)
                {
                    var opts = new List<int>();
                    for (int d = 0; d < 4; d++) if (Open(w.X + DX[d], w.Y + DY[d])) opts.Add(d);
                    if (opts.Count == 0) return;
                    int pick = opts[rng.Next(opts.Count)];
                    w.Dx = DX[pick]; w.Dy = DY[pick];
                }
                // now and then a trapped worm chews a little way toward the miner
                if (rng.Next(8) == 0 && TunnelStep(w.X, w.Y) < 0)
                {
                    int ddx = Math.Sign(mx - w.X), ddy = Math.Sign(my - w.Y);
                    if (ddx != 0 && rng.Next(2) == 0) ddy = 0; else if (ddy != 0) ddx = 0;
                    int x = w.X + ddx, y = w.Y + ddy;
                    if (In(x, y) && ground[x, y] == Soil && RockAt(x, y) == null && RockAt(x, y - 1) == null)
                    {
                        ground[x, y] = Tunnel; w.FromX = w.X; w.FromY = w.Y; w.X = x; w.Y = y; w.Moved = tick;
                        if (ddx != 0 || ddy != 0) { w.Dx = ddx; w.Dy = ddy; }
                    }
                    return;
                }
            }
            else { w.Dx = DX[dir]; w.Dy = DY[dir]; }
            if (Open(w.X + w.Dx, w.Y + w.Dy) && WormAt(w.X + w.Dx, w.Y + w.Dy) == null)
            {
                w.FromX = w.X; w.FromY = w.Y; w.X += w.Dx; w.Y += w.Dy; w.Moved = tick;
            }
        }

        // the first step from (x, y) along the tunnels to the miner, or -1 when the tunnels don't reach him
        int TunnelStep(int x, int y)
        {
            var first = new int[G * G];
            for (int k = 0; k < first.Length; k++) first[k] = -2;
            var q = new Queue<int>();
            first[y * G + x] = -1; q.Enqueue(y * G + x);
            while (q.Count > 0)
            {
                int c = q.Dequeue(), cx = c % G, cy = c / G;
                if (cx == mx && cy == my) return first[c];
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + DX[d], ny = cy + DY[d];
                    if (!Open(nx, ny) || first[ny * G + nx] != -2) continue;
                    first[ny * G + nx] = first[c] < 0 ? d : first[c];
                    q.Enqueue(ny * G + nx);
                }
            }
            return -1;
        }

        // ---------- drawing ----------

        int[] px;
        void Put(int x, int y, int color) { if (x >= 0 && x < N && y >= 0 && y < N) px[y * N + x] = color; }

        // where something that moved from cell (fx, fy) to (x, y) at tick `at` is drawn, in pixels: on the Tivoo it
        // slides over a pixel at a time instead of jumping a whole cell
        int Slide(int from, int to, int at)
        {
            if (!Big) return to * C;
            int f = Math.Min(C, tick - at + 1);
            return from * C + (to - from) * f;
        }

        int Hash(int x, int y) { unchecked { int h = seed ^ (x * 73856093) ^ (y * 19349663); h ^= h >> 13; h *= 0x5bd1e995; return (h ^ (h >> 15)) & 0x7FFFFFFF; } }

        int[] Draw()
        {
            px = new int[N * N];
            if (!Big) return DrawSmall();

            // sky: night blue fading to light blue at the surface
            for (int x = 0; x < N; x++) { Put(x, 0, Palette16.Navy); Put(x, 1, Palette16.SkyBlue); }
            // earth: each layer's color with a pebble here and there; gems are faceted and glint
            for (int cy = Sky; cy < G; cy++)
                for (int cx = 0; cx < G; cx++)
                {
                    int g = ground[cx, cy], x0 = cx * 2, y0 = cy * 2;
                    if (g == Soil)
                    {
                        var l = layers[(cy - Sky) * layers.Length / (G - Sky)];
                        int h = Hash(cx, cy), pebble = h % 3 == 0 ? h / 3 % 4 : -1;
                        for (int k = 0; k < 4; k++) Put(x0 + k % 2, y0 + k / 2, k == pebble ? l[1] : l[0]);
                    }
                    else if (g == Gem)
                    {
                        int shine = (tick / 3 + cx + cy) % 8;   // a white glint runs around the facets now and then
                        int[] facets = { 7, 6, 6, Palette16.Teal };
                        for (int k = 0; k < 4; k++) Put(x0 + k % 2, y0 + k / 2, shine == k ? 7 : facets[k] == 7 ? Palette16.SkyBlue : facets[k]);
                    }
                    else if (cheer > 0 && Hash(cx + cheer, cy) % 3 == 0)   // level cleared: the tunnels sparkle
                        Put(x0 + Hash(cx, cy + cheer) % 2, y0 + Hash(cy, cx + cheer) % 2, (cheer / 2) % 2 == 0 ? 6 : 7);
                }
            // boulders: gray with a light top edge; a loose one rocks from side to side
            foreach (var r in rocks)
            {
                int x0 = r.X * 2 + (r.Wobble > 0 && !r.Falling ? (tick % 2 == 0 ? 1 : -1) * (r.Wobble % 2) : 0);
                int y0 = r.Falling ? Slide(r.FromY, r.Y, r.Moved) : r.Y * 2;
                Put(x0, y0, 7); Put(x0 + 1, y0, Palette16.Gray); Put(x0, y0 + 1, Palette16.Gray); Put(x0 + 1, y0 + 1, Palette16.DimGray);
            }
            // worms: a lime body that wriggles, with a white eye at the front; a zapped or squashed one flickers out
            foreach (var w in worms)
            {
                int x0 = Slide(w.FromX, w.X, w.Moved), y0 = Slide(w.FromY, w.Y, w.Moved);
                if (w.Dead > 0)
                {
                    int c = w.Dead % 2 == 0 ? 7 : Palette16.Lime;
                    if (w.Dead > 3) { Put(x0, y0, c); Put(x0 + 1, y0 + 1, c); } else { Put(x0 + 1, y0, c); Put(x0, y0 + 1, c); }
                    continue;
                }
                bool wiggle = (tick / 3) % 2 == 0;
                int front = w.Dx < 0 ? 0 : 1;
                Put(x0, y0 + 1, Palette16.Lime); Put(x0 + 1, y0 + 1, Palette16.Lime);
                Put(x0 + front, y0, 7);                                                   // head with its eye
                if (wiggle) Put(x0 + 1 - front, y0, Palette16.DarkGreen);                 // the tail humps up
                if (w.Dy != 0) { Put(x0, y0, wiggle ? 7 : Palette16.Lime); Put(x0 + 1, y0, wiggle ? Palette16.Lime : 7); }
            }
            // the lamp beam, along the middle of the tunnel
            if (zap > 0)
            {
                int bx = mx * 2 + (zapDx > 0 ? 2 : zapDx < 0 ? -1 : (zapX + zapY) % 2), by = my * 2 + (zapDy > 0 ? 2 : zapDy < 0 ? -1 : 0);
                for (int s = 0; s < zapLen * 2 - 1; s++)
                    Put(bx + zapDx * s, by + zapDy * s, zap % 2 == 0 ? 3 : 7);
            }
            // the miner: yellow hard hat with the lamp on the side he faces, blue overalls; red when caught
            {
                int x0 = Slide(fromX, mx, moved), y0 = Slide(fromY, my, moved);
                int lamp = faceX < 0 ? 0 : 1;
                bool flash = hurt > 0 && hurt % 2 == 0;
                Put(x0 + lamp, y0, flash ? 1 : glint > 0 && glint % 2 == 0 ? 6 : 7);   // the lamp (cyan as he grabs a gem)
                Put(x0 + 1 - lamp, y0, flash ? 1 : 3);
                bool step = moved > 0 && tick - moved < Pace && (tick / 2) % 2 == 0;   // legs move as he walks
                Put(x0, y0 + 1, flash ? Palette16.DarkRed : step ? Palette16.Navy : Palette16.SkyBlue);
                Put(x0 + 1, y0 + 1, flash ? Palette16.DarkRed : step ? Palette16.SkyBlue : Palette16.Navy);
            }
            return px;
        }

        // 10 x 10: one pixel per cell
        int[] DrawSmall()
        {
            for (int y = 0; y < G; y++)
                for (int x = 0; x < G; x++)
                {
                    int g = ground[x, y], c = 0;
                    if (g == Soil) c = layers[y * layers.Length / G][0];
                    else if (g == Gem) c = (tick + x + y) % 12 == 0 ? 7 : 6;
                    else if (cheer > 0 && (x + y + cheer) % 3 == 0) c = (cheer / 2) % 2 == 0 ? 6 : 7;
                    Put(x, y, c);
                }
            foreach (var r in rocks)
                if (r.Falling || r.Wobble == 0 || tick % 2 == 1) Put(r.X, r.Y, 5);   // a wobbling boulder blinks
            foreach (var w in worms)
                Put(w.X, w.Y, w.Dead == 0 ? 2 : w.Dead % 2 == 0 ? 7 : 0);
            if (zap > 0)
                for (int s = 1; s < zapLen; s++) Put(zapX + zapDx * s, zapY + zapDy * s, 7);
            Put(mx, my, hurt > 0 ? (hurt % 2 == 0 ? 1 : 7) : glint > 0 && glint % 2 == 0 ? 6 : 7);
            return px;
        }
    }

    // Star Swarm: a space shooter that plays itself. A wave of alien ships swoops in from the sides into a swaying
    // formation; a few at a time peel off and dive at the rocket along looping paths, engines blazing, dropping bombs,
    // then come back in from the top. The rocket dodges and fires up at them. Clearing a wave brings the next, faster
    // and in new colors. Ships are drawn from small pictures (Designs): 5 x 3 on the Tivoo, 3 x 2 on the AuraBox, three
    // kinds (saucers, fighters, bombers). Each takes a few hits (one more in the top row), flashing white when hit.
    // Next() gives palette indexes: 0..7 at 10 x 10, Palette16 at 16 x 16.
    class StarSwarm
    {
        const int Entering = 0, InFormation = 1, Diving = 2, Returning = 3;
        class Ship { public int Row, Col, Kind, State, Delay, Hp, Flash, Bombs; public double X, Y, LastY, T, Dt; public double[] P = new double[8]; }
        class Dot { public double X, Y, Dx, Dy; public int Life, Color; }

        // the ship pictures: b body, t trim, c dome or cockpit (blinks), l lights (flash in turn), . empty
        static readonly string[][] Designs16 = {
            new[] { ".ccc.", "bbbbb", ".l.l." },   // saucer: a glass dome on a wide hull, lights underneath
            new[] { "b.c.b", "bbbbb", ".t.t." },   // fighter: two prongs either side of the cockpit, twin engines
            new[] { ".bbb.", "tbcbt", "t...t" },   // bomber: a round body with swept wings hanging down
        };
        static readonly string[][] Designs10 = {
            new[] { ".c.", "lbl" },
            new[] { "bcb", "t.t" },
            new[] { "tbt", ".c." },
        };

        readonly int N, W, H, Cols, Rows;   // screen size; a ship's width and height; the formation
        readonly double K;                   // distances scale with the screen
        readonly string[][] designs;
        readonly Random rng = new Random();
        readonly List<Ship> ships = new List<Ship>();
        readonly List<Dot> shots = new List<Dot>(), bombs = new List<Dot>(), sparks = new List<Dot>(), stars = new List<Dot>();
        readonly int[][] waveColors;   // per wave: each row's body and trim colors
        int tick, wave, shipX, rocketDead, cooldown, pause, waveTicks, nextDive;

        public StarSwarm() : this(10) { }
        public StarSwarm(int size)
        {
            N = size; K = size / 10.0;
            Cols = 3; Rows = 2;
            designs = N == 16 ? Designs16 : Designs10;
            W = designs[0][0].Length; H = designs[0].Length;
            if (N == 16)
            {
                waveColors = new[] {
                    new[] { Palette16.Lime, Palette16.DarkGreen, 5, Palette16.Purple },
                    new[] { Palette16.Orange, Palette16.DarkRed, Palette16.Pink, 5 },
                    new[] { Palette16.Teal, 4, 3, Palette16.Amber },
                    new[] { 5, Palette16.Purple, Palette16.Lime, Palette16.Teal },
                };
                for (int k = 0; k < 14; k++) stars.Add(new Dot { X = rng.Next(N), Y = rng.NextDouble() * N, Dy = 0.12 + rng.NextDouble() * 0.25, Color = rng.Next(3) == 0 ? Palette16.Gray : Palette16.DimGray });
            }
            else
            {
                waveColors = new[] { new[] { 2, 2, 5, 5 }, new[] { 5, 1, 3, 3 }, new[] { 6, 4, 2, 2 }, new[] { 3, 1, 6, 6 } };
                for (int k = 0; k < 4; k++) stars.Add(new Dot { X = rng.Next(N), Y = rng.NextDouble() * N, Dy = 0.1 + rng.NextDouble() * 0.15, Color = 4 });
            }
            shipX = N / 2;
            NewWave();
        }

        // the formation sways a pixel side to side; three ships fill the width
        double SlotX(Ship s) { return s.Col * W + Math.Round((Math.Sin(tick * 0.05) + 1) / 2); }
        double SlotY(Ship s) { return N == 16 ? 1 + s.Row * 4 : s.Row * 3; }

        void NewWave()
        {
            ships.Clear(); bombs.Clear(); shots.Clear();
            waveTicks = 0; nextDive = 50;
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    var s = new Ship { Row = r, Col = c, State = Entering, Kind = (wave + r) % designs.Length, Hp = (N == 16 ? 2 : 1) + (r == 0 ? 1 : 0) };
                    int side = c == 1 ? (r == 0 ? -1 : 1) : c == 0 ? -1 : 1;   // in from both sides
                    s.Delay = (r * Cols + c) * 6;
                    double y0 = N * (0.35 + rng.NextDouble() * 0.2);
                    s.P = new[] { side < 0 ? -W - 1.0 : N + 1.0, y0, N / 2.0 - side * N * 0.1, N * 0.85, N / 2.0 + side * N * 0.35, N * 0.05, 0, 0 };
                    s.X = s.P[0]; s.Y = s.LastY = s.P[1];
                    s.Dt = Speed() / (N * 2.2);
                    ships.Add(s);
                }
        }

        double Speed() { return (0.45 + 0.05 * Math.Min(wave, 6)) * K; }

        static double Bez(double a, double b, double c, double d, double t)
        {
            double u = 1 - t;
            return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        }

        public int[] Next()
        {
            tick++; waveTicks++;
            foreach (var s in stars) { s.Y += s.Dy; if (s.Y >= N) { s.Y -= N; s.X = rng.Next(N); } }
            for (int k = sparks.Count - 1; k >= 0; k--) { var s = sparks[k]; s.X += s.Dx; s.Y += s.Dy; if (--s.Life <= 0) sparks.RemoveAt(k); }

            if (pause > 0) { if (--pause == 0) { wave++; NewWave(); } return Draw(); }
            if (ships.Count == 0 || waveTicks > 2500) { pause = 24; bombs.Clear(); shots.Clear(); return Draw(); }

            MoveShips();
            MoveShots();
            if (rocketDead > 0) { if (--rocketDead == 0) shipX = N / 2; }
            else Pilot();
            return Draw();
        }

        void MoveShips()
        {
            // now and then a ship in formation breaks off and dives at the rocket
            int diving = 0;
            foreach (var s in ships) if (s.State == Diving) diving++;
            if (--nextDive <= 0)
            {
                nextDive = Math.Max(10, 30 - wave * 3) + rng.Next(16);
                var ready = ships.FindAll(s => s.State == InFormation);
                if (ready.Count > 0 && diving < 1 + (wave + 1) / 3)
                    StartDive(ready[rng.Next(ready.Count)]);
            }
            foreach (var s in ships)
            {
                s.LastY = s.Y;
                if (s.Flash > 0) s.Flash--;
                if (s.State == Entering)
                {
                    if (s.Delay > 0) { s.Delay--; continue; }
                    s.T = Math.Min(1, s.T + s.Dt);
                    s.X = Bez(s.P[0], s.P[2], s.P[4], SlotX(s), s.T); s.Y = Bez(s.P[1], s.P[3], s.P[5], SlotY(s), s.T);
                    if (s.T >= 1) s.State = InFormation;
                }
                else if (s.State == InFormation) { s.X = SlotX(s); s.Y = SlotY(s); }
                else if (s.State == Diving)
                {
                    s.T += s.Dt;
                    s.X = Bez(s.P[0], s.P[2], s.P[4], s.P[6], s.T); s.Y = Bez(s.P[1], s.P[3], s.P[5], s.P[7], s.T);
                    // bombs away when it's lined up with the rocket on the way down
                    double mid = s.X + (W - 1) / 2.0;
                    if (s.Bombs > 0 && rocketDead == 0 && s.Y > N * 0.1 && s.Y < N * 0.4 && Math.Abs(mid - shipX) < 2 * K
                        && (bombs.Count == 0 || bombs[bombs.Count - 1].Y > s.Y + H + 3 * K))
                    {
                        s.Bombs--;
                        bombs.Add(new Dot { X = Math.Round(mid), Y = s.Y + H, Dy = 0.4 * K });
                    }
                    if (s.T >= 1) { s.State = Returning; s.X = SlotX(s); s.Y = s.LastY = -H - 1; }   // gone off the side: back in from the top
                }
                else if (s.State == Returning)
                {
                    double dx = SlotX(s) - s.X, dy = SlotY(s) - s.Y, d = Math.Sqrt(dx * dx + dy * dy), v = Speed() * 0.8;
                    if (d <= v) { s.State = InFormation; s.X = SlotX(s); s.Y = SlotY(s); }
                    else { s.X += dx / d * v; s.Y += dy / d * v; }
                }
            }
        }

        void StartDive(Ship s)
        {
            int dir = s.X + W / 2.0 < N / 2.0 ? -1 : 1;   // loops out toward its own side first
            double aim = shipX - (W - 1) / 2.0 + (rng.NextDouble() * 2 - 1) * 2.5 * K;   // roughly at the rocket
            // swoops down over the rocket, then pulls up and away off the far side
            double exit = dir < 0 ? N + 2.0 : -W - 2.0;
            s.P = new[] { s.X, s.Y, s.X + dir * 3 * K, s.Y - 2 * K, aim, N * 0.95, exit, N * 0.35 };
            double len = 0;
            for (int k = 0; k < 6; k += 2) len += Math.Sqrt(Math.Pow(s.P[k + 2] - s.P[k], 2) + Math.Pow(s.P[k + 3] - s.P[k + 1], 2));
            s.Dt = Speed() * 1.1 / len; s.T = 0;
            s.Bombs = 1 + (rng.Next(3) == 0 ? 1 : 0);   // one bomb, sometimes two
            s.State = Diving;
        }

        bool Solid(Ship s, int x, int y)
        {
            int rx = (int)Math.Round(s.X), ry = (int)Math.Round(s.Y);
            if (x < rx || x >= rx + W || y < ry || y >= ry + H) return false;
            return designs[s.Kind][y - ry][x - rx] != '.';
        }

        // the rocket: a nose on top of a 3-wide body (on the Tivoo, a 3 x 3 rocket: nose, body, then fins and the engine)
        int RocketTop { get { return N == 16 ? N - 3 : N - 2; } }
        bool RocketAt(int x, int y)
        {
            if (rocketDead > 0 || y < RocketTop || y >= N) return false;
            return y == RocketTop ? x == shipX : Math.Abs(x - shipX) <= 1;
        }

        void MoveShots()
        {
            for (int k = shots.Count - 1; k >= 0; k--)
            {
                var sh = shots[k];
                Ship hit = null;
                for (int step = 0; step < 2 && hit == null; step++)   // two half-steps so a shot can't slip past a ship
                {
                    sh.Y -= 0.7 * K;
                    hit = ships.Find(s => (s.State != Entering || s.T > 0.6) && Solid(s, (int)sh.X, (int)Math.Round(sh.Y)));
                }
                if (hit != null)
                {
                    if (--hit.Hp > 0) { hit.Flash = 4; Boom(sh.X, sh.Y, 7, 2); }   // a big one shrugs off the first hit
                    else { Boom(hit.X + (W - 1) / 2.0, hit.Y + (H - 1) / 2.0, RowColor(hit.Row, 0), N == 16 ? 10 : 5); ships.Remove(hit); }
                }
                if (hit != null || sh.Y < -1) shots.RemoveAt(k);
            }
            for (int k = bombs.Count - 1; k >= 0; k--)
            {
                var b = bombs[k];
                b.Y += b.Dy;
                if (RocketAt((int)b.X, (int)Math.Round(b.Y))) { bombs.RemoveAt(k); RocketHit(); }
                else if (b.Y >= N + 1) bombs.RemoveAt(k);
            }
            // a diving ship that flies into the rocket takes it out too
            if (rocketDead == 0)
                foreach (var s in ships.ToArray())
                    if (s.State == Diving && HitsRocket(s))
                    {
                        Boom(s.X + W / 2.0, s.Y + H / 2.0, RowColor(s.Row, 0), 6); ships.Remove(s); RocketHit(); break;
                    }
        }

        bool HitsRocket(Ship s)
        {
            for (int y = RocketTop; y < N; y++)
                for (int x = shipX - 1; x <= shipX + 1; x++)
                    if (RocketAt(x, y) && Solid(s, x, y)) return true;
            return false;
        }

        void RocketHit()
        {
            Boom(shipX, (RocketTop + N - 1) / 2.0, 7, 10);
            rocketDead = 28;
            shots.Clear();
        }

        void Boom(double x, double y, int color, int n)
        {
            for (int k = 0; k < n; k++)
            {
                double a = rng.NextDouble() * 2 * Math.PI, v = (0.25 + rng.NextDouble() * 0.4) * K;
                sparks.Add(new Dot { X = x, Y = y, Dx = Math.Cos(a) * v, Dy = Math.Sin(a) * v, Life = 4 + rng.Next(5), Color = k == 0 ? 7 : k % 2 == 0 ? color : (N == 16 ? Palette16.Orange : 1) });
            }
        }

        // the rocket slides to the safest spot near whatever it's aiming at, and fires when a ship is overhead
        void Pilot()
        {
            if (cooldown > 0) cooldown--;
            Ship target = null;
            foreach (var s in ships)
                if (s.State != Entering && (target == null || Threat(s) > Threat(target))) target = s;
            double aim = target == null ? N / 2.0 : target.X + (W - 1) / 2.0;
            if (target != null && target.State == Diving) aim += (target.P[6] - target.P[4]) * 0.15;   // lead a diver a little

            int best = shipX; double bestScore = double.MaxValue;
            for (int x = 1; x <= N - 2; x++)
            {
                double danger = 0;
                foreach (var b in bombs) if (Math.Abs(b.X - x) <= 1 && b.Y > N - 9 * K) danger += 10 + b.Y;
                foreach (var s in ships)
                    if (s.State == Diving && s.Y > N - 7 * K && s.X - 1 <= x + 1 && s.X + W >= x - 1) danger += 12;
                double score = danger * 100 + Math.Abs(x - aim) + Math.Abs(x - shipX) * 0.6;
                if (score < bestScore) { bestScore = score; best = x; }
            }
            if (tick % (N == 16 ? 1 : 2) == 0) shipX += Math.Sign(best - shipX);

            if (cooldown == 0 && shots.Count < 2)
                foreach (var s in ships)
                    if (s.State != Entering && s.X - 0.5 <= shipX && s.X + W - 0.5 >= shipX && s.Y < RocketTop - 1)
                    {
                        shots.Add(new Dot { X = shipX, Y = RocketTop });
                        cooldown = 6;
                        break;
                    }
        }

        // how much the rocket cares about a ship: divers first, the closer the more
        double Threat(Ship s) { return s.Y + (s.State == Diving ? 20 : 0) - Math.Abs(s.X - shipX) * 0.3; }

        int RowColor(int row, int part) { return waveColors[wave % waveColors.Length][row * 2 + part]; }

        int[] Draw()
        {
            var px = new int[N * N];
            Action<double, double, int> put = (x, y, color) =>
            {
                int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
                if (ix >= 0 && ix < N && iy >= 0 && iy < N) px[iy * N + ix] = color;
            };
            int hot = N == 16 ? Palette16.Orange : 1;
            foreach (var s in stars) put(s.X, Math.Floor(s.Y), s.Color);
            // bombs: a glowing head with a dim trail on the Tivoo
            foreach (var b in bombs)
            {
                if (N == 16) put(b.X, b.Y - 1, Palette16.DarkRed);
                put(b.X, b.Y, tick % 2 == 0 ? 1 : hot);
            }
            foreach (var sh in shots) put(sh.X, sh.Y, 3);
            foreach (var s in ships)
            {
                if (s.State == Entering && s.Delay > 0) continue;
                int rx = (int)Math.Round(s.X), ry = (int)Math.Round(s.Y);
                // heading down (a dive, or coming back in from the top): the engines blaze above it
                if ((s.State == Diving || s.State == Returning) && s.Y > s.LastY)
                {
                    int mid = rx + W / 2;
                    put(mid, ry - 1, tick % 2 == 0 ? 3 : hot);
                    if (N == 16) { put(mid - 1, ry - 1, hot); put(mid + 1, ry - 1, hot); if (tick % 3 != 0) put(mid, ry - 2, Palette16.DarkRed); }
                }
                var d = designs[s.Kind];
                int body = s.Flash > 0 && s.Flash % 2 == 0 ? 7 : RowColor(s.Row, 0), trim = RowColor(s.Row, 1);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        char ch = d[y][x];
                        if (ch == '.') continue;
                        int c = ch == 'b' ? body : ch == 't' ? trim
                              : ch == 'c' ? ((tick / 6 + s.Col) % 4 == 0 ? 7 : 6)                     // the dome or cockpit glints
                              : ((tick / 3 + x + s.Col) % 2 == 0 ? 7 : 3);                             // running lights
                        put(rx + x, ry + y, c);
                    }
            }
            if (rocketDead == 0)
            {
                int flame = tick % 3 == 0 ? 3 : hot;   // the engine flickers
                if (N == 16)
                {
                    put(shipX, N - 3, 6);
                    put(shipX - 1, N - 2, Palette16.SkyBlue); put(shipX, N - 2, 7); put(shipX + 1, N - 2, Palette16.SkyBlue);
                    put(shipX - 1, N - 1, 7); put(shipX, N - 1, flame); put(shipX + 1, N - 1, 7);
                }
                else
                {
                    put(shipX, N - 2, 6);
                    put(shipX - 1, N - 1, 7); put(shipX, N - 1, 7); put(shipX + 1, N - 1, 7);
                }
            }
            foreach (var sp in sparks) put(sp.X, sp.Y, sp.Life > 6 ? 7 : sp.Color);
            return px;
        }
    }

    // Aquarium: a planted tank seen through the glass. A goldfish, an angelfish and a little school of neon tetras
    // wander about, slowing to turn around and flicking their tails; grasses, a sword plant and a red plant sway; an air
    // stone sends up a stream of bubbles that pop at the shimmering surface; and now and then food flakes drift down
    // and the fish rise to snap them up. Next() gives RGB colors at 16 x 16 (soft-edged, so things glide between
    // pixels) and palette indexes at 10 x 10.
    class Aquarium
    {
        class Fish { public int Kind, Dir = 1, Rest; public double X, Y, Vx, Vy, Tx, Ty, Tail, Ox, Oy; public bool Lead; }
        class Dot { public double X, Y, V, Wob; public int Life; }   // a bubble, a food flake or a ripple
        class Blade { public double X, Lean, Amp, Phase; public int H, Kind; public bool Front; }

        // fish facing right, top row first. o back, O body, p belly, t tail fin, f fin, K stripe, s/S silver, e eye,
        // b blue stripe, c head, r red; at 10 x 10 the digits are palette indexes
        static readonly string[][] Sprites16 = {
            new[] { "t.oOo.", "ttOOeO", "t.ppp." },                    // goldfish
            new[] { "..f..", ".Kse.", "tKSSS", ".Kss.", "..f.." },     // angelfish
            new[] { "bbc", "r.." },                                    // neon tetra
        };
        static readonly string[][] Sprites10 = { new[] { ".1.", "311" }, new[] { "7" }, new[] { "16" } };
        static readonly double[] Speed = { 0.22, 0.14, 0.34 };

        readonly int N, Floor; readonly bool Big;
        readonly Random rng = new Random();
        readonly List<Fish> fish = new List<Fish>();
        readonly List<Blade> blades = new List<Blade>();
        readonly List<Dot> bubbles = new List<Dot>(), flakes = new List<Dot>(), ripples = new List<Dot>();
        readonly double StoneX;
        readonly double[] back, R, G, B, lr, lg, lb, la;   // the still scenery, the picture, and one soft-edged object
        int tick, nextFeed;

        public Aquarium() : this(10) { }
        public Aquarium(int size)
        {
            N = size; Big = size == 16; Floor = Big ? 14 : 9; StoneX = Big ? 10.5 : 6;
            int M = N * N; back = new double[M * 3]; R = new double[M]; G = new double[M]; B = new double[M];
            lr = new double[M]; lg = new double[M]; lb = new double[M]; la = new double[M];
            if (Big)
            {
                Grass(0, 0.7, 12, 0.04); Grass(0, 2.3, 9, -0.05); Grass(0, 1.4, 6, 0.1);
                Grass(1, 8.6, 5, 0);
                Grass(2, 14.4, 7, -0.34); Grass(2, 14.6, 9, -0.1); Grass(2, 14.9, 5, 0.18);
                Grass(3, 3.8, 2, -0.2); Grass(3, 12.6, 2, 0.15);
                AddFish(0); AddFish(1); for (int k = 0; k < 3; k++) AddFish(2);
            }
            else
            {
                Grass(0, 0.5, 7, 0.03); Grass(0, 3.5, 3, 0);
                Grass(2, 9, 5, -0.15);
                AddFish(0); AddFish(2); AddFish(2);
            }
            fish[Big ? 2 : 1].Lead = true;
            MakeBack();
            nextFeed = 150 + rng.Next(200);
        }

        void Grass(int kind, double x, int h, double lean)
        {
            blades.Add(new Blade { Kind = kind, X = x, H = h, Lean = lean, Front = kind == 3,
                                   Amp = kind == 0 ? 0.9 : kind == 2 ? 0.45 : 0.3, Phase = rng.NextDouble() * 6 });
        }

        void AddFish(int kind)
        {
            var f = new Fish { Kind = kind, Dir = rng.Next(2) * 2 - 1, Tail = rng.NextDouble() * 6 };
            f.X = rng.NextDouble() * (N - W(f)); f.Y = 1 + rng.NextDouble() * (Floor - H(f) - 2);
            f.Tx = f.X; f.Ty = f.Y; f.Ox = -2 - rng.Next(3); f.Oy = rng.Next(5) - 2;
            fish.Add(f);
        }

        string[] Sprite(Fish f) { return (Big ? Sprites16 : Sprites10)[f.Kind]; }
        int W(Fish f) { return Sprite(f)[0].Length; }
        int H(Fish f) { return Sprite(f).Length; }

        static int Hash(int x, int y) { unchecked { int h = x * 374761393 + y * 668265263; h = (h ^ (h >> 13)) * 1274126177; return (h ^ (h >> 16)) & 0x7FFFFFFF; } }

        static int Mix(int a, int b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            Func<int, int> ch = s => (int)Math.Round(((a >> s) & 255) * (1 - t) + ((b >> s) & 255) * t);
            return (ch(16) << 16) | (ch(8) << 8) | ch(0);
        }

        // still scenery at 16 x 16: water darkening with depth, gravel, a rock and the air stone
        void MakeBack()
        {
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int c;
                    if (y < Floor) c = Mix(0x2C84B0, 0x082848, Math.Pow(y / (double)(Floor - 1), 0.85));
                    else
                    {
                        int h = Hash(x, y) % 10;
                        c = h < 2 ? 0x6A5640 : h < 4 ? 0xD8C8A4 : h < 6 ? 0x9C8058 : 0xBCA478;
                        if (y > Floor) c = Mix(c, 0, 0.25);     // the front of the gravel bed is in shadow
                    }
                    Put(back, x, y, c);
                }
            if (!Big) return;
            string[] rock = { ".##.", "####" };   // a rock at the back, lit from above
            for (int r = 0; r < 2; r++)
                for (int k = 0; k < 4; k++)
                    if (rock[r][k] == '#') Put(back, 4 + k, Floor - 2 + r, Mix(r == 0 ? 0x8E8C84 : 0x5E5C58, 0, Hash(k, r) % 3 * 0.12));
            Put(back, 10, Floor - 1, 0xA4ACB4); Put(back, 11, Floor - 1, 0x6C747C);   // the air stone
        }

        void Put(double[] img, int x, int y, int c) { int k = (y * N + x) * 3; img[k] = (c >> 16) & 255; img[k + 1] = (c >> 8) & 255; img[k + 2] = c & 255; }

        // soft drawing: a pixel at a fractional x lands partly on two pixels of the object being drawn
        void Soft(double x, int y, int c, double a)
        {
            if (y < 0 || y >= N || a <= 0) return;
            int ix = (int)Math.Floor(x); double f = x - ix;
            Add(ix, y, c, a * (1 - f)); Add(ix + 1, y, c, a * f);
        }
        void Add(int x, int y, int c, double a)
        {
            if (x < 0 || x >= N || a <= 0) return;
            int k = y * N + x; lr[k] += ((c >> 16) & 255) * a; lg[k] += ((c >> 8) & 255) * a; lb[k] += (c & 255) * a; la[k] += a;
        }
        void Lay()   // puts the object over the picture
        {
            for (int k = 0; k < la.Length; k++)
            {
                if (la[k] <= 0) continue;
                double a = Math.Min(1, la[k]);
                R[k] = R[k] * (1 - a) + lr[k] / la[k] * a; G[k] = G[k] * (1 - a) + lg[k] / la[k] * a; B[k] = B[k] * (1 - a) + lb[k] / la[k] * a;
                lr[k] = lg[k] = lb[k] = la[k] = 0;
            }
        }

        public int[] Next()
        {
            tick++;
            Feed(); MoveFish(); MoveBubbles();
            return Big ? Draw() : DrawSmall();
        }

        // ---- what happens ----

        void Feed()
        {
            if (--nextFeed <= 0)
            {
                nextFeed = 400 + rng.Next(400);
                double x0 = N * 0.3 + rng.NextDouble() * N * 0.4;
                for (int k = 0; k < (Big ? 4 : 2); k++)
                    flakes.Add(new Dot { X = x0 + rng.NextDouble() * 4 - 2, Y = -rng.NextDouble() * 2, V = 0.05 + rng.NextDouble() * 0.04, Wob = rng.NextDouble() * 6, Life = 80 });
            }
            foreach (var d in flakes)
            {
                if (d.Y < Floor - 0.6) { d.Y += d.V; d.X += Math.Sin(tick * 0.08 + d.Wob) * 0.04; }
                else d.Life--;   // lying on the gravel
            }
            flakes.RemoveAll(d => d.Life <= 0);
        }

        double MouthX(Fish f) { return f.Dir > 0 ? f.X + W(f) - 1 : f.X; }
        double MouthY(Fish f) { return f.Y + H(f) / 2; }

        void MoveFish()
        {
            Fish lead = fish.Find(f => f.Lead);
            foreach (var f in fish)
            {
                int w = W(f), h = H(f);
                double speed = Speed[f.Kind];
                Dot food = null; double best = 1e9;
                foreach (var d in flakes)
                {
                    double dist = Math.Abs(d.X - MouthX(f)) + Math.Abs(d.Y - MouthY(f));
                    if (dist < best) { best = dist; food = d; }
                }
                if (food != null)
                {
                    speed *= 1.6;
                    f.Tx = food.X > f.X + w / 2.0 ? food.X - (w - 1) : food.X;
                    f.Ty = food.Y - h / 2;
                    if (Math.Abs(food.X - MouthX(f)) < 0.9 && Math.Abs(food.Y - MouthY(f)) < 0.9) { flakes.Remove(food); f.Rest = 0; }
                }
                else if (f.Kind == 2 && !f.Lead)
                {   // tetras keep to their school, trailing the leader
                    if (rng.Next(80) == 0) { f.Ox = -2 - rng.Next(3); f.Oy = rng.Next(5) - 2; }
                    f.Tx = lead.X + f.Ox * lead.Dir; f.Ty = lead.Y + f.Oy;
                    speed *= 1.4;
                }
                else if (Math.Abs(f.Tx - f.X) < 0.5 && Math.Abs(f.Ty - f.Y) < 0.5)
                {
                    if (f.Rest > 0) f.Rest--;
                    else
                    {   // somewhere new; the goldfish often noses along the bottom, the angelfish keeps to the middle
                        f.Tx = rng.NextDouble() * (N - w);
                        f.Ty = f.Kind == 0 && rng.Next(3) == 0 ? Floor - h : f.Kind == 1 ? 2 + rng.NextDouble() * (Floor - h - 5) : 1 + rng.NextDouble() * (Floor - h - 2);
                        f.Rest = rng.Next(f.Kind == 1 ? 60 : 30);
                    }
                }
                f.Tx = Math.Max(0, Math.Min(N - w, f.Tx)); f.Ty = Math.Max(1, Math.Min(Floor - h, f.Ty));

                double dx = f.Tx - f.X, dy = f.Ty - f.Y;
                double want = Math.Abs(dx) < 0.3 ? 0 : Math.Sign(dx) * Math.Min(speed, Math.Abs(dx) * 0.12 + 0.03);
                if (want * f.Dir < 0)
                {   // a fish swims forward: it slows down, then turns around
                    f.Vx *= 0.75;
                    if (Math.Abs(f.Vx) < 0.04) { f.Dir = -f.Dir; f.Vx = 0; }
                }
                else f.Vx += (want - f.Vx) * 0.1;
                f.Vy += (Math.Max(-speed * 0.5, Math.Min(speed * 0.5, dy * 0.1)) - f.Vy) * 0.1;
                f.X = Math.Max(0, Math.Min(N - w, f.X + f.Vx));
                f.Y = Math.Max(1, Math.Min(Floor - h, f.Y + f.Vy));
                f.Tail += 0.3 + Math.Abs(f.Vx) * 2.5;
            }
        }

        void MoveBubbles()
        {
            if (rng.NextDouble() < (Big ? 0.35 : 0.2))
                bubbles.Add(new Dot { X = StoneX + rng.NextDouble() * 0.6 - 0.3, Y = Floor - 1.5, V = 0.35 + rng.NextDouble() * 0.2, Wob = rng.NextDouble() * 6 });
            foreach (var b in bubbles)
            {
                b.Y -= b.V; b.V = Math.Min(0.9, b.V * 1.03);
                b.X += Math.Sin(b.Y * 1.3 + b.Wob) * 0.12;
                if (b.Y < 0.5) { b.Life = -1; ripples.Add(new Dot { X = b.X, Life = 3 }); }
            }
            bubbles.RemoveAll(b => b.Life < 0);
            foreach (var r in ripples) r.Life--;
            ripples.RemoveAll(r => r.Life <= 0);
        }

        double Sway(Blade b, int h) { double t = (h + 1.0) / b.H; return b.X + b.Lean * h + b.Amp * t * t * Math.Sin(tick * 0.05 + b.Phase + h * 0.18); }

        // ---- drawing, 16 x 16 ----

        int[] Draw()
        {
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int k = y * N + x; R[k] = back[k * 3]; G[k] = back[k * 3 + 1]; B[k] = back[k * 3 + 2];
                    if (y >= Floor) continue;
                    double depth = y / (double)Floor;
                    // light from above wavers through the water, and the surface glints
                    double ray = Math.Pow(Math.Max(0, Math.Sin(x * 0.55 + y * 0.3 - tick * 0.06) * Math.Sin(x * 0.23 - y * 0.12 + tick * 0.037)), 2) * 34 * (1 - depth);
                    if (y == 0) ray += 40 + 30 * Math.Sin(x * 1.1 + tick * 0.21) + 18 * Math.Sin(x * 2.3 - tick * 0.13);
                    R[k] += ray * 0.7; G[k] += ray; B[k] += ray;
                }
            foreach (var r in ripples)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = (int)Math.Round(r.X) + dx;
                    if (x >= 0 && x < N) { R[x] += 30 * r.Life; G[x] += 30 * r.Life; B[x] += 30 * r.Life; }
                }

            foreach (var b in blades) if (!b.Front) DrawBlade(b);
            foreach (var d in flakes) { Soft(d.X, (int)Math.Round(d.Y), Hash((int)(d.Wob * 100), 1) % 2 == 0 ? 0xD08A40 : 0xA85A2C, 1); Lay(); }
            foreach (int kind in new[] { 1, 0, 2 }) foreach (var f in fish) if (f.Kind == kind) DrawFish(f);
            foreach (var b in blades) if (b.Front) DrawBlade(b);
            foreach (var b in bubbles)
            {
                double a = 0.45 + 0.4 * (1 - b.Y / Floor);
                Soft(b.X, (int)Math.Round(b.Y), 0xE4F6FF, a); Lay();
            }

            var px = new int[N * N];
            for (int k = 0; k < px.Length; k++)
                px[k] = (Clamp(R[k]) << 16) | (Clamp(G[k]) << 8) | Clamp(B[k]);
            return px;
        }

        static int Clamp(double v) { return v < 0 ? 0 : v > 255 ? 255 : (int)Math.Round(v); }

        void DrawBlade(Blade b)
        {
            for (int h = 0; h < b.H; h++)
            {
                int y = Floor - 1 - h; double t = h / (double)Math.Max(1, b.H - 1), x = Sway(b, h);
                int c = b.Kind == 1 ? Mix(0x7A2A1A, 0xE0583A, t)          // red plant
                      : b.Kind == 2 ? Mix(0x256E26, 0x72C24A, t)           // sword plant
                      : b.Kind == 3 ? Mix(0x3A8A2A, 0x8CD85A, t)           // carpet in front
                      : Mix(0x1A4C1E, 0x5AAA3A, t);                        // tall grass
                Soft(x, y, c, h == b.H - 1 ? 0.8 : 1);
                if (b.Kind == 1 && h % 2 == 1) Soft(x + (h % 4 == 1 ? -1 : 1), y, Mix(c, 0xFF7A50, 0.2), 0.7);   // little leaves
            }
            Lay();
        }

        void DrawFish(Fish f)
        {
            var sp = Sprite(f); int w = sp[0].Length, h = sp.Length;
            int y0 = (int)Math.Round(f.Y + Math.Sin(f.Tail * 0.3) * 0.15);
            bool flick = Math.Sin(f.Tail) > 0;
            for (int r = 0; r < h; r++)
                for (int k = 0; k < w; k++)
                {
                    char ch = sp[r][k];
                    if (ch == '.') continue;
                    if (ch == 't' && ((r == 0 && flick) || (r == h - 1 && !flick && h > 2))) continue;   // the tail sweeps up and down
                    double a = ch == 't' ? 0.6 : ch == 'f' ? 0.65 : 1;
                    int c = ch == 'o' ? 0xE2601A : ch == 'O' ? 0xFF8C26 : ch == 'p' ? 0xFFC48A : ch == 't' ? 0xFFA040
                          : ch == 'e' ? 0x161212 : ch == 'f' ? 0xC8D2DA : ch == 'K' ? 0x2A2C30 : ch == 's' ? 0xAEB8C2
                          : ch == 'S' ? 0xE8ECF0 : ch == 'b' ? 0x2ACAFF : ch == 'c' ? 0xA6CCDA : 0xEA2A3E;
                    double x = f.Dir > 0 ? f.X + k : f.X + (w - 1 - k);
                    Soft(x, y0 + r, c, a);
                }
            Lay();
        }

        // ---- drawing, 10 x 10 (palette indexes) ----

        int[] DrawSmall()
        {
            var px = new int[N * N];
            for (int x = 0; x < N; x++)
            {
                px[x] = Math.Sin(x * 1.1 + tick * 0.21) > 0.7 ? 6 : 4;     // the surface
                int h = Hash(x, Floor) % 5;
                px[Floor * N + x] = h == 0 ? 1 : h == 1 ? 0 : 3;           // gravel
            }
            foreach (var r in ripples) { int x = (int)Math.Round(r.X); if (x >= 0 && x < N) px[x] = 7; }
            px[(Floor - 1) * N + (int)StoneX] = 7;   // the air stone
            foreach (var b in blades)
                for (int h = 0; h < b.H; h++)
                {
                    int x = (int)Math.Round(Sway(b, h)), y = Floor - 1 - h;
                    if (x >= 0 && x < N) px[y * N + x] = b.Kind == 1 ? 1 : 2;
                }
            foreach (var d in flakes) Set(px, d.X, d.Y, 3);
            foreach (var f in fish)
            {
                var sp = Sprite(f); int w = sp[0].Length;
                for (int r = 0; r < sp.Length; r++)
                    for (int k = 0; k < w; k++)
                        if (sp[r][k] != '.') Set(px, f.Dir > 0 ? f.X + k : f.X + (w - 1 - k), f.Y + r, sp[r][k] - '0');
            }
            foreach (var b in bubbles) Set(px, b.X, b.Y, b.Y < 3 ? 7 : 6);
            return px;
        }

        void Set(int[] px, double x, double y, int c)
        {
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            if (ix >= 0 && ix < N && iy >= 0 && iy < N) px[iy * N + ix] = c;
        }
    }

    // Jellyfish: at 16 x 16 it is WallJellyfish (the jellyfish that swim across all the displays) on one screen. At
    // 10 x 10, in the AuraBox's 8 colors on black: two jellyfish whose bells alternate a tall dome and a flat cap as
    // they pulse up and sink, two tentacles each swaying below, and a few specks drifting by. Next() gives RGB colors
    // at 16 x 16 and palette indexes at 10 x 10.
    class Jellyfish
    {
        class Jelly { public double X, Y, Vx, Vy, Tx, Ty, Phase, Period, R, H, Squeeze; public int Bell, Arms, Len; }
        class Speck { public double X, Y, V, Wob; }

        readonly int N; readonly bool Big;
        readonly Random rng = new Random();
        readonly List<Jelly> jellies = new List<Jelly>();
        readonly List<Speck> snow = new List<Speck>();
        readonly WallJellyfish wall;
        readonly WallScreen screen = new WallScreen { X = 0, Y = 0, Size = 70, N = 16 };   // a Tivoo's screen, in millimeters
        int tick;

        public Jellyfish() : this(10) { }
        public Jellyfish(int size)
        {
            N = size; Big = size == 16;
            if (Big) { wall = new WallJellyfish(); wall.SetArea(new List<WallScreen> { screen }); return; }
            Add(7.0, 3, 1);   // yellow bell, red tentacles
            Add(3.0, 5, 6);   // magenta bell, cyan tentacles
            for (int k = 0; k < 4; k++)
                snow.Add(new Speck { X = rng.NextDouble() * N, Y = rng.NextDouble() * N, V = (rng.NextDouble() - 0.6) * 0.03, Wob = rng.NextDouble() * 6 });
        }

        void Add(double x, int bell, int arms)
        {
            var j = new Jelly { X = x, R = 1.5, H = 1.2, Bell = bell, Arms = arms, Len = 3, Period = 26 + rng.Next(10), Phase = rng.NextDouble() };
            j.Y = H0(j) + rng.NextDouble() * (Bottom(j) - H0(j)); j.Tx = x; j.Ty = j.Y;
            jellies.Add(j);
        }

        double H0(Jelly j) { return j.H * 1.3 + 0.5; }             // highest the rim goes (the bell stays in the water)
        double Bottom(Jelly j) { return N - 1 - j.Len * 0.8; }     // lowest, so the tentacles mostly show

        public int[] Next()
        {
            if (Big) { wall.Step(0.12); return wall.Render(screen); }   // a frame is about an eighth of a second at the usual speed
            tick++;
            foreach (var j in jellies) Move(j);
            foreach (var s in snow)
            {
                s.Y += s.V; s.X += Math.Sin(tick * 0.03 + s.Wob) * 0.015;
                if (s.Y < -1) s.Y = N; if (s.Y > N) s.Y = -1;
                if (s.X < 0) s.X += N; if (s.X >= N) s.X -= N;
            }
            return DrawSmall();
        }

        // a pulse: the bell squeezes for the first third of its beat and pushes up; the rest of the beat it relaxes,
        // the water slows it and it sinks gently. Each has a depth it is heading for (a new one every half minute or
        // so): above it, it pushes weakly and drifts down; below it, it pushes harder and climbs.
        void Move(Jelly j)
        {
            j.Phase += 1 / j.Period;
            double c = j.Phase - Math.Floor(j.Phase);
            j.Squeeze = c < 0.32 ? Math.Sin(Math.PI * c / 0.32) : 0;
            if (rng.Next(300) == 0) j.Ty = H0(j) + rng.NextDouble() * (Bottom(j) - H0(j));
            double push = Math.Max(0.15, Math.Min(1.8, 0.65 + (j.Y - j.Ty) * 0.45));
            j.Vy -= 0.052 * j.Squeeze * push;
            j.Vy += 0.0049;
            j.Vy *= 0.9;
            if (Math.Abs(j.Tx - j.X) < 0.4 || rng.Next(260) == 0) j.Tx = j.R + 0.3 + rng.NextDouble() * (N - 2 * j.R - 0.6);
            j.Vx += (j.Tx - j.X) * 0.0012; j.Vx *= 0.95;
            foreach (var o in jellies)   // they keep out of each other's way, bell to tentacle tips, by swimming aside
                if (o != j && Math.Abs(o.X - j.X) < o.R + j.R + 1.2 && j.Y - j.H * 1.4 < o.Y + o.Len + 1 && o.Y - o.H * 1.4 < j.Y + j.Len + 1)
                {
                    double away = Math.Sign(j.X - o.X + (jellies.IndexOf(j) - jellies.IndexOf(o)) * 0.01);
                    if (j.X - j.R < 1) away = 1; else if (j.X + j.R > N - 1) away = -1;   // cornered: the other way
                    j.Vx += away * 0.008;
                    j.Tx = Math.Max(j.R, Math.Min(N - j.R, j.X + away * (o.R + j.R + 2)));
                }
            j.X = Math.Max(j.R * 0.6, Math.Min(N - j.R * 0.6, j.X + j.Vx));
            j.Y = Math.Max(H0(j), Math.Min(Bottom(j), j.Y + j.Vy));
        }

        // ---- 10 x 10 (palette indexes): black water, bells that alternate a tall dome and a flat cap ----

        int[] DrawSmall()
        {
            var px = new int[N * N];
            foreach (var s in snow) Set(px, s.X, s.Y, 4);
            foreach (var j in jellies)
            {
                int x = (int)Math.Round(j.X), y = (int)Math.Round(j.Y);
                bool squeezed = j.Squeeze > 0.35;
                for (int s = -1; s <= 1; s += 2)   // two tentacles, swaying a pixel now and then
                    for (int i = 1; i <= j.Len; i++)
                    {
                        int sway = Math.Sin(tick * 0.16 + i * 0.9 + s) > 0.55 ? s : 0;
                        Set(px, x + s + sway - (squeezed && i == 1 ? s : 0), y + i, j.Arms);
                    }
                if (squeezed) { Set(px, x, y - 1, j.Bell == 5 ? 7 : j.Bell); Set(px, x - 1, y, j.Bell); Set(px, x, y, j.Bell); Set(px, x + 1, y, j.Bell); }
                else { Set(px, x - 1, y - 1, j.Bell); Set(px, x, y - 1, j.Bell); Set(px, x + 1, y - 1, j.Bell); Set(px, x - 1, y, j.Bell); Set(px, x + 1, y, j.Bell); }
            }
            return px;
        }

        void Set(int[] px, double x, double y, int c)
        {
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
            if (ix >= 0 && ix < N && iy >= 0 && iy < N) px[iy * N + ix] = c;
        }
    }

    // Days to the next holiday: how many days are left to the next US holiday in big digits, with "DAYS TO" and the
    // holiday's name scrolling underneath, the name in that holiday's colors (red, white and blue for July 4th, orange
    // and gold for Thanksgiving...) and a ribbon of its colors sliding along the bottom. On the day itself confetti
    // falls in its colors over a greeting ("HAPPY THANKSGIVING"); Memorial Day gets quiet twinkling stars instead.
    // Next() gives RGB colors at 16 x 16 and palette indexes at 10 x 10.
    class HolidayCountdown
    {
        internal class Day { public string Name, Greeting; public Func<int, DateTime> Date; public int[] Rgb, Index; public bool Solemn; }

        static readonly int[] Rwb = { 0xFF2A3A, 0xFFFFFF, 0x2E6BFF }, RwbI = { 1, 7, 4 };
        static DateTime Nth(int y, int m, DayOfWeek dow, int n)
        {
            var d = new DateTime(y, m, 1);
            while (d.DayOfWeek != dow) d = d.AddDays(1);
            return d.AddDays(7 * (n - 1));
        }
        static DateTime Last(int y, int m, DayOfWeek dow)
        {
            var d = new DateTime(y, m, DateTime.DaysInMonth(y, m));
            while (d.DayOfWeek != dow) d = d.AddDays(-1);
            return d;
        }

        static readonly Day[] Days = {
            new Day { Name = "New Year's Day", Greeting = "Happy New Year!", Date = y => new DateTime(y, 1, 1), Rgb = new[] { 0xFFD040, 0xFFFFFF, 0xC8D0DC }, Index = new[] { 3, 7 } },
            new Day { Name = "MLK Day", Greeting = "Martin Luther King Jr. Day", Date = y => Nth(y, 1, DayOfWeek.Monday, 3), Rgb = new[] { 0xFFFFFF, 0xB070FF }, Index = new[] { 7, 5 } },
            new Day { Name = "Valentine's Day", Greeting = "Happy Valentine's Day!", Date = y => new DateTime(y, 2, 14), Rgb = new[] { 0xFF2A5A, 0xFF8CC8 }, Index = new[] { 1, 5 } },
            new Day { Name = "Presidents' Day", Greeting = "Happy Presidents' Day!", Date = y => Nth(y, 2, DayOfWeek.Monday, 3), Rgb = Rwb, Index = RwbI },
            new Day { Name = "St. Patrick's Day", Greeting = "Happy St. Patrick's Day!", Date = y => new DateTime(y, 3, 17), Rgb = new[] { 0x2AE04A, 0xA0FF60, 0xFFD040 }, Index = new[] { 2, 3 } },
            new Day { Name = "Easter", Greeting = "Happy Easter!", Date = y => Holidays.Easter(y), Rgb = new[] { 0xFFA0DC, 0x90DCFF, 0xFFF080, 0xA0F0A0 }, Index = new[] { 5, 6, 3 } },
            new Day { Name = "Mother's Day", Greeting = "Happy Mother's Day!", Date = y => Nth(y, 5, DayOfWeek.Sunday, 2), Rgb = new[] { 0xFF6CB0, 0xFFC0E0 }, Index = new[] { 5, 7 } },
            new Day { Name = "Memorial Day", Greeting = "Memorial Day - We Remember", Date = y => Last(y, 5, DayOfWeek.Monday), Rgb = Rwb, Index = RwbI, Solemn = true },
            new Day { Name = "Father's Day", Greeting = "Happy Father's Day!", Date = y => Nth(y, 6, DayOfWeek.Sunday, 3), Rgb = new[] { 0x3C82FF, 0x8CD8FF }, Index = new[] { 4, 6 } },
            new Day { Name = "Juneteenth", Greeting = "Happy Juneteenth!", Date = y => new DateTime(y, 6, 19), Rgb = new[] { 0xFF3030, 0x30C850, 0xFFD000 }, Index = new[] { 1, 2, 3 } },
            new Day { Name = "July 4th", Greeting = "Happy Fourth of July!", Date = y => new DateTime(y, 7, 4), Rgb = Rwb, Index = RwbI },
            new Day { Name = "Labor Day", Greeting = "Happy Labor Day!", Date = y => Nth(y, 9, DayOfWeek.Monday, 1), Rgb = new[] { 0x3C82FF, 0xFFC020 }, Index = new[] { 4, 3 } },
            new Day { Name = "Halloween", Greeting = "Happy Halloween!", Date = y => new DateTime(y, 10, 31), Rgb = new[] { 0xFF7A00, 0xA848FF, 0x60FF40 }, Index = new[] { 3, 5, 2 } },
            new Day { Name = "Veterans Day", Greeting = "Thank You, Veterans!", Date = y => new DateTime(y, 11, 11), Rgb = Rwb, Index = RwbI },
            new Day { Name = "Thanksgiving", Greeting = "Happy Thanksgiving!", Date = y => Holidays.Thanksgiving(y), Rgb = new[] { 0xFF8C1E, 0xFFC848, 0xD8502A }, Index = new[] { 3, 1 } },
            new Day { Name = "Christmas", Greeting = "Merry Christmas!", Date = y => new DateTime(y, 12, 25), Rgb = new[] { 0xFF2828, 0x28D050, 0xFFFFFF }, Index = new[] { 1, 2, 7 } },
        };

        // 3 x 5 capitals (digits come from WeatherArt.Font)
        static readonly Dictionary<char, string> Letters = new Dictionary<char, string> {
            { 'A', "010101111101101" }, { 'B', "110101110101110" }, { 'C', "011100100100011" }, { 'D', "110101101101110" },
            { 'E', "111100110100111" }, { 'F', "111100110100100" }, { 'G', "011100101101011" }, { 'H', "101101111101101" },
            { 'I', "111010010010111" }, { 'J', "001001001101010" }, { 'K', "101101110101101" }, { 'L', "100100100100111" },
            { 'M', "101111111101101" }, { 'N', "110101101101101" }, { 'O', "010101101101010" }, { 'P', "110101110100100" },
            { 'Q', "010101101110011" }, { 'R', "110101110101101" }, { 'S', "011100010001110" }, { 'T', "111010010010010" },
            { 'U', "101101101101111" }, { 'V', "101101101101010" }, { 'W', "101101111111101" }, { 'X', "101101010101101" },
            { 'Y', "101101010010010" }, { 'Z', "111001010100111" }, { '\'', "010010000000000" }, { '.', "000000000000010" },
            { ',', "000000000010100" }, { '!', "010010010000010" }, { '-', "000000111000000" } };

        readonly int N; readonly bool Big;
        readonly Random rng = new Random();
        readonly List<double[]> confetti = new List<double[]>();   // x, y, speed, color slot
        List<int[]> strip;    // the scrolling words: one 5-pixel column each (colors, 0 = off)
        string showing;
        int tick, scroll;

        public static Func<DateTime> Today = () => DateTime.Today;   // the test harness sets other days

        public HolidayCountdown() : this(10) { }
        public HolidayCountdown(int size) { N = size; Big = size == 16; }

        // the next holiday on or after this day
        internal static Day Upcoming(DateTime today, out int days)
        {
            Day best = null; DateTime when = DateTime.MaxValue;
            foreach (var h in Days)
                for (int y = today.Year; y <= today.Year + 1; y++)
                {
                    var d = h.Date(y);
                    if (d >= today && d < when) { when = d; best = h; }
                }
            days = (int)(when - today).TotalDays;
            return best;
        }

        int Color(Day h, int k) { return Big ? h.Rgb[k % h.Rgb.Length] : h.Index[k % h.Index.Length]; }

        public int[] Next()
        {
            tick++;
            int days;
            var h = Upcoming(Today(), out days);
            string key = h.Name + days;
            if (key != showing)
            {   // a new day: new words, started from the right edge
                showing = key; scroll = 0; confetti.Clear();
                strip = days == 0 ? Words(h.Greeting.ToUpperInvariant(), h, true)
                                  : Words(days == 1 ? "DAY TO " : "DAYS TO ", h, false, h.Name.ToUpperInvariant());
            }

            var px = new int[N * N];
            if (days == 0) Celebrate(px, h);
            else
            {
                Number(px, days, Color(h, 0));
                if (Big)   // a ribbon of the holiday's colors slides along the bottom
                    for (int x = 0; x < N; x++)
                    {
                        int c = h.Rgb[((x + tick / 2) / 2) % h.Rgb.Length];
                        px[15 * N + x] = Dim(c, 0.55);
                    }
            }

            int top = Big ? 9 : 5, loop = strip.Count + N;
            for (int x = 0; x < N; x++)
            {
                int k = (scroll + x) % loop - N;   // a screen's width of gap between passes
                if (k < 0 || k >= strip.Count) continue;
                for (int r = 0; r < 5; r++) if (strip[k][r] != 0) px[(top + r) * N + x] = strip[k][r];
            }
            scroll = (scroll + 1) % loop;
            return px;
        }

        static int Dim(int c, double f) { return (int)((c >> 16 & 255) * f) << 16 | (int)((c >> 8 & 255) * f) << 8 | (int)((c & 255) * f); }

        // the words as columns: the lead-in in white or gray, the holiday letter by letter in its colors
        List<int[]> Words(string lead, Day h, bool allColor, string name = "")
        {
            var cols = new List<int[]>();
            int plain = Big ? 0xC8C8C8 : 7, n = 0;
            foreach (char ch in lead + name)
            {
                bool inName = allColor || n >= lead.Length;
                int color = inName ? Color(h, n - (allColor ? 0 : lead.Length)) : plain;
                n++;
                if (ch == ' ') { cols.Add(new int[5]); cols.Add(new int[5]); continue; }
                string g;
                if (!Letters.TryGetValue(ch, out g) && !WeatherArt.Font.TryGetValue(ch, out g)) continue;
                int w = ch == '\'' || ch == '.' || ch == '!' || ch == ',' ? 2 : 3;   // narrow marks
                int from = w == 2 && ch != ',' ? 1 : 0;
                for (int c = from; c < from + w; c++)
                {
                    var col = new int[5];
                    for (int r = 0; r < 5; r++) if (c < 3 && g[r * 3 + c] == '1') col[r] = color;
                    cols.Add(col);
                }
                cols.Add(new int[5]);
            }
            return cols;
        }

        // the day count: 5 x 7 digits on 16 x 16 (3 x 5 past 99), 3 x 5 digits on 10 x 10
        void Number(int[] px, int days, int color)
        {
            string s = days.ToString();
            bool large = Big && s.Length <= 2;
            int w = large ? 5 : 3, h = large ? 7 : 5, step = w + 1, width = s.Length * step - 1;
            int x0 = (N - width) / 2, y0 = Big ? (large ? 0 : 2) : 0;
            for (int i = 0; i < s.Length; i++)
                for (int r = 0; r < h; r++)
                    for (int c = 0; c < w; c++)
                    {
                        bool on = large ? ClockArt.Big[s[i] - '0'][r * 5 + c] == '#' : WeatherArt.Font[s[i]][r * 3 + c] == '1';
                        if (on) px[(y0 + r) * N + x0 + i * step + c] = color;
                    }
        }

        // the day itself: confetti in the holiday's colors above the greeting (stars twinkling on a solemn day)
        void Celebrate(int[] px, Day h)
        {
            int rows = Big ? 8 : 5;
            if (h.Solemn)
            {
                for (int k = 0; k < (Big ? 6 : 3); k++)
                {
                    int x = (k * 7 + 3) % N, y = (k * 5 + 1) % rows;
                    double glow = 0.5 + 0.5 * Math.Sin(tick * 0.12 + k * 1.7);
                    if (Big) px[y * N + x] = Dim(0xFFFFFF, 0.25 + 0.75 * glow);
                    else if (glow > 0.4) px[y * N + x] = 7;
                }
                return;
            }
            if (rng.NextDouble() < (Big ? 0.7 : 0.45))
                confetti.Add(new[] { rng.Next(N), -1.0, 0.2 + rng.NextDouble() * 0.3, rng.Next(6) });
            foreach (var p in confetti)
            {
                p[1] += p[2];
                int x = (int)Math.Round(p[0] + Math.Sin(p[1] * 1.3 + p[3]) * 0.4), y = (int)p[1];
                if (y >= 0 && y < rows && x >= 0 && x < N) px[y * N + x] = Color(h, (int)p[3]);
            }
            confetti.RemoveAll(p => p[1] >= rows);
        }
    }

    // ---------- animations across all displays ----------
    // One picture laid over every display as they stand (the All Displays page): it lives in millimeters, and each
    // display shows the part of it that falls on its screen, so a ball leaving one screen comes in on the next at the
    // same height and speed (crossing the gap between them unseen).

    // a display's screen: its top-left corner and width in millimeters, and its pixels across
    class WallScreen { public double X, Y, Size; public int N; }

    abstract class WallAnim
    {
        public double Left, Top, Right, Bottom;   // around all the screens, in millimeters
        protected List<WallScreen> Screens = new List<WallScreen>();   // the screens themselves, for animations placed on them

        public void SetArea(List<WallScreen> screens)
        {
            Screens = new List<WallScreen>(screens);
            Left = Top = double.MaxValue; Right = Bottom = double.MinValue;
            foreach (var s in screens) { Left = Math.Min(Left, s.X); Top = Math.Min(Top, s.Y); Right = Math.Max(Right, s.X + s.Size); Bottom = Math.Max(Bottom, s.Y + s.Size); }
        }

        public abstract void Step(double seconds);

        // the RGB color at a point; pixel: the size of a pixel there in millimeters (for soft edges)
        public abstract int ColorAt(double x, double y, double pixel);

        // one display's frame: RGB on 16 x 16 (each pixel from 2 x 2 samples), palette indexes on 10 x 10
        public int[] Render(WallScreen s)
        {
            int n = s.N; double pix = s.Size / n;
            var px = new int[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int c = ColorAt(s.X + (i + 0.25 + k % 2 * 0.5) * pix, s.Y + (j + 0.25 + k / 2 * 0.5) * pix, pix);
                        r += c >> 16 & 255; g += c >> 8 & 255; b += c & 255;
                    }
                    r /= 4; g /= 4; b /= 4;
                    px[j * n + i] = n == 16 ? r << 16 | g << 8 | b
                                  : (r > 100 ? 1 : 0) | (g > 100 ? 2 : 0) | (b > 100 ? 4 : 0);   // the AuraBox's 8 colors
                }
            return px;
        }

        protected static int Rgb(double r, double g, double b)
        {
            Func<double, int> c = v => v <= 0 ? 0 : v >= 255 ? 255 : (int)v;
            return c(r) << 16 | c(g) << 8 | c(b);
        }

        protected static int Hue(double h)   // a full-strength color around the color wheel, h in turns
        {
            h -= Math.Floor(h); h *= 6;
            double f = h - Math.Floor(h);
            switch ((int)h)
            {
                case 0: return Rgb(255, 255 * f, 0);
                case 1: return Rgb(255 * (1 - f), 255, 0);
                case 2: return Rgb(0, 255, 255 * f);
                case 3: return Rgb(0, 255 * (1 - f), 255);
                case 4: return Rgb(255 * f, 0, 255);
                default: return Rgb(255, 0, 255 * (1 - f));
            }
        }
    }

    // Bouncing balls that fly from display to display, bounce off the outer edges and off each other
    class WallBalls : WallAnim
    {
        class Ball { public double X, Y, Vx, Vy, R; public int Color; }
        static readonly int[] Colors = { 0xFF3030, 0xFFD020, 0x20E0FF, 0xFF40E0, 0x40FF50 };
        readonly List<Ball> balls = new List<Ball>();
        readonly Random rng = new Random();

        public override void Step(double t)
        {
            if (balls.Count == 0)
                for (int k = 0; k < Colors.Length; k++)
                {
                    double a = rng.NextDouble() * 2 * Math.PI, v = 45 + rng.NextDouble() * 35;
                    balls.Add(new Ball { R = 9, Color = Colors[k], Vx = Math.Cos(a) * v, Vy = Math.Sin(a) * v,
                                         X = Left + 10 + rng.NextDouble() * Math.Max(1, Right - Left - 20), Y = Top + 10 + rng.NextDouble() * Math.Max(1, Bottom - Top - 20) });
                }
            foreach (var b in balls)
            {
                b.X += b.Vx * t; b.Y += b.Vy * t;
                if (b.X < Left + b.R) { b.X = Left + b.R; b.Vx = Math.Abs(b.Vx); }
                if (b.X > Right - b.R) { b.X = Right - b.R; b.Vx = -Math.Abs(b.Vx); }
                if (b.Y < Top + b.R) { b.Y = Top + b.R; b.Vy = Math.Abs(b.Vy); }
                if (b.Y > Bottom - b.R) { b.Y = Bottom - b.R; b.Vy = -Math.Abs(b.Vy); }
            }
            for (int i = 0; i < balls.Count; i++)   // equal balls trade their speed along the line between them
                for (int j = i + 1; j < balls.Count; j++)
                {
                    Ball a = balls[i], b = balls[j];
                    double dx = b.X - a.X, dy = b.Y - a.Y, d = Math.Sqrt(dx * dx + dy * dy), min = a.R + b.R;
                    if (d >= min || d < 1e-6) continue;
                    double nx = dx / d, ny = dy / d, rel = (a.Vx - b.Vx) * nx + (a.Vy - b.Vy) * ny;
                    if (rel > 0) { a.Vx -= rel * nx; a.Vy -= rel * ny; b.Vx += rel * nx; b.Vy += rel * ny; }
                    double push = (min - d) / 2; a.X -= nx * push; a.Y -= ny * push; b.X += nx * push; b.Y += ny * push;
                }
        }

        public override int ColorAt(double x, double y, double pixel)
        {
            foreach (var b in balls)
            {
                double dx = x - b.X, dy = y - b.Y, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > b.R + pixel / 2) continue;
                double edge = Math.Min(1, (b.R - d) / pixel + 0.5);
                double hx = x - (b.X - b.R * 0.35), hy = y - (b.Y - b.R * 0.35);   // lit from the upper left
                double light = 1.15 - 0.55 * Math.Min(1, Math.Sqrt(hx * hx + hy * hy) / (b.R * 1.4));
                double shine = Math.Max(0, 1 - Math.Sqrt(hx * hx + hy * hy) / (b.R * 0.35)) * 120;
                return Rgb(((b.Color >> 16 & 255) * light + shine) * edge, ((b.Color >> 8 & 255) * light + shine) * edge, ((b.Color & 255) * light + shine) * edge);
            }
            return 0;
        }
    }

    // A comet looping through all the displays on a long winding path, its glowing tail fading behind it
    class WallComet : WallAnim
    {
        readonly List<double[]> tail = new List<double[]>();   // x, y, age in seconds
        double t;

        public override void Step(double s)
        {
            t += s;
            double cx = (Left + Right) / 2, cy = (Top + Bottom) / 2, ax = Math.Max(5, (Right - Left) / 2 - 8), ay = Math.Max(5, (Bottom - Top) / 2 - 8);
            double x = cx + ax * Math.Sin(t * 0.53), y = cy + ay * Math.Sin(t * 0.91 + 1.1);
            foreach (var p in tail) p[2] += s;
            tail.Insert(0, new[] { x, y, 0.0 });
            tail.RemoveAll(p => p[2] > 1.3);
        }

        public override int ColorAt(double x, double y, double pixel)
        {
            double best = 0; double bestAge = 0;
            foreach (var p in tail)
            {
                double r = 7 * (1 - p[2] / 1.3) + pixel * 0.4;   // the tail narrows as it fades
                double dx = x - p[0], dy = y - p[1], d = Math.Sqrt(dx * dx + dy * dy);
                if (d > r + pixel) continue;
                double glow = Math.Min(1, (r + pixel / 2 - d) / pixel) * (1 - p[2] / 1.3);
                if (glow > best) { best = glow; bestAge = p[2]; }
            }
            if (best <= 0) return 0;
            double f = bestAge / 1.3;   // white head, then yellow, orange and a red fade
            return Rgb(255 * best, (255 - 200 * f) * best, (255 - 600 * f) * best);
        }
    }

    // Rainbow bands sweeping across all the displays as one picture, slowly turning (good for lining the layout up)
    class WallRainbow : WallAnim
    {
        double t;
        public override void Step(double s) { t += s; }
        public override int ColorAt(double x, double y, double pixel)
        {
            double a = t * 0.15, along = x * Math.Cos(a) + y * Math.Sin(a);
            return Hue(along / 120 - t * 0.25);
        }
    }

    // A worm that slithers from display to display, turning gently and bouncing off the outer edges
    class WallWorm : WallAnim
    {
        readonly List<double[]> body = new List<double[]>();
        readonly Random rng = new Random();
        double x = double.NaN, y, heading, turn, t;

        public override void Step(double s)
        {
            t += s;
            if (double.IsNaN(x)) { x = (Left + Right) / 2; y = (Top + Bottom) / 2; heading = rng.NextDouble() * 6.28; }
            if (rng.NextDouble() < s * 0.8) turn = (rng.NextDouble() - 0.5) * 2.4;
            heading += turn * s;
            x += Math.Cos(heading) * 55 * s; y += Math.Sin(heading) * 55 * s;
            if (x < Left + 5 || x > Right - 5) { heading = Math.PI - heading; x = Math.Max(Left + 5, Math.Min(Right - 5, x)); }
            if (y < Top + 5 || y > Bottom - 5) { heading = -heading; y = Math.Max(Top + 5, Math.Min(Bottom - 5, y)); }
            body.Insert(0, new[] { x, y });
            while (body.Count > 2 && Length() > 90) body.RemoveAt(body.Count - 1);
        }

        double Length() { double l = 0; for (int k = 1; k < body.Count; k++) l += Math.Sqrt(Sq(body[k][0] - body[k - 1][0]) + Sq(body[k][1] - body[k - 1][1])); return l; }
        static double Sq(double v) { return v * v; }

        public override int ColorAt(double px, double py, double pixel)
        {
            for (int k = 0; k < body.Count; k++)
            {
                double f = k / (double)body.Count, r = 8 - 4 * f;
                double d = Math.Sqrt(Sq(px - body[k][0]) + Sq(py - body[k][1]));
                if (d > r) continue;
                if (k < 2) return d < 2.5 && py < body[0][1] ? 0xFFFFFF : 0x70FF40;   // the head, with an eye
                int c = Hue(0.25 + f * 0.25 + t * 0.05);
                return ((k / 4) % 2 == 0) ? c : Rgb((c >> 16 & 255) * 0.75, (c >> 8 & 255) * 0.75, (c & 255) * 0.75);   // stripes
            }
            return 0;
        }
    }

    // Creepy eyeballs: a big eyeball on every display (and two little ones beside it on a 16 x 16 screen), each with its
    // own iris color, bloodshot at the edges. They all look slowly around together, as one, until now and then a few
    // of them glitch: a twitch and a jitter, and they stare the other way for a few seconds before snapping back.
    class WallEyes : WallAnim
    {
        class Eye { public double X, Y, R, Lx, Ly, Jx, Jy, GlitchFrom = -1, GlitchTo = -1, Slice, Seed; public int Iris; }
        static readonly int[] Irises = { 0x38D84A, 0x3C8CFF, 0xE0A020, 0xB050FF, 0xE02828, 0x20D8C8, 0xF0E040, 0xFF60B0 };
        readonly List<Eye> eyes = new List<Eye>();
        readonly Random rng = new Random();
        string layout = "";
        double t, lookX, lookY, wantX, wantY, nextLook, nextGlitch = 4;
        const double Twitch = 0.35;   // seconds of jitter as an eye glitches away and as it snaps back

        // the eyes sit on the screens, so none is lost in a gap; made again when the displays are moved
        void Place()
        {
            string key = "";
            foreach (var s in Screens) key += s.X + "," + s.Y + "," + s.Size + "," + s.N + ";";
            if (key == layout) return;
            layout = key; eyes.Clear();
            int color = rng.Next(Irises.Length);
            foreach (var s in Screens)
            {
                double c = s.Size / 2;
                if (s.N == 10) { eyes.Add(NewEye(s.X + c, s.Y + c, s.Size * 0.42, ref color)); continue; }
                bool left = rng.Next(2) == 0;   // the big eye to one side, the little ones stacked on the other
                eyes.Add(NewEye(s.X + s.Size * (left ? 0.38 : 0.62), s.Y + c, s.Size * 0.33, ref color));
                double sx = s.X + s.Size * (left ? 0.84 : 0.16);
                eyes.Add(NewEye(sx, s.Y + s.Size * 0.24, s.Size * 0.15, ref color));
                eyes.Add(NewEye(sx, s.Y + s.Size * 0.74, s.Size * 0.15, ref color));
            }
        }

        Eye NewEye(double x, double y, double r, ref int color)
        {
            color = (color + 1 + rng.Next(2)) % Irises.Length;
            return new Eye { X = x, Y = y, R = r, Iris = Irises[color], Seed = rng.NextDouble() * 100 };
        }

        public override void Step(double dt)
        {
            Place();
            t += dt;
            // where they all look: somewhere new every two to four seconds, sometimes straight ahead, eased slowly there
            if ((nextLook -= dt) <= 0)
            {
                nextLook = 2 + rng.NextDouble() * 2;
                double a = rng.NextDouble() * 2 * Math.PI, m = rng.Next(5) == 0 ? 0 : 0.6 + rng.NextDouble() * 0.4;
                wantX = Math.Cos(a) * m; wantY = Math.Sin(a) * m * 0.8;
            }
            double ease = 1 - Math.Exp(-dt * 1.6);
            lookX += (wantX - lookX) * ease; lookY += (wantY - lookY) * ease;
            // now and then a few of them glitch the other way
            if ((nextGlitch -= dt) <= 0 && eyes.Count > 0)
            {
                nextGlitch = 4 + rng.NextDouble() * 5;
                int k = 1 + rng.Next(Math.Max(1, eyes.Count / 3));
                for (int i = 0; i < k; i++)
                {
                    var e = eyes[rng.Next(eyes.Count)];
                    if (e.GlitchTo > t) continue;
                    e.GlitchFrom = t + rng.NextDouble() * 0.3; e.GlitchTo = e.GlitchFrom + 1.8 + rng.NextDouble() * 2.2;
                }
            }
            foreach (var e in eyes)
            {
                bool off = t >= e.GlitchFrom && t < e.GlitchTo;
                double x = off ? -lookX : lookX, y = off ? -lookY : lookY;
                if (Twitching(e))
                {   // jitter: a fresh random jolt each step, and the picture tears in slices
                    e.Jx = (rng.NextDouble() - 0.5) * 0.9; e.Jy = (rng.NextDouble() - 0.5) * 0.9;
                    e.Slice = rng.NextDouble() * 1000;
                    e.Lx = x + e.Jx; e.Ly = y + e.Jy;
                }
                else if (off) { e.Lx = x; e.Ly = y; }                  // glitched: snapped to the opposite way
                else { e.Lx += (x - e.Lx) * Math.Min(1, ease * 6); e.Ly += (y - e.Ly) * Math.Min(1, ease * 6); }
            }
        }

        bool Twitching(Eye e)
        {
            return (t >= e.GlitchFrom && t < e.GlitchFrom + Twitch) || (t >= e.GlitchTo && t < e.GlitchTo + Twitch * 0.7);
        }

        public override int ColorAt(double x, double y, double pixel)
        {
            foreach (var e in eyes)
            {
                double dx = x - e.X, dy = y - e.Y;
                if (dx * dx + dy * dy > (e.R + pixel) * (e.R + pixel)) continue;
                bool twitch = Twitching(e);
                if (twitch)   // torn: each band of rows shifts sideways by a pixel or two
                {
                    int band = (int)Math.Floor((y - e.Y) / (pixel * 1.5));
                    dx += ((Hash(band, (int)e.Slice) % 5) - 2) * pixel * 0.8;
                }
                int c = EyeAt(e, dx, dy, pixel, twitch);
                if (twitch && Hash((int)e.Slice, 7) % 3 == 0) c = (c & 0xFF0000) | (EyeAt(e, dx + pixel, dy, pixel, true) & 0x00FFFF);   // colors split
                return c;
            }
            // the dark between them, a deep red glow
            double v = 0.5 + 0.5 * Math.Sin(x * 0.02 + t * 0.3) * Math.Sin(y * 0.03 - t * 0.2);
            return Rgb(10 + 14 * v, 0, 2 + 3 * v);
        }

        int EyeAt(Eye e, double dx, double dy, double pixel, bool twitch)
        {
            double d = Math.Sqrt(dx * dx + dy * dy), R = e.R;
            if (d > R + pixel / 2) return Rgb(10, 0, 2);
            double edge = Math.Min(1, (R - d) / pixel + 0.5);
            // the white, shaded toward its edge, with red veins creeping in from the rim
            double shade = 1 - 0.55 * Math.Pow(d / R, 2.2);
            double a = Math.Atan2(dy, dx);
            double vein = d > R * 0.5 ? Math.Max(0, Math.Sin(a * 9 + Math.Sin(d / R * 7 + e.Seed) * 1.6 + e.Seed) - 0.86) / 0.14 * (d / R - 0.5) * 2 : 0;
            double r = (228 - 30 * vein) * shade, g = (224 - 190 * vein) * shade, b = (220 - 185 * vein) * shade;
            // the iris and pupil, moved toward where the eye looks
            double reach = R * 0.42, ix = e.X + e.Lx * reach, iy = e.Y + e.Ly * reach;
            double ox = (e.X + dx) - ix, oy = (e.Y + dy) - iy, id = Math.Sqrt(ox * ox + oy * oy), Ri = R * 0.52;
            double pupil = Ri * (0.42 + 0.08 * Math.Sin(t * 0.7 + e.Seed) + (twitch ? 0.15 : 0));
            if (id < Ri + pixel / 2)
            {
                int col = twitch ? Irises[(Hash((int)e.Slice, 3) % Irises.Length)] : e.Iris;
                double streak = 0.85 + 0.25 * Math.Sin(Math.Atan2(oy, ox) * 11 + e.Seed);
                double ring = id > Ri * 0.82 ? 0.5 : 1;   // a dark ring around the iris
                double k = id < pupil ? 0 : streak * ring * (0.75 + 0.25 * shade);
                double w = Math.Min(1, (Ri - id) / pixel + 0.5);
                r = r * (1 - w) + (col >> 16 & 255) * k * w; g = g * (1 - w) + (col >> 8 & 255) * k * w; b = b * (1 - w) + (col & 255) * k * w;
            }
            // a wet glint, up and to the left
            double hx = dx + R * 0.32, hy = dy + R * 0.36, hd = Math.Sqrt(hx * hx + hy * hy);
            if (hd < R * 0.13 + pixel * 0.3) { double s = Math.Min(1, (R * 0.13 + pixel * 0.3 - hd) / pixel); r += (255 - r) * s * 0.8; g += (255 - g) * s * 0.8; b += (255 - b) * s * 0.8; }
            return Rgb(r * edge + 10 * (1 - edge), g * edge, b * edge + 2 * (1 - edge));
        }

        static int Hash(int x, int y) { unchecked { int h = x * 374761393 + y * 668265263; h = (h ^ (h >> 13)) * 1274126177; return (h ^ (h >> 16)) & 0x7FFFFFFF; } }
    }

    // Lava clock across all the displays: one lava lamp whose blobs drift from display to display, and at every new
    // minute the time bubbles up out of the lava (the hours on the first display, the minutes on the next, in reading
    // order; on a single display hours over minutes), holds so it can be read, then melts and drips away. Every minute
    // brings a new color theme, seeping in over the end of the minute before.
    class WallLavaClock : WallAnim
    {
        const double FormSeconds = 4, HoldSeconds = 8, MeltSeconds = 4;
        // core, body, glow; chosen so the AuraBox's 8 colors come out as the Lava Clock's own themes there
        static readonly int[][] Themes = {
            new[] { 0xFFE040, 0xFF4A10, 0xB01478 },   // classic: yellow, red-orange, magenta glow
            new[] { 0xF0FFFF, 0x10D0F0, 0x1030C0 },   // ocean: white, cyan, blue
            new[] { 0xF0F040, 0x30E030, 0x10A0A0 },   // toxic: yellow, green, teal
            new[] { 0xFFE8FF, 0xF030D0, 0x3020C0 },   // berry: white, magenta, blue
            new[] { 0xFFF8E0, 0xFF3010, 0xE0A010 },   // ember: white, red, amber
        };
        class Blob { public double Ax, Ay, Sx, Sy, P, R; }
        class Drop { public int X, Y; public double Delay, Speed; }
        class Face { public WallScreen S; public bool[] Lit; public List<Drop> Drops = new List<Drop>(); public double[] Wobble; }

        readonly bool h24;
        readonly Random rng = new Random();
        readonly List<Blob> blobs = new List<Blob>();
        readonly List<Face> faces = new List<Face>();
        string layout = "";
        int lastMinute = -1, theme, nextTheme;
        double t, since, mix;   // since: seconds into this minute's show; mix: how far the next theme has seeped in

        public WallLavaClock(bool h24) { this.h24 = h24; theme = rng.Next(Themes.Length); nextTheme = Other(theme); }

        int Other(int th) { return (th + 1 + rng.Next(Themes.Length - 1)) % Themes.Length; }

        public override void Step(double dt)
        {
            t += dt; since += dt;
            string key = "";
            foreach (var s in Screens) key += s.X + "," + s.Y + "," + s.Size + "," + s.N + ";";
            if (key != layout) { layout = key; MakeBlobs(); lastMinute = -1; }
            var now = DateTime.Now;
            int minute = now.Hour * 60 + now.Minute;
            if (minute != lastMinute)
            {
                lastMinute = minute; since = 0;
                theme = nextTheme; nextTheme = Other(theme);   // the one that seeped in over the end of the last minute
                MakeFaces(now);
            }
            double toNext = 60 - now.Second - now.Millisecond / 1000.0;
            mix = Math.Max(0, Math.Min(1, (24 - toNext) / 24));   // the next theme seeps in over the last 24 seconds
        }

        // about two blobs per display, each wandering the whole area on its own slow loop
        void MakeBlobs()
        {
            blobs.Clear();
            double w = Right - Left, h = Bottom - Top, size = 0;
            foreach (var s in Screens) size += s.Size / Screens.Count;
            for (int k = 0; k < Math.Max(3, Screens.Count * 2); k++)
                blobs.Add(new Blob { Ax = w / 2, Ay = h / 2, Sx = 0.12 + rng.NextDouble() * 0.12, Sy = 0.1 + rng.NextDouble() * 0.12, P = rng.NextDouble() * 20,
                                     R = size * (0.19 + rng.NextDouble() * 0.08) });
        }

        // which display shows what: reading order (rows of displays top to bottom, each row left to right)
        void MakeFaces(DateTime now)
        {
            faces.Clear();
            var order = new List<WallScreen>(Screens);
            double size = 0;
            foreach (var s in order) size += s.Size / order.Count;
            order.Sort((a, b) =>
            {
                int ra = (int)Math.Floor((a.Y + a.Size / 2 - Top) / size), rb = (int)Math.Floor((b.Y + b.Size / 2 - Top) / size);
                return ra != rb ? ra.CompareTo(rb) : a.X.CompareTo(b.X);
            });
            int h = h24 ? now.Hour : (now.Hour % 12 == 0 ? 12 : now.Hour % 12);
            string hours = h24 ? h.ToString("00") : h.ToString(), minutes = now.Minute.ToString("00");
            for (int i = 0; i < order.Count && i < 2; i++)
            {
                var s = order[i]; int n = s.N;
                var f = new Face { S = s, Lit = new bool[n * n], Wobble = new double[n] };
                if (order.Count == 1)
                {   // one display: hours over minutes, as on the Lava Clock
                    var px = n == 16 ? ClockArt.Face16(now, h24, 1, 1, false, false) : ClockArt.Face(now, h24, 1, 1);
                    for (int k = 0; k < px.Length; k++) f.Lit[k] = px[k] != 0;
                }
                else Digits(f.Lit, n, i == 0 ? hours : minutes);
                for (int x = 0; x < n; x++) f.Wobble[x] = rng.NextDouble() * 1.6;
                for (int k = 0; k < n * n; k++)
                    if (f.Lit[k]) f.Drops.Add(new Drop { X = k % n, Y = k / n, Delay = rng.NextDouble() * 1.4 + (n - k / n) * 0.04, Speed = 5 + rng.NextDouble() * 5 });
                faces.Add(f);
            }
        }

        // a number in big digits, centered: 5 x 7 on 16 x 16, 3 x 5 on 10 x 10
        static void Digits(bool[] lit, int n, string text)
        {
            bool big = n == 16;
            int w = big ? 5 : 3, h = big ? 7 : 5, gap = 1, x0 = (n - (text.Length * (w + gap) - gap)) / 2, y0 = (n - h) / 2;
            foreach (char ch in text)
            {
                string g = big ? ClockArt.Big[ch - '0'] : WeatherArt.Font[ch];
                for (int k = 0; k < w * h; k++)
                    if (g[k] == (big ? '#' : '1')) lit[(y0 + k / w) * n + x0 + k % w] = true;
                x0 += w + gap;
            }
        }

        // the theme's colors now, partway to the next one as the minute ends
        int Color(int which) { return Blend(Themes[theme][which], Themes[nextTheme][which], mix); }

        static int Blend(int a, int b, double m)
        {
            return Rgb((a >> 16 & 255) * (1 - m) + (b >> 16 & 255) * m, (a >> 8 & 255) * (1 - m) + (b >> 8 & 255) * m, (a & 255) * (1 - m) + (b & 255) * m);
        }

        public override int ColorAt(double x, double y, double pixel)
        {
            foreach (var f in faces)
            {
                var s = f.S;
                if (x < s.X || y < s.Y || x >= s.X + s.Size || y >= s.Y + s.Size) continue;
                int n = s.N, px = (int)((x - s.X) / s.Size * n), py = (int)((y - s.Y) / s.Size * n);
                int c = DigitAt(f, Math.Min(n - 1, px), Math.Min(n - 1, py));
                if (c >= 0) return c;
                double dim = since < FormSeconds ? 1 - 0.6 * since / FormSeconds : since < FormSeconds + HoldSeconds ? 0.4
                           : since < FormSeconds + HoldSeconds + MeltSeconds ? 0.4 + 0.6 * (since - FormSeconds - HoldSeconds) / MeltSeconds : 1;
                return Lava(x, y, dim);   // the lava dims behind the digits so they can be read
            }
            return Lava(x, y, 1);
        }

        int Lava(double x, double y, double dim)
        {
            double f = 0, w = Right - Left, h = Bottom - Top;
            foreach (var b in blobs)
            {
                double bx = Left + w / 2 + (w / 2 - b.R * 0.5) * Math.Sin(t * b.Sx + b.P), by = Top + h / 2 + (h / 2 - b.R * 0.3) * Math.Sin(t * b.Sy + b.P * 1.3);
                double dx = x - bx, dy = y - by;
                f += b.R * b.R / (dx * dx + dy * dy + b.R * b.R * 0.06);
            }
            int c = f > 2.6 ? Color(0) : f > 1.2 ? Color(1) : f > 0.8 ? Color(2) : 0;
            double edge = f > 0.8 ? Math.Min(1, (f - 0.8) * 4) : 0;   // soft rim
            return Rgb((c >> 16 & 255) * dim * edge, (c >> 8 & 255) * dim * edge, (c & 255) * dim * edge);
        }

        // the digits: rising out of the lava (a fizzing crest climbs up from the bottom), held, then dripping away
        int DigitAt(Face f, int x, int y)
        {
            int n = f.S.N, at = y * n + x, core = Color(0), body = Color(1);
            if (since < FormSeconds)
            {
                double edge = n + 0.5 - since / FormSeconds * (n + 2.5) + f.Wobble[x];
                if (y >= edge) return f.Lit[at] ? core : -1;
                if (y >= edge - 1 && Hash(x, (int)(t * 12)) % 3 == 0) return Hash(x + 7, (int)(t * 12)) % 2 == 0 ? 0xFFFFFF : body;   // fizz
                return -1;
            }
            if (since < FormSeconds + HoldSeconds) return f.Lit[at] ? core : -1;
            double m = since - FormSeconds - HoldSeconds;
            if (m >= MeltSeconds + 2) return -1;
            foreach (var d in f.Drops)
            {
                if (d.X != x) continue;
                double fall = Math.Max(0, m - d.Delay), dy = fall * d.Speed + fall * fall * 3;
                double head = d.Y + dy;
                if (y == (int)head) return core;
                if (y < head && y >= head - 2 && dy > 0.5) return y >= head - 1 ? body : Color(2);   // the glowing trail
            }
            return -1;
        }

        static int Hash(int x, int y) { unchecked { int h = x * 374761393 + y * 668265263; h = (h ^ (h >> 13)) * 1274126177; return (h ^ (h >> 16)) & 0x7FFFFFFF; } }
    }

    // Jellyfish across all the displays (and, on one screen, the Jellyfish animation's 16 x 16 picture): a dark tank,
    // its light drifting slowly between deep blue and violet, with jellyfish that glow. Each bell squeezes narrow and
    // tall to push itself along the way it is tilted, then opens wide and glides, sinking a little; it tilts toward
    // where it is heading, so it climbs and swims sideways in pulses. Its tentacles are chains pulled through the
    // water, so they trail behind each push, swing out on turns and ripple. A moon jelly with its four pink rings, an
    // orange sea nettle, a blue jelly and a pink one; specks of marine snow drift by. Everything is in millimeters.
    class WallJellyfish : WallAnim
    {
        class Strand { public double[] X, Y, Px, Py; public double At, Seg, Seed, Width; public bool Arm; }
        class Jelly
        {
            public double X, Y, Vx, Vy, Tilt, Phase, Period, R, H, Depth, Squeeze, Tx, Ty, Wait;
            public int Bell, Rim, Arm; public bool Rings;
            public readonly List<Strand> Strands = new List<Strand>();
            public double BoxL, BoxT, BoxR, BoxB;   // around the bell, its glow and its tentacles
        }
        class Speck { public double X, Y, V, Seed, A; }

        // bell, rim glow, mouth arms / tentacles; rings: the moon jelly's four pink rings
        static readonly int[][] Kinds = {
            new[] { 0x9C8CFF, 0xFFFFFF, 0xFF4CC0, 1 },   // moon jelly
            new[] { 0xFF7A10, 0xFFE4A0, 0xFF2C10, 0 },   // sea nettle
            new[] { 0x2C78FF, 0xB4F4FF, 0x30D8FF, 0 },   // blue
            new[] { 0xFF40B0, 0xFFE0F4, 0xC040FF, 0 },   // pink
        };
        // sizes in millimeters (a 16 x 16 screen is about 70 mm across): bell radius and height, tentacle length; nearness
        static readonly double[][] Sizes = { new[] { 11.0, 8.0, 34.0, 1.0 }, new[] { 8.5, 6.4, 28.0, 0.85 }, new[] { 6.2, 4.6, 20.0, 0.65 } };

        readonly Random rng = new Random();
        readonly List<Jelly> jellies = new List<Jelly>();
        List<Jelly> order = new List<Jelly>();
        readonly List<Speck> snow = new List<Speck>();
        string layout = "";
        double t, spare;
        const double Tick = 0.02;   // the water is worked out in steps of a fiftieth of a second

        void Populate()
        {
            string key = "";
            foreach (var s in Screens) key += s.X + "," + s.Y + "," + s.Size + ";";
            if (key == layout) return;
            layout = key; jellies.Clear(); snow.Clear();
            int count = Screens.Count == 1 ? 2 : (int)Math.Round(Screens.Count * 1.6), kind = rng.Next(Kinds.Length);
            for (int k = 0; k < count; k++)
            {
                // one screen: a big one and a small one further back
                var sz = Sizes[Screens.Count == 1 ? k * 2 % Sizes.Length : k % Sizes.Length]; var c = Kinds[kind]; kind = (kind + 1) % Kinds.Length;
                var j = new Jelly { R = sz[0], H = sz[1], Depth = sz[3], Bell = c[0], Rim = c[1], Arm = c[2], Rings = c[3] == 1,
                                    Period = 2.6 + rng.NextDouble() * 1.2, Phase = rng.NextDouble() };
                // spread out over the screens to begin with, two on one screen on opposite sides
                var s = Screens[k % Screens.Count];
                j.X = s.X + s.Size * ((k / Screens.Count) % 2 == 0 ? 0.3 : 0.72) + (rng.NextDouble() - 0.5) * 6; j.Y = s.Y + s.Size * (0.25 + 0.35 * rng.NextDouble());
                j.Tx = j.X; j.Ty = j.Y; j.Wait = rng.NextDouble() * 6;
                double len = sz[2];
                foreach (double at in new[] { -0.85, 0.85 })   // fine tentacles from the edges of the rim
                    j.Strands.Add(NewStrand(j, at, len * (0.85 + 0.3 * rng.NextDouble()), 10, false, 0.5));
                j.Strands.Add(NewStrand(j, 0, len * 0.55, 6, true, 1.4));   // the frilly mouth arms in the middle, shorter
                jellies.Add(j);
            }
            foreach (var s in Screens)
                for (int k = 0; k < 8; k++)
                    snow.Add(new Speck { X = s.X + rng.NextDouble() * s.Size, Y = s.Y + rng.NextDouble() * s.Size, V = 0.6 + rng.NextDouble() * 1.4, Seed = rng.NextDouble() * 6, A = 0.3 + rng.NextDouble() * 0.5 });
        }

        Strand NewStrand(Jelly j, double at, double len, int n, bool arm, double width)
        {
            var s = new Strand { At = at, Seg = len / (n - 1), Seed = rng.NextDouble() * 6, Arm = arm, Width = width, X = new double[n], Y = new double[n], Px = new double[n], Py = new double[n] };
            for (int i = 0; i < n; i++) { s.X[i] = s.Px[i] = j.X + at * j.R; s.Y[i] = s.Py[i] = j.Y + i * s.Seg; }
            return s;
        }

        public override void Step(double dt)
        {
            Populate();
            spare += dt;
            while (spare >= Tick) { spare -= Tick; t += Tick; foreach (var j in jellies) Swim(j); }
            foreach (var p in snow)
            {
                p.Y -= p.V * dt * 0.4; p.X += Math.Sin(t * 0.4 + p.Seed) * dt * 0.8;
                if (p.Y < Top - 2) p.Y = Bottom + 2;
            }
            foreach (var j in jellies) Bounds(j);
            order = new List<Jelly>(jellies);
            order.Sort((a, b) => a.Depth.CompareTo(b.Depth));   // far ones first
        }

        double Rw(Jelly j) { return j.R * (1 - 0.22 * j.Squeeze); }   // squeezed: narrower...
        double Rh(Jelly j) { return j.H * (1 + 0.26 * j.Squeeze); }   // ...and taller

        void Swim(Jelly j)
        {
            double h = Tick;
            j.Phase += h / j.Period;
            double c = j.Phase - Math.Floor(j.Phase);
            j.Squeeze = c < 0.3 ? Math.Sin(Math.PI * c / 0.3) : 0;
            // the smaller of two that come close picks somewhere new at once (the other keeps going)
            foreach (var o in jellies)
                if (o != j && Math.Abs(o.X - j.X) < o.R + j.R + 6 && Math.Abs(o.Y - j.Y) < 24 && j.Wait > 1.5
                    && (o.Depth > j.Depth || (o.Depth == j.Depth && jellies.IndexOf(o) < jellies.IndexOf(j))))
                    j.Wait = 0;
            // somewhere new to head for every so often: the best of a few spots, the one farthest from the others
            if ((j.Wait -= h) <= 0)
            {
                j.Wait = 7 + rng.NextDouble() * 9;
                double best = -1, lo = Top + j.H * 1.5 + 4, span = Math.Max(1, Bottom - Top - j.H * 1.5 - 4 - Sizes[0][2] * 0.6);
                for (int k = 0; k < 6; k++)
                {
                    double x = Left + j.R + 2 + rng.NextDouble() * Math.Max(1, Right - Left - 2 * j.R - 4), y = lo + rng.NextDouble() * span, room = 1e9;
                    foreach (var o in jellies)
                        if (o != j) room = Math.Min(room, Math.Abs(o.X - x) / (o.R + j.R) + Math.Abs(o.Y - y) / 30);
                    if (room > best) { best = room; j.Tx = x; j.Ty = y; }
                }
            }
            j.Tx = Math.Max(Left + j.R + 2, Math.Min(Right - j.R - 2, j.Tx));
            // it leans toward where it is going, and pushes along its lean; harder when below where it wants to be
            double lean = Math.Max(-0.5, Math.Min(0.5, (j.Tx - j.X) * 0.04));
            j.Tilt += (lean - j.Tilt) * h * 0.9;
            double push = Math.Max(0.15, Math.Min(1.8, 0.75 + (j.Y - j.Ty) * 0.05)) * j.Squeeze * 17;
            j.Vx += Math.Sin(j.Tilt) * push * h;
            j.Vy += (-Math.Cos(j.Tilt) * push + 2.7) * h;   // and sinks a little all the time
            double drag = Math.Exp(-1.25 * h);
            j.Vx *= drag; j.Vy *= drag;
            j.X += j.Vx * h; j.Y += j.Vy * h;
            if (j.X < Left + j.R) { j.X = Left + j.R; j.Vx = Math.Abs(j.Vx) * 0.3; }
            if (j.X > Right - j.R) { j.X = Right - j.R; j.Vx = -Math.Abs(j.Vx) * 0.3; }
            if (j.Y < Top + j.H * 1.3) { j.Y = Top + j.H * 1.3; j.Vy = Math.Abs(j.Vy) * 0.3; }
            if (j.Y > Bottom - 6) { j.Y = Bottom - 6; j.Vy = -Math.Abs(j.Vy) * 0.3; }
            // the tentacles: the top of each is held at the rim, the rest swing, pulled down a little, slowed by the
            // water and rippled, each link kept its length from the one above
            double cs = Math.Cos(j.Tilt), sn = Math.Sin(j.Tilt), rw = Rw(j);
            foreach (var s in j.Strands)
            {
                int n = s.X.Length;
                s.X[0] = j.X + s.At * rw * cs; s.Y[0] = j.Y + s.At * rw * sn + 0.5;
                for (int i = 1; i < n; i++)
                {
                    double vx = (s.X[i] - s.Px[i]) * 0.9, vy = (s.Y[i] - s.Py[i]) * 0.9;
                    s.Px[i] = s.X[i]; s.Py[i] = s.Y[i];
                    double wave = Math.Sin(t * 2.4 - i * 0.7 + s.Seed) * (s.Arm ? 9 : 6) * i / n;
                    s.X[i] += vx + wave * h * h * 4; s.Y[i] += vy + 30 * h * h;
                }
                for (int i = 1; i < n; i++)
                {
                    double dx = s.X[i] - s.X[i - 1], dy = s.Y[i] - s.Y[i - 1], d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-6) { s.Y[i] = s.Y[i - 1] + s.Seg; continue; }
                    double k = s.Seg / d;
                    s.X[i] = s.X[i - 1] + dx * k; s.Y[i] = s.Y[i - 1] + dy * k;
                }
            }
        }

        void Bounds(Jelly j)
        {
            double l = j.X - j.R * 1.7, r = j.X + j.R * 1.7, top = j.Y - j.H * 1.9, b = j.Y + j.R * 0.6;
            foreach (var s in j.Strands)
                for (int i = 0; i < s.X.Length; i++) { l = Math.Min(l, s.X[i] - 3); r = Math.Max(r, s.X[i] + 3); b = Math.Max(b, s.Y[i] + 3); }
            j.BoxL = l; j.BoxR = r; j.BoxT = top; j.BoxB = b;
        }

        static int Mix(int a, int b, double m)
        {
            m = Math.Max(0, Math.Min(1, m));
            return Rgb((a >> 16 & 255) * (1 - m) + (b >> 16 & 255) * m, (a >> 8 & 255) * (1 - m) + (b >> 8 & 255) * m, (a & 255) * (1 - m) + (b & 255) * m);
        }

        public override int ColorAt(double x, double y, double pixel)
        {
            // the water: dark, a little lighter at the top, with faint shafts of light moving through it
            double depth = Math.Max(0, Math.Min(1, (y - Top) / Math.Max(1, Bottom - Top)));
            // (nearly black: on LEDs black is off, and the jellyfish need all the contrast they can get)
            int top = Mix(0x061434, 0x10082C, (Math.Sin(t * 0.06) + 1) / 2);
            int water = Mix(top, 0x000000, Math.Pow(depth, 0.6));
            double ray = Math.Pow(Math.Max(0, Math.Sin(x * 0.07 + y * 0.025 - t * 0.25) * Math.Sin(x * 0.031 - y * 0.012 + t * 0.17)), 2) * 10 * (1 - depth);
            double r = (water >> 16 & 255) + ray * 0.5, g = (water >> 8 & 255) + ray * 0.7, b = (water & 255) + ray;
            foreach (var p in snow)
            {
                double dx = x - p.X, dy = y - p.Y;
                if (dx * dx + dy * dy < pixel * pixel * 0.3) { r += 40 * p.A; g += 48 * p.A; b += 60 * p.A; }
            }
            foreach (var j in order)
            {
                if (x < j.BoxL || x > j.BoxR || y < j.BoxT || y > j.BoxB) continue;
                double fade = 0.8 + 0.2 * j.Depth;
                // tentacles first: the bell is in front of where they join it
                foreach (var s in j.Strands)
                {
                    int n = s.X.Length; double best = 1e9; int at = 0;
                    for (int i = 1; i < n; i++)
                    {
                        double d = SegDist(x, y, s.X[i - 1], s.Y[i - 1], s.X[i], s.Y[i]);
                        if (d < best) { best = d; at = i; }
                    }
                    double reach = pixel * 0.35 + s.Width;
                    if (best >= reach) continue;
                    double a = Math.Min(1, (1 - best / reach) * 1.4) * (s.Arm ? 0.95 : 0.85) * (1 - 0.6 * (at - 1) / (double)(n - 1)) * fade;
                    int col = s.Arm ? Mix(j.Arm, j.Rim, 0.3) : Mix(j.Rim, j.Arm, 0.35);
                    r = r * (1 - a) + (col >> 16 & 255) * a; g = g * (1 - a) + (col >> 8 & 255) * a; b = b * (1 - a) + (col & 255) * a;
                }
                // the bell, in its own tilted frame: u across, v up from the rim
                double rw = Rw(j), rh = Rh(j), cs = Math.Cos(j.Tilt), sn = Math.Sin(j.Tilt);
                double ox = x - j.X, oy = y - j.Y, lx = ox * cs + oy * sn, ly = -ox * sn + oy * cs;
                double u = lx / rw, v = -ly / rh, e = Math.Sqrt(u * u + v * v);
                if (e > 1 && e < 1.7 && v > -0.4)   // its glow in the water around it
                {
                    double gl = 0.22 * (1.7 - e) / 0.7 * fade;
                    r += (j.Rim >> 16 & 255) * gl; g += (j.Rim >> 8 & 255) * gl; b += (j.Rim & 255) * gl;
                }
                double edge = Math.Max(0, Math.Min(1, (1 - e) * Math.Min(rw, rh) / pixel + 0.5)) * Math.Max(0, Math.Min(1, (v + 0.12) * rh / pixel + 0.5));
                if (edge <= 0) continue;
                int c = Mix(j.Bell, j.Rim, e * e * e);
                if (j.Rings)
                    c = Mix(c, j.Arm, 0.85 * Math.Exp(-((Math.Abs(u) - 0.4) * (Math.Abs(u) - 0.4) + (v - 0.42) * (v - 0.42)) / 0.025));
                if (v > 0.55 && Math.Abs(u) < 0.5) c = Mix(c, 0xFFFFFF, 0.3 * j.Squeeze);   // the crown catches the light as it squeezes
                double alpha = Math.Min(1, (0.9 + 0.2 * Math.Pow(e, 3)) * edge * fade);
                if (v < 0.22) c = Mix(c, j.Rim, 0.35 * (0.5 + 0.5 * Math.Sin(u * 17 + t * 5)));   // the frilly rim ripples
                r = r * (1 - alpha) + (c >> 16 & 255) * alpha; g = g * (1 - alpha) + (c >> 8 & 255) * alpha; b = b * (1 - alpha) + (c & 255) * alpha;
            }
            return Rgb(r, g, b);
        }

        static double SegDist(double x, double y, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l = dx * dx + dy * dy;
            double k = l <= 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / l));
            double px = ax + dx * k - x, py = ay + dy * k - y;
            return Math.Sqrt(px * px + py * py);
        }
    }

    // one display on the All Displays page: its name, where it stands (the settings' own numbers: left, top and width
    // in millimeters) and what it shows
    class WallTile { public string Name, Mode; public double[] Place; public int[] Image; public bool Connected, Out; public object Key; }

    // the All Displays layout: every display drawn where it stands, showing what it shows now. Drag a display to move
    // it; drag its lower-right corner to set its screen's size; the box by its name takes it out of (or back into) the
    // animations across all displays. The view fits itself around them between drags.
    class WallView : Control
    {
        public Func<List<WallTile>> Tiles;
        public event Action Moved;   // a drag ended
        public event Action<WallTile> Toggled;   // its box was clicked: in or out of the animations across all displays
        double scale = 1.5, ox, oy;  // pixels per millimeter, and the millimeters at the top-left corner
        WallTile dragging; bool resizing; Point downAt; double[] startPlace;

        public WallView() { DoubleBuffered = true; ResizeRedraw = true; }

        void Fit(List<WallTile> tiles)
        {
            if (dragging != null || tiles.Count == 0) return;
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var w in tiles) { l = Math.Min(l, w.Place[0]); t = Math.Min(t, w.Place[1]); r = Math.Max(r, w.Place[0] + w.Place[2]); b = Math.Max(b, w.Place[1] + w.Place[2]); }
            double margin = 30, width = Math.Max(240, r - l + 2 * margin), height = Math.Max(120, b - t + 2 * margin + 12);
            scale = Math.Min(ClientSize.Width / width, ClientSize.Height / height);
            ox = (l + r) / 2 - ClientSize.Width / scale / 2; oy = (t + b) / 2 - (ClientSize.Height - 16) / scale / 2;
        }

        Rectangle Box(WallTile w) { var r = Screen(w); int s = Font.Height - 2; return new Rectangle((int)r.X - 4, (int)r.Bottom + 9, s, s); }

        RectangleF Screen(WallTile w) { return new RectangleF((float)((w.Place[0] - ox) * scale), (float)((w.Place[1] - oy) * scale), (float)(w.Place[2] * scale), (float)(w.Place[2] * scale)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(28, 28, 32));
            var tiles = Tiles == null ? new List<WallTile>() : Tiles();
            Fit(tiles);
            using (var dot = new SolidBrush(Color.FromArgb(55, 55, 62)))   // a dot every centimeter
                for (double y = Math.Ceiling(oy / 10) * 10; (y - oy) * scale < ClientSize.Height; y += 10)
                    for (double x = Math.Ceiling(ox / 10) * 10; (x - ox) * scale < ClientSize.Width; x += 10)
                        g.FillRectangle(dot, (float)((x - ox) * scale), (float)((y - oy) * scale), 1.5f, 1.5f);
            foreach (var w in tiles)
            {
                var r = Screen(w);
                using (var body = new SolidBrush(Color.FromArgb(60, 60, 66))) g.FillRectangle(body, RectangleF.Inflate(r, 5, 5));
                g.FillRectangle(Brushes.Black, r);
                var img = w.Image;
                if (img != null)
                {
                    int n = img.Length == 256 ? 16 : 10;
                    float c = r.Width / n;
                    for (int k = 0; k < n * n; k++)
                    {
                        var col = Proto.ColorAt(img, k);
                        if (col.R + col.G + col.B == 0) continue;
                        using (var b = new SolidBrush(col)) g.FillRectangle(b, r.X + k % n * c + 0.5f, r.Y + k / n * c + 0.5f, Math.Max(1, c - 1), Math.Max(1, c - 1));
                    }
                }
                else if (w.Mode != null)
                    TextRenderer.DrawText(g, w.Mode, Font, Rectangle.Round(r), Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                if (w.Out)
                {
                    using (var veil = new SolidBrush(Color.FromArgb(170, 28, 28, 32))) g.FillRectangle(veil, r);
                    TextRenderer.DrawText(g, "left out:" + Environment.NewLine + "own playlist", Font, Rectangle.Round(r), Color.Silver, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
                else if (!w.Connected)
                {
                    using (var veil = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(veil, r);
                    TextRenderer.DrawText(g, "not connected", Font, Rectangle.Round(r), Color.Silver, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                using (var grip = new SolidBrush(Color.FromArgb(140, 140, 150)))   // the size handle
                    g.FillPolygon(grip, new[] { new PointF(r.Right + 5, r.Bottom - 7), new PointF(r.Right + 5, r.Bottom + 5), new PointF(r.Right - 7, r.Bottom + 5) });
                var box = Box(w);   // in or out of the animations across all displays
                using (var pen = new Pen(Color.FromArgb(170, 170, 180))) g.DrawRectangle(pen, box);
                if (!w.Out)
                    using (var tick = new Pen(Color.FromArgb(235, 235, 240), 2))
                        g.DrawLines(tick, new[] { new PointF(box.X + 2.5f, box.Y + box.Height / 2f), new PointF(box.X + box.Width * 0.42f, box.Bottom - 3), new PointF(box.Right - 2.5f, box.Y + 3) });
                TextRenderer.DrawText(g, w.Name + "  ·  " + Math.Round(w.Place[2]) + " mm", Font, new Point(box.Right + 4, (int)r.Bottom + 8), w.Out ? Color.Gray : Color.Silver);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (Tiles == null) return;
            var tiles = Tiles();
            foreach (var w in tiles)
                if (Rectangle.Inflate(Box(w), 3, 3).Contains(e.Location)) { if (Toggled != null) Toggled(w); Invalidate(); return; }
            for (int k = tiles.Count - 1; k >= 0; k--)
            {
                var r = Screen(tiles[k]);
                bool corner = e.X >= r.Right - 10 && e.X <= r.Right + 8 && e.Y >= r.Bottom - 10 && e.Y <= r.Bottom + 8;
                if (!corner && !RectangleF.Inflate(r, 5, 5).Contains(e.Location)) continue;
                dragging = tiles[k]; resizing = corner; downAt = e.Location; startPlace = (double[])dragging.Place.Clone();
                Capture = true;
                return;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging == null)
            {
                Cursor cur = Cursors.Default;
                if (Tiles != null)
                    foreach (var w in Tiles())
                    {
                        var r = Screen(w);
                        if (Rectangle.Inflate(Box(w), 3, 3).Contains(e.Location)) { cur = Cursors.Hand; break; }
                        if (e.X >= r.Right - 10 && e.X <= r.Right + 8 && e.Y >= r.Bottom - 10 && e.Y <= r.Bottom + 8) { cur = Cursors.SizeNWSE; break; }
                        if (RectangleF.Inflate(r, 5, 5).Contains(e.Location)) cur = Cursors.SizeAll;
                    }
                Cursor = cur;
                return;
            }
            double dx = (e.X - downAt.X) / scale, dy = (e.Y - downAt.Y) / scale;
            if (resizing) dragging.Place[2] = Math.Round(Math.Max(20, Math.Min(300, startPlace[2] + Math.Max(dx, dy))));
            else { dragging.Place[0] = Math.Round(startPlace[0] + dx); dragging.Place[1] = Math.Round(startPlace[1] + dy); }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging == null) return;
            dragging = null; Capture = false;
            Invalidate();
            if (Moved != null) Moved();
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
        public static volatile int PageSize = 30;   // designs per Gallery page (Gallery: Per page); every source uses it

        public class Entry { public string Id, Name; }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "okhttp/3.12.13";
            return wc;
        }

        // category: 1 featured, 2 popular, 3 new; animations: FileType 1, pictures: FileType 5
        public const int Piece = 30;   // the most designs Divoom sends per request

        // one page, asked for 30 designs at a time (see DivoomCloud16.Pieces)
        public static List<Entry> List(int category, bool animations, int page)
        {
            var all = new List<Entry>();
            int first = page * PageSize + 1, end = first + PageSize - 1;
            for (int at = first; at <= end; at += Piece)
            {
                string body = Body(category, animations, at, Math.Min(end, at + Piece - 1));
                var part = Entries(DesignCache.List("classic:" + body, DesignCache.ListFresh, () => FetchJson(body)));
                all.AddRange(part);
                if (part.Count == 0) break;
            }
            return all;
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
        static int PageSize { get { return DivoomCloud.PageSize; } }   // set by the Gallery's Per page slider
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

        public const string Prefix128 = "d128:";   // a 128 x 128 design (Divoom128)

        // fileSize: which sizes of design, a bit each: 1 = 16 x 16, 2 = 32, 4 = 64, 16 = 128, 32 = 256 (the Divoom app 3.8.50)
        static string Range(int start, int end, int fileSize)
        {
            return "\"StartNum\":" + start + ",\"EndNum\":" + end + ",\"FileSize\":" + fileSize + ",\"FileType\":5,\"Version\":12,\"RefreshIndex\":0";
        }

        // Divoom sends at most 30 designs per request (checked 2026-11-11: asking for more still gives 30, and the
        // rest of the range is skipped), so a page of any size is asked for 30 at a time; each piece is cached by its
        // own range, so 30-design pages ask exactly what they always did
        static List<DivoomCloud.Entry> Pieces(int page, TimeSpan fresh, string command, string fields, out int total, bool big = false)
        {
            var all = new List<DivoomCloud.Entry>();
            total = -1;
            int first = page * PageSize + 1, end = first + PageSize - 1;
            for (int at = first; at <= end; at += DivoomCloud.Piece)
            {
                int t;
                var part = Parse(Cached(command, Range(at, Math.Min(end, at + DivoomCloud.Piece - 1), big ? 16 : 1) + fields, fresh), out t, big ? Prefix128 : Prefix);
                if (t >= 0) total = t;
                all.AddRange(part);
                if (part.Count == 0) break;   // past the end of the list
            }
            return all;
        }

        // classify: Divoom's category number; sort: 0 newest, 1 most liked; total: how many the list has (Divoom
        // stops counting at 10,000)
        public static List<DivoomCloud.Entry> List(int classify, int sort, int page, out int total, bool big = false)
        {
            return Pieces(page, DesignCache.ListFresh, "GetCategoryFileListV2", ",\"Classify\":" + classify + ",\"FileSort\":" + sort, out total, big);
        }

        // every 16 x 16 design whose name or tags match the words
        public static List<DivoomCloud.Entry> Search(string words, int page, out int total, bool big = false)
        {
            return Pieces(page, DesignCache.ListFresh, "SearchGalleryV3", ",\"Classify\":0,\"FileSort\":0,\"Keywords\":" + Json(words) + ",\"KeywordsEn\":" + Json(words), out total, big);
        }

        // the signed-in account's liked designs, or its own uploads
        public static List<DivoomCloud.Entry> Mine(bool likes, int page, out int total)
        {
            if (!DivoomAccount.SignedIn) throw new Exception("Sign in to your Divoom account first (Divoom Account button)");
            // the account's own lists change whenever something is liked or uploaded: kept only briefly (and for offline use)
            return Pieces(page, TimeSpan.FromMinutes(10), likes ? "GetMyLikeListV3" : "GetMyUploadListV3", ",\"Classify\":0,\"FileSort\":0," + DivoomAccount.Auth(), out total);
        }

        // a list page from the cache when it is recent, otherwise from Divoom (and saved)
        static string Cached(string command, string fields, TimeSpan fresh)
        {
            return DesignCache.List("d16:" + command + ":" + fields, fresh, () => Post(command, fields));
        }

        static List<DivoomCloud.Entry> Parse(string json, out int total, string prefix = Prefix)
        {
            var count = Regex.Match(json, "\"FileListNum\":(\\d+)");
            total = count.Success ? int.Parse(count.Groups[1].Value) : -1;
            var list = new List<DivoomCloud.Entry>();
            foreach (Match m in Regex.Matches(json, @"\{[^{}]*""FileId""[^{}]*\}"))   // one design (its lists hold no objects)
            {
                var id = Regex.Match(m.Value, "\"FileId\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                var name = Regex.Match(m.Value, "\"FileName\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (id.Success && id.Groups[1].Value.Length > 0)
                    list.Add(new DivoomCloud.Entry { Id = prefix + Regex.Unescape(id.Groups[1].Value), Name = name.Success ? Regex.Unescape(name.Groups[1].Value).Trim() : "" });
            }
            return list;
        }

        // a design file as Divoom keeps it (saved on this PC under the prefix of its size)
        public static byte[] File(string prefix, string id)
        {
            return DesignCache.File(prefix + id, delegate
            {
                for (int tries = 0; ; tries++)   // when Divoom says "too many requests", wait and try again
                {
                    try { using (var wc = Client()) return wc.DownloadData(FileUrl + id); }
                    catch (WebException e) { if (!TooMany(e) || tries >= 3) throw; System.Threading.Thread.Sleep(1500 * (tries + 1)); }
                }
            });
        }

        // a 128 x 128 design: its frames (0xRRGGBB per pixel, row by row); its time per frame goes to MsOf
        public static List<int[]> Frames128(string id)
        {
            int time;
            var frames = Divoom128.Decode(File(Prefix128, id), out time);
            if (frames == null) throw new Exception("Unsupported 128 x 128 file");
            lock (ms) ms[id] = time;
            return frames;
        }

        // 16 x 16 RGB frames (0xRRGGBB per pixel)
        public static List<int[]> Frames(string id)
        {
            byte[] d = File(Prefix, id);
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

    // Divoom's 128 x 128 gallery files (file type 26), worked out from the Divoom app's libtimebox.so
    // (divoom_image_decode_decode_one_fix and the fix_64/32/16/8 functions under it, with Ghidra, 2026-11-12).
    //   file:  1A <frames> <ms per frame, BE16> 08 08, then per frame <length BE32> <frame>
    //   frame: AA <length LE16> <ms LE16> 15 <colors LE16> <the colors, RGB>, then the four 64 x 64 quarters
    //          (top left, top right, bottom left, bottom right)
    // Each square is a quadtree node. A 64, 32 or 16 node starts with a mode byte and a count n (0 = 256):
    //   0  its pixels, each a number into the colors its parent may use (the whole palette at the top)
    //   2  a bitmap of n bits picking which of the parent's colors it uses, then its pixels numbered among those
    //   1  the same bitmap, then its four children (the next size down) choosing among those colors
    // An 8 x 8 node is one byte: top bit set = a bitmap of (byte & 7F) bits then pixels, else pixels straight away.
    // Pixels go in 8 x 8 tiles (tiles left to right, top to bottom; inside a tile row by row), each number packed in
    // as few bits as the colors to choose from need (none when there is just one), lowest bits first.
    static class Divoom128
    {
        public const int N = 128;

        // a 128 x 128 file's frames (0xRRGGBB row by row) and its time per frame; null if it isn't one
        public static List<int[]> Decode(byte[] d, out int ms)
        {
            ms = 0;
            if (d == null || d.Length < 10 || d[0] != 0x1A || d[4] != 8 || d[5] != 8) return null;
            int count = d[1]; ms = d[2] << 8 | d[3];
            var frames = new List<int[]>();
            int at = 6;
            for (int f = 0; f < count && at + 4 <= d.Length; f++)
            {
                int len = d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3];
                at += 4;
                if (len <= 0 || at + len > d.Length) break;
                var px = Frame(d, at, len);
                if (px != null) frames.Add(px);
                at += len;
            }
            return frames.Count > 0 ? frames : null;
        }

        static int Bits(int colors) { int b = 0; while ((1 << b) < colors) b++; return b; }

        class Reader
        {
            public byte[] D; public int Pos; int bit;
            public int Take(int bits)
            {
                int v = 0;
                for (int i = 0; i < bits; i++)
                {
                    if (Pos < D.Length && (D[Pos] >> bit & 1) != 0) v |= 1 << i;
                    if (++bit == 8) { bit = 0; Pos++; }
                }
                return v;
            }
            public void Align() { if (bit != 0) { bit = 0; Pos++; } }
        }

        static int[] Frame(byte[] d, int at, int len)
        {
            if (d[at] != 0xAA || (d[at + 5] & 0x7F) != 0x15) return null;
            int colors = d[at + 6] | d[at + 7] << 8;
            var palette = new int[Math.Max(1, colors)];
            for (int i = 0; i < colors && at + 8 + i * 3 + 2 < d.Length; i++)
                palette[i] = d[at + 8 + i * 3] << 16 | d[at + 9 + i * 3] << 8 | d[at + 10 + i * 3];
            var all = new int[colors];
            for (int i = 0; i < colors; i++) all[i] = i;
            var px = new int[N * N];
            int pos = at + 8 + colors * 3;
            for (int q = 0; q < 4; q++) pos = Node(d, pos, 64, (q & 1) * 64, (q >> 1) * 64, all, palette, px);
            return px;
        }

        // one square of the quadtree at (x, y), `size` across; may: the palette numbers it may use. Returns where the
        // next one starts.
        static int Node(byte[] d, int pos, int size, int x, int y, int[] may, int[] palette, int[] px)
        {
            if (pos >= d.Length) return pos;
            int[] use = may;
            int mode;
            if (size == 8)
            {
                int b = d[pos];
                if ((b & 0x80) != 0) { use = Pick(d, pos + 1, b & 0x7F, may); pos += 1 + ((b & 0x7F) + 7) / 8; }
                else pos += 1;
                return Pixels(d, pos, size, x, y, use, palette, px);
            }
            mode = d[pos];
            int n = d[pos + 1] == 0 ? 256 : d[pos + 1];
            if (mode == 0) return Pixels(d, pos + 1, size, x, y, may, palette, px);
            use = Pick(d, pos + 2, n, may);
            pos += 2 + (n + 7) / 8;
            if (mode == 2) return Pixels(d, pos, size, x, y, use, palette, px);
            int h = size / 2;
            pos = Node(d, pos, h, x, y, use, palette, px);
            pos = Node(d, pos, h, x + h, y, use, palette, px);
            pos = Node(d, pos, h, x, y + h, use, palette, px);
            return Node(d, pos, h, x + h, y + h, use, palette, px);
        }

        // the colors a bitmap of n bits picks out of `may`
        static int[] Pick(byte[] d, int pos, int n, int[] may)
        {
            var o = new List<int>();
            for (int i = 0; i < n; i++)
                if (pos + (i >> 3) < d.Length && (d[pos + (i >> 3)] >> (i & 7) & 1) != 0 && i < may.Length) o.Add(may[i]);
            return o.ToArray();
        }

        static int Pixels(byte[] d, int pos, int size, int x, int y, int[] use, int[] palette, int[] px)
        {
            int bits = Bits(use.Length);
            var r = new Reader { D = d, Pos = pos };
            for (int ty = 0; ty < size; ty += 8)
                for (int tx = 0; tx < size; tx += 8)
                    for (int yy = 0; yy < 8; yy++)
                        for (int xx = 0; xx < 8; xx++)
                        {
                            int v = r.Take(bits);
                            int c = use.Length == 0 ? 0 : palette[use[Math.Min(v, use.Length - 1)]];
                            px[(y + ty + yy) * N + x + tx + xx] = c;
                        }
            // the data takes whole bytes: size x size pixels of `bits` bits
            return pos + size * size * bits / 8;
        }
    }

    // Writing Divoom's 128 x 128 format (Divoom128 reads it): each frame gets its own palette of up to 256 colors and
    // each square of the quadtree takes whichever of its three ways is smallest (all its parent's colors, just the
    // ones it uses, or split in four), so plain areas cost a few bytes. Lossless.
    static class Divoom128Writer
    {
        const int N = 128;

        // a 128 x 128 file (type 26) of these frames (0xRRGGBB, row by row), ms each
        public static byte[] File(List<int[]> frames, int ms)
        {
            var o = new List<byte> { 0x1A, (byte)Math.Min(255, frames.Count), (byte)(ms >> 8), (byte)ms, 8, 8 };
            for (int f = 0; f < frames.Count && f < 255; f++)
            {
                var fr = Frame(frames[f], ms);
                o.Add((byte)(fr.Length >> 24)); o.Add((byte)(fr.Length >> 16)); o.Add((byte)(fr.Length >> 8)); o.Add((byte)fr.Length);
                o.AddRange(fr);
            }
            return o.ToArray();
        }

        static int Bits(int colors) { int b = 0; while ((1 << b) < colors) b++; return b; }

        public static byte[] Frame(int[] px, int ms)
        {
            // the palette: every color, fewer shades if there are more than 256 (rarely)
            int mask = 0xFFFFFF;
            Dictionary<int, int> index;
            while (true)
            {
                index = new Dictionary<int, int>();
                foreach (int c in px) { int k = c & mask; if (!index.ContainsKey(k)) { index[k] = index.Count; if (index.Count > 256) break; } }
                if (index.Count <= 256) break;
                mask = mask == 0xFFFFFF ? 0xFEFEFE : mask == 0xFEFEFE ? 0xFCFCFC : mask == 0xFCFCFC ? 0xF8F8F8 : 0xF0F0F0;
            }
            var ix = new int[px.Length];
            for (int k = 0; k < px.Length; k++) ix[k] = index[px[k] & mask];
            var palette = new int[index.Count];
            foreach (var kv in index) palette[kv.Value] = kv.Key;
            var all = new int[palette.Length];
            for (int i = 0; i < all.Length; i++) all[i] = i;

            var body = new List<byte>();
            for (int q = 0; q < 4; q++) body.AddRange(Node(ix, 64, (q & 1) * 64, (q >> 1) * 64, all));
            var o = new List<byte> { 0xAA, 0, 0, (byte)ms, (byte)(ms >> 8), 0x15, (byte)palette.Length, (byte)(palette.Length >> 8) };
            foreach (int c in palette) { o.Add((byte)(c >> 16)); o.Add((byte)(c >> 8)); o.Add((byte)c); }
            o.AddRange(body);
            o[1] = (byte)o.Count; o[2] = (byte)(o.Count >> 8);
            return o.ToArray();
        }

        // the smallest way to write one square; may: the palette numbers its parent allows, in order
        static List<byte> Node(int[] ix, int size, int x, int y, int[] may)
        {
            var rank = new Dictionary<int, int>();
            for (int i = 0; i < may.Length; i++) rank[may[i]] = i;
            var usedSet = new bool[may.Length];
            for (int yy = 0; yy < size; yy++) for (int xx = 0; xx < size; xx++) usedSet[rank[ix[(y + yy) * N + x + xx]]] = true;
            var used = new List<int>();
            for (int i = 0; i < may.Length; i++) if (usedSet[i]) used.Add(may[i]);
            int n = may.Length;

            // all of the parent's colors
            var best = new List<byte> { 0 };
            best.AddRange(Pixels(ix, size, x, y, rank, Bits(may.Length)));
            if (size == 8)
            {
                if (n <= 127 && used.Count < may.Length)
                {
                    var pick = new List<byte> { (byte)(0x80 | n) }; pick.AddRange(Bitmap(usedSet));
                    pick.AddRange(Pixels(ix, size, x, y, Ranks(used), Bits(used.Count)));
                    if (pick.Count < best.Count) best = pick;
                }
                return best;
            }
            var head = new List<byte> { 2, (byte)(n & 0xFF) }; head.AddRange(Bitmap(usedSet));
            var just = new List<byte>(head); just.AddRange(Pixels(ix, size, x, y, Ranks(used), Bits(used.Count)));
            if (just.Count < best.Count) best = just;
            if (used.Count > 1)
            {   // split in four, each choosing among the colors used here
                var split = new List<byte>(head); split[0] = 1;
                var u = used.ToArray(); int h = size / 2;
                split.AddRange(Node(ix, h, x, y, u)); split.AddRange(Node(ix, h, x + h, y, u));
                split.AddRange(Node(ix, h, x, y + h, u)); split.AddRange(Node(ix, h, x + h, y + h, u));
                if (split.Count < best.Count) best = split;
            }
            return best;
        }

        static Dictionary<int, int> Ranks(List<int> list) { var r = new Dictionary<int, int>(); for (int i = 0; i < list.Count; i++) r[list[i]] = i; return r; }

        static byte[] Bitmap(bool[] on)
        {
            var b = new byte[(on.Length + 7) / 8];
            for (int i = 0; i < on.Length; i++) if (on[i]) b[i >> 3] |= (byte)(1 << (i & 7));
            return b;
        }

        // the pixels in 8 x 8 tiles, each number `bits` bits, lowest bits first
        static byte[] Pixels(int[] ix, int size, int x, int y, Dictionary<int, int> rank, int bits)
        {
            var o = new byte[size * size * bits / 8];
            int at = 0;
            for (int ty = 0; ty < size; ty += 8)
                for (int tx = 0; tx < size; tx += 8)
                    for (int yy = 0; yy < 8; yy++)
                        for (int xx = 0; xx < 8; xx++)
                        {
                            int v = rank[ix[(y + ty + yy) * N + x + tx + xx]];
                            for (int i = 0; i < bits; i++, at++) if ((v >> i & 1) != 0) o[at >> 3] |= (byte)(1 << (at & 7));
                        }
            return o;
        }
    }

    // A 128 x 128 animation for the Tiivoo 2, made as whole loops it plays by itself (sending frame by frame over
    // Bluetooth is too slow at this size). Next gives the next loop, its time per frame, and how long to leave it
    // playing before asking again (0: until Stale). Photo: send it as JPEG (smooth color) instead of Divoom's lossless
    // format (crisp lines on plain backgrounds).
    abstract class Loop128
    {
        public const int N = 128;
        public virtual bool Photo { get { return false; } }
        public virtual bool Stale { get { return false; } }
        public abstract List<int[]> Next(out int ms, out double play);
        // the soundtrack for the loop Next last made (as long as the loop, LoopSound), or null for none
        public virtual short[] Sound() { return null; }

        protected static int Mix(int a, int b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return (int)Math.Round((a >> 16 & 255) * (1 - t) + (b >> 16 & 255) * t) << 16 | (int)Math.Round((a >> 8 & 255) * (1 - t) + (b >> 8 & 255) * t) << 8 | (int)Math.Round((a & 255) * (1 - t) + (b & 255) * t);
        }
    }

    // The heart monitor across the Tiivoo 2's whole 160 x 128 screen: a green ECG trace sweeping across a monitor grid, the
    // bright write head trailing a glow and clearing a gap ahead of it; the heart and the beats per minute at the top,
    // the heart swelling and flushing red with every beat. A sweep holds a whole number of beats, so it loops
    // seamlessly; it is made again when the heart rate (the speed slider) changes.
    class HeartMonitor128 : Loop128
    {
        const int W = Tiivoo2.Wide, H = N;   // the Tiivoo 2's whole screen, 160 x 128
        const int Frames = 60, Base = 94, Gap = 14;
        int bpm = -1;
        public override bool Stale { get { return bpm != HeartMonitor.Bpm(HeartMonitor.Speed()); } }

        // one beat, as a height in pixels at a point of the beat (0..1): P wave, Q, the R spike, S, T wave
        static double Wave(double p)
        {
            Func<double, double, double, double> g = (c, w, h) => h * Math.Exp(-(p - c) * (p - c) / (2 * w * w));
            return g(0.14, 0.03, 8) + g(0.265, 0.008, -7) + g(0.30, 0.01, 62) + g(0.335, 0.01, -18) + g(0.56, 0.05, 15);
        }

        int lastBeats, lastMs;
        // a monitor's beep (about 1 kHz, short) as each spike is drawn
        public override short[] Sound()
        {
            if (lastBeats == 0) return null;
            var track = new LoopSound.Track(Frames * lastMs / 1000.0);
            for (int k = 0; k < lastBeats; k++) track.Tone((k + 0.30) / lastBeats * track.Seconds, 0.09, 1000, 1000, 0.35);
            return track.Pcm();
        }

        public override List<int[]> Next(out int ms, out double play)
        {
            bpm = HeartMonitor.Bpm(HeartMonitor.Speed());
            // a sweep takes about 3 seconds and holds a whole number of beats
            int beats = Math.Max(2, (int)Math.Round(3.0 * bpm / 60.0));
            ms = (int)Math.Round(beats * 60000.0 / bpm / Frames);
            play = 0;
            lastBeats = beats; lastMs = ms;
            var trace = new double[W];
            for (int x = 0; x < W; x++) trace[x] = Base - Wave((x * (double)beats / W) % 1.0);
            var list = new List<int[]>();
            for (int f = 0; f < Frames; f++) list.Add(Draw((int)Math.Round(f * (double)W / Frames) % W, trace, beats));
            return list;
        }

        static readonly int Grid = 0x0A2A12, Fine = 0x061A0B, Line = 0x34FF6A, Glow = 0x0E5A22, Head = 0xE8FFF0;

        int[] Draw(int head, double[] trace, int beats)
        {
            var px = new int[W * H];
            for (int y = 0; y < H; y++)   // the monitor grid
                for (int x = 0; x < W; x++)
                    px[y * W + x] = (x % 16 == 0 || y % 16 == 0) ? Grid : 0;
            // the trace behind the head, brightest just behind it; the gap ahead of the head is cleared
            for (int a = 1; a < W - Gap; a++)
            {
                int x = ((head - a) % W + W) % W, x0 = (x + W - 1) % W;
                if (x0 == W - 1 && x == 0) continue;   // no line across the wrap
                // as it ages the trace pales from green to white, then fades away just before the gap
                double age = a / (double)(W - Gap), pale = Smooth01(0.25, 0.75, age), gone = Smooth01(0.78, 1.0, age);
                int line = Mix(Mix(Line, 0xF2FFF6, pale), 0, gone), glow = Mix(Mix(Glow, 0x3A4A40, pale), 0, gone);
                int ya = (int)Math.Round(trace[x0]), yb = (int)Math.Round(trace[x]);
                int lo = Math.Min(ya, yb), hi = Math.Max(ya, yb);
                for (int y = lo - 1; y <= hi + 1; y++)
                {
                    Set(px, x - 1, y, glow, true); Set(px, x + 1, y, glow, true);
                    Set(px, x, y, y >= lo && y <= hi ? line : glow, false);
                }
                for (int y = lo; y <= hi; y++) Set(px, x, y + 1, line, false);   // two pixels thick
            }
            // the write head: a bright dot with a halo
            int hy = (int)Math.Round(trace[head % W]);
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= 1.2) Set(px, head % W + dx, hy + dy, Head, false);
                    else if (d <= 3.2) Set(px, head % W + dx, hy + dy, Mix(Line, 0, (d - 1.2) / 2.4), true);
                }
            // the heart at the top swells and flushes with the R spike as the head passes it
            double phase = (head % W * (double)beats / W) % 1.0, beat = Math.Exp(-Math.Pow((phase - 0.31) / 0.05, 2));
            Heart(px, 22, 22, 1 + 0.25 * beat, Mix(0x9A1020, 0xFF3048, beat));
            Number(px, 42, 12, bpm.ToString(), Mix(0x34FF6A, 0xC8FFD8, beat * 0.6));
            Label(px, 44 + bpm.ToString().Length * 12, 23, Mix(0x1C8A3A, 0x34FF6A, 0.4));
            return px;
        }

        static int Bright(int c) { return (c >> 16 & 255) + (c >> 8 & 255) + (c & 255); }
        static double Smooth01(double e0, double e1, double x) { double t = Math.Max(0, Math.Min(1, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); }

        static void Set(int[] px, int x, int y, int c, bool under)
        {
            if (x < 0 || x >= W || y < 0 || y >= H) return;
            int k = y * W + x;
            if (under && Bright(px[k]) > Bright(c) && px[k] != Grid && px[k] != Fine) return;   // a glow doesn't dim a brighter line
            px[k] = c;
        }

        static void Heart(int[] px, int cx, int cy, double s, int color)
        {
            for (int y = -14; y <= 14; y++)
                for (int x = -14; x <= 14; x++)
                {
                    double u = x / (9.0 * s), v = -y / (9.0 * s) + 0.25;
                    double f = Math.Pow(u * u + v * v - 1, 3) - u * u * v * v * v;   // the heart curve
                    if (f <= 0) Set(px, cx + x, cy + y, f > -0.08 ? Mix(color, 0xFFFFFF, 0.25) : color, false);
                }
        }

        // big digits: the 5 x 7 clock digits at twice the size
        static void Number(int[] px, int x0, int y0, string text, int color)
        {
            foreach (char ch in text)
            {
                string g = ClockArt.Big[ch - '0'];
                for (int k = 0; k < 35; k++)
                    if (g[k] == '#') for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) Set(px, x0 + k % 5 * 2 + dx, y0 + k / 5 * 2 + dy, color, false);
                x0 += 12;
            }
        }

        // "BPM" in a 3 x 5 pixel font
        static void Label(int[] px, int x0, int y0, int color)
        {
            string[] letters = { "##.#.###.#.###.", "##.#.###.#..#..", "#.#####.##.##.#" };   // B, P, M
            foreach (var g in letters)
            {
                for (int k = 0; k < 15; k++) if (g[k] == '#') Set(px, x0 + k % 3, y0 + k / 3, color, false);
                x0 += 4;
            }
        }
    }

    // The Mandelbrot zoom across the Tiivoo 2's whole 160 x 128 screen: a smooth dive into one of the set's famous spots and back out again (so
    // it loops seamlessly), the colors flowing as it goes; after a few rounds it goes on to another spot, made while
    // the last one plays.
    class Mandelbrot128 : Loop128
    {
        static readonly double[,] Spots = {
            { -0.743643887037151, 0.131825904205330 }, { -0.77568377, 0.13646737 }, { -0.10109636384562, 0.95628651080914 },
            { -1.25066, 0.02012 }, { -0.761574, -0.0847596 }, { 0.360240443437614, -0.641313061064803 },
            { -0.16070135, 1.0375665 }, { 0.2925755, -0.0149977 }, { -0.0452407411, 0.9868162204352258 },
        };
        static readonly int[] Gradient = { 0x0020FF, 0x00D8FF, 0xFFE000, 0xFF4800, 0xE000B0 };
        readonly Random rng = new Random();
        int last = -1;
        public static Func<int> Speed = () => 5;   // its speed slider, 1..10 (set from the main window)
        public override bool Photo { get { return true; } }

        public override List<int[]> Next(out int ms, out double play)
        {
            int spot; do spot = rng.Next(Spots.GetLength(0)); while (spot == last && Spots.GetLength(0) > 1);
            last = spot;
            // frames on the way in (the way out is the same in reverse), small steps (about 1.18 times a frame) so it
            // glides; each frame 100 ms at full speed, 208 at the slowest
            const int Dive = 44;
            ms = 220 - 12 * Math.Max(1, Math.Min(10, Speed()));
            double tx = Spots[spot, 0], ty = Spots[spot, 1], deep = Math.Pow(10, -2.4 - rng.NextDouble() * 0.6), shift = rng.NextDouble();
            var dive = new List<int[]>();
            for (int f = 0; f < Dive; f++)
            {
                double t = f / (Dive - 1.0), e = 0.7 * t + 0.3 * t * t * (3 - 2 * t);   // nearly even steps, a little gentler at both ends
                double width = 3.2 * Math.Pow(deep / 3.2, e), pull = (width / 3.2) * (width / 3.2);
                dive.Add(Draw(tx + (-0.6 - tx) * pull, ty + (0 - ty) * pull, width, shift + t * 0.6));
            }
            var loop = new List<int[]>(dive);
            for (int f = Dive - 2; f > 0; f--) loop.Add(dive[f]);
            play = loop.Count * ms / 1000.0 * 3;   // three rounds, then another spot
            return loop;
        }

        int[] Draw(double cx, double cy, double width, double phase)
        {
            const int W = Tiivoo2.Wide, H = N;   // the whole screen: a wider view of the same height
            var px = new int[W * H];
            int limit = (int)Math.Min(1200, 90 + 70 * Math.Log10(3.2 / width));
            double step = width / H;
            System.Threading.Tasks.Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    double ca = cx + (x - W / 2 + 0.5) * step, cb = cy + (y - H / 2 + 0.5) * step, a = 0, b = 0;
                    int i = 0;
                    for (; i < limit && a * a + b * b < 256; i++) { double t = a * a - b * b + ca; b = 2 * a * b + cb; a = t; }
                    if (i >= limit) { px[y * W + x] = 0; continue; }
                    double mu = i + 1 - Math.Log(Math.Log(Math.Sqrt(a * a + b * b))) / Math.Log(2);
                    double h = Math.Log(1 + mu) * 0.42 + phase; h -= Math.Floor(h);
                    double g = h * Gradient.Length; int k = (int)g;
                    px[y * W + x] = Mix(Gradient[k % Gradient.Length], Gradient[(k + 1) % Gradient.Length], g - k);
                }
            });
            return px;
        }
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

    // LZO1X decompression (as lzo1x_decompress_safe), for Divoom's multi-panel gallery files (types 17 and 18)
    static class Lzo
    {
        public static byte[] Decompress(byte[] src, int maxOut)
        {
            var o = new byte[maxOut]; int ip = 0, op = 0, t, m;
            if (src[ip] > 17)
            {
                t = src[ip++] - 17;
                if (t < 4) goto match_next;
                while (t-- > 0) o[op++] = src[ip++];
                goto first_literal_run;
            }
        loop_top:
            t = src[ip++];
            if (t >= 16) goto match;
            if (t == 0) { while (src[ip] == 0) { t += 255; ip++; } t += 15 + src[ip++]; }
            t += 3; while (t-- > 0) o[op++] = src[ip++];
        first_literal_run:
            t = src[ip++];
            if (t >= 16) goto match;
            m = op - (1 + 0x0800) - (t >> 2) - (src[ip++] << 2);
            o[op++] = o[m++]; o[op++] = o[m++]; o[op++] = o[m];
            goto match_done;
        match:
            if (t >= 64) { m = op - 1 - ((t >> 2) & 7) - (src[ip++] << 3); t = (t >> 5) - 1; }
            else if (t >= 32)
            {
                t &= 31; if (t == 0) { while (src[ip] == 0) { t += 255; ip++; } t += 31 + src[ip++]; }
                m = op - 1 - ((src[ip] >> 2) + (src[ip + 1] << 6)); ip += 2;
            }
            else if (t >= 16)
            {
                m = op - ((t & 8) << 11); t &= 7;
                if (t == 0) { while (src[ip] == 0) { t += 255; ip++; } t += 7 + src[ip++]; }
                m -= (src[ip] >> 2) + (src[ip + 1] << 6); ip += 2;
                if (m == op) goto done;
                m -= 0x4000;
            }
            else
            {
                m = op - 1 - (t >> 2) - (src[ip++] << 2);
                o[op++] = o[m++]; o[op++] = o[m];
                goto match_done;
            }
            t += 2; while (t-- > 0) o[op++] = o[m++];
        match_done:
            t = src[ip - 2] & 3;
            if (t == 0) goto loop_top;
        match_next:
            while (t-- > 0) o[op++] = src[ip++];
            t = src[ip++];
            goto match;
        done:
            var r = new byte[op]; Array.Copy(o, r, op); return r;
        }
    }

    // Divoom's gallery files of every size, as the Divoom app reads them (W3/b.java, 3.8.50), and as the Tiivoo 2 wants
    // them when one of its clock faces asks for a picture (W3/c.java encodeForBlueZSTDDivoomJPeg, worked out 2026-11-12)
    //   9   09 <frames> <ms BE16>, then AES of 16 x 16 RGB frames
    //   17  11 <rows> <cols> <length BE32>, then AES of (LZO of one picture's RGB, in 16 x 16 tiles)
    //   18  12 <frames> <ms BE16> <rows> <cols>, then AES of (<length BE32> <LZO of the RGB in tiles>) per frame
    //   26  1A <frames> <ms BE16> <rows> <cols>, then per frame <length BE32> <Divoom frame> (the device's own frames)
    //   31  1F <frames> <ms BE16> <rows> <cols>, then per frame <length BE32> <JPEG>
    // rows and cols count 16-pixel blocks, so 2 x 2 is 32 x 32
    static class DivoomFiles
    {
        static byte[] Aes(byte[] d, int at)
        {
            int n = (d.Length - at) / 16 * 16;
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = Encoding.ASCII.GetBytes("78hrey23y28ogs89"); aes.IV = Encoding.ASCII.GetBytes("1234567890123456");
                aes.Mode = System.Security.Cryptography.CipherMode.CBC; aes.Padding = System.Security.Cryptography.PaddingMode.None;
                using (var dec = aes.CreateDecryptor()) return dec.TransformFinalBlock(d, at, n);
            }
        }

        // the app's f4.b.p: RGB stored as 16 x 16 tiles (left to right, then down) into rows of the whole picture
        static int[] Untile(byte[] tiles, int at, int rows, int cols)
        {
            int w = cols * 16, h = rows * 16; var px = new int[w * h];
            for (int k = 0; k < w * h; k++)
            {
                int tile = k / 256, inTile = k % 256;
                int x = (tile % cols) * 16 + inTile % 16, y = (tile / cols) * 16 + inTile / 16;
                int i = at + k * 3;
                if (i + 2 < tiles.Length) px[y * w + x] = tiles[i] << 16 | tiles[i + 1] << 8 | tiles[i + 2];
            }
            return px;
        }

        static int BE32(byte[] d, int at) { return d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]; }

        // its frames (0xRRGGBB, row by row), how wide they are and the time per frame; null when the format is unknown
        public static List<int[]> Decode(byte[] d, out int width, out int ms)
        {
            width = 16; ms = 100;
            if (d == null || d.Length < 8) return null;
            var frames = new List<int[]>();
            try
            {
                switch (d[0])
                {
                    case 9:
                    {
                        ms = d[2] << 8 | d[3]; var p = Aes(d, 4);
                        for (int f = 0; (f + 1) * 768 <= p.Length; f++) frames.Add(Untile(p, f * 768, 1, 1));
                        break;
                    }
                    case 17:
                    {
                        int rows = d[1], cols = d[2], len = BE32(d, 3); var p = Aes(d, 7);
                        var part = new byte[len]; Array.Copy(p, part, len);
                        frames.Add(Untile(Lzo.Decompress(part, rows * cols * 768 + 4096), 0, rows, cols)); width = cols * 16;
                        break;
                    }
                    case 18:
                    {
                        ms = d[2] << 8 | d[3]; int rows = d[4], cols = d[5], count = d[1]; var p = Aes(d, 6); int at = 0;
                        for (int f = 0; f < count && at + 4 <= p.Length; f++)
                        {
                            int len = BE32(p, at); at += 4;
                            if (len <= 0 || at + len > p.Length) break;
                            var part = new byte[len]; Array.Copy(p, at, part, 0, len); at += len;
                            frames.Add(Untile(Lzo.Decompress(part, rows * cols * 768 + 4096), 0, rows, cols));
                        }
                        width = cols * 16;
                        break;
                    }
                    case 26:
                    {
                        if (d[4] != 8 || d[5] != 8) return Frames26(d, out width, out ms);   // 32 or 64 across
                        var all = Divoom128.Decode(d, out ms); width = 128; return all;
                    }
                    case 31:
                    {
                        ms = d[2] << 8 | d[3]; int count = d[1], at = 6;
                        for (int f = 0; f < count && at + 4 <= d.Length; f++)
                        {
                            int len = BE32(d, at); at += 4;
                            if (len <= 0 || at + len > d.Length) break;
                            using (var mem = new System.IO.MemoryStream(d, at, len))
                            using (var bmp = new Bitmap(mem))
                            {
                                width = bmp.Width; var px = new int[bmp.Width * bmp.Height];
                                for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) px[y * bmp.Width + x] = bmp.GetPixel(x, y).ToArgb() & 0xFFFFFF;
                                frames.Add(px);
                            }
                            at += len;
                        }
                        break;
                    }
                    default: return null;
                }
            }
            catch (Exception) { return null; }
            return frames.Count > 0 ? frames : null;
        }

        static void AddBE(List<byte> o, long v, int n) { for (int i = n - 1; i >= 0; i--) o.Add((byte)(v >> (8 * i))); }

        // 22 <frames> <ms BE16> <rows> <cols> <length BE32>, then the end of each frame (BE32, counted from after this
        // table) and the frames themselves
        static byte[] Wrap22(List<byte[]> frames, int ms, int blocks)
        {
            var o = new List<byte> { 0x22, (byte)frames.Count };
            AddBE(o, Math.Max(20, Math.Min(0xFFFF, ms)), 2); o.Add((byte)blocks); o.Add((byte)blocks);
            int total = frames.Count * 4; foreach (var f in frames) total += f.Length;
            AddBE(o, total, 4);
            int end = 0; foreach (var f in frames) { end += f.Length; AddBE(o, end, 4); }
            foreach (var f in frames) o.AddRange(f);
            return o.ToArray();
        }

        // a 32 x 32 or 64 x 64 Divoom frame with all its colors (libtimebox decode_one_big type 3, decode_one_64 type 0C):
        // AA <length LE16> <ms LE16> <type> <colors LE16> <the colors, RGB>, then each pixel's color number in
        // ceil(log2(colors)) bits, packed lowest bits first
        static byte[] FramePalette(int[] px, int ms, int type)
        {
            var colors = new List<int>(); var index = new int[px.Length]; var seen = new Dictionary<int, int>();
            for (int i = 0; i < px.Length; i++)
            {
                int at; if (!seen.TryGetValue(px[i], out at)) { at = colors.Count; colors.Add(px[i]); seen[px[i]] = at; }
                index[i] = at;
            }
            int bits = 0; while ((1 << bits) < colors.Count) bits++;
            var pix = new byte[px.Length * bits / 8]; int bit = 0;
            foreach (int v in index) for (int b = 0; b < bits; b++, bit++) if ((v >> b & 1) != 0) pix[bit >> 3] |= (byte)(1 << (bit & 7));
            int size = 8 + colors.Count * 3 + pix.Length;
            var o = new List<byte> { 0xAA, (byte)size, (byte)(size >> 8), (byte)ms, (byte)(ms >> 8), (byte)type, (byte)colors.Count, (byte)(colors.Count >> 8) };
            foreach (int c in colors) { o.Add((byte)(c >> 16)); o.Add((byte)(c >> 8)); o.Add((byte)c); }
            o.AddRange(pix);
            return o.ToArray();
        }

        // a type 26 file of 32 x 32 or 64 x 64 frames (libtimebox decode_one_big and decode_one_64): each frame is raw RGB
        // (32: type 2; 64: 0B, 0E), all its colors (3; 0C, 0F) or colors added to the last frame's (4; 0D, 10), then
        // the pixels as color numbers in ceil(log2(colors)) bits, lowest bits first; the top bit of the type is a flag
        static List<int[]> Frames26(byte[] d, out int width, out int ms)
        {
            width = d[5] * 16; ms = d[2] << 8 | d[3];
            if (width != 32 && width != 64) return null;
            int n = width * width; var frames = new List<int[]>(); var pal = new List<int>(); int at = 6;
            for (int f = 0; f < d[1] && at + 4 <= d.Length; f++)
            {
                int len = BE32(d, at); int p = at + 4; at += 4 + len;
                if (len < 8 || p + len > d.Length || d[p] != 0xAA) break;
                int type = d[p + 5] & 0x7F, count = d[p + 6] | d[p + 7] << 8;
                bool raw = width == 32 ? type == 2 : type == 0x0B || type == 0x0E;
                bool full = width == 32 ? type == 3 : type == 0x0C || type == 0x0F;
                var px = new int[n];
                if (raw)
                {
                    for (int k = 0; k < n && p + 8 + k * 3 + 2 < d.Length; k++) px[k] = d[p + 8 + k * 3] << 16 | d[p + 9 + k * 3] << 8 | d[p + 10 + k * 3];
                    pal.Clear(); frames.Add(width == 64 ? Rows(px, width) : px); continue;
                }
                if (full) pal.Clear();
                for (int c = 0; c < count; c++) { int q = p + 8 + c * 3; pal.Add(d[q] << 16 | d[q + 1] << 8 | d[q + 2]); }
                int bits = 0; while ((1 << bits) < pal.Count) bits++;
                int start = p + 8 + count * 3, bit = 0;
                for (int k = 0; k < n; k++)
                {
                    int v = 0;
                    for (int b = 0; b < bits; b++, bit++) { int q = start + (bit >> 3); if (q < d.Length && (d[q] >> (bit & 7) & 1) != 0) v |= 1 << b; }
                    px[k] = pal.Count == 0 ? 0 : pal[Math.Min(v, pal.Count - 1)];
                }
                frames.Add(width == 64 ? Rows(px, width) : px);
            }
            return frames.Count > 0 ? frames : null;
        }

        // our own 64 x 64 pictures (0xRRGGBB, row by row) the way the Tiivoo 2 takes a clock face's picture
        public static byte[] File64(List<int[]> frames, int ms)
        {
            var enc = new List<byte[]>(); foreach (var f in frames) enc.Add(FramePalette(f, ms, 0x0C));
            return Wrap22(enc, ms, 4);
        }

        // a 64 x 64 frame stored as 16 x 16 tiles (left to right, then down, each row by row), as the store's files have
        // them, put into rows; the Tiivoo 2 takes its frames in rows (tiles sent as they are come out scrambled)
        static int[] Rows(int[] tiled, int w)
        {
            var o = new int[tiled.Length]; int across = w / 16;
            for (int k = 0; k < tiled.Length; k++)
            {
                int tile = k / 256, inTile = k % 256;
                o[((tile / across) * 16 + inTile / 16) * w + (tile % across) * 16 + inTile % 16] = tiled[k];
            }
            return o;
        }

        // a gallery file the way the Tiivoo 2 takes a clock face's picture; null for sizes it isn't made for here
        public static byte[] ForDevice(byte[] d)
        {
            if (d == null || d.Length < 8) return null;
            if (d[0] == 35) return d;   // 160 x 128 (LZO or JPEG frames): the device takes it as it is (W3/c.java r)
            if (d[0] == 31) return d;   // 160 x 128 JPEG frames (1F): as it is too (the Aquarium Clock's background is one)
            if (d[0] == 26 && d[4] != 8)
            {   // 32 or 64 across: written again as whole-palette frames, as the app does (its own frames, which may
                // add to the last frame's colors, the device turns down)
                int w26, ms26; var fr26 = Frames26(d, out w26, out ms26);
                if (fr26 == null) return null;
                var enc26 = new List<byte[]>();
                foreach (var f in fr26) enc26.Add(FramePalette(f, ms26, w26 == 32 ? 3 : 0x0C));
                return Wrap22(enc26, ms26, w26 / 16);
            }
            if (d[0] == 26)
            {   // 128 x 128: its frames are the device's already, only the wrapper changes
                var list = new List<byte[]>(); int at = 6;
                for (int f = 0; f < d[1] && at + 4 <= d.Length; f++)
                {
                    int len = BE32(d, at);
                    if (len <= 0 || at + 4 + len > d.Length) break;
                    var fr = new byte[len]; Array.Copy(d, at + 4, fr, 0, len); list.Add(fr); at += 4 + len;
                }
                return list.Count == 0 ? null : Wrap22(list, d[2] << 8 | d[3], d[5]);
            }
            int width, ms;
            var frames = Decode(d, out width, out ms);
            if (frames == null) return null;
            if (frames.Count > 255) frames = frames.GetRange(0, 255);
            var enc = new List<byte[]>();
            if (width == 16) foreach (var f in frames) enc.Add(Tivoo.Picture(f, ms));
            else if (width == 32) foreach (var f in frames) enc.Add(FramePalette(f, ms, 3));
            else if (width == 64) foreach (var f in frames) enc.Add(FramePalette(f, ms, 0x0C));
            else if (width == 128 && frames[0].Length == 128 * 128)
                return ForDevice(Divoom128Writer.File(frames, ms));
            else return null;
            return Wrap22(enc, ms, width / 16);
        }
    }

    // The Tiivoo 2's clock faces come from Divoom's clock store. The device keeps only a face's number: when one is
    // picked it asks the phone app for the face (its layout, then each picture and font in it), and the app fetches them
    // from Divoom and hands them over. VaVaVoom does the app's part here (the Divoom app's bluetooth/g.java and u.java,
    // 2026-11-12). The store needs a Divoom account and the device's own registration (its id and password, which
    // Divoom gives the device the first time it meets the app: BlueDevice/NewDevice).
    static class ClockStore
    {
        const string Api = "https://appin.divoom-gz.com/", FileUrl = "https://f.divoom-gz.com/";
        public static long DeviceId;
        public static int DevicePassword;

        public class Face
        {
            public int Id; public string Name, Image;
            public override string ToString() { return Name; }
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "okhttp/3.12.13";
            wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
            return wc;
        }

        // one of the store's commands, signed in and as this device; fields: the rest of the JSON, without braces
        public static string Post(string command, string fields)
        {
            if (!DivoomAccount.SignedIn) throw new Exception("Sign in to your Divoom account first (Gallery tab, Divoom Account button)");
            string body = "{\"Command\":\"" + command + "\"," + DivoomAccount.Auth() + ",\"DeviceId\":" + DeviceId + ",\"DevicePassword\":" + DevicePassword +
                          (string.IsNullOrEmpty(fields) ? "" : "," + fields) + "}";
            string json; using (var wc = Client()) json = wc.UploadString(Api + command, body);
            var code = Regex.Match(json, "\"ReturnCode\":(-?\\d+)");
            if (code.Success && code.Groups[1].Value != "0")
            {
                var msg = Regex.Match(json, "\"ReturnMessage\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                throw new Exception("Divoom: " + (msg.Success && msg.Groups[1].Value.Length > 0 ? Regex.Unescape(msg.Groups[1].Value) : "code " + code.Groups[1].Value));
            }
            return json;
        }

        const string Where = "\"Language\":\"en\",\"CountryISOCode\":\"US\"";

        // the store's shelves: (id, name)
        public static List<KeyValuePair<int, string>> Categories()
        {
            string j = Post("Channel/StoreClockGetClassify", "\"StartNum\":1,\"EndNum\":50," + Where);
            var o = new List<KeyValuePair<int, string>>();
            foreach (Match m in Regex.Matches(j, "\"ClassifyId\":(\\d+),\"ClassifyName\":\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string name = Regex.Unescape(m.Groups[2].Value);
                int cut = name.IndexOfAny(new[] { '(', '（' }); if (cut > 0) name = name.Substring(0, cut).Trim();
                o.Add(new KeyValuePair<int, string>(int.Parse(m.Groups[1].Value), name.Replace("&", " & ")));
            }
            return o;
        }

        static List<Face> Parse(string j)
        {
            var o = new List<Face>();
            foreach (Match m in Regex.Matches(j, "\\{\"ClockId\":(\\d+),\"ClockName\":\"((?:[^\"\\\\]|\\\\.)*)\",\"ImagePixelId\":\"([^\"]*)\""))
                o.Add(new Face { Id = int.Parse(m.Groups[1].Value), Name = Regex.Unescape(m.Groups[2].Value), Image = m.Groups[3].Value });
            return o;
        }

        // the faces on one shelf (numbered from 1)
        public static List<Face> Faces(int category, int start, int end)
        {
            return Parse(Post("Channel/StoreClockGetList", "\"ClassifyId\":" + category + ",\"Flag\":0,\"StartNum\":" + start + ",\"EndNum\":" + end + "," + Where));
        }

        // the account's own faces ("My Clocks" in the app)
        public static List<Face> Mine()
        {
            return Parse(Post("Channel/MyClockGetList", "\"StartNum\":1,\"EndNum\":100,\"Flag\":0," + Where));
        }
        public static void AddToMine(int id) { Post("Channel/MyClockAdd", "\"ClockId\":" + id); }

        // a picture file from Divoom, kept on this PC after the first time
        public static byte[] File(string id)
        {
            return DesignCache.File("clock:" + id, delegate { using (var wc = Client()) return wc.DownloadData(FileUrl + id); });
        }

        // the store's preview of a face, at most 64 x 64 for the list (null if its format isn't one read here)
        public static Bitmap Preview(Face f, int size)
        {
            int w, ms;
            var frames = DivoomFiles.Decode(File(f.Image), out w, out ms);
            if (frames == null) return null;
            var px = frames[0]; int h = px.Length / w;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = w > size ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (var src = new Bitmap(w, h))
                {
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) src.SetPixel(x, y, Color.FromArgb(px[y * w + x] | unchecked((int)0xFF000000)));
                    float k = Math.Min((float)size / w, (float)size / h);
                    g.DrawImage(src, (size - w * k) / 2, (size - h * k) / 2, w * k, h * k);
                }
            }
            return bmp;
        }

        // registering a device Divoom hasn't met: its server's time, signed with the app's key, then a new id and password
        public static void Register(int type, int subType, out long id, out int password)
        {
            string utcJ = Post("APP/GetServerUTC", "");
            string utc = Regex.Match(utcJ, "\"UTC\":(\\d+)").Groups[1].Value;
            string sig;
            using (var h = new System.Security.Cryptography.HMACMD5(Encoding.ASCII.GetBytes("DivoomBluetoothDevice<>?")))
            {
                var sb = new StringBuilder(); foreach (byte b in h.ComputeHash(Encoding.UTF8.GetBytes(utc))) sb.Append(b.ToString("x2")); sig = sb.ToString();
            }
            string r = Post("BlueDevice/NewDevice", "\"UTC\":\"" + utc + "\",\"UTCEncrypt\":\"" + sig + "\",\"Type\":" + type + ",\"SubType\":" + subType);
            id = long.Parse(Regex.Match(r, "\"BluetoothDeviceId\":(\\d+)").Groups[1].Value);
            password = int.Parse(Regex.Match(r, "\"DevicePassword\":(-?\\d+)").Groups[1].Value);
        }
    }

    // A loop's soundtrack, played on this PC's sound output to the Tiivoo 2 (it is also a Bluetooth speaker: "Speakers
    // (Divoom Tiivoo 2-Audio)" in Windows), or the default output if it isn't there. It repeats seamlessly with the
    // animation, started when the loop starts on the screen. Uses the Windows waveOut functions; nothing to install.
    static class LoopSound
    {
        public const int Rate = 22050;
        // Bluetooth audio arrives late: sounds are placed this much earlier in their loop so they land on time
        public const double Latency = 0.2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WaveOutCaps { public short Mid, Pid; public int DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public int Formats; public short Channels, Reserved; public int Support; }
        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormat { public short Tag, Channels; public int SamplesPerSec, AvgBytesPerSec; public short BlockAlign, BitsPerSample, Size; }
        [StructLayout(LayoutKind.Sequential)]
        struct WaveHeader { public IntPtr Data; public int BufferLength, BytesRecorded; public IntPtr User; public int Flags, Loops; public IntPtr Next, Reserved; }

        [DllImport("winmm.dll")] static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCapsW(IntPtr id, ref WaveOutCaps caps, int size);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, IntPtr id, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        static readonly object gate = new object();
        static IntPtr handle = IntPtr.Zero, header = IntPtr.Zero, data = IntPtr.Zero;

        // the Tiivoo 2's speaker, or -1 (Windows' default) when it isn't connected
        static IntPtr Device()
        {
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WaveOutCaps();
                if (waveOutGetDevCapsW((IntPtr)i, ref caps, Marshal.SizeOf(typeof(WaveOutCaps))) == 0 && caps.Name != null &&
                    caps.Name.IndexOf("Tiivoo", StringComparison.OrdinalIgnoreCase) >= 0) return (IntPtr)i;
            }
            return (IntPtr)(-1);
        }

        // play 16-bit mono samples (at Rate) round and round until Stop
        public static void Play(short[] pcm)
        {
            lock (gate)
            {
                Stop();
                if (pcm == null || pcm.Length == 0) return;
                var fmt = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSec = Rate, AvgBytesPerSec = Rate * 2, BlockAlign = 2, BitsPerSample = 16, Size = 0 };
                if (waveOutOpen(out handle, Device(), ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { handle = IntPtr.Zero; return; }
                data = Marshal.AllocHGlobal(pcm.Length * 2);
                Marshal.Copy(pcm, 0, data, pcm.Length);
                var hdr = new WaveHeader { Data = data, BufferLength = pcm.Length * 2, Flags = 4 | 8, Loops = int.MaxValue };   // WHDR_BEGINLOOP | WHDR_ENDLOOP
                header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHeader)));
                Marshal.StructureToPtr(hdr, header, false);
                int size = Marshal.SizeOf(typeof(WaveHeader));
                waveOutPrepareHeader(handle, header, size);
                // the loop flags are set again after preparing (preparing adds its own flag)
                var prepared = (WaveHeader)Marshal.PtrToStructure(header, typeof(WaveHeader));
                prepared.Flags |= 4 | 8; prepared.Loops = int.MaxValue;
                Marshal.StructureToPtr(prepared, header, false);
                waveOutWrite(handle, header, size);
            }
        }

        public static void Stop()
        {
            lock (gate)
            {
                if (handle != IntPtr.Zero)
                {
                    waveOutReset(handle);
                    if (header != IntPtr.Zero) waveOutUnprepareHeader(handle, header, Marshal.SizeOf(typeof(WaveHeader)));
                    waveOutClose(handle);
                    handle = IntPtr.Zero;
                }
                if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
                if (data != IntPtr.Zero) { Marshal.FreeHGlobal(data); data = IntPtr.Zero; }
            }
        }

        // building a soundtrack: a buffer as long as the loop, sounds added at their times (wrapping round the end,
        // shifted earlier by Latency), then turned into 16-bit samples
        public class Track
        {
            public readonly float[] S;
            public Track(double seconds) { S = new float[Math.Max(1, (int)Math.Round(seconds * Rate))]; }
            public double Seconds { get { return S.Length / (double)Rate; } }

            // a tone from f0 to f1 Hz over `dur` seconds, with soft edges; buzz adds odd harmonics for a retro edge
            public void Tone(double at, double dur, double f0, double f1, double vol, double buzz = 0)
            {
                int n = (int)(dur * Rate), start = (int)Math.Round((at - Latency) * Rate);
                double phase = 0;
                for (int i = 0; i < n; i++)
                {
                    double p = i / (double)n, f = f0 + (f1 - f0) * p;
                    phase += 2 * Math.PI * f / Rate;
                    double env = Math.Min(1, i / (0.005 * Rate)) * Math.Min(1, (n - i) / (0.02 * Rate));
                    double v = Math.Sin(phase) + buzz * (Math.Sin(3 * phase) / 3 + Math.Sin(5 * phase) / 5);
                    int k = ((start + i) % S.Length + S.Length) % S.Length;
                    S[k] += (float)(v * vol * env);
                }
            }

            public short[] Pcm()
            {
                var o = new short[S.Length];
                for (int i = 0; i < S.Length; i++) o[i] = (short)Math.Max(-32000, Math.Min(32000, S[i] * 32000));
                return o;
            }
        }
    }

    // The weather on the Tiivoo 2's whole 160 x 128 screen, drawn to look like a photograph of the sky rather than an
    // icon: soft layered clouds lit from the sun's side and drifting, a sun with its glow by day, a moon and twinkling
    // stars at night, rain streaks, falling snow, fog banks, lightning; distant hills in the haze below. The temperature
    // sits over it. When tomorrow's forecast came, the card turns over like a card to show tomorrow (its high and low
    // over tomorrow's sky), then turns back. One seamless loop the device plays by itself.
    static class WeatherScene
    {
        const int W = 160, H = 128, Frames = 96, Ms = 115;
        public static int FrameMs { get { return Ms; } }

        static double Hash(int x, int y, int seed)
        {
            unchecked { int h = x * 374761393 + y * 668265263 + seed * 1442695041; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0; }
        }
        static double Smooth(double e0, double e1, double x) { double t = Math.Max(0, Math.Min(1, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); }

        // value noise, repeating every `period` cells across
        static double Noise(double x, double y, int period, int seed)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0; fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int xa = ((x0 % period) + period) % period, xb = (xa + 1) % period;
            double a = Hash(xa, y0, seed), b = Hash(xb, y0, seed), c = Hash(xa, y0 + 1, seed), d = Hash(xb, y0 + 1, seed);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }
        static double Fbm(double x, double y, int period, int seed)
        {
            double s = 0, amp = 0.5;
            for (int o = 0; o < 5; o++) { s += amp * Noise(x, y, period, seed + o * 17); x *= 2; y *= 2; period *= 2; amp *= 0.5; }
            return s;
        }

        // the kinds of sky
        enum Sky { Clear, PartlyCloudy, Overcast, Fog, Rain, Showers, Snow, Storm }
        static Sky KindOf(int code)
        {
            if (code == 0) return Sky.Clear;
            if (code <= 2) return Sky.PartlyCloudy;
            if (code == 3) return Sky.Overcast;
            if (code == 45 || code == 48) return Sky.Fog;
            if (code >= 51 && code <= 67) return Sky.Rain;
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return Sky.Snow;
            if (code >= 80 && code <= 82) return Sky.Showers;
            if (code >= 95) return Sky.Storm;
            return Sky.Overcast;
        }

        public static List<int[]> Make(WeatherInfo w, string place)
        {
            var now = new Scene(KindOf(w.Code), w.IsDay, 11);
            Scene next = w.HasTomorrow ? new Scene(KindOf(w.TomorrowCode), true, 23) : null;
            string temp = Math.Round(w.Temp) + "°", what = w.Description;
            string tHigh = next != null ? Math.Round(w.TomorrowHigh) + "°" : "", tLow = next != null ? Math.Round(w.TomorrowLow) + "°" : "";
            var frames = new List<int[]>();
            // today 44 frames, a turn of 4, tomorrow 44, a turn back of 4 (no forecast: today all through)
            for (int f = 0; f < Frames; f++)
            {
                double t = f / (double)Frames;
                int[] px;
                if (next == null || f < 44) px = Label(now.Draw(t), temp, what, place, false);
                else if (f >= 48 && f < 92) px = Label(next.Draw(t), tHigh, w.TomorrowDescription, tLow, true);
                else
                {   // turning over: the old face narrows to its edge, then the new one widens
                    double p = ((f < 48 ? f - 44 : f - 92) + 0.5) / 4.0;
                    bool toTomorrow = f < 48, first = p < 0.5;
                    int[] face = (toTomorrow == first) ? Label(now.Draw(t), temp, what, place, false) : Label(next.Draw(t), tHigh, w.TomorrowDescription, tLow, true);
                    px = Turn(face, Math.Abs(Math.Cos(Math.PI * p)), 1 - 0.45 * Math.Sin(Math.PI * p));
                }
                frames.Add(px);
            }
            return frames;
        }

        static int[] Turn(int[] face, double width, double light)
        {
            var px = new int[W * H];
            int half = (int)Math.Round(W / 2.0 * width);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int dx = x - W / 2;
                    if (Math.Abs(dx) >= half) { px[y * W + x] = 0x050608; continue; }
                    int sx = W / 2 + (int)Math.Round(dx / Math.Max(0.01, width));
                    if (sx < 0 || sx >= W) continue;
                    int c = face[y * W + sx];
                    px[y * W + x] = (int)((c >> 16 & 255) * light) << 16 | (int)((c >> 8 & 255) * light) << 8 | (int)((c & 255) * light);
                }
            return px;
        }

        // the words over the sky: today the temperature big, the conditions and the place; tomorrow "Tomorrow", the
        // high big and the low beside it, and the conditions. White with a soft shadow, so they read on any sky.
        static int[] Label(int[] px, string big, string line, string extra, bool tomorrow)
        {
            using (var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                var raw = new int[W * H];
                for (int k = 0; k < raw.Length; k++) raw[k] = px[k] | unchecked((int)0xFF000000);
                System.Runtime.InteropServices.Marshal.Copy(raw, 0, data.Scan0, raw.Length);
                bmp.UnlockBits(data);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Action<string, Font, float, float, Color> text = (s, font, x, y, col) =>
                    {
                        using (var sh = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                            for (int k = 0; k < 4; k++) g.DrawString(s, font, sh, x + (k % 2 == 0 ? 1 : 2), y + (k < 2 ? 1 : 2));
                        using (var b = new SolidBrush(col)) g.DrawString(s, font, b, x, y);
                    };
                    using (var huge = new Font("Segoe UI Semibold", 38f, GraphicsUnit.Pixel))
                    using (var mid = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel))
                    using (var small = new Font("Segoe UI", 13f, GraphicsUnit.Pixel))
                    using (var tag = new Font("Segoe UI Semibold", 12f, GraphicsUnit.Pixel))
                    {
                        if (!tomorrow)
                        {
                            text(big, huge, 4, 0, Color.White);
                            text(line, small, 7, 46, Color.FromArgb(235, 242, 250));
                            if (!string.IsNullOrEmpty(extra)) text(extra.Split(',')[0], tag, 7, 62, Color.FromArgb(200, 215, 230));
                        }
                        else
                        {
                            text("TOMORROW", tag, 7, 4, Color.FromArgb(220, 232, 245));
                            text(big, huge, 4, 14, Color.White);
                            var bigSize = g.MeasureString(big, huge);
                            text("/ " + extra, mid, 4 + bigSize.Width - 8, 28, Color.FromArgb(200, 215, 230));
                            text(line, small, 7, 60, Color.FromArgb(235, 242, 250));
                        }
                    }
                }
                data = bmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
                var back = new int[W * H];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, back, 0, back.Length);
                bmp.UnlockBits(data);
                for (int k = 0; k < back.Length; k++) back[k] &= 0xFFFFFF;
                return back;
            }
        }

        // one sky, drawn at a moment of the loop (0..1); everything moves in whole cycles of the loop
        class Scene
        {
            readonly Sky kind; readonly bool day; readonly int seed;
            readonly double sunX, sunY;
            readonly double[] hillFar = new double[W], hillNear = new double[W];
            readonly double[,] drops, flakes, stars;
            readonly List<Point> bolt = new List<Point>();

            public Scene(Sky kind, bool day, int seed)
            {
                this.kind = kind; this.day = day; this.seed = seed;
                var rnd = new Random(seed * 7 + (int)kind);
                sunX = 108 + rnd.Next(30); sunY = 26 + rnd.Next(12);
                for (int x = 0; x < W; x++)
                {
                    hillFar[x] = H - 22 - 9 * Fbm(x / 40.0, 0.5, 4, seed + 3);
                    hillNear[x] = H - 10 - 7 * Fbm(x / 26.0, 3.5, 7, seed + 9);
                }
                drops = new double[kind == Sky.Storm ? 110 : kind == Sky.Showers ? 60 : 85, 3];
                for (int i = 0; i < drops.GetLength(0); i++) { drops[i, 0] = rnd.NextDouble() * (W + 30); drops[i, 1] = rnd.NextDouble(); drops[i, 2] = 3 + rnd.Next(3); }
                flakes = new double[70, 4];
                for (int i = 0; i < 70; i++) { flakes[i, 0] = rnd.NextDouble() * W; flakes[i, 1] = rnd.NextDouble(); flakes[i, 2] = 0.5 + rnd.NextDouble() * 1.5; flakes[i, 3] = rnd.NextDouble(); }
                stars = new double[45, 4];
                for (int i = 0; i < 45; i++) { stars[i, 0] = rnd.NextDouble() * W; stars[i, 1] = rnd.NextDouble() * 70; stars[i, 2] = 0.3 + rnd.NextDouble() * 0.7; stars[i, 3] = rnd.NextDouble(); }
                // a lightning bolt: a jagged path from the cloud base down to the hills, with a fork
                int bx = 30 + rnd.Next(100), by = 34;
                while (by < H - 18) { bolt.Add(new Point(bx, by)); by += 2 + rnd.Next(3); bx += rnd.Next(5) - 2; }
            }

            double Cover { get { return kind == Sky.Clear ? 0.0 : kind == Sky.PartlyCloudy ? 0.42 : kind == Sky.Showers ? 0.7 : kind == Sky.Fog ? 0.5 : 1.0; } }
            bool Wet { get { return kind == Sky.Rain || kind == Sky.Showers || kind == Sky.Storm; } }

            // the cloud texture at a point and moment (about 0..1): drifting slowly right, blended so the loop joins up
            double Raw(double x, double y, double t)
            {
                double drift = 26;
                double u = x / 40.0, v = y / 17.0;
                double a = Fbm(u - t * drift / 40.0, v, 64, seed), b = Fbm(u - (t - 1) * drift / 40.0, v, 64, seed);
                double n = a * (1 - t) + b * t;
                n = (n - 0.5) / Math.Sqrt(t * t + (1 - t) * (1 - t));   // keep the contrast through the blend
                return n * 2.3 + 0.5;
            }
            // how much cloud there is: the texture over a threshold that falls as the cover grows; thinner low down
            double Density(double n, double y)
            {
                double lift = Smooth(75, 15, y);
                return Smooth(0.75 - 0.6 * Cover, 0.95 - 0.5 * Cover, n) * (0.4 + 0.6 * lift);
            }

            public int[] Draw(double t)
            {
                var r = new double[W * H]; var g = new double[W * H]; var b = new double[W * H];
                bool dark = !day;
                double gloom = kind == Sky.Storm ? 0.45 : Wet ? 0.65 : kind == Sky.Overcast ? 0.8 : kind == Sky.Fog ? 0.9 : 1;
                // lightning: a flash and a flicker once a loop
                double flash = kind == Sky.Storm ? Math.Max(Math.Exp(-Math.Pow((t - 0.31) / 0.008, 2)), 0.6 * Math.Exp(-Math.Pow((t - 0.345) / 0.006, 2))) : 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int k = y * W + x; double d = y / (double)H;
                        // the sky: deep overhead, paler toward the horizon
                        double sr, sg, sb;
                        if (!dark) { sr = 40 + 120 * d; sg = 95 + 105 * d; sb = 190 + 45 * d; }
                        else { sr = 6 + 22 * d; sg = 10 + 26 * d; sb = 30 + 40 * d; }
                        double grey = kind == Sky.Clear || kind == Sky.PartlyCloudy ? 0 : kind == Sky.Fog ? 0.85 : 0.75;
                        double lum = (sr + sg + sb) / 3;
                        sr += (lum - sr) * grey; sg += (lum - sg) * grey; sb += (lum - sb) * grey;
                        sr *= gloom; sg *= gloom; sb *= gloom;
                        // the sun's (or moon's) glow
                        double dx = x - sunX, dy = y - sunY, dist = Math.Sqrt(dx * dx + dy * dy);
                        if (!dark && kind != Sky.Storm)
                        {
                            double glow = Math.Exp(-dist / 18.0) * 0.9 + Math.Exp(-dist / 55.0) * 0.35;
                            glow *= kind == Sky.Clear || kind == Sky.PartlyCloudy ? 1 : 0.35;
                            sr += 255 * glow; sg += 235 * glow; sb += 190 * glow;
                        }
                        else if (dark && (kind == Sky.Clear || kind == Sky.PartlyCloudy))
                        {
                            double glow = Math.Exp(-dist / 14.0) * 0.35;
                            sr += 170 * glow; sg += 185 * glow; sb += 210 * glow;
                        }
                        r[k] = sr; g[k] = sg; b[k] = sb;
                    }
                // stars, twinkling, behind the clouds
                if (dark && (kind == Sky.Clear || kind == Sky.PartlyCloudy))
                    for (int i = 0; i < stars.GetLength(0); i++)
                    {
                        int sx = (int)stars[i, 0], sy = (int)stars[i, 1]; int k = sy * W + sx;
                        double tw = stars[i, 2] * (0.6 + 0.4 * Math.Sin(2 * Math.PI * (t * (1 + i % 3) + stars[i, 3])));
                        r[k] += 230 * tw; g[k] += 235 * tw; b[k] += 255 * tw;
                    }
                // the sun's disc, or the moon with its shading
                if (kind == Sky.Clear || kind == Sky.PartlyCloudy)
                    for (int y = (int)sunY - 9; y <= sunY + 9; y++)
                        for (int x = (int)sunX - 9; x <= sunX + 9; x++)
                        {
                            if (x < 0 || x >= W || y < 0 || y >= H) continue;
                            double dist = Math.Sqrt((x - sunX) * (x - sunX) + (y - sunY) * (y - sunY));
                            double rad = day ? 6.5 : 7.5, edge = Math.Max(0, Math.Min(1, rad + 0.5 - dist));
                            if (edge <= 0) continue;
                            int k = y * W + x;
                            double cr, cg, cb;
                            if (day) { cr = 255; cg = 250; cb = 228; }
                            else
                            {   // the moon: pale, with darker seas, lit a little more on one side
                                double sea = Fbm((x - sunX) / 3.0 + 10, (y - sunY) / 3.0 + 10, 64, seed + 40);
                                double lit = 0.75 + 0.25 * ((x - sunX) / rad);
                                cr = (235 - 70 * Smooth(0.45, 0.65, sea)) * lit; cg = (235 - 65 * Smooth(0.45, 0.65, sea)) * lit; cb = (225 - 50 * Smooth(0.45, 0.65, sea)) * lit;
                            }
                            r[k] += (cr - r[k]) * edge; g[k] += (cg - g[k]) * edge; b[k] += (cb - b[k]) * edge;
                        }
                // clouds: soft, lit on the side toward the sun, darker underneath
                if (kind != Sky.Clear)
                {
                    double lx = (sunX - W / 2.0) / W * 6, ly = -3;
                    for (int y = 0; y < H - 12; y++)
                        for (int x = 0; x < W; x++)
                        {
                            double n = Raw(x, y, t), c = Density(n, y);
                            if (c <= 0.01) continue;
                            double toward = Raw(x + lx, y + ly, t);
                            // shadowed where the cloud thickens toward the light, bright where it thins: billows, even in
                            // a sky that is all cloud
                            double lit = Math.Max(0.35, Math.Min(1.2, 0.95 - 1.9 * (toward - n) - 0.25 * Smooth(0.5, 1.2, n)));
                            double baseShade = 1 - 0.3 * Smooth(0.4, 1.1, n) * (0.4 + y / 70.0);
                            double cr, cg, cb;
                            if (!dark) { cr = 238; cg = 240; cb = 245; } else { cr = 70; cg = 78; cb = 95; }
                            double tone = lit * baseShade * gloom * (kind == Sky.Storm ? 0.75 : 1);
                            cr *= tone; cg *= tone; cb *= tone;
                            if (!dark && kind == Sky.PartlyCloudy)
                            {   // the sun lighting the edges facing it
                                double sd = Math.Sqrt((x - sunX) * (x - sunX) + (y - sunY) * (y - sunY));
                                double rim = Math.Exp(-sd / 30.0) * (1 - c) * 1.2;
                                cr += 255 * rim; cg += 230 * rim; cb += 180 * rim;
                            }
                            int k = y * W + x; double a = Math.Min(1, c * 1.15);
                            r[k] += (cr - r[k]) * a; g[k] += (cg - g[k]) * a; b[k] += (cb - b[k]) * a;
                        }
                }
                // the lightning bolt, glowing, at the flash
                if (flash > 0.05)
                {
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int k = y * W + x; r[k] += 120 * flash; g[k] += 125 * flash; b[k] += 150 * flash; }
                    for (int i = 1; i < bolt.Count; i++)
                    {
                        var p0 = bolt[i - 1]; var p1 = bolt[i];
                        for (int s = 0; s <= 4; s++)
                        {
                            int x = p0.X + (p1.X - p0.X) * s / 4, y = p0.Y + (p1.Y - p0.Y) * s / 4;
                            for (int oy = -2; oy <= 2; oy++)
                                for (int ox = -2; ox <= 2; ox++)
                                {
                                    int xx = x + ox, yy = y + oy; if (xx < 0 || xx >= W || yy < 0 || yy >= H) continue;
                                    double a = (ox == 0 && oy == 0 ? 1 : Math.Exp(-(ox * ox + oy * oy) / 2.0) * 0.5) * flash;
                                    int k = yy * W + xx; r[k] += (255 - r[k]) * a; g[k] += (250 - g[k]) * a; b[k] += (255 - b[k]) * a;
                                }
                        }
                    }
                }
                // the hills: far ones pale in the haze, near ones dark; white under snow, glistening when wet
                for (int x = 0; x < W; x++)
                    for (int y = (int)hillFar[x]; y < H; y++)
                    {
                        int k = y * W + x; bool near = y >= hillNear[x];
                        double hr, hg, hb, haze;
                        if (kind == Sky.Snow) { hr = near ? 225 : 200; hg = near ? 230 : 208; hb = near ? 238 : 222; haze = near ? 0.1 : 0.45; }
                        else { hr = near ? 22 : 50; hg = near ? 40 : 70; hb = near ? 34 : 78; haze = near ? 0.05 : 0.55; }
                        double tone = (dark ? 0.35 : 1) * (kind == Sky.Snow ? 1 : gloom) + 0.6 * flash;
                        if (Hash(x, y, seed) > 0.8) tone *= 0.88;   // texture: trees and rocks
                        hr *= tone; hg *= tone; hb *= tone;
                        hr += (r[k] - hr) * haze; hg += (g[k] - hg) * haze; hb += (b[k] - hb) * haze;
                        r[k] = hr; g[k] = hg; b[k] = hb;
                    }
                // fog banks drifting low across
                if (kind == Sky.Fog)
                    for (int y = 30; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            double a0 = Fbm((x - t * 30) / 60.0, y / 9.0, 64, seed + 77), a1 = Fbm((x - (t - 1) * 30) / 60.0, y / 9.0, 64, seed + 77);
                            double f = (a0 * (1 - t) + a1 * t) * Smooth(30, 100, y) * 1.25;
                            int k = y * W + x; double fv = dark ? 70 : 205;
                            r[k] += (fv - r[k]) * Math.Min(0.92, f); g[k] += (fv + 4 - g[k]) * Math.Min(0.92, f); b[k] += (fv + 10 - b[k]) * Math.Min(0.92, f);
                        }
                // rain: thin bright streaks slanting down, nearer ones longer
                if (Wet)
                    for (int i = 0; i < drops.GetLength(0); i++)
                    {
                        double sp = drops[i, 2], p = (drops[i, 1] + t * sp) % 1.0;
                        double y0 = p * (H + 30) - 20, x0 = drops[i, 0] - 0.3 * (y0 + 20);
                        int len = (int)(5 + sp * 2);
                        for (int s = 0; s < len; s++)
                        {
                            int x = (int)Math.Round(x0 - 0.3 * s), y = (int)Math.Round(y0 - s);
                            if (x < 0 || x >= W || y < 0 || y >= H) continue;
                            int k = y * W + x; double a = 0.28 * (1 - s / (double)len) * (dark ? 0.6 : 1);
                            r[k] += (210 - r[k]) * a; g[k] += (220 - g[k]) * a; b[k] += (235 - b[k]) * a;
                        }
                    }
                // snow: soft flakes drifting down and swaying, the near ones larger and blurred
                if (kind == Sky.Snow)
                    for (int i = 0; i < flakes.GetLength(0); i++)
                    {
                        double size = flakes[i, 2], p = (flakes[i, 1] + t * (size > 1.3 ? 2 : 1)) % 1.0;
                        double fy = p * (H + 10) - 5, fx = flakes[i, 0] + 3 * Math.Sin(2 * Math.PI * (t * 2 + flakes[i, 3]));
                        for (int oy = -2; oy <= 2; oy++)
                            for (int ox = -2; ox <= 2; ox++)
                            {
                                int x = (int)Math.Round(fx) + ox, y = (int)Math.Round(fy) + oy; if (x < 0 || x >= W || y < 0 || y >= H) continue;
                                double a = Math.Exp(-(ox * ox + oy * oy) / (size * size)) * 0.9;
                                int k = y * W + x; r[k] += (250 - r[k]) * a; g[k] += (252 - g[k]) * a; b[k] += (255 - b[k]) * a;
                            }
                    }
                var px = new int[W * H];
                for (int k = 0; k < px.Length; k++)
                {   // a lens's vignette, then into 0xRRGGBB
                    int x = k % W, y = k / W; double vx = (x - W / 2.0) / (W / 2.0), vy = (y - H / 2.0) / (H / 2.0);
                    double v = 1 - 0.25 * Smooth(0.5, 1.6, vx * vx + vy * vy);
                    px[k] = (int)Math.Max(0, Math.Min(255, r[k] * v)) << 16 | (int)Math.Max(0, Math.Min(255, g[k] * v)) << 8 | (int)Math.Max(0, Math.Min(255, b[k] * v));
                }
                return px;
            }
        }
    }

    // A retro sci-fi robot filling the Tiivoo 2's whole 160 x 128 screen, rendered to look like a photograph of a
    // chrome-and-enamel machine in a dim lab: mirror-chrome head, shoulders and arms reflecting a bright ceiling and a dark
    // floor, teal enamel torso with rivets, a smoked-glass visor across its face with a red light scanning side to side and
    // a fading trail, a mouth grille whose lights dance as it talks, a blinking antenna, spinning ear rings, a chest panel
    // with an oscilloscope, gauges and blinking lamps, and an arm that waves now and then. It beeps and boops (LoopSound,
    // through the Tiivoo 2's speaker) and its lights answer each sound. One seamless loop.
    class Robot160 : Loop128
    {
        const int W = Tiivoo2.Wide, H = N, Frames = 80, Ms = 110;
        const double Loop = Frames * Ms / 1000.0;   // seconds
        public override bool Photo { get { return true; } }

        // the sounds: what, when (seconds into the loop) and how long
        enum Kind { Beep, Boop, Chirp, Warble, Burble }
        class Ev { public Kind K; public double At, Dur, F0, F1; }
        static readonly Ev[] Events =
        {
            new Ev { K = Kind.Beep, At = 0.40, Dur = 0.12, F0 = 1250, F1 = 1250 },
            new Ev { K = Kind.Boop, At = 0.60, Dur = 0.20, F0 = 620, F1 = 380 },
            new Ev { K = Kind.Beep, At = 1.05, Dur = 0.08, F0 = 1500, F1 = 1500 },
            new Ev { K = Kind.Beep, At = 1.18, Dur = 0.08, F0 = 1500, F1 = 1500 },
            new Ev { K = Kind.Warble, At = 2.00, Dur = 0.45, F0 = 850, F1 = 1250 },
            new Ev { K = Kind.Chirp, At = 3.20, Dur = 0.12, F0 = 700, F1 = 2100 },
            new Ev { K = Kind.Chirp, At = 3.40, Dur = 0.12, F0 = 2100, F1 = 700 },
            new Ev { K = Kind.Boop, At = 4.30, Dur = 0.20, F0 = 450, F1 = 290 },
            new Ev { K = Kind.Boop, At = 4.60, Dur = 0.22, F0 = 420, F1 = 260 },
            new Ev { K = Kind.Burble, At = 5.60, Dur = 0.70, F0 = 500, F1 = 1800 },
            new Ev { K = Kind.Beep, At = 7.10, Dur = 0.10, F0 = 1300, F1 = 1300 },
            new Ev { K = Kind.Boop, At = 7.28, Dur = 0.24, F0 = 700, F1 = 330 },
        };

        public override List<int[]> Next(out int ms, out double play)
        {
            ms = Ms; play = 150;   // sent again every few minutes so sound and picture stay together
            var list = new List<int[]>();
            for (int f = 0; f < Frames; f++) list.Add(Draw(f * Ms / 1000.0));
            return list;
        }

        public override short[] Sound()
        {
            var track = new LoopSound.Track(Loop);
            foreach (var e in Events)
            {
                if (e.K == Kind.Warble)
                    for (double s = 0; s < e.Dur; s += 1 / 16.0) track.Tone(e.At + s, 1 / 16.0, (int)(s * 16) % 2 == 0 ? e.F0 : e.F1, (int)(s * 16) % 2 == 0 ? e.F0 : e.F1, 0.28, 0.4);
                else if (e.K == Kind.Burble)
                {
                    var rnd = new Random(7);
                    for (double s = 0; s < e.Dur; s += 0.045) { double f = e.F0 + rnd.NextDouble() * (e.F1 - e.F0); track.Tone(e.At + s, 0.04, f, f, 0.24, 0.6); }
                }
                else track.Tone(e.At, e.Dur, e.F0, e.F1, e.K == Kind.Boop ? 0.38 : 0.3, e.K == Kind.Beep ? 0.35 : 0.15);
            }
            return track.Pcm();
        }

        // how much it is "talking" at a moment (0..1): up while a sound plays, easing off just after
        static double Talk(double s)
        {
            double v = 0;
            foreach (var e in Events)
            {
                double d = ((s - e.At) % Loop + Loop) % Loop;
                if (d < e.Dur) v = Math.Max(v, 1);
                else if (d < e.Dur + 0.15) v = Math.Max(v, 1 - (d - e.Dur) / 0.15);
            }
            return v;
        }
        static double Beeping(double s)
        {   // just the bright beeps and chirps, for the antenna
            double v = 0;
            foreach (var e in Events)
            {
                if (e.K != Kind.Beep && e.K != Kind.Chirp) continue;
                double d = ((s - e.At) % Loop + Loop) % Loop;
                if (d < e.Dur + 0.1) v = Math.Max(v, d < e.Dur ? 1 : 1 - (d - e.Dur) / 0.1);
            }
            return v;
        }

        static double Hash(int x, int y) { unchecked { int h = x * 374761393 + y * 668265263; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0; } }
        static double Smooth(double e0, double e1, double x) { double t = Math.Max(0, Math.Min(1, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); }
        static double Wave(double s, double cycles, double phase) { return Math.Sin(2 * Math.PI * (s / Loop * cycles + phase)); }

        // lighting: a key light above and to the left, toward the viewer
        const double Lx = -0.38, Ly = -0.72, Lz = 0.58;

        // mirror chrome: the reflection of a bright lab ceiling above, a dark floor below, a sharp horizon between;
        // a hard highlight from the key light and a cool rim light from behind
        static void Chrome(double nx, double ny, double nz, double tint, out double r, out double g, out double b)
        {
            double ry = 2 * nz * ny, rx = 2 * nz * nx;   // the reflected direction (looking into the screen)
            double er, eg, eb;
            if (ry < -0.05) { double k = Smooth(-0.05, -0.9, ry); er = 120 + 120 * k; eg = 140 + 110 * k; eb = 165 + 85 * k; }   // the ceiling
            else if (ry < 0.08) { er = 30; eg = 34; eb = 42; }   // the horizon line
            else { double k = Smooth(0.08, 0.9, ry); er = 70 - 40 * k; eg = 58 - 30 * k; eb = 52 - 26 * k; }   // the floor
            er += 40 * Math.Max(0, rx) * (ry < 0 ? 1 : 0.3); eb += 30 * Math.Max(0, -rx);   // warm light one side, cool the other
            double spec = Math.Pow(Math.Max(0, nx * Lx + ny * Ly + nz * Lz), 60) * 255;
            double rim = Math.Pow(1 - nz, 3) * 0.6;
            r = er * (1 - 0.2 * tint) + spec + 40 * rim; g = eg + spec + 140 * rim; b = eb * (1 + 0.1 * tint) + spec + 170 * rim;
        }

        // glossy enamel paint over metal: lit color, a soft gloss and a little of the room reflected
        static void Enamel(double nx, double ny, double nz, double cr, double cg, double cb, out double r, out double g, out double b)
        {
            double diff = Math.Max(0, nx * Lx + ny * Ly + nz * Lz), spec = Math.Pow(Math.Max(0, nx * Lx + ny * Ly + nz * Lz), 24) * 160;
            double rim = Math.Pow(1 - nz, 3) * 0.5;
            double k = 0.22 + 0.9 * diff;
            r = cr * k + spec + 30 * rim; g = cg * k + spec + 110 * rim; b = cb * k + spec + 140 * rim;
        }

        // the picture at s seconds into the loop: each pixel from four samples, for smooth edges
        int[] Draw(double s)
        {
            var px = new int[W * H];
            double talk = Talk(s), beep = Beeping(s);
            System.Threading.Tasks.Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    double R = 0, G = 0, B = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        double r, g, b;
                        Sample(x + 0.25 + (k & 1) * 0.5, y + 0.25 + (k >> 1) * 0.5, s, talk, beep, out r, out g, out b);
                        R += r; G += g; B += b;
                    }
                    double vx = (x - W / 2.0) / (W / 2.0), vy = (y - H / 2.0) / (H / 2.0), vig = 1 - 0.3 * Smooth(0.5, 1.6, vx * vx + vy * vy);
                    px[y * W + x] = (int)Math.Max(0, Math.Min(255, R / 4 * vig)) << 16 | (int)Math.Max(0, Math.Min(255, G / 4 * vig)) << 8 | (int)Math.Max(0, Math.Min(255, B / 4 * vig));
                }
            });
            return px;
        }

        // the scene at one point, front to back
        static void Sample(double x, double y, double s, double talk, double beep, out double r, out double g, out double b)
        {
            // the head: bobbing a little, tilting as it glances about
            double tilt = 0.07 * Wave(s, 1, 0.1) + 0.05 * Smooth(1.9, 2.3, s) * (1 - Smooth(2.6, 3.0, s)) - 0.05 * Smooth(4.2, 4.5, s) * (1 - Smooth(5.0, 5.4, s));
            double hx = 80, hy = 40 + 1.2 * Wave(s, 2, 0);
            double ct = Math.Cos(-tilt), st = Math.Sin(-tilt);
            double u = (x - hx) * ct - (y - hy) * st, v = (x - hx) * st + (y - hy) * ct;   // head coordinates

            // the antenna ball, glowing red, flashing with the beeps
            {
                double du = u, dv = v + 35, d = Math.Sqrt(du * du + dv * dv);
                double glow = 0.35 + 0.65 * beep;
                if (d < 3.4)
                {
                    double nz = Math.Sqrt(Math.Max(0, 1 - d * d / 11.6));
                    r = 120 + 135 * glow + 80 * nz; g = 20 + 60 * glow * nz; b = 20 + 40 * glow * nz; return;
                }
                if (Math.Abs(u) < 0.9 && v > -33 && v < -20)
                {   // its rod
                    Chrome(u / 0.9, 0, Math.Sqrt(Math.Max(0, 1 - u * u / 0.81)), 0, out r, out g, out b); return;
                }
            }
            // the head: a rounded chrome box (a superellipse), with the ears at its sides
            double a = 28, bb = 21;
            {
                // ears: chrome discs with spinning grooves
                for (int side = -1; side <= 1; side += 2)
                {
                    double eu = u - side * (a + 1.5), ev = v, ed = Math.Sqrt(eu * eu + ev * ev);
                    if (ed < 7)
                    {
                        double nz = Math.Sqrt(Math.Max(0, 1 - ed * ed / 49));
                        Chrome(eu / 7 * 0.8, ev / 7 * 0.8, nz, 0, out r, out g, out b);
                        double ang = Math.Atan2(ev, eu) + side * 2 * Math.PI * s / Loop * 6;
                        double groove = 0.75 + 0.25 * Math.Sign(Math.Sin(ang * 4));
                        if (ed < 2.2) groove = 0.6;
                        r *= groove; g *= groove; b *= groove; return;
                    }
                }
                double f = Math.Pow(Math.Abs(u / a), 4) + Math.Pow(Math.Abs(v / bb), 4);
                if (f <= 1)
                {
                    // the visor: a band of smoked glass across the face, a red light scanning along it
                    if (Math.Abs(v + 1) < 7 && Math.Abs(u) < 23)
                    {
                        double vv = (v + 1) / 7, uu = u / 23;
                        double edge = Math.Max(Math.Abs(uu), Math.Abs(vv));
                        // the glass itself: dark, reflecting the room faintly, curved top and bottom
                        double gnz = Math.Sqrt(Math.Max(0, 1 - vv * vv * 0.6)), cr, cg, cb;
                        Chrome(uu * 0.3, vv * 0.7, gnz, 0, out cr, out cg, out cb);
                        r = 8 + cr * 0.16; g = 6 + cg * 0.14; b = 10 + cb * 0.16;
                        // the scanner: a row of red cells; the bright one sweeps side to side (2 round trips a loop,
                        // easing at the ends), cells behind it fading out
                        double cell = Math.Floor((u + 21) / 3.5);
                        double cx = -21 + (cell + 0.5) * 3.5, inCell = Math.Abs(u - cx) < 1.4 && Math.Abs(vv) < 0.4 ? 1 : 0.25;
                        double sweep = Math.Sin(2 * Math.PI * s / Loop * 2), pos = sweep * 19;   // where the light is
                        double dirn = Math.Cos(2 * Math.PI * s / Loop * 2);                       // which way it moves
                        double behind = (cx - pos) * -Math.Sign(dirn);                              // > 0: in its trail
                        double lit = Math.Exp(-Math.Pow(cx - pos, 2) / 6.0);
                        if (behind > 0) lit = Math.Max(lit, Math.Exp(-behind / 7.0) * 0.75);
                        lit *= 0.85 + 0.25 * talk;
                        double glow = Math.Exp(-Math.Pow(u - pos, 2) / 30.0) * Math.Exp(-vv * vv * 2) * 0.6;   // light spilling in the glass
                        r += 255 * lit * inCell + 200 * glow; g += 30 * lit * inCell * inCell + 20 * glow; b += 25 * lit * inCell + 15 * glow;
                        if (vv < -0.55 && Math.Abs(uu) < 0.9) { r += 40; g += 45; b += 55; }   // a reflection along the glass's top edge
                        if (edge > 0.93) { r *= 0.5; g *= 0.5; b *= 0.5; }                     // its rim
                        return;
                    }
                    // the mouth grille: a dark slot with lights that dance as it talks
                    if (v > 9 && v < 15.5 && Math.Abs(u) < 11)
                    {
                        r = 14; g = 16; b = 20;
                        double bar = Math.Floor((u + 11) / 3.67), bx = -11 + (bar + 0.5) * 3.67;
                        if (Math.Abs(u - bx) < 1.1)
                        {
                            double level = talk * (0.35 + 0.65 * Hash((int)bar, (int)(s * 22)));
                            double fromBottom = (15.5 - v) / 6.5;
                            if (fromBottom < level) { r = 60; g = 220; b = 255; } else { r = 22; g = 40; b = 48; }
                        }
                        return;
                    }
                    // chrome: the surface normal of the rounded box
                    double e = Math.Sqrt(f), gu = Math.Sign(u) * Math.Pow(Math.Abs(u / a), 3) / a, gv = Math.Sign(v) * Math.Pow(Math.Abs(v / bb), 3) / bb;
                    double gl = Math.Sqrt(gu * gu + gv * gv) + 1e-9, sl = Math.Pow(e, 1.6) * 0.95;
                    double nx = gu / gl * sl, ny = gv / gl * sl, nz = Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny));
                    // turn the normal back from the head's tilt
                    double wx = nx * Math.Cos(tilt) - ny * Math.Sin(tilt), wy = nx * Math.Sin(tilt) + ny * Math.Cos(tilt);
                    Chrome(wx, wy, nz, 0, out r, out g, out b);
                    if (Math.Abs(v + 9) < 0.5 && Math.Abs(u) < 24) { r *= 0.55; g *= 0.55; b *= 0.55; }   // a seam above the visor
                    return;
                }
            }
            // the right arm (on the left of the picture), waving now and then; the other hangs at its side
            {
                double wave = Smooth(5.4, 5.8, s) * (1 - Smooth(7.4, 7.9, s));
                double ang = (1 - wave) * 0.15 + wave * (-2.2 + 0.25 * Math.Sin(2 * Math.PI * s * 2.2));   // from hanging down to up and waving
                if (Arm(x, y, 30, 84, ang, s, out r, out g, out b)) return;
                if (Arm(x, y, 130, 84, -0.15, s, out r, out g, out b)) return;
            }
            // shoulders: chrome balls
            for (int side = -1; side <= 1; side += 2)
            {
                double sx0 = 80 + side * 48, sy0 = 82, du = x - sx0, dv = y - sy0, d = Math.Sqrt(du * du + dv * dv);
                if (d < 11) { double nz = Math.Sqrt(Math.Max(0, 1 - d * d / 121)); Chrome(du / 11, dv / 11, nz, 0, out r, out g, out b); return; }
            }
            // the neck: chrome rings over a dark rubber hose
            if (y > 58 && y < 74 && Math.Abs(x - 80) < 12)
            {
                double ring = (y - 58) % 5;
                if (ring < 2.6)
                {
                    double nx = (x - 80) / 12.0, ny = (ring - 1.3) / 1.6;
                    Chrome(nx, ny * 0.8, Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny * 0.64)), 0, out r, out g, out b); return;
                }
                if (Math.Abs(x - 80) < 9) { r = 20; g = 22; b = 26; return; }
            }
            // the torso: teal enamel, rounded across and at the top, with rivets and the chest panel
            if (x > 34 && x < 126 && y > 72)
            {
                double tx = (x - 80) / 46.0, top = y - 72;
                // rounded top corners (radius 14)
                double cx0 = Math.Abs(x - 80) - 32, cy0 = 14 - top;
                bool inside = cx0 <= 0 || cy0 <= 0 || cx0 * cx0 + cy0 * cy0 <= 196;
                if (inside)
                {
                    // the chest panel
                    if (x > 54 && x < 106 && y > 82 && y < 124)
                    {
                        Panel(x, y, s, talk, out r, out g, out b); return;
                    }
                    double nx = tx * 0.85, ny = top < 7 ? -(1 - top / 7.0) * 0.8 : 0, nz = Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny));
                    Enamel(nx, ny, nz, 40, 150, 150, out r, out g, out b);
                    // rivets
                    for (int side = -1; side <= 1; side += 2)
                        for (int k = 0; k < 5; k++)
                        {
                            double rx = 80 + side * 39, ry = 80 + k * 10, d = Math.Sqrt((x - rx) * (x - rx) + (y - ry) * (y - ry));
                            if (d < 1.6) { Chrome((x - rx) / 1.6 * 0.8, (y - ry) / 1.6 * 0.8, Math.Sqrt(Math.Max(0, 1 - d * d / 2.56)), 0, out r, out g, out b); return; }
                        }
                    if (Math.Abs(x - 54) < 0.6 && y > 82 && y < 124 || Math.Abs(x - 106) < 0.6 && y > 82 && y < 124 || Math.Abs(y - 82) < 0.6 && x > 54 && x < 106)
                    { r *= 0.5; g *= 0.5; b *= 0.5; }
                    return;
                }
            }
            // the lab behind: dark blue-grey, wall panels, soft out-of-focus lights
            Background(x, y, s, out r, out g, out b);
        }

        // an arm from the shoulder at (sx0, sy0), pointing at `ang` (0 = straight down, negative = up and out to the
        // left of the picture): a chrome tube with accordion joints and a two-fingered claw
        static bool Arm(double x, double y, double sx0, double sy0, double ang, double s, out double r, out double g, out double b)
        {
            r = g = b = 0;
            double dx = Math.Sin(ang), dy = Math.Cos(ang);   // along the arm
            double px = x - sx0, py = y - sy0, along = px * dx + py * dy, across = px * dy - py * dx;
            const double Len = 40, Rad = 5.2;
            if (along > 0 && along < Len && Math.Abs(across) < Rad)
            {
                double n = across / Rad, nz = Math.Sqrt(Math.Max(0, 1 - n * n));
                double nx = n * dy, ny = -n * dx;
                Chrome(nx, ny, nz, 0, out r, out g, out b);
                if (along % 6 < 1.4 && along > 8) { r *= 0.55; g *= 0.55; b *= 0.6; }   // accordion folds
                return true;
            }
            // the claw at the end: two curved fingers
            double ca = along - Len;
            if (ca > -1 && ca < 9)
            {
                for (int f = -1; f <= 1; f += 2)
                {
                    double fx = f * (2.5 + ca * 0.45 - ca * ca * 0.06);
                    if (Math.Abs(across - fx) < 1.5)
                    {
                        double n = (across - fx) / 1.5, nz = Math.Sqrt(Math.Max(0, 1 - n * n));
                        Chrome(n * dy, -n * dx, nz, 0, out r, out g, out b); return true;
                    }
                }
            }
            return false;
        }

        // the chest panel: dark glass behind a bezel, with an oscilloscope tracing the sounds, two gauges and a row of lamps
        static void Panel(double x, double y, double s, double talk, out double r, out double g, out double b)
        {
            r = 10; g = 12; b = 16;
            if (x < 55.5 || x > 104.5 || y < 83.5 || y > 122.5) { r = 90; g = 96; b = 104; return; }   // the bezel
            // the oscilloscope
            if (x > 58 && x < 86 && y > 86 && y < 104)
            {
                r = 4; g = 22; b = 10;
                if (((int)(x - 58) % 7 == 0) || ((int)(y - 86) % 6 == 0)) { r = 8; g = 40; b = 18; }   // its grid
                double amp = 1 + 6 * talk;
                double wy = 95 + amp * Math.Sin(x * 0.9 + s * 40) * Math.Sin(x * 0.17 + s * 7);
                double d = Math.Abs(y - wy);
                if (d < 2.2) { double k = Math.Exp(-d * d * 1.2); r += 60 * k; g += 255 * k; b += 110 * k; }
                return;
            }
            // two gauges: cream faces, red needles swinging
            for (int k = 0; k < 2; k++)
            {
                double gx = 96, gy = 92 + k * 19, d = Math.Sqrt((x - gx) * (x - gx) + (y - gy) * (y - gy));
                if (d < 7.5)
                {
                    if (d > 6.3) { Chrome((x - gx) / 7.5, (y - gy) / 7.5, 0.5, 0, out r, out g, out b); return; }
                    r = 235; g = 228; b = 205;
                    double needle = -2.2 + 1.6 * (k == 0 ? 0.5 + 0.4 * Math.Sin(2 * Math.PI * s / Loop * 3) + 0.3 * talk : 0.3 + 0.6 * talk);
                    double nx = Math.Sin(needle), ny = -Math.Cos(needle), along = (x - gx) * nx + (y - gy) * ny, across = (x - gx) * ny - (y - gy) * nx;
                    if (along > -1 && along < 5.5 && Math.Abs(across) < 0.6) { r = 200; g = 30; b = 25; }
                    if (d < 1) { r = 40; g = 40; b = 40; }
                    if (Math.Abs(d - 5.4) < 0.4 && Math.Sin(Math.Atan2(y - gy, x - gx) * 6) > 0.6) { r = 60; g = 60; b = 60; }   // ticks
                    return;
                }
            }
            // a row of lamps: red, amber, green, blue, white, blinking in their own rhythms
            int[][] lamp = { new[] { 255, 50, 40 }, new[] { 255, 170, 30 }, new[] { 60, 255, 90 }, new[] { 60, 140, 255 }, new[] { 240, 240, 255 } };
            for (int k = 0; k < 5; k++)
            {
                double lx = 61 + k * 5.5, ly = 113, d = Math.Sqrt((x - lx) * (x - lx) + (y - ly) * (y - ly));
                if (d < 2.2)
                {
                    bool on = Hash(k, (int)(s * (3 + k))) > 0.45 || talk > 0.5 && k % 2 == 0;
                    double kk = on ? 1 : 0.18, core = Math.Max(0, 1 - d / 2.2);
                    r = lamp[k][0] * kk * (0.6 + 0.4 * core) + 60 * core * kk; g = lamp[k][1] * kk * (0.6 + 0.4 * core) + 60 * core * kk; b = lamp[k][2] * kk * (0.6 + 0.4 * core) + 60 * core * kk;
                    return;
                }
            }
            // a faint reflection across the glass
            double refl = Math.Exp(-Math.Pow((x - 70) * 0.6 + (y - 90), 2) / 40.0) * 0.5;
            r += 30 * refl; g += 36 * refl; b += 44 * refl;
        }

        static void Background(double x, double y, double s, out double r, out double g, out double b)
        {
            double d = y / H;
            r = 16 + 14 * d; g = 22 + 18 * d; b = 34 + 20 * d;
            // wall panels, softly out of focus
            double px = (x % 40) / 40.0, py = (y % 36) / 36.0;
            if (px < 0.03 || py < 0.03) { r *= 0.8; g *= 0.8; b *= 0.8; }
            // out-of-focus lights: soft discs of color, slowly pulsing
            double[,] bokeh = { { 14, 20, 7, 0 }, { 150, 16, 6, 1 }, { 22, 70, 5, 2 }, { 140, 60, 8, 0 }, { 8, 110, 6, 1 }, { 152, 104, 5, 2 }, { 118, 30, 4, 2 }, { 40, 38, 4, 1 } };
            int[][] col = { new[] { 40, 200, 255 }, new[] { 255, 80, 200 }, new[] { 255, 170, 50 } };
            for (int i = 0; i < bokeh.GetLength(0); i++)
            {
                double dx = x - bokeh[i, 0], dy = y - bokeh[i, 1], rr = bokeh[i, 2], dd = Math.Sqrt(dx * dx + dy * dy);
                if (dd > rr + 1.5) continue;
                double k = Smooth(rr + 1.5, rr - 3, dd) * (0.12 + 0.05 * Math.Sin(2 * Math.PI * (s / Loop + i * 0.17)));   // faint and soft
                int[] c = col[(int)bokeh[i, 3]];
                r += c[0] * k; g += c[1] * k; b += c[2] * k;
            }
        }
    }

    // A slot machine on the Tiivoo 2's whole 160 x 128 screen, drawn like a photograph: a glossy red cabinet with a
    // marquee of chasing bulbs, three curved reels behind chrome and glass with a red payline, a win display and a chrome
    // lever. Each loop holds two spins: the lever pulls with a clunk, the reels blur and tick as they spin, then stop one
    // by one with a thunk. Small wins ding and show what they pay; now and then (about one loop in five) it hits the
    // jackpot, 7-7-7: the last reel holds back for suspense, then the lights flash, a fanfare plays, JACKPOT! glows and
    // coins shower down clinking. Its sounds come through the Tiivoo 2's speaker (LoopSound).
    class SlotMachine160 : Loop128
    {
        const int W = Tiivoo2.Wide, H = N, SpinFrames = 42, Ms = 115;
        public override bool Photo { get { return true; } }

        enum Sym { Seven, Cherry, Bar, Lemon, Bell, Diamond }
        static readonly Sym[] Strip = { Sym.Seven, Sym.Cherry, Sym.Bar, Sym.Lemon, Sym.Bell, Sym.Diamond, Sym.Cherry, Sym.Lemon, Sym.Bar, Sym.Bell };
        const int SymH = 22;   // pixels from one symbol to the next on a reel

        readonly Random rng = new Random();
        static Bitmap[] art;   // each symbol, drawn once

        // one spin: where each reel starts and stops (strip positions), when each stops (frame), and what it pays
        class Spin { public double[] From = new double[3], To = new double[3]; public int[] StopAt = new int[3]; public int Win; public bool Jackpot; }
        Spin[] spins;

        public override List<int[]> Next(out int ms, out double play)
        {
            ms = Ms; play = 0;   // the next pair of spins is sent as soon as this one is up
            if (art == null) art = MakeArt();
            // two spins: the first lands on A, the second on B, starting from B, so the loop ends where it began.
            // About one loop in five, the second spin is the jackpot.
            bool jackpot = rng.NextDouble() < 0.2;
            int winA, winB;
            int[] a = Outcome(false, out winA), b = Outcome(jackpot, out winB);
            spins = new[] { Between(b, a, false), Between(a, b, jackpot) };
            spins[0].Win = winA; spins[1].Win = winB; spins[1].Jackpot = jackpot;
            var list = new List<int[]>();
            for (int k = 0; k < 2; k++) for (int f = 0; f < SpinFrames; f++) list.Add(Draw(spins[k], f, k * SpinFrames + f));
            return list;
        }

        // a spin from one landing to another: whole turns of the strip first, more for the later reels (and a long
        // last reel for a jackpot, for suspense)
        static Spin Between(int[] from, int[] to, bool jackpot)
        {
            var sp = new Spin();
            for (int r = 0; r < 3; r++)
            {
                int turns = 2 + r + (jackpot && r == 2 ? 2 : 0);
                sp.From[r] = from[r];
                sp.To[r] = from[r] + turns * Strip.Length + ((to[r] - from[r]) % Strip.Length + Strip.Length) % Strip.Length;
                sp.StopAt[r] = 18 + r * 4 + (jackpot && r == 2 ? 6 : 0);
            }
            return sp;
        }
        // where the reels should land for a spin: a jackpot, sometimes a small win, else a miss
        int[] Outcome(bool jackpot, out int win)
        {
            var land = new int[3];
            if (jackpot) { int s7 = Array.IndexOf(Strip, Sym.Seven); land[0] = land[1] = land[2] = s7; win = 500; return land; }
            double roll = rng.NextDouble();
            if (roll < 0.25)
            {   // three of a kind (not sevens)
                var kinds = new[] { Sym.Cherry, Sym.Lemon, Sym.Bell, Sym.Bar, Sym.Diamond };
                var k = kinds[rng.Next(kinds.Length)];
                for (int r = 0; r < 3; r++) { int i; do i = rng.Next(Strip.Length); while (Strip[i] != k); land[r] = i; }
            }
            else
                do { for (int r = 0; r < 3; r++) land[r] = rng.Next(Strip.Length); } while (Strip[land[0]] == Strip[land[1]] && Strip[land[1]] == Strip[land[2]]);
            bool jp; win = Pays(land, out jp);
            return land;
        }
        static int Pays(int[] land, out bool jackpot)
        {
            Sym a = Strip[land[0]], b = Strip[land[1]], c = Strip[land[2]];
            jackpot = a == Sym.Seven && b == Sym.Seven && c == Sym.Seven;
            if (jackpot) return 500;
            if (a == b && b == c) return a == Sym.Diamond ? 100 : a == Sym.Bar ? 50 : a == Sym.Bell ? 25 : a == Sym.Lemon ? 15 : 10;
            if (a == Sym.Cherry && b == Sym.Cherry) return 5;
            return 0;
        }

        // a reel's position at frame f of a spin: still before the pull, speeding up, spinning, easing into its stop
        // with a small bounce
        static double ReelAt(Spin sp, int r, int f)
        {
            const int Go = 5;
            if (f <= Go) return sp.From[r];
            int stop = sp.StopAt[r];
            if (f >= stop + 3) return sp.To[r];
            double total = sp.To[r] - sp.From[r];
            if (f >= stop) { double k = (f - stop) / 3.0; return sp.To[r] + 0.18 * Math.Sin(k * Math.PI) * (1 - k); }   // the bounce
            double p = (f - Go) / (double)(stop - Go);
            double e = p < 0.15 ? 0.5 * p * p / 0.15 / 0.85 : (p - 0.075) / 0.925;   // speeds up, then steady to the stop
            e = Math.Min(1, e);
            return sp.From[r] + total * e;
        }
        static bool Spinning(Spin sp, int r, int f) { return f > 5 && f < sp.StopAt[r]; }

        // ---- the soundtrack ----
        public override short[] Sound()
        {
            if (spins == null) return null;
            var track = new LoopSound.Track(2 * SpinFrames * Ms / 1000.0);
            for (int k = 0; k < 2; k++)
            {
                var sp = spins[k]; double t0 = k * SpinFrames * Ms / 1000.0;
                Func<int, double> at = f => t0 + f * Ms / 1000.0;
                Clunk(track, at(4));
                // the ticking of the reels going round, until the last one stops
                for (double s = at(6); s < at(sp.StopAt[2]); s += 0.045) Click(track, s, 0.12);
                for (int r = 0; r < 3; r++) Thump(track, at(sp.StopAt[r]));
                double done = at(sp.StopAt[2] + 1);
                if (sp.Jackpot)
                {
                    double[] notes = { 523, 659, 784, 1047, 784, 1047, 1319 };
                    for (int i = 0; i < notes.Length; i++) Bell(track, done + i * 0.13, notes[i], 0.5, 0.22);
                    Bell(track, done + 0.95, 1047, 1.2, 0.18); Bell(track, done + 0.95, 1319, 1.2, 0.14); Bell(track, done + 0.95, 1568, 1.2, 0.12);
                    var rnd = new Random(k + 3);
                    for (double s = done + 0.4; s < done + 2.6; s += 0.05 + rnd.NextDouble() * 0.06) Bell(track, s, 3000 + rnd.NextDouble() * 2500, 0.12, 0.07);   // coins
                }
                else if (sp.Win > 0) { Bell(track, done, 1319, 0.4, 0.25); Bell(track, done + 0.16, 1760, 0.6, 0.25); }
            }
            return track.Pcm();
        }
        static int Index(LoopSound.Track tr, double at) { int n = tr.S.Length; return ((int)Math.Round((at - LoopSound.Latency) * LoopSound.Rate) % n + n) % n; }
        // a bell: a struck tone dying away, with a bright overtone
        static void Bell(LoopSound.Track tr, double at, double f, double dur, double vol)
        {
            int start = Index(tr, at), n = (int)(dur * LoopSound.Rate);
            for (int i = 0; i < n; i++)
            {
                double tt = i / (double)LoopSound.Rate, env = Math.Exp(-tt * 5 / dur) * Math.Min(1, i / 40.0);
                tr.S[(start + i) % tr.S.Length] += (float)(vol * env * (Math.Sin(2 * Math.PI * f * tt) + 0.4 * Math.Sin(2 * Math.PI * f * 2.76 * tt) * Math.Exp(-tt * 12)));
            }
        }
        static readonly Random noise = new Random(11);
        // a reel's tick: a tiny burst of noise
        static void Click(LoopSound.Track tr, double at, double vol)
        {
            int start = Index(tr, at);
            for (int i = 0; i < 90; i++) tr.S[(start + i) % tr.S.Length] += (float)(vol * (noise.NextDouble() * 2 - 1) * Math.Exp(-i / 18.0));
        }
        // a reel stopping: a low knock
        static void Thump(LoopSound.Track tr, double at)
        {
            int start = Index(tr, at);
            for (int i = 0; i < 2200; i++)
            {
                double tt = i / (double)LoopSound.Rate;
                tr.S[(start + i) % tr.S.Length] += (float)(0.45 * Math.Exp(-tt * 28) * Math.Sin(2 * Math.PI * (110 - 40 * tt * 10) * tt) + 0.15 * (noise.NextDouble() * 2 - 1) * Math.Exp(-i / 60.0));
            }
        }
        // the lever: a mechanical clunk, a knock and a short ratchet
        static void Clunk(LoopSound.Track tr, double at)
        {
            Thump(tr, at);
            for (int k = 0; k < 6; k++) Click(tr, at + 0.03 + k * 0.025, 0.2);
        }

        // ---- the picture ----
        static Bitmap[] MakeArt()
        {
            var o = new Bitmap[6];
            for (int i = 0; i < 6; i++)
            {
                // drawn at 4 times the size, then shrunk smoothly
                using (var big = new Bitmap(120, 88))
                {
                    using (var g = Graphics.FromImage(big))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                        g.Clear(Color.Transparent);
                        DrawSymbol(g, (Sym)i);
                    }
                    var small = new Bitmap(30, 22);
                    using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(big, 0, 0, 30, 22); }
                    o[i] = small;
                }
            }
            return o;
        }

        static void DrawSymbol(Graphics g, Sym s)
        {
            switch (s)
            {
                case Sym.Cherry:
                    using (var stem = new Pen(Color.FromArgb(40, 120, 30), 5)) { g.DrawBezier(stem, 42, 58, 46, 30, 60, 14, 70, 10); g.DrawBezier(stem, 78, 60, 76, 34, 72, 18, 70, 10); }
                    using (var leaf = new LinearGradientBrush(new Rectangle(66, 2, 30, 16), Color.FromArgb(120, 200, 70), Color.FromArgb(30, 110, 30), 45f))
                        g.FillEllipse(leaf, 68, 4, 28, 12);
                    foreach (var c in new[] { new Point(40, 62), new Point(78, 64) })
                        Ball(g, c.X, c.Y, 19, Color.FromArgb(255, 70, 70), Color.FromArgb(120, 0, 10));
                    break;
                case Sym.Lemon:
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(16, 16, 88, 58);
                        using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(255, 250, 140), SurroundColors = new[] { Color.FromArgb(220, 170, 0) }, CenterPoint = new PointF(48, 34) })
                            g.FillPath(b, path);
                    }
                    using (var tip = new SolidBrush(Color.FromArgb(220, 180, 20))) { g.FillEllipse(tip, 8, 38, 14, 12); g.FillEllipse(tip, 98, 38, 14, 12); }
                    using (var hl = new SolidBrush(Color.FromArgb(150, 255, 255, 230))) g.FillEllipse(hl, 36, 26, 22, 10);
                    break;
                case Sym.Bell:
                    using (var path = new GraphicsPath())
                    {
                        path.AddBezier(60, 6, 34, 6, 34, 36, 30, 56); path.AddLine(30, 56, 18, 70); path.AddLine(18, 70, 102, 70); path.AddLine(102, 70, 90, 56);
                        path.AddBezier(90, 56, 86, 36, 86, 6, 60, 6);
                        using (var b = new LinearGradientBrush(new Rectangle(18, 6, 84, 66), Color.FromArgb(255, 240, 150), Color.FromArgb(170, 110, 10), 0f))
                        {
                            var blend = new ColorBlend { Colors = new[] { Color.FromArgb(150, 95, 5), Color.FromArgb(255, 245, 170), Color.FromArgb(230, 170, 30), Color.FromArgb(130, 80, 0) }, Positions = new[] { 0f, 0.35f, 0.6f, 1f } };
                            b.InterpolationColors = blend; g.FillPath(b, path);
                        }
                    }
                    Ball(g, 60, 76, 9, Color.FromArgb(255, 230, 120), Color.FromArgb(150, 100, 0));
                    break;
                case Sym.Bar:
                    using (var b = new LinearGradientBrush(new Rectangle(8, 20, 104, 48), Color.FromArgb(70, 70, 80), Color.FromArgb(10, 10, 14), 90f))
                        g.FillRectangle(b, 8, 20, 104, 48);
                    using (var p = new Pen(Color.FromArgb(230, 180, 40), 5)) g.DrawRectangle(p, 10, 22, 100, 44);
                    using (var f = new Font("Arial Black", 30f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("BAR", f, Brushes.White, new RectangleF(8, 22, 104, 46), sf);
                    break;
                case Sym.Seven:
                    using (var path = new GraphicsPath())
                    using (var ff = new FontFamily("Arial Black"))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        path.AddString("7", ff, (int)FontStyle.Bold, 86f, new RectangleF(0, -4, 120, 96), sf);
                        using (var b = new LinearGradientBrush(new Rectangle(20, 0, 80, 88), Color.FromArgb(255, 90, 80), Color.FromArgb(150, 0, 10), 90f)) g.FillPath(b, path);
                        using (var p = new Pen(Color.FromArgb(240, 190, 50), 4)) g.DrawPath(p, path);
                    }
                    break;
                case Sym.Diamond:
                    var pts = new[] { new PointF(60, 6), new PointF(100, 34), new PointF(60, 82), new PointF(20, 34) };
                    using (var b = new LinearGradientBrush(new Rectangle(20, 6, 80, 76), Color.FromArgb(200, 255, 255), Color.FromArgb(20, 120, 200), 60f)) g.FillPolygon(b, pts);
                    using (var facet = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) g.FillPolygon(facet, new[] { new PointF(60, 6), new PointF(76, 34), new PointF(44, 34) });
                    using (var facet = new SolidBrush(Color.FromArgb(70, 0, 40, 90))) g.FillPolygon(facet, new[] { new PointF(60, 82), new PointF(76, 34), new PointF(100, 34) });
                    using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), 2)) { g.DrawPolygon(p, pts); g.DrawLine(p, 20, 34, 100, 34); }
                    break;
            }
        }
        // a glossy ball: lit from the upper left, with a hard highlight
        static void Ball(Graphics g, float cx, float cy, float r, Color light, Color dark)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                using (var b = new PathGradientBrush(path) { CenterColor = light, SurroundColors = new[] { dark }, CenterPoint = new PointF(cx - r * 0.35f, cy - r * 0.35f) }) g.FillPath(b, path);
            }
            using (var hl = new SolidBrush(Color.FromArgb(200, 255, 255, 255))) g.FillEllipse(hl, cx - r * 0.55f, cy - r * 0.6f, r * 0.45f, r * 0.3f);
        }

        // reel windows: left edge of each, and the box they sit in
        static readonly int[] ReelX = { 22, 60, 98 };
        const int ReelW = 38, ReelTop = 36, ReelH = 54;

        int[] Draw(Spin sp, int f, int loopFrame)
        {
            int total = 2 * SpinFrames;
            double lt = loopFrame / (double)total;   // where in the loop (0..1), for the bulbs and the room
            int done = sp.StopAt[2] + 1;
            bool result = f >= done;
            double since = (f - done) * Ms / 1000.0;   // seconds since the last reel stopped
            using (var bmp = new Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // the casino behind: dark and warm, with soft lights out of focus
                    using (var b = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(40, 8, 20), Color.FromArgb(12, 4, 10), 90f)) g.FillRectangle(b, 0, 0, W, H);
                    var bokeh = new[] { new PointF(6, 20), new PointF(154, 30), new PointF(10, 100), new PointF(150, 110), new PointF(4, 60) };
                    for (int i = 0; i < bokeh.Length; i++)
                        using (var bb = new SolidBrush(Color.FromArgb(40 + (int)(20 * Math.Sin(2 * Math.PI * (lt + i * 0.2))), 255, 170, 60))) g.FillEllipse(bb, bokeh[i].X - 6, bokeh[i].Y - 6, 12, 12);
                    // the cabinet: glossy red with a highlight down its front
                    var cab = new Rectangle(12, 4, 130, 122);
                    using (var b = new LinearGradientBrush(cab, Color.FromArgb(200, 20, 30), Color.FromArgb(90, 0, 10), 0f))
                    {
                        b.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(110, 0, 12), Color.FromArgb(230, 40, 50), Color.FromArgb(170, 10, 25), Color.FromArgb(90, 0, 10) }, Positions = new[] { 0f, 0.25f, 0.6f, 1f } };
                        g.FillRectangle(b, cab);
                    }
                    // the marquee: a gold panel with a sign, ringed by bulbs chasing round (all flashing in a jackpot)
                    var marquee = new Rectangle(18, 6, 118, 24);
                    using (var b = new LinearGradientBrush(marquee, Color.FromArgb(255, 220, 110), Color.FromArgb(150, 90, 10), 90f)) g.FillRectangle(b, marquee);
                    using (var b = new SolidBrush(Color.FromArgb(60, 10, 10))) g.FillRectangle(b, 26, 10, 102, 16);
                    using (var font = new Font("Arial Black", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    {
                        bool flashSign = sp.Jackpot && result && ((int)(since * 6)) % 2 == 0;
                        g.DrawString("LUCKY 7s", font, new SolidBrush(flashSign ? Color.White : Color.FromArgb(255, 210, 60)), new RectangleF(26, 10, 102, 17), sf);
                    }
                    for (int i = 0; i < 16; i++)
                    {
                        float bx = 20 + i * 7.6f;
                        bool on = sp.Jackpot && result ? ((int)(since * 8)) % 2 == 0 : (i + (int)(lt * total * 0.5)) % 3 == 0;
                        Bulb(g, bx, 7, on); Bulb(g, bx, 28, !on || (sp.Jackpot && result));
                    }
                    // the reel box: chrome bezel, the three reels, the payline, glass glare
                    var box = new Rectangle(18, 32, 118, 62);
                    using (var b = new LinearGradientBrush(box, Color.FromArgb(240, 240, 245), Color.FromArgb(90, 90, 100), 90f))
                    {
                        b.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(250, 250, 255), Color.FromArgb(120, 120, 130), Color.FromArgb(220, 220, 230), Color.FromArgb(70, 70, 80) }, Positions = new[] { 0f, 0.45f, 0.55f, 1f } };
                        g.FillRectangle(b, box);
                    }
                    for (int r = 0; r < 3; r++) Reel(g, sp, r, f, result && sp.Win > 0, since);
                    using (var p = new Pen(Color.FromArgb(result && sp.Win > 0 && ((int)(since * 5)) % 2 == 0 ? 255 : 170, 255, 30, 30), 1.5f)) g.DrawLine(p, 20, ReelTop + ReelH / 2, 134, ReelTop + ReelH / 2);
                    using (var glare = new LinearGradientBrush(new Rectangle(20, 36, 114, 54), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 30f))
                        g.FillPolygon(glare, new[] { new PointF(22, 36), new PointF(64, 36), new PointF(40, 90), new PointF(22, 90) });
                    // the win display: red LED numbers on black
                    var disp = new Rectangle(44, 98, 66, 14);
                    g.FillRectangle(Brushes.Black, disp);
                    using (var p = new Pen(Color.FromArgb(150, 150, 160))) g.DrawRectangle(p, disp);
                    int shownWin = result ? (int)Math.Min(sp.Win, sp.Win * Math.Min(1, since / 1.2)) : 0;   // counting up
                    using (var font = new Font("Consolas", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                        g.DrawString("WIN " + shownWin.ToString("0000"), font, new SolidBrush(Color.FromArgb(255, 60, 40)), 47, 98);
                    // the coin tray
                    using (var b = new LinearGradientBrush(new Rectangle(30, 114, 94, 10), Color.FromArgb(60, 60, 66), Color.FromArgb(15, 15, 18), 90f)) g.FillRectangle(b, 30, 114, 94, 10);
                    // the lever: a chrome arm with a red ball, pulled down and springing back
                    double pull = f < 2 ? 0 : f < 5 ? (f - 1) / 3.0 : f < 10 ? 1 - (f - 5) / 5.0 : 0;
                    double ang = -1.1 + 2.0 * pull;   // up and back at rest, down when pulled
                    float px = 146, py = 72, lx = px + (float)(Math.Sin(ang) * 6), ly = py - (float)(Math.Cos(ang) * 44);
                    using (var b = new LinearGradientBrush(new Rectangle(140, 60, 14, 24), Color.FromArgb(240, 240, 245), Color.FromArgb(80, 80, 90), 0f)) g.FillRectangle(b, 141, 62, 12, 22);
                    using (var p = new Pen(Color.FromArgb(220, 220, 228), 3f)) g.DrawLine(p, px, py, lx, ly);
                    using (var p = new Pen(Color.FromArgb(120, 120, 130), 1f)) g.DrawLine(p, px + 1, py, lx + 1, ly);
                    Ball(g, lx, ly, 6, Color.FromArgb(255, 90, 80), Color.FromArgb(120, 0, 0));
                    // a jackpot: JACKPOT! glowing over the reels, a golden flash, coins showering down
                    if (sp.Jackpot && result)
                    {
                        int flash = (int)(60 * Math.Max(0, Math.Sin(since * 10)));
                        using (var b = new SolidBrush(Color.FromArgb(flash, 255, 220, 120))) g.FillRectangle(b, 0, 0, W, H);
                        using (var path = new GraphicsPath())
                        using (var ff = new FontFamily("Arial Black"))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        {
                            float scale = (float)(1 + 0.08 * Math.Sin(since * 9));
                            path.AddString("JACKPOT!", ff, (int)FontStyle.Bold, 24f * scale, new RectangleF(0, 44, W, 30), sf);
                            for (int k = 6; k >= 1; k--) using (var p = new Pen(Color.FromArgb(30, 255, 200, 40), k * 2.2f) { LineJoin = LineJoin.Round }) g.DrawPath(p, path);
                            using (var b = new LinearGradientBrush(new Rectangle(0, 46, W, 26), Color.FromArgb(255, 255, 200), Color.FromArgb(230, 140, 0), 90f)) g.FillPath(b, path);
                            using (var p = new Pen(Color.FromArgb(150, 60, 0), 1.2f)) g.DrawPath(p, path);
                        }
                        var crnd = new Random(5);
                        for (int i = 0; i < 26; i++)
                        {
                            double x0 = 10 + crnd.NextDouble() * 140, delay = crnd.NextDouble() * 1.2, tt = since - delay;
                            if (tt < 0) continue;
                            double cy = -8 + 40 * tt + 70 * tt * tt, cx = x0 + 12 * Math.Sin(tt * 3 + i);
                            if (cy > H + 8) continue;
                            float wobble = (float)Math.Abs(Math.Cos(tt * 9 + i));
                            var rc = new RectangleF((float)cx - 4 * wobble, (float)cy - 4, 8 * wobble + 0.5f, 8);
                            using (var b = new LinearGradientBrush(new RectangleF((float)cx - 4, (float)cy - 4, 8, 8), Color.FromArgb(255, 245, 160), Color.FromArgb(190, 120, 0), 45f)) g.FillEllipse(b, rc);
                        }
                    }
                }
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
                var px2 = new int[W * H];
                Marshal.Copy(data.Scan0, px2, 0, px2.Length);
                bmp.UnlockBits(data);
                for (int k = 0; k < px2.Length; k++)
                {   // a lens's vignette
                    int x = k % W, y = k / W; double vx = (x - W / 2.0) / (W / 2.0), vy = (y - H / 2.0) / (H / 2.0), v = 1 - 0.28 * Math.Max(0, Math.Min(1, (vx * vx + vy * vy - 0.5) / 1.1));
                    int c = px2[k];
                    px2[k] = (int)((c >> 16 & 255) * v) << 16 | (int)((c >> 8 & 255) * v) << 8 | (int)((c & 255) * v);
                }
                return px2;
            }
        }

        static void Bulb(Graphics g, float x, float y, bool on)
        {
            if (on) using (var glow = new SolidBrush(Color.FromArgb(70, 255, 230, 120))) g.FillEllipse(glow, x - 4, y - 4, 8, 8);
            Ball(g, x, y, 2.2f, on ? Color.FromArgb(255, 255, 220) : Color.FromArgb(150, 110, 60), on ? Color.FromArgb(255, 190, 40) : Color.FromArgb(70, 40, 10));
        }

        // one reel: a white cylinder carrying the symbols, curving away top and bottom, blurred while it spins; the
        // winning symbols pulse
        static void Reel(Graphics g, Spin sp, int r, int f, bool win, double since)
        {
            var rect = new Rectangle(ReelX[r], ReelTop, ReelW, ReelH);
            var state = g.Save();
            g.SetClip(rect);
            using (var b = new LinearGradientBrush(rect, Color.White, Color.Gray, 90f))
            {
                b.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(120, 120, 120), Color.FromArgb(250, 250, 248), Color.FromArgb(250, 250, 248), Color.FromArgb(110, 110, 110) }, Positions = new[] { 0f, 0.35f, 0.65f, 1f } };
                g.FillRectangle(b, rect);
            }
            double pos = ReelAt(sp, r, f);
            bool spinning = Spinning(sp, r, f);
            double speed = spinning ? Math.Abs(ReelAt(sp, r, f + 1) - pos) : 0;
            int copies = spinning ? Math.Min(6, 1 + (int)(speed * 1.5)) : 1;
            int mid = ReelTop + ReelH / 2;
            for (int c = 0; c < copies; c++)
            {
                double p = pos - c * speed / Math.Max(1, copies) * 0.8;   // motion blur: faint copies trailing
                int baseIdx = (int)Math.Floor(p);
                for (int k = -2; k <= 2; k++)
                {
                    int idx = ((baseIdx + k) % Strip.Length + Strip.Length) % Strip.Length;
                    double off = (baseIdx + k - p);            // symbols from the payline (+ below)
                    double ang = off * 0.62;                   // round the cylinder
                    if (Math.Abs(ang) > 1.5) continue;
                    float y = mid + (float)(Math.Sin(ang) * 30), hgt = (float)(22 * Math.Cos(ang));
                    float scale = win && k == 0 && Math.Abs(off) < 0.1 ? (float)(1 + 0.12 * Math.Max(0, Math.Sin(since * 10))) : 1f;
                    var img = art[(int)Strip[idx]];
                    var dst = new RectangleF(ReelX[r] + 4 - 15 * (scale - 1), y - hgt / 2 * scale, 30 * scale, hgt * scale);
                    float alpha = copies > 1 ? 1f / copies * 1.6f : 1f;
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = Math.Min(1f, alpha) };
                        float shade = (float)(0.55 + 0.45 * Math.Cos(ang));
                        cm.Matrix00 = cm.Matrix11 = cm.Matrix22 = shade;
                        ia.SetColorMatrix(cm);
                        g.DrawImage(img, Rectangle.Round(dst), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            g.Restore(state);
            using (var p = new Pen(Color.FromArgb(60, 60, 66), 1)) g.DrawRectangle(p, rect);
        }
    }

    // A calm aquarium filling the Tiivoo 2's whole 160 x 128 screen, made to look like a photograph: deep blue-green
    // water with hazy depth, light shafts and rippling caustic light on fine gravel, drifting specks and a soft
    // vignette. The fish, plants, ornament and starfish are the user's own photos, cut out (Aquarium\ beside the
    // program, not part of the release): a blue tang and a clownfish glide slowly back and forth, turning edge-on at the
    // ends; plants at the back sway; a pirate skeleton at a ship's wheel sends up a trickle of bubbles and twice a loop a
    // burst, rocking harder as each lets go and making the plants lean; a starfish clings to the front glass. Without
    // the photos it falls back to drawn fish. Everything moves in whole cycles of the loop, so it plays seamlessly;
    // each new loop rearranges the tank (made while the last one plays).
    class Aquarium128 : Loop128
    {
        const int W = Tiivoo2.Wide, H = N, Frames = 80, Ms = 140, Floor = 108;
        int round;
        public override bool Photo { get { return true; } }

        class Canvas
        {
            public double[] R = new double[W * H], G = new double[W * H], B = new double[W * H];
            public void Blend(int x, int y, double r, double g, double b, double a)
            {
                if (x < 0 || x >= W || y < 0 || y >= H || a <= 0) return;
                if (a > 1) a = 1;
                int k = y * W + x; R[k] += (r - R[k]) * a; G[k] += (g - G[k]) * a; B[k] += (b - B[k]) * a;
            }
            public void Blend(int x, int y, int c, double a) { Blend(x, y, c >> 16 & 255, c >> 8 & 255, c & 255, a); }
            public void Scale(int x, int y, double f)
            {
                if (x < 0 || x >= W || y < 0 || y >= H) return;
                int k = y * W + x; R[k] *= f; G[k] *= f; B[k] *= f;
            }
            public void Add(int x, int y, double r, double g, double b)
            {
                if (x < 0 || x >= W || y < 0 || y >= H) return;
                int k = y * W + x; R[k] += r; G[k] += g; B[k] += b;
            }
            public Canvas Copy() { var c = new Canvas(); Array.Copy(R, c.R, R.Length); Array.Copy(G, c.G, G.Length); Array.Copy(B, c.B, B.Length); return c; }
            public int[] Pixels()
            {
                var px = new int[W * H];
                for (int k = 0; k < px.Length; k++)
                    px[k] = (int)Math.Max(0, Math.Min(255, R[k])) << 16 | (int)Math.Max(0, Math.Min(255, G[k])) << 8 | (int)Math.Max(0, Math.Min(255, B[k]));
                return px;
            }
        }

        class Fish { public int Kind; public double Cx, Cy, Ax, Ay, Phase; public Sprite Pic; }
        Fish[] fish; Canvas still; int ornX, plantX; double burst; double[,] specks;

        // A picture cut out of a photo (PNG with transparency, facing right), shrunk to its size in the tank, its
        // colors premultiplied by its transparency so it scales and blends without fringes. The aquarium's photos are
        // the user's own, kept beside the program in Aquarium\ (not part of the release).
        class Sprite
        {
            public int W, H; public double[] R, G, B, A;
            public double AvgR, AvgG, AvgB;   // its main color, from its solid middle
            public static Sprite Load(string file, int width, double taller = 1)
            {
                string path = System.IO.Path.Combine(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Aquarium"), file);
                if (!System.IO.File.Exists(path)) return null;
                try
                {
                    using (var src = new Bitmap(path))
                    {
                        int h = Math.Max(1, (int)Math.Round(src.Height * (double)width / src.Width * taller));
                        using (var bmp = new Bitmap(width, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                                g.DrawImage(src, new Rectangle(0, 0, width, h));
                            }
                            var s = new Sprite { W = width, H = h, R = new double[width * h], G = new double[width * h], B = new double[width * h], A = new double[width * h] };
                            for (int y = 0; y < h; y++)
                                for (int x = 0; x < width; x++)
                                {
                                    var c = bmp.GetPixel(x, y); double a = c.A / 255.0; int k = y * width + x;
                                    s.A[k] = a; s.R[k] = c.R * a; s.G[k] = c.G * a; s.B[k] = c.B * a;
                                }
                            double n = 0;
                            for (int y = h / 4; y < h * 3 / 4; y++)
                                for (int x = width / 4; x < width * 3 / 4; x++)
                                {
                                    int k = y * width + x; if (s.A[k] < 0.9) continue;
                                    double pr = s.R[k] / s.A[k], pg = s.G[k] / s.A[k], pb = s.B[k] / s.A[k];
                                    double mx = Math.Max(pr, Math.Max(pg, pb)), mn = Math.Min(pr, Math.Min(pg, pb));
                                    double w = mx <= 0 ? 0 : Math.Pow((mx - mn) / mx, 2) * mx / 255;   // its strongest colors count most
                                    s.AvgR += pr * w; s.AvgG += pg * w; s.AvgB += pb * w; n += w;
                                }
                            if (n > 0) { s.AvgR /= n; s.AvgG /= n; s.AvgB /= n; }
                            return s;
                        }
                    }
                }
                catch (Exception) { return null; }
            }
            // bilinear, premultiplied; false outside the picture
            public bool Sample(double x, double y, out double r, out double g, out double b, out double a)
            {
                r = g = b = a = 0;
                if (x < -0.5 || y < -0.5 || x > W - 0.5 || y > H - 0.5) return false;
                x = Math.Max(0, Math.Min(W - 1.001, x)); y = Math.Max(0, Math.Min(H - 1.001, y));
                int x0 = (int)x, y0 = (int)y; double fx = x - x0, fy = y - y0;
                for (int k = 0; k < 4; k++)
                {
                    int xx = x0 + (k & 1), yy = y0 + (k >> 1); if (xx >= W) xx = W - 1; if (yy >= H) yy = H - 1;
                    double w = ((k & 1) == 1 ? fx : 1 - fx) * ((k >> 1) == 1 ? fy : 1 - fy); int i = yy * W + xx;
                    r += R[i] * w; g += G[i] * w; b += B[i] * w; a += A[i] * w;
                }
                return a > 0.003;
            }
        }
        static Sprite clown, babyClown, blueTang, starfish, pirate, plants;

        // a fish's place and heading at a moment of the loop. Most swim back and forth along a gentle wave; the blue tang
        // lingers on one side, hovering and turning about, then glides across to linger on the other. vx: -1..1, which
        // way it faces (near 0 only while it turns round)
        static void FishAt(Fish f, double t, out double x, out double y, out double vx)
        {
            if (f.Kind == 11)
            {
                Func<double, double> px = tt =>
                {
                    double side = Math.Tanh(2.2 * Math.Sin(2 * Math.PI * (tt + f.Phase))) / Math.Tanh(2.2);   // mostly at one end or the other
                    return f.Cx + f.Ax * side + 6 * Math.Sin(2 * Math.PI * (tt * 3 + f.Phase * 2));        // hovering where it lingers
                };
                x = px(t);
                double v = (px(t + 0.002) - px(t - 0.002)) / 0.004;
                vx = Math.Tanh(v / 40);
                y = f.Cy + f.Ay * Math.Sin(2 * Math.PI * (t * 2 + f.Phase * 3));
                return;
            }
            double a = 2 * Math.PI * (t + f.Phase);
            if (f.Kind == 10 || f.Kind == 12)
            {   // clownfish: a figure-eight around their spot by the plants, dipping and rising as they go, with a little
                // dart in each pass instead of an even glide
                double s = a + 0.35 * Math.Sin(2 * a);
                x = f.Cx + f.Ax * Math.Sin(s); y = f.Cy + f.Ay * Math.Sin(2 * s);
                vx = Math.Cos(s); return;
            }
            x = f.Cx + f.Ax * Math.Sin(a); y = f.Cy + f.Ay * Math.Sin(2 * a + f.Phase * 3); vx = Math.Cos(a);
        }
        static bool spritesTried;
        static readonly object spriteGate = new object();
        static void LoadSprites()
        {
            lock (spriteGate)
            {
                if (spritesTried) return;
                spritesTried = true;
                clown = Sprite.Load("clownfish.png", 50);
                babyClown = Sprite.Load("clownfish.png", 33);
                blueTang = Sprite.Load("blue_tang.png", 64);
                starfish = Sprite.Load("starfish.png", 34);
                pirate = Sprite.Load("pirate_wheel.png", 46);
                plants = Sprite.Load("plants.png", 88, 1.32);
            }
        }

        // a photo fish swimming: facing its way, its back half swaying, a touch of the water's tint and light from
        // above. As it turns it swings round toward the viewer: the picture narrows, the nearer end grows and the far
        // end shrinks and darkens, and a rounded body (its cross-section, `thick` of its height across) shows behind
        // so it never goes flat
        static void DrawSprite(Canvas c, Sprite s, double fx, double fy, double vx, double t, double phase, double thick)
        {
            int dir = vx >= 0 ? 1 : -1;
            double squash = Math.Max(0.1, Math.Min(1, Math.Abs(vx) * 2.2)), turn = Math.Sqrt(1 - squash * squash);
            double beat = Math.Sin(2 * Math.PI * (t * 7 + phase)) * (0.4 + 0.6 * squash);
            // the body seen end-on: a shaded oval in the fish's own color, lit from the upper left
            if (turn > 0.05)
            {
                double ew = Math.Max(1, s.H * thick * turn) / 2, eh = s.H * 0.38 * (1 + 0.12 * turn);
                for (int y = (int)(fy - eh) - 1; y <= fy + eh + 1; y++)
                    for (int x = (int)(fx - ew) - 1; x <= fx + ew + 1; x++)
                    {
                        double u = (x - fx) / ew, v = (y - fy) / eh, e = u * u + v * v;
                        if (e > 1) continue;
                        double nz = Math.Sqrt(1 - e), lit = 0.45 + 0.75 * Math.Max(0, -0.45 * u - 0.55 * v + 0.7 * nz);
                        double edge = Math.Min(1, (1 - e) * 6);
                        c.Blend(x, y, s.AvgR * lit * 0.95 + 1, s.AvgG * lit * 0.95 + 5, s.AvgB * lit * 0.95 + 7, Math.Min(1, turn * 1.6) * edge);
                    }
            }
            int hw = (int)Math.Ceiling(s.W * squash / 2) + 2, hh = (int)(s.H / 2 * 1.3) + 3;
            for (int y = (int)fy - hh; y <= fy + hh; y++)
                for (int x = (int)fx - hw; x <= fx + hw; x++)
                {
                    double sx = (x - fx) * dir / squash + s.W / 2.0;   // 0 at the tail, W at the snout
                    double along = sx / s.W * 2 - 1;                    // -1 tail .. 1 snout
                    double near = 1 + 0.28 * turn * along;              // the snout swings toward the viewer: larger
                    double back = Math.Max(0, 1 - sx / (s.W * 0.55));
                    double sy = (y - fy) / near + s.H / 2.0 - beat * 1.6 * back * back;
                    double r, g, b, a;
                    if (!s.Sample(sx, sy, out r, out g, out b, out a)) continue;
                    double lit = (1.06 - 0.16 * (sy / s.H)) * (1 - 0.3 * turn * (1 - along) / 2);   // lighter on top; the far end in shadow
                    r = r / a * lit; g = g / a * lit; b = b / a * lit;
                    r = r * 0.9 + 6 * 0.1; g = g * 0.9 + 60 * 0.1; b = b * 0.9 + 80 * 0.1;   // a touch of the water's tint
                    c.Blend(x, y, r, g, b, a);
                }
        }

        // the starfish on the front glass, in clear focus, turned to `angle`. loose: how far one arm (at armAngle, in the
        // picture) has come away from the glass, -1..1 as it bobs in the current. That arm is lifted out of the picture
        // and swung about where it joins the body, curling more toward its tip, shorter-looking and darker as it lifts;
        // the glass shows where it was.
        static void DrawStarfish(Canvas c, Sprite s, double cx, double cy, double angle, double armAngle, double loose)
        {
            double ca = Math.Cos(angle), sa = Math.Sin(angle);
            double mx = s.W * 0.53, my = s.H * 0.56;   // where its arms meet, in the picture
            double reachArm = Math.Max(s.W, s.H) * 0.55, root = reachArm * 0.3;
            double rx = root * Math.Cos(armAngle), ry = root * Math.Sin(armAngle);   // the arm's root, from the middle
            // the arm's share of the picture, from `from` (times the root's distance) out; the body leaves a little more
            // than the arm takes, so they overlap at the root with no gap
            Func<double, double, double, bool> inArm = (px, py, from) =>
            {
                double d = Math.Sqrt(px * px + py * py);
                double off = Math.Abs(Math.IEEERemainder(Math.Atan2(py, px) - armAngle, 2 * Math.PI));
                return d > root * from && off < 0.62;
            };
            int rad = (int)Math.Ceiling(reachArm * 1.3) + 2;
            for (int y = (int)cy - rad; y <= cy + rad; y++)
                for (int x = (int)cx - rad; x <= cx + rad; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    double px = dx * ca + dy * sa, py = -dx * sa + dy * ca;   // from its middle, in the picture's turn
                    double r, g, b, a;
                    // the body (and, when nothing is loose, every arm) where it is
                    if (loose == 0 || !inArm(px, py, 1.15))
                        if (s.Sample(px + mx, py + my, out r, out g, out b, out a))
                            c.Blend(x, y, r / a * 1.2, g / a * 1.12, b / a * 1.1, a);
                    if (loose == 0) continue;
                    // the loose arm: undo its swing about the root to find where in the picture this point came from
                    double ax = px - rx, ay = py - ry, d = Math.Sqrt(ax * ax + ay * ay), len = reachArm - root;
                    double f = Math.Min(1.2, d / len), turn = loose * (0.35 + 0.65 * f) * 0.85, grow = 1 + (0.18 + 0.15 * Math.Abs(loose)) * f;
                    double ct = Math.Cos(-turn), st = Math.Sin(-turn);
                    double sx = rx + (ax * ct - ay * st) * grow, sy = ry + (ax * st + ay * ct) * grow;
                    if (!inArm(sx, sy, 0.75)) continue;
                    if (!s.Sample(sx + mx, sy + my, out r, out g, out b, out a)) continue;
                    double shade = 1 - (0.25 + 0.2 * Math.Abs(loose)) * f;   // lifted off the glass, out of the light
                    c.Blend(x, y, r / a * 1.2 * shade, g / a * 1.12 * shade, b / a * 1.1 * shade, a);
                }
        }
        // where the starfish is on the glass: it creeps a couple of pixels between loops, too slowly to notice, wandering
        // all over the glass and turning a little as it goes; now and then an upper arm comes loose for a while
        double starX = 120, starY = 30, starAngle = 0.35, starHeading = 2.6;
        bool armLoose; double armAt;
        // the loose arm, -1..1: away from the glass and rippling in the current; least lifted at the start of the loop
        // (when it lets go) and kept off the glass all through, so the loop repeats without it snapping back
        static double Loose(double t) { return 0.62 - 0.3 * Math.Cos(2 * Math.PI * t) + 0.16 * Wave(t, 3, 0.1) + 0.08 * Wave(t, 5, 0.6); }
        void CreepStarfish(Random rnd)
        {
            starHeading += (rnd.NextDouble() - 0.5) * 0.5;
            double nx = starX + 2 * Math.Cos(starHeading), ny = starY + 1.6 * Math.Sin(starHeading);   // about 2 pixels a loop
            if (nx < 20 || nx > W - 20) { starHeading = Math.PI - starHeading; nx = starX; }
            if (ny < 20 || ny > Floor - 14) { starHeading = -starHeading; ny = starY; }
            starX = nx; starY = ny;
            starAngle += (rnd.NextDouble() - 0.5) * 0.02;
        }

        static double Hash(int x, int y) { unchecked { int h = x * 374761393 + y * 668265263; h = (h ^ (h >> 13)) * 1274126177; return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0; } }
        static double Wave(double t, int cycles, double phase) { return Math.Sin(2 * Math.PI * (t * cycles + phase)); }
        static double Smooth(double e0, double e1, double x) { double t = Math.Max(0, Math.Min(1, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); }
        static int FloorAt(int x) { return Floor + (int)Math.Round(2 * Math.Sin(x * 0.05 + 1.3) + 1.2 * Math.Sin(x * 0.17)); }

        // The tank is set up once and keeps its fish, plants and ornament, so a new loop takes over without a jump.
        // Loops alternate: all the starfish's arms on the glass, then one upper arm loose in the current. Sending a loop
        // takes about 35 seconds, during which the last one keeps playing, so with these hold times the arm stays on the
        // glass for about 80 seconds and comes loose for about 40: once every 2 minutes.
        bool armWasLoose = true;
        public override List<int[]> Next(out int ms, out double play)
        {
            ms = Ms;
            var rnd = new Random(Environment.TickCount + 7919 * round++);
            LoadSprites();
            if (still == null)
            {
                burst = rnd.NextDouble();
                ornX = 1 + rnd.Next(4);                  // the pirate ornament at the left side
                plantX = W - 86 + rnd.Next(4);           // the plants at the right side
                specks = new double[30, 4];
                for (int i = 0; i < 30; i++) { specks[i, 0] = rnd.NextDouble() * W; specks[i, 1] = rnd.NextDouble() * Floor; specks[i, 2] = rnd.NextDouble(); specks[i, 3] = 0.4 + rnd.NextDouble(); }
                if (clown != null && blueTang != null)
                {
                    double cy = 64 + rnd.Next(6), ph = rnd.NextDouble();
                    fish = new[]
                    {   // the user's photos: a blue tang higher up, a clownfish lower, and a smaller one following it.
                        // The tang crosses the whole tank against the clownfish's timing (half a loop apart) while the
                        // clownfish loop around near the plants on the right, so the three never move in step
                        new Fish { Kind = 11, Cx = 80, Cy = 30 + rnd.Next(8), Ax = 40, Ay = 3, Phase = ph + 0.4 + rnd.NextDouble() * 0.2, Pic = blueTang },
                        new Fish { Kind = 12, Cx = 100, Cy = cy + 12, Ax = 32, Ay = 9, Phase = ph - 0.12, Pic = babyClown },
                        new Fish { Kind = 10, Cx = 100, Cy = cy, Ax = 32, Ay = 9, Phase = ph, Pic = clown },
                    };
                }
                else
                    fish = new[]
                    {   // no photos: the drawn ones
                        new Fish { Kind = 0, Cx = 80, Cy = 34 + rnd.Next(12), Ax = 46, Ay = 4, Phase = rnd.NextDouble() },   // goldfish
                        new Fish { Kind = 2, Cx = 80, Cy = 64 + rnd.Next(8), Ax = 38, Ay = 5, Phase = rnd.NextDouble() },    // angelfish
                    };
                still = Backdrop(rnd);
            }
            CreepStarfish(rnd);
            NewLeans(rnd);
            armLoose = !armWasLoose; armWasLoose = armLoose;
            if (armLoose) armAt = rnd.Next(2) == 0 ? -1.48 : -2.88;   // the arm pointing up, or the one up and to the left
            play = armLoose ? 4 : 44;
            var list = new List<int[]>();
            for (int f = 0; f < Frames; f++) list.Add(Draw(f / (double)Frames));
            return list;
        }
        // what doesn't move: the water with its depth haze, blurred plants at the back, the gravel bed
        Canvas Backdrop(Random rnd)
        {
            var c = new Canvas();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double d = y / (double)Floor, side = Math.Abs(x - W / 2.0) / (W / 2.0);
                    double r = 28 - 22 * d, g = 128 - 62 * d, b = 150 - 52 * d;
                    double haze = 0.85 + 0.15 * (1 - side * side);
                    int k = y * W + x; c.R[k] = r * haze; c.G[k] = g * haze; c.B[k] = b * haze;
                }
            // far plants: dark, soft silhouettes, blurred as if out of focus
            var far = new double[W * H];
            for (int p = 0; p < 9; p++)
            {
                double bx = rnd.NextDouble() * W, h = 30 + rnd.Next(45), lean = (rnd.NextDouble() - 0.5) * 0.4;
                int blades = 3 + rnd.Next(4);
                for (int bl = 0; bl < blades; bl++)
                {
                    double off = (bl - blades / 2.0) * 2.2, curve = (rnd.NextDouble() - 0.5) * 10, len = h * (0.6 + rnd.NextDouble() * 0.4);
                    for (int s = 0; s < len; s++)
                    {
                        double f = s / len, x = bx + off + lean * s + curve * f * f, y = Floor + 2 - s;
                        double wdt = 2.2 * (1 - f) + 0.6;
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            int xi = (int)Math.Round(x) + dx; if (xi < 0 || xi >= W || y < 0) continue;
                            far[(int)y * W + xi] = Math.Max(far[(int)y * W + xi], Math.Max(0, 1 - Math.Abs(dx) / wdt));
                        }
                    }
                }
            }
            for (int pass = 0; pass < 3; pass++) far = Blur(far, 2);
            for (int k = 0; k < far.Length; k++)
            {
                double a = far[k] * 0.55;
                c.R[k] += (8 - c.R[k]) * a; c.G[k] += (62 - c.G[k]) * a; c.B[k] += (58 - c.B[k]) * a;
            }
            // the gravel bed: fine grains, light on top of each pebble, darker toward the front glass
            for (int x = 0; x < W; x++)
            {
                int top = FloorAt(x);
                for (int y = top; y < H; y++)
                {
                    double n = Hash(x, y), n2 = Hash(x / 2, y / 2 + 99), d = (y - top) / (double)(H - top);
                    double tone = 0.75 + 0.35 * n2 - 0.25 * d + (n > 0.85 ? 0.2 : n < 0.12 ? -0.25 : 0);
                    if (y == top) tone *= 0.85;
                    double hue = Hash(x + 31, y / 2) * 0.25;   // magenta grains, some pinker, some more purple
                    c.Blend(x, y, (214 - 30 * hue) * tone, (52 + 40 * hue) * tone, (168 + 50 * hue) * tone, 1);
                }
                for (int y = top - 3; y < top; y++) c.Blend(x, y, 80, 50, 100, 0.25 * (y - top + 4) / 4.0);   // where the gravel meets the water
            }
            return c;
        }

        static double[] Blur(double[] a, int r)
        {
            var o = new double[a.Length]; var tmp = new double[a.Length];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double s = 0; int n = 0;
                    for (int d = -r; d <= r; d++) { int xx = x + d; if (xx < 0 || xx >= W) continue; s += a[y * W + xx]; n++; }
                    tmp[y * W + x] = s / n;
                }
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double s = 0; int n = 0;
                    for (int d = -r; d <= r; d++) { int yy = y + d; if (yy < 0 || yy >= H) continue; s += tmp[yy * W + x]; n++; }
                    o[y * W + x] = s / n;
                }
            return o;
        }

        // caustics: the bright, shifting net of light that ripples on an aquarium floor
        static double Caustic(double x, double y, double t)
        {
            double a = Math.Sin(x * 0.21 + y * 0.13 + 2 * Math.PI * t) + Math.Sin(x * -0.17 + y * 0.23 + 2 * Math.PI * (t * 2 + 0.3)) +
                       Math.Sin(x * 0.09 - y * 0.27 + 2 * Math.PI * (-t + 0.6));
            double v = 1 - Math.Abs(a) / 1.1;
            return v <= 0 ? 0 : v * v * v;
        }

        // the skeleton's lean: it creaks from 25 degrees right to the middle, to 25 degrees left, back to the middle and
        // round again. Each move is, at random, a quick lurch (with a little overshoot and shiver) or a slow creak that
        // stutters as it goes. One round per loop, starting and ending leaning right, so the loop stays seamless.
        const double Tilt = 25 * Math.PI / 180;
        double[] leanAt, leanTo, leanFor;
        void NewLeans(Random rnd)
        {
            leanTo = new[] { 0, -Tilt, 0, Tilt };   // from leaning right: the middle, left, the middle, right again
            leanAt = new double[4]; leanFor = new double[4];
            for (int i = 0; i < 4; i++)
            {
                bool slow = rnd.NextDouble() < 0.5;
                leanFor[i] = slow ? 0.12 + rnd.NextDouble() * 0.06 : 0.025 + rnd.NextDouble() * 0.01;   // ~1.5-2 s, or ~0.3 s
                leanAt[i] = i * 0.25 + rnd.NextDouble() * (0.24 - leanFor[i]);
            }
        }
        double Lean(double t)
        {
            int i = -1;
            for (int k = 0; k < 4; k++) if (t >= leanAt[k]) i = k;
            if (i < 0) return Tilt;   // still leaning right from the round before
            double from = i == 0 ? Tilt : leanTo[i - 1], to = leanTo[i], since = t - leanAt[i], p = Math.Min(1, since / leanFor[i]);
            if (leanFor[i] < 0.05)
            {   // a lurch: out fast, overshooting a little, shivering as it stops
                double ease = 1 + 2.7 * Math.Pow(p - 1, 3) + 1.7 * Math.Pow(p - 1, 2);
                return from + (to - from) * ease + 0.03 * Math.Sin(since * 2 * Math.PI * 30) * Math.Exp(-since / 0.03);
            }
            // a creak: slow, catching and slipping on the way
            double q = p * p * (3 - 2 * p);
            q += 0.045 * Math.Sin(q * Math.PI * 9) * Math.Sin(q * Math.PI);
            return from + (to - from) * q + 0.006 * Math.Sin(since * 2 * Math.PI * 45) * (p < 1 ? 1 : 0);
        }
        // the air from the ornament: a steady trickle, and twice a loop a burst (0..1, peaking as it lets go)
        double Burst(double t)
        {
            double p = ((t - burst) % 0.5 + 0.5) % 0.5 / 0.5, s = p < 0.25 ? Math.Sin(p / 0.25 * Math.PI) : 0;
            return s * s;
        }

        // a cut-out picture standing on the gravel, its left edge at x0 and its foot at bottom, bent sideways by
        // sway(height 0..1, x) pixels; below `rigid` (a share of its height) it stays put
        // ragged: how many pixels its foot sinks unevenly into the gravel, column by column (0: a straight cut)
        static void DrawRooted(Canvas c, Sprite s, int x0, int bottom, double rigid, Func<double, double, double> sway, double haze, double ragged = 0)
        {
            int top = bottom - s.H, reach = 8;
            Func<int, double> rnd = i => { double v = Math.Sin(i * 12.9898 + 78.233) * 43758.5453; return v - Math.Floor(v); };
            var cut = new double[s.W + 2 * reach];   // where each column's foot ends: bumps of a few pixels with single stems poking lower
            for (int i = 0; i < cut.Length; i++)
            {
                double u = i / 4.0; int j = (int)Math.Floor(u); double f = u - j; f = f * f * (3 - 2 * f);
                double lump = rnd(j) * (1 - f) + rnd(j + 1) * f;
                cut[i] = bottom - ragged * (0.15 + 0.7 * lump + 0.35 * (rnd(i + 1000) - 0.5));
            }
            for (int y = top; y < bottom; y++)
            {
                double h = (bottom - y) / (double)s.H, k = h <= rigid ? 0 : Math.Pow((h - rigid) / (1 - rigid), 1.4);
                for (int x = x0 - reach; x < x0 + s.W + reach; x++)
                {
                    if (y >= cut[x - x0 + reach]) continue;
                    double dx = k == 0 ? 0 : sway(k, x);
                    double r, g, b, a;
                    if (!s.Sample(x - x0 - dx, y - top, out r, out g, out b, out a) || a < 0.2) continue;   // faint edge pixels: leftovers of the white
                    r /= a; g /= a; b /= a; a = Math.Min(1, (a - 0.2) / 0.6);
                    r = r * (1 - haze) + 10 * haze; g = g * (1 - haze) + 70 * haze; b = b * (1 - haze) + 80 * haze;   // the water between
                    double lit = 0.82 + 0.25 * h;   // more light higher up
                    c.Blend(x, y, r * lit, g * lit, b * lit, a);
                }
            }
        }

        // the pirate at the ship's wheel (pirate_wheel.png): the wheel turns back and forth on its hub (but not the
        // post standing in front of it) and the skeleton's upper body leans with it, hands on the spokes; above the
        // base it all rocks with the water. Places are shares of the picture's size, measured on the cut-out.
        static void DrawPirate(Canvas c, Sprite s, int x0, int bottom, double wheel, double body, double rock)
        {
            int top = bottom - s.H;
            double hx = 0.595 * s.W, hy = 0.564 * s.H, rimOut = 0.39 * s.W, rimIn = 0.076 * s.W;   // the hub and the wheel
            double postL = 0.48 * s.W, postR = 0.68 * s.W, postTop = 0.505 * s.H;                  // the post in front of the hub
            double px = 0.595 * s.W, py = 0.42 * s.H;                                                // the skeleton's waist
            double cw = Math.Cos(-wheel), sw = Math.Sin(-wheel), cb = Math.Cos(-body), sb = Math.Sin(-body);
            for (int y = top - 6; y < bottom; y++)
            {
                double h = (bottom - y) / (double)s.H, k = h <= 0.3 ? 0 : Math.Pow((h - 0.3) / 0.7, 1.4);
                for (int x = x0 - 8; x < x0 + s.W + 8; x++)
                {
                    double sx = x - x0 - rock * k, sy = y - top;
                    double r = 0, g = 0, b = 0, a = 0; bool got = false;
                    double ddx = sx - hx, ddy = sy - hy, dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                    bool post = sx > postL && sx < postR && sy > postTop;
                    if (!post && dist > rimIn && dist < rimOut)
                    {   // the turning wheel; where its gaps show what is behind, fall through
                        got = s.Sample(hx + ddx * cw - ddy * sw, hy + ddx * sw + ddy * cw, out r, out g, out b, out a) && a > 0.5;
                        if (got) { double over = Smooth(rimOut - 1.5, rimOut, dist); a *= 1 - over; }
                    }
                    bool upper = sy < py + 0.02 * s.H && !post;
                    if (!got && upper)
                    {   // the upper body, leaning about the waist (only the leaning one: no ghost of it standing straight)
                        double bx = sx - px, by = sy - py;
                        got = s.Sample(px + bx * cb - by * sb, py + bx * sb + by * cb, out r, out g, out b, out a);
                    }
                    if (!got && !upper) got = s.Sample(sx, sy, out r, out g, out b, out a);
                    if (!got || a < 0.2) continue;   // faint edge pixels: leftovers of the white
                    r /= a; g /= a; b /= a; a = Math.Min(1, (a - 0.2) / 0.6);
                    double lit = 0.82 + 0.25 * h;
                    c.Blend(x, y, (r * 0.96 + 0.4) * lit, (g * 0.96 + 2.8) * lit, (b * 0.96 + 3.2) * lit, a);
                }
            }
        }

        // the bubbles from the ornament: a steady trickle, and a rush of them in each burst; clear, with a bright rim
        void OrnamentBubbles(Canvas c, double t, double sx, double sy)
        {
            for (int i = 0; i < 26; i++)
            {
                bool rush = i >= 8;
                double start = rush ? burst + (i - 8) * 0.004 + ((i - 8) % 2) * 0.5 : i / 8.0;
                double p = ((t - start) % 1.0 + 1.0) % 1.0, life = rush ? 0.42 : 0.5;
                if (p > life) continue;
                double q = p / life;
                double y = sy - q * (sy - 4), x = sx + (rush ? (i * 5 % 9) - 4 : 0) * (0.4 + q) + 2.2 * Math.Sin(q * 11 + i), r = (rush ? 1.1 : 0.8) + q * 1.6 + (i % 3) * 0.25;
                for (int yy = (int)(y - r - 1); yy <= y + r + 1; yy++)
                    for (int xx = (int)(x - r - 1); xx <= x + r + 1; xx++)
                    {
                        double d = Math.Sqrt((xx - x) * (xx - x) + (yy - y) * (yy - y));
                        double ring = Math.Max(0, 1 - Math.Abs(d - r) / 0.8);
                        c.Add(xx, yy, 120 * ring * 0.7, 160 * ring * 0.7, 170 * ring * 0.7);
                        if (d < r) c.Add(xx, yy, 10, 16, 18);
                    }
                c.Blend((int)Math.Round(x - r * 0.45), (int)Math.Round(y - r * 0.45), 0xFFFFFF, 0.85);
            }
        }

        int[] Draw(double t)
        {
            var c = still.Copy();
            // light shafts from the surface, slow and soft
            for (int i = 0; i < 4; i++)
            {
                double x0 = 15 + i * 40 + 6 * Wave(t, 1, i * 0.27), w = 6 + 2 * Wave(t, 1, i * 0.4), glow = 0.55 + 0.45 * Wave(t, 1, i * 0.61);
                for (int y = 0; y < Floor; y++)
                {
                    double cx = x0 + y * 0.3, fade = (1 - y / (double)Floor) * 0.22 * glow;
                    for (int x = (int)(cx - 3 * w); x <= cx + 3 * w; x++)
                    {
                        double a = Math.Exp(-(x - cx) * (x - cx) / (2 * w * w)) * fade;
                        c.Add(x, y, 70 * a, 110 * a, 95 * a);
                    }
                }
            }
            // the surface: a bright band with moving glints, seen from below
            for (int x = 0; x < W; x++)
                for (int y = 0; y < 6; y++)
                {
                    double g = Caustic(x * 1.6, y * 4, t) * (1 - y / 6.0);
                    c.Add(x, y, 90 * g + 18 * (1 - y / 6.0), 140 * g + 30 * (1 - y / 6.0), 140 * g + 30 * (1 - y / 6.0));
                }
            // caustics on the gravel
            for (int x = 0; x < W; x++)
                for (int y = FloorAt(x); y < H; y++)
                {
                    double g = Caustic(x, y * 2.2, t) * (0.9 - 0.5 * (y - Floor) / 20.0);
                    c.Add(x, y, 120 * g, 100 * g, 120 * g);
                }
            double gust = Burst(t);
            // the plants at the back: stems bend more the higher they reach, and lean away as a burst of bubbles rises
            if (plants != null)
                DrawRooted(c, plants, plantX, Floor + 4, 0.05,
                           (k, x) => k * (3.2 * Wave(t, 1, x * 0.004) + 2.0 * Wave(t, 2, 0.3 + x * 0.006) + 1.4 * Wave(t, 3, x * 0.01 - k * 0.9)
                                          + 3.5 * gust * Math.Exp(-Math.Abs(x - ornX - 20) / 45.0)), 0.12, 7);
            // the fish's soft shadows on the gravel
            foreach (var f in fish)
            {
                double x, fy, fvx; FishAt(f, t, out x, out fy, out fvx);
                int half = f.Pic != null ? f.Pic.W / 2 : 14;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -half; dx <= half; dx++)
                    {
                        double e = dx * dx / (double)(half * half) + dy * dy / 5.0;
                        if (e < 1) c.Scale((int)Math.Round(x + dx + 6), FloorAt((int)x) + 3 + dy, 1 - 0.28 * (1 - e));
                    }
            }
            // the pirate at the ship's wheel: the base sits in the gravel; skeleton and wheel rock with the air,
            // swinging harder as each burst lets go
            if (pirate != null)
            {
                double lean = Lean(t);   // the skeleton holds a pose, then lurches to the next; the wheel turns with it
                DrawPirate(c, pirate, ornX, Floor + 7, lean * 1.1 + 0.08 * gust * Wave(t, 8, 0), lean, 0.8 * Wave(t, 1, 0.1) + 2.0 * gust * Wave(t, 8, 0));
                OrnamentBubbles(c, t, ornX + pirate.W * 0.62, Floor - pirate.H * 0.55);
            }
            foreach (var f in fish)
            {
                double x, y, vx; FishAt(f, t, out x, out y, out vx);
                if (f.Pic != null) DrawSprite(c, f.Pic, x, y, vx, t, f.Phase, f.Kind == 11 ? 0.3 : 0.48);   // kinds: 10 clownfish, 11 blue tang, 12 the small clownfish   // a blue tang is a thin fish, a clownfish rounder
                else DrawFish(c, f.Kind, x, y, vx, t);
            }
            // drifting specks catching the light, some sharp, some blurred
            for (int i = 0; i < specks.GetLength(0); i++)
            {
                double x = specks[i, 0] + 3 * Wave(t, 1, specks[i, 2]), y = specks[i, 1] + 2 * Wave(t, 1, specks[i, 2] + 0.25);
                double s = specks[i, 3];
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        double a = (dx == 0 && dy == 0 ? 0.35 : 0.08) * s;
                        c.Add((int)Math.Round(x) + dx, (int)Math.Round(y) + dy, 120 * a, 150 * a, 140 * a);
                    }
            }
            // a lens's vignette
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double dx = (x - W / 2.0) / (W / 2.0), dy = (y - H / 2.0) / (H / 2.0);
                    c.Scale(x, y, 1 - 0.32 * Smooth(0.45, 1.5, dx * dx + dy * dy));
                }
            // the starfish on the front glass, up in a corner (after the vignette: it is right at the glass)
            if (starfish != null)
                DrawStarfish(c, starfish, starX, starY, starAngle, armAt, armLoose ? Loose(t) : 0);
            return c.Pixels();
        }

        // a fish at (x, y), vx > 0 swimming right; turning as vx passes 0 (seen end-on, its body narrows)
        static void DrawFish(Canvas c, int kind, double fx, double fy, double vx, double t)
        {
            int dir = vx >= 0 ? 1 : -1;
            double squash = Math.Max(0.18, Math.Min(1, Math.Abs(vx) * 2.2));
            double a = kind == 2 ? 11 : 17, b = kind == 2 ? 11.5 : 8, tail = kind == 2 ? 9 : 14;
            double beat = Math.Sin(2 * Math.PI * t * 9 + fx * 0.04) * (0.3 + 0.7 * squash);
            int reach = (int)(a + tail + (kind == 2 ? 26 : 10));
            for (int y = (int)(fy - reach); y <= fy + reach; y++)
                for (int x = (int)(fx - reach); x <= fx + reach; x++)
                {
                    double sr = 0, sg = 0, sb = 0, cover = 0;
                    for (int sy = 0; sy < 2; sy++)
                        for (int sx = 0; sx < 2; sx++)
                        {
                            double u = (x + 0.25 + sx * 0.5 - fx) * dir / squash, v = y + 0.25 + sy * 0.5 - fy;
                            double alpha, r, g, bl;
                            if (!FishColor(kind, u, v, a, b, tail, beat, out r, out g, out bl, out alpha)) continue;
                            cover += 0.25 * alpha; sr += r * 0.25 * alpha; sg += g * 0.25 * alpha; sb += bl * 0.25 * alpha;
                        }
                    if (cover <= 0) continue;
                    c.Blend(x, y, sr / cover, sg / cover, sb / cover, cover);
                }
        }

        static bool InTri(double x, double y, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            double d1 = (x - x2) * (y1 - y2) - (x1 - x2) * (y - y2), d2 = (x - x3) * (y2 - y3) - (x2 - x3) * (y - y3), d3 = (x - x1) * (y3 - y1) - (x3 - x1) * (y - y1);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }

        // a rounded body lit from above and in front: soft light falling off toward the belly, a sheen along the back
        static void Lit(double nu, double nv, double baseR, double baseG, double baseB, double shine, out double r, out double g, out double b)
        {
            double nz = Math.Sqrt(Math.Max(0, 1 - nu * nu - nv * nv));
            double lx = -0.25, ly = -0.75, lz = 0.6;   // the light: above, a little ahead, toward the viewer
            double diff = Math.Max(0, nu * lx + nv * ly + nz * lz);
            double hz = nz * 2 * (nu * lx + nv * ly + nz * lz) - lz;   // reflection toward the viewer
            double spec = Math.Pow(Math.Max(0, hz), 18) * shine;
            double k = 0.32 + 0.85 * diff;
            r = baseR * k + 255 * spec; g = baseG * k + 255 * spec; b = baseB * k + 245 * spec;
        }

        static bool FishColor(int kind, double u, double v, double a, double b, double tail, double beat, out double r, out double g, out double bl, out double alpha)
        {
            r = g = bl = 0; alpha = 1;
            double tv = v - beat * Math.Max(0, -u - a * 0.6) * 0.3;
            double taper = 1 - 0.38 * Math.Max(0, -u / a);   // narrower toward the tail
            double bv = b * taper, e = (u / a) * (u / a) + (v / bv) * (v / bv);
            // the eye: a dark pupil in a gold or silver ring, with a wet highlight
            double ex = u - a * 0.6, ey = v + b * 0.18, ee = ex * ex + ey * ey;
            if (ee < 4.4)
            {
                if ((ex + 0.7) * (ex + 0.7) + (ey + 0.7) * (ey + 0.7) < 0.55) { r = g = bl = 255; return true; }
                if (ee < 2.4) { r = 10; g = 10; bl = 16; return true; }
                if (kind == 0) { r = 210; g = 150; bl = 70; } else { r = 200; g = 196; bl = 170; }
                return true;
            }
            if (kind == 2)
            {   // angelfish: silver with dark bars, tall see-through fins swept back, long feelers below
                bool body = e <= 1;
                bool dorsal = InTri(u, v, a * 0.35, -b * 0.75, -a * 1.2, -b * 2.4, -a * 0.95, -b * 0.25);
                bool anal = InTri(u, v, a * 0.2, b * 0.75, -a * 1.1, b * 2.3, -a * 0.9, b * 0.25);
                bool tl = u < -a * 0.8 && u > -a - tail && Math.Abs(tv) < (-u - a * 0.8) * 0.85 + 1;
                bool feeler = Math.Abs(u - a * 0.1 + (v - b) * 0.15) < 0.55 && v > b * 0.7 && v < b * 2.3;
                if (!body && !dorsal && !anal && !tl && !feeler) return false;
                // three narrow bars: through the eye, mid-body, and before the tail
                bool bar = Math.Abs(u - a * 0.6) < 1.0 || Math.Abs(u - a * 0.02) < 1.3 || Math.Abs(u + a * 0.62) < 1.2;
                if (!body)
                {   // fins: thin, glassy, with fine rays and the bars carried through
                    alpha = feeler ? 0.55 : 0.62;
                    double ray = (Math.Floor((u + 50) * 1.1) % 2 == 0) ? 1 : 0.85;
                    double f = (bar ? 0.35 : 1) * ray;
                    r = 200 * f; g = 205 * f; bl = 200 * f;
                    return true;
                }
                double sr, sg, sb;
                Lit(u / a * 0.35, v / bv * 0.45, 215, 214, 200, 0.35, out sr, out sg, out sb);   // a thin disc: lit nearly flat
                sr += (1 - Math.Abs(v / bv)) * 12; sg += (1 - Math.Abs(v / bv)) * 12;
                if (bar) { sr *= 0.25; sg *= 0.23; sb *= 0.2; }
                double sheen = 0.06 * Math.Sin(u * 2.2 + v * 0.8);   // a faint iridescence
                r = sr * (1 + sheen); g = sg * (1 + sheen * 0.5); bl = sb * (1 + sheen * 1.4);
                if (Math.Abs(u - a * 0.42) < 0.5 && Math.Abs(v) < bv * 0.7) { r *= 0.7; g *= 0.7; bl *= 0.7; }   // gill cover
                return true;
            }
            // goldfish: deep orange-red over gold, scales catching the light, flowing forked tail and soft fins
            bool inBody = e <= 1;
            bool inTail = u < -a * 0.75 && u > -a - tail && Math.Abs(tv) < (-u - a * 0.75) * 0.65 + 0.8;
            if (inTail && -u - a > tail * 0.45 && Math.Abs(tv) < (-u - a - tail * 0.45) * 0.75) inTail = false;
            bool dorsalFin = v < 0 && InTri(u, v, a * 0.25, -bv * 0.85, -a * 0.55, -b * 1.75, -a * 0.6, -bv * 0.6);
            bool pelvic = InTri(u, v, -a * 0.15, bv * 0.8, -a * 0.5, b * 1.5, -a * 0.45, bv * 0.7);
            bool pectoral = InTri(u, v, a * 0.3, bv * 0.3, -a * 0.05, b * 1.15, a * 0.05, bv * 0.55);
            if (!inBody && !inTail && !dorsalFin && !pelvic) return false;
            if (!inBody || pectoral)
            {
                alpha = inTail ? 0.7 : 0.6;
                double far = inTail ? Math.Min(1, (-u - a) / tail) : 0.5;
                double ray = Math.Floor(Math.Atan2(tv, -u - a * 0.75) * 10) % 2 == 0 ? 1 : 0.82;
                r = (250 - 10 * far) * ray; g = (130 + 90 * far) * ray; bl = (40 + 110 * far) * ray;
                return true;
            }
            double back = Math.Max(0, Math.Min(1, (v / bv + 1) / 2));
            double baseR = 238 + 12 * back, baseG = 70 + 120 * back, baseB = 10 + 60 * back;
            // scales: a fine overlapping pattern, each scale brighter at its edge
            double su = u * 1.25, sv = v * 1.25 + (Math.Floor(su) % 2) * 0.5;
            double cell = Math.Sqrt(Math.Pow(su - Math.Floor(su) - 0.5, 2) + Math.Pow(sv - Math.Floor(sv) - 0.5, 2));
            double scale = 0.92 + 0.16 * Smooth(0.25, 0.5, cell);
            double lr, lg, lb;
            Lit(u / a * 0.8, v / bv, baseR * scale, baseG * scale, baseB * scale, 1.0, out lr, out lg, out lb);
            if (Math.Abs(u - a * 0.38) < 0.55 && Math.Abs(v) < bv * 0.75) { lr *= 0.72; lg *= 0.6; lb *= 0.6; }   // gill cover
            if (u > a * 0.84 && Math.Abs(v - bv * 0.12) < 0.7) { lr *= 0.5; lg *= 0.35; lb *= 0.3; }   // mouth
            r = lr; g = lg; bl = lb;
            return true;
        }
    }
}
