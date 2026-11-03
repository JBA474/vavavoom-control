// Main window: sidebar navigation, connection, all device features, and background jobs
// (playlist of clock/weather/effects, scrolling text, snake chase).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AuraBoxControl
{
    class MainForm : Form
    {
        readonly AuraBox box = new AuraBox();
        readonly Settings cfg;
        readonly List<int[]> frames = new List<int[]>();
        readonly object playlistGate = new object();
        ComboBox deviceBox; Button connectBtn; CheckBox syncBox; Label status, connDot, pageTitle;
        TrackBar bright; System.Windows.Forms.Timer brightTimer;
        PixelGrid grid; CheckBox liveBox; NumericUpDown delayBox;
        readonly PixelGrid[] frameSlots = new PixelGrid[8]; readonly Label[] frameRims = new Label[8]; int selFrame = -1;   // animation frame strip
        ColorWheel penWheel; Label penSwatch;
        NotifyIcon tray;
        Panel host, side;
        Button stopBtn;
        readonly List<NavButton> nav = new List<NavButton>();
        readonly List<Panel> pages = new List<Panel>();

        // background job (playlist, text or snakes): only one runs at a time
        Thread job; volatile bool jobStop, skipItem; Button playingBtn;   // skipItem: end the current playlist item now
        CheckBox rotateBox; TextBox placeBox; Label placeLabel, weatherLabel;
        PixelGrid weatherIcon, weatherTemp;
        List<int[]> weatherFrames = new List<int[]>(); int weatherFrame;
        WeatherInfo weather; readonly object weatherGate = new object();
        TextBox textBox; ColorWheel textColor; ComboBox textFont; TrackBar textSpeed; Button textPlay;
        Button livePlay;
        volatile string currentItem; DateTime currentEnd; string highlightedItem;   // playlist item on screen now (for the Playlist page)
        bool startHidden, started;

        // live animations generated on this PC, frame by frame
        class LiveAnim { public string Id, Name, About; public Func<Func<int[]>> Make; }
        static readonly LiveAnim[] LiveAnims = {
            new LiveAnim { Id = "snakes", Name = "Snake Chase", Make = () => new SnakeChase().Next,
                           About = "A long green snake and a short magenta one play tag. The red head is \"it\"." },
            new LiveAnim { Id = "blocks", Name = "Falling Blocks", Make = () => new FallingBlocks().Next,
                           About = "Pieces drop into place; finished rows flash and clear, forever." },
            new LiveAnim { Id = "stars", Name = "Hyperspace", Make = () => new Starfield().Next,
                           About = "Stars stream past you on all sides, stretching into streaks." },
            new LiveAnim { Id = "rain", Name = "Rain on a Window", Make = () => new RainWindow().Next,
                           About = "Drops slide down the glass at their own pace, sometimes clinging for a moment." },
            new LiveAnim { Id = "fire", Name = "Fireplace", Make = () => new Fireplace().Next,
                           About = "Flickering flames rise and fade from white-hot to yellow to red." },
            new LiveAnim { Id = "balls", Name = "Bouncing Balls", Make = () => new BouncingBalls().Next,
                           About = "Three balls bounce with gravity and get a fresh kick when they settle." },
            new LiveAnim { Id = "bricks", Name = "Brick Breaker", Make = () => new BrickBreaker().Next,
                           About = "A paddle keeps the ball in play and knocks out a wall of bricks. Clear it and a new wall goes up." },
            new LiveAnim { Id = "laos", Name = "Laos Flag", Make = () => new WavingFlag(WavingFlag.Laos).Next,
                           About = "The flag of Laos ripples in a breeze." },
            new LiveAnim { Id = "lava", Name = "Lava Lamp", Make = () => new LavaLamp().Next,
                           About = "Slow blobs of lava drift, merge and split with a magenta glow." },
            new LiveAnim { Id = "lavaclock", Name = "Lava Clock", Make = () => new LavaClock(H24).Next,
                           About = "Lava drifts, and at each new minute the ooze slowly gathers into the time, holds, then melts away." },
        };

        // single instance: a second launch asks the running copy to show itself
        static readonly int ShowMessage = RegisterWindowMessage("AuraBoxControl.ShowWindow");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, string l);
        const int EM_SETCUEBANNER = 0x1501;   // gray hint text in an empty text box
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "AuraBoxControl";
        readonly List<PlayRow> playRows = new List<PlayRow>();

        class PlayRow { public string Id; public CheckBox On; public NumericUpDown Amount; public ComboBox Unit; public Button Up, Down; }

        const int W = 700, H = 520;   // page area (logical pixels at 100% scaling)

        public MainForm(Settings settings, bool hidden)
        {
            cfg = settings;
            startHidden = hidden || settings.StartInTray;
            clock24 = cfg.Clock24; LavaClock.FixedTheme = cfg.LavaTheme; LavaClock.Puddle = cfg.LavaPuddle;
            cfg.ThermoFahrenheit = cfg.Fahrenheit;   // one unit setting now covers the weather and the box's own thermometer
            cfg.Brightness = MaxBrightness;          // every launch starts at full brightness
            for (int k = 0; k < Settings.ShowNowSlots; k++)   // a pick that no longer exists goes back to its default
                if (!ShowNowChoices().Exists(c => c.Key == cfg.ShowNow[k])) cfg.ShowNow[k] = Settings.DefaultShowNow[k];
            Text = "AuraBox Control";
            Font = new Font(Theme.UiFont, 9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(212 + W + 16, 52 + H + 30);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon(Size.Empty);

            BuildChrome();
            // sidebar groups; the page order here is the sidebar order
            AddGroup("Display");
            AddPage("\uE768", "Now", BuildNowPage());
            AddPage("\uE7F5", "Original Modes", BuildBoxPage());
            AddPage("\uE823", "Clock & Weather", BuildClockWeatherPage());
            AddPage("\uE8B2", "Animations", BuildLivePage());
            AddGroup("Create");
            AddPage("\uE70F", "Draw & Animate", BuildDrawPage());
            AddPage("\uE8D2", "Text", BuildTextPage());
            AddPage("\uE8B9", "Gallery", BuildGalleryPage());
            AddGroup("Schedule");
            AddPage("\uE8EE", "Playlist", BuildPlaylistPage());
            AddPage("\uEA8F", "Alarm & Sleep", BuildAlarmPage());
            AddGroup(null);
            AddPage("\uE713", "Settings", BuildSettingsPage());
            ShowPage(0);

            tray = new NotifyIcon { Icon = AppIcon(SystemInformation.SmallIconSize), Text = "AuraBox Control", Visible = true };
            var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new MenuRenderer() };
            menu.Items.Add("Show", null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            var skipMenu = menu.Items.Add("Skip to Next Item", null, delegate { if (rotateBox.Checked) { skipItem = true; status.Text = "Skipping to the next item..."; } });
            var pauseMenu = menu.Items.Add("Pause Playlist", null, delegate { rotateBox.Checked = !rotateBox.Checked; });
            var showNowMenu = new ToolStripMenuItem("Show Now");   // the Now page's eight picks
            menu.Items.Add(showNowMenu);
            menu.Items.Add("Sync Clock", null, delegate { Run(delegate { box.SetTime(); }, "Clock synced"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            menu.Opening += delegate
            {
                pauseMenu.Text = rotateBox.Checked ? "Pause Playlist" : "Resume Playlist";
                skipMenu.Enabled = rotateBox.Checked;
                showNowMenu.DropDownItems.Clear();
                foreach (string pick in cfg.ShowNow)
                {
                    string id = pick;
                    showNowMenu.DropDownItems.Add(ShowNowName(id), null, delegate { ShowNowRun(id); });
                }
                showNowMenu.DropDown.Renderer = menu.Renderer;
            };
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreFromTray(); };
            tray.BalloonTipClicked += delegate { RestoreFromTray(); };   // "Click here to open" on the startup notification
            Resize += delegate { if (WindowState == FormWindowState.Minimized && cfg.MinimizeToTray) Hide(); };

            Theme.Prepare(this);
            prepared = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemTheme;

            Shown += delegate { Startup(); };
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                // with "minimize to the system tray" on, the X hides the window like minimize does; the app keeps running
                // until Exit in the tray menu (or Windows shutting down, or an import restarting it)
                if (cfg.MinimizeToTray && !exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    if (!toldAboutTray && cfg.ShowNotifications)
                    {
                        toldAboutTray = true;
                        tray.ShowBalloonTip(5000, "AuraBox Control is still running", "It keeps the playlist going from the system tray. " +
                                            "To quit, right-click the AuraBox icon and choose Exit.", ToolTipIcon.Info);
                    }
                    return;
                }
                closing = true;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemTheme;
                StopJob(); SaveSettings(); box.Close(); tray.Visible = false;
            };
        }

        void Startup()
        {
            if (started) return;
            started = true;
            RefreshDevices();
            if (deviceBox.Text.Length > 0) Connect();
            StartReconnectTimer();
            if (cfg.Rotate) rotateBox.Checked = true;
            ThreadPool.QueueUserWorkItem(delegate { GetWeather(false); });
            // portable app: if it was moved, point the "start with Windows" entry at the new location
            string run = ReadRunValue();
            bool tray = run.Contains("--tray");
            if (run.Length > 0 && !RunCommand(tray).Equals(run, StringComparison.OrdinalIgnoreCase)) SetStartWithWindows(true, tray);
        }

        // started with --tray (at sign-in): stay hidden in the tray, but still connect and run the playlist
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
                BeginInvoke((Action)Startup);
                // a hidden start is easy to miss (Windows tucks new tray icons under ^), so say it's running
                // a tray notification can't hold a link, but the whole notification is clickable
                if (cfg.ShowNotifications) tray.ShowBalloonTip(6000, "AuraBox Control is running", "Click here to open AuraBox Control. Later, double-click the AuraBox icon in the system tray.",
                                    ToolTipIcon.Info);
            }
            base.SetVisibleCore(value);
        }

        protected override void WndProc(ref Message m)
        {
            if (ShowMessage != 0 && m.Msg == ShowMessage) RestoreFromTray();
            base.WndProc(ref m);
        }

        static string RunCommand(bool tray) { return "\"" + Application.ExecutablePath + "\"" + (tray ? " --tray" : ""); }

        static string ReadRunValue()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k == null ? "" : (k.GetValue(RunName) as string ?? ""); }
            catch (Exception) { return ""; }
        }

        static void SetStartWithWindows(bool on, bool tray = true)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, RunCommand(tray));
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
        }

        void OnSystemTheme(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (cfg.ThemeMode != "system") return;
            BeginInvoke((Action)delegate { ApplyTheme(); });
        }

        void ApplyTheme()
        {
            Theme.Load(cfg.ThemeMode);
            Theme.Apply(this);
            RefreshFrames();   // the strip's selection rim uses the accent color
            Refresh();
        }

        void RestoreFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        bool exiting, toldAboutTray;   // exiting: really quit, even with "minimize to the system tray" on

        void ExitApp() { exiting = true; Close(); }

