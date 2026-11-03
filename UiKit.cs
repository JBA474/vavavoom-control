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

    // one entry of the clock/weather/... playlist
    class PlayItem
    {
        public string Id, Name; public bool On; public int Seconds;

        static readonly string[,] All = {
            { "clock", "Clock" }, { "weather", "Weather" }, { "mode1", "Temperature (Built-In Sensor)" }, { "mode2", "Color Light" },
            { "mode3", "Sound Light" }, { "mode4", "Forest" }, { "mode5", "Marquee" }, { "mode6", "Color Pieces" },
            { "snakes", "Snake Chase" }, { "blocks", "Falling Blocks" }, { "stars", "Hyperspace" }, { "rain", "Rain on a Window" }, { "fire", "Fireplace" }, { "balls", "Bouncing Balls" }, { "bricks", "Brick Breaker" }, { "laos", "Laos Flag" },{ "lava", "Lava Lamp" }, { "lavaclock", "Lava Clock" }, { "text", "Scrolling Text" }, { "drawing", "Current Drawing" }, { "animation", "Current Animation" } };

        // sections on the Playlist page for items that are turned off
        public static readonly string[] Groups = { "Clock & Weather", "Animations", "Original Modes", "Your Content" };

        public string Group
        {
            get
            {
                if (Id == "clock" || Id == "weather" || Id == "lavaclock") return Groups[0];
                if (Id.StartsWith("mode")) return Groups[2];
                if (Id == "text" || Id == "drawing" || Id == "animation") return Groups[3];
                return Groups[1];
            }
        }

        public static List<PlayItem> Defaults()
        {
            var list = new List<PlayItem>();
            for (int k = 0; k < All.GetLength(0); k++)
                list.Add(new PlayItem { Id = All[k, 0], Name = All[k, 1], On = k < 2, Seconds = k == 0 ? 120 : 60 });
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
        static readonly List<Point> Path = BuildPath();
        readonly int[] head = { 0, 40 };
        readonly int[] length = { 7, 3 };
        readonly int[] body = { 2, 5 };     // green, magenta
        int it = 0, tick, flash;

        static List<Point> BuildPath()
        {
            // serpentine through columns 1-9, then back up column 0: a closed loop covering every pixel
            var p = new List<Point> { new Point(0, 0) };
            for (int y = 0; y < 10; y++)
                for (int i = 0; i < 9; i++) p.Add(new Point(y % 2 == 0 ? 1 + i : 9 - i, y));
            for (int y = 9; y >= 1; y--) p.Add(new Point(0, y));
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
            var px = new int[100];
            for (int s = 0; s < 2; s++)
                for (int k = length[s] - 1; k >= 0; k--)
                {
                    var p = Path[(head[s] - k + Path.Count) % Path.Count];
                    px[p.Y * 10 + p.X] = k == 0 ? (s == it ? (flash > 0 && flash % 2 == 0 ? 7 : 1) : 3) : body[s];
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
        static readonly List<Point[]>[] Rotations = BuildRotations();
        readonly int[] board = new int[100];
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

        static bool Fits(int[] b, Point[] cells, int x, int y)
        {
            foreach (var p in cells)
            {
                int bx = x + p.X, by = y + p.Y;
                if (bx < 0 || bx >= 10 || by >= 10) return false;
                if (by >= 0 && b[by * 10 + bx] != 0) return false;
            }
            return true;
        }

        // with rows == null, removes full rows; otherwise only lists them. Returns how many are full.
        static int ClearRows(int[] b, List<int> rows)
        {
            int n = 0;
            for (int y = 0; y < 10; y++)
            {
                bool full = true;
                for (int x = 0; x < 10; x++) if (b[y * 10 + x] == 0) { full = false; break; }
                if (!full) continue;
                n++;
                if (rows != null) rows.Add(y);
                else { for (int yy = y; yy > 0; yy--) Array.Copy(b, (yy - 1) * 10, b, yy * 10, 10); Array.Clear(b, 0, 10); }
            }
            return n;
        }

        // well-known weights for stack height, completed lines, holes and bumpiness
        static double Score(int[] b, int lines)
        {
            int agg = 0, holes = 0, bump = 0, prev = -1;
            for (int x = 0; x < 10; x++)
            {
                int h = 0;
                bool seen = false;
                for (int y = 0; y < 10; y++)
                {
                    if (b[y * 10 + x] != 0) { if (!seen) { h = 10 - y; seen = true; } }
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
                for (int x = 0; x + Width(cells) <= 10; x++)
                {
                    int y = -Height(cells);
                    while (Fits(board, cells, x, y + 1)) y++;
                    var trial = (int[])board.Clone();
                    bool over = false;
                    foreach (var p in cells) { if (y + p.Y < 0) over = true; else trial[(y + p.Y) * 10 + x + p.X] = 1; }
                    double score = Score(trial, ClearRows(trial, null)) - (over ? 1000 : 0) + rng.NextDouble() * 0.01;
                    if (score > best) { best = score; bestCells = cells; bestX = x; }
                }
            piece = bestCells; color = Colors[s]; targetX = bestX;
            px = Math.Min(3, 10 - Width(piece)); py = -Height(piece);
        }

        void Lock()
        {
            bool over = false;
            foreach (var p in piece)
            {
                int y = py + p.Y;
                if (y < 0) over = true; else board[y * 10 + px + p.X] = color;
            }
            piece = null;
            fullRows.Clear();
            if (over)
            {
                wiping = true;
                for (int y = 0; y < 10; y++) fullRows.Add(y);
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
                    if (wiping) { Array.Clear(board, 0, 100); wiping = false; }
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
            foreach (int y in fullRows)
                for (int x = 0; x < 10; x++) frame[y * 10 + x] = flash % 2 == 0 ? 7 : (wiping ? board[y * 10 + x] : 0);
            if (piece != null)
                foreach (var p in piece)
                    if (py + p.Y >= 0) frame[(py + p.Y) * 10 + px + p.X] = color;
            return frame;
        }
    }

    // Hyperspace: stars stream out from the center in every direction, speeding up and stretching
    // into streaks (blue far away, cyan closer, white as they rush past). Starts with a short jump.
    class Starfield
    {
        class Star { public double Angle, Dist; }
        readonly List<Star> stars = new List<Star>();
        readonly Random rng = new Random();
        int tick;

        public Starfield() { for (int k = 0; k < 14; k++) stars.Add(NewStar(rng.NextDouble() * 5)); }

        Star NewStar(double dist) { return new Star { Angle = rng.NextDouble() * 2 * Math.PI, Dist = dist }; }

        static void Plot(int[] px, Star s, double dist, int color)
        {
            int x = (int)Math.Floor(5 + Math.Cos(s.Angle) * dist), y = (int)Math.Floor(5 + Math.Sin(s.Angle) * dist);
            if (x >= 0 && x < 10 && y >= 0 && y < 10 && px[y * 10 + x] < color) px[y * 10 + x] = color;
        }

        public int[] Next()
        {
            tick++;
            double grow = 1.06 + 0.2 * Math.Min(1.0, tick / 25.0);   // the jump: accelerate over the first few seconds
            var px = new int[100];
            for (int k = 0; k < stars.Count; k++)
            {
                var s = stars[k];
                double prev = s.Dist;
                s.Dist = Math.Max(0.4, s.Dist) * grow;
                if (s.Dist > 7.5) { stars[k] = NewStar(0.3 + rng.NextDouble() * 0.8); continue; }
                int head = s.Dist < 1.8 ? 4 : s.Dist < 3.2 ? 6 : 7;          // palette: blue < cyan < white
                if (s.Dist > 2.2) Plot(px, s, (prev + s.Dist) / 2, s.Dist > 3.5 ? 6 : 4);   // streak behind fast stars
                if (s.Dist > 4.0) Plot(px, s, prev, 4);
                Plot(px, s, s.Dist, head);
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

        static void Set(int[] px, int x, int y, int c) { if (x >= 0 && x < 10 && y >= 0 && y < 10 && px[y * 10 + x] < c) px[y * 10 + x] = c; }

        public int[] Next()
        {
            if (drops.Count < 9 && rng.NextDouble() < 0.5) drops.Add(new Drop { X = rng.Next(10), Y = -1, Speed = 0.35 + rng.NextDouble() * 0.8 });
            var px = new int[100];
            for (int i = drops.Count - 1; i >= 0; i--)
            {
                var d = drops[i];
                if (d.Pause > 0) d.Pause--;
                else
                {
                    d.Y += d.Speed;
                    if (rng.NextDouble() < 0.06) d.Pause = rng.Next(3, 10);
                    if (rng.NextDouble() < 0.05) d.X = Math.Max(0, Math.Min(9, d.X + rng.Next(2) * 2 - 1));
                }
                if (d.Y >= 11) { drops.RemoveAt(i); continue; }
                int x = (int)d.X, y = (int)Math.Floor(d.Y);
                Set(px, x, y - 1, 4); Set(px, x, y - 2, 4);     // blue trail
                Set(px, x, y, d.Pause > 0 ? 7 : 6);             // cyan drop, white while it clings
            }
            return px;
        }
    }

    // Fireplace: classic heat-rises fire. Two hidden rows under the screen feed it; each cell averages the heat below it
    // and cools a little, so flames flicker upward and fade from white to yellow to red. The middle burns hottest, and
    // random flares shoot yellow tongues up toward the top.
    class Fireplace
    {
        readonly double[,] heat = new double[12, 10];
        readonly Random rng = new Random();

        public int[] Next()
        {
            for (int x = 0; x < 10; x++)
            {
                double edge = x == 0 || x == 9 ? 0.55 : x == 1 || x == 8 ? 0.8 : x >= 3 && x <= 6 ? 1.15 : 1.0;
                heat[11, x] = (0.75 + rng.NextDouble() * 0.3) * edge;
                heat[10, x] = (0.45 + rng.NextDouble() * 0.6) * edge;
            }
            // a flare: one column burns extra hot for a frame, sending a tall flame up
            if (rng.NextDouble() < 0.35) { int fx = 2 + rng.Next(6); heat[10, fx] = heat[11, fx] = 1.6; }
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                {
                    double s = heat[y + 1, x] * 2 + heat[y + 1, Math.Max(0, x - 1)] + heat[y + 1, Math.Min(9, x + 1)] + heat[Math.Min(11, y + 2), x];
                    heat[y, x] = Math.Max(0, s / 5 - (0.05 + rng.NextDouble() * 0.06));
                }
            var px = new int[100];
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                {
                    double h = heat[y, x];
                    double white = x >= 3 && x <= 6 ? 0.72 : 0.85;   // the center glows white-hot more easily
                    px[y * 10 + x] = h > white ? 7 : h > 0.45 ? 3 : h > 0.2 ? 1 : 0;   // white-hot, yellow, red
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
        int t;

        public WavingFlag(string[] rows) { this.rows = rows; }

        public int[] Next()
        {
            var px = new int[100];
            for (int x = 0; x < 10; x++)
            {
                int d = x > 0 && Math.Sin(0.8 * x - 2 * Math.PI * t / 12) > 0 ? 1 : 0;
                for (int y = 0; y < rows.Length && y + d < 10; y++) px[(y + d) * 10 + x] = rows[y][x] - '0';
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

        public BouncingBalls()
        {
            int[] colors = { 1, 3, 6 };
            foreach (int c in colors) balls.Add(new Ball { X = rng.Next(9), Y = rng.Next(4), Vx = (rng.NextDouble() - 0.5) * 1.2, Vy = 0, Color = c });
        }

        public int[] Next()
        {
            var px = new int[100];
            foreach (var b in balls)
            {
                b.Vy += 0.12;
                b.X += b.Vx; b.Y += b.Vy;
                if (b.X < 0) { b.X = -b.X; b.Vx = -b.Vx; }
                if (b.X > 8) { b.X = 16 - b.X; b.Vx = -b.Vx; }
                if (b.Y > 8)
                {
                    b.Y = 8; b.Vy = -b.Vy * 0.82; b.Vx *= 0.97;
                    if (Math.Abs(b.Vy) < 0.35) { b.Vy = -(1.1 + rng.NextDouble() * 0.6); b.Vx = (rng.Next(2) * 2 - 1) * (0.25 + rng.NextDouble() * 0.4); }
                }
                int x = (int)Math.Round(b.X), y = (int)Math.Round(b.Y);
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        if (x + dx < 10 && y + dy < 10 && y + dy >= 0) px[(y + dy) * 10 + x + dx] = b.Color;
            }
            return px;
        }
    }

    // Brick breaker: a paddle on the bottom row keeps a ball in play and knocks out three rows of two-pixel bricks.
    // A hit brick flashes white; where the ball meets the paddle sets its new angle. Now and then the paddle
    // misses and serves again, and a cleared wall flashes through colors and is rebuilt.
    class BrickBreaker
    {
        static readonly int[][] RowColors = { new[] { 1, 5 }, new[] { 3, 1 }, new[] { 2, 6 } };   // rows 1-3, alternating bricks
        readonly int[] bricks = new int[100];      // brick color per pixel, 0 = empty
        readonly int[] flash = new int[100];       // frames left of a hit brick's white flash
        readonly Random rng = new Random();
        int bx, by, dx, dy, paddle = 3, aim, wait, missed, cheer;   // paddle: left pixel of the 3-wide paddle

        public BrickBreaker() { Build(); Serve(); }

        void Build()
        {
            Array.Clear(bricks, 0, 100);
            for (int r = 0; r < 3; r++)
                for (int x = 0; x < 10; x++) bricks[(r + 1) * 10 + x] = RowColors[r][x / 2 % 2];
        }

        // the ball rides on the paddle for a moment, then goes up at a random angle
        void Serve() { bx = paddle + 1; by = 8; dx = rng.Next(2) == 0 ? -1 : 1; dy = -1; wait = 6; PickAim(); }

        // which part of the paddle to meet the ball with; once in a while it aims to miss
        void PickAim() { aim = rng.Next(3) - 1; if (rng.NextDouble() < 0.06) aim = 4 * (rng.Next(2) * 2 - 1); }

        bool Brick(int x, int y) { return x >= 0 && x < 10 && y >= 0 && y < 10 && bricks[y * 10 + x] != 0; }

        void Hit(int x, int y)
        {
            int left = x - x % 2;                 // bricks are two pixels wide
            for (int k = left; k < left + 2; k++) { bricks[y * 10 + k] = 0; flash[y * 10 + k] = 2; }
        }

        // where the ball will reach the row above the paddle (walls only)
        int LandingX()
        {
            int x = bx, d = dx;
            for (int y = by; y < 8; y++) { if (x + d < 0 || x + d > 9) d = -d; x += d; }
            return x;
        }

        void Step()
        {
            if (by == 8 && dy > 0)
            {
                int nx = bx + dx;
                bool on = (bx >= paddle && bx <= paddle + 2) || (nx >= paddle && nx <= paddle + 2);
                if (on)
                {
                    int off = bx - (paddle + 1);      // edge hits send the ball off toward that side
                    if (off != 0) dx = off < 0 ? -1 : 1;
                    dy = -1; PickAim();
                }
                else { by = 9; bx = Math.Max(0, Math.Min(9, nx)); missed = 6; return; }
            }
            if (bx + dx < 0 || bx + dx > 9) dx = -dx;
            if (by + dy < 0) dy = -dy;
            if (Brick(bx + dx, by)) { Hit(bx + dx, by); dx = -dx; }
            else if (Brick(bx, by + dy)) { Hit(bx, by + dy); dy = -dy; }
            else if (Brick(bx + dx, by + dy)) { Hit(bx + dx, by + dy); dx = -dx; dy = -dy; }
            else { bx += dx; by += dy; }
        }

        public int[] Next()
        {
            var px = new int[100];
            if (cheer > 0)
            {
                cheer--;
                for (int k = 0; k < 100; k++) px[k] = cheer % 2 == 0 ? 0 : 1 + (k / 10 + cheer) % 7;
                if (cheer == 0) { Build(); Serve(); }
                return px;
            }
            if (missed > 0) { if (--missed == 0) Serve(); }
            else if (wait > 0) { wait--; bx = paddle + 1; }
            else Step();

            // the paddle slides toward where the ball will come down, one pixel per frame
            int center = missed > 0 || wait > 0 ? paddle + 1 : dy > 0 ? LandingX() - aim : 4 + (bx - 4) / 2;
            int goal = Math.Max(0, Math.Min(7, center - 1));
            if (wait == 0 && missed == 0) { if (paddle < goal) paddle++; else if (paddle > goal) paddle--; }
            if (wait > 0) bx = paddle + 1;

            bool any = false;
            for (int k = 0; k < 100; k++)
            {
                if (bricks[k] != 0) { px[k] = bricks[k]; any = true; }
                if (flash[k] > 0) { px[k] = 7; flash[k]--; }
            }
            for (int k = 0; k < 3; k++) px[90 + paddle + k] = 6;
            if (missed == 0 || missed % 2 == 0) px[by * 10 + bx] = 7;   // a missed ball blinks before the next serve
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

        // how strongly the blobs pull on one pixel
        protected double Field(int x, int y)
        {
            double f = 0;
            for (int k = 0; k < 3; k++)
            {
                double bx = 4.5 + 2.6 * Math.Sin(t * speedX[k] + phase[k]), by = 4.5 + 3.8 * Math.Sin(t * speedY[k] + phase[k] * 1.3);
                double dx = x - bx, dy = y - by;
                f += size[k] * size[k] / (dx * dx + dy * dy + 0.3);
            }
            return f;
        }

        protected static int Shade(double f) { return f > 2.6 ? 3 : f > 1.2 ? 1 : f > 0.8 ? 5 : 0; }   // yellow core, red, magenta glow

        public virtual int[] Next()
        {
            t += 0.08;
            var px = new int[100];
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++) px[y * 10 + x] = Shade(Field(x, y));
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
        static readonly int[][] Themes = {                   // core, body, glow (palette indexes)
            new[] { 3, 1, 5 },   // classic: yellow, red, magenta
            new[] { 7, 6, 4 },   // ocean: white, cyan, blue
            new[] { 3, 2, 6 },   // toxic: yellow, green, cyan
            new[] { 7, 5, 4 },   // berry: white, magenta, blue
            new[] { 7, 1, 3 },   // ember: white, red, yellow
        };
        public static readonly string[] ThemeNames = { "Classic", "Ocean", "Toxic", "Berry", "Ember" };
        public static volatile int FixedTheme = -1;         // -1 = a new theme every minute (set from Settings)
        public static volatile bool Puddle = true;          // melt into a puddle that bubbles back up, or just drip away
        class Bit { public double X, Y, Speed, Delay; public int Color, Top, Len = 1; }

        readonly bool h24;
        readonly Random rng = new Random();
        readonly double[] wobble = new double[10];         // uneven, bubbling edge of the rising time
        readonly List<Bit> bubbles = new List<Bit>(), drips = new List<Bit>(), side = new List<Bit>();
        // columns beside the hour (rows 0-4) and beside the minutes (rows 5-9) with room for bubbles
        readonly List<int>[] freeColumns = { new List<int>(), new List<int>() };
        // the melted time pools on the bottom: one stack of colors per column, then breaks into rising blobs
        readonly List<int>[] puddle = new List<int>[10];
        readonly List<Bit> blobs = new List<Bit>();
        int poolTotal = -1;

        int PoolSize() { int n = 0; foreach (var c in puddle) n += c.Count; return n; }

        int TallestColumn()
        {
            int best = 0;
            for (int x = 1; x < 10; x++)
                if (puddle[x].Count > puddle[best].Count || (puddle[x].Count == puddle[best].Count && rng.Next(2) == 0)) best = x;
            return best;
        }

        // liquid finds its level: a column two higher than its neighbor spills its top pixel over
        void Settle()
        {
            int start = rng.Next(10);
            for (int i = 0; i < 10; i++)
            {
                int x = (start + i) % 10, n = x + (rng.Next(2) == 0 ? -1 : 1);
                if (n < 0 || n > 9) continue;
                var a = puddle[x];
                if (a.Count > puddle[n].Count + 1) { puddle[n].Add(a[a.Count - 1]); a.RemoveAt(a.Count - 1); }
            }
        }
        int lastMinute = -1, theme, nextTheme = -1;
        // the next minute's colors seep into the lamp over the end of each minute instead of switching at once:
        // each pixel changes when the blend passes its seed (lower rows first, unevenly)
        const double BlendSeconds = 24;
        readonly double[] seed = new double[100];
        double blend;
        int[] face;                                         // 1 = hour pixel, 2 = minute pixel
        DateTime morphStart, lastTick = DateTime.Now;
        bool dripping;

        public LavaClock(bool h24)
        {
            this.h24 = h24; theme = rng.Next(Themes.Length);
            for (int x = 0; x < 10; x++) puddle[x] = new List<int>();
        }

        static int ShadeWith(double f, int[] th) { return f > 2.6 ? th[0] : f > 1.2 ? th[1] : f > 0.8 ? th[2] : 0; }

        // lamp pixel in this minute's colors, or the next minute's once the blend has reached it (the edge wavers a little)
        int Shade(double f, int x, int y, int[] th)
        {
            bool next = blend > 0 && seed[y * 10 + x] + 0.06 * Math.Sin(t * 1.7 + x * 0.9 + y * 0.6) < blend;
            return ShadeWith(f, next ? Themes[nextTheme] : th);
        }

        static void Set(int[] px, int x, int y, int c) { if (x >= 0 && x < 10 && y >= 0 && y < 10) px[y * 10 + x] = c; }

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
                for (int at = 0; at < 100; at++) seed[at] = 0.55 * rng.NextDouble() + 0.45 * (1 - at / 10 / 9.0);
                face = ClockArt.Face(now, h24, 1, 2, rng.Next(2) == 0, rng.Next(2) == 0);
                // free space beside each row of digits: columns with no digit pixel and at least one column away from one
                side.Clear();
                for (int band = 0; band < 2; band++)
                {
                    freeColumns[band].Clear();
                    for (int x = 0; x < 10; x++)
                    {
                        bool near = false;
                        for (int y = band * 5; y < band * 5 + 5; y++)
                            for (int dx = -1; dx <= 1; dx++)
                                if (x + dx >= 0 && x + dx < 10 && face[y * 10 + x + dx] != 0) near = true;
                        if (!near) freeColumns[band].Add(x);
                    }
                }
                for (int x = 0; x < 10; x++) wobble[x] = rng.NextDouble() * 1.6;
            }
            int[] th = Themes[theme];
            double s = (now - morphStart).TotalSeconds;
            double toNextMinute = 60 - now.Second - now.Millisecond / 1000.0;
            blend = nextTheme == theme || s < FormSeconds + HoldSeconds + MeltSeconds + PoolSeconds + RiseSeconds ? 0
                  : Math.Max(0, Math.Min(1, (BlendSeconds - toNextMinute) / BlendSeconds));
            var px = new int[100];

            if (s < FormSeconds)
            {
                // the time rises out of the lava from the bottom; the edge bubbles and throws up bright bubbles
                double fill = 10.5 - s / FormSeconds * 12.5;
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 10; x++)
                    {
                        double edge = fill + wobble[x];
                        int at = y * 10 + x;
                        if (y >= edge) px[at] = Digit(at, th);
                        else if (y >= edge - 1) px[at] = rng.NextDouble() < 0.5 ? th[rng.Next(3)] : 7;   // fizzing crest
                        else px[at] = ShadeWith(Field(x, y), th);
                    }
                if (rng.NextDouble() < 0.8)
                {
                    int x = rng.Next(10);
                    bubbles.Add(new Bit { X = x, Y = fill + wobble[x] - 1, Speed = 6 + rng.NextDouble() * 5, Color = rng.NextDouble() < 0.3 ? 7 : th[rng.Next(3)] });
                }
            }
            else if (s < FormSeconds + HoldSeconds)
            {
                for (int at = 0; at < 100; at++) px[at] = Digit(at, th);   // clean, readable time
                // colored bubbles keep rising in the free space next to the hour and next to the minutes, when there is any
                for (int band = 0; band < 2; band++)
                {
                    var free = freeColumns[band];
                    if (free.Count > 0 && rng.NextDouble() < 0.45)
                        side.Add(new Bit { X = free[rng.Next(free.Count)], Y = band * 5 + 5, Top = band * 5, Speed = 1.5 + rng.NextDouble() * 2,
                                           Color = rng.NextDouble() < 0.15 ? 7 : th[rng.Next(3)], Len = rng.NextDouble() < 0.35 ? 2 : 1 });
                }
                for (int i = side.Count - 1; i >= 0; i--)
                {
                    var b = side[i];
                    b.Y -= b.Speed * dt;
                    if (b.Y < b.Top - b.Len) { side.RemoveAt(i); continue; }
                    int y = (int)Math.Floor(b.Y);
                    for (int k = 0; k < b.Len; k++)                 // some bubbles are two pixels tall
                        if (y + k >= b.Top && y + k < b.Top + 5) px[(y + k) * 10 + (int)b.X] = b.Color;
                }
            }
            else
            {
                // melt: digits let go top rows first and fall with gravity, trailing glow, into a puddle on the bottom
                double m = s - FormSeconds - HoldSeconds;
                if (!dripping)
                {
                    dripping = true;
                    for (int at = 0; at < 100; at++)
                        if (face[at] != 0)
                            drips.Add(new Bit { X = at % 10, Y = at / 10, Delay = at / 10 * 0.2 + rng.NextDouble() * 0.5, Color = Digit(at, th) });
                }
                double rise = m - MeltSeconds - PoolSeconds;
                if (!Puddle)
                {
                    // no puddle: the drips fall off the bottom while the lamp swells back in
                    double back = Math.Min(1, m / MeltSeconds);
                    for (int y = 0; y < 10; y++)
                        for (int x = 0; x < 10; x++) px[y * 10 + x] = Shade(Field(x, y) * back, x, y, th);
                }
                else if (rise > 0)
                {
                    // the puddle bubbles back up: blobs break off its surface and float up while the lamp swells back in
                    double back = Math.Min(1, rise / RiseSeconds);
                    for (int y = 0; y < 10; y++)
                        for (int x = 0; x < 10; x++) px[y * 10 + x] = Shade(Field(x, y) * back, x, y, th);
                    if (poolTotal < 0) poolTotal = PoolSize();
                    while (PoolSize() > poolTotal * (1 - back))
                    {
                        int col = TallestColumn();
                        var stack = puddle[col];
                        blobs.Add(new Bit { X = col, Y = 9 - (stack.Count - 1), Speed = 1.5 + rng.NextDouble() * 2.5, Color = stack[stack.Count - 1] });
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
                    if (Puddle && d.Y >= 9 - stack.Count) { stack.Add(d.Color); drips.RemoveAt(i); continue; }   // lands on the puddle
                    if (d.Y > 10.5) { drips.RemoveAt(i); continue; }
                    if (d.Speed > 0) { Set(px, x, y - 1, th[2]); if (d.Speed > 4) Set(px, x, y - 2, th[2]); }   // melty trail
                    Set(px, x, y, d.Color);
                }
                for (int x = 0; x < 10; x++)
                    for (int k = 0; k < puddle[x].Count; k++)
                    {
                        bool surface = k == puddle[x].Count - 1;
                        Set(px, x, 9 - k, surface && rng.NextDouble() < 0.12 ? th[2] : puddle[x][k]);   // shimmering surface
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
                bubbles.Add(new Bit { X = rng.Next(10), Y = 10, Speed = 2 + rng.NextDouble() * 2, Color = (rng.NextDouble() < blend ? Themes[nextTheme] : th)[rng.Next(3)] });   // late bubbles carry the next colors
            for (int i = bubbles.Count - 1; i >= 0; i--)
            {
                var b = bubbles[i];
                b.Y -= b.Speed * dt;
                if (b.Y < -1) { bubbles.RemoveAt(i); continue; }
                int x = (int)b.X, y = (int)Math.Floor(b.Y);
                bool holding = s >= FormSeconds && s < FormSeconds + HoldSeconds;
                if (!holding && y >= 0 && y < 10 && px[y * 10 + x] == 0) px[y * 10 + x] = b.Color;   // never cover the time
            }
            return px;
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
            string body = "{\"Category\":" + category + ",\"Command\":\"GetCategoryFileList\",\"DeviceId\":0,\"EndNum\":" + (start + PageSize - 1) +
                          ",\"FileSize\":0,\"FileType\":" + (animations ? 1 : 5) + ",\"StartNum\":" + start + ",\"Token\":0,\"UserId\":0}";
            string json;
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.ContentType] = "application/json; charset=utf-8";
                wc.Encoding = Encoding.UTF8;
                json = wc.UploadString(ListUrl, body);
            }
            var list = new List<Entry>();
            foreach (Match m in Regex.Matches(json, "\"FileId\":\"((?:[^\"\\\\]|\\\\.)*)\",\"FileName\":\"((?:[^\"\\\\]|\\\\.)*)\""))
                list.Add(new Entry { Id = Regex.Unescape(m.Groups[1].Value), Name = Regex.Unescape(m.Groups[2].Value) });
            return list;
        }

        // download and convert to 10x10 AuraBox frames (crop the 11x11 art, snap to the 8 colors)
        public static List<int[]> Frames(string id)
        {
            byte[] d;
            using (var wc = Client()) d = wc.DownloadData(FileUrl + id);
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
}
