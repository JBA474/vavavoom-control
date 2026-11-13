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
        // One player per display: its device, its own settings and playlist, and whatever is playing on it. The window
        // controls the selected one (sel); each background job keeps its own (threadPlayer), so it goes on sending to
        // its display when the window switches to another. P is the player for the code running now.
        class Player
        {
            public Device Box; public DeviceSettings Cfg; public string Name;
            public Thread Job; public volatile bool JobStop, SkipItem, PlaylistOn; public Button PlayingBtn;
            public volatile string CurrentItem, Showing, JobStatus; public DateTime CurrentEnd;
            public long HoldUntilTicks; public int FlashCount; public bool JobSendFailed;   // see FlashNotification
            public volatile bool Connecting; public bool UserDisconnected; public int FailedTries; public DateTime NextTry = DateTime.MaxValue;
            public readonly object ConnectGate = new object();
            public int Brightness = MaxBrightness;
        }
        readonly List<Player> players = new List<Player>();
        Player sel;
        [ThreadStatic] static Player threadPlayer;
        Player P { get { return threadPlayer ?? sel; } }

        // run a with P set to pl, on this thread
        static void As(Player pl, Action a)
        {
            var was = threadPlayer; threadPlayer = pl;
            try { a(); } finally { threadPlayer = was; }
        }

        // run a on the window's thread with P set to pl
        void UiAs(Player pl, Action a)
        {
            if (closing) return;
            try { BeginInvoke((Action)delegate { As(pl, a); }); }
            catch (InvalidOperationException) { }   // the window is already gone
        }

        Device box { get { return P.Box; } }
        DeviceSettings dcfg { get { return P.Cfg; } }
        Thread job { get { return P.Job; } set { P.Job = value; } }
        bool jobStop { get { return P.JobStop; } set { P.JobStop = value; } }
        bool skipItem { get { return P.SkipItem; } set { P.SkipItem = value; } }
        Button playingBtn { get { return P.PlayingBtn; } set { P.PlayingBtn = value; } }
        string currentItem { get { return P.CurrentItem; } set { P.CurrentItem = value; } }
        DateTime currentEnd { get { return P.CurrentEnd; } set { P.CurrentEnd = value; } }
        string showing { get { return P.Showing; } set { P.Showing = value; } }   // name of a manual job (live animation, clock...) for the Now page
        string jobStatus { get { return P.JobStatus; } set { P.JobStatus = value; } }   // last "what's playing" message from a background job
        bool jobSendFailed { get { return P.JobSendFailed; } set { P.JobSendFailed = value; } }
        bool connecting { get { return P.Connecting; } set { P.Connecting = value; } }
        bool userDisconnected { get { return P.UserDisconnected; } set { P.UserDisconnected = value; } }
        int failedTries { get { return P.FailedTries; } set { P.FailedTries = value; } }
        DateTime nextTry { get { return P.NextTry; } set { P.NextTry = value; } }
        bool playlistOn { get { return P.PlaylistOn; } }
        readonly Settings cfg;
        readonly List<int[]> frames = new List<int[]>();
        readonly object playlistGate = new object();
        ComboBox deviceBox; Button connectBtn; CheckBox syncBox; Label status, connDot, pageTitle;
        TrackBar bright; System.Windows.Forms.Timer brightTimer;
        PixelGrid grid; CheckBox liveBox; NumericUpDown delayBox;
        readonly PixelGrid[] frameSlots = new PixelGrid[AuraBox.MaxFrames]; readonly Label[] frameRims = new Label[AuraBox.MaxFrames]; int selFrame = -1;   // animation frame strip
        ColorWheel penWheel; Label penSwatch;
        NotifyIcon tray;
        Panel host, side;
        Button stopBtn;
        readonly List<NavButton> nav = new List<NavButton>();
        readonly List<Panel> pages = new List<Panel>();

        // background job (playlist, text or snakes): only one runs at a time
        CheckBox rotateBox; TextBox placeBox; Label placeLabel, weatherLabel;
        PixelGrid weatherIcon, weatherTemp;
        List<int[]> weatherFrames = new List<int[]>(); int weatherFrame;
        WeatherInfo weather; readonly object weatherGate = new object();
        TextBox textBox; ColorWheel textColor; ComboBox textFont; TrackBar textSpeed; Button textPlay;
        Button livePlay;
        string highlightedItem;   // playlist item highlighted on the Playlist page
        bool startHidden, started;

        // live animations generated on this PC, frame by frame
        // Make draws 10 x 10 (palette indexes); Make16, where there is one, draws 16 x 16 for the Tivoo (RGB colors)
        // Make128, where there is one, makes whole loops for the Tiivoo 2's 128 x 128 screen (Loop128)
        class LiveAnim { public string Id, Name, About; public Func<Func<int[]>> Make, Make16; public Func<Loop128> Make128; }

        // a 16 x 16 animation's palette frames (Palette16) as the RGB pictures the Tivoo takes
        static Func<int[]> Rgb16(Func<int[]> next) { return () => Palette16.ToRgb(next()); }
        // a full-screen Tiivoo 2 loop shown on a small display (and in the page's preview): its frames, the middle
        // squeezed to 16 x 16 (other displays get their own versions later)
        static Func<int[]> Squeezed(Loop128 loop)
        {
            List<int[]> frames = null; int at = 0;
            return () =>
            {
                if (frames == null)
                {
                    int ms; double play; frames = new List<int[]>();
                    foreach (var f in loop.Next(out ms, out play))
                    {
                        var mid = new int[128 * 128];
                        for (int y = 0; y < 128; y++) Array.Copy(f, y * Tiivoo2.Wide + (Tiivoo2.Wide - 128) / 2, mid, y * 128, 128);
                        frames.Add(Proto.Shrink128(mid));
                    }
                }
                return frames[at++ % frames.Count];
            };
        }

        // the animation drawn for a display of this size: its own 16 x 16 version on a Tivoo, else the 10 x 10 one (stretched)
        static Func<int[]> MakeFor(LiveAnim la, int size) { return size == 16 && la.Make16 != null ? la.Make16() : la.Make(); }
        static readonly LiveAnim[] LiveAnims = {
            new LiveAnim { Id = "snakes", Name = "Snake Chase", Make = () => new SnakeChase().Next, Make16 = () => Rgb16(new SnakeChase(16).Next),
                           About = "A long green snake and a short magenta one play tag. The red head is \"it\"." },
            new LiveAnim { Id = "blocks", Name = "Tetrix", Make = () => new FallingBlocks().Next, Make16 = () => Rgb16(new FallingBlocks(16).Next),
                           About = "Pieces drop into place; finished rows flash and clear, forever." },
            new LiveAnim { Id = "stars", Name = "Hyperspace", Make = () => new Starfield().Next, Make16 = () => Rgb16(new Starfield(16).Next),
                           About = "Stars stream past you on all sides, stretching into streaks." },
            new LiveAnim { Id = "rain", Name = "Rain on a Window", Make = () => new RainWindow().Next, Make16 = () => Rgb16(new RainWindow(16).Next),
                           About = "Drops slide down the glass at their own pace, sometimes clinging for a moment." },
            new LiveAnim { Id = "fire", Name = "Fireplace", Make = () => new Fireplace().Next, Make16 = () => Rgb16(new Fireplace(16).Next),
                           About = "Flickering flames rise and fade from white-hot to yellow to red." },
            new LiveAnim { Id = "fireworks", Name = "Fireworks", Make = () => new Fireworks().Next, Make16 = () => Rgb16(new Fireworks(16).Next),
                           About = "A 90-second show: a rocket now and then, building to a finale of bursts and one big boom." },
            new LiveAnim { Id = "heart", Name = "Heart Monitor", Make = () => new HeartMonitor().Next, Make16 = () => Rgb16(new HeartMonitor(16).Next), Make128 = () => new HeartMonitor128(),
                           About = "A heartbeat traces across the screen like a bedside monitor, with the odd skipped or extra beat and a rare flatline." },
            new LiveAnim { Id = "balls", Name = "Bouncing Balls", Make = () => new BouncingBalls().Next, Make16 = () => Rgb16(new BouncingBalls(16).Next),
                           About = "Three balls bounce with gravity and get a fresh kick when they settle." },
            new LiveAnim { Id = "bricks", Name = "Brick Breaker", Make = () => new BrickBreaker().Next, Make16 = () => Rgb16(new BrickBreaker(16).Next),
                           About = "A paddle keeps the ball in play and knocks out a wall of bricks. Clear it and a new wall goes up." },
            new LiveAnim { Id = "laos", Name = "Laos Flag", Make = () => new WavingFlag(WavingFlag.Laos).Next, Make16 = () => Rgb16(new WavingFlag(WavingFlag.Laos, 16).Next),
                           About = "The flag of Laos ripples in a breeze." },
            new LiveAnim { Id = "lava", Name = "Lava Lamp", Make = () => new LavaLamp().Next, Make16 = () => Rgb16(new LavaLamp(16).Next),
                           About = "Slow blobs of lava drift, merge and split with a magenta glow." },
            new LiveAnim { Id = "mandelbrot", Name = "Mandelbrot Zoom", Make = () => new Mandelbrot().Next, Make16 = () => new Mandelbrot(16).Next, Make128 = () => new Mandelbrot128(),
                           About = "Dives into the edge of the Mandelbrot set toward its spirals and seahorses as the colors cycle, then pulls back out and heads somewhere new." },
            new LiveAnim { Id = "miner", Name = "Gem Miner", Make = () => new GemMiner().Next, Make16 = () => Rgb16(new GemMiner(16).Next),
                           About = "A miner digs through the earth after buried gems. His lamp zaps glow worms down the tunnels, and boulders he digs under drop on them." },
            new LiveAnim { Id = "swarm", Name = "Star Swarm", Make = () => new StarSwarm().Next, Make16 = () => Rgb16(new StarSwarm(16).Next),
                           About = "Alien saucers, fighters and bombers swoop into formation, then dive at the rocket below with engines blazing to drop bombs. It dodges and shoots back. Each wave comes faster, in new colors." },
            new LiveAnim { Id = "aquarium", Name = "Aquarium", Make = () => new Aquarium().Next, Make16 = () => new Aquarium(16).Next, Make128 = () => new Aquarium128(),
                           About = "A planted tank. On the Tiivoo 2: a blue tang drifts from side to side while a clownfish and its little follower loop around by the plants, a starfish creeps over the glass, a pirate skeleton creaks at a ship's wheel, and bubbles rise to the sound of bubbling water. On the smaller screens: a goldfish, an angelfish and a school of neon tetras wander among swaying plants." },
            new LiveAnim { Id = "aquaclock", Name = "Aquarium Clock", Make = () => new Aquarium().Next, Make16 = () => new Aquarium(16).Next, Make128 = () => new Aquarium128(),
                           About = "The Tiivoo 2's aquarium as one of its own clock faces: the Tiivoo 2 keeps the time itself, shown along the bottom over the gravel, with the bubbling water sound. Made on this PC and sent straight to the Tiivoo 2 (nothing is uploaded). Other displays show the plain aquarium." },
            new LiveAnim { Id = "slots", Name = "Slot Machine", Make = () => Squeezed(new SlotMachine160()), Make16 = () => Squeezed(new SlotMachine160()), Make128 = () => new SlotMachine160(),
                           About = "A slot machine (made for the Tiivoo 2's full screen) with sound through its speaker: the lever clunks, the reels tick round and thunk to a stop, small wins ding, and now and then 7-7-7 hits the jackpot with flashing lights, a fanfare and a shower of coins." },
            new LiveAnim { Id = "robot", Name = "Retro Robot", Make = () => Squeezed(new Robot160()), Make16 = () => Squeezed(new Robot160()), Make128 = () => new Robot160(),
                           About = "A chrome retro robot (made for the Tiivoo 2's full screen): a red light scans across its visor, its mouth lights dance as it beeps and boops through the Tiivoo 2's speaker, gauges swing, lamps blink, and now and then it waves." },
            new LiveAnim { Id = "jellyfish", Name = "Jellyfish", Make = () => new Jellyfish().Next, Make16 = () => new Jellyfish(16).Next,
                           About = "Glowing jellyfish in a dark tank: each bell squeezes to push itself along and opens to glide, tilting toward where it is going, its tentacles trailing behind each push and rippling." },
            new LiveAnim { Id = "nextholiday", Name = "Days to Next Holiday", Make = () => new HolidayCountdown().Next, Make16 = () => new HolidayCountdown(16).Next,
                           About = "Counts the days to the next US holiday, from New Year's Day to Christmas, with its name scrolling by in its colors. On the day itself, confetti and a greeting." },
            new LiveAnim { Id = "lavaclock", Name = "Lava Clock", Make = () => new LavaClock(H24).Next, Make16 = () => Rgb16(new LavaClock(H24, 16).Next),
                           About = "Lava drifts, and at each new minute the ooze slowly gathers into the time, holds, then melts away." },
            new LiveAnim { Id = "countdown", Name = "Countdown Timer", Make = () => new Countdown().Next, Make16 = () => Rgb16(new Countdown(16).Next),
                           About = "Counts down a number of minutes, or to a date and time, with rolling digits and a draining bar. The last 9 seconds show big, then it bursts at zero." },
        };

        // single instance: a second launch asks the running copy to show itself
        static readonly int ShowMessage = RegisterWindowMessage("VaVaVoomControl.ShowWindow");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, string l);
        const int EM_SETCUEBANNER = 0x1501;   // gray hint text in an empty text box
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "VaVaVoomControl", OldRunName = "AuraBoxControl";
        readonly List<PlayRow> playRows = new List<PlayRow>();

        class PlayRow { public string Id; public CheckBox On; public NumericUpDown Amount; public ComboBox Unit; public Button Up, Down; public TrackBar Speed; }

        // a live animation's speed changed (on the Playlist or Animations page): the other page's slider follows
        Action<string> speedChanged = delegate { };

        const int W = 700, H = 520;   // page area (logical pixels at 100% scaling)

        public MainForm(Settings settings, bool hidden)
        {
            cfg = settings;
            foreach (var d in cfg.Devices) players.Add(NewPlayer(d, null));
            sel = players.Find(pl => pl.Cfg.Mac.Length > 0 && pl.Cfg.Mac == cfg.SelectedDevice) ?? players[0];   // the display picked last time
            startHidden = hidden || settings.StartInTray;
            clock24 = cfg.Clock24; LavaClock.FixedTheme = cfg.LavaTheme; LavaClock.Puddle = cfg.LavaPuddle; Countdown.Minutes = cfg.CountdownMinutes;
            Countdown.Until = cfg.CountdownToDate ? cfg.CountdownUntil : DateTime.MinValue;
            HeartMonitor.Speed = () => cfg.SpeedOf("heart");   // its slider sets the heart rate
            Mandelbrot128.Speed = () => cfg.SpeedOf("mandelbrot");   // the Tiivoo 2 zoom's time per frame
            DivoomAccount.Email = cfg.DivoomEmail; DivoomAccount.UserId = cfg.DivoomUserId; DivoomAccount.Token = cfg.DivoomToken;
            ApplyTivooClock();
            cfg.ThermoFahrenheit = cfg.Fahrenheit;   // one unit setting now covers the weather and the box's own thermometer
            cfg.Brightness = MaxBrightness;          // every launch starts at full brightness
            for (int k = 0; k < Settings.ShowNowSlots; k++)   // a pick that no longer exists goes back to its default
                if (!ShowNowChoices().Exists(c => c.Key == cfg.ShowNow[k])) cfg.ShowNow[k] = Settings.DefaultShowNow[k];
            Text = "VaVaVoom Control";
            Font = new Font(Theme.UiFont, 9f);
            FormBorderStyle = FormBorderStyle.Sizable;   // bigger shows more of the Gallery; the other pages keep their size
            // only the close button: the window can't be resized, and with "minimize to the system tray" on, X hides it
            // to the tray (Windows shows minimize and maximize together or not at all)
            MaximizeBox = true; MinimizeBox = true;
            ClientSize = new Size(212 + W + 16, 52 + H + 30);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon(Size.Empty);

            BuildChrome();
            // sidebar groups; the page order here is the sidebar order
            AddGroup("Display");
            AddPage("\uE768", "Now", BuildNowPage());
            AddPage("\uE7F4", "All Displays", BuildWallPage());
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

            tray = new NotifyIcon { Icon = AppIcon(SystemInformation.SmallIconSize), Text = "VaVaVoom Control", Visible = true };
            // the tray menu acts on every display: with one, its items are listed directly; with more, each display gets
            // its own submenu (named as at the top of the window), plus Pause All / Resume All and Sync All Clocks
            var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new MenuRenderer() };
            menu.Opening += delegate
            {
                menu.Items.Clear();
                menu.Items.Add("Show", null, delegate { RestoreFromTray(); });
                menu.Items.Add(new ToolStripSeparator());
                if (players.Count == 1) AddTrayItems(menu.Items, players[0], menu.Renderer);
                else
                {
                    foreach (var pl0 in players)
                    {
                        var pl = pl0;
                        var sub = new ToolStripMenuItem(pl.Name + (pl.Box.Connected ? "" : "  (not connected)") + (pl.PlaylistOn ? "  ▶" : ""));
                        AddTrayItems(sub.DropDownItems, pl, menu.Renderer);
                        sub.DropDown.Renderer = menu.Renderer;
                        menu.Items.Add(sub);
                    }
                    bool any = players.Exists(pl => pl.PlaylistOn);
                    menu.Items.Add(any ? "Pause All Playlists" : "Resume All Playlists", null, delegate
                    {
                        foreach (var pl in players) As(pl, delegate { if (playlistOn == any) SetPlaylist(!any); });
                    });
                    menu.Items.Add("Sync All Clocks", null, delegate { foreach (var pl in players) As(pl, delegate { Run(delegate { box.SetTime(); }, "Clock synced"); }); });
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, delegate { ExitApp(); });
            };
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreFromTray(); };
            tray.BalloonTipClicked += delegate { RestoreFromTray(); };   // "Click here to open" on the startup notification
            Resize += delegate { if (WindowState == FormWindowState.Minimized && cfg.MinimizeToTray) Hide(); };

            Theme.Prepare(this);
            MinimumSize = Size;   // never smaller than the standard layout
            ApplySavedPosition();
            prepared = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemTheme;

            Shown += delegate { everShown = true; Startup(); };
            HandleCreated += delegate { if (keyHookHandle == IntPtr.Zero) HookWinArrows(); };
            VisibleChanged += delegate { if (Visible) everShown = true; };   // also when first opened from the tray
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                // with "minimize to the system tray" on, the X hides the window like minimize does; the app keeps running
                // until Exit in the tray menu (or Windows shutting down, or an import restarting it)
                if (cfg.MinimizeToTray && !exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    RememberPosition(); SaveSettings();
                    Hide();
                    if (!toldAboutTray && cfg.ShowNotifications)
                    {
                        toldAboutTray = true;
                        tray.ShowBalloonTip(5000, "VaVaVoom Control is still running", "It keeps the playlist going from the system tray. " +
                                            "To quit, right-click the VaVaVoom Control icon and choose Exit.", ToolTipIcon.Info);
                    }
                    return;
                }
                closing = true;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemTheme;
                RememberPosition();
                foreach (var pl in players) As(pl, delegate { StopJob(); pl.Box.Close(); });
                SaveSettings(); tray.Visible = false;
                if (keyHookHandle != IntPtr.Zero) { UnhookWindowsHookEx(keyHookHandle); keyHookHandle = IntPtr.Zero; }
            };
        }

        // one display's tray items: skip, pause or resume its playlist, its Show Now picks, sync its clock
        void AddTrayItems(ToolStripItemCollection items, Player pl, ToolStripRenderer renderer)
        {
            var skip = new ToolStripMenuItem("Skip to Next Item", null, delegate { As(pl, delegate { if (playlistOn) { skipItem = true; SetStatus("Skipping to the next item..."); } }); });
            skip.Enabled = pl.PlaylistOn;
            items.Add(skip);
            items.Add(pl.PlaylistOn ? "Pause Playlist" : "Resume Playlist", null, delegate { As(pl, delegate { SetPlaylist(!playlistOn); }); });
            var showNow = new ToolStripMenuItem("Show Now");   // the Now page's eight picks
            foreach (string pick in cfg.ShowNow)
            {
                string id = pick;
                if (id == EmptyPick) continue;   // empty buttons aren't listed in the tray menu
                showNow.DropDownItems.Add(ShowNowName(id), null, delegate { As(pl, delegate { ShowNowRun(id); }); });
            }
            showNow.DropDown.Renderer = renderer;
            items.Add(showNow);
            items.Add("Sync Clock", null, delegate { As(pl, delegate { Run(delegate { box.SetTime(); }, "Clock synced"); }); });
        }

        void Startup()
        {
            if (started) return;
            started = true;
            RefreshDevices();
            foreach (var pl in players) if (pl.Cfg.Mac.Length > 0) As(pl, Connect);
            if (sel.Cfg.Mac.Length == 0 && deviceBox.Text.Length > 0) Connect();
            StartReconnectTimer();
            foreach (var pl in players) if (pl.Cfg.Rotate) As(pl, delegate { SetPlaylist(true); });
            ThreadPool.QueueUserWorkItem(delegate { GetWeather(false); });
            if (UpdateRepo.Length > 0 && cfg.CheckUpdates) CheckForUpdates(false);   // quietly; only says something when there's a newer version
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
                if (cfg.ShowNotifications) tray.ShowBalloonTip(6000, "VaVaVoom Control is running", "Click here to open VaVaVoom Control. Later, double-click its icon in the system tray.",
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
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k == null ? "" : (k.GetValue(RunName) as string ?? k.GetValue(OldRunName) as string ?? ""); }
            catch (Exception) { return ""; }
        }

        static void SetStartWithWindows(bool on, bool tray = true)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, RunCommand(tray));
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                if (k.GetValue(OldRunName) != null) k.DeleteValue(OldRunName);   // from before the rename
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

        void RestoreFromTray()
        {
            if (!everShown) ApplySavedPosition();   // started hidden in the tray: Windows may have placed it since
            Show(); WindowState = FormWindowState.Normal; Activate();
        }

        // back where it was last time, if that spot is still on a screen (a monitor may have been unplugged)
        void ApplySavedPosition()
        {
            if (cfg.WindowW >= MinimumSize.Width && cfg.WindowH >= MinimumSize.Height) Size = new Size(cfg.WindowW, cfg.WindowH);
            if (cfg.WindowMax) WindowState = FormWindowState.Maximized;
            if (!cfg.HasWindowPos) return;
            var saved = new Rectangle(cfg.WindowX, cfg.WindowY, Width, Height);
            foreach (var screen in Screen.AllScreens)
            {
                var seen = Rectangle.Intersect(screen.WorkingArea, saved);
                if (seen.Width >= 200 && seen.Height >= 100) { StartPosition = FormStartPosition.Manual; Location = saved.Location; return; }
            }
        }

        // ---------- Win+Left / Win+Right: move to the next screen ----------
        // Windows only snaps resizable windows, and this one has a fixed size, so those keys did nothing here. While this
        // window is the active one, they move it to the next screen that way (wrapping around), keeping its spot on the
        // screen. The keys are passed on, so Windows still sees them (and the Start menu doesn't pop up on release).
        delegate IntPtr KeyboardHook(int code, IntPtr msg, IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, KeyboardHook proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr data);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        KeyboardHook keyHook;   // kept in a field so it isn't garbage-collected while Windows calls it
        IntPtr keyHookHandle;

        void HookWinArrows()
        {
            keyHook = delegate (int code, IntPtr msg, IntPtr data)
            {
                const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, VK_LEFT = 0x25, VK_RIGHT = 0x27;
                if (code >= 0 && ((int)msg == WM_KEYDOWN || (int)msg == WM_SYSKEYDOWN) && IsHandleCreated && GetForegroundWindow() == Handle)
                {
                    int key = Marshal.ReadInt32(data);
                    bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
                    bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;   // Win+Shift+arrows already move the window
                    if (win && !shift && (key == VK_LEFT || key == VK_RIGHT)) BeginInvoke((Action)delegate { MoveToScreen(key == VK_LEFT ? -1 : 1); });
                }
                return CallNextHookEx(keyHookHandle, code, msg, data);
            };
            keyHookHandle = SetWindowsHookEx(13, keyHook, GetModuleHandle(null), 0);   // 13 = low-level keyboard
        }

        void MoveToScreen(int step)
        {
            var screens = new List<Screen>(Screen.AllScreens);
            if (screens.Count < 2 || WindowState != FormWindowState.Normal) return;
            screens.Sort((a, b) => a.Bounds.X != b.Bounds.X ? a.Bounds.X - b.Bounds.X : a.Bounds.Y - b.Bounds.Y);   // left to right
            var from = Screen.FromHandle(Handle);
            int at = screens.FindIndex(s => s.DeviceName == from.DeviceName);
            var to = screens[((at < 0 ? 0 : at) + step + screens.Count) % screens.Count].WorkingArea;
            var was = from.WorkingArea;
            // the same relative spot: left edge, middle or right edge stays that way
            double fx = was.Width > Width ? (double)(Left - was.Left) / (was.Width - Width) : 0;
            double fy = was.Height > Height ? (double)(Top - was.Top) / (was.Height - Height) : 0;
            fx = Math.Max(0, Math.Min(1, fx)); fy = Math.Max(0, Math.Min(1, fy));
            Location = new Point(to.Left + (int)Math.Round(fx * Math.Max(0, to.Width - Width)), to.Top + (int)Math.Round(fy * Math.Max(0, to.Height - Height)));
        }

        bool exiting, toldAboutTray;   // exiting: really quit, even with "minimize to the system tray" on
        bool everShown;                // the window has been on screen at least once (see RememberPosition)

        void ExitApp() { exiting = true; Close(); }

        // the window's spot on screen, for next time (minimized, Location is off-screen: use where it would be restored)
        void RememberPosition()
        {
            if (!everShown) return;   // started in the tray and never opened: keep the saved spot
            var at = WindowState == FormWindowState.Normal ? Location : RestoreBounds.Location;
            if (at.X <= -30000 || at.Y <= -30000) return;
            cfg.WindowX = at.X; cfg.WindowY = at.Y; cfg.HasWindowPos = true;
            var size = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            cfg.WindowW = size.Width; cfg.WindowH = size.Height; cfg.WindowMax = WindowState == FormWindowState.Maximized;
        }

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
            if (P != sel) s = P.Name + ": " + s;
            if (!InvokeRequired) { status.Text = s; return; }
            if (closing) return;
            try { BeginInvoke((Action)delegate { status.Text = s; }); }
            catch (InvalidOperationException) { }   // the window is already gone
        }

        // ---------- window chrome: sidebar, header, page host, status bar ----------
        void BuildChrome()
        {
            side = new Panel { Location = new Point(0, 0), Size = new Size(204, 52 + H + 30), Tag = "sidebar", Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left };
            side.Controls.Add(new Label { Text = "VaVaVoom", Location = new Point(18, 14), AutoSize = true, Font = Theme.Semibold(14f) });
            Controls.Add(side);

            pageTitle = new Label { Location = new Point(212, 12), AutoSize = true, Font = Theme.Semibold(16f), UseMnemonic = false };
            Controls.Add(pageTitle);
            connDot = new Label { Text = "●", Location = new Point(212 + W - 440, 17), AutoSize = true, Tag = "dot", ForeColor = Color.Gray };
            Controls.Add(connDot);
            connDot.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            deviceBox = new ComboBox { Location = new Point(212 + W - 422, 14), Width = 250 };
            deviceBox.SelectionChangeCommitted += delegate
            {
                string mac;
                if (deviceBox.SelectedItem != null && SelectedAddress((string)deviceBox.SelectedItem, out mac) != 0) SelectDevice(mac, (string)deviceBox.SelectedItem);
            };
            Controls.Add(deviceBox);
            deviceBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var refresh = new Button { Text = "", Font = new Font(Theme.IconFont, 9f), Location = new Point(212 + W - 168, 13), Size = new Size(32, 26) };
            refresh.Click += delegate { RefreshDevices(); };
            Controls.Add(refresh);
            refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            connectBtn = new Button { Text = "Connect", Location = new Point(212 + W - 130, 13), Size = new Size(130, 26), Tag = "primary" };
            connectBtn.Click += delegate
            {
                // Disconnect stays disconnected (no automatic retries) until Connect is pressed again
                if (box.Connected) { userDisconnected = true; box.Close(); SetConnected(false, null); }
                else
                {
                    string mac;
                    ulong typed = SelectedAddress(out mac);   // a device picked or typed in the list that isn't the current one
                    if (typed != 0 && mac != dcfg.Mac && dcfg.Mac.Length > 0) { SelectDevice(mac, deviceBox.Text); return; }
                    userDisconnected = false; failedTries = 0; Connect();
                }
            };
            Controls.Add(connectBtn);
            connectBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            host = new Panel { Location = new Point(212, 52), Size = new Size(W, H), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            Controls.Add(host);
            // status bar: the last message, brightness (from every page), the playlist state, and Resume / Stop
            status = new Label { Location = new Point(212, 52 + H + 4), Size = new Size(186, 22), Text = "Not connected", Tag = "subtle",
                                 TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
            Controls.Add(status);
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var sun = new Label { Text = "", Font = new Font(Theme.IconFont, 10f), Location = new Point(212 + 192, 52 + H + 6), AutoSize = true, Tag = "subtle" };
            Controls.Add(sun);
            sun.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            bright = new TrackBar { Minimum = 0, Maximum = 255, TickStyle = TickStyle.None, AutoSize = false, Value = Math.Max(0, Math.Min(255, cfg.Brightness)),
                                    Location = new Point(212 + 212, 52 + H + 2), Size = new Size(110, 26) };
            brightTimer = new System.Windows.Forms.Timer { Interval = 250 };
            brightTimer.Tick += delegate { brightTimer.Stop(); SendBrightness(); };
            bright.ValueChanged += delegate { brightTimer.Stop(); brightTimer.Start(); };
            Controls.Add(bright);
            bright.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var tips = new ToolTip();
            tips.SetToolTip(bright, "Brightness"); tips.SetToolTip(sun, "Brightness");
            playState = new Label { Location = new Point(212 + 328, 52 + H + 4), Size = new Size(W - 328 - 174, 22), Tag = "accent",
                                    TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, UseMnemonic = false, Font = Theme.Semibold(9f) };
            Controls.Add(playState);
            playState.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            resumeBtn = Btn("▶  Resume", 212 + W - 168, 52 + H + 1, 82, delegate { SetPlaylist(true); }, true);
            resumeBtn.Height = 26; resumeBtn.Visible = false;
            Controls.Add(resumeBtn);
            resumeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            var stateTimer = new System.Windows.Forms.Timer { Interval = 500 };
            stateTimer.Tick += delegate { UpdatePlayState(); stopBtn.Visible = sel.Job != null; };   // Stop is for the selected display
            stateTimer.Start();
            // stops whatever is playing from this PC (playlist, text, live animations, imported GIFs)
            stopBtn = Btn("\u25A0  Stop", 212 + W - 82, 52 + H + 1, 82, delegate
            {
                if (playlistOn) SetPlaylist(false); else StopJob();
                status.Text = "Stopped";
            });
            stopBtn.Visible = false;
            stopBtn.Height = 26;
            Controls.Add(stopBtn);
            stopBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        }

        Label playState; Button resumeBtn;

        // "Now: Lava Clock · 3:12 left", or that the playlist is paused / off
        string PlayStateText()
        {
            string id = currentItem;
            PlayItem cur = null;
            lock (playlistGate) if (id != null) cur = PlayItem.Find(dcfg.Playlist, id);
            if (cur != null)
            {
                var left = currentEnd - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                return cur.Name + "  ·  " + (int)left.TotalMinutes + ":" + left.Seconds.ToString("00") + " left";
            }
            if (playlistOn) return "Playlist starting...";
            if (dcfg.Rotate) return "Playlist paused";
            return "Playlist off";
        }

        void UpdatePlayState()
        {
            if (rotateBox == null) return;
            playState.Text = PlayStateText();
            resumeBtn.Visible = !playlistOn && dcfg.Rotate;   // paused for something manual: offer to pick it back up
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
            page.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
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

        static Panel NewPage() { return new Panel { Size = new Size(W, H) }; }   // full size from the start, so anchored controls keep their places

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
            var paired = PairedDevices.List();
            // A to Z by the name shown, the Tiivoo 2 sorted as "Tivoo 2" so it comes after the Tivoo
            Func<string, string> sortName = n => DeviceName(n).Replace("Tiivoo", "Tivoo");
            paired.Sort((a, b) => string.Compare(sortName(a.Key), sortName(b.Key), StringComparison.CurrentCultureIgnoreCase));
            foreach (var d in paired)
            {
                // only Divoom displays (and any display already in use, whatever it is called)
                string mac = PairedDevices.Mac(d.Value);
                if (!Device.IsDisplay(d.Key) && !cfg.Devices.Exists(x => x.Mac == mac)) continue;
                string item = DeviceName(d.Key) + "  (" + mac + ")";
                deviceBox.Items.Add(item);
                if (pick == null && dcfg.Mac.Length == 0 && d.Key.IndexOf("AuraBox", StringComparison.OrdinalIgnoreCase) >= 0) pick = item;
                if (dcfg.Mac.Length > 0 && item.Contains(dcfg.Mac)) pick = item;
            }
            if (pick != null) deviceBox.Text = pick;
            else if (current.Length > 0) deviceBox.Text = current;
            else if (dcfg.Mac.Length > 0) deviceBox.Text = dcfg.Mac;
            if (deviceBox.Text.Length == 0)
                status.Text = "No AuraBox paired. Pair \"AuraBox-audio\" in Windows Bluetooth settings (PIN 0000 if asked), then press refresh.";
        }

        // Windows pairs the box as "AuraBox-audio"; the app only uses its display link, so show plain "AuraBox"
        // the name a display goes by in the app, tidied from its Bluetooth name: "Divoom Tiivoo 2" is "Tiivoo 2", and the
        // Ditoo Pro (it pairs as "DitooPro-Audio-<owner>" or "DitooPro-<owner>") is "Ditoo Pro"
        static string DeviceName(string name)
        {
            foreach (string tail in new[] { "-audio", " audio" })   // also mid-name
            {
                int at = name.IndexOf(tail, StringComparison.OrdinalIgnoreCase);
                if (at > 0) { name = name.Remove(at, tail.Length); break; }
            }
            if (name.StartsWith("DitooPro", StringComparison.OrdinalIgnoreCase)) return "Ditoo Pro";
            if (name.StartsWith("Divoom ", StringComparison.OrdinalIgnoreCase) && name.Length > 7) name = name.Substring(7);
            return name;
        }

        ulong SelectedAddress(out string mac) { return SelectedAddress(deviceBox.Text, out mac); }

        static ulong SelectedAddress(string t, out string mac)
        {
            int open = t.LastIndexOf('('), close = t.LastIndexOf(')');
            mac = open >= 0 && close > open ? t.Substring(open + 1, close - open - 1) : t.Trim();
            ulong a;
            return PairedDevices.TryParseMac(mac, out a) ? a : 0;
        }

        void Connect()
        {
            if (connecting) return;
            string mac = dcfg.Mac;
            ulong addr;
            if (!PairedDevices.TryParseMac(mac, out addr)) addr = P == sel ? SelectedAddress(out mac) : 0;
            if (addr == 0) { status.Text = "Pick a paired display or type its MAC address (AA:BB:CC:DD:EE:FF)."; return; }
            connecting = true;
            if (P == sel)
            {
                connectBtn.Enabled = false;
                connectBtn.Text = "Connecting...";
            }
            SetStatus((failedTries > 0 ? "Reconnecting to " : "Connecting to ") + P.Name + "...");
            bool sync = syncBox.Checked;
            var pl = P;
            ThreadPool.QueueUserWorkItem(delegate
            {
                As(pl, delegate
                {
                    try
                    {
                        ConnectNow(addr, mac, sync);
                        UiAs(pl, delegate { connecting = false; SetConnected(true, null); });
                    }
                    catch (Exception e) { UiAs(pl, delegate { connecting = false; SetConnected(false, e.Message); }); }
                });
            });
        }

        // Keeps trying to reach the box after a failed or lost connection (also right after launch), unless the
        // Disconnect button was used. Waits a little longer after each failure, up to a minute between tries.

        void StartReconnectTimer()
        {
            var t = new System.Windows.Forms.Timer { Interval = 1000 };
            t.Tick += delegate
            {
                foreach (var pl in players)
                    As(pl, delegate
                    {
                        if (box.Connected || connecting || userDisconnected || DateTime.Now < nextTry) return;
                        if (job != null) return;   // a running job reconnects on its own
                        if (dcfg.Mac.Length > 0) Connect();
                        else if (pl == sel)
                        {
                            if (deviceBox.Text.Length == 0) RefreshDevices();
                            if (deviceBox.Text.Length > 0) Connect();
                        }
                    });
            };
            t.Start();
        }

        void ConnectNow(ulong addr, string mac, bool sync)
        {
            lock (P.ConnectGate)   // the reconnect timer and a background job may try at the same moment
            {
                if (box.Connected) return;
                int ch = box.Connect(addr, dcfg.Channel);
                dcfg.Mac = mac; dcfg.Channel = ch;
                if (sync) box.SetTime();
                box.SetTempUnit(cfg.ThermoFahrenheit);   // the box's own thermometer follows the saved unit
                box.SetAudioInput(cfg.AudioAux);         // the box ignores AUX when no cable is plugged in
                box.SetBrightness(P.Brightness);
            }
        }

        // used by background jobs: reconnect silently to the last device
        bool EnsureConnected()
        {
            if (box.Connected) return true;
            ulong a;
            if (!PairedDevices.TryParseMac(dcfg.Mac, out a)) return false;
            try
            {
                ConnectNow(a, dcfg.Mac, true);
                UiAs(P, delegate { SetConnected(true, null); });
                return true;
            }
            catch (Exception) { return false; }
        }

        // a connection to P's display opened or closed (on the window's thread)
        void SetConnected(bool ok, string err)
        {
            if (P == sel) ShowConnection();
            if (ok)
            {
                failedTries = 0; nextTry = DateTime.MaxValue;
                SetStatus("Connected to " + P.Name);   // the address is in the device list
                SaveSettings();
                return;
            }
            if (err == null) { SetStatus("Disconnected"); return; }
            // try again soon: 10 s, 20 s, 30 s... then once a minute
            int wait = Math.Min(60, 10 * (failedTries + 1));
            failedTries++;
            nextTry = DateTime.Now.AddSeconds(wait);
            SetStatus(err + "  (Is the " + P.Name + " on and not connected to a phone?) Retrying in " + wait + " s.");
        }

        // the header shows the selected display's connection
        void ShowConnection()
        {
            bool ok = sel.Box.Connected;
            connectBtn.Enabled = !sel.Connecting;
            connectBtn.Text = sel.Connecting ? "Connecting..." : ok ? "Disconnect" : "Connect";
            connDot.ForeColor = ok ? Color.FromArgb(16, 185, 80) : Color.Gray;
        }

        Player NewPlayer(DeviceSettings d, string pairedName)
        {
            var pl = new Player { Cfg = d, Box = Device.Create(d.Kind), Name = pairedName != null ? DeviceName(pairedName) : PairedName(d.Mac, d.Kind) };
            // a Tiivoo 2 saved before it had its own kind (as a Tivoo): its name tells
            if (Device.KindOf(pl.Name) == "tiivoo2" && d.Kind != "tiivoo2") { d.Kind = "tiivoo2"; pl.Box = Device.Create(d.Kind); }
            Dim(pl);
            return pl;
        }

        // the Ditoo Pro's LEDs are brighter than the Tivoo's: scale it down so the same slider level looks the same
        void Dim(Player pl) { if (pl.Name.IndexOf("ditoo", StringComparison.OrdinalIgnoreCase) >= 0) pl.Box.BrightnessScale = cfg.DitooBrightness / 100.0; }

        // the display's name as paired in Windows ("Tivoo"), or its kind while it isn't paired
        static string PairedName(string mac, string kind)
        {
            foreach (var d in PairedDevices.List()) if (PairedDevices.Mac(d.Value) == mac) return DeviceName(d.Key);
            return kind == "tiivoo2" ? "Tiivoo 2" : kind == "tivoo" ? "Tivoo" : "AuraBox";
        }

        // switch the window to a display, adding it the first time (it gets its own settings and playlist)
        void SelectDevice(string mac, string listItem)
        {
            var pl = players.Find(x => x.Cfg.Mac == mac);
            if (pl == null)
            {
                int open = listItem.LastIndexOf("  (");
                string name = open > 0 ? listItem.Substring(0, open) : listItem;
                if (players.Count == 1 && players[0].Cfg.Mac.Length == 0) pl = players[0];   // the first display ever picked
                else
                {
                    var d = new DeviceSettings();
                    cfg.Devices.Add(d);
                    pl = new Player { Cfg = d };
                    players.Add(pl);
                }
                pl.Cfg.Mac = mac; pl.Cfg.Kind = Device.KindOf(name);
                pl.Box = Device.Create(pl.Cfg.Kind); pl.Name = DeviceName(name); Dim(pl);
                SaveSettings();
            }
            SelectPlayer(pl);
            if (!pl.Box.Connected && !pl.Connecting) { pl.UserDisconnected = false; pl.FailedTries = 0; As(pl, Connect); }
        }

        void SelectPlayer(Player pl)
        {
            sel = pl;
            if (pl.Cfg.Mac.Length > 0 && cfg.SelectedDevice != pl.Cfg.Mac) { cfg.SelectedDevice = pl.Cfg.Mac; SaveSettings(); }   // start on it next time
            RefreshDevices();
            ShowConnection();
            ShowRotate();
            RefreshPlayRows(); LayoutPlayRows();
            highlightedItem = "";
            if (bright.Value != pl.Brightness) { bright.Value = pl.Brightness; brightTimer.Stop(); }
            if (nowCard != null) nowCard.Text = "On the " + pl.Name;
            if (gallerySend != null) gallerySend.Text = "Send to " + pl.Name;
            deviceChanged();
            status.Text = "Controlling the " + pl.Name + (pl.Box.Connected ? "" : " (not connected)");
        }

        void Run(Action a, string done)
        {
            if (!box.Connected) { status.Text = "Not connected"; return; }
            var pl = P;
            ThreadPool.QueueUserWorkItem(delegate
            {
                As(pl, delegate
                {
                    try { a(); SetStatus(done); }
                    catch (Exception e)
                    {
                        box.Close();
                        UiAs(pl, delegate { SetConnected(false, "Connection lost: " + e.Message); });
                    }
                });
            });
        }

        // send from a background job; drops the connection on failure so EnsureConnected retries
        bool JobSend(Action a)
        {
            // a notification icon is on the box: hold this frame until it has had its turn
            while (DateTime.Now.Ticks < Interlocked.Read(ref P.HoldUntilTicks) && !jobStop) Thread.Sleep(50);
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
                box.Close(); UiAs(P, delegate { SetConnected(false, "Connection lost — retrying"); });
                return false;
            }
        }


        // ---------- background jobs ----------
        void StartJob(ThreadStart body, Button playButton = null)
        {
            StopJob();
            jobStop = false;
            playingBtn = playButton;
            if (playButton != null) playButton.Text = "Stop";
            var pl = P;
            job = new Thread(delegate () { threadPlayer = pl; body(); }) { IsBackground = true };
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
            if (playlistOn) PausePlaylist(); else StopJob();
            status.Text = "Playlist paused for manual control";
        }

        // A pause for something manual (a mode, color, picture, live animation) is temporary: the saved setting keeps
        // the playlist on, so it starts again the next time the app opens. Only unchecking "Run the playlist",
        // Pause in the tray menu or the Stop button turn it off for good.
        bool manualPause;

        // turn the playlist of P's display on or off
        void SetPlaylist(bool on)
        {
            var pl = P;
            if (on == pl.PlaylistOn) { ShowRotate(); return; }
            pl.PlaylistOn = on;
            if (!manualPause) pl.Cfg.Rotate = on;   // remembered across restarts (and rebuilds: it lives in the .ini)
            if (on) { StartJob(delegate { PlaylistLoop(null, true); }); SetStatus("Playlist running"); }
            else { StopJob(); SetStatus("Playlist stopped"); }
            SaveSettings();
            ShowRotate();
        }

        // the Playlist page's checkbox follows the selected display
        bool syncingRotate;
        void ShowRotate()
        {
            if (rotateBox == null || rotateBox.Checked == sel.PlaylistOn) return;
            syncingRotate = true;
            try { rotateBox.Checked = sel.PlaylistOn; } finally { syncingRotate = false; }
        }

        void PausePlaylist()
        {
            manualPause = true;
            try { SetPlaylist(false); }
            finally { manualPause = false; }
        }

        // toggles a streamed effect (text, snakes) on its Play/Stop button
        void TogglePlay(Button btn, ThreadStart body, string what)
        {
            if (job != null && playingBtn == btn) { StopJob(); status.Text = what + " stopped"; return; }
            if (playlistOn) PausePlaylist();
            StartJob(body, btn);
            showing = what;
            status.Text = what + " playing";
        }

        Card nowCard;

        // ---------- now: what's on the box, playlist controls, quick picks ----------
        Panel BuildNowPage()
        {
            var p = NewPage();
            var card = nowCard = NewCard("On the " + sel.Name, 0, 0, W, 252);
            var preview = new PixelGrid(18) { Location = new Point(14, 40), ReadOnly = true };
            card.Controls.Add(preview);
            var what = new Label { Location = new Point(222, 36), Size = new Size(W - 236, 34), Font = Theme.Semibold(15f), UseMnemonic = false, AutoEllipsis = true };
            var detail = new Label { Location = new Point(224, 72), Size = new Size(W - 236, 22), Tag = "subtle", UseMnemonic = false, AutoEllipsis = true };
            card.Controls.Add(what); card.Controls.Add(detail);
            var playBtn = Btn("Run Playlist", 222, 100, 150, delegate { SetPlaylist(!playlistOn); }, true);
            card.Controls.Add(playBtn);
            card.Controls.Add(Btn("Skip to Next Item  ›", 380, 100, 170, delegate
            {
                if (!playlistOn) { status.Text = "Start the playlist first"; return; }
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

            // the other displays at a glance: what each shows, its playlist button, and Switch to make it the one above
            var others = NewCard("Other Displays", 0, 430, W, 88);
            p.Controls.Add(others);
            var otherRows = new List<KeyValuePair<Player, Control[]>>();
            Action buildOthers = delegate
            {
                others.Controls.Clear(); otherRows.Clear();
                int x = 12;
                foreach (var pl0 in players)
                {
                    if (pl0 == sel) continue;
                    var pl = pl0;
                    var pv = new PixelGrid(4) { Location = new Point(x, 34), ReadOnly = true };
                    var nm = new Label { Location = new Point(x + 50, 32), Size = new Size(170, 20), Font = Theme.Semibold(9f), UseMnemonic = false, AutoEllipsis = true };
                    var wh = new Label { Location = new Point(x + 50, 52), Size = new Size(170, 20), Tag = "subtle", UseMnemonic = false, AutoEllipsis = true };
                    var run = Btn("Run Playlist", x + 224, 30, 104, delegate { As(pl, delegate { SetPlaylist(!playlistOn); }); });
                    var sw = Btn("Switch", x + 224, 58, 104, delegate { SelectPlayer(pl); });
                    sw.Height = 24; run.Height = 26;
                    foreach (Control c in new Control[] { pv, nm, wh, run, sw }) others.Controls.Add(c);
                    if (prepared) foreach (Control c in new Control[] { pv, nm, wh, run, sw }) c.Scale(new SizeF(Theme.Scale, Theme.Scale));
                    otherRows.Add(new KeyValuePair<Player, Control[]>(pl, new Control[] { pv, nm, wh, run }));
                    x += 344;
                    if (x > W - 300) break;   // room for two
                }
                if (otherRows.Count == 0)
                {
                    var none = Note("Only one display so far. Pick another paired display at the top right to add it.", 14, 40);
                    others.Controls.Add(none);
                    if (prepared) none.Scale(new SizeF(Theme.Scale, Theme.Scale));
                }
                Theme.Apply(others);
            };
            buildOthers();
            deviceChanged += delegate { buildOthers(); };

            var tick = new System.Windows.Forms.Timer { Interval = 200 };
            tick.Tick += delegate
            {
                if (!p.Visible) return;
                if (otherRows.Count != Math.Min(2, players.Count - 1)) buildOthers();   // a display was added
                foreach (var row in otherRows)
                {
                    var pl = row.Key; var c = row.Value;
                    ((PixelGrid)c[0]).SetAll(pl.Box.LastImage ?? new int[100]);
                    c[1].Text = pl.Name;
                    string item = pl.CurrentItem; PlayItem it = null;
                    lock (playlistGate) if (item != null) it = PlayItem.Find(pl.Cfg.Playlist, item);
                    c[2].Text = !pl.Box.Connected ? "Not connected" : it != null ? "Playlist: " + it.Name : pl.Job != null ? (pl.Showing ?? "Playing from this PC")
                              : pl.Box.LastMode ?? (pl.Box.LastImage != null ? "Picture" : "Nothing sent yet");
                    c[3].Text = pl.PlaylistOn ? "Pause Playlist" : "Run Playlist";
                }
                var coming = UpNext(3);
                for (int k = 0; k < 3; k++)
                {
                    bool has = coming != null && k < coming.Count;
                    nextName[k].Text = has ? (k + 1) + ".  " + coming[k].Name : k == 0 && coming == null ? "The playlist is off." : "";
                    nextTime[k].Text = has ? Duration(coming[k].Seconds) : k == 0 && coming == null ? "Run it to see what plays next." : "";
                }
                playBtn.Text = playlistOn ? "Pause Playlist" : "Run Playlist";
                int[] img = box.LastImage; string mode = box.LastMode;
                preview.SetAll(img ?? new int[100]);
                string id = currentItem; PlayItem cur = null;
                lock (playlistGate) if (id != null) cur = PlayItem.Find(dcfg.Playlist, id);
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


        // the turned-on playlist items that come after the one playing (wrapping around); null while the playlist is off
        List<PlayItem> UpNext(int count)
        {
            if (!playlistOn) return null;
            var on = new List<PlayItem>();
            lock (playlistGate) foreach (var it in dcfg.Playlist) if (it.On) on.Add(it);
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
                new KeyValuePair<string, string>(EmptyPick, "Nothing (leave this button empty)"),
                new KeyValuePair<string, string>("clock", "Clock"), new KeyValuePair<string, string>("weather", "Weather") };
            foreach (var la in LiveAnims) list.Add(new KeyValuePair<string, string>(la.Id, la.Name));
            for (int m = 0; m < Proto.ModeNames.Length; m++) list.Add(new KeyValuePair<string, string>("mode" + m, Proto.ModeNames[m] + " (built-in)"));
            return list;
        }

        const string EmptyPick = "none";   // a Show Now button with nothing on it

        static string ShowNowName(string id)
        {
            if (id == EmptyPick) return "(empty)";
            foreach (var c in ShowNowChoices()) if (c.Key == id) return c.Value;
            return id;
        }

        void ShowNowRun(string id)
        {
            if (id == EmptyPick) status.Text = "This button is empty: right-click it to choose what it shows";
            else if (id == "clock") { StopRotationForManual(); ShowClockNow(); }
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

        void FlashNotification(int code)
        {
            if (!box.Connected) { status.Text = "Not connected"; return; }
            int mine = Interlocked.Increment(ref P.FlashCount);
            Interlocked.Exchange(ref P.HoldUntilTicks, DateTime.Now.AddSeconds(NotifySeconds).Ticks);
            string name = Proto.NotifyNames[code];
            status.Text = "Notification: " + name + (job != null ? " (playback holds for " + NotifySeconds + " s)" : "");
            var pl = P;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    pl.Box.Notify(code);
                    Thread.Sleep(TimeSpan.FromSeconds(NotifySeconds));
                    if (mine == Interlocked.CompareExchange(ref pl.FlashCount, 0, 0)) pl.Box.Redisplay();   // a newer flash restores instead
                }
                catch (Exception e)
                {
                    pl.Box.Close();
                    UiAs(pl, delegate { SetConnected(false, "Connection lost: " + e.Message); });
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
                    StartJob(delegate { PlayLiveAnim(la, DateTime.MaxValue); });
                    showing = la.Name;
                    status.Text = la.Name + " playing";
                }
        }

        // ---------- original modes: the selected display's own built-in screens ----------
        // AuraBox: modes, solid color, notification icons. Tivoo: clock, light, effects, tools, games (BuildTivooPanel).
        Panel BuildBoxPage()
        {
            var outer = NewPage();
            var p = new Panel { Size = new Size(W, H) };
            var tivoo = BuildTivooPanel();
            outer.Controls.Add(p); outer.Controls.Add(tivoo);
            Action follow = delegate { p.Visible = !(sel.Box is Tivoo); tivoo.Visible = sel.Box is Tivoo; };
            follow();
            deviceChanged += follow;
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

            // the built-in clock's color (12 or 24 hours follows the Clock & Weather page)
            var clockCard = NewCard("Built-In Clock", 0, 396, W, 76);
            clockCard.Controls.Add(new Label { Text = "Color:", Location = new Point(14, 42), AutoSize = true });
            var clockColor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(70, 38), Width = 120 };
            for (int k = 1; k < 8; k++) clockColor.Items.Add(Proto.ColorNames[k]);
            clockColor.SelectedIndex = Math.Max(1, Math.Min(7, cfg.AuraClockColor)) - 1;
            clockColor.SelectedIndexChanged += delegate { cfg.AuraClockColor = clockColor.SelectedIndex + 1; ApplyTivooClock(); SaveSettings(); };
            clockCard.Controls.Add(clockColor);
            clockCard.Controls.Add(Btn("Show Clock", 204, 35, 120, delegate { StopRotationForManual(); Run(delegate { box.SetMode(0); }, "Mode: Clock"); }, true));
            clockCard.Controls.Add(Note("12 or 24 hours: the Clock & Weather page.", 338, 42));
            p.Controls.Add(clockCard);
            p.Controls.Add(Note("Brightness is the slider at the bottom of the window. The audio input (AUX jack or Bluetooth)\r\n" +
                                "is on the Settings page.", 2, 482));
            return outer;
        }

        // the window switched to another display: pages that differ by kind of display follow
        Action deviceChanged = delegate { };

        // run something on the selected Tivoo (pausing the playlist, like the AuraBox modes do)
        void OnTivoo(Action<Tivoo> a, string done, bool manual = true)
        {
            var tv = box as Tivoo;
            if (tv == null) return;
            if (manual) StopRotationForManual();
            Run(delegate { a(tv); }, done);
        }

        // a color button: shows its color, and clicking it opens the Windows color picker
        Label ColorButton(int x, int y, Func<int> get, Action<int> set)
        {
            // a label, not a button: the theme repaints buttons, but leaves "swatch" labels their own color
            var b = new Label { Location = new Point(x, y), Size = new Size(46, 26), Tag = "swatch", BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
            new ToolTip().SetToolTip(b, "Click to pick a color");
            Action show = delegate { int c = get(); b.BackColor = Color.FromArgb(c >> 16 & 0xFF, c >> 8 & 0xFF, c & 0xFF); };
            show();
            b.Click += delegate
            {
                using (var dlg = new ColorDialog { FullOpen = true, Color = b.BackColor })
                    if (dlg.ShowDialog(this) == DialogResult.OK) { set(dlg.Color.ToArgb() & 0xFFFFFF); show(); }
            };
            return b;
        }

        // the Tivoo's clock as saved, for every Tivoo (12/24 hours from the Clock & Weather page)
        void ApplyTivooClock()
        {
            Tivoo.ClockStyle = cfg.TivooClockStyle; Tivoo.ClockColor = cfg.TivooClockColor;
            Tivoo.ClockWeather = cfg.TivooClockWeather; Tivoo.ClockTemp = cfg.TivooClockTemp; Tivoo.ClockCalendar = cfg.TivooClockCalendar;
            Tivoo.Hour24 = H24;
            Tivoo.Vivid = cfg.TivooVivid;
            DivoomCloud.PageSize = cfg.GalleryPageSize;
            AuraBox.ClockH24 = H24; AuraBox.ClockColor = cfg.AuraClockColor;
        }

        // the current weather to a Tivoo (for its clock's weather and its weather screen)
        void SendWeatherTo(Tivoo tv, WeatherInfo w)
        {
            double c = w.Unit == "F" ? (w.Temp - 32) * 5 / 9 : w.Temp;
            tv.SendWeather((int)Math.Round(c), Tivoo.WeatherType(w.Code), cfg.Fahrenheit);
        }

        Panel BuildTivooPanel()
        {
            var p = new Panel { Size = new Size(W, H), AutoScroll = true };   // scrolls for the last card
            const int CW = W - 22;   // card width, leaving room for the scroll bar
            int lightColor = 0xFF8000, lightEffect = 0;

            // the clock: style, what shows with it, its color; also used by the playlist's Clock
            var clock = NewCard("Clock", 0, 0, CW, 148);
            clock.Controls.Add(new Label { Text = "Style:", Location = new Point(14, 42), AutoSize = true });
            var style = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(70, 38), Width = 190 };
            style.Items.AddRange(Tivoo.ClockStyles);
            style.SelectedIndex = Math.Max(0, Math.Min(Tivoo.ClockStyles.Length - 1, cfg.TivooClockStyle));
            var wx = new CheckBox { Text = "Weather", Location = new Point(280, 41), AutoSize = true, Checked = cfg.TivooClockWeather };
            var tmp = new CheckBox { Text = "Temperature", Location = new Point(372, 41), AutoSize = true, Checked = cfg.TivooClockTemp };
            var cal = new CheckBox { Text = "Calendar", Location = new Point(496, 41), AutoSize = true, Checked = cfg.TivooClockCalendar };
            clock.Controls.Add(new Label { Text = "Color:", Location = new Point(14, 84), AutoSize = true });
            var color = ColorButton(70, 79, () => cfg.TivooClockColor, delegate (int c) { cfg.TivooClockColor = c; ApplyTivooClock(); SaveSettings(); });
            Action saveClock = delegate
            {
                cfg.TivooClockStyle = style.SelectedIndex; cfg.TivooClockWeather = wx.Checked; cfg.TivooClockTemp = tmp.Checked; cfg.TivooClockCalendar = cal.Checked;
                ApplyTivooClock(); SaveSettings();
            };
            style.SelectedIndexChanged += delegate { saveClock(); };
            wx.CheckedChanged += delegate { saveClock(); }; tmp.CheckedChanged += delegate { saveClock(); }; cal.CheckedChanged += delegate { saveClock(); };
            clock.Controls.Add(style); clock.Controls.Add(wx); clock.Controls.Add(tmp); clock.Controls.Add(cal); clock.Controls.Add(color);
            clock.Controls.Add(Btn("Show Clock", 136, 76, 130, delegate
            {
                var w = GetWeather(false);
                OnTivoo(tv => { if (w != null) SendWeatherTo(tv, w); tv.ShowClock(); }, "Showing the Tivoo clock");
            }, true));
            clock.Controls.Add(Btn("Show Weather", 274, 76, 130, delegate
            {
                var w = GetWeather(false);
                if (w == null) { status.Text = "No weather yet: check the place on the Clock & Weather page"; return; }
                OnTivoo(tv => { SendWeatherTo(tv, w); tv.ShowWeather(); }, "Weather on the Tivoo: " + Math.Round(w.Temp) + "°" + w.Unit + ", " + w.Description);
            }));
            clock.Controls.Add(Note("The playlist's Clock and Weather use these too. 12 or 24 hours: the Clock & Weather page.", 14, 118));
            p.Controls.Add(clock);

            // light, effects and the music visualizer
            var fx = NewCard("Light, Effects and Visualizer", 0, 158, CW, 112);
            fx.Controls.Add(new Label { Text = "Light:", Location = new Point(14, 44), AutoSize = true });
            fx.Controls.Add(ColorButton(70, 39, () => lightColor, delegate (int c) { lightColor = c; }));
            var effect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(124, 40), Width = 136 };
            effect.Items.AddRange(Tivoo.LightEffects); effect.SelectedIndex = 0;
            effect.SelectedIndexChanged += delegate { lightEffect = effect.SelectedIndex; };
            fx.Controls.Add(effect);
            fx.Controls.Add(Btn("Show Light", 268, 37, 120, delegate
            {
                int c = lightColor, e = lightEffect;
                OnTivoo(tv => tv.ShowLight(Color.FromArgb(c >> 16 & 0xFF, c >> 8 & 0xFF, c & 0xFF), e), "Tivoo light on");
            }));
            Func<string, ComboBox> numbers = delegate (string what)
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
                for (int k = 1; k <= 16; k++) cb.Items.Add(what + " " + k);
                cb.SelectedIndex = 0;
                return cb;
            };
            var fxn = numbers("Effect"); fxn.Location = new Point(14, 76); fx.Controls.Add(fxn);
            fx.Controls.Add(Btn("Show", 142, 73, 80, delegate { int n = fxn.SelectedIndex; OnTivoo(tv => tv.ShowEffect(n), "Tivoo effect " + (n + 1)); }));
            var viz = numbers("Visualizer"); viz.Location = new Point(268, 76); fx.Controls.Add(viz);
            fx.Controls.Add(Btn("Show", 396, 73, 80, delegate { int n = viz.SelectedIndex; OnTivoo(tv => tv.ShowVisualizer(n), "Tivoo visualizer " + (n + 1)); }));
            fx.Controls.Add(Note("The visualizer moves with\r\nmusic the Tivoo plays.", 494, 70));
            p.Controls.Add(fx);

            // tools: scoreboard, countdown, stopwatch, noise meter
            var tools = NewCard("Tools", 0, 280, CW, 148);
            tools.Controls.Add(new Label { Text = "Red:", Location = new Point(14, 44), AutoSize = true });
            var red = new NumericUpDown { Minimum = 0, Maximum = 999, Location = new Point(56, 40), Width = 60 };
            tools.Controls.Add(new Label { Text = "Blue:", Location = new Point(128, 44), AutoSize = true });
            var blue = new NumericUpDown { Minimum = 0, Maximum = 999, Location = new Point(174, 40), Width = 60 };
            tools.Controls.Add(red); tools.Controls.Add(blue);
            tools.Controls.Add(Btn("Show Scoreboard", 246, 37, 150, delegate
            {
                int r = (int)red.Value, b = (int)blue.Value;
                OnTivoo(tv => tv.ShowScoreboard(r, b), "Scoreboard " + r + " : " + b);
            }));
            tools.Controls.Add(Note("Change a score and\r\nshow it again to update.", 410, 36));
            tools.Controls.Add(new Label { Text = "Countdown:", Location = new Point(14, 80), AutoSize = true });
            var mm = new NumericUpDown { Minimum = 0, Maximum = 99, Value = 5, Location = new Point(100, 76), Width = 50 };
            var ss = new TwoDigitUpDown { Minimum = 0, Maximum = 59, Value = 0, Location = new Point(164, 76), Width = 50 };
            tools.Controls.Add(new Label { Text = ":", Location = new Point(152, 80), AutoSize = true });
            tools.Controls.Add(mm); tools.Controls.Add(ss);
            tools.Controls.Add(Btn("Start", 222, 73, 80, delegate
            {
                int m = (int)mm.Value, sec = (int)ss.Value;
                OnTivoo(tv => tv.ShowCountdown(m, sec), "Tivoo countdown: " + m + ":" + sec.ToString("00"));
            }));
            tools.Controls.Add(Btn("Stop", 308, 73, 70, delegate { OnTivoo(tv => tv.StopCountdown(), "Countdown stopped"); }));
            // the stopwatch and noise meter run on the device itself; these are the Divoom app's own buttons for them
            tools.Controls.Add(new Label { Text = "Stopwatch:", Location = new Point(14, 116), AutoSize = true });
            tools.Controls.Add(Btn("Start", 100, 109, 70, delegate { OnTivoo(tv => tv.ShowStopwatch(), "Stopwatch started"); }));
            tools.Controls.Add(Btn("Pause", 176, 109, 70, delegate { OnTivoo(tv => tv.StopwatchControl(0), "Stopwatch paused"); }));
            tools.Controls.Add(Btn("Reset", 252, 109, 70, delegate { OnTivoo(tv => tv.StopwatchControl(2), "Stopwatch reset"); }));
            tools.Controls.Add(new Label { Text = "Noise meter:", Location = new Point(346, 116), AutoSize = true });
            tools.Controls.Add(Btn("On", 440, 109, 60, delegate { OnTivoo(tv => tv.ShowNoiseMeter(), "Noise meter on"); }));
            tools.Controls.Add(Btn("Off", 506, 109, 60, delegate { OnTivoo(tv => tv.NoiseMeterOff(), "Noise meter off"); }));
            p.Controls.Add(tools);

            // the Tivoo's built-in games and their buttons
            var games = NewCard("Games", 0, 438, CW, 112);
            var game = numbers("Game"); game.Location = new Point(14, 42); game.Width = 110; games.Controls.Add(game);
            games.Controls.Add(Btn("Play", 132, 39, 80, delegate { int n = game.SelectedIndex; OnTivoo(tv => tv.ShowGame(n), "Tivoo game " + (n + 1)); }, true));
            string[] keys = { "Go", "◀", "▶", "▲", "▼", "OK" };
            for (int k = 0; k < keys.Length; k++)
            {
                int key = k;
                games.Controls.Add(Btn(keys[k], 236 + k * 58, 39, 52, delegate { OnTivoo(tv => tv.GameKey(key), "Sent " + keys[key], false); }));
            }
            games.Controls.Add(Note("Then press the buttons to play. Which games there are depends on the Tivoo's firmware.", 14, 80));
            p.Controls.Add(games);

            // notification icons, and how strongly colors are boosted for the Tivoo's LEDs
            var more = NewCard("Notifications and Colors", 0, 560, CW, 112);
            more.Controls.Add(new Label { Text = "Notification:", Location = new Point(14, 44), AutoSize = true });
            var notify = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 40), Width = 150 };
            notify.Items.AddRange(Proto.NotifyNames); notify.SelectedIndex = 2;
            more.Controls.Add(notify);
            more.Controls.Add(Btn("Flash", 268, 37, 80, delegate { FlashNotification(notify.SelectedIndex); }));
            more.Controls.Add(new Label { Text = "Color boost:", Location = new Point(14, 80), AutoSize = true });
            RadioRow(more, new[] { "Off", "Vivid", "Extra Vivid" }, new[] { 110, 170, 250 }, 78, cfg.TivooVivid,
                     delegate (int v) { cfg.TivooVivid = v; Tivoo.Vivid = v; SaveSettings(); status.Text = "Color boost: " + (v == 0 ? "off" : v == 1 ? "vivid" : "extra vivid") + " (applies to the next picture sent)"; });
            more.Controls.Add(Note("Deeper, richer colors on the LEDs;\r\nthe app's previews stay as they are.", 380, 70));
            p.Controls.Add(more);

            // the Tiivoo 2's own sound and screens (shown only for it)
            var t2 = NewCard("Tiivoo 2: Sound and Screens", 0, 682, CW, 326);
            Func<Action<Tiivoo2>, string, bool> onT2 = delegate (Action<Tiivoo2> act, string what)
            {
                var d = box as Tiivoo2;
                if (d == null) { status.Text = "Pick the Tiivoo 2 first"; return false; }
                StopRotationForManual();
                Run(delegate { act(d); }, what);
                return true;
            };
            t2.Controls.Add(new Label { Text = "White noise:", Location = new Point(14, 44), AutoSize = true });
            var sound = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 40), Width = 150, AccessibleName = "White noise sound" };
            sound.Items.AddRange(Tiivoo2.NoiseSounds); sound.SelectedIndex = 4;
            t2.Controls.Add(sound);
            var noiseVol = new TrackBar { Minimum = 5, Maximum = 100, SmallChange = 5, LargeChange = 10, Value = 40, Location = new Point(266, 36), Width = 130, TickStyle = TickStyle.None, AccessibleName = "White noise volume" };
            t2.Controls.Add(noiseVol);
            var noiseTime = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(402, 40), Width = 110, AccessibleName = "White noise time" };
            noiseTime.Items.AddRange(new object[] { "15 minutes", "30 minutes", "1 hour", "Until stopped" }); noiseTime.SelectedIndex = 1;
            t2.Controls.Add(noiseTime);
            Func<int[]> mix = delegate { var v = new int[8]; v[sound.SelectedIndex] = noiseVol.Value; return v; };
            Func<int> minutes = () => new[] { 15, 30, 60, 0 }[noiseTime.SelectedIndex];
            t2.Controls.Add(Btn("Play", 520, 37, 70, delegate { var v = mix(); int m = minutes(); onT2(d => d.WhiteNoise(v, m, true), "White noise: " + sound.Text); }, true));
            t2.Controls.Add(Btn("Stop", 596, 37, 70, delegate { var v = mix(); onT2(d => d.WhiteNoise(v, 0, false), "White noise off"); }));

            t2.Controls.Add(new Label { Text = "FM radio:", Location = new Point(14, 92), AutoSize = true });
            var fm = new NumericUpDown { Location = new Point(110, 88), Width = 80, Minimum = 87.5m, Maximum = 108.0m, Increment = 0.1m, DecimalPlaces = 1,
                                         Value = (decimal)Math.Max(87.5, Math.Min(108.0, cfg.FmMhz)), AccessibleName = "FM frequency" };
            t2.Controls.Add(fm);
            t2.Controls.Add(new Label { Text = "MHz", Location = new Point(196, 92), AutoSize = true, Tag = "subtle" });
            t2.Controls.Add(Btn("Tune In", 240, 85, 96, delegate
            {
                double mhz = (double)fm.Value; cfg.FmMhz = mhz; SaveSettings();
                onT2(d => d.FmTune(mhz), "FM radio " + mhz.ToString("0.0") + " MHz");
            }, true));
            t2.Controls.Add(Btn("Bluetooth Audio", 342, 85, 140, delegate { onT2(d => d.BluetoothAudio(), "Tiivoo 2 back to Bluetooth audio"); }));

            // lyrics (for music playing through it): its 16 backgrounds and 6 ways the lines arrive
            t2.Controls.Add(new Label { Text = "Lyrics:", Location = new Point(14, 140), AutoSize = true });
            var lyricBack = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 136), Width = 130, AccessibleName = "Lyrics background" };
            for (int i = 1; i <= 16; i++) lyricBack.Items.Add("Background " + i);
            lyricBack.SelectedIndex = 0;
            t2.Controls.Add(lyricBack);
            var lyricFx = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(246, 136), Width = 160, AccessibleName = "Lyrics text effect" };
            lyricFx.Items.AddRange(Tiivoo2.LyricEffects); lyricFx.SelectedIndex = 0;
            t2.Controls.Add(lyricFx);
            t2.Controls.Add(Btn("Show Lyrics", 412, 133, 120, delegate
            {
                int lyricBg = lyricBack.SelectedIndex, lyricHow = lyricFx.SelectedIndex;
                onT2(d => d.ShowLyrics(lyricBg, lyricHow), "Tiivoo 2 lyrics (for music playing through it)");
            }, true));

            t2.Controls.Add(new Label { Text = "Screens:", Location = new Point(14, 188), AutoSize = true });
            t2.Controls.Add(Btn("Photos", 110, 181, 96, delegate { onT2(d => d.ShowPhotos(), "Tiivoo 2 photo album"); }));
            t2.Controls.Add(Btn("SD Card Music", 212, 181, 130, delegate { onT2(d => d.SdCardMusic(), "Tiivoo 2 playing its SD card"); }));

            // a Pomodoro timer: minutes of work, short break, long break, and how many rounds
            t2.Controls.Add(new Label { Text = "Pomodoro:", Location = new Point(14, 236), AutoSize = true });
            Func<int, int, int, string, NumericUpDown> minutesBox = delegate (int x, int v, int max, string name)
            {
                var b = new NumericUpDown { Location = new Point(x, 232), Width = 52, Minimum = 1, Maximum = max, Value = v, AccessibleName = name };
                t2.Controls.Add(b); return b;
            };
            var work = minutesBox(110, 25, 120, "Work minutes");
            t2.Controls.Add(new Label { Text = "work", Location = new Point(164, 236), AutoSize = true, Tag = "subtle" });
            var shortRest = minutesBox(206, 5, 60, "Short break minutes");
            t2.Controls.Add(new Label { Text = "break", Location = new Point(260, 236), AutoSize = true, Tag = "subtle" });
            var longRest = minutesBox(306, 15, 60, "Long break minutes");
            t2.Controls.Add(new Label { Text = "long break", Location = new Point(360, 236), AutoSize = true, Tag = "subtle" });
            var rounds = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(436, 232), Width = 92, AccessibleName = "Pomodoro rounds" };
            rounds.Items.AddRange(new object[] { "Work only", "1 round", "2 rounds", "3 rounds", "4 rounds" }); rounds.SelectedIndex = 4;
            t2.Controls.Add(rounds);
            var ding = new CheckBox { Text = "Sound", Checked = true, Location = new Point(536, 234), AutoSize = true, AccessibleName = "Pomodoro sound" };
            t2.Controls.Add(ding);
            t2.Controls.Add(Btn("Start", 600, 229, 70, delegate
            {
                int w = (int)work.Value, s = (int)shortRest.Value, l = (int)longRest.Value, r = rounds.SelectedIndex; bool on = ding.Checked;
                onT2(d => d.Pomodoro(w, s, l, r, on), "Pomodoro started: " + w + " minutes of work (stop it early on the Tiivoo 2)");
            }, true));
            t2.Controls.Add(Note("The Pomodoro timer keeps the screen until it ends or is stopped on the Tiivoo 2. Visualizers, games,\r\nStopwatch, Scoreboard, Countdown and Noise Meter: the cards above.", 14, 268));
            p.Controls.Add(t2);

            // the Tiivoo 2's clock faces, from Divoom's clock store (ClockStore; needs the Divoom account)
            var faces = NewCard("Tiivoo 2: Clock Faces", 0, 1018, CW, 340);
            faces.Controls.Add(new Label { Text = "Shelf:", Location = new Point(14, 44), AutoSize = true });
            var shelf = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 40), Width = 220, AccessibleName = "Clock face shelf" };
            faces.Controls.Add(shelf);
            var faceIcons = new ImageList { ImageSize = new Size(64, 64), ColorDepth = ColorDepth.Depth24Bit };
            var faceView = new ListView { Location = new Point(14, 80), Size = new Size(CW - 28, 214), View = View.LargeIcon, LargeImageList = faceIcons,
                                          MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None, AccessibleName = "Clock faces" };
            AccentIcons(faceView);
            faces.Controls.Add(faceView);
            var faceNote = Note("", 14, 304);
            faces.Controls.Add(faceNote);
            var shelves = new List<KeyValuePair<int, string>>();
            int faceGen = 0;
            Action showFace = delegate
            {
                if (faceView.SelectedItems.Count == 0) { status.Text = "Pick a clock face first"; return; }
                var face = (ClockStore.Face)faceView.SelectedItems[0].Tag;
                onT2(d => d.ShowClockFace(face.Id, face.Name), "Tiivoo 2 clock face: " + face.Name);
            };
            faces.Controls.Add(Btn("Show on Tiivoo 2", 340, 37, 160, showFace, true));
            faceView.ItemActivate += delegate { showFace(); };
            // one shelf's faces, then their pictures one by one (newest request wins)
            Action loadShelf = delegate
            {
                int k = shelf.SelectedIndex;
                if (k < 0) return;
                int gen = ++faceGen;
                faceView.Items.Clear(); faceIcons.Images.Clear();
                faceNote.Text = "Loading clock faces...";
                int id = k == 0 ? -1 : shelves[k - 1].Key;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        var list = id < 0 ? ClockStore.Mine() : ClockStore.Faces(id, 1, 60);
                        BeginInvoke((Action)delegate
                        {
                            if (gen != faceGen) return;
                            faceView.BeginUpdate();
                            foreach (var f in list)
                            {
                                faceIcons.Images.Add(new Bitmap(64, 64));
                                faceView.Items.Add(new ListViewItem(f.Name, faceIcons.Images.Count - 1) { Tag = f });
                            }
                            faceView.EndUpdate();
                            faceNote.Text = list.Count + " clock faces. Double-click one to show it; the Tiivoo 2 fetches its parts through VaVaVoom.";
                        });
                        for (int i = 0; i < list.Count && gen == faceGen; i++)
                        {
                            Bitmap pic = null;
                            try { pic = ClockStore.Preview(list[i], 64); } catch (Exception) { }
                            if (pic == null) continue;
                            int at = i;
                            BeginInvoke((Action)delegate { if (gen == faceGen && at < faceIcons.Images.Count) { faceIcons.Images[at] = pic; faceView.Invalidate(); } });
                        }
                    }
                    catch (Exception e) { BeginInvoke((Action)delegate { if (gen == faceGen) faceNote.Text = e.Message; }); }
                });
            };
            shelf.SelectedIndexChanged += delegate { loadShelf(); };
            // the shelves, once: after sign-in and once the Tiivoo 2 has said who it is
            bool shelvesAsked = false;
            Action loadShelves = delegate
            {
                if (shelvesAsked) return;
                if (!DivoomAccount.SignedIn) { faceNote.Text = "Sign in to Divoom (Gallery tab, Divoom Account button) to use the clock store."; return; }
                if (!(box is Tiivoo2) || !box.Connected) { faceNote.Text = "Connect the Tiivoo 2 to load its clock faces."; return; }
                shelvesAsked = true;
                faceNote.Text = "Loading the clock store...";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        for (int w = 0; w < 30 && ClockStore.DeviceId == 0; w++) Thread.Sleep(200);
                        if (ClockStore.DeviceId == 0) throw new Exception("The Tiivoo 2 hasn't said its Divoom registration; reconnect it and try again.");
                        var got = ClockStore.Categories();
                        BeginInvoke((Action)delegate
                        {
                            shelves.Clear(); shelves.AddRange(got);
                            shelf.Items.Clear(); shelf.Items.Add("My Clocks");
                            foreach (var s in got) shelf.Items.Add(s.Value);
                            shelf.SelectedIndex = 0;
                        });
                    }
                    catch (Exception e) { shelvesAsked = false; BeginInvoke((Action)delegate { faceNote.Text = e.Message; }); }
                });
            };
            faces.VisibleChanged += delegate { if (faces.Visible) loadShelves(); };
            shelf.DropDown += delegate { loadShelves(); };
            faceView.Click += delegate { loadShelves(); };
            p.Controls.Add(faces);
            Action t2Shown = delegate { t2.Visible = sel.Box is Tiivoo2; faces.Visible = t2.Visible; };
            t2Shown();
            deviceChanged += t2Shown;
            return p;
        }

        const int MaxBrightness = 255;

        void SendBrightness()
        {
            int v = bright.Value;
            if (P.Brightness == v) return;   // just showing the selected display's level
            P.Brightness = v;
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
            var strip = NewCard("Animation Frames (up to " + AuraBox.MaxFrames + "): click to pick, double-click to edit", 0, 352, W, 164);
            for (int k = 0; k < AuraBox.MaxFrames; k++)
            {
                int i = k, x = 12 + k * 39;
                frameSlots[k] = new PixelGrid(3) { Location = new Point(x, 40), ReadOnly = true, Cursor = Cursors.Hand };
                frameRims[k] = new Label { Location = new Point(x - 3, 37), Size = new Size(36, 36), Tag = "swatch", BackColor = Color.Transparent };
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
                    if (frames.Count >= AuraBox.MaxFrames) { MessageBox.Show(this, "The AuraBox holds at most " + AuraBox.MaxFrames + " frames.", "VaVaVoom Control"); return; }
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
                if (lines.Length == 0 || Parse(lines[0]) == null) { MessageBox.Show(this, "Not an AuraBox design file.", "VaVaVoom Control"); return; }
                grid.SetAll(Parse(lines[0]));
                frames.Clear();
                for (int k = 1; k < lines.Length && frames.Count < 8; k++) { var f = Parse(lines[k]); if (f != null) frames.Add(f); }
                RefreshFrames();
            }
        }

        // ---------- gallery: built-in designs, favorites, Divoom's 16 x 16 art (by topic, search, the account's likes and
        // uploads), Divoom's older small art and LaMetric's icons, in one list down the left ----------
        class GallerySource
        {
            public string Title, Kind, Query;   // Kind: header, fav, builtin, d16, search, likes, uploads, classic, lametric
            public int Arg, Sort;                // d16: category and sort; classic: Divoom's category; builtin: -
            public string Key { get { return Kind + ":" + Arg + ":" + Sort + ":" + Query; } }
            public bool Cloud { get { return Kind != "fav" && Kind != "builtin" && Kind != "header" && Kind != "mylist" && Kind != "newlist"; } }
            public bool Mine { get { return Kind == "search" || Kind == "likes" || Kind == "uploads"; } }
            public bool KeepNames { get { return Mine || Kind == "holiday"; } }   // keep same-named designs (many are just "Halloween")
            public override string ToString() { return Title; }
        }

        static List<GallerySource> GallerySources(List<string> builtIn)
        {
            var list = new List<GallerySource>();
            Action<string> header = t => list.Add(new GallerySource { Title = t, Kind = "header" });
            header("MINE");
            list.Add(new GallerySource { Title = "★  Favorites 16×16", Kind = "fav", Arg = 16 });   // Arg: the designs' size
            list.Add(new GallerySource { Title = "★  Favorites 10×10", Kind = "fav", Arg = 10 });
            list.Add(new GallerySource { Title = "★  Favorites 128×128", Kind = "fav", Arg = 128 });
            list.Add(new GallerySource { Title = "My Divoom Likes", Kind = "likes" });
            list.Add(new GallerySource { Title = "My Divoom Uploads", Kind = "uploads" });
            header("DIVOOM 16×16");
            // Divoom's category numbers, matched to what each one actually holds (sampled 2026-11-10): 34 emoji, 23
            // characters (Yoda, The Simpsons, Toy Story), 4 fan characters (Venom, Harley), 9 games (Minecraft, Tetris,
            // Pac-Man), 24 patterns, 33 people. Characters shows 23, 4 and 33 (people) together. (Divoom's holiday
            // categories, 17, 25 and 30, mix holidays together, so each holiday below is a Divoom search instead.)
            string[] topics = { "Recommended", "Newest", "Emoji", "Characters", "Animals", "Food", "Plants", "Games", "Symbols", "Patterns" };
            int[] classify = { 18, 0, 34, 23, 32, 35, 31, 9, 7, 24 };
            for (int k = 0; k < topics.Length; k++)
                list.Add(new GallerySource { Title = topics[k], Kind = "d16", Arg = classify[k], Sort = k == 1 ? 0 : 1,   // Newest by date, the rest most liked
                                             Query = topics[k] == "Characters" ? "4,33" : null });                       // more categories shown with it
            // the same topics in Divoom's 128 x 128 art (for the Tiivoo 2; other displays get it shrunk)
            header("DIVOOM 128×128");
            for (int k = 0; k < topics.Length; k++)
                list.Add(new GallerySource { Title = topics[k], Kind = "d128", Arg = classify[k], Sort = k == 1 ? 0 : 1,
                                             Query = topics[k] == "Characters" ? "4,33" : null });
            // each holiday: the Divoom searches whose results it shows together (Holidays.cs)
            header("HOLIDAYS");
            foreach (var h in Holidays.All) list.Add(new GallerySource { Title = h.Name, Kind = "holiday", Query = h.Searches });
            header("SMALL ART");
            list.Add(new GallerySource { Title = "Classic: Featured", Kind = "classic", Arg = 1 });
            list.Add(new GallerySource { Title = "Classic: Popular", Kind = "classic", Arg = 2 });
            list.Add(new GallerySource { Title = "Classic: Newest", Kind = "classic", Arg = 3 });
            list.Add(new GallerySource { Title = "Icons: Popular", Kind = "lametric", Query = "popular" });
            list.Add(new GallerySource { Title = "Icons: Newest", Kind = "lametric", Query = "newest" });
            if (builtIn.Count > 0)
            {
                header("BUILT-IN");
                foreach (var c in builtIn) list.Add(new GallerySource { Title = c, Kind = "builtin", Query = c });
            }
            return list;
        }

        // one page of a cloud source; total: how many designs it has (-1 = not known yet)
        static List<DivoomCloud.Entry> CloudList(GallerySource s, int page, out int total)
        {
            total = -1;
            switch (s.Kind)
            {
                case "d16":
                case "d128":
                    bool big = s.Kind == "d128";
                    var d16 = DivoomCloud16.List(s.Arg, s.Sort, page, out total, big);
                    if (!string.IsNullOrEmpty(s.Query))   // more categories on the same pages (Characters: 23 and 4)
                        foreach (string c in s.Query.Split(','))
                        {
                            int more;
                            d16.AddRange(DivoomCloud16.List(int.Parse(c), s.Sort, page, out more, big));
                        }
                    // holiday designs belong under their holiday (Halloween's pumpkins and ghosts, Christmas, Easter...):
                    // the topic lists leave them out (Recommended and Newest show everything); checked on the original
                    // name, so Divoom's Haunted2026 contest tag counts too
                    if (s.Arg != 18 && s.Arg != 0) d16.RemoveAll(e => Holidays.Of(e.Name) != null);
                    return d16;
                case "holiday":   // its searches side by side on each page, each design once; a search Divoom refuses is skipped
                    var found = new List<DivoomCloud.Entry>();
                    var ids = new HashSet<string>();
                    Exception failed = null; int answered = 0;
                    foreach (string words in s.Query.Split('|'))
                    {
                        List<DivoomCloud.Entry> part;
                        int t;
                        try { part = DivoomCloud16.Search(words, page, out t); answered++; } catch (Exception ex) { failed = ex; continue; }
                        foreach (var e in part) if (ids.Add(e.Id)) found.Add(e);
                    }
                    if (answered == 0 && failed != null) throw failed;   // nothing answered: say why instead of showing an empty list
                    return found;
                case "search": return DivoomCloud16.Search(s.Query, page, out total);
                case "likes": return DivoomCloud16.Mine(true, page, out total);
                case "uploads": return DivoomCloud16.Mine(false, page, out total);
                case "lametric":
                    int pages;
                    var icons = LaMetricIcons.List(s.Query, page, out pages);
                    total = pages * DivoomCloud.PageSize;
                    return icons;
                default:   // classic: its animations and (for Featured and Popular) its pictures, side by side
                    var both = DivoomCloud.List(s.Arg, true, page);
                    if (s.Arg != 3) both.AddRange(DivoomCloud.List(s.Arg, false, page));
                    return both;
            }
        }

        // How many designs a list holds: find its last page that still has designs (Divoom sometimes sends a page a
        // design or two short, so only an empty page marks the end) by doubling the page number until one is empty,
        // then halving the gap. About a dozen small requests, spaced out so Divoom doesn't refuse them. Divoom stops
        // at 10,000 (333 pages).
        static int MeasureTotal(Func<int, int> countAt)
        {
            int size = DivoomCloud.PageSize, last = 10000 / size - 1;
            int c = countAt(0);
            if (c == 0) return 0;
            int lo = 0, loCount = c, hi = 1;   // lo: a page with designs; hi: the next page to try
            while (true)
            {
                Thread.Sleep(400);
                hi = Math.Min(hi, last);
                c = countAt(hi);
                if (c == 0) break;
                lo = hi; loCount = c;
                if (hi == last) return 10000;
                hi *= 2;
            }
            while (hi - lo > 1)
            {
                Thread.Sleep(400);
                int mid = (lo + hi) / 2;
                c = countAt(mid);
                if (c > 0) { lo = mid; loCount = c; } else hi = mid;
            }
            // the Gallery's page size changed meanwhile: the pages measured were of different sizes, so the answer is wrong
            if (DivoomCloud.PageSize != size) throw new InvalidOperationException("The page size changed while measuring");
            return lo * size + loCount;
        }

        // classic lists don't say how long they are: measure them (slow, so once per session, in the background)
        static int ClassicCount(GallerySource s)
        {
            int anims = DivoomCloud.Count(s.Arg, true), pics = s.Arg != 3 ? DivoomCloud.Count(s.Arg, false) : 0;
            return Math.Max(anims, pics);
        }

        static List<int[]> CloudFrames(string id)
        {
            if (id.StartsWith(DivoomCloud16.Prefix128)) return DivoomCloud16.Frames128(id.Substring(DivoomCloud16.Prefix128.Length));
            if (id.StartsWith(DivoomCloud16.Prefix)) return DivoomCloud16.Frames(id.Substring(DivoomCloud16.Prefix.Length));
            return id.StartsWith(LaMetricIcons.Prefix) ? LaMetricIcons.Frames(id.Substring(LaMetricIcons.Prefix.Length)) : DivoomCloud.Frames(id);
        }

        Panel BuildGalleryPage()
        {
            var p = NewPage();
            var items = GalleryItem.Load();
            foreach (var it in items) { string plain = GalleryNames.NoYears(it.Name); if (plain.Length > 0) it.Name = plain; Renamed(it); }
            var cats = new List<string>();
            foreach (var it in items) if (!cats.Contains(it.Category)) cats.Add(it.Category);
            var favorites = GalleryItem.LoadFavorites();
            var myLists = MyLists.Load();
            var sources = GallerySources(cats);

            // the sources down the left: section headings (not pickable) and the lists under them
            const int NavW = 176, X = NavW + 10, VW = W - X;
            var nav = new AccessibleListBox { Location = new Point(0, 0), Size = new Size(NavW, 384), DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(23),
                                              IntegralHeight = false, BorderStyle = BorderStyle.None, AccessibleName = "Gallery lists" };
            nav.IsHeading = o => ((GallerySource)o).Kind == "header";
            nav.NameOf = o => ((GallerySource)o).Title.Replace("★  ", "");
            nav.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                var src = (GallerySource)nav.Items[e.Index];
                bool head = src.Kind == "header", picked = (e.State & DrawItemState.Selected) != 0 && !head;
                using (var bg = new SolidBrush(picked ? Theme.AccentFill : nav.BackColor)) e.Graphics.FillRectangle(bg, e.Bounds);
                var font = head ? new Font(Theme.UiFont, 7.5f, FontStyle.Bold) : nav.Font;
                var color = picked ? Theme.AccentText : head ? Theme.Subtle : Theme.Text;
                var r = new Rectangle(e.Bounds.X + (head ? 4 : 12), e.Bounds.Y + (head ? 5 : 0), e.Bounds.Width - 14, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, src.Title, font, r, color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (head) font.Dispose();
            };
            foreach (var s in sources) nav.Items.Add(s);
            p.Controls.Add(nav);
            // the user's own lists sit under the favorites, then "+ New List…"; rebuilt whenever a list is made,
            // renamed or deleted (navQuiet: the nav's selection changes meanwhile don't load anything)
            bool navQuiet = false;
            Action refreshLists = delegate
            {
                navQuiet = true;
                try
                {
                    for (int k = nav.Items.Count - 1; k >= 0; k--)
                    {
                        var s = (GallerySource)nav.Items[k];
                        if (s.Kind == "mylist" || s.Kind == "newlist") nav.Items.RemoveAt(k);
                    }
                    int at = -1;
                    for (int k = 0; k < nav.Items.Count; k++) if (((GallerySource)nav.Items[k]).Kind == "fav") at = k;
                    foreach (var n in myLists.Names) nav.Items.Insert(++at, new GallerySource { Title = "▸  " + n, Kind = "mylist", Query = n });
                    nav.Items.Insert(++at, new GallerySource { Title = "+  New List…", Kind = "newlist" });
                }
                finally { navQuiet = false; }
            };
            refreshLists();
            nav.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            var account = Btn("Divoom Account…", 0, 424, NavW, delegate { DivoomAccountDialog(); });
            account.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            p.Controls.Add(account);

            // top: search (filters the list shown as you type; Enter searches all of Divoom's 16 x 16 art), English names
            var search = new TextBox { Location = new Point(X, 3), Width = 190 };
            search.HandleCreated += delegate { SendMessage(search.Handle, EM_SETCUEBANNER, (IntPtr)1, "Filter by name, or press Enter to search Divoom"); };
            p.Controls.Add(search);
            var english = new CheckBox { Text = "English names", Location = new Point(X + 200, 5), AutoSize = true, Checked = cfg.TranslateNames };
            p.Controls.Add(english);
            // only the animated designs (on at first): still pictures are left out of every list
            var animatedOnly = new CheckBox { Text = "Animated only", Location = new Point(X + 312, 5), AutoSize = true, Checked = cfg.GalleryAnimatedOnly,
                                              AccessibleName = "Show only animated designs" };
            p.Controls.Add(animatedOnly);
            // every animated thumbnail plays (only the ones in view, each at its own speed); this pauses them all
            bool animateAll = cfg.GalleryAnimate;
            var animBtn = Btn(animateAll ? "❚❚  Pause" : "▶  Play", W - 92, 0, 92, null);   // short, to leave room for the filters
            animBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            animBtn.AccessibleName = animateAll ? "Pause animations" : "Play animations";
            p.Controls.Add(animBtn);
            var title = new Label { Location = new Point(X, 36), Size = new Size(VW - 300, 22), Font = Theme.Semibold(10f), UseMnemonic = false, AutoEllipsis = true };
            p.Controls.Add(title);
            // how many designs each page of an online list loads (10 to 150): more to scroll through, fewer to load
            // faster; under the lists, above the account button
            var perLabel = new Label { Location = new Point(0, 393), Size = new Size(78, 20), UseMnemonic = false };
            var perPage = new TrackBar { Minimum = 1, Maximum = 15, SmallChange = 1, LargeChange = 3, Location = new Point(76, 388), Width = NavW - 76,
                                         AutoSize = false, Height = Theme.Px(26), TickStyle = TickStyle.None, Value = Math.Max(1, Math.Min(15, cfg.GalleryPageSize / 10)),
                                         AccessibleName = "Designs per page" };
            perLabel.Text = perPage.Value * 10 + " per page";
            perLabel.Anchor = perPage.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            p.Controls.Add(perLabel); p.Controls.Add(perPage);
            var prev = Btn("‹ Previous", W - 290, 31, 96, null); var next = Btn("Next ›", W - 96, 31, 96, null);
            var pageLabel = new Label { Location = new Point(W - 192, 36), Size = new Size(94, 20), TextAlign = ContentAlignment.TopCenter, UseMnemonic = false };
            prev.Anchor = next.Anchor = pageLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            p.Controls.Add(prev); p.Controls.Add(next); p.Controls.Add(pageLabel);
            var favSort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(W - 170, 32), Width = 170, Visible = false,
                                         AccessibleName = "Favorites order" };
            favSort.Items.AddRange(new object[] { "A to Z", "Newest first", "Oldest first" });
            favSort.SelectedIndex = cfg.FavoriteSort;
            favSort.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            p.Controls.Add(favSort);
            var totals = new Dictionary<string, int>();   // source -> how many designs, once known
            var counting = new HashSet<string>();

            int thumb = Theme.Px(50);
            var images = new ImageList { ImageSize = new Size(thumb, thumb), ColorDepth = ColorDepth.Depth24Bit };
            var view = new ListView { Location = new Point(X, 64), Size = new Size(VW, 388), View = View.LargeIcon, LargeImageList = images,
                                      MultiSelect = true, HideSelection = false, BorderStyle = BorderStyle.None };
            AccentIcons(view);
            view.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            p.Controls.Add(view);
            var shown = new List<GalleryItem>();
            var preview = new System.Windows.Forms.Timer { Interval = 250 };
            int cloudPage = 0, generation = 0;
            GallerySource cur = null, searchSource = null;
            var pageCache = new Dictionary<string, List<GalleryItem>>();      // "source:page" -> deduplicated items
            var pageNotes = new Dictionary<string, string>();                  // "source:page" -> what the status said
            var downloads = new Dictionary<string, List<int[]>>();            // FileId -> frames
            var seen = new HashSet<string>();                                 // looks already shown (built-ins included)
            foreach (var it in items) seen.Add(Signature(it.Frames));

            // the list loaded for the source (all of it, or one cloud page) is kept in pageItems; typing in the search
            // box shows only the names containing the text
            var pageItems = new List<GalleryItem>();
            Func<GalleryItem, bool> matches = it =>
                (!animatedOnly.Checked || it.IsAnimation) &&
                (search.Text.Trim().Length == 0 || (it.Name ?? "").IndexOf(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0);
            Action<GalleryItem> display = delegate (GalleryItem it)
            {
                images.Images.Add(Proto.Render(it.Frames[BestFrame(it)], Math.Max(1, thumb / 10)));
                view.Items.Add(new ListViewItem(it.Name, images.Images.Count - 1));
                shown.Add(it);
            };
            Action<GalleryItem> addItem = delegate (GalleryItem it) { pageItems.Add(it); if (matches(it)) display(it); };
            Action clearView = delegate { view.Items.Clear(); images.Images.Clear(); shown.Clear(); pageItems.Clear(); };
            bool searching = false;   // set while Enter starts a Divoom search, so the box's text isn't used as a filter
            bool toldTranslate = false;   // the translator's trouble is mentioned once
            search.TextChanged += delegate
            {
                if (searching) return;
                view.BeginUpdate();
                view.Items.Clear(); images.Images.Clear(); shown.Clear();
                foreach (var it in pageItems) if (matches(it)) display(it);
                view.EndUpdate();
                if (search.Text.Trim().Length > 0)
                    status.Text = shown.Count + " of " + pageItems.Count + " match “" + search.Text.Trim() + "”" + (cur != null && cur.Cloud ? " on this page (press Enter to search all of Divoom)" : "");
            };

            var favBtn = Btn("Add to Favorites", W - 170, 462, 170, null);
            favBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Action load = null;
            Func<bool> onFavorites = () => cur != null && cur.Kind == "fav";
            Func<bool> onList = () => cur != null && cur.Kind == "mylist";
            // make a list (asks for its name); null when cancelled
            Func<string> newList = null;
            Action<string> selectList = null;
            newList = delegate
            {
                string n = AskText("New List", "Name for the new list:", "");
                if (n == null) return null;
                n = n.Trim().Replace("|", "/");
                if (n.Length == 0) return null;
                if (myLists.Names.Contains(n)) return n;
                myLists.Names.Add(n);
                try { myLists.Save(); }
                catch (Exception e) { myLists.Names.Remove(n); status.Text = "Could not save the list: " + e.Message; return null; }
                refreshLists();
                status.Text = "Made the list “" + n + "”: right-click any design and choose Add to List";
                return n;
            };
            selectList = delegate (string n)
            {
                foreach (GallerySource x in nav.Items) if (x.Kind == "mylist" && x.Query == n) { nav.SelectedItem = x; return; }
            };
            // the selected designs (Ctrl- or Shift-click picks several), in the order shown
            Func<List<GalleryItem>> picks = delegate
            {
                var o = new List<GalleryItem>();
                foreach (int i in view.SelectedIndices) if (i < shown.Count) o.Add(shown[i]);
                return o;
            };
            // the one design that Send, Edit and Play act on: the focused one when it is selected, else the first selected
            Func<GalleryItem> one = delegate
            {
                if (view.SelectedIndices.Count == 0) return null;
                var f = view.FocusedItem;
                int i = f != null && f.Selected ? f.Index : view.SelectedIndices[0];
                return i < shown.Count ? shown[i] : null;
            };
            Func<List<GalleryItem>, string> several = its => its.Count == 1 ? "“" + its[0].Name + "”" : its.Count + " designs";
            // copies of designs put in one of the user's lists (each look once per list), saved in one go
            Action<List<GalleryItem>, string> addToList = delegate (List<GalleryItem> its, string n)
            {
                var have = new HashSet<string>();
                foreach (var x in myLists.In(n)) have.Add(Signature(x.Frames));
                var added = new List<GalleryItem>();
                foreach (var it in its)
                {
                    if (!have.Add(Signature(it.Frames))) continue;
                    added.Add(it.CopyAs(n, it.Name));
                }
                int already = its.Count - added.Count;
                if (added.Count == 0) { status.Text = (its.Count == 1 ? "“" + its[0].Name + "” is" : "All " + its.Count + " are") + " already in “" + n + "”"; return; }
                myLists.Items.AddRange(added);
                try { myLists.Save(); }
                catch (Exception e) { foreach (var c in added) myLists.Items.Remove(c); status.Text = "Could not save the list: " + e.Message; return; }
                status.Text = "Added " + several(added) + " to “" + n + "”" + (already > 0 ? " (" + already + " already there)" : "");
            };
            favSort.SelectedIndexChanged += delegate
            {
                if (cfg.FavoriteSort == favSort.SelectedIndex) return;
                cfg.FavoriteSort = favSort.SelectedIndex; SaveSettings();
                if (onFavorites() || onList()) load();
            };
            // a copy of a design saved to favorites (false when the same look is already there)
            Func<GalleryItem, string, bool> saveFavorite = delegate (GalleryItem it, string name)
            {
                string sig = Signature(it.Frames);
                foreach (var f in favorites) if (Signature(f.Frames) == sig) { status.Text = "“" + f.Name + "” is already a favorite"; return false; }
                var copy = it.CopyAs("Favorites", name);
                favorites.Add(copy);
                try { GalleryItem.SaveFavorites(favorites); }
                catch (Exception e) { favorites.Remove(copy); status.Text = "Could not save favorites: " + e.Message; return false; }
                int side = copy.Frames.Count > 0 ? Proto.Side(copy.Frames[0]) : 10;
                status.Text = "Saved “" + name + "” to Favorites " + side + "×" + side + " (kept on this PC)";
                return true;
            };
            // a 16 x 16 design made 10 x 10 for the AuraBox (each frame shrunk to its 8 colors), saved to Favorites 10×10
            // to touch up with Edit
            Action<GalleryItem> save10 = delegate (GalleryItem it)
            {
                var small = new GalleryItem { Category = "Favorites" };
                var sigs = new HashSet<string>();
                foreach (var f in it.Frames)
                {
                    var px = Proto.To10(f);
                    if (it.Frames.Count == 1 || sigs.Add(string.Join(",", px))) small.Frames.Add(px);   // repeated frames once
                    if (small.Frames.Count >= 60) break;
                }
                if (saveFavorite(small, it.Name + " (10×10)"))
                    status.Text += ": open Favorites 10×10 to edit it";
            };
            // several designs saved to favorites in one go (each look once)
            Action<List<GalleryItem>> addFavorites = delegate (List<GalleryItem> its)
            {
                if (its.Count == 1) { saveFavorite(its[0], its[0].Name); return; }
                var have = new HashSet<string>();
                foreach (var f in favorites) have.Add(Signature(f.Frames));
                var added = new List<GalleryItem>();
                foreach (var it in its)
                {
                    if (!have.Add(Signature(it.Frames))) continue;
                    added.Add(it.CopyAs("Favorites", it.Name));
                }
                int already = its.Count - added.Count;
                if (added.Count == 0) { status.Text = "All " + its.Count + " are already favorites"; return; }
                favorites.AddRange(added);
                try { GalleryItem.SaveFavorites(favorites); }
                catch (Exception e) { foreach (var c in added) favorites.Remove(c); status.Text = "Could not save favorites: " + e.Message; return; }
                status.Text = "Saved " + several(added) + " to Favorites (kept on this PC)" + (already > 0 ? "; " + already + " already there" : "");
            };
            // designs taken out of favorites (by how they look, so it works from any list)
            Action<List<GalleryItem>> removeFavorites = delegate (List<GalleryItem> its)
            {
                var sigs = new HashSet<string>();
                foreach (var it in its) sigs.Add(Signature(it.Frames));
                var before = new List<GalleryItem>(favorites);
                int gone = favorites.RemoveAll(f => sigs.Contains(Signature(f.Frames)));
                if (gone == 0) { status.Text = (its.Count == 1 ? "“" + its[0].Name + "” is not a favorite" : "None of these are favorites"); return; }
                try { GalleryItem.SaveFavorites(favorites); }
                catch (Exception e) { favorites.Clear(); favorites.AddRange(before); status.Text = "Could not save favorites: " + e.Message; return; }
                if (onFavorites()) load();
                status.Text = "Removed " + (gone == 1 && its.Count == 1 ? "“" + its[0].Name + "”" : gone + " design" + (gone == 1 ? "" : "s")) + " from favorites";
            };
            favBtn.Click += delegate
            {
                var its = picks();
                if (its.Count == 0) { status.Text = "Pick a design first (Ctrl- or Shift-click picks several)"; return; }
                if (onFavorites()) removeFavorites(its); else addFavorites(its);
            };
            p.Controls.Add(favBtn);

            // right-click a design: rename or remove it in Favorites, or add it to favorites from any other list
            var itemMenu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new MenuRenderer() };
            Action<ListViewItem> rename = delegate (ListViewItem item) { view.LabelEdit = true; item.BeginEdit(); };
            Point menuAt = Point.Empty;   // where the menu was asked for, in the list's coordinates
            itemMenu.Opening += delegate (object s, System.ComponentModel.CancelEventArgs e)
            {
                itemMenu.Items.Clear();
                var hit = view.HitTest(menuAt).Item;
                if (hit == null) { e.Cancel = true; return; }
                // right-clicking one of several selected designs keeps them all; any other picks just that one
                if (!hit.Selected) { view.SelectedItems.Clear(); hit.Selected = true; }
                hit.Focused = true;
                var picked = picks();
                bool many = picked.Count > 1;
                string them = many ? " (" + picked.Count + ")" : "";
                itemMenu.Items.Add("Send to " + P.Name, null, delegate { gallerySend.PerformClick(); });
                var big = picked.FindAll(x => x.Frames.Count > 0 && Proto.Side(x.Frames[0]) == 16);
                if (big.Count > 0)
                    itemMenu.Items.Add("Make 10×10 Cop" + (big.Count > 1 ? "ies (" + big.Count + ")" : "y") + " (Favorites)", null, delegate
                    {
                        foreach (var x in big) save10(x);
                        if (big.Count > 1) status.Text = "Made 10×10 copies of " + big.Count + " designs in Favorites 10×10";
                    });
                if (!many) itemMenu.Items.Add("Rename", null, delegate { rename(hit); });
                var addTo = new ToolStripMenuItem("Add to List" + them);
                foreach (var n in myLists.Names)
                {
                    string ln = n;
                    if (onList() && cur.Query == ln) continue;
                    addTo.DropDownItems.Add(ln, null, delegate { addToList(picked, ln); });
                }
                addTo.DropDownItems.Add("New List…", null, delegate { string ln = newList(); if (ln != null) addToList(picked, ln); });
                itemMenu.Items.Add(addTo);
                if (onList())
                {
                    itemMenu.Items.Add("Remove From This List" + them, null, delegate
                    {
                        string ln = cur.Query;
                        foreach (var x in picked) myLists.Items.Remove(x);
                        try { myLists.Save(); }
                        catch (Exception ex) { myLists.Items.AddRange(picked); status.Text = "Could not save the list: " + ex.Message; return; }
                        load(); status.Text = "Removed " + several(picked) + " from “" + ln + "”";
                    });
                }
                if (onFavorites())
                {
                    itemMenu.Items.Add("Remove From Favorites" + them, null, delegate { removeFavorites(picked); });
                    return;
                }
                // any other list: Add for the ones not yet favorites, Remove for the ones that are
                var favSigs = new HashSet<string>();
                foreach (var f in favorites) favSigs.Add(Signature(f.Frames));
                var notFav = picked.FindAll(x => !favSigs.Contains(Signature(x.Frames)));
                var isFav = picked.FindAll(x => favSigs.Contains(Signature(x.Frames)));
                if (notFav.Count > 0) itemMenu.Items.Add("Add to Favorites" + (many ? " (" + notFav.Count + ")" : ""), null, delegate { addFavorites(notFav); });
                if (isFav.Count > 0) itemMenu.Items.Add("Remove From Favorites" + (many ? " (" + isFav.Count + ")" : ""), null, delegate { removeFavorites(isFav); });
            };
            // the menu opens toward the inside of the window: near the right edge (the right-most designs, with the window
            // maximized) it and its Add to List menu open to the left, instead of spilling onto the next monitor
            Action<Point> showMenu = delegate (Point at)
            {
                menuAt = at;
                int right = view.RectangleToScreen(view.ClientRectangle).Right, x = view.PointToScreen(at).X;
                bool left = x + Theme.Px(2 * 240) > right;
                itemMenu.Show(view, at, left ? ToolStripDropDownDirection.BelowLeft : ToolStripDropDownDirection.BelowRight);
                foreach (ToolStripItem i in itemMenu.Items)
                {
                    var sub = i as ToolStripMenuItem;
                    if (sub != null && sub.HasDropDownItems) sub.DropDownDirection = left ? ToolStripDropDownDirection.Left : ToolStripDropDownDirection.Right;
                }
            };
            view.MouseUp += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Right) showMenu(e.Location); };
            view.KeyDown += delegate (object s, KeyEventArgs e)   // F2 renames too, like in Explorer
            {
                if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))   // the menu key: the menu at the focused design
                {
                    e.SuppressKeyPress = true;
                    var f = view.FocusedItem ?? (view.SelectedItems.Count > 0 ? view.SelectedItems[0] : null);
                    if (f != null) { var r = view.GetItemRect(f.Index, ItemBoundsPortion.Icon); showMenu(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2)); }
                }
                if (e.KeyCode == Keys.F2 && view.SelectedItems.Count == 1) rename(view.SelectedItems[0]);
                if (e.KeyCode == Keys.A && e.Control)   // Ctrl+A picks every design shown
                {
                    e.SuppressKeyPress = true;
                    view.BeginUpdate(); foreach (ListViewItem x in view.Items) x.Selected = true; view.EndUpdate();
                }
            };
            view.AfterLabelEdit += delegate (object s, LabelEditEventArgs e)
            {
                view.LabelEdit = false;
                e.CancelEdit = true;   // the list is redrawn (in A-Z order) from the saved names instead
                string name = e.Label == null ? "" : e.Label.Trim().Replace("|", "/");
                if (name.Length == 0 || e.Item >= shown.Count) return;
                var it = shown[e.Item];
                string old = it.Name;
                if (name == old) return;
                it.Name = name;
                try
                {
                    if (onFavorites()) GalleryItem.SaveFavorites(favorites);
                    else if (onList()) myLists.Save();
                    else
                    {
                        // any other design: the new name is kept on this PC by how it looks, so it sticks wherever the
                        // design shows up again; a downloaded one also gets it as its saved name
                        SaveRename(it, name);
                        if (it.Key != null) DesignCache.SetName(it.Key, name);
                    }
                }
                catch (Exception ex) { it.Name = old; status.Text = "Could not save the name: " + ex.Message; return; }
                BeginInvoke((Action)delegate { load(); status.Text = "Renamed “" + old + "” to “" + name + "”"; });
            };

            // "Page 3 of 51" on every cloud list; built-in lists and favorites show everything at once
            Action showPaging = delegate
            {
                bool cloud = cur.Cloud;
                prev.Visible = next.Visible = pageLabel.Visible = cloud;
                favSort.Visible = cur.Kind == "fav" || cur.Kind == "mylist";
                if (!cloud) return;
                int total;
                bool known = totals.TryGetValue(cur.Key, out total);
                if (!known && !cur.Mine && DesignCache.TryTotal(cur.Key, out total)) { totals[cur.Key] = total; known = true; }
                int pages = known ? Math.Max(1, (total + DivoomCloud.PageSize - 1) / DivoomCloud.PageSize) : 0;
                pageLabel.Text = "Page " + (cloudPage + 1) + " of " + (known ? pages.ToString("N0") + (total >= 10000 ? "+" : "") : "…");
                prev.Enabled = cloudPage > 0;
                next.Enabled = !known || cloudPage + 1 < pages;
            };

            load = delegate
            {
                int gen = ++generation;
                clearView();
                if (cur == null) return;
                var src = cur;
                title.Text = src.Kind == "search" ? "Divoom search: “" + src.Query + "”" : src.Title.Replace("★  ", "").Replace("▸  ", "");
                favBtn.Text = src.Kind == "fav" ? "Remove From Favorites" : "Add to Favorites";
                showPaging();
                if (src.Kind == "fav")
                {
                    view.BeginUpdate();
                    var mine = favorites.FindAll(f => f.Frames.Count > 0 && Proto.Side(f.Frames[0]) == src.Arg);   // oldest first, as saved
                    var order = cfg.FavoriteSort == 0 ? SortedUnique(mine, false) : mine;
                    if (cfg.FavoriteSort == 1) order.Reverse();
                    foreach (var it in order) addItem(it);
                    view.EndUpdate();
                    string size = src.Arg + "×" + src.Arg;
                    status.Text = mine.Count == 0 ? "No " + size + " favorites yet: right-click a design anywhere and choose Add to Favorites"
                        : mine.Count + " " + size + " favorite" + (mine.Count == 1 ? "" : "s");
                    return;
                }
                if (src.Kind == "mylist")
                {
                    view.BeginUpdate();
                    var inList = myLists.In(src.Query);   // oldest first, as added
                    var order = cfg.FavoriteSort == 0 ? SortedUnique(inList, false) : inList;
                    if (cfg.FavoriteSort == 1) order.Reverse();
                    foreach (var it in order) addItem(it);
                    view.EndUpdate();
                    status.Text = inList.Count == 0 ? "“" + src.Query + "” is empty: right-click a design anywhere and choose Add to List"
                        : inList.Count + " design" + (inList.Count == 1 ? "" : "s") + " in “" + src.Query + "”";
                    return;
                }
                if (src.Kind == "builtin")
                {
                    view.BeginUpdate();
                    foreach (var it in SortedUnique(items.FindAll(i => i.Category == src.Query))) addItem(it);
                    view.EndUpdate();
                    status.Text = pageItems.Count + " built-in designs";
                    return;
                }
                if ((src.Kind == "likes" || src.Kind == "uploads") && !DivoomAccount.SignedIn)
                {
                    status.Text = "Sign in with the Divoom Account button (bottom left) to see your " + (src.Kind == "likes" ? "likes" : "uploads");
                    return;
                }
                int page = cloudPage;
                if (src.Kind == "classic" && !totals.ContainsKey(src.Key) && counting.Add(src.Key))
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        int n = -1;
                        try { n = ClassicCount(src); } catch (Exception) { }
                        if (n >= 0) DesignCache.SetTotal(src.Key, n);
                        try { BeginInvoke((Action)delegate { counting.Remove(src.Key); if (n < 0) return; totals[src.Key] = n; if (cur == src) showPaging(); }); }
                        catch (InvalidOperationException) { }   // window closed meanwhile
                    });
                string key = src.Key + "#" + page + "x" + DivoomCloud.PageSize + (src.Mine ? "#" + DivoomAccount.UserId : "");
                List<GalleryItem> cached;
                if (pageCache.TryGetValue(key, out cached))
                {
                    view.BeginUpdate();
                    foreach (var it in cached) addItem(it);
                    view.EndUpdate();
                    status.Text = pageNotes[key];
                    return;
                }
                status.Text = "Loading " + title.Text + ", page " + (page + 1) + "...";
                bool translate = english.Checked;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        int total;
                        var list = CloudList(src, page, out total);
                        // how long the list is: LaMetric says, and so do the account's own
                        // lists; Divoom's other lists always claim 10,000, so those are measured once in the background
                        bool lastPage = src.Kind != "classic" && page == 0 && list.Count == 0;
                        bool told = src.Kind == "lametric" || ((src.Kind == "likes" || src.Kind == "uploads") && total >= 0 && total < 10000);
                        if (lastPage) total = page * DivoomCloud.PageSize + list.Count;
                        if (lastPage || told) BeginInvoke((Action)delegate { totals[src.Key] = total; if (cur == src) showPaging(); });
                        else if (src.Kind != "classic") BeginInvoke((Action)delegate
                        {
                            if (totals.ContainsKey(src.Key) || !counting.Add(src.Key)) return;
                            ThreadPool.QueueUserWorkItem(delegate
                            {
                                int n = -1;
                                try { n = MeasureTotal(pg => { int t; return CloudList(src, pg, out t).Count; }); } catch (Exception) { }
                                if (n >= 0 && !src.Mine) DesignCache.SetTotal(src.Key, n);
                                try { BeginInvoke((Action)delegate { counting.Remove(src.Key); if (n < 0) return; totals[src.Key] = n; if (cur == src) showPaging(); }); }
                                catch (InvalidOperationException) { }
                            });
                        });
                        // English names: a design already saved on this PC keeps the English name saved with it; the
                        // rest are tidied and put into English in one go, and saved with the design once it loads
                        var names = new List<string>();
                        var savedName = new bool[list.Count];
                        var ask = new List<string>();
                        for (int i = 0; i < list.Count; i++)
                        {
                            string known = translate ? DesignCache.Name(DesignCache.KeyOf(list[i].Id)) : null;
                            savedName[i] = known != null;
                            names.Add(known ?? GalleryNames.Clean(list[i].Name));
                            if (known == null) ask.Add(names[i]);
                        }
                        GalleryNames.TranslateOn = translate;
                        bool translated = false;
                        if (ask.Count > 0)
                        {
                            ask = GalleryNames.Translate(ask, out translated);
                            for (int i = 0, k = 0; i < list.Count; i++) if (!savedName[i]) names[i] = ask[k++];
                        }
                        var kept = new List<GalleryItem>();
                        var mine = new HashSet<string>();
                        int dupes = 0;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var e = list[i];
                            if (gen != generation) return;
                            List<int[]> fr;
                            lock (downloads)
                                if (!downloads.TryGetValue(e.Id, out fr))
                                {
                                    try { fr = CloudFrames(e.Id); } catch (Exception) { continue; }
                                    downloads[e.Id] = fr;
                                }
                            // a duplicate looks the same as one already shown, or (except in search results and the
                            // account's own lists) has the same name as another on this page
                            string sig = Signature(fr), name = GalleryNames.NoYears(names[i]), nameKey = src.KeepNames || name.Length == 0 ? "" : NameKey(name);
                            bool dup;
                            lock (seen) dup = seen.Contains(sig);
                            if (dup || !mine.Add(sig) || (nameKey.Length > 0 && !mine.Add(nameKey))) { dupes++; continue; }
                            var it = new GalleryItem { Category = "Cloud", Name = name.Length > 0 ? name : "Untitled" };
                            if (translated && !savedName[i]) DesignCache.SetName(DesignCache.KeyOf(e.Id), name);   // the English name, not the original ("" = untitled)
                            if (e.Id.StartsWith(DivoomCloud16.Prefix)) it.Ms = DivoomCloud16.MsOf(e.Id.Substring(DivoomCloud16.Prefix.Length));
                            if (e.Id.StartsWith(DivoomCloud16.Prefix128))
                            {   // the file as Divoom made it goes along, for a Tiivoo 2 (and for saving)
                                string fid = e.Id.Substring(DivoomCloud16.Prefix128.Length);
                                it.Ms = DivoomCloud16.MsOf(fid);
                                it.Raw = DivoomCloud16.File(DivoomCloud16.Prefix128, fid);
                            }
                            foreach (var f in Distinct(fr)) it.Frames.Add(f);
                            it.Key = DesignCache.KeyOf(e.Id);
                            Renamed(it);
                            UpdateYear(it, e.Name + " " + name);
                            kept.Add(it);
                            BeginInvoke((Action)delegate { if (gen == generation) addItem(it); });
                        }
                        if (gen != generation) return;
                        // only a fully loaded page counts toward "already seen", so a cancelled load never hides anything
                        if (!src.Mine) lock (seen) foreach (var sig in mine) if (!sig.StartsWith("name:")) seen.Add(sig);
                        string kind = src.Kind == "lametric" ? "8×8 icons, centered" : src.Kind == "classic" ? "11×11 art cropped to 10×10" :
                                      src.Kind == "d128" ? "128×128 art" + (P.Box is Tiivoo2 ? "" : " (shrunk to fit this display)") :
                                      "16×16 art" + (P.Box.Size == 16 ? "" : " (squeezed to 10×10 on an AuraBox)");
                        string note = kept.Count + " designs" + (dupes > 0 ? ", " + dupes + " duplicates hidden" : "") + "  ·  " + kind;
                        BeginInvoke((Action)delegate { pageCache[key] = kept; pageNotes[key] = note; });
                        string trouble = translate ? GalleryNames.Problem : null;
                        if (trouble != null && !toldTranslate) { toldTranslate = true; SetStatus(trouble); }
                        else SetStatus(note);
                        if (src.Kind != "lametric" || list.Count > 0) Prefetch(src, page + 1, () => gen == generation);
                    }
                    catch (Exception ex) { SetStatus(title.Text + " unavailable: " + ex.Message); }
                });
            };
            nav.SelectedIndexChanged += delegate
            {
                if (navQuiet) return;
                var s = nav.SelectedItem as GallerySource;
                if (s == null) return;
                if (s.Kind == "header") { nav.SelectedIndex = Math.Min(nav.Items.Count - 1, nav.SelectedIndex + 1); return; }   // skip to the first list under it
                if (s.Kind == "newlist")
                {
                    string made = newList();
                    if (made == null) { navQuiet = true; nav.SelectedItem = cur; navQuiet = false; return; }
                    selectList(made);
                    return;
                }
                if (s == cur) return;
                cur = s; cloudPage = 0;
                if (search.Text.Length > 0) { searching = true; search.Text = ""; searching = false; }
                load();
            };
            var navMenu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new MenuRenderer() };
            navMenu.Opening += delegate (object o, System.ComponentModel.CancelEventArgs e)
            {
                navMenu.Items.Clear();
                int k = nav.IndexFromPoint(nav.PointToClient(Cursor.Position));
                var s = k >= 0 ? nav.Items[k] as GallerySource : null;
                if (s == null || s.Kind != "mylist") { e.Cancel = true; return; }
                string n = s.Query;
                navMenu.Items.Add("Rename List…", null, delegate
                {
                    string to = AskText("Rename List", "New name for “" + n + "”:", n);
                    if (to == null) return;
                    to = to.Trim().Replace("|", "/");
                    if (to.Length == 0 || to == n) return;
                    if (myLists.Names.Contains(to)) { status.Text = "There is already a list called “" + to + "”"; return; }
                    myLists.Names[myLists.Names.IndexOf(n)] = to;
                    foreach (var it in myLists.Items) if (it.Category == n) it.Category = to;
                    try { myLists.Save(); } catch (Exception ex) { status.Text = "Could not save the list: " + ex.Message; return; }
                    bool showing = onList() && cur.Query == n;
                    refreshLists();
                    if (showing) { cur = null; selectList(to); }
                    status.Text = "Renamed the list “" + n + "” to “" + to + "”";
                });
                navMenu.Items.Add("Delete List", null, delegate
                {
                    int count = myLists.In(n).Count;
                    if (MessageBox.Show(this, "Delete the list “" + n + "”" + (count > 0 ? " and the " + count + " design" + (count == 1 ? "" : "s") + " in it" : "") +
                                        "?\r\n\r\nDesigns you also saved to Favorites or other lists stay there.", "Delete List",
                                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    myLists.Names.Remove(n);
                    myLists.Items.RemoveAll(it => it.Category == n);
                    try { myLists.Save(); } catch (Exception ex) { status.Text = "Could not save the lists: " + ex.Message; return; }
                    bool showing = onList() && cur.Query == n;
                    refreshLists();
                    if (showing) { cur = null; nav.SelectedItem = sources.Find(x => x.Kind == "fav"); }
                    status.Text = "Deleted the list “" + n + "”";
                });
            };
            nav.ContextMenuStrip = navMenu;
            // Enter: search all of Divoom's 16 x 16 art; the results appear as a list at the top
            search.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string words = search.Text.Trim();
                if (words.Length == 0) return;
                if (searchSource == null)
                {
                    nav.Items.Insert(0, new GallerySource { Title = "SEARCH", Kind = "header" });
                    searchSource = new GallerySource { Kind = "search" };
                    nav.Items.Insert(1, searchSource);
                }
                searchSource.Query = words; searchSource.Title = "“" + words + "”";
                nav.Invalidate();
                searching = true; search.Text = ""; searching = false;
                cur = null; cloudPage = 0;
                nav.SelectedIndex = 1;
                if (cur != searchSource) { cur = searchSource; load(); }
            };
            english.CheckedChanged += delegate
            {
                cfg.TranslateNames = english.Checked; SaveSettings();
                pageCache.Clear(); load();
            };
            animatedOnly.CheckedChanged += delegate
            {   // shows the list again with or without the still pictures (nothing to download)
                cfg.GalleryAnimatedOnly = animatedOnly.Checked; SaveSettings();
                view.BeginUpdate();
                view.Items.Clear(); images.Images.Clear(); shown.Clear();
                foreach (var it in pageItems) if (matches(it)) display(it);
                view.EndUpdate();
                status.Text = animatedOnly.Checked ? shown.Count + " of " + pageItems.Count + " designs here are animated (still pictures hidden)"
                                                   : "Showing still pictures too (" + pageItems.Count + " designs)";
            };
            // the page size applies once the slider rests (dragging past 60 doesn't load 70, 80 and 90 on the way); the
            // page shown then is the one holding the first design that was on screen
            var perWait = new System.Windows.Forms.Timer { Interval = 500 };
            perPage.ValueChanged += delegate { perLabel.Text = perPage.Value * 10 + " per page"; perWait.Stop(); perWait.Start(); };
            perWait.Tick += delegate
            {
                perWait.Stop();
                int old = DivoomCloud.PageSize, size = perPage.Value * 10;
                if (size == old) return;
                DivoomCloud.PageSize = size; cfg.GalleryPageSize = size; SaveSettings();
                if (cur == null || !cur.Cloud) return;
                cloudPage = cloudPage * old / size;
                load();
            };
            prev.Click += delegate { if (cloudPage > 0) { cloudPage--; load(); } };
            next.Click += delegate { cloudPage++; load(); };
            accountChanged += delegate { if (cur != null && (cur.Kind == "likes" || cur.Kind == "uploads")) { pageCache.Clear(); load(); } };

            // animate the thumbnails: every animated one in view, or with animations paused just the selected one. Calm
            // on purpose (many Divoom designs blink 11 to 20 times a second): each frame stays up at least a tenth of a
            // second, the frames keep a steady beat from a clock (not the timer's uneven ticks), and a 16 x 16 design
            // fades through a half-and-half picture into its next frame instead of snapping. The list draws off-screen
            // first, so redrawing a thumbnail doesn't flicker. Only the app's thumbnails: displays get designs as made.
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(view, true, null);
            var frameAt = new Dictionary<GalleryItem, int>();      // what each thumbnail shows: frame * 2, + 1 while fading
            var startAt = new Dictionary<GalleryItem, double>();   // when it started playing (clock ms)
            var clock = System.Diagnostics.Stopwatch.StartNew();
            const double FadeFrom = 0.6;   // the last 40% of each frame's time is the fade
            preview.Interval = 25;
            preview.Tick += delegate
            {
                if (!p.Visible || shown.Count == 0 || view.Items.Count != shown.Count) return;
                double now = clock.Elapsed.TotalMilliseconds;
                var area = view.ClientRectangle;
                int sel = view.SelectedIndices.Count > 0 ? view.SelectedIndices[0] : -1;
                for (int i = 0; i < shown.Count; i++)
                {
                    var it = shown[i];
                    int n = it.Frames.Count;
                    if (n < 2 || (!animateAll && i != sel)) continue;
                    var r = view.GetItemRect(i, ItemBoundsPortion.Icon);
                    if (!r.IntersectsWith(area)) continue;   // scrolled out of view
                    double began;
                    if (!startAt.TryGetValue(it, out began)) startAt[it] = began = now;
                    double beat = (now - began) / Math.Max(100, it.Ms > 0 ? it.Ms : 250);
                    int f = (int)beat % n;
                    bool fading = it.Frames[f].Length == 256 && beat - Math.Floor(beat) >= FadeFrom;   // RGB designs only
                    int at = f * 2 + (fading ? 1 : 0), was;
                    if (frameAt.TryGetValue(it, out was) && was == at) continue;   // already showing it
                    frameAt[it] = at;
                    var px = fading ? Mix(it.Frames[f], it.Frames[(f + 1) % n]) : it.Frames[f];
                    images.Images[i] = Proto.Render(px, Math.Max(1, thumb / 10));   // the list holds on to the picture: not disposed here
                    view.Invalidate(r);
                }
            };
            animBtn.Click += delegate
            {
                animateAll = !animateAll; cfg.GalleryAnimate = animateAll; SaveSettings();
                animBtn.Text = animateAll ? "❚❚  Pause" : "▶  Play";
                animBtn.AccessibleName = animateAll ? "Pause animations" : "Play animations";
                if (!animateAll)   // back to each design's first frame, except the selected one
                    for (int i = 0; i < shown.Count; i++)
                        if (shown[i].Frames.Count > 1 && !view.Items[i].Selected) { images.Images[i] = Proto.Render(shown[i].Frames[BestFrame(shown[i])], Math.Max(1, thumb / 10)); frameAt.Remove(shown[i]); startAt.Remove(shown[i]); }
                view.Invalidate();
            };
            preview.Start();

            Action send = delegate
            {
                var it = one();
                if (it == null) { status.Text = "Pick a design first"; return; }
                StopRotationForManual();
                var t2 = box as Tiivoo2;
                if (t2 != null && it.Raw != null)   // a 128 x 128 design to a Tiivoo 2: its own file, every frame
                {
                    var raw = it.Raw; var look = Proto.ToRgb16(it.Frames[0]);
                    Run(delegate { t2.ShowDivoom128(raw, look); }, "Sent “" + it.Name + "” (128×128, " + it.Frames.Count + " frame" + (it.Frames.Count == 1 ? "" : "s") + ")");
                    return;
                }
                var tv = box as Tivoo;
                if (it.IsAnimation && tv != null)   // the Tivoo plays long animations by itself, at the design's own speed
                {
                    var fr = Sample(it.Frames, Tivoo.MaxFrames);
                    int ms = it.Ms > 0 ? it.Ms : (int)delayBox.Value * 100;
                    Run(delegate { tv.ShowAnimationMs(fr, ms); }, "Sent “" + it.Name + "” (" + fr.Count + " frames)");
                }
                else if (it.IsAnimation) SendAnimation(Sample(it.Frames));
                else { var px = it.Frames[0]; Run(delegate { box.ShowImage(px); }, "Sent “" + it.Name + "”"); }
            };
            view.ItemActivate += delegate { send(); };

            gallerySend = Btn("Send to " + P.Name, 0, 462, 176, send, true);
            gallerySend.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            p.Controls.Add(gallerySend);
            Button editBtn;
            p.Controls.Add(editBtn = Btn("Edit", 186, 462, 90, delegate
            {
                var it = one();
                if (it == null) return;
                grid.SetAll(Proto.To10(it.Frames[0]));   // drawing is 10 x 10: 16 x 16 designs are squeezed
                if (it.IsAnimation) { frames.Clear(); foreach (var f in Sample(it.Frames)) frames.Add(Proto.To10(f)); RefreshFrames(); }
                status.Text = "Loaded “" + it.Name + "” into Draw & Animate" + (it.IsAnimation ? " (picture and frames)" : "");
            }));
            editBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var playAll = Btn("Play All Frames From PC", 284, 462, 200, null);
            playAll.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            playAll.Click += delegate
            {
                if (job != null && playingBtn == playAll) { TogglePlay(playAll, null, "Animation"); return; }
                var it = one();
                if (it == null) { status.Text = "Pick an animation first"; return; }
                if (!it.IsAnimation) { status.Text = "“" + it.Name + "” is a still picture"; return; }
                var all = new List<int[]>(it.Frames);
                int n = 0;
                TogglePlay(playAll, delegate { StreamLoop(delegate { return all[n++ % all.Count]; }, () => FrameSeconds(Settings.DefaultSpeed), DateTime.MaxValue); },
                           "“" + it.Name + "” (" + all.Count + " frames)");
            };
            p.Controls.Add(playAll);
            var tip = Note("Double-click sends to the display (AuraBox: 12 frames, Tivoo: 60). Right-click: rename, favorites, lists. Ctrl- or Shift-click picks several.", 2, 499);
            tip.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            p.Controls.Add(tip);
            // start where the art fits the display: Divoom 16 x 16 on a Tivoo, the built-in designs on an AuraBox
            Func<GallerySource> start = () => sources.Find(s => P.Box is Tiivoo2 ? s.Kind == "d128" : P.Box.Size == 16 ? s.Kind == "d16" : s.Kind == "builtin") ?? sources.Find(s => s.Kind == "fav" && s.Arg == P.Box.Size);
            p.VisibleChanged += delegate { if (p.Visible && cur == null) nav.SelectedItem = start(); };   // first opened
            return p;
        }

        // lists the person makes and names (under Mine in the Gallery): settings\lists.txt holds an "L|name" line for each
        // list, in order, then each design as a favorites-style line whose category is its list's name
        class MyLists
        {
            public List<string> Names = new List<string>();
            public List<GalleryItem> Items = new List<GalleryItem>();
            static string PathOf { get { return Settings.DataFile("lists.txt"); } }

            public List<GalleryItem> In(string name) { return Items.FindAll(i => i.Category == name); }

            public static MyLists Load()
            {
                var m = new MyLists();
                try
                {
                    if (!File.Exists(PathOf)) return m;
                    foreach (string line in File.ReadAllLines(PathOf, Encoding.UTF8))
                    {
                        if (line.StartsWith("L|")) { string n = line.Substring(2); if (n.Length > 0 && !m.Names.Contains(n)) m.Names.Add(n); continue; }
                        var it = GalleryItem.Parse(line);
                        if (it == null) continue;
                        if (!m.Names.Contains(it.Category)) m.Names.Add(it.Category);
                        m.Items.Add(it);
                    }
                }
                catch (Exception) { }
                return m;
            }

            public void Save()
            {
                var lines = new List<string>();
                foreach (var n in Names) lines.Add("L|" + n);
                foreach (var it in Items) lines.Add(it.Format(it.Category));
                Directory.CreateDirectory(Path.GetDirectoryName(PathOf));
                File.WriteAllLines(PathOf, lines.ToArray(), Encoding.UTF8);
            }
        }

        // asks for a line of text (a list's name); null when cancelled
        string AskText(string title, string label, string initial)
        {
            using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                      StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(380, 120), Font = Font, ShowInTaskbar = false })
            {
                f.Controls.Add(new Label { Text = label, Location = new Point(14, 14), Size = new Size(352, 20), UseMnemonic = false, AutoEllipsis = true });
                var box = new TextBox { Location = new Point(14, 40), Width = 352, Text = initial ?? "" };
                f.Controls.Add(box);
                var ok = new Button { Text = "OK", Location = new Point(186, 78), Size = new Size(86, 28), DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Location = new Point(280, 78), Size = new Size(86, 28), DialogResult = DialogResult.Cancel };
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                Theme.Prepare(f);
                return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
            }
        }

        // the window's Divoom account changed (signed in or out): the Gallery reloads My Likes / My Uploads
        Action accountChanged = delegate { };

        // sign in to the Divoom app account (for My Likes and My Uploads), or choose a password for one made with an
        // emailed code
        void DivoomAccountDialog()
        {
            using (var f = new Form { Text = "Divoom Account", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                      StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(440, 330), Font = Font, ShowInTaskbar = false })
            {
                var info = new Label { Location = new Point(14, 12), Size = new Size(412, 36), UseMnemonic = false };
                var email = new TextBox { Location = new Point(110, 56), Width = 316, Text = DivoomAccount.Email.Length > 0 ? DivoomAccount.Email : cfg.DivoomEmail };
                var pass = new TextBox { Location = new Point(110, 88), Width = 200, UseSystemPasswordChar = true };
                var signIn = new Button { Text = "Sign In", Location = new Point(318, 86), Size = new Size(108, 28) };
                var signOut = new Button { Text = "Sign Out", Location = new Point(318, 86), Size = new Size(108, 28) };
                var codeNote = new Label { Location = new Point(14, 136), Size = new Size(412, 50), Tag = "subtle", UseMnemonic = false,
                    Text = "No password? Accounts made with an emailed code have none. Divoom can email you a code to choose one " +
                           "(the same as \"Forgot password\" in the Divoom app)." };
                var sendCode = new Button { Text = "Email Me a Code", Location = new Point(14, 190), Size = new Size(150, 28) };
                var code = new TextBox { Location = new Point(110, 230), Width = 100 };
                var newPass = new TextBox { Location = new Point(110, 262), Width = 200, UseSystemPasswordChar = true };
                var setPass = new Button { Text = "Set Password and Sign In", Location = new Point(14, 294), Size = new Size(200, 28) };
                var close = new Button { Text = "Close", Location = new Point(346, 294), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
                f.CancelButton = close;
                foreach (Control c in new Control[] { info, email, pass, signIn, signOut, codeNote, sendCode, code, newPass, setPass, close,
                    new Label { Text = "Email:", Location = new Point(14, 59), AutoSize = true }, new Label { Text = "Password:", Location = new Point(14, 91), AutoSize = true },
                    new Label { Text = "Code:", Location = new Point(14, 233), AutoSize = true }, new Label { Text = "New password:", Location = new Point(14, 265), AutoSize = true } })
                    f.Controls.Add(c);
                Action refresh = delegate
                {
                    bool on = DivoomAccount.SignedIn;
                    info.Text = on ? "Signed in as " + DivoomAccount.Email + ". The Gallery's My Divoom Likes and My Divoom Uploads use this account."
                                   : "Sign in to the account you use in the Divoom phone app to see your likes and uploads in the Gallery. Your password is not saved.";
                    signIn.Visible = !on; signOut.Visible = on; pass.Enabled = !on; email.Enabled = !on;
                    foreach (Control c in new Control[] { codeNote, sendCode, code, newPass, setPass }) c.Enabled = !on;
                };
                Action<Action, string> busy = delegate (Action work, string done)
                {
                    f.UseWaitCursor = true; f.Enabled = false;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string err = null;
                        try { work(); } catch (Exception ex) { err = ex.Message; }
                        try
                        {
                            f.BeginInvoke((Action)delegate
                            {
                                f.UseWaitCursor = false; f.Enabled = true;
                                if (err != null) { MessageBox.Show(f, err, "Divoom Account", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                                if (DivoomAccount.SignedIn)
                                {
                                    cfg.DivoomEmail = DivoomAccount.Email; cfg.DivoomUserId = DivoomAccount.UserId; cfg.DivoomToken = DivoomAccount.Token;
                                    SaveSettings(); accountChanged();
                                }
                                refresh();
                                if (done != null) status.Text = done;
                            });
                        }
                        catch (InvalidOperationException) { }   // the dialog was closed meanwhile
                    });
                };
                signIn.Click += delegate
                {
                    string em = email.Text.Trim(), pw = pass.Text;
                    if (em.Length == 0 || pw.Length == 0) { MessageBox.Show(f, "Type your email and password.", "Divoom Account"); return; }
                    busy(delegate { DivoomAccount.SignIn(em, pw); }, "Signed in to Divoom as " + em);
                };
                signOut.Click += delegate
                {
                    DivoomAccount.SignOut(); cfg.DivoomUserId = 0; cfg.DivoomToken = 0; SaveSettings(); accountChanged(); refresh();
                    status.Text = "Signed out of Divoom";
                };
                sendCode.Click += delegate
                {
                    string em = email.Text.Trim();
                    if (em.Length == 0) { MessageBox.Show(f, "Type your email first.", "Divoom Account"); return; }
                    busy(delegate { DivoomAccount.SendCode(em); }, "Divoom emailed a code to " + em);
                };
                setPass.Click += delegate
                {
                    string em = email.Text.Trim(), pw = newPass.Text; int c;
                    if (em.Length == 0 || !int.TryParse(code.Text.Trim(), out c) || pw.Length < 6)
                    { MessageBox.Show(f, "Type your email, the code from the email, and a new password of at least 6 characters.", "Divoom Account"); return; }
                    busy(delegate { DivoomAccount.SetPassword(em, c, pw); DivoomAccount.SignIn(em, pw); }, "Password set and signed in to Divoom");
                };
                refresh();
                Theme.Prepare(f);
                f.ShowDialog(this);
            }
        }

        // the frame a still thumbnail shows: the one with the most lit pixels (many animations start dark or blank)
        static int BestFrame(GalleryItem it)
        {
            int best = 0, most = -1;
            for (int f = 0; f < it.Frames.Count; f++)
            {
                int lit = 0;
                foreach (int c in it.Frames[f]) if (c != 0) lit++;
                if (lit > most) { most = lit; best = f; }
            }
            return best;
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

        // a year drawn in a downloaded design ("Happy New Year 2021") brought up to date when it can be read (names: its
        // original and English names)
        static void UpdateYear(GalleryItem it, string names)
        {
            int year = YearArt.YearIn(names);
            if (year == 0 && YearArt.IsNewYear(names)) year = YearArt.YearDrawn(it.Frames);   // "New Year" showing 2020
            if (year == 0) return;
            var newer = YearArt.Update(it.Frames, year, YearArt.TargetFor(names));
            if (newer != null) it.Frames = newer;
        }

        // the next page of a Divoom list downloaded in the background (into the design cache) while the person looks at
        // this one, so Next opens at once; stops as soon as another list or page is opened
        static void Prefetch(GallerySource src, int page, Func<bool> still)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(1500);   // let the page on screen settle first
                    if (!still()) return;
                    int total;
                    var list = CloudList(src, page, out total);
                    foreach (var e in list)
                    {
                        if (!still()) return;
                        try { CloudFrames(e.Id); } catch (Exception) { }
                        Thread.Sleep(150);   // gently: Divoom refuses bursts
                    }
                }
                catch (Exception) { }
            });
        }

        // names given to designs outside Favorites (built-in and downloaded ones), by look: settings\renames.txt holds
        // "fingerprint<TAB>name" lines, the fingerprint being an MD5 of the design's Signature
        static Dictionary<string, string> renames;
        static readonly object renamesGate = new object();

        static string LookOf(GalleryItem it)
        {
            if (it.Look == null)
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    it.Look = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(Signature(it.Frames)))).Replace("-", "").ToLowerInvariant();
            return it.Look;
        }

        static void LoadRenames()
        {
            if (renames != null) return;
            renames = new Dictionary<string, string>();
            try
            {
                string path = Settings.DataFile("renames.txt");
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) renames[line.Substring(0, tab)] = line.Substring(tab + 1);
                    }
            }
            catch (Exception) { }
        }

        // gives the design the name it was renamed to, if any
        static void Renamed(GalleryItem it)
        {
            if (it.Frames.Count == 0) return;
            string look = LookOf(it), name;
            lock (renamesGate) { LoadRenames(); if (renames.TryGetValue(look, out name)) it.Name = name; }
        }

        static void SaveRename(GalleryItem it, string name)
        {
            string look = LookOf(it);
            lock (renamesGate)
            {
                LoadRenames();
                renames[look] = name.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
                var lines = new List<string>();
                foreach (var kv in renames) lines.Add(kv.Key + "\t" + kv.Value);
                string path = Settings.DataFile("renames.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
            }
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

        // the AuraBox holds 12 frames (the Tivoo 60): longer animations are sampled evenly
        static List<int[]> Sample(List<int[]> all, int max = AuraBox.MaxFrames)
        {
            if (all.Count <= max) return all;
            var list = new List<int[]>();
            for (int k = 0; k < max; k++) list.Add(all[k * all.Count / max]);
            return list;
        }

        // two 16 x 16 RGB pictures mixed half and half (the Gallery's thumbnails fade through it between frames)
        static int[] Mix(int[] a, int[] b)
        {
            var m = new int[a.Length];
            for (int j = 0; j < a.Length; j++)
            {
                int p = a[j], q = b[j];
                m[j] = ((p >> 16 & 255) + (q >> 16 & 255) + 1) / 2 << 16 | ((p >> 8 & 255) + (q >> 8 & 255) + 1) / 2 << 8 | ((p & 255) + (q & 255) + 1) / 2;
            }
            return m;
        }

        Button gallerySend;

        // ---------- live animations: generated on this PC frame by frame, each with its own speed ----------
        Panel BuildLivePage()
        {
            var p = NewPage();
            var live = NewCard("Played From This PC", 0, 0, W, 250);
            Func<int[]> sim = null;
            var pick = new ListBox { Location = new Point(12, 40), Size = new Size(190, 196), IntegralHeight = false };
            AccentList(pick);
            foreach (var la in LiveAnims) pick.Items.Add(la.Name);
            live.Controls.Add(pick);
            var title = new Label { Location = new Point(218, 38), Size = new Size(W - 218 - 180, 26), Font = Theme.Semibold(12f), UseMnemonic = false };
            var about = new Label { Location = new Point(218, 68), Size = new Size(W - 218 - 180, 60) };
            livePlay = Btn("Play", 218, 126, 120, null, true);
            var speedLabel = new Label { Text = "Speed:", Location = new Point(218, 174), AutoSize = true };
            live.Controls.Add(speedLabel);
            var speed = new TrackBar { Minimum = 1, Maximum = 10, Location = new Point(270, 166), Width = W - 270 - 186, TickStyle = TickStyle.None };
            var preview = new PixelGrid(15) { Location = new Point(W - 166, 40), ReadOnly = true };
            live.Controls.Add(title); live.Controls.Add(about); live.Controls.Add(livePlay); live.Controls.Add(speed); live.Controls.Add(preview);
            live.Controls.Add(Note("Speed is saved for each one, and also used in the playlist.", 218, 220));
            var speedNote = (Label)live.Controls[live.Controls.Count - 1];
            // the Countdown Timer's options, shown only while it is picked: a number of minutes, or a date and time
            var forMinutes = new RadioButton { Text = "Minutes:", Location = new Point(352, 132), AutoSize = true, Checked = !cfg.CountdownToDate };
            var minutes = new NumericUpDown { Minimum = 1, Maximum = 99, Value = cfg.CountdownMinutes, Location = new Point(446, 130), Width = 60 };
            var toDate = new RadioButton { Text = "Until:", Location = new Point(218, 214), AutoSize = true, Checked = cfg.CountdownToDate };
            var until = new DateTimePicker { Format = DateTimePickerFormat.Custom, Location = new Point(290, 210), Width = 230,
                                             CustomFormat = H24 ? "MMM d, yyyy  HH:mm" : "MMM d, yyyy  h:mm tt", Value = cfg.CountdownUntil };
            var countdownControls = new Control[] { forMinutes, minutes, toDate, until };
            Action countdownChanged = delegate
            {
                cfg.CountdownMinutes = Countdown.Minutes = (int)minutes.Value;
                cfg.CountdownToDate = toDate.Checked;
                cfg.CountdownUntil = until.Value.Date.AddHours(until.Value.Hour).AddMinutes(until.Value.Minute);
                Countdown.Until = cfg.CountdownToDate ? cfg.CountdownUntil : DateTime.MinValue;
                // the playlist gives a timer its whole run and a little of the finish; a date just a minute's look
                lock (playlistGate)
                {
                    var item = PlayItem.Find(dcfg.Playlist, "countdown");
                    if (item != null) item.Seconds = cfg.CountdownToDate ? 60 : cfg.CountdownMinutes * 60 + 10;
                }
                RefreshPlayRows();
                SaveSettings(); sim = null;
            };
            minutes.ValueChanged += delegate { if (!forMinutes.Checked) forMinutes.Checked = true; else countdownChanged(); };
            until.ValueChanged += delegate { if (!toDate.Checked) toDate.Checked = true; else countdownChanged(); };
            forMinutes.CheckedChanged += delegate { if (forMinutes.Checked) countdownChanged(); };
            toDate.CheckedChanged += delegate { if (toDate.Checked) countdownChanged(); };
            live.Controls.AddRange(countdownControls);
            p.Controls.Add(live);

            bool picking = false;
            // the Heart Monitor's title shows the heart rate its slider sets
            Action showRate = delegate
            {
                if (pick.SelectedIndex < 0) return;
                var la = LiveAnims[pick.SelectedIndex];
                title.Text = la.Id == "heart" ? la.Name + "  ·  " + HeartMonitor.Bpm(speed.Value) + " beats a minute" : la.Name;
            };
            pick.SelectedIndexChanged += delegate
            {
                if (pick.SelectedIndex < 0) return;
                var la = LiveAnims[pick.SelectedIndex];
                title.Text = la.Name; about.Text = la.About; sim = null;
                foreach (var c in countdownControls) c.Visible = la.Id == "countdown";
                speedNote.Visible = la.Id != "countdown";
                speedLabel.Text = la.Id == "heart" ? "Rate:" : "Speed:";
                picking = true; speed.Value = cfg.SpeedOf(la.Id); picking = false;
                showRate();
            };
            speed.ValueChanged += delegate
            {
                if (picking || pick.SelectedIndex < 0) return;
                string id = LiveAnims[pick.SelectedIndex].Id;
                cfg.SetSpeed(id, speed.Value); SaveSettings(); speedChanged(id); showRate();
            };
            // set on the Playlist page: move this slider too if it shows that animation
            speedChanged += delegate (string changed)
            {
                if (pick.SelectedIndex < 0 || LiveAnims[pick.SelectedIndex].Id != changed || speed.Value == cfg.SpeedOf(changed)) return;
                picking = true; speed.Value = cfg.SpeedOf(changed); picking = false;
            };
            pick.SelectedIndex = 0;
            livePlay.Click += delegate
            {
                var la = LiveAnims[pick.SelectedIndex];
                TogglePlay(livePlay, delegate { PlayLiveAnim(la, DateTime.MaxValue); }, la.Name);
            };

            // preview the picked animation at its speed while this page is open
            var previewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            DateTime nextFrame = DateTime.MinValue;
            previewTimer.Tick += delegate
            {
                if (!p.Visible) { sim = null; return; }
                if (DateTime.Now < nextFrame || pick.SelectedIndex < 0) return;
                if (sim == null) sim = MakeFor(LiveAnims[pick.SelectedIndex], sel.Box.Size);
                preview.SetAll(sim());
                nextFrame = DateTime.Now.AddSeconds(SizePace(sel.Box.Size) * Uneven(LiveAnims[pick.SelectedIndex].Id, LiveFrameSeconds(LiveAnims[pick.SelectedIndex].Id, speed.Value)));
            };
            previewTimer.Start();
            deviceChanged += delegate { sim = null; };   // another display: preview its version

            p.Controls.Add(BuildLavaCard(0, 262));
            return p;
        }

        // frame time for a live animation, read on every frame so a speed change applies right away
        Func<double> LiveInterval(string id) { double k = SizePace(box.Size); return () => k * Uneven(id, LiveFrameSeconds(id, cfg.SpeedOf(id))); }

        // frame time for a display: things move a pixel a frame, and the Tivoo has 16 pixels across to the AuraBox's 10,
        // so its frames come 10/16 as far apart to cross the screen in the same time
        static double SizePace(int size) { return size == 16 ? 10.0 / 16 : 1; }

        // the Heart Monitor's slider sets its heart rate instead, so it always sends frames as fast as it can
        static double LiveFrameSeconds(string id, int v) { return FrameSeconds(id == "heart" ? 10 : v); }

        // seconds per live-animation frame for speed slider value v (1..10)
        static double FrameSeconds(int v) { return (0.42 - v * 0.035) * 0.8; }

        // each frame of a live animation waits a little more or less than the set time (half to one and a half times,
        // the same on average), so motion has the uneven, lively pace of a hospital scope trace
        static readonly Random frameRng = new Random();
        static double Uneven(double seconds) { lock (frameRng) return seconds * (0.5 + frameRng.NextDouble()); }

        // the aquarium, the Mandelbrot zoom and the holiday countdown's scrolling words keep a steady pace, so they glide instead of lurching
        static double Uneven(string id, double seconds) { return id == "aquarium" || id == "jellyfish" || id == "mandelbrot" || id == "nextholiday" ? seconds : Uneven(seconds); }

        void SendAnimation(List<int[]> list)
        {
            var copy = new List<int[]>(list); int delay = (int)delayBox.Value;
            Run(delegate { box.ShowAnimation(copy, delay); }, "Animation sent (" + Math.Min(AuraBox.MaxFrames, copy.Count) + " frames)");
        }

        // redraw the frame strip; the picked frame gets an accent-colored rim
        void RefreshFrames()
        {
            if (selFrame >= frames.Count) selFrame = frames.Count - 1;
            for (int k = 0; k < frameSlots.Length; k++)
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
        // a live animation on the current display until a time: frame by frame, or on a Tiivoo 2 as whole loops it plays
        // by itself (Loop128) when the animation has them
        void PlayLiveAnim(LiveAnim la, DateTime until)
        {
            var t2 = box as Tiivoo2;
            if (t2 != null && la.Make128 != null)
            {
                // the aquarium bubbles: the Tiivoo 2's own "Mountain Stream" sound, low, for as long as it plays. The
                // order when switching: stop the sound, stop the animation, start the new one, then start its sound,
                // so the bubbling starts once the aquarium is on screen
                bool bubbling = la.Id == "aquarium" || la.Id == "aquaclock";
                var quiet = new int[8]; quiet[5] = 15;
                Action shown = bubbling ? (Action)delegate { JobSend(delegate { t2.WhiteNoise(quiet, 0, true); }); } : null;
                try
                {
                    if (la.Id == "aquaclock") AquariumClock(t2, until, shown);
                    else LoopPlay(t2, la.Make128(), until, shown);
                }
                finally
                {   // sent even when the playlist is stopping (JobSend skips sending then)
                    if (bubbling && t2.Connected) try { t2.WhiteNoise(quiet, 0, false); } catch (Exception) { }
                }
            }
            else StreamLoop(MakeFor(la, box.Size), LiveInterval(la.Id), until);
        }

        // the aquarium as a Tiivoo 2 clock face of our own (Tiivoo2.ShowLocalClock): one loop, sent once, with the time
        // centered along the bottom over the gravel (AquariumClockItems). Its sound starts once the picture is across
        // (about a minute and a half; at once when the Tiivoo 2 still has it)
        public const int AquariumClockId = 9900001;
        // The time as four digit pictures (kinds 59-62: hour tens, hour ones, minute tens, minute ones, as the store's Flip
        // Clock has them), drawn here in Divoom font 366 (#40 on the sample sheet, 9 x 12, the user's pick): white with
        // its outline (red in the font file) grey. The hour's tens has a blank "0", so 1:47 shows as "1:47", not "01:47"
        // (the hour item, kind 3, always writes two digits), and each "1" sits in the middle of its place. The colon
        // (outlined the same) is part of the picture.
        public static string[] AquariumClockItems(string tens, string digits)
        {
            Func<int, int, string, string> item = (x, kind, file) => x + ",112,9,12," + kind + ",4,1,2,FFFFFF,FF0000,0," + file;
            return new[] { item(60, 59, tens), item(69, 60, digits), item(83, 61, digits), item(92, 62, digits) };
        }

        // the digit pictures by name: [0] the hour's tens (blank 0), [1] all ten; names change with the pictures (the
        // device keeps files by name). Made once from the font and kept in settings\cache
        public static Dictionary<string, byte[]> AquariumClockDigits(out string tens, out string digits)
        {
            string dir = Settings.DataFile("cache");
            string pt = System.IO.Path.Combine(dir, "aquarium_clock_digits_366_tens.bin"), pa = System.IO.Path.Combine(dir, "aquarium_clock_digits_366.bin");
            byte[] ft = null, fa = null;
            try { if (System.IO.File.Exists(pt) && System.IO.File.Exists(pa)) { ft = System.IO.File.ReadAllBytes(pt); fa = System.IO.File.ReadAllBytes(pa); } } catch (Exception) { }
            if (ft == null || fa == null)
            {
                string info = ClockStore.Post("Device/GetSomeFontInfo", "\"FontIds\":[{\"id\":366}]");
                var m = System.Text.RegularExpressions.Regex.Match(info, "\"366,\\d+,(\\d+),(\\d+),([^,\"]+),([^\"]*)\"");
                if (!m.Success) throw new Exception("Divoom didn't describe font 366");
                int gw = int.Parse(m.Groups[1].Value), gh = int.Parse(m.Groups[2].Value); string chars = m.Groups[4].Value;
                int fw, fms; var glyphs = DivoomFiles.Decode(ClockStore.File(m.Groups[3].Value), out fw, out fms);
                if (glyphs == null) throw new Exception("Font 366's picture couldn't be read");
                var all = new List<int[]>();
                for (int dgt = 0; dgt <= 9; dgt++)
                {
                    var g = glyphs[chars.IndexOf((char)('0' + dgt))]; int l = gw, r = -1;
                    for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++) if ((g[y * fw + x] & 0xFFFFFF) != 0) { l = Math.Min(l, x); r = Math.Max(r, x); }
                    int shift = r < 0 ? 0 : (gw - (r - l + 1)) / 2 - l;   // its ink in the middle of its place
                    var px = new int[64 * 64];
                    for (int y = 0; y < gh; y++)
                        for (int x = 0; x < gw; x++)
                        {
                            int c = g[y * fw + x] & 0xFFFFFF, tx = x + shift; if (c == 0 || tx < 0 || tx >= 64) continue;
                            px[y * 64 + tx] = c == 0xFF0000 ? 0x707070 : c;   // the outline grey
                        }
                    all.Add(px);
                }
                var tensFrames = new List<int[]>(all); tensFrames[0] = new int[64 * 64];
                ft = DivoomFiles.File64(tensFrames, 100); fa = DivoomFiles.File64(all, 100);
                try { System.IO.Directory.CreateDirectory(dir); System.IO.File.WriteAllBytes(pt, ft); System.IO.File.WriteAllBytes(pa, fa); } catch (Exception) { }
            }
            Func<byte[], string> hash = b => { uint h = 2166136261; foreach (var x in b) h = (h ^ x) * 16777619; return h.ToString("x8"); };
            tens = "vvdigits/t/" + hash(ft); digits = "vvdigits/a/" + hash(fa);
            return new Dictionary<string, byte[]> { { tens, ft }, { digits, fa } };
        }
        // what the picture's own part of the clock looks like; a change here makes a new picture (else the saved one is reused)
        const string AquariumClockLook = "colon-366-grey_plants-ragged";   // (the digits are pictures of their own, AquariumClockDigits)
        public static List<int[]> AquariumClockFrames(out int ms)
        {
            double play;
            var frames = new Aquarium128().Next(out ms, out play);
            const int W2 = Tiivoo2.Wide;
            foreach (var f in frames)
                foreach (int top in new[] { 115, 119 })   // the colon: two white 2 x 2 dots, each in a grey 1-pixel outline
                {
                    for (int y = top - 1; y <= top + 2; y++) for (int x = 78; x <= 81; x++) f[y * W2 + x] = 0x707070;
                    for (int y = top; y <= top + 1; y++) for (int x = 79; x <= 80; x++) f[y * W2 + x] = 0xFFFFFF;
                }
            return frames;
        }

        // the picture, made once and kept (settings\cache): every restart sends the same one, which the Tiivoo 2 already
        // has, so a restart mid-way never leaves it asking for a picture that no longer exists
        static byte[] AquariumClockFile()
        {
            string path = System.IO.Path.Combine(Settings.DataFile("cache"), "aquarium_clock_" + AquariumClockLook + ".bin");
            try { if (System.IO.File.Exists(path)) return System.IO.File.ReadAllBytes(path); } catch (Exception) { }
            int ms; var file = Tiivoo2.LocalClockFile(AquariumClockFrames(out ms), ms);
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)); System.IO.File.WriteAllBytes(path, file); } catch (Exception) { }
            return file;
        }

        void AquariumClock(Tiivoo2 t2, DateTime until, Action shown)
        {
            var file = AquariumClockFile();
            string tens, digits; var pictures = AquariumClockDigits(out tens, out digits);
            var items = AquariumClockItems(tens, digits);
            if (!JobSend(delegate { t2.ShowLocalClock(AquariumClockId, "Aquarium Clock", file, items, pictures); })) return;
            SetStatus("Aquarium Clock: sending the aquarium to the Tiivoo 2 (about a minute and a half)...");
            var started = DateTime.Now;
            while (!jobStop && Before(until) && !t2.LocalFaceSent && (t2.LocalFaceAsked || (DateTime.Now - started).TotalSeconds < 20) && (DateTime.Now - started).TotalSeconds < 300)
                Wait(0.5);
            if (jobStop || !Before(until)) return;
            SetStatus("Aquarium Clock on the Tiivoo 2");
            if (shown != null) shown();
            while (!jobStop && Before(until)) Wait(0.5);
        }

        // send a loop, leave it playing (the next one is made meanwhile), then the next; an animation whose loop only
        // changes with a setting (the heart rate) is made again when that changes
        // shown: run once the first loop is on screen (its sound starts after the picture)
        void LoopPlay(Tiivoo2 t2, Loop128 loop, DateTime until, Action shown = null)
        {
            try { LoopPlaying(t2, loop, until, shown); }
            finally { LoopSound.Stop(); }
        }

        void LoopPlaying(Tiivoo2 t2, Loop128 loop, DateTime until, Action shown)
        {
            int ms = 0; double play = 0;
            var frames = loop.Next(out ms, out play);
            var sound = loop.Sound();
            while (!jobStop && Before(until))
            {
                var fr = frames; int m = ms; double hold = play;
                if (!JobSend(delegate { t2.ShowLoop(fr, m, loop.Photo); })) { SetStatus("Waiting for Tiivoo 2 connection..."); Wait(5); continue; }
                LoopSound.Play(sound);   // its soundtrack starts with it (none: stops the last one)
                if (shown != null) { shown(); shown = null; }
                List<int[]> nextFrames = null; int nextMs = 0; double nextPlay = 0;
                Thread maker = null;
                if (hold > 0) { maker = new Thread(delegate () { nextFrames = loop.Next(out nextMs, out nextPlay); }) { IsBackground = true }; maker.Start(); }
                var started = DateTime.Now;
                while (!jobStop && Before(until) && (hold > 0 ? (DateTime.Now - started).TotalSeconds < hold : !loop.Stale)) Wait(0.25);
                if (maker != null) { maker.Join(); frames = nextFrames; ms = nextMs; play = nextPlay; }
                else frames = loop.Next(out ms, out play);
                sound = loop.Sound();
            }
        }

        void StreamLoop(Func<int[]> next, Func<double> interval, DateTime until)
        {
            while (!jobStop && Before(until))
            {
                var started = DateTime.Now;
                int[] px = next();
                if (!JobSend(delegate { box.ShowImage(px); })) { SetStatus("Waiting for AuraBox connection..."); Wait(5); continue; }
                Wait(Math.Max(0, interval() - (DateTime.Now - started).TotalSeconds));   // sending time counts toward the frame
            }
        }

        // ---------- text ----------
        Panel BuildTextPage()
        {
            var p = NewPage();
            // the message, font, color and speed are saved, so the playlist's Scrolling Text item keeps them too
            var card = NewCard("Scrolling Message", 0, 0, W, 300);
            textBox = new TextBox { Location = new Point(12, 38), Width = W - 24, Text = cfg.TextMessage };
            card.Controls.Add(textBox);
            textColor = new ColorWheel { Location = new Point(12, 80), Size = new Size(150, 150), Value = cfg.TextColor };
            card.Controls.Add(textColor);
            card.Controls.Add(new Label { Text = "Font:", Location = new Point(186, 86), AutoSize = true });
            textFont = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(240, 82), Width = 170 };
            textFont.Items.AddRange(new object[] { "Tahoma", "Arial", "Verdana", "Consolas", "Segoe UI" });
            textFont.SelectedIndex = Math.Max(0, textFont.Items.IndexOf(cfg.TextFont));
            card.Controls.Add(textFont);
            card.Controls.Add(new Label { Text = "Speed:", Location = new Point(186, 130), AutoSize = true });
            textSpeed = new TrackBar { Minimum = 1, Maximum = 10, Value = cfg.TextSpeed, Location = new Point(236, 122), Width = 240, TickStyle = TickStyle.None };
            card.Controls.Add(textSpeed);

            // preview: the message scrolling here at the chosen speed, the way the box will show it
            var preview = new PixelGrid(10) { Location = new Point(W - 124, 80), ReadOnly = true };
            card.Controls.Add(preview);
            card.Controls.Add(new Label { Text = "Preview", Location = new Point(W - 124, 186), AutoSize = true, Tag = "subtle" });
            int[,] previewStrip = null; int previewOffset = 0; DateTime previewNext = DateTime.MinValue;
            EventHandler changed = delegate
            {
                previewStrip = null;   // redraw from the start
                cfg.TextMessage = textBox.Text; cfg.TextFont = (string)textFont.SelectedItem;
                cfg.TextColor = textColor.Value; cfg.TextSpeed = textSpeed.Value;
            };
            textBox.TextChanged += changed; textFont.SelectedIndexChanged += changed;
            textColor.Changing += changed; textColor.Picked += delegate { changed(null, null); SaveSettings(); };
            textSpeed.ValueChanged += changed;
            textBox.Leave += delegate { SaveSettings(); }; textFont.SelectionChangeCommitted += delegate { SaveSettings(); };
            textSpeed.MouseUp += delegate { SaveSettings(); };
            var previewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            previewTimer.Tick += delegate
            {
                if (!p.Visible || DateTime.Now < previewNext) return;
                int rows = sel.Box.Size;   // the preview follows the display picked at the top
                if (previewStrip == null || previewStrip.GetLength(1) != rows) { previewStrip = TextArt.Strip(textBox.Text, (string)textFont.SelectedItem, rows); previewOffset = 0; }
                preview.SetAll(TextArt.Window(previewStrip, previewOffset, textColor.Value));
                previewOffset = (previewOffset + 1) % (previewStrip.GetLength(0) + rows + 1);
                previewNext = DateTime.Now.AddSeconds(0.45 - textSpeed.Value * 0.035);   // the same step time as the box
            };
            previewTimer.Start();

            textPlay = Btn("Play", 190, 176, 120, null, true);
            textPlay.Click += delegate
            {
                string text = textBox.Text, font = (string)textFont.SelectedItem;
                int color = textColor.Value;
                TogglePlay(textPlay, delegate { MarqueeLoop(text, font, color, DateTime.MaxValue); }, "Text");
            };
            card.Controls.Add(textPlay);
            card.Controls.Add(Note("Add emoji from the card below. The message is also the playlist's Scrolling Text item.\r\n" +
                                   "It is sent frame by frame from this PC, so keep the app running (it can sit in the tray).", 12, 246));
            p.Controls.Add(card);

            // emoji: each button shows the pixel picture the box will draw, and adds it where the cursor is
            var emojiCard = NewCard("Emoji (drawn on the box as these pixel pictures)", 0, 312, W, 100);
            var tips = new ToolTip();
            for (int k = 0; k < TextArt.Emoji.Length; k++)
            {
                char ch = TextArt.Emoji[k].Key;
                var b = Btn("", 12 + k * 54, 36, 48, delegate
                {
                    textBox.SelectedText = ch.ToString();
                    textBox.Focus();
                });
                b.Height = 48; b.Image = EmojiImage(ch);
                tips.SetToolTip(b, TextArt.Emoji[k].Value);
                emojiCard.Controls.Add(b);
            }
            emojiCard.Controls.Add(Note("Click one to add it to the\r\nmessage at the cursor.", 12 + TextArt.Emoji.Length * 54 + 12, 44));
            p.Controls.Add(emojiCard);
            return p;
        }

        // an emoji's pixel picture for its button (its 6 rows, off pixels left clear)
        static Bitmap EmojiImage(char emoji)
        {
            var cols = TextArt.PictureColumns(emoji);
            int cell = Theme.Px(4);
            var bmp = new Bitmap(cols.Count * cell, 6 * cell);
            using (var g = Graphics.FromImage(bmp))
                for (int x = 0; x < cols.Count; x++)
                    for (int y = 3; y < 9; y++)
                        if (cols[x][y] > 0)
                            using (var br = new SolidBrush(Proto.Palette[cols[x][y]])) g.FillRectangle(br, x * cell, (y - 3) * cell, cell - 1, cell - 1);
            return bmp;
        }

        // the message scrolling across the display: 10 rows on the AuraBox, 16 on the Tivoo
        void MarqueeLoop(string text, string font, int color, DateTime until)
        {
            int[,] strip = TextArt.Strip(text, font, box.Size);
            int total = strip.GetLength(0) + box.Size;
            while (!jobStop && Before(until))
            {
                for (int off = 0; off <= total && !jobStop && Before(until); off++)
                {
                    var started = DateTime.Now;
                    int[] px = TextArt.Window(strip, off, color);
                    if (!JobSend(delegate { box.ShowImage(px); })) { Wait(5); break; }
                    int speed = 5;
                    if (!OnUi(delegate { speed = textSpeed.Value; })) return;
                    // 16-row text is wider, so it steps faster to move across in the same time as on the AuraBox
                    Wait(Math.Max(0, (0.45 - speed * 0.035) * SizePace(box.Size) - (DateTime.Now - started).TotalSeconds));
                }
            }
        }

        // ---------- all displays: seen together as they stand, and animations that run across all of them ----------
        class WallAnimInfo { public string Id, Name, About; public Func<WallAnim> Make; }
        static readonly WallAnimInfo[] WallAnims = {
            new WallAnimInfo { Id = "wall_balls", Name = "Bouncing Balls", Make = () => new WallBalls(),
                               About = "Shiny balls fly from display to display, bouncing off the outer edges and off each other." },
            new WallAnimInfo { Id = "wall_comet", Name = "Comet", Make = () => new WallComet(),
                               About = "A comet loops through all the displays on a long winding path, its glowing tail fading behind it." },
            new WallAnimInfo { Id = "wall_worm", Name = "Worm", Make = () => new WallWorm(),
                               About = "A striped worm slithers from display to display, turning gently and bouncing off the outer edges." },
            new WallAnimInfo { Id = "wall_lavaclock", Name = "Lava Clock", Make = () => new WallLavaClock(H24),
                               About = "One lava lamp flowing across all the displays. Every minute the time bubbles up out of the lava, hours on the first display and minutes on the next, then melts and drips away." },
            new WallAnimInfo { Id = "wall_jellyfish", Name = "Jellyfish", Make = () => new WallJellyfish(),
                               About = "Glowing jellyfish pulse and drift from display to display through a dark tank, tentacles trailing behind each push." },
            new WallAnimInfo { Id = "wall_eyes", Name = "Creepy Eyes", Make = () => new WallEyes(),
                               About = "Bloodshot eyeballs in different colors, all slowly looking around together, until a few of them glitch and stare the other way." },
            new WallAnimInfo { Id = "wall_rainbow", Name = "Rainbow Sweep", Make = () => new WallRainbow(),
                               About = "Rainbow bands sweep across all the displays as one picture, slowly turning. Handy for lining up the layout." },
        };
        const string WallTag = "All Displays: ";   // what a display's job shows while it is part of one

        Thread wallThread; volatile bool wallStop; volatile WallAnimInfo wallPlaying;
        readonly List<Player> wallResume = new List<Player>();   // displays whose playlists paused for it, resumed on Stop
        readonly Dictionary<Player, int[]> wallFrames = new Dictionary<Player, int[]>();

        // the paired displays and their screens as they stand (all: also the ones left out of the animations across all
        // displays). Only 16 x 16 and bigger take part: the AuraBox's 10 x 10 and 8 colors don't fit in. A display new
        // to the layout goes to the right of the others.
        List<KeyValuePair<Player, WallScreen>> WallLayout(bool all = false)
        {
            var o = new List<KeyValuePair<Player, WallScreen>>();
            var list = new List<Player>(players).FindAll(pl => pl.Box.Size >= 16);
            lock (cfg.Layout)
            {
                double nextX = 0; double[] v;
                foreach (var pl in list) if (pl.Cfg.Mac.Length > 0 && cfg.Layout.TryGetValue(pl.Cfg.Mac, out v)) nextX = Math.Max(nextX, v[0] + v[2] + 20);
                foreach (var pl in list)
                {
                    if (pl.Cfg.Mac.Length == 0) continue;
                    if (!cfg.Layout.TryGetValue(pl.Cfg.Mac, out v)) { v = new[] { nextX, 0, 70.0 }; cfg.Layout[pl.Cfg.Mac] = v; nextX += v[2] + 20; }
                    if (!all && WallLeftOut(pl)) continue;
                    o.Add(new KeyValuePair<Player, WallScreen>(pl, new WallScreen { X = v[0], Y = v[1], Size = v[2], N = pl.Box.Size }));
                }
            }
            return o;
        }

        List<WallTile> WallTiles()
        {
            var o = new List<WallTile>();
            foreach (var kv in WallLayout(true))
            {
                var pl = kv.Key; int[] img = null;
                if (wallPlaying != null && pl.Showing != null && pl.Showing.StartsWith(WallTag)) lock (wallFrames) wallFrames.TryGetValue(pl, out img);
                lock (cfg.Layout)
                    o.Add(new WallTile { Name = pl.Name, Place = cfg.Layout[pl.Cfg.Mac], Image = img ?? pl.Box.LastImage, Mode = pl.Box.LastMode, Connected = pl.Box.Connected,
                                         Out = WallLeftOut(pl), Key = pl });
            }
            return o;
        }

        bool WallLeftOut(Player pl) { lock (cfg.WallOut) return cfg.WallOut.Contains(pl.Cfg.Mac); }

        // a display joins the animation playing across all displays: its playlist pauses and its job sends its part
        void WallJoin(Player pl, WallAnimInfo info)
        {
            As(pl, delegate
            {
                if (playlistOn) { PausePlaylist(); if (!wallResume.Contains(P)) wallResume.Add(P); }
                StartJob(WallSendLoop);
                showing = WallTag + info.Name;
            });
        }

        // a display leaves it (left out while it plays): back to its own playlist if it was playing one
        void WallLeave(Player pl)
        {
            As(pl, delegate
            {
                if (showing != null && showing.StartsWith(WallTag)) StopJob();
                if (wallResume.Remove(P) && !playlistOn && dcfg.Rotate) SetPlaylist(true);
            });
        }

        // the box by a display's name: in or out of the animations across all displays, at once if one is playing
        void WallToggle(WallTile tile)
        {
            var pl = tile.Key as Player;
            if (pl == null || pl.Cfg.Mac.Length == 0) return;
            bool leave;
            lock (cfg.WallOut) { leave = !cfg.WallOut.Contains(pl.Cfg.Mac); if (leave) cfg.WallOut.Add(pl.Cfg.Mac); else cfg.WallOut.Remove(pl.Cfg.Mac); }
            SaveSettings();
            var info = wallPlaying;
            if (leave)
            {
                if (info != null) WallLeave(pl);
                status.Text = pl.Name + " left out of All Displays" + (info != null ? ": back to its own playlist" : "; it keeps to its own playlist");
            }
            else
            {
                if (info != null) WallJoin(pl, info);
                status.Text = pl.Name + " is back in All Displays";
            }
        }

        static double WallPace(int speed) { return Math.Pow(1.25, speed - 5); }   // speed 5 = as designed

        // start an animation across every display: each one's playlist pauses and its job sends its part of the picture
        void PlayWall(WallAnimInfo info)
        {
            StopWall();
            var anim = info.Make();
            lock (wallFrames) wallFrames.Clear();
            wallStop = false; wallPlaying = info;
            foreach (var kv in WallLayout()) WallJoin(kv.Key, info);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            wallThread = new Thread(delegate ()
            {
                double last = 0;
                while (!wallStop)
                {
                    double now = stopwatch.Elapsed.TotalSeconds, dt = Math.Min(0.2, now - last); last = now;
                    var layout = WallLayout();
                    // every display went on to something else: nothing left to draw for
                    if (!layout.Exists(kv => kv.Key.Showing != null && kv.Key.Showing.StartsWith(WallTag))) break;
                    anim.SetArea(layout.ConvertAll(kv => kv.Value));
                    anim.Step(dt * WallPace(cfg.SpeedOf(info.Id)));
                    foreach (var kv in layout) { var f = anim.Render(kv.Value); lock (wallFrames) wallFrames[kv.Key] = f; }
                    Thread.Sleep(40);
                }
                if (!wallStop) wallPlaying = null;
            }) { IsBackground = true };
            wallThread.Start();
            status.Text = info.Name + " playing on all displays";
        }

        void StopWall()
        {
            if (wallThread == null) return;
            wallStop = true;
            foreach (var pl in players) As(pl, delegate { if (showing != null && showing.StartsWith(WallTag)) StopJob(); });
            wallThread.Join(1000);
            wallThread = null; wallPlaying = null;
        }

        // a display's job while an animation runs across all of them: send its newest part as fast as its link allows,
        // so a slower display skips frames instead of falling behind the others
        void WallSendLoop()
        {
            var pl = P; int[] last = null;
            while (!jobStop && !wallStop)
            {
                int[] f;
                lock (wallFrames) wallFrames.TryGetValue(pl, out f);
                if (f == null || f == last) { Thread.Sleep(10); continue; }
                last = f;
                if (!JobSend(delegate { box.ShowImage(f); })) { SetStatus("Waiting for " + pl.Name + " connection..."); Wait(5); }
            }
        }

        Panel BuildWallPage()
        {
            var p = NewPage();
            var lay = NewCard("Your Displays, As They Stand", 0, 0, W, 300);
            var view = new WallView { Location = new Point(12, 36), Size = new Size(W - 24, 232), Tiles = WallTiles };
            view.Moved += delegate { SaveSettings(); };
            view.Toggled += WallToggle;
            lay.Controls.Add(view);
            lay.Controls.Add(Note("Drag each 16×16 display to where it stands and its corner to its screen's width (mm). Untick one to leave it out.", 12, 274));
            p.Controls.Add(lay);

            var run = NewCard("Across All Displays", 0, 310, W, 210);
            var pick = new ListBox { Location = new Point(12, 40), Size = new Size(190, 156), IntegralHeight = false };
            AccentList(pick);
            foreach (var w in WallAnims) pick.Items.Add(w.Name);
            run.Controls.Add(pick);
            var title = new Label { Location = new Point(218, 38), Size = new Size(W - 230, 26), Font = Theme.Semibold(12f), UseMnemonic = false };
            var about = new Label { Location = new Point(218, 68), Size = new Size(W - 230, 44) };
            var play = Btn("Play", 218, 116, 120, null, true);
            run.Controls.Add(new Label { Text = "Speed:", Location = new Point(218, 166), AutoSize = true });
            var speed = new TrackBar { Minimum = 1, Maximum = 10, Location = new Point(270, 158), Width = 300, TickStyle = TickStyle.None };
            run.Controls.Add(title); run.Controls.Add(about); run.Controls.Add(play); run.Controls.Add(speed);
            p.Controls.Add(run);

            bool picking = false;
            pick.SelectedIndexChanged += delegate
            {
                if (pick.SelectedIndex < 0) return;
                var w = WallAnims[pick.SelectedIndex];
                title.Text = w.Name; about.Text = w.About;
                picking = true; speed.Value = cfg.SpeedOf(w.Id); picking = false;
                if (wallPlaying != null && wallPlaying != w) PlayWall(w);   // playing: switch to it
            };
            speed.ValueChanged += delegate
            {
                if (picking || pick.SelectedIndex < 0) return;
                cfg.SetSpeed(WallAnims[pick.SelectedIndex].Id, speed.Value); SaveSettings();
            };
            play.Click += delegate
            {
                if (wallPlaying != null)
                {
                    StopWall();
                    foreach (var pl in wallResume) As(pl, delegate { if (!playlistOn && dcfg.Rotate) SetPlaylist(true); });   // back to their playlists
                    status.Text = wallResume.Count > 0 ? "Stopped on all displays; playlists resumed" : "Stopped on all displays";
                    wallResume.Clear();
                }
                else if (WallLayout().Count == 0) status.Text = "No display to play on: tick one under its picture (16×16 displays only)";
                else if (pick.SelectedIndex >= 0) PlayWall(WallAnims[pick.SelectedIndex]);
            };
            pick.SelectedIndex = 0;

            var refresh = new System.Windows.Forms.Timer { Interval = 80 };
            refresh.Tick += delegate
            {
                if (!p.Visible) return;
                view.Invalidate();
                play.Text = wallPlaying != null ? "Stop" : "Play";
            };
            refresh.Start();
            return p;
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
            hours.SelectedIndexChanged += delegate { cfg.Clock24 = clock24 = hours.SelectedIndex - 1; ApplyTivooClock(); SaveSettings(); };
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
            LavaClock lavaSim = null; bool lavaH24 = H24; int lavaSize = 10;
            var lavaTick = new System.Windows.Forms.Timer { Interval = 100 };
            lavaTick.Tick += delegate
            {
                if (!lava.Visible) { lavaSim = null; return; }   // false while its page is hidden
                int size = sel.Box.Size;
                if (lavaSim == null || lavaH24 != H24 || lavaSize != size) { lavaH24 = H24; lavaSize = size; lavaSim = new LavaClock(lavaH24, size); }
                var f = lavaSim.Next();
                lavaPreview.SetAll(size == 16 ? Palette16.ToRgb(f) : f);
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

        // the app's own steady clock is for the AuraBox (whose built-in clock blinks); the Tivoo's own clock looks better
        bool SteadyClock { get { return cfg.SteadyClock && box.Kind == "aurabox"; } }

        static int[] ClockFace(DateTime t) { return ClockArt.Face(t, H24, 7, 6); }   // white hours, cyan minutes

        void ShowClockNow()
        {
            if (SteadyClock) { StartJob(delegate { ClockLoop(DateTime.MaxValue); }); showing = "Clock"; status.Text = "Showing the steady clock"; }
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
            bool repeat = playlistOn;
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
        // the weather on a 128 x 128 screen: the 16 x 16 animated icon five times the size, the temperature large, and the
        // conditions under it; one frame for each frame of the icon
        static List<int[]> Weather128(WeatherInfo w)
        {
            var frames = new List<int[]>();
            string temp = Math.Round(w.Temp) + "°" + w.Unit, what = w.Description;
            foreach (var icon in WeatherArt16.IconFrames(w))
            {
                var rgb = Palette16.ToRgb(icon);
                using (var bmp = new Bitmap(128, 128, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Black);
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        for (int k = 0; k < 256; k++)
                            if (rgb[k] != 0)
                                using (var b = new SolidBrush(Color.FromArgb(rgb[k] >> 16 & 255, rgb[k] >> 8 & 255, rgb[k] & 255)))
                                    g.FillRectangle(b, 24 + (k % 16) * 5, 2 + (k / 16) * 5, 5, 5);
                        var center = new StringFormat { Alignment = StringAlignment.Center };
                        using (var big = new Font("Segoe UI Semibold", 22f, GraphicsUnit.Pixel))
                            g.DrawString(temp, big, Brushes.White, new RectangleF(0, 80, 128, 30), center);
                        using (var small = new Font("Segoe UI", 12f, GraphicsUnit.Pixel))
                        using (var gray = new SolidBrush(Color.FromArgb(190, 210, 230)))
                            g.DrawString(what, small, gray, new RectangleF(0, 108, 128, 18), center);
                    }
                    var px = new int[128 * 128];
                    for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) px[y * 128 + x] = bmp.GetPixel(x, y).ToArgb() & 0xFFFFFF;
                    frames.Add(px);
                }
            }
            return frames;
        }

        void WeatherPeriod(DateTime end)
        {
            var w = GetWeather(false);
            if (w == null) { SetStatus("Weather unavailable — retrying later"); WaitUntil(end); return; }
            var tv = box as Tivoo;
            if (tv != null) JobSend(delegate { SendWeatherTo(tv, w); });   // keeps the Tivoo's own clock and weather screen current
            SetStatus("Weather — " + Math.Round(w.Temp) + "°" + w.Unit + ", " + w.Description);
            var t2w = box as Tiivoo2;
            if (t2w != null)
            {   // the Tiivoo 2: a full-screen card it plays by itself: the sky as it is, and tomorrow's on its back
                var card = WeatherScene.Make(w, cfg.Place);
                JobSend(delegate { t2w.ShowLoop(card, WeatherScene.FrameMs, true); });
                WaitUntil(end);
                return;
            }
            // a 16 x 16 display gets the 16 x 16 pictures (palette indexes until sent, then RGB)
            bool big = box.Size == 16;
            Func<int[], int[]> ready = px => big ? Palette16.ToRgb(px) : px;
            var icon = big ? WeatherArt16.IconFrames(w) : WeatherArt.IconFrames(w);
            int r0x = 0, r0y = 0;
            int[] temp = big ? WeatherArt16.Temperature(w, 0, 0, out r0x, out r0y) : WeatherArt.Temperature(w);
            if (big) temp = WeatherArt16.Temperature(w, r0x / 2, r0y / 2, out r0x, out r0y);   // centered
            bool showIcon = true;
            // the temperature drifts and bounces off the edges, starting from a random corner
            var rng = new Random();
            int roomX, roomY;
            if (big) WeatherArt16.Temperature(w, 0, 0, out roomX, out roomY); else WeatherArt.Temperature(w, 0, 0, out roomX, out roomY);
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
                        var send = ready(px);
                        ok = JobSend(delegate { box.ShowImage(send); });
                        if (ok) Wait(0.3);
                    }
                }
                else if (!showIcon && cfg.BounceTemp && roomX + roomY > 0)
                {
                    ok = true;
                    while (ok && !jobStop && Before(phaseEnd))
                    {
                        int[] px = ready(big ? WeatherArt16.Temperature(w, bx, by, out roomX, out roomY) : WeatherArt.Temperature(w, bx, by, out roomX, out roomY));
                        ok = JobSend(delegate { box.ShowImage(px); });
                        if (ok) Wait(0.25);
                        if (roomX > 0) { if (bx + vx < 0 || bx + vx > roomX) vx = -vx; bx += vx; }
                        if (roomY > 0) { if (by + vy < 0 || by + vy > roomY) vy = -vy; by += vy; }
                    }
                }
                else
                {
                    int[] px = ready(showIcon ? icon[0] : temp);
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
            rotateBox.CheckedChanged += delegate { if (!syncingRotate) SetPlaylist(rotateBox.Checked); };
            p.Controls.Add(rotateBox);
            p.Controls.Add(Btn("Skip to Next Item  ›", W - 190, 0, 190, delegate
            {
                if (!playlistOn) { status.Text = "Start the playlist first"; return; }
                skipItem = true;
                status.Text = "Skipping to the next item...";
            }));   // a plain button: running the playlist is this page's main action
            p.Controls.Add(Note("Turned-on items play top to bottom, then repeat. Sliders set each animation's speed.", 2, 32));
            var onlyOn = new CheckBox { Text = "Show only turned-on items", Location = new Point(W - 190, 36), AutoSize = true, Checked = cfg.OnlyOnItems };
            onlyOn.CheckedChanged += delegate { cfg.OnlyOnItems = onlyOn.Checked; SaveSettings(); LayoutPlayRows(); };
            p.Controls.Add(onlyOn);

            var card = NewCard(null, 0, 62, W, H - 62);
            var rowTips = new ToolTip();
            playRowsPanel = new Panel { Location = new Point(1, 7), Size = new Size(W - 2, H - 62 - 14), AutoScroll = true };
            card.Controls.Add(playRowsPanel);
            playHeaders["on"] = Header("Playing, In Order");
            foreach (string g in PlayItem.Groups) playHeaders[g] = Header("Turn On More: " + g);
            foreach (var it in dcfg.Playlist)
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
                if (Array.Exists(LiveAnims, la => la.Id == id))
                {
                    // the animation's speed, the same setting as on the Animations page
                    var row = r;
                    row.Speed = new TrackBar { Minimum = 1, Maximum = 10, Value = cfg.SpeedOf(id), TickStyle = TickStyle.None, AutoSize = false, Size = new Size(96, 26) };
                    rowTips.SetToolTip(row.Speed, "Speed (also on the Animations page)");
                    bool syncing = false;
                    row.Speed.ValueChanged += delegate { if (syncing) return; cfg.SetSpeed(id, row.Speed.Value); SaveSettings(); speedChanged(id); };
                    speedChanged += delegate (string changed)
                    {
                        if (changed != id || row.Speed.Value == cfg.SpeedOf(id)) return;
                        syncing = true; row.Speed.Value = cfg.SpeedOf(id); syncing = false;
                    };
                    playRowsPanel.Controls.Add(row.Speed);
                }
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
                foreach (var it in dcfg.Playlist)
                    if (it.On) on.Add(RowFor(it.Id));
            Place(playHeaders["on"], 14, y); y += 26;
            for (int k = 0; k < on.Count; k++) { PlaceRow(on[k], y, true, k > 0, k < on.Count - 1); y += 31; }
            if (on.Count == 0) y += 4;
            foreach (string g in PlayItem.Groups)
            {
                var rows = new List<PlayRow>();
                lock (playlistGate)
                    foreach (var it in dcfg.Playlist)
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
            if (r.Speed != null) r.Speed.Visible = visible;
            if (!visible) return;
            Place(r.On, 14, y + 2); Place(r.Amount, 330, y); Place(r.Unit, 402, y);
            Place(r.Up, 506, y - 1); Place(r.Down, 544, y - 1);   // right after the time, not off at the far edge
            if (r.Speed != null) Place(r.Speed, 584, y);
        }

        PlayRow RowFor(string id) { foreach (var r in playRows) if (r.Id == id) return r; return null; }

        // a newly turned-on item joins the end of the play order
        void TurnedOnOrOff(string id, bool on)
        {
            if (on)
                lock (playlistGate)
                {
                    var it = PlayItem.Find(dcfg.Playlist, id);
                    dcfg.Playlist.Remove(it);
                    int last = -1;
                    for (int k = 0; k < dcfg.Playlist.Count; k++) if (dcfg.Playlist[k].On) last = k;
                    dcfg.Playlist.Insert(last + 1, it);
                }
            SaveSettings();
            LayoutPlayRows();
        }

        bool refreshingRows;

        void RefreshPlayRows()
        {
            refreshingRows = true;
            lock (playlistGate)
                foreach (var it in dcfg.Playlist)
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
                foreach (var it in dcfg.Playlist)
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
                var list = dcfg.Playlist;
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
                lock (playlistGate) it = PlayItem.Find(dcfg.Playlist, first);
                RunItem(it, ref lastSync);
                if (!repeat)
                {
                    currentItem = null;
                    if (jobStop) return;
                    SetStatus("Back to clock"); showing = "Clock";
                    if (SteadyClock) ClockLoop(DateTime.MaxValue);
                    else JobSend(delegate { box.SetMode(0); });
                    return;
                }
            }
            while (!jobStop)
            {
                var items = new List<PlayItem>();
                lock (playlistGate) foreach (var it in dcfg.Playlist) if (it.On) items.Add(new PlayItem { Id = it.Id, Name = it.Name, Seconds = it.Seconds });
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
            foreach (var la in LiveAnims) if (la.Id == it.Id) { PlayLiveAnim(la, end); return; }
            switch (it.Id)
            {
                case "weather": WeatherPeriod(end); return;
                case "clock": if (SteadyClock) { ClockLoop(end); return; } break;
                case "text":
                    string text = null, font = null; int color = 7;
                    if (!OnUi(delegate { text = textBox.Text; font = (string)textFont.SelectedItem; color = textColor.Value; })) return;
                    MarqueeLoop(text, font, color, end);
                    return;
            }
            Action show = null;
            if (it.Id == "favorite" || it.Id == "holiday")
            {
                GalleryItem pick;
                string from = "Random Favorite";
                if (it.Id == "favorite")
                {
                    pick = RandomFavorite(box.Size);
                    if (pick == null) { SetStatus("Random Favorite: no favorites yet (right-click a design in the Gallery)"); Wait(5); return; }
                }
                else
                {
                    var h = Holidays.Current(DateTime.Today);
                    if (h == null)   // nothing in season: the item is skipped
                    {
                        DateTime when;
                        var nh = Holidays.Next(DateTime.Today, out when);
                        SetStatus("Holiday Designs: no holiday in season" + (nh != null ? " (next: " + nh.Name + ", from " + when.ToString("MMMM d") + ")" : ""));
                        Wait(3);
                        return;
                    }
                    pick = RandomHoliday(h);
                    if (pick == null) { SetStatus("Holiday Designs: couldn't get " + h.Name + " designs from Divoom"); Wait(5); return; }
                    from = h.Name;
                }
                jobStatus = "Playlist: " + pick.Name + " (" + from + ") until " + end.ToString("t");
                SetStatus(jobStatus);
                var tv = box as Tivoo;
                var t2 = box as Tiivoo2;
                if (pick.Raw != null && t2 != null) { var raw = pick.Raw; var look = Proto.ToRgb16(pick.Frames[0]); show = delegate { t2.ShowDivoom128(raw, look); }; }
                else if (pick.Frames.Count > 1 && tv != null)
                {
                    var fr = Sample(pick.Frames, Tivoo.MaxFrames); int ms = pick.Ms > 0 ? pick.Ms : 200;
                    show = delegate { tv.ShowAnimationMs(fr, ms); };
                }
                else if (pick.Frames.Count > 1) { var fr = Sample(pick.Frames); int delay = 5; OnUi(delegate { delay = (int)delayBox.Value; }); show = delegate { box.ShowAnimation(fr, delay); }; }
                else { var px = pick.Frames[0]; show = delegate { box.ShowImage(px); }; }
            }
            else if (it.Id == "clock" && box is Tivoo) { var tv = (Tivoo)box; var w = GetWeather(false); show = delegate { if (w != null) SendWeatherTo(tv, w); tv.ShowClock(); }; }
            else if (it.Id == "clock") show = delegate { box.SetMode(0); };
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

        // a design for the holiday in season, from the first pages of its Gallery list (saved designs come straight
        // from this PC), with its year brought up to date; never the same one twice in a row
        string lastHoliday;
        GalleryItem RandomHoliday(Holiday h)
        {
            var src = new GallerySource { Title = h.Name, Kind = "holiday", Query = h.Searches };
            for (int tries = 0; tries < 4; tries++)
            {
                int page;
                lock (favoriteDice) page = favoriteDice.Next(3);
                List<DivoomCloud.Entry> list;
                int total;
                try { list = CloudList(src, page, out total); } catch (Exception) { continue; }
                if (list.Count == 0) continue;
                DivoomCloud.Entry e;
                lock (favoriteDice) e = list[favoriteDice.Next(list.Count)];
                if (e.Id == lastHoliday && list.Count > 1) continue;
                List<int[]> fr;
                try { fr = CloudFrames(e.Id); } catch (Exception) { continue; }
                string name = GalleryNames.NoYears(GalleryNames.Clean(e.Name));
                var item = new GalleryItem { Category = h.Name, Name = name.Length > 0 ? name : h.Name };
                if (e.Id.StartsWith(DivoomCloud16.Prefix)) item.Ms = DivoomCloud16.MsOf(e.Id.Substring(DivoomCloud16.Prefix.Length));
                foreach (var f in Distinct(fr)) item.Frames.Add(f);
                Renamed(item);
                UpdateYear(item, e.Name + " " + h.Name);
                lastHoliday = e.Id;
                return item;
            }
            return null;
        }

        // a favorite for the playlist: one made for this display's size (any favorite when there are none), never the
        // same one twice in a row on a display
        static readonly Random favoriteDice = new Random();
        readonly Dictionary<int, string> lastFavorite = new Dictionary<int, string>();
        GalleryItem RandomFavorite(int size)
        {
            List<GalleryItem> all;
            try { all = GalleryItem.LoadFavorites(); } catch (Exception) { return null; }
            var fits = all.FindAll(f => f.Frames.Count > 0 && Proto.Side(f.Frames[0]) == size);
            if (fits.Count == 0) fits = all;
            if (fits.Count == 0) return null;
            string last;
            lock (lastFavorite) lastFavorite.TryGetValue(size, out last);
            GalleryItem pick;
            lock (favoriteDice) pick = fits[favoriteDice.Next(fits.Count)];
            if (fits.Count > 1 && pick.Name + Signature(pick.Frames) == last)
                lock (favoriteDice) pick = fits[(fits.IndexOf(pick) + 1 + favoriteDice.Next(fits.Count - 1)) % fits.Count];
            lock (lastFavorite) lastFavorite[size] = pick.Name + Signature(pick.Frames);
            return pick;
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
                Run(delegate { box.SetSleep(mins, sc, on); }, on ? "Sleep timer: " + mins + (mins == 1 ? " minute" : " minutes") : "Sleep timer off");
            }, true));
            p.Controls.Add(sg);
            p.Controls.Add(Note("Alarm and sleep go to the display picked at the top and run on it; the app does not need to stay open.", 2, 284));
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
            var trayBox = new CheckBox { Text = "Close (X) goes to the system tray, keeping the playlist running. Quit with Exit in the tray menu.", Location = new Point(14, 38),
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
            string[] starts = { "Don't start VaVaVoom Control", "Open VaVaVoom Control",
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
            win.Controls.Add(Note("When VaVaVoom Control isn't running or connected, the AuraBox goes back to its built-in clock.", 14, 158));
            p.Controls.Add(win);

            var backup = NewCard("Backup and Saved Designs", 0, 306, W, 108);
            backup.Controls.Add(Btn("Export Settings...", 12, 34, 162, delegate { ExportSettings(); }));
            backup.Controls.Add(Btn("Import Settings...", 182, 34, 162, delegate { ImportSettings(); }));
            backup.Controls.Add(Note("Settings and favorites are in the settings folder next\r\nto the program. Export puts them, with the playlist, in one file.", 360, 32));
            // every design the Gallery downloads is kept on this PC (settings\cache); how many, where, and clearing them
            var saved = new Label { Text = "Saved designs: counting…", Location = new Point(14, 76), AutoSize = true, UseMnemonic = false };
            backup.Controls.Add(saved);
            Action countSaved = delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    int files; long bytes;
                    DesignCache.Usage(out files, out bytes);
                    string text = "Saved designs: " + files.ToString("N0") + " (" + (bytes / 1048576.0).ToString("0.0") + " MB)";
                    try { BeginInvoke((Action)delegate { saved.Text = text; }); } catch (InvalidOperationException) { }
                });
            };
            p.VisibleChanged += delegate { if (p.Visible) countSaved(); };
            backup.Controls.Add(Btn("Open Folder", 360, 68, 130, delegate
            {
                try { Directory.CreateDirectory(DesignCache.Dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + DesignCache.Dir + "\""); }
                catch (Exception e) { status.Text = "Could not open the folder: " + e.Message; }
            }));
            backup.Controls.Add(Btn("Clear Saved Designs…", 498, 68, 188, delegate
            {
                if (MessageBox.Show(this, "Delete the Gallery designs saved on this PC?\r\n\r\nThey download again when you browse. Favorites, your lists and names you gave designs are kept.",
                                    "Clear Saved Designs", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try { DesignCache.Clear(); status.Text = "Saved designs cleared"; }
                catch (Exception e) { status.Text = "Could not clear everything: " + e.Message; }
                countSaved();
            }));
            p.Controls.Add(backup);

            var about = NewCard("About", 0, 424, W, 96);
            about.Controls.Add(new Label { Text = "VaVaVoom Control " + VersionText(AppVersion) + " — a portable app for Divoom pixel displays over Bluetooth.",
                                           Location = new Point(14, 34), AutoSize = true, UseMnemonic = false });
            // Check for Updates turns into "Download x.y.z" when GitHub has a newer release, which opens the release
            // page to download it from (hidden when UpdateRepo is empty)
            updateBtn = Btn("Check for Updates", W - 172, 26, 158, delegate
            {
                if (updateVersion != null) OpenUpdatePage();
                else CheckForUpdates(true);
            });
            updateBtn.Visible = UpdateRepo.Length > 0;
            about.Controls.Add(updateBtn);
            about.Controls.Add(Note("Not affiliated with or endorsed by Divoom. Live weather and the Divoom gallery need the internet.", 14, 54));
            var autoCheck = new CheckBox { Text = "Check for updates when VaVaVoom Control starts", Location = new Point(14, 70), AutoSize = true,
                                           Checked = cfg.CheckUpdates, Visible = UpdateRepo.Length > 0 };
            autoCheck.CheckedChanged += delegate { cfg.CheckUpdates = autoCheck.Checked; SaveSettings(); };
            about.Controls.Add(autoCheck);
            p.Controls.Add(about);
            return p;
        }

        // ---------- version and updates ----------
        // GitHub "owner/repo" whose latest release is compared with this version (empty: no update check)
        const string UpdateRepo = "JBA474/vavavoom-control";
        Button updateBtn; string updateUrl; Version updateVersion;

        static Version AppVersion { get { return typeof(MainForm).Assembly.GetName().Version; } }
        static string VersionText(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        void CheckForUpdates(bool asked)
        {
            if (asked) status.Text = "Checking for updates...";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string tag = null, page = null, error = null;
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;   // TLS 1.2
                    using (var wc = new System.Net.WebClient())
                    {
                        wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "VaVaVoomControl/" + VersionText(AppVersion);
                        string json = wc.DownloadString("https://api.github.com/repos/" + UpdateRepo + "/releases/latest");
                        var t = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){0,3})");
                        var u = System.Text.RegularExpressions.Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+)\"");   // the first one is the release page
                        if (t.Success) { tag = t.Groups[1].Value; page = u.Success ? u.Groups[1].Value : "https://github.com/" + UpdateRepo + "/releases"; }
                    }
                }
                catch (Exception e) { error = e.Message; }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        Version latest;
                        if (tag != null && Version.TryParse(tag.Contains(".") ? tag : tag + ".0", out latest) && latest > AppVersion)
                        {
                            updateUrl = page; updateVersion = latest;
                            updateBtn.Text = "Download " + VersionText(latest); updateBtn.Tag = "primary"; Theme.Apply(updateBtn);
                            status.Text = "VaVaVoom Control " + VersionText(latest) + " is available: see Settings, About";
                        }
                        else if (asked)
                            status.Text = error != null ? "Couldn't check for updates: " + error
                                                        : "You have the latest version (" + VersionText(AppVersion) + ")";
                    });
                }
                catch (InvalidOperationException) { }   // window closed meanwhile
            });
        }

        // the release page, where the newer version can be downloaded
        void OpenUpdatePage()
        {
            try { System.Diagnostics.Process.Start(updateUrl ?? "https://github.com/" + UpdateRepo + "/releases"); } catch (Exception) { }
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
            using (var dlg = new SaveFileDialog { Filter = "VaVaVoom Control backup (*.txt)|*.txt", FileName = "VaVaVoom Control Backup " + DateTime.Now.ToString("yyyy-MM-dd") + ".txt" })
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
            using (var dlg = new OpenFileDialog { Filter = "VaVaVoom Control backup (*.txt)|*.txt|All files (*.*)|*.*" })
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
                        MessageBox.Show(this, "That file isn't a VaVaVoom Control backup.", "Import Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (MessageBox.Show(this, "Replace the current settings" + (mark < 0 ? "" : " and favorites") + " with this backup?\r\n\r\n" +
                                        "VaVaVoom Control will restart to load them.", "Import Settings",
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
            using (var mutex = new Mutex(true, "VaVaVoomControl.SingleInstance", out first))
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