// app.ico (made by tools\make_icon.ps1) holds every size from 16 to 256 px. It is the exe's own icon
        // (Explorer, Task Manager) and is embedded again so the window and tray pick the right size.
        // Size.Empty keeps all sizes, so Windows picks the best one for the title bar and taskbar.
        static Icon AppIcon(Size size)
        {
            using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            {
                if (s == null) return SystemIcons.Application;
                return size.IsEmpty ? new Icon(s) : new Icon(s, size);
            }
        }

        volatile bool closing;

        void SetStatus(string s)
        {
            if (!InvokeRequired) { status.Text = s; return; }
            if (closing) return;
            try { BeginInvoke((Action)delegate { status.Text = s; }); }
            catch (InvalidOperationException) { }   // the window is already gone
        }

        // ---------- window chrome: sidebar, header, page host, status bar ----------
        void BuildChrome()
        {
            side = new Panel { Location = new Point(0, 0), Size = new Size(204, 52 + H + 30), Tag = "sidebar" };
            side.Controls.Add(new Label { Text = "AuraBox", Location = new Point(18, 14), AutoSize = true, Font = Theme.Semibold(14f) });
            Controls.Add(side);

            pageTitle = new Label { Location = new Point(212, 12), AutoSize = true, Font = Theme.Semibold(16f), UseMnemonic = false };
            Controls.Add(pageTitle);
            connDot = new Label { Text = "●", Location = new Point(212 + W - 440, 17), AutoSize = true, Tag = "dot", ForeColor = Color.Gray };
            Controls.Add(connDot);
            deviceBox = new ComboBox { Location = new Point(212 + W - 422, 14), Width = 250 };
            Controls.Add(deviceBox);
            var refresh = new Button { Text = "", Font = new Font(Theme.IconFont, 9f), Location = new Point(212 + W - 168, 13), Size = new Size(32, 26) };
            refresh.Click += delegate { RefreshDevices(); };
            Controls.Add(refresh);
            connectBtn = new Button { Text = "Connect", Location = new Point(212 + W - 130, 13), Size = new Size(130, 26), Tag = "primary" };
            connectBtn.Click += delegate
            {
                // Disconnect stays disconnected (no automatic retries) until Connect is pressed again
                if (box.Connected) { userDisconnected = true; box.Close(); SetConnected(false, null); }
                else { userDisconnected = false; failedTries = 0; Connect(); }
            };
            Controls.Add(connectBtn);

            host = new Panel { Location = new Point(212, 52), Size = new Size(W, H) };
            Controls.Add(host);
            // status bar: the last message, brightness (from every page), the playlist state, and Resume / Stop
            status = new Label { Location = new Point(212, 52 + H + 4), Size = new Size(186, 22), Text = "Not connected", Tag = "subtle",
                                 TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
            Controls.Add(status);
            var sun = new Label { Text = "", Font = new Font(Theme.IconFont, 10f), Location = new Point(212 + 192, 52 + H + 6), AutoSize = true, Tag = "subtle" };
            Controls.Add(sun);
            bright = new TrackBar { Minimum = 0, Maximum = 255, TickStyle = TickStyle.None, AutoSize = false, Value = Math.Max(0, Math.Min(255, cfg.Brightness)),
                                    Location = new Point(212 + 212, 52 + H + 2), Size = new Size(110, 26) };
            brightTimer = new System.Windows.Forms.Timer { Interval = 250 };
            brightTimer.Tick += delegate { brightTimer.Stop(); SendBrightness(); };
            bright.ValueChanged += delegate { brightTimer.Stop(); brightTimer.Start(); };
            Controls.Add(bright);
            var tips = new ToolTip();
            tips.SetToolTip(bright, "Brightness"); tips.SetToolTip(sun, "Brightness");
            playState = new Label { Location = new Point(212 + 328, 52 + H + 4), Size = new Size(W - 328 - 174, 22), Tag = "accent",
                                    TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, UseMnemonic = false, Font = Theme.Semibold(9f) };
            Controls.Add(playState);
            resumeBtn = Btn("▶  Resume", 212 + W - 168, 52 + H + 1, 82, delegate { rotateBox.Checked = true; }, true);
            resumeBtn.Height = 26; resumeBtn.Visible = false;
            Controls.Add(resumeBtn);
            var stateTimer = new System.Windows.Forms.Timer { Interval = 500 };
            stateTimer.Tick += delegate { UpdatePlayState(); };
            stateTimer.Start();
            // stops whatever is playing from this PC (playlist, text, live animations, imported GIFs)
            stopBtn = Btn("\u25A0  Stop", 212 + W - 82, 52 + H + 1, 82, delegate
            {
                if (rotateBox.Checked) rotateBox.Checked = false; else StopJob();
                status.Text = "Stopped";
            });
            stopBtn.Visible = false;
            stopBtn.Height = 26;
            Controls.Add(stopBtn);
        }

        Label playState; Button resumeBtn;

        // "Now: Lava Clock · 3:12 left", or that the playlist is paused / off
        string PlayStateText()
        {
            string id = currentItem;
            PlayItem cur = null;
            lock (playlistGate) if (id != null) cur = PlayItem.Find(cfg.Playlist, id);
            if (cur != null)
            {
                var left = currentEnd - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                return cur.Name + "  ·  " + (int)left.TotalMinutes + ":" + left.Seconds.ToString("00") + " left";
            }
            if (rotateBox.Checked) return "Playlist starting...";
            if (cfg.Rotate) return "Playlist paused";
            return "Playlist off";
        }

        void UpdatePlayState()
        {
            if (rotateBox == null) return;
            playState.Text = PlayStateText();
            resumeBtn.Visible = !rotateBox.Checked && cfg.Rotate;   // paused for something manual: offer to pick it back up
        }

        int navY = 50;   // where the next sidebar entry goes

        // small section header in the sidebar (null: just a gap)
        void AddGroup(string title)
        {
            if (title == null) { navY += 10; return; }
            side.Controls.Add(new Label { Text = title.ToUpperInvariant(), Location = new Point(20, navY + 6), AutoSize = true, Tag = "subtle",
                                          Font = Theme.Semibold(7.5f), UseMnemonic = false });
            navY += 24;
        }

        void AddPage(string glyph, string title, Panel page)
        {
            int index = pages.Count;
            page.Location = Point.Empty; page.Size = new Size(W, H); page.Visible = false; page.Tag = title;
            host.Controls.Add(page);
            pages.Add(page);
            var b = new NavButton { Glyph = glyph, Text = title, Location = new Point(8, navY), Size = new Size(188, 34) };
            navY += 35;
            b.Click += delegate { ShowPage(index); };
            side.Controls.Add(b);
            nav.Add(b);
        }

        void ShowPage(int index)
        {
            for (int k = 0; k < pages.Count; k++) { pages[k].Visible = k == index; nav[k].Selected = k == index; }
            pageTitle.Text = (string)pages[index].Tag;
        }

        static Panel NewPage() { return new Panel(); }

        static Card NewCard(string title, int x, int y, int w, int h) { return new Card { Text = title, Location = new Point(x, y), Size = new Size(w, h) }; }

        // lists highlight the selection in the theme's accent color instead of Windows blue
        static void AccentList(ListBox lb)
        {
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.HandleCreated += delegate { lb.ItemHeight = lb.Font.Height + Theme.Px(1); };
            lb.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.AccentFill : lb.BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
                var r = new Rectangle(e.Bounds.X + Theme.Px(4), e.Bounds.Y, e.Bounds.Width - Theme.Px(4), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, lb.Items[e.Index].ToString(), lb.Font, r, sel ? Theme.AccentText : lb.ForeColor,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        static void AccentIcons(ListView lv)
        {
            lv.OwnerDraw = true;
            lv.DrawItem += delegate (object s, DrawListViewItemEventArgs e)
            {
                bool sel = e.Item.Selected;
                var icon = e.Item.GetBounds(ItemBoundsPortion.Icon);
                var label = e.Item.GetBounds(ItemBoundsPortion.Label);
                using (var b = new SolidBrush(lv.BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
                if (sel) using (var b = new SolidBrush(Theme.AccentFill)) e.Graphics.FillRectangle(b, Rectangle.Union(icon, label));
                var img = lv.LargeImageList;
                if (img != null && e.Item.ImageIndex >= 0 && e.Item.ImageIndex < img.Images.Count)
                {
                    var size = img.ImageSize;
                    e.Graphics.DrawImage(img.Images[e.Item.ImageIndex], icon.X + (icon.Width - size.Width) / 2, icon.Y + (icon.Height - size.Height) / 2,
                                         size.Width, size.Height);
                }
                // the whole item width and up to two lines, like Windows draws it (the label bounds are one short line)
                var text = new Rectangle(e.Bounds.X + 2, label.Y, e.Bounds.Width - 4, Math.Max(label.Height, e.Bounds.Bottom - label.Y));
                if (sel) using (var b = new SolidBrush(Theme.AccentFill)) e.Graphics.FillRectangle(b, text);
                TextRenderer.DrawText(e.Graphics, e.Item.Text, lv.Font, text, sel ? Theme.AccentText : lv.ForeColor,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                                      TextFormatFlags.NoPrefix);   // "b&w squares" is a name, not a keyboard shortcut
            };
        }

        // UseMnemonic off: "Clock & Weather" must not turn into an underlined "W"
        static Label Note(string text, int x, int y) { return new Label { Text = text, Location = new Point(x, y), AutoSize = true, Tag = "subtle", UseMnemonic = false }; }

        static Button Btn(string text, int x, int y, int w, Action click, bool primary = false)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 30), Tag = primary ? "primary" : null };
            b.Click += delegate { if (click != null) click(); };
            return b;
        }

        // ---------- connection ----------
        void RefreshDevices()
        {
            string current = deviceBox.Text;
            deviceBox.Items.Clear();
            string pick = null;
            foreach (var d in PairedDevices.List())
            {
                string item = DeviceName(d.Key) + "  (" + PairedDevices.Mac(d.Value) + ")";
                deviceBox.Items.Add(item);
                if (pick == null && d.Key.IndexOf("AuraBox", StringComparison.OrdinalIgnoreCase) >= 0) pick = item;
                if (cfg.Mac.Length > 0 && item.Contains(cfg.Mac)) pick = item;
            }
            if (pick != null) deviceBox.Text = pick;
            else if (current.Length > 0) deviceBox.Text = current;
            else if (cfg.Mac.Length > 0) deviceBox.Text = cfg.Mac;
            if (deviceBox.Text.Length == 0)
                status.Text = "No AuraBox paired. Pair \"AuraBox-audio\" in Windows Bluetooth settings (PIN 0000 if asked), then press refresh.";
        }

        // Windows pairs the box as "AuraBox-audio"; the app only uses its display link, so show plain "AuraBox"
        static string DeviceName(string name)
        {
            foreach (string tail in new[] { "-audio", " audio" })
                if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - tail.Length);
            return name;
        }

        ulong SelectedAddress(out string mac)
        {
            string t = deviceBox.Text;
            int open = t.LastIndexOf('('), close = t.LastIndexOf(')');
            mac = open >= 0 && close > open ? t.Substring(open + 1, close - open - 1) : t.Trim();
            ulong a;
            return PairedDevices.TryParseMac(mac, out a) ? a : 0;
        }

        void Connect()
        {
            if (connecting) return;
            string mac;
            ulong addr = SelectedAddress(out mac);
            if (addr == 0) { status.Text = "Pick a paired AuraBox or type its MAC address (AA:BB:CC:DD:EE:FF)."; return; }
            connecting = true;
            connectBtn.Enabled = false;
            connectBtn.Text = "Connecting...";
            status.Text = (failedTries > 0 ? "Reconnecting to AuraBox..." : "Connecting to AuraBox...");
            bool sync = syncBox.Checked;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ConnectNow(addr, mac, sync);
                    BeginInvoke((Action)delegate { connecting = false; SetConnected(true, null); });
                }
                catch (Exception e) { BeginInvoke((Action)delegate { connecting = false; SetConnected(false, e.Message); }); }
            });
        }

        // Keeps trying to reach the box after a failed or lost connection (also right after launch), unless the
        // Disconnect button was used. Waits a little longer after each failure, up to a minute between tries.
        volatile bool connecting;
        bool userDisconnected;
        int failedTries;
        DateTime nextTry = DateTime.MaxValue;
        readonly object connectGate = new object();

        void StartReconnectTimer()
        {
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            t.Tick += delegate
            {
                if (box.Connected || connecting || userDisconnected || DateTime.Now < nextTry) return;
                if (job != null) return;   // a running job reconnects on its own
                if (deviceBox.Text.Length == 0) RefreshDevices();
                if (deviceBox.Text.Length > 0) Connect();
            };
            t.Start();
        }

        void ConnectNow(ulong addr, string mac, bool sync)
        {
            lock (connectGate)   // the reconnect timer and a background job may try at the same moment
            {
                if (box.Connected) return;
                int ch = box.Connect(addr, cfg.Channel);
                cfg.Mac = mac; cfg.Channel = ch;
                if (sync) box.SetTime();
                box.SetTempUnit(cfg.ThermoFahrenheit);   // the box's own thermometer follows the saved unit
                box.SetAudioInput(cfg.AudioAux);         // the box ignores AUX when no cable is plugged in
                box.SetBrightness(cfg.Brightness);
            }
        }

        // used by background jobs: reconnect silently to the last device
        bool EnsureConnected()
        {
            if (box.Connected) return true;
            ulong a;
            if (!PairedDevices.TryParseMac(cfg.Mac, out a)) return false;
            try
            {
                ConnectNow(a, cfg.Mac, true);
                BeginInvoke((Action)delegate { SetConnected(true, null); });
                return true;
            }
            catch (Exception) { return false; }
        }

        void SetConnected(bool ok, string err)
        {
            connectBtn.Enabled = true;
            connectBtn.Text = ok ? "Disconnect" : "Connect";
            connDot.ForeColor = ok ? Color.FromArgb(16, 185, 80) : Color.Gray;
            if (ok)
            {
                failedTries = 0; nextTry = DateTime.MaxValue;
                status.Text = "Connected to AuraBox";   // the address is in the device list
                SaveSettings();
                return;
            }
            if (err == null) { status.Text = "Disconnected"; return; }
            // try again soon: 10 s, 20 s, 30 s... then once a minute
            int wait = Math.Min(60, 10 * (failedTries + 1));
            failedTries++;
            nextTry = DateTime.Now.AddSeconds(wait);
            status.Text = err + "  (Is the AuraBox on and not connected to a phone?) Retrying in " + wait + " s.";
        }

        void Run(Action a, string done)
        {
            if (!box.Connected) { status.Text = "Not connected"; return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { a(); SetStatus(done); }
                catch (Exception e)
                {
                    box.Close();
                    BeginInvoke((Action)delegate { SetConnected(false, "Connection lost: " + e.Message); });
                }
            });
        }

        // send from a background job; drops the connection on failure so EnsureConnected retries
        bool JobSend(Action a)
        {
            // a notification icon is on the box: hold this frame until it has had its turn
            while (DateTime.Now.Ticks < Interlocked.Read(ref holdUntilTicks) && !jobStop) Thread.Sleep(50);
            if (jobStop) return true;   // stopping anyway; nothing to send
            if (!EnsureConnected()) { jobSendFailed = true; return false; }
            try
            {
                a();
                // back in touch after a failure: replace the "waiting" message with what is playing
                if (jobSendFailed) { jobSendFailed = false; SetStatus(jobStatus ?? "Connected"); }
                return true;
            }
            catch (Exception)
            {
                jobSendFailed = true;
                box.Close(); BeginInvoke((Action)delegate { SetConnected(false, "Connection lost — retrying"); });
                return false;
            }
        }

        bool jobSendFailed; volatile string jobStatus;   // last "what's playing" message from a background job

        // ---------- background jobs ----------
        void StartJob(ThreadStart body, Button playButton = null)
        {
            StopJob();
            jobStop = false;
            playingBtn = playButton;
            if (playButton != null) playButton.Text = "Stop";
            job = new Thread(body) { IsBackground = true };
            job.Start();
            stopBtn.Visible = true;
        }

        void StopJob()
        {
            jobStop = true;
            if (job != null && job.IsAlive && Thread.CurrentThread != job) job.Join(3000);
            job = null;
            currentItem = null; showing = null;
            if (stopBtn != null) stopBtn.Visible = false;
            if (playingBtn != null) { playingBtn.Text = "Play"; playingBtn = null; }
        }

        // read window state from a background job; once the window is closing this ends the job instead of crashing
        bool OnUi(Action read)
        {
            try
            {
                if (closing || IsDisposed || Disposing) { jobStop = true; return false; }
                Invoke(read);
                return true;
            }
            catch (ObjectDisposedException) { jobStop = true; return false; }
            catch (InvalidOperationException) { jobStop = true; return false; }
        }

        bool Wait(double seconds)
        {
            DateTime end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end && !skipItem) { if (jobStop) return false; Thread.Sleep(100); }
            return !jobStop;
        }

        // still inside the current item's time (and not skipped)?
        bool Before(DateTime end) { return DateTime.Now < end && !skipItem; }

        bool WaitUntil(DateTime end) { return Wait(Math.Max(0, (end - DateTime.Now).TotalSeconds)); }

        // manual display actions pause the playlist/text/snakes so they aren't immediately overwritten
        void StopRotationForManual()
        {
            if (job == null) return;
            if (rotateBox.Checked) PausePlaylist(); else StopJob();
            status.Text = "Playlist paused for manual control";
        }

        // A pause for something manual (a mode, color, picture, live animation) is temporary: the saved setting keeps
        // the playlist on, so it starts again the next time the app opens. Only unchecking "Run the playlist",
        // Pause in the tray menu or the Stop button turn it off for good.
        bool manualPause;
        void PausePlaylist()
        {
            manualPause = true;
            try { rotateBox.Checked = false; }
            finally { manualPause = false; }
        }

        // toggles a streamed effect (text, snakes) on its Play/Stop button
        void TogglePlay(Button btn, ThreadStart body, string what)
        {
            if (job != null && playingBtn == btn) { StopJob(); status.Text = what + " stopped"; return; }
            if (rotateBox.Checked) PausePlaylist();
            StartJob(body, btn);
            showing = what;
            status.Text = what + " playing";
        }

        // ---------- now: what's on the box, playlist controls, quick picks ----------
        Panel BuildNowPage()
        {
            var p = NewPage();
            var card = NewCard("On the AuraBox", 0, 0, W, 252);
            var preview = new PixelGrid(18) { Location = new Point(14, 40), ReadOnly = true };
            card.Controls.Add(preview);
            var what = new Label { Location = new Point(222, 36), Size = new Size(W - 236, 34), Font = Theme.Semibold(15f), UseMnemonic = false, AutoEllipsis = true };
            var detail = new Label { Location = new Point(224, 72), Size = new Size(W - 236, 22), Tag = "subtle", UseMnemonic = false, AutoEllipsis = true };
            card.Controls.Add(what); card.Controls.Add(detail);
            var playBtn = Btn("Run Playlist", 222, 100, 150, delegate { rotateBox.Checked = !rotateBox.Checked; }, true);
            card.Controls.Add(playBtn);
            card.Controls.Add(Btn("Skip to Next Item  ›", 380, 100, 170, delegate
            {
                if (!rotateBox.Checked) { status.Text = "Start the playlist first"; return; }
                skipItem = true; status.Text = "Skipping to the next item...";
            }));
            card.Controls.Add(Note("Pausing here turns the playlist off until you run it again.", 224, 136));

            // up next: the following three playlist items, under the controls
            card.Controls.Add(new Label { Text = "Up next", Location = new Point(224, 168), AutoSize = true, Font = Theme.Semibold(9f) });
            var nextName = new Label[3]; var nextTime = new Label[3];
            for (int k = 0; k < 3; k++)
            {
                nextName[k] = new Label { Location = new Point(224 + k * 156, 190), Size = new Size(150, 22), UseMnemonic = false, AutoEllipsis = true };
                nextTime[k] = new Label { Location = new Point(224 + k * 156, 212), Size = new Size(150, 20), Tag = "subtle", UseMnemonic = false };
                card.Controls.Add(nextName[k]); card.Controls.Add(nextTime[k]);
            }
            p.Controls.Add(card);

            // eight quick picks; right-click one to choose what it shows (saved)
            var quick = NewCard("Show Now", 0, 264, W, 154);
            for (int k = 0; k < Settings.ShowNowSlots; k++)
            {
                int slot = k;
                var b = Btn(ShowNowName(cfg.ShowNow[slot]), 12 + (k % 4) * 170, 40 + (k / 4) * 40, 162, delegate { ShowNowRun(cfg.ShowNow[slot]); });
                var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Renderer = new MenuRenderer() };
                menu.Opening += delegate
                {
                    menu.Items.Clear();
                    menu.Items.Add(new ToolStripMenuItem("Show on this button:") { Enabled = false });
                    foreach (var choice in ShowNowChoices())
                    {
                        string id = choice.Key;
                        var item = new ToolStripMenuItem(choice.Value) { Checked = id == cfg.ShowNow[slot] };
                        item.Click += delegate { cfg.ShowNow[slot] = id; b.Text = ShowNowName(id); SaveSettings(); };
                        menu.Items.Add(item);
                    }
                };
                b.ContextMenuStrip = menu;
                quick.Controls.Add(b);
            }
            quick.Controls.Add(Note("Right-click a button to choose what it shows.", 14, 122));
            p.Controls.Add(quick);

            var tick = new System.Windows.Forms.Timer { Interval = 200 };
            tick.Tick += delegate
            {
                if (!p.Visible) return;
                var coming = UpNext(3);
                for (int k = 0; k < 3; k++)
                {
                    bool has = coming != null && k < coming.Count;
                    nextName[k].Text = has ? (k + 1) + ".  " + coming[k].Name : k == 0 && coming == null ? "The playlist is off." : "";
                    nextTime[k].Text = has ? Duration(coming[k].Seconds) : k == 0 && coming == null ? "Run it to see what plays next." : "";
                }
                playBtn.Text = rotateBox.Checked ? "Pause Playlist" : "Run Playlist";
                int[] img = box.LastImage; string mode = box.LastMode;
                preview.SetAll(img ?? new int[100]);
                string id = currentItem; PlayItem cur = null;
                lock (playlistGate) if (id != null) cur = PlayItem.Find(cfg.Playlist, id);
                if (!box.Connected) { what.Text = "Not connected"; detail.Text = "Connect at the top right. Background jobs reconnect on their own."; }
                else if (cur != null)
                {
                    what.Text = cur.Name;
                    var left = currentEnd - DateTime.Now; if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                    detail.Text = "Playlist item  ·  " + (int)left.TotalMinutes + ":" + left.Seconds.ToString("00") + " left";
                }
                else if (job != null) { what.Text = showing ?? "Playing from this PC"; detail.Text = "Sent frame by frame from this PC. Stop is at the bottom right."; }
                else if (mode != null) { what.Text = mode; detail.Text = "A built-in AuraBox mode (runs on the box itself)."; }
                else if (img != null) { what.Text = "Picture"; detail.Text = "The last picture or animation sent from this PC."; }
                else { what.Text = "Nothing sent yet"; detail.Text = ""; }
            };
            tick.Start();
            return p;
        }

        volatile string showing;   // name of a manual job (live animation, clock...) for the Now page

        // the turned-on playlist items that come after the one playing (wrapping around); null while the playlist is off
        List<PlayItem> UpNext(int count)
        {
            if (!rotateBox.Checked) return null;
            var on = new List<PlayItem>();
            lock (playlistGate) foreach (var it in cfg.Playlist) if (it.On) on.Add(it);
            var list = new List<PlayItem>();
            if (on.Count == 0) return list;
            int at = on.FindIndex(it => it.Id == currentItem);
            for (int k = 1; k <= count && k <= on.Count; k++) list.Add(on[(at + k + on.Count) % on.Count]);
            return list;
        }

        static string Duration(int seconds)
        {
            if (seconds < 60) return seconds + " sec";
            return seconds / 60 + " min" + (seconds % 60 > 0 ? " " + seconds % 60 + " sec" : "");
        }

        // what a Show Now button can play: the clock, the weather, a live animation, or a built-in mode
        static List<KeyValuePair<string, string>> ShowNowChoices()
        {
            var list = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string, string>("clock", "Clock"), new KeyValuePair<string, string>("weather", "Weather") };
            foreach (var la in LiveAnims) list.Add(new KeyValuePair<string, string>(la.Id, la.Name));
            for (int m = 0; m < Proto.ModeNames.Length; m++) list.Add(new KeyValuePair<string, string>("mode" + m, Proto.ModeNames[m] + " (built-in)"));
            return list;
        }

        static string ShowNowName(string id)
        {
            foreach (var c in ShowNowChoices()) if (c.Key == id) return c.Value;
            return id;
        }

        void ShowNowRun(string id)
        {
            if (id == "clock") { StopRotationForManual(); ShowClockNow(); }
            else if (id == "weather") ShowWeatherNow();
            else if (id.StartsWith("mode"))
            {
                int m = id[4] - '0';
                StopRotationForManual();
                Run(delegate { box.SetMode(m); }, "Mode: " + Proto.ModeNames[m]);
            }
            else PlayLive(id);
        }

        // A notification icon needs the screen to itself: the playlist (or whatever is playing) holds its next frame
        // for a few seconds, then the box gets back what it showed before. Its time keeps counting meanwhile.
        const double NotifySeconds = 6;
        long holdUntilTicks;   // background jobs don't send before this time (DateTime ticks)
        int flashCount;

        void FlashNotification(int code)
        {
            if (!box.Connected) { status.Text = "Not connected"; return; }
            int mine = Interlocked.Increment(ref flashCount);
            Interlocked.Exchange(ref holdUntilTicks, DateTime.Now.AddSeconds(NotifySeconds).Ticks);
            string name = Proto.NotifyNames[code];
            status.Text = "Notification: " + name + (job != null ? " (playback holds for " + NotifySeconds + " s)" : "");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    box.Notify(code);
                    Thread.Sleep(TimeSpan.FromSeconds(NotifySeconds));
                    if (mine == Interlocked.CompareExchange(ref flashCount, 0, 0)) box.Redisplay();   // a newer flash restores instead
                }
                catch (Exception e)
                {
                    box.Close();
                    try { BeginInvoke((Action)delegate { SetConnected(false, "Connection lost: " + e.Message); }); }
                    catch (InvalidOperationException) { }
                }
            });
        }

        // play one live animation now (pauses the playlist)
        void PlayLive(string id)
        {
            foreach (var la0 in LiveAnims)
                if (la0.Id == id)
                {
                    var la = la0;
                    StopRotationForManual();
                    StartJob(delegate { StreamLoop(la.Make(), LiveInterval(la.Id), DateTime.MaxValue); });
                    showing = la.Name;
                    status.Text = la.Name + " playing";
                }
        }

        // ---------- aurabox: built-in modes, solid color, audio input ----------
        Panel BuildBoxPage()
        {
            var p = NewPage();
            var modes = NewCard("Mode", 0, 0, W, 112);
            for (int k = 0; k < Proto.ModeNames.Length; k++)
            {
                int m = k;
                modes.Controls.Add(Btn(Proto.ModeNames[k], 12 + (k % 4) * 170, 36 + (k / 4) * 36, 162,
                    delegate { StopRotationForManual(); Run(delegate { box.SetMode(m); }, "Mode: " + Proto.ModeNames[m]); }));
            }
            p.Controls.Add(modes);

            var colors = NewCard("Solid Color", 0, 124, 344, 260);
            var wheel = new ColorWheel { Location = new Point(14, 38), Size = new Size(200, 200) };
            var swatch = new Label { Location = new Point(232, 60), Size = new Size(92, 52), Tag = "swatch", BackColor = Proto.Palette[1] };
            var name = new Label { Location = new Point(232, 120), AutoSize = true, Text = "Red", Font = Theme.Semibold(10f) };
            wheel.Changing += delegate { swatch.BackColor = Proto.Palette[wheel.Value]; name.Text = Proto.ColorNames[wheel.Value]; };
            wheel.Picked += delegate
            {
                int c = wheel.Value;
                swatch.BackColor = Proto.Palette[c]; name.Text = Proto.ColorNames[c];
                StopRotationForManual();
                Run(delegate { box.SetColor(c); }, "Color: " + Proto.ColorNames[c]);
            };
            colors.Controls.Add(wheel); colors.Controls.Add(swatch); colors.Controls.Add(name);
            colors.Controls.Add(new Label { Text = "Click a wedge,\r\nor the center\r\nfor white.", Location = new Point(232, 150),
                                            AutoSize = true, Tag = "subtle" });
            p.Controls.Add(colors);

            // the box's own notification icons; playback holds while one shows (see FlashNotification)
            var note = NewCard("Flash a Notification Icon", 356, 124, W - 356, 260);
            for (int k = 0; k < Proto.NotifyNames.Length; k++)
            {
                int code = k;
                note.Controls.Add(Btn(Proto.NotifyNames[k], 12 + (k % 2) * 164, 40 + (k / 2) * 42, 156, delegate { FlashNotification(code); }));
            }
            p.Controls.Add(note);
            p.Controls.Add(Note("Brightness is the slider at the bottom of the window. The audio input (AUX jack or Bluetooth)\r\n" +
                                "is on the Settings page.", 2, 396));
            return p;
        }

        const int MaxBrightness = 255;

        void SendBrightness()
        {
            int v = bright.Value;
            cfg.Brightness = v;
            Run(delegate { box.SetBrightness(v); }, "Brightness: " + v);
        }

        // ---------- draw ----------
        Panel BuildDrawPage()
        {
            var p = NewPage();
            grid = new PixelGrid(34) { Location = new Point(0, 0) };
            grid.Changed += delegate { if (liveBox.Checked) SendImage(); };
            p.Controls.Add(grid);

            var pen = NewCard("Pen", 376, 0, W - 376, 206);
            penWheel = new ColorWheel { Location = new Point(12, 36), Size = new Size(160, 160) };
            penSwatch = new Label { Location = new Point(196, 44), Size = new Size(96, 40), Tag = "swatch", BackColor = Proto.Palette[1],
                                    TextAlign = ContentAlignment.MiddleCenter };
            penWheel.Changing += delegate { SetPen(penWheel.Value); };
            penWheel.Picked += delegate { SetPen(penWheel.Value); };
            pen.Controls.Add(penWheel); pen.Controls.Add(penSwatch);
            pen.Controls.Add(Btn("Eraser", 196, 96, 96, delegate { SetPen(0); }));
            pen.Controls.Add(new Label { Text = "Right-click\r\nalso erases.", Location = new Point(196, 140), AutoSize = true, Tag = "subtle" });
            p.Controls.Add(pen);

            string[] labels = { "Send to AuraBox", "Fill With Pen", "Clear", "Import Picture...", "Open Design...", "Save Design..." };
            Action[] actions = {
                delegate { StopRotationForManual(); SendImage(); },
                delegate { var a = new int[100]; for (int i = 0; i < 100; i++) a[i] = grid.Pen; grid.SetAll(a); },
                delegate { grid.SetAll(new int[100]); },
                ImportPicture, OpenDesign, SaveDesign };
            for (int k = 0; k < labels.Length; k++)
                p.Controls.Add(Btn(labels[k], 376 + (k % 2) * 166, 216 + (k / 2) * 36, 158, actions[k], k == 0));
            liveBox = new CheckBox { Text = "Live update while drawing", Location = new Point(378, 324), AutoSize = true };
            p.Controls.Add(liveBox);

            // the animation's frames as a strip under the editor: click to pick one, double-click to edit it
            var strip = NewCard("Animation Frames (up to 8): click to pick, double-click to edit", 0, 352, W, 164);
            for (int k = 0; k < 8; k++)
            {
                int i = k, x = 12 + k * 58;
                frameSlots[k] = new PixelGrid(5) { Location = new Point(x, 32), ReadOnly = true, Cursor = Cursors.Hand };
                frameRims[k] = new Label { Location = new Point(x - 3, 29), Size = new Size(56, 56), Tag = "swatch", BackColor = Color.Transparent };
                frameSlots[k].Click += delegate { if (i < frames.Count) { selFrame = i; RefreshFrames(); } };
                frameSlots[k].DoubleClick += delegate { if (i < frames.Count) { selFrame = i; RefreshFrames(); LoadFrame(); } };
                strip.Controls.Add(frameSlots[k]); strip.Controls.Add(frameRims[k]);   // added after, so the rim sits behind
            }
            // moving a frame keeps it picked, so repeated clicks walk it along the strip
            Action<int> move = delegate (int step)
            {
                int i = selFrame, j = selFrame + step;
                if (i < 0 || i >= frames.Count || j < 0 || j >= frames.Count) return;
                var f = frames[i]; frames[i] = frames[j]; frames[j] = f; selFrame = j; RefreshFrames();
            };
            string[] labels2 = { "Add Drawing", "Replace", "Edit Frame", "Remove", "◀  Move Left", "Move Right  ▶", "Clear All", "Import GIF..." };
            Action[] actions2 = {
                delegate {
                    if (frames.Count >= 8) { MessageBox.Show(this, "The AuraBox holds at most 8 frames.", "AuraBox Control"); return; }
                    frames.Add((int[])grid.Pixels.Clone()); selFrame = frames.Count - 1; RefreshFrames(); },
                delegate { if (selFrame >= 0 && selFrame < frames.Count) { frames[selFrame] = (int[])grid.Pixels.Clone(); RefreshFrames(); } },
                LoadFrame,
                delegate { if (selFrame >= 0 && selFrame < frames.Count) { frames.RemoveAt(selFrame); RefreshFrames(); } },
                delegate { move(-1); },
                delegate { move(1); },
                delegate { frames.Clear(); selFrame = -1; RefreshFrames(); },
                ImportPicture };
            for (int k = 0; k < labels2.Length; k++) strip.Controls.Add(Btn(labels2[k], 12 + (k % 4) * 116, 90 + (k / 4) * 36, 110, actions2[k]));

            // right side: the animation playing here (no box needed), its delay, and Send
            var animPreview = new PixelGrid(7) { Location = new Point(490, 32), ReadOnly = true };
            strip.Controls.Add(animPreview);
            strip.Controls.Add(new Label { Text = "Delay:", Location = new Point(574, 34), AutoSize = true });
            delayBox = new NumericUpDown { Minimum = 1, Maximum = 255, Value = Math.Max(1, Math.Min(255, cfg.AnimDelay)), Location = new Point(574, 58), Width = 60 };
            delayBox.ValueChanged += delegate { cfg.AnimDelay = (int)delayBox.Value; };
            new ToolTip().SetToolTip(delayBox, "Time between frames: higher is slower. The preview on the left plays at about this speed.");
            strip.Controls.Add(delayBox);
            strip.Controls.Add(Btn("Send Animation", 490, 126, 198, delegate
            {
                if (frames.Count == 0) { status.Text = "Add at least one frame first"; return; }
                StopRotationForManual();
                SendAnimation(frames);
            }, true));
            int previewAt = 0; DateTime previewNext = DateTime.MinValue;
            var previewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            previewTimer.Tick += delegate
            {
                if (!p.Visible || DateTime.Now < previewNext) return;
                if (frames.Count == 0) { animPreview.SetAll(new int[100]); previewNext = DateTime.Now.AddSeconds(0.5); return; }
                previewAt = (previewAt + 1) % frames.Count;
                animPreview.SetAll(frames[previewAt]);
                previewNext = DateTime.Now.AddSeconds((double)delayBox.Value / 10);   // about 0.1 s per delay step
            };
            previewTimer.Start();
            p.Controls.Add(strip);
            RefreshFrames();
            return p;
        }

        void SetPen(int c)
        {
            grid.Pen = c;
            if (c != 0) penWheel.Value = c;
            penSwatch.BackColor = c == 0 ? Color.Black : Proto.Palette[c];
            penSwatch.ForeColor = Color.Gray;
            penSwatch.Text = c == 0 ? "Eraser" : "";
        }

        void SendImage()
        {
            int[] px = (int[])grid.Pixels.Clone();
            Run(delegate { box.ShowImage(px); }, "Picture sent");
        }

        void ImportPicture()
        {
            using (var dlg = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.jfif;*.gif;*.bmp;*.ico;*.tif;*.tiff|All files|*.*",
                                                  Title = "Import picture" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Image img; bool ok;
                string report = ImportDialog.Verify(dlg.FileName, out img, out ok);
                if (!ok)
                {
                    MessageBox.Show(this, report, "Picture cannot be used", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                using (img)
                using (var d = new ImportDialog(dlg.FileName, img, report))
                {
                    Theme.Prepare(d);
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    switch (d.Action)
                    {
                        case ImportAction.SendImage:
                            StopRotationForManual();
                            grid.SetAll(d.Frames[0]);
                            SendImage();
                            break;
                        case ImportAction.Edit:
                            grid.SetAll(d.Frames[0]);
                            break;
                        case ImportAction.SendAnimation:
                            StopRotationForManual();
                            SendAnimation(d.Frames);
                            break;
                        case ImportAction.PlayFromPC:
                            StopRotationForManual();
                            PlayFrames(d.Frames, d.Delays, Path.GetFileName(dlg.FileName));
                            break;
                        case ImportAction.AddFrames:
                            foreach (var f in d.Frames) if (frames.Count < 8) frames.Add(f);
                            RefreshFrames();
                            status.Text = "Added frames to the animation strip on Draw & Animate (" + frames.Count + "/8)";
                            break;
                    }
                }
            }
        }

        // play every frame of an imported animation from this PC, at the file's own timing
        void PlayFrames(List<int[]> all, List<int> delays, string name)
        {
            int n = 0;
            StartJob(delegate
            {
                StreamLoop(delegate { return all[n % all.Count]; },
                           delegate { double s = delays[n % delays.Count] / 1000.0; n++; return Math.Max(0.05, s); }, DateTime.MaxValue);
            });
            status.Text = "Playing " + name + " from this PC (" + all.Count + " frames)";
        }

        // design file: one line of 100 digits for the drawing, then one line per animation frame
        static string Digits(int[] px) { var sb = new StringBuilder(); foreach (int c in px) sb.Append(c); return sb.ToString(); }
        static int[] Parse(string line)
        {
            line = line.Trim();
            if (line.Length != 100) return null;
            var px = new int[100];
            for (int k = 0; k < 100; k++) { px[k] = line[k] - '0'; if (px[k] < 0 || px[k] > 7) return null; }
            return px;
        }

        void SaveDesign()
        {
            using (var dlg = new SaveFileDialog { Filter = "AuraBox design|*.aura", DefaultExt = "aura" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var lines = new List<string> { Digits(grid.Pixels) };
                foreach (var f in frames) lines.Add(Digits(f));
                File.WriteAllLines(dlg.FileName, lines.ToArray());
                status.Text = "Saved " + Path.GetFileName(dlg.FileName);
            }
        }

        void OpenDesign()
        {
            using (var dlg = new OpenFileDialog { Filter = "AuraBox design|*.aura" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string[] lines = File.ReadAllLines(dlg.FileName);
                if (lines.Length == 0 || Parse(lines[0]) == null) { MessageBox.Show(this, "Not an AuraBox design file.", "AuraBox Control"); return; }
                grid.SetAll(Parse(lines[0]));
                frames.Clear();
                for (int k = 1; k < lines.Length && frames.Count < 8; k++) { var f = Parse(lines[k]); if (f != null) frames.Add(f); }
                RefreshFrames();
            }
        }

        // ---------- gallery (built-in designs + Divoom cloud) ----------
        static readonly string[] CloudSources = {
            "Divoom Cloud: Featured Animations", "Divoom Cloud: Popular Animations", "Divoom Cloud: New Animations",
            "Divoom Cloud: Featured Pictures", "Divoom Cloud: Popular Pictures" };
        static readonly int[] CloudCategory = { 1, 2, 3, 1, 2 };

        Panel BuildGalleryPage()
        {
            var p = NewPage();
            var items = GalleryItem.Load();
            var cats = new List<string>();
            foreach (var it in items) if (!cats.Contains(it.Category)) cats.Add(it.Category);

            var catBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, 2), Width = 250 };
            var search = new TextBox { Location = new Point(258, 3), Width = 136 };
            search.HandleCreated += delegate { SendMessage(search.Handle, EM_SETCUEBANNER, (IntPtr)1, "Search names"); };
            p.Controls.Add(search);
            foreach (var c in cats) catBox.Items.Add("Built-In: " + c);
            catBox.Items.Add("My Favorites");
            foreach (var c in CloudSources) catBox.Items.Add(c);
            var favorites = GalleryItem.LoadFavorites();
            p.Controls.Add(catBox);
            var prev = Btn("‹ Previous", W - 300, 0, 100, null); var next = Btn("Next ›", W - 100, 0, 100, null);
            var pageLabel = new Label { Location = new Point(W - 196, 7), Size = new Size(92, 20), TextAlign = ContentAlignment.TopCenter, UseMnemonic = false };
            var pageCounts = new Dictionary<int, int>();   // cloud source -> number of pages, counted once per session
            var counting = new HashSet<int>();
            p.Controls.Add(prev); p.Controls.Add(next); p.Controls.Add(pageLabel);

            int thumb = Theme.Px(50);
            var images = new ImageList { ImageSize = new Size(thumb, thumb), ColorDepth = ColorDepth.Depth24Bit };
            var view = new ListView { Location = new Point(0, 40), Size = new Size(W, 412), View = View.LargeIcon, LargeImageList = images,
                                      MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None };
            AccentIcons(view);
            p.Controls.Add(view);
            var shown = new List<GalleryItem>();
            var preview = new System.Windows.Forms.Timer { Interval = 250 };
            int previewFrame = 0, cloudPage = 0, generation = 0;
            var pageCache = new Dictionary<string, List<GalleryItem>>();      // "source:page" -> deduplicated items
            var downloads = new Dictionary<string, List<int[]>>();            // FileId -> converted frames
            var seen = new HashSet<string>();                                 // looks already shown (built-ins included)
            foreach (var it in items) { seen.Add(Signature(it.Frames)); string n = NameKey(it.Name); if (n.Length > 0) seen.Add(n); }

            // search: the list loaded for the source (all of it, or one cloud page) is kept in pageItems,
            // and only names containing the search text are shown
            var pageItems = new List<GalleryItem>();
            Func<GalleryItem, bool> matches = it =>
                search.Text.Trim().Length == 0 || (it.Name ?? "").IndexOf(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;
            Action<GalleryItem> display = delegate (GalleryItem it)
            {
                images.Images.Add(Proto.Render(it.Frames[0], Math.Max(1, thumb / 10)));
                view.Items.Add(new ListViewItem(it.Name, images.Images.Count - 1));
                shown.Add(it);
            };
            Action<GalleryItem> addItem = delegate (GalleryItem it) { pageItems.Add(it); if (matches(it)) display(it); };
            Action clearView = delegate { view.Items.Clear(); images.Images.Clear(); shown.Clear(); pageItems.Clear(); };
            search.TextChanged += delegate
            {
                view.BeginUpdate();
                view.Items.Clear(); images.Images.Clear(); shown.Clear();
                foreach (var it in pageItems) if (matches(it)) display(it);
                view.EndUpdate();
                if (search.Text.Trim().Length > 0)
                    status.Text = shown.Count + " of " + pageItems.Count + " match “" + search.Text.Trim() + "”" + (catBox.SelectedIndex > cats.Count ? " on this page" : "");
            };

            var favBtn = Btn("Add to Favorites", W - 170, 462, 170, null);
            Action load = null;
            favBtn.Click += delegate
            {
                if (view.SelectedIndices.Count == 0) { status.Text = "Pick a design first"; return; }
                var it = shown[view.SelectedIndices[0]];
                if (catBox.SelectedIndex == cats.Count) favorites.Remove(it);
                else
                {
                    string sig = Signature(it.Frames);
                    foreach (var f in favorites) if (Signature(f.Frames) == sig) { status.Text = "“" + it.Name + "” is already a favorite"; return; }
                    var copy = new GalleryItem { Category = "Favorites", Name = it.Name };
                    foreach (var f in it.Frames) copy.Frames.Add((int[])f.Clone());
                    favorites.Add(copy);
                }
                try { GalleryItem.SaveFavorites(favorites); }
                catch (Exception e) { status.Text = "Could not save favorites: " + e.Message; return; }
                if (catBox.SelectedIndex == cats.Count) { load(); status.Text = "Removed “" + it.Name + "” from favorites"; }
                else status.Text = "Saved “" + it.Name + "” to My Favorites (kept on this PC)";
            };
            p.Controls.Add(favBtn);
            load = delegate
            {
                int gen = ++generation;
                clearView();
                int sel = catBox.SelectedIndex;
                bool cloud = sel > cats.Count, fav = sel == cats.Count;
                prev.Visible = next.Visible = pageLabel.Visible = cloud;
                favBtn.Text = fav ? "Remove From Favorites" : "Add to Favorites";
                if (fav)
                {
                    view.BeginUpdate();
                    foreach (var it in SortedUnique(favorites, false)) addItem(it);
                    view.EndUpdate();
                    status.Text = favorites.Count == 0 ? "No favorites yet: select a design and press Add to Favorites" : favorites.Count + " favorites";
                    return;
                }
                if (!cloud)
                {
                    view.BeginUpdate();
                    foreach (var it in SortedUnique(items.FindAll(i => i.Category == cats[sel]))) addItem(it);
                    view.EndUpdate();
                    return;
                }
                int src = sel - cats.Count - 1, page = cloudPage;
                // "3 of 51"; the total is counted in the background the first time a source is opened
                int pages;
                bool known = pageCounts.TryGetValue(src, out pages);
                pageLabel.Text = (page + 1) + " of " + (known ? pages.ToString() : "…");
                prev.Enabled = page > 0;
                next.Enabled = !known || page + 1 < pages;
                if (!known && counting.Add(src))
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        int total = -1;
                        try { total = DivoomCloud.Count(CloudCategory[src], src < 3); } catch (Exception) { }
                        try
                        {
                            BeginInvoke((Action)delegate
                            {
                                counting.Remove(src);
                                if (total < 0) return;   // offline: keep showing just the page number
                                int n = Math.Max(1, (total + DivoomCloud.PageSize - 1) / DivoomCloud.PageSize);
                                pageCounts[src] = n;
                                if (catBox.SelectedIndex - cats.Count - 1 != src) return;   // moved on to another source
                                pageLabel.Text = (cloudPage + 1) + " of " + n;
                                next.Enabled = cloudPage + 1 < n;
                            });
                        }
                        catch (InvalidOperationException) { }   // window closed meanwhile
                    });
                string key = src + ":" + page;
                List<GalleryItem> cached;
                if (pageCache.TryGetValue(key, out cached))
                {
                    view.BeginUpdate();
                    foreach (var it in cached) addItem(it);
                    view.EndUpdate();
                    status.Text = cached.Count + " designs from the Divoom cloud (duplicates hidden)";
                    return;
                }
                status.Text = "Loading " + CloudSources[src] + ", page " + (page + 1) + "...";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        var list = DivoomCloud.List(CloudCategory[src], src < 3, page);
                        var kept = new List<GalleryItem>();
                        var mine = new HashSet<string>();
                        int dupes = 0;
                        foreach (var e in list)
                        {
                            if (gen != generation) return;
                            List<int[]> fr;
                            lock (downloads)
                                if (!downloads.TryGetValue(e.Id, out fr))
                                {
                                    try { fr = DivoomCloud.Frames(e.Id); } catch (Exception) { continue; }
                                    downloads[e.Id] = fr;
                                }
                            // duplicates are judged by how they look on the box (after cropping and color snapping)
                            string sig = Signature(fr), nameKey = NameKey(e.Name);
                            bool dup;
                            lock (seen) dup = seen.Contains(sig) || (nameKey.Length > 0 && seen.Contains(nameKey));
                            if (dup || !mine.Add(sig) || (nameKey.Length > 0 && !mine.Add(nameKey))) { dupes++; continue; }
                            var it = new GalleryItem { Category = "Cloud", Name = e.Name };
                            foreach (var f in Distinct(fr)) it.Frames.Add(f);
                            kept.Add(it);
                            BeginInvoke((Action)delegate { if (gen == generation) addItem(it); });
                        }
                        if (gen != generation) return;
                        // only a fully loaded page counts toward "already seen", so a cancelled load never hides anything
                        lock (seen) foreach (var sig in mine) seen.Add(sig);
                        kept = SortedUnique(kept);
                        BeginInvoke((Action)delegate
                        {
                            pageCache[key] = kept;
                            if (gen != generation) return;
                            // items appeared as they downloaded; now put the page in A-Z order
                            view.BeginUpdate();
                            clearView();
                            foreach (var it in kept) addItem(it);
                            view.EndUpdate();
                        });
                        SetStatus(kept.Count + " designs from the Divoom cloud" + (dupes > 0 ? " (" + dupes + " duplicates hidden)" : "") +
                                  " — 11×11 art cropped to 10×10");
                    }
                    catch (Exception ex) { SetStatus("Divoom cloud unavailable: " + ex.Message); }
                });
            };
            catBox.SelectedIndexChanged += delegate { cloudPage = 0; load(); };
            prev.Click += delegate { if (cloudPage > 0) { cloudPage--; load(); } };
            next.Click += delegate { cloudPage++; load(); };

            // animate the selected animation's thumbnail
            preview.Tick += delegate
            {
                if (view.SelectedIndices.Count == 0) return;
                int i = view.SelectedIndices[0];
                if (i >= shown.Count) return;
                var it = shown[i];
                if (it.Frames.Count < 2) return;
                previewFrame = (previewFrame + 1) % it.Frames.Count;
                images.Images[i] = Proto.Render(it.Frames[previewFrame], Math.Max(1, thumb / 10));
                view.Invalidate(view.Items[i].Bounds);
            };
            preview.Start();

            Action send = delegate
            {
                if (view.SelectedIndices.Count == 0) { status.Text = "Pick a design first"; return; }
                var it = shown[view.SelectedIndices[0]];
                StopRotationForManual();
                if (it.IsAnimation) SendAnimation(Sample(it.Frames));
                else { var px = it.Frames[0]; Run(delegate { box.ShowImage(px); }, "Sent “" + it.Name + "”"); }
            };
            view.ItemActivate += delegate { send(); };

            p.Controls.Add(Btn("Send to AuraBox", 0, 462, 150, send, true));
            p.Controls.Add(Btn("Edit", 158, 462, 110, delegate
            {
                if (view.SelectedIndices.Count == 0) return;
                var it = shown[view.SelectedIndices[0]];
                grid.SetAll(it.Frames[0]);
                if (it.IsAnimation) { frames.Clear(); foreach (var f in Sample(it.Frames)) frames.Add((int[])f.Clone()); RefreshFrames(); }
                status.Text = "Loaded “" + it.Name + "” into Draw & Animate" + (it.IsAnimation ? " (picture and frames)" : "");
            }));
            var playAll = Btn("Play All Frames From PC", 276, 462, 200, null);
            playAll.Click += delegate
            {
                if (job != null && playingBtn == playAll) { TogglePlay(playAll, null, "Animation"); return; }
                if (view.SelectedIndices.Count == 0) { status.Text = "Pick an animation first"; return; }
                var it = shown[view.SelectedIndices[0]];
                if (!it.IsAnimation) { status.Text = "“" + it.Name + "” is a still picture"; return; }
                var all = new List<int[]>(it.Frames);
                int n = 0;
                TogglePlay(playAll, delegate { StreamLoop(delegate { return all[n++ % all.Count]; }, () => FrameSeconds(Settings.DefaultSpeed), DateTime.MaxValue); },
                           "“" + it.Name + "” (" + all.Count + " frames)");
            };
            p.Controls.Add(playAll);
            p.Controls.Add(Note("Double-click sends to the box (max 8 frames). Longer animations: Play All Frames From PC.", 2, 499));
            if (catBox.Items.Count > 0) catBox.SelectedIndex = 0;
            return p;
        }

        // Gallery lists run A to Z by name, and designs that share a name (ignoring case) show only once.
        // Name keys get a prefix so they never collide with look signatures in the shared "seen" set.
        // runs of spaces (including non-breaking ones) count as one, so "scattered  cloud" matches "scattered cloud"
        static string NameKey(string name)
        {
            string n = System.Text.RegularExpressions.Regex.Replace(name ?? "", @"\s+", " ").Trim().ToLowerInvariant();
            return n.Length == 0 ? "" : "name:" + n;
        }

        // unique: false keeps every design (My Favorites may hold several with one name)
        static List<GalleryItem> SortedUnique(IEnumerable<GalleryItem> list, bool unique = true)
        {
            var names = new HashSet<string>();
            var result = new List<GalleryItem>();
            foreach (var it in list) { string k = NameKey(it.Name); if (!unique || k.Length == 0 || names.Add(k)) result.Add(it); }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        // what an item looks like on the box; an animation whose frames are all identical counts as a still picture
        static string Signature(List<int[]> fr)
        {
            var sb = new StringBuilder();
            foreach (var f in Distinct(fr)) { foreach (int c in f) sb.Append(c); sb.Append(','); }
            return sb.ToString();
        }

        static List<int[]> Distinct(List<int[]> fr)
        {
            var first = Digits(fr[0]);
            foreach (var f in fr) if (Digits(f) != first) return fr;
            return new List<int[]> { fr[0] };
        }

        // the AuraBox holds 8 frames: longer animations are sampled evenly
        static List<int[]> Sample(List<int[]> all)
        {
            if (all.Count <= 8) return all;
            var list = new List<int[]>();
            for (int k = 0; k < 8; k++) list.Add(all[k * all.Count / 8]);
            return list;
        }

        // ---------- live animations: generated on this PC frame by frame, each with its own speed ----------
        Panel BuildLivePage()
        {
            var p = NewPage();
            var live = NewCard("Played From This PC", 0, 0, W, 250);
            var pick = new ListBox { Location = new Point(12, 40), Size = new Size(190, 196), IntegralHeight = false };
            AccentList(pick);
            foreach (var la in LiveAnims) pick.Items.Add(la.Name);
            live.Controls.Add(pick);
            var title = new Label { Location = new Point(218, 38), Size = new Size(W - 218 - 180, 26), Font = Theme.Semibold(12f), UseMnemonic = false };
            var about = new Label { Location = new Point(218, 68), Size = new Size(W - 218 - 180, 60) };
            livePlay = Btn("Play", 218, 126, 120, null, true);
            live.Controls.Add(new Label { Text = "Speed:", Location = new Point(218, 174), AutoSize = true });
            var speed = new TrackBar { Minimum = 1, Maximum = 10, Location = new Point(270, 166), Width = W - 270 - 186, TickStyle = TickStyle.None };
            var preview = new PixelGrid(15) { Location = new Point(W - 166, 40), ReadOnly = true };
            live.Controls.Add(title); live.Controls.Add(about); live.Controls.Add(livePlay); live.Controls.Add(speed); live.Controls.Add(preview);
            live.Controls.Add(Note("Speed is saved for each one, and also used in the playlist.", 218, 220));
            p.Controls.Add(live);

            Func<int[]> sim = null;
            bool picking = false;
            pick.SelectedIndexChanged += delegate
            {
                if (pick.SelectedIndex < 0) return;
                var la = LiveAnims[pick.SelectedIndex];
                title.Text = la.Name; about.Text = la.About; sim = null;
                picking = true; speed.Value = cfg.SpeedOf(la.Id); picking = false;
            };
            speed.ValueChanged += delegate
            {
                if (picking || pick.SelectedIndex < 0) return;
                cfg.SetSpeed(LiveAnims[pick.SelectedIndex].Id, speed.Value); SaveSettings();
            };
            pick.SelectedIndex = 0;
            livePlay.Click += delegate
            {
                var la = LiveAnims[pick.SelectedIndex];
                TogglePlay(livePlay, delegate { StreamLoop(la.Make(), LiveInterval(la.Id), DateTime.MaxValue); }, la.Name);
            };

            // preview the picked animation at its speed while this page is open
            var previewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            DateTime nextFrame = DateTime.MinValue;
            previewTimer.Tick += delegate
            {
                if (!p.Visible) { sim = null; return; }
                if (DateTime.Now < nextFrame || pick.SelectedIndex < 0) return;
                if (sim == null) sim = LiveAnims[pick.SelectedIndex].Make();
                preview.SetAll(sim());
                nextFrame = DateTime.Now.AddSeconds(FrameSeconds(speed.Value));
            };
            previewTimer.Start();

            p.Controls.Add(BuildLavaCard(0, 262));
            return p;
        }

        // frame time for a live animation, read on every frame so a speed change applies right away
        Func<double> LiveInterval(string id) { return () => FrameSeconds(cfg.SpeedOf(id)); }

        // seconds per live-animation frame for speed slider value v (1..10)
        static double FrameSeconds(int v) { return (0.42 - v * 0.035) * 0.8; }

        void SendAnimation(List<int[]> list)
        {
            var copy = new List<int[]>(list); int delay = (int)delayBox.Value;
            Run(delegate { box.ShowAnimation(copy, delay); }, "Animation sent (" + Math.Min(8, copy.Count) + " frames)");
        }

        // redraw the frame strip; the picked frame gets an accent-colored rim
        void RefreshFrames()
        {
            if (selFrame >= frames.Count) selFrame = frames.Count - 1;
            for (int k = 0; k < 8; k++)
            {
                if (frameSlots[k] == null) return;
                frameSlots[k].SetAll(k < frames.Count ? frames[k] : new int[100]);
                frameRims[k].BackColor = k == selFrame ? Theme.AccentFill : Color.Transparent;
            }
        }

        void LoadFrame()
        {
            if (selFrame >= 0 && selFrame < frames.Count) grid.SetAll(frames[selFrame]);
        }

        // frames generated on this PC and sent one at a time (snakes)
        void StreamLoop(Func<int[]> next, Func<double> interval, DateTime until)
        {
            while (!jobStop && Before(until))
            {
                int[] px = next();
                if (!JobSend(delegate { box.ShowImage(px); })) { SetStatus("Waiting for AuraBox connection..."); Wait(5); continue; }
                Wait(interval());
            }
        }

        // ---------- text ----------
        Panel BuildTextPage()
        {
            var p = NewPage();
            var card = NewCard("Scrolling Message", 0, 0, W, 300);
            textBox = new TextBox { Location = new Point(12, 38), Width = W - 24, Text = "I ❤️ U!" };
            card.Controls.Add(textBox);
            textColor = new ColorWheel { Location = new Point(12, 80), Size = new Size(150, 150), Value = 7 };
            card.Controls.Add(textColor);
            card.Controls.Add(new Label { Text = "Font:", Location = new Point(186, 86), AutoSize = true });
            textFont = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(240, 82), Width = 170 };
            textFont.Items.AddRange(new object[] { "Tahoma", "Arial", "Verdana", "Consolas", "Segoe UI" });
            textFont.SelectedIndex = 0;
            card.Controls.Add(textFont);
            card.Controls.Add(new Label { Text = "Speed:", Location = new Point(186, 130), AutoSize = true });
            textSpeed = new TrackBar { Minimum = 1, Maximum = 10, Value = 5, Location = new Point(236, 122), Width = 240, TickStyle = TickStyle.None };
            card.Controls.Add(textSpeed);
            textPlay = Btn("Play", 190, 176, 120, null, true);
            textPlay.Click += delegate
            {
                var strip = TextArt.Strip(textBox.Text, (string)textFont.SelectedItem);
                int color = textColor.Value;
                TogglePlay(textPlay, delegate { MarqueeLoop(strip, color, DateTime.MaxValue); }, "Text");
            };
            card.Controls.Add(textPlay);
            card.Controls.Add(Note("Type ❤ or ♥ for a red heart.\r\n" +
                                   "The text is sent frame by frame from this PC, so keep the app running (it can sit in the tray).", 12, 246));
            p.Controls.Add(card);
            return p;
        }

        void MarqueeLoop(int[,] strip, int color, DateTime until)
        {
            int total = strip.GetLength(0) + 10;
            while (!jobStop && Before(until))
            {
                for (int off = 0; off <= total && !jobStop && Before(until); off++)
                {
                    int[] px = TextArt.Window(strip, off, color);
                    if (!JobSend(delegate { box.ShowImage(px); })) { Wait(5); break; }
                    int speed = 5;
                    if (!OnUi(delegate { speed = textSpeed.Value; })) return;
                    Wait(0.45 - speed * 0.035);
                }
            }
        }

        // ---------- clock & weather ----------
        Panel BuildClockWeatherPage()
        {
            var p = NewPage();
            var card = NewCard("Clock", 0, 0, W, 176);
            var now = new Label { Location = new Point(12, 30), AutoSize = true, Font = Theme.Semibold(22f) };
            var tick = new System.Windows.Forms.Timer { Interval = 500 };
            tick.Tick += delegate { now.Text = DateTime.Now.ToString(H24 ? "H:mm" : "h:mm tt"); };
            card.Controls.Add(new Label { Text = "Hours:", Location = new Point(220, 44), AutoSize = true });
            var hours = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(274, 40), Width = 150 };
            hours.Items.AddRange(new object[] { "Like Windows", "12-hour", "24-hour" });
            hours.SelectedIndex = cfg.Clock24 + 1;
            hours.SelectedIndexChanged += delegate { cfg.Clock24 = clock24 = hours.SelectedIndex - 1; SaveSettings(); };
            card.Controls.Add(hours);
            tick.Start();
            card.Controls.Add(now);
            var face = new PixelGrid(8) { Location = new Point(W - 96, 34), ReadOnly = true };
            card.Controls.Add(face);
            var steady = new RadioButton { Text = "Steady clock drawn by this app (no blinking colon; keep the app running)",
                                           Location = new Point(14, 84), AutoSize = true, Checked = cfg.SteadyClock };
            var builtIn = new RadioButton { Text = "AuraBox built-in clock (blinking colon; works without the PC)",
                                            Location = new Point(14, 108), AutoSize = true, Checked = !cfg.SteadyClock };
            steady.CheckedChanged += delegate { cfg.SteadyClock = steady.Checked; SaveSettings(); };
            card.Controls.Add(steady); card.Controls.Add(builtIn);
            tick.Tick += delegate { face.Visible = steady.Checked; if (face.Visible) face.SetAll(ClockFace(DateTime.Now)); };
            card.Controls.Add(Btn("Show Clock Now", 12, 136, 150, delegate
            {
                StopRotationForManual();
                ShowClockNow();
            }, true));
            card.Controls.Add(Btn("Sync to PC Time", 170, 136, 150, delegate
            {
                Run(delegate { box.SetTime(); }, "Clock synced to " + DateTime.Now.ToString("t"));
            }));
            syncBox = new CheckBox { Text = "Sync the clock whenever the app connects", Location = new Point(334, 142), AutoSize = true, Checked = cfg.SyncOnConnect };
            card.Controls.Add(syncBox);
            p.Controls.Add(card);

            AddWeatherCard(p, 186);
            p.Controls.Add(Note("The clock re-syncs every 6 hours while the playlist runs; how long each item shows is set on the Playlist page.\r\n" +
                                "The Lava Clock's options are on the Animations page.", 2, 482));
            return p;
        }

        // the Lava Clock: its own options and a live preview (drawn on this PC only)
        Card BuildLavaCard(int x, int y)
        {
            var lava = NewCard("Lava Clock", x, y, W, 196);
            var lavaPreview = new PixelGrid(13) { Location = new Point(W - 150, 40), ReadOnly = true };
            lava.Controls.Add(lavaPreview);
            lava.Controls.Add(new Label { Text = "Colors:", Location = new Point(14, 46), AutoSize = true });
            var colors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(84, 42), Width = 220 };
            colors.Items.Add("A new theme every minute");
            foreach (string n in LavaClock.ThemeNames) colors.Items.Add(n);
            colors.SelectedIndex = Math.Max(0, Math.Min(colors.Items.Count - 1, cfg.LavaTheme + 1));
            colors.SelectedIndexChanged += delegate { cfg.LavaTheme = LavaClock.FixedTheme = colors.SelectedIndex - 1; SaveSettings(); };
            lava.Controls.Add(colors);
            var puddle = new CheckBox { Text = "Melt into a puddle that bubbles back up", Location = new Point(14, 84), AutoSize = true, Checked = cfg.LavaPuddle };
            puddle.CheckedChanged += delegate { cfg.LavaPuddle = LavaClock.Puddle = puddle.Checked; SaveSettings(); };
            lava.Controls.Add(puddle);
            lava.Controls.Add(Btn("Show Lava Clock Now", 12, 124, 190, delegate { PlayLive("lavaclock"); }, true));
            lava.Controls.Add(Note("Uses the 12/24-hour setting on the Clock & Weather page.", 14, 164));
            LavaClock lavaSim = null; bool lavaH24 = H24;
            var lavaTick = new System.Windows.Forms.Timer { Interval = 100 };
            lavaTick.Tick += delegate
            {
                if (!lava.Visible) { lavaSim = null; return; }   // false while its page is hidden
                if (lavaSim == null || lavaH24 != H24) { lavaH24 = H24; lavaSim = new LavaClock(lavaH24); }
                lavaPreview.SetAll(lavaSim.Next());
            };
            lavaTick.Start();
            return lava;
        }

        static volatile int clock24 = -1;   // copy of Settings.Clock24 for the static live-animation table
        static bool H24
        {
            get
            {
                if (clock24 >= 0) return clock24 == 1;
                return System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
            }
        }

        static int[] ClockFace(DateTime t) { return ClockArt.Face(t, H24, 7, 6); }   // white hours, cyan minutes

        void ShowClockNow()
        {
            if (cfg.SteadyClock) { StartJob(delegate { ClockLoop(DateTime.MaxValue); }); showing = "Clock"; status.Text = "Showing the steady clock"; }
            else Run(delegate { box.SetMode(0); }, "Showing the AuraBox clock");
        }

        // steady clock: redraw whenever the minute changes, until the end time
        void ClockLoop(DateTime until)
        {
            int shown = -1;
            while (!jobStop && Before(until))
            {
                DateTime now = DateTime.Now;
                int stamp = now.Hour * 60 + now.Minute;
                if (stamp != shown)
                {
                    int[] px = ClockFace(now);
                    if (JobSend(delegate { box.ShowImage(px); })) shown = stamp;
                    else { SetStatus("Waiting for AuraBox connection..."); Wait(5); continue; }
                }
                Wait(1);
            }
        }

        // ---------- weather ----------
        // weather: location, what's showing (from Open-Meteo), and how it is shown; one card on the Clock & Weather page
        void AddWeatherCard(Panel p, int y)
        {
            var now = NewCard("Weather (Open-Meteo)", 0, y, W, 286);
            placeBox = new TextBox { Location = new Point(12, 34), Width = 220, Text = cfg.Place };
            placeBox.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;   // no "ding"
                LookUpPlace();
            };
            now.Controls.Add(placeBox);
            now.Controls.Add(Btn("Look Up", 240, 32, 90, LookUpPlace));
            placeLabel = new Label { Location = new Point(340, 38), AutoSize = true, Tag = "subtle" };
            now.Controls.Add(placeLabel);
            UpdatePlaceLabel();

            weatherIcon = new PixelGrid(9) { Location = new Point(12, 72), ReadOnly = true };
            weatherTemp = new PixelGrid(9) { Location = new Point(112, 72), ReadOnly = true };
            now.Controls.Add(weatherIcon); now.Controls.Add(weatherTemp);
            weatherLabel = new Label { Location = new Point(220, 72), Size = new Size(W - 232, 54), Text = "Not loaded yet" };
            now.Controls.Add(weatherLabel);
            now.Controls.Add(Btn("Show Weather Now", 220, 130, 160, delegate
            {
                // one weather period now, then back to the clock (or on with the playlist)
                ShowWeatherNow();
            }, true));
            now.Controls.Add(Btn("Refresh", 388, 130, 100, delegate { ThreadPool.QueueUserWorkItem(delegate { GetWeather(true); }); }));
            p.Controls.Add(now);
            var anim = new System.Windows.Forms.Timer { Interval = 300 };
            int bx = 0, by = 0, vx = 1, vy = 1, ix = 0, iy = 0, ivx = 1, ivy = 1;
            anim.Tick += delegate
            {
                if (!p.Visible) return;
                if (weatherFrames.Count >= 2 && cfg.AnimatedIcons) weatherFrame = (weatherFrame + 1) % weatherFrames.Count;
                if (weatherFrames.Count > 0)
                {
                    int[] px = weatherFrames[cfg.AnimatedIcons ? weatherFrame : 0];
                    if (cfg.BounceTemp)
                    {
                        // the same bounce the box shows
                        int loX, hiX, loY, hiY;
                        WeatherArt.IconRoom(weatherFrames, out loX, out hiX, out loY, out hiY);
                        if (ix + ivx < loX || ix + ivx > hiX) ivx = -ivx; ix = Math.Max(loX, Math.Min(hiX, ix + ivx));
                        if (iy + ivy < loY || iy + ivy > hiY) ivy = -ivy; iy = Math.Max(loY, Math.Min(hiY, iy + ivy));
                        px = WeatherArt.Shift(px, ix, iy);
                    }
                    weatherIcon.SetAll(px);
                }
                WeatherInfo w;
                lock (weatherGate) w = weather;
                if (w == null) return;
                if (!cfg.BounceTemp) { weatherTemp.SetAll(WeatherArt.Temperature(w)); return; }
                // the same bounce the box shows
                int roomX, roomY;
                weatherTemp.SetAll(WeatherArt.Temperature(w, bx, by, out roomX, out roomY));
                if (roomX > 0) { if (bx + vx < 0 || bx + vx > roomX) vx = -vx; bx = Math.Max(0, Math.Min(roomX, bx + vx)); }
                if (roomY > 0) { if (by + vy < 0 || by + vy > roomY) vy = -vy; by = Math.Max(0, Math.Min(roomY, by + vy)); }
            };
            anim.Start();

            now.Controls.Add(new Label { Text = "Switch between icon and temperature every", Location = new Point(12, 180), AutoSize = true });
            var flip = new NumericUpDown { Minimum = 2, Maximum = 60, Value = Math.Max(2, Math.Min(60, cfg.WeatherFlip)), Location = new Point(290, 177), Width = 56 };
            flip.ValueChanged += delegate { cfg.WeatherFlip = (int)flip.Value; };
            now.Controls.Add(flip);
            now.Controls.Add(new Label { Text = "seconds", Location = new Point(352, 180), AutoSize = true });
            var animBox = new CheckBox { Text = "Animated icons (falling rain, drifting snow, lightning...)", Location = new Point(14, 212), AutoSize = true,
                                         Checked = cfg.AnimatedIcons };
            animBox.CheckedChanged += delegate { cfg.AnimatedIcons = animBox.Checked; if (weatherFrames.Count > 0) weatherIcon.SetAll(weatherFrames[0]); };
            now.Controls.Add(animBox);
            var bounceBox = new CheckBox { Text = "Bounce the icon and temperature around the screen", Location = new Point(14, 242), AutoSize = true,
                                           Checked = cfg.BounceTemp };
            bounceBox.CheckedChanged += delegate { cfg.BounceTemp = bounceBox.Checked; SaveSettings(); };
            now.Controls.Add(bounceBox);

            // one unit for everything: the weather and the box's own thermometer (Temperature mode)
            now.Controls.Add(new Label { Text = "Temperature:", Location = new Point(480, 180), AutoSize = true });
            RadioRow(now, new[] { "Fahrenheit", "Celsius" }, new[] { 480, 590 }, 210, cfg.Fahrenheit ? 0 : 1, delegate (int k)
            {
                bool f = k == 0;
                cfg.Fahrenheit = cfg.ThermoFahrenheit = f; SaveSettings();
                lock (weatherGate) weather = null;
                ThreadPool.QueueUserWorkItem(delegate { GetWeather(true); });
                if (box.Connected) Run(delegate { box.SetTempUnit(f); }, "Temperature unit: " + (f ? "Fahrenheit" : "Celsius"));
            });
            now.Controls.Add(Note("Also the box's own thermometer.", 480, 244));
        }

        // one weather period now, then back to the clock (or on with the playlist)
        void ShowWeatherNow()
        {
            bool repeat = rotateBox.Checked;
            StartJob(delegate { PlaylistLoop("weather", repeat); });
        }

        void UpdatePlaceLabel()
        {
            placeLabel.Text = cfg.Lat.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                              cfg.Lon.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        void LookUpPlace()
        {
            string q = placeBox.Text.Trim();
            if (q.Length == 0) return;
            status.Text = "Looking up " + q + "...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    double lat, lon;
                    string name = Weather.Geocode(q, out lat, out lon);
                    BeginInvoke((Action)delegate
                    {
                        cfg.Place = name; cfg.Lat = lat; cfg.Lon = lon;
                        placeBox.Text = name; UpdatePlaceLabel(); SaveSettings();
                        lock (weatherGate) weather = null;
                        status.Text = "Location set to " + name;
                    });
                    GetWeather(true);
                }
                catch (Exception e) { SetStatus("Location lookup failed: " + e.Message); }
            });
        }

        WeatherInfo GetWeather(bool force)
        {
            lock (weatherGate)
            {
                if (!force && weather != null && (DateTime.Now - weather.Fetched).TotalMinutes < 10) return weather;
            }
            try
            {
                var w = Weather.Fetch(cfg.Lat, cfg.Lon, cfg.Fahrenheit);
                lock (weatherGate) weather = w;
                BeginInvoke((Action)delegate
                {
                    weatherFrames = WeatherArt.IconFrames(w); weatherFrame = 0;
                    weatherIcon.SetAll(weatherFrames[0]);
                    weatherTemp.SetAll(WeatherArt.Temperature(w));
                    weatherLabel.Text = cfg.Place + "\r\n" + Math.Round(w.Temp) + "°" + w.Unit + ", " + w.Description +
                                        "\r\nUpdated " + w.Fetched.ToString("t");
                });
                return w;
            }
            catch (Exception e)
            {
                SetStatus("Weather update failed: " + e.Message);
                lock (weatherGate) return weather;  // last known, may be null
            }
        }

        // one weather period: icon and temperature alternate until the end time
        void WeatherPeriod(DateTime end)
        {
            var w = GetWeather(false);
            if (w == null) { SetStatus("Weather unavailable — retrying later"); WaitUntil(end); return; }
            SetStatus("Weather — " + Math.Round(w.Temp) + "°" + w.Unit + ", " + w.Description);
            var icon = WeatherArt.IconFrames(w);
            int[] temp = WeatherArt.Temperature(w);
            bool showIcon = true;
            // the temperature drifts and bounces off the edges, starting from a random corner
            var rng = new Random();
            int roomX, roomY;
            WeatherArt.Temperature(w, 0, 0, out roomX, out roomY);
            int bx = rng.Next(roomX + 1), by = rng.Next(roomY + 1);
            int vx = rng.Next(2) == 0 ? -1 : 1, vy = rng.Next(2) == 0 ? -1 : 1;
            // the icon bounces too, from where it's drawn
            int loX, hiX, loY, hiY;
            WeatherArt.IconRoom(icon, out loX, out hiX, out loY, out hiY);
            int ix = 0, iy = 0, ivx = rng.Next(2) == 0 ? -1 : 1, ivy = rng.Next(2) == 0 ? -1 : 1;
            while (!jobStop && Before(end))
            {
                DateTime phaseEnd = DateTime.Now.AddSeconds(Math.Min(cfg.WeatherFlip, Math.Max(0.1, (end - DateTime.Now).TotalSeconds)));
                bool ok;
                bool animate = cfg.AnimatedIcons && icon.Count > 1;
                if (showIcon && (animate || cfg.BounceTemp))
                {
                    // send the icon frame by frame (like live drawing) rather than uploading an animation every few seconds
                    ok = true;
                    for (int f = 0; ok && !jobStop && Before(phaseEnd); f++)
                    {
                        int[] px = icon[animate ? f % icon.Count : 0];
                        if (cfg.BounceTemp)
                        {
                            px = WeatherArt.Shift(px, ix, iy);
                            if (ix + ivx < loX || ix + ivx > hiX) ivx = -ivx; ix += ivx;
                            if (iy + ivy < loY || iy + ivy > hiY) ivy = -ivy; iy += ivy;
                        }
                        ok = JobSend(delegate { box.ShowImage(px); });
                        if (ok) Wait(0.3);
                    }
                }
                else if (!showIcon && cfg.BounceTemp && roomX + roomY > 0)
                {
                    ok = true;
                    while (ok && !jobStop && Before(phaseEnd))
                    {
                        int[] px = WeatherArt.Temperature(w, bx, by, out roomX, out roomY);
                        ok = JobSend(delegate { box.ShowImage(px); });
                        if (ok) Wait(0.25);
                        if (roomX > 0) { if (bx + vx < 0 || bx + vx > roomX) vx = -vx; bx += vx; }
                        if (roomY > 0) { if (by + vy < 0 || by + vy > roomY) vy = -vy; by += vy; }
                    }
                }
                else
                {
                    int[] px = showIcon ? icon[0] : temp;
                    ok = JobSend(delegate { box.ShowImage(px); });
                    if (ok) WaitUntil(phaseEnd);
                }
                if (!ok) { SetStatus("Waiting for AuraBox connection..."); Wait(10); continue; }
                showIcon = !showIcon;
            }
        }

        // ---------- playlist ----------
        // Turned-on items are listed first, in play order, with arrows to reorder them. Turned-off items wait below in
        // sections (Clock & Weather, Animations, Original Modes, Your Content); turning one on adds it to the end.
        Panel BuildPlaylistPage()
        {
            var p = NewPage();
            rotateBox = new CheckBox { Text = "Run the playlist", Location = new Point(2, 2), AutoSize = true, Font = Theme.Semibold(10.5f) };
            rotateBox.CheckedChanged += delegate
            {
                if (!manualPause) cfg.Rotate = rotateBox.Checked;   // remembered across restarts (and rebuilds: it lives in the .ini)
                if (rotateBox.Checked) { StartJob(delegate { PlaylistLoop(null, true); }); status.Text = "Playlist running"; }
                else { StopJob(); status.Text = "Playlist stopped"; }
                SaveSettings();
            };
            p.Controls.Add(rotateBox);
            p.Controls.Add(Btn("Skip to Next Item  ›", W - 190, 0, 190, delegate
            {
                if (!rotateBox.Checked) { status.Text = "Start the playlist first"; return; }
                skipItem = true;
                status.Text = "Skipping to the next item...";
            }));   // a plain button: running the playlist is this page's main action
            p.Controls.Add(Note("Turned-on items play top to bottom, then start over. Keep the app running (tray is fine).", 2, 32));
            var onlyOn = new CheckBox { Text = "Show only turned-on items", Location = new Point(W - 190, 36), AutoSize = true, Checked = cfg.OnlyOnItems };
            onlyOn.CheckedChanged += delegate { cfg.OnlyOnItems = onlyOn.Checked; SaveSettings(); LayoutPlayRows(); };
            p.Controls.Add(onlyOn);

            var card = NewCard(null, 0, 62, W, H - 62);
            playRowsPanel = new Panel { Location = new Point(1, 7), Size = new Size(W - 2, H - 62 - 14), AutoScroll = true };
            card.Controls.Add(playRowsPanel);
            playHeaders["on"] = Header("Playing, In Order");
            foreach (string g in PlayItem.Groups) playHeaders[g] = Header("Turn On More: " + g);
            foreach (var it in cfg.Playlist)
            {
                string id = it.Id;
                var r = new PlayRow
                {
                    Id = id,
                    On = new CheckBox { Size = new Size(300, 24) },
                    Amount = new NumericUpDown { Minimum = 1, Maximum = 999, Width = 64 },
                    Unit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 96 },
                    Up = Btn("", 0, 0, 34, delegate { MoveItem(id, -1); }),
                    Down = Btn("", 0, 0, 34, delegate { MoveItem(id, 1); })
                };
                r.Unit.Items.AddRange(new object[] { "seconds", "minutes" });
                r.Up.Font = r.Down.Font = new Font(Theme.IconFont, 8f);
                playRowsPanel.Controls.Add(r.On); playRowsPanel.Controls.Add(r.Amount); playRowsPanel.Controls.Add(r.Unit);
                playRowsPanel.Controls.Add(r.Up); playRowsPanel.Controls.Add(r.Down);
                playRows.Add(r);
            }
            p.Controls.Add(card);
            RefreshPlayRows();
            LayoutPlayRows();

            // highlight the item on screen
            var bold = Theme.Semibold(9f);
            var nowTimer = new System.Windows.Forms.Timer { Interval = 500 };
            nowTimer.Tick += delegate
            {
                string id = currentItem;
                foreach (var r in playRows)
                {
                    bool on = id != null && r.Id == id;
                    r.On.ForeColor = on ? Theme.AccentFill : Theme.Text;
                    if (highlightedItem != id) r.On.Font = on ? bold : Font;
                }
                highlightedItem = id;
            };
            nowTimer.Start();
            foreach (var r0 in playRows)
            {
                var r = r0;
                r.On.CheckedChanged += delegate { if (!refreshingRows) { ReadPlayRows(); TurnedOnOrOff(r.Id, r.On.Checked); } };
                r.Amount.ValueChanged += delegate { if (!refreshingRows) ReadPlayRows(); };
                r.Unit.SelectedIndexChanged += delegate { if (!refreshingRows) ReadPlayRows(); };
            }
            return p;
        }

        Panel playRowsPanel;
        readonly Dictionary<string, Label> playHeaders = new Dictionary<string, Label>();
        bool prepared;   // set once the window has been scaled for the display; later layout must scale by hand

        Label Header(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = Theme.Semibold(9f), Tag = "subtle", UseMnemonic = false };
            playRowsPanel.Controls.Add(l);
            return l;
        }

        int L(int v) { return prepared ? Theme.Px(v) : v; }

        void Place(Control c, int x, int y) { c.Location = new Point(L(x), L(y) + playRowsPanel.AutoScrollPosition.Y); }

        // positions every row: turned-on items first in play order, then the turned-off ones by section
        void LayoutPlayRows()
        {
            playRowsPanel.SuspendLayout();
            int y = 6;
            var on = new List<PlayRow>();
            lock (playlistGate)
                foreach (var it in cfg.Playlist)
                    if (it.On) on.Add(RowFor(it.Id));
            Place(playHeaders["on"], 14, y); y += 26;
            for (int k = 0; k < on.Count; k++) { PlaceRow(on[k], y, true, k > 0, k < on.Count - 1); y += 31; }
            if (on.Count == 0) y += 4;
            foreach (string g in PlayItem.Groups)
            {
                var rows = new List<PlayRow>();
                lock (playlistGate)
                    foreach (var it in cfg.Playlist)
                        if (!it.On && it.Group == g) rows.Add(RowFor(it.Id));
                bool show = !cfg.OnlyOnItems && rows.Count > 0;
                playHeaders[g].Visible = show;
                if (show) { y += 10; Place(playHeaders[g], 14, y); y += 26; }
                foreach (var r in rows) { PlaceRow(r, y, show, false, false); if (show) y += 31; }
            }
            playRowsPanel.ResumeLayout();
        }

        void PlaceRow(PlayRow r, int y, bool visible, bool canUp, bool canDown)
        {
            r.On.Visible = r.Amount.Visible = r.Unit.Visible = visible;
            r.Up.Visible = r.Down.Visible = visible && r.On.Checked;
            r.Up.Enabled = canUp; r.Down.Enabled = canDown;
            if (!visible) return;
            Place(r.On, 14, y + 2); Place(r.Amount, 330, y); Place(r.Unit, 402, y);
            Place(r.Up, 506, y - 1); Place(r.Down, 544, y - 1);   // right after the time, not off at the far edge
        }

        PlayRow RowFor(string id) { foreach (var r in playRows) if (r.Id == id) return r; return null; }

        // a newly turned-on item joins the end of the play order
        void TurnedOnOrOff(string id, bool on)
        {
            if (on)
                lock (playlistGate)
                {
                    var it = PlayItem.Find(cfg.Playlist, id);
                    cfg.Playlist.Remove(it);
                    int last = -1;
                    for (int k = 0; k < cfg.Playlist.Count; k++) if (cfg.Playlist[k].On) last = k;
                    cfg.Playlist.Insert(last + 1, it);
                }
            SaveSettings();
            LayoutPlayRows();
        }

        bool refreshingRows;

        void RefreshPlayRows()
        {
            refreshingRows = true;
            lock (playlistGate)
                foreach (var it in cfg.Playlist)
                {
                    var r = RowFor(it.Id);
                    if (r == null) continue;
                    r.On.Text = it.Name; r.On.Checked = it.On;
                    bool minutes = it.Seconds >= 60 && it.Seconds % 60 == 0;
                    r.Unit.SelectedIndex = minutes ? 1 : 0;
                    r.Amount.Value = Math.Min(999, minutes ? it.Seconds / 60 : it.Seconds);
                }
            refreshingRows = false;
        }

        void ReadPlayRows()
        {
            lock (playlistGate)
                foreach (var it in cfg.Playlist)
                {
                    var r = RowFor(it.Id);
                    if (r == null) continue;
                    it.On = r.On.Checked;
                    it.Seconds = Math.Max(5, (int)r.Amount.Value * (r.Unit.SelectedIndex == 1 ? 60 : 1));
                }
            SaveSettings();
        }

        // swap with the previous / next turned-on item
        void MoveItem(string id, int dir)
        {
            lock (playlistGate)
            {
                var list = cfg.Playlist;
                int i = list.IndexOf(PlayItem.Find(list, id)), j = i + dir;
                while (j >= 0 && j < list.Count && !list[j].On) j += dir;
                if (i < 0 || j < 0 || j >= list.Count) return;
                var it = list[i]; list[i] = list[j]; list[j] = it;
            }
            SaveSettings();
            LayoutPlayRows();
            highlightedItem = "";   // redraw the highlight on the next tick
        }

        // first: show this item once before the playlist (e.g. "Show weather now"); repeat: keep cycling
        void PlaylistLoop(string first, bool repeat)
        {
            DateTime lastSync = DateTime.Now;
            if (first != null)
            {
                PlayItem it;
                lock (playlistGate) it = PlayItem.Find(cfg.Playlist, first);
                RunItem(it, ref lastSync);
                if (!repeat)
                {
                    currentItem = null;
                    if (jobStop) return;
                    SetStatus("Back to clock"); showing = "Clock";
                    if (cfg.SteadyClock) ClockLoop(DateTime.MaxValue);
                    else JobSend(delegate { box.SetMode(0); });
                    return;
                }
            }
            while (!jobStop)
            {
                var items = new List<PlayItem>();
                lock (playlistGate) foreach (var it in cfg.Playlist) if (it.On) items.Add(new PlayItem { Id = it.Id, Name = it.Name, Seconds = it.Seconds });
                if (items.Count == 0) { SetStatus("The playlist is empty: turn on at least one item"); Wait(5); continue; }
                foreach (var it in items) { if (jobStop) break; RunItem(it, ref lastSync); }
            }
            if (box.Connected) try { box.SetMode(0); } catch (Exception) { }
        }

        void RunItem(PlayItem it, ref DateTime lastSync)
        {
            skipItem = false;
            DateTime end = DateTime.Now.AddSeconds(it.Seconds);
            if ((DateTime.Now - lastSync).TotalHours >= 6 && JobSend(delegate { box.SetTime(); })) lastSync = DateTime.Now;
            currentItem = it.Id; currentEnd = end;
            jobStatus = "Playlist: " + it.Name + " until " + end.ToString("t");
            if (it.Id != "weather") SetStatus(jobStatus);
            foreach (var la in LiveAnims) if (la.Id == it.Id) { StreamLoop(la.Make(), LiveInterval(la.Id), end); return; }
            switch (it.Id)
            {
                case "weather": WeatherPeriod(end); return;
                case "clock": if (cfg.SteadyClock) { ClockLoop(end); return; } break;
                case "text":
                    int[,] strip = null; int color = 7;
                    if (!OnUi(delegate { strip = TextArt.Strip(textBox.Text, (string)textFont.SelectedItem); color = textColor.Value; })) return;
                    MarqueeLoop(strip, color, end);
                    return;
            }
            Action show = null;
            if (it.Id == "clock") show = delegate { box.SetMode(0); };
            else if (it.Id.StartsWith("mode")) { int m = it.Id[4] - '0'; show = delegate { box.SetMode(m); }; }
            else if (it.Id == "drawing") { int[] px = null; if (!OnUi(delegate { px = (int[])grid.Pixels.Clone(); })) return; show = delegate { box.ShowImage(px); }; }
            else if (it.Id == "animation")
            {
                List<int[]> copy = null; int delay = 5;
                if (!OnUi(delegate { copy = new List<int[]>(frames); delay = (int)delayBox.Value; }) || copy.Count == 0) return;
                show = delegate { box.ShowAnimation(copy, delay); };
            }
            if (show == null) return;
            if (!JobSend(show)) { SetStatus("Waiting for AuraBox connection..."); Wait(30); return; }
            WaitUntil(end);
        }

        // ---------- alarm & sleep ----------
        Panel BuildAlarmPage()
        {
            var p = NewPage();
            var ag = NewCard("Alarm", 0, 0, W, 130);
            // the last values sent are remembered (the box can't be asked for them)
            var alarmOn = new CheckBox { Text = "Alarm on", Location = new Point(14, 42), AutoSize = true, Checked = cfg.AlarmOn };
            var hour = new TwoDigitUpDown { Minimum = 0, Maximum = 23, Value = Math.Max(0, Math.Min(23, cfg.AlarmHour)), Location = new Point(120, 39), Width = 54 };
            var minute = new TwoDigitUpDown { Minimum = 0, Maximum = 59, Value = Math.Max(0, Math.Min(59, cfg.AlarmMinute)), Location = new Point(190, 39), Width = 54 };
            ag.Controls.Add(alarmOn); ag.Controls.Add(hour); ag.Controls.Add(minute);
            ag.Controls.Add(new Label { Text = ":", Location = new Point(177, 42), AutoSize = true });
            ag.Controls.Add(Note("24-hour time", 252, 42));
            var alarmScene = SceneBox(new Point(420, 39), cfg.AlarmScene);
            ag.Controls.Add(new Label { Text = "Scene:", Location = new Point(366, 42), AutoSize = true }); ag.Controls.Add(alarmScene);
            ag.Controls.Add(Btn("Set Alarm", 12, 84, 130, delegate
            {
                int h = (int)hour.Value, m = (int)minute.Value, sc = alarmScene.SelectedIndex; bool on = alarmOn.Checked;
                cfg.AlarmHour = h; cfg.AlarmMinute = m; cfg.AlarmScene = sc; cfg.AlarmOn = on; SaveSettings();
                Run(delegate { box.SetAlarm(h, m, sc, on); }, on ? "Alarm set for " + DateTime.Today.AddHours(h).AddMinutes(m).ToString("t") : "Alarm turned off");
            }, true));
            p.Controls.Add(ag);

            var sg = NewCard("Sleep Timer (Light and Sound Fade Off)", 0, 142, W, 130);
            var sleepOn = new CheckBox { Text = "Sleep on", Location = new Point(14, 42), AutoSize = true, Checked = cfg.SleepOn };
            var sleepMin = new NumericUpDown { Minimum = 1, Maximum = 120, Value = Math.Max(1, Math.Min(120, cfg.SleepMinutes)), Location = new Point(120, 39), Width = 60 };
            var sleepScene = SceneBox(new Point(420, 39), cfg.SleepScene);
            sg.Controls.Add(sleepOn); sg.Controls.Add(sleepMin);
            sg.Controls.Add(new Label { Text = "minutes", Location = new Point(186, 42), AutoSize = true });
            sg.Controls.Add(new Label { Text = "Scene:", Location = new Point(366, 42), AutoSize = true }); sg.Controls.Add(sleepScene);
            sg.Controls.Add(Btn("Start Sleep Timer", 12, 84, 150, delegate
            {
                int mins = (int)sleepMin.Value, sc = sleepScene.SelectedIndex; bool on = sleepOn.Checked;
                cfg.SleepMinutes = mins; cfg.SleepScene = sc; cfg.SleepOn = on; SaveSettings();
                Run(delegate { box.SetSleep(mins, sc, on); }, on ? "Sleep timer: " + mins + " minutes" : "Sleep timer off");
            }, true));
            p.Controls.Add(sg);
            p.Controls.Add(Note("Alarm and sleep run on the AuraBox itself; the app does not need to stay open for these.", 2, 284));
            return p;
        }

        static ComboBox SceneBox(Point at, int scene)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = at, Width = 120 };
            c.Items.AddRange(new object[] { "Scene 1", "Scene 2", "Scene 3", "Scene 4" });
            c.SelectedIndex = Math.Max(0, Math.Min(3, scene));
            return c;
        }

        // ---------- settings ----------
        Panel BuildSettingsPage()
        {
            var p = NewPage();
            // appearance and audio input share one card; each row is its own radio group
            var general = NewCard("General", 0, 0, W, 104);
            general.Controls.Add(new Label { Text = "Appearance:", Location = new Point(14, 42), AutoSize = true });
            string[] modes = { "system", "light", "dark" };
            RadioRow(general, new[] { "Use my Windows setting", "Light", "Dark" }, new[] { 120, 310, 390 }, 40, Array.IndexOf(modes, cfg.ThemeMode),
                     delegate (int k) { cfg.ThemeMode = modes[k]; SaveSettings(); ApplyTheme(); });

            // sent on every connect; with no cable in the AUX jack the box stays on Bluetooth
            general.Controls.Add(new Label { Text = "Audio input:", Location = new Point(14, 72), AutoSize = true });
            RadioRow(general, new[] { "AUX (audio jack)", "Bluetooth" }, new[] { 120, 310 }, 70, cfg.AudioAux ? 0 : 1, delegate (int k)
            {
                bool aux = k == 0;
                cfg.AudioAux = aux; SaveSettings();
                if (box.Connected) Run(delegate { box.SetAudioInput(aux); }, "Audio input: " + (aux ? "AUX" : "Bluetooth"));
            });
            general.Controls.Add(Note("(AUX needs a cable in the jack)", 400, 72));
            p.Controls.Add(general);

            var win = NewCard("Window and Startup", 0, 114, W, 182);
            var trayBox = new CheckBox { Text = "Minimize and close (X) go to the system tray. Quit with Exit in the tray menu.", Location = new Point(14, 38),
                                         AutoSize = true, Checked = cfg.MinimizeToTray };
            trayBox.CheckedChanged += delegate { cfg.MinimizeToTray = trayBox.Checked; SaveSettings(); };
            win.Controls.Add(trayBox);
            win.Controls.Add(new Label { Text = "When Windows starts:", Location = new Point(14, 68), AutoSize = true });
            // off: no Windows notifications from the app (the startup one and the "still running" one after X)
            var notifyBox = new CheckBox { Text = "Show notifications at startup", Location = new Point(330, 66), AutoSize = true, Checked = cfg.ShowNotifications };
            notifyBox.CheckedChanged += delegate { cfg.ShowNotifications = notifyBox.Checked; SaveSettings(); };
            win.Controls.Add(notifyBox);
            string run = ReadRunValue();
            int startChoice = run.Length == 0 ? 0 : run.Contains("--tray") ? 2 : 1;
            string[] starts = { "Don't start AuraBox Control", "Open AuraBox Control",
                                "Start in the system tray (connects and runs the playlist without showing this window)" };
            for (int k = 0; k < 3; k++)
            {
                int choice = k;
                var rb = new RadioButton { Text = starts[k], Location = new Point(14, 90 + k * 22), AutoSize = true, Checked = k == startChoice };
                rb.CheckedChanged += delegate
                {
                    if (!rb.Checked) return;
                    try
                    {
                        if (choice == 0) SetStartWithWindows(false); else SetStartWithWindows(true, choice == 2);
                        cfg.StartInTray = choice == 2; SaveSettings();
                        status.Text = choice == 0 ? "Won't start with Windows" : choice == 1 ? "Will open when Windows starts" : "Will start in the tray with Windows";
                    }
                    catch (Exception e) { status.Text = "Could not change startup setting: " + e.Message; }
                };
                win.Controls.Add(rb);
            }
            win.Controls.Add(Note("When AuraBox Control isn't running or connected, the AuraBox goes back to its built-in clock.", 14, 158));
            p.Controls.Add(win);

            var backup = NewCard("Backup and Restore", 0, 306, W, 72);
            backup.Controls.Add(Btn("Export Settings...", 12, 34, 162, delegate { ExportSettings(); }));
            backup.Controls.Add(Btn("Import Settings...", 182, 34, 162, delegate { ImportSettings(); }));
            backup.Controls.Add(Note("Settings and favorites are in the settings folder next\r\nto the program. Export puts them, with the playlist, in one file.", 360, 32));
            p.Controls.Add(backup);

            var about = NewCard("About", 0, 388, W, 124);
            about.Controls.Add(new Label { Text = "AuraBox Control — a portable app for the Divoom AuraBox over Bluetooth.", Location = new Point(14, 34), AutoSize = true });
            about.Controls.Add(Note("This is an independent app. It is not affiliated with, endorsed by or supported by\r\nDivoom, the maker of the AuraBox.", 14, 56));
            about.Controls.Add(Note("Some features, like live weather and the Divoom cloud gallery, need an internet connection.", 14, 94));
            p.Controls.Add(about);
            return p;
        }

        // a row of radio buttons that acts as its own group, so several rows can share one card
        static void RadioRow(Control parent, string[] labels, int[] xs, int y, int selected, Action<int> picked)
        {
            var row = new List<RadioButton>();
            for (int k = 0; k < labels.Length; k++)
            {
                int choice = k;
                var rb = new RadioButton { Text = labels[k], Location = new Point(xs[k], y), AutoSize = true, AutoCheck = false, Checked = k == selected };
                rb.Click += delegate
                {
                    if (rb.Checked) return;
                    foreach (var other in row) other.Checked = other == rb;
                    picked(choice);
                };
                row.Add(rb);
                parent.Controls.Add(rb);
            }
        }

        void SaveSettings()
        {
            if (restoring) return;   // a restored backup is on disk; don't write over it while closing
            if (syncBox != null) cfg.SyncOnConnect = syncBox.Checked;
            lock (playlistGate) cfg.Save();
        }

        // ---------- backup ----------
        // a backup is the ini text, then a [favorites] line, then favorites.txt
        const string FavoritesMark = "[favorites]";
        bool restoring;

        void ExportSettings()
        {
            using (var dlg = new SaveFileDialog { Filter = "AuraBox Control backup (*.txt)|*.txt", FileName = "AuraBox Control Backup " + DateTime.Now.ToString("yyyy-MM-dd") + ".txt" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SaveSettings();
                    string text;
                    lock (playlistGate) text = cfg.Text();
                    string fav = Settings.DataFile("favorites.txt");
                    text += FavoritesMark + "\r\n" + (File.Exists(fav) ? File.ReadAllText(fav) : "");
                    File.WriteAllText(dlg.FileName, text);
                    status.Text = "Settings exported to " + Path.GetFileName(dlg.FileName);
                }
                catch (Exception e) { status.Text = "Export failed: " + e.Message; }
            }
        }

        void ImportSettings()
        {
            using (var dlg = new OpenFileDialog { Filter = "AuraBox Control backup (*.txt)|*.txt|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string[] lines = File.ReadAllLines(dlg.FileName);
                    int mark = Array.IndexOf(lines, FavoritesMark);
                    var settingLines = new List<string>(lines);
                    var favLines = new List<string>();
                    if (mark >= 0) { favLines = settingLines.GetRange(mark + 1, lines.Length - mark - 1); settingLines.RemoveRange(mark, lines.Length - mark); }
                    if (!settingLines.Exists(l => l.StartsWith("playlist=") || l.StartsWith("mac=")))
                    {
                        MessageBox.Show(this, "That file isn't an AuraBox Control backup.", "Import Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (MessageBox.Show(this, "Replace the current settings" + (mark < 0 ? "" : " and favorites") + " with this backup?\r\n\r\n" +
                                        "AuraBox Control will restart to load them.", "Import Settings",
                                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    Settings restored = Settings.FromLines(settingLines);
                    StopJob();
                    lock (playlistGate) restored.Save();
                    if (mark >= 0) File.WriteAllLines(Settings.DataFile("favorites.txt"), favLines.ToArray());
                    restoring = true;
                    System.Diagnostics.Process.Start(Application.ExecutablePath, "--restart");
                    ExitApp();
                }
                catch (Exception e) { status.Text = "Import failed: " + e.Message; }
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (var mutex = new Mutex(true, "AuraBoxControl.SingleInstance", out first))
            {
                if (!first && Array.IndexOf(args, "--restart") >= 0)   // restarted after an import: wait for the old copy to close
                {
                    try { first = mutex.WaitOne(15000); }
                    catch (AbandonedMutexException) { first = true; }
                }
                if (!first) { PostMessage((IntPtr)0xFFFF, ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }   // show the running copy
                var settings = Settings.Load();
                Theme.Init(settings.ThemeMode);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(settings, Array.IndexOf(args, "--tray") >= 0));
            }
        }
    }
}
