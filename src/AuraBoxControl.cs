// AuraBox Control - portable Windows app for the Divoom AuraBox (Bluetooth SPP).
// Build (no SDK needed): build.cmd  ->  AuraBoxControl.exe
//
// Protocol (reverse-engineered from the official Android app, com.cheerchip.aurabox1):
//   frame  = 01 | escaped(len_lo len_hi cmd data... sum_lo sum_hi) | 02
//   len    = data.Length + 3, sum = 16-bit sum of len, cmd and data
//   escape = 01 -> 03 04, 02 -> 03 05, 03 -> 03 06
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("AuraBox Control")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace AuraBoxControl
{
    static class Proto
    {
        public const byte ChangeMode = 0x05, SetTime = 0x18, Brightness = 0x32, SleepTime = 0x40, SleepScene = 0x41, AlarmScene = 0x43,
                          SetImage = 0x44, SetMode = 0x45, SetColor = 0x47, SetAnimation = 0x49, TempUnit = 0x4C, Notify = 0x50;

        public static readonly string[] NotifyNames =
            { "Facebook", "Twitter", "Incoming Call", "Missed Call", "WhatsApp", "Text Message", "Skype", "Line", "WeChat", "QQ" };
        public const int Size = 10;

        public static readonly string[] ModeNames =
            { "Clock", "Temperature", "Color Light", "Sound Light", "Forest", "Marquee", "Color Pieces", "Countdown" };

        // 3-bit palette: bit0 = red, bit1 = green, bit2 = blue
        public static readonly Color[] Palette = {
            Color.Black, Color.FromArgb(255, 0, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(255, 255, 0),
            Color.FromArgb(0, 0, 255), Color.FromArgb(255, 0, 255), Color.FromArgb(0, 255, 255), Color.White };
        public static readonly string[] ColorNames = { "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White" };

        public static byte[] Frame(byte cmd, byte[] data)
        {
            var body = new List<byte>();
            int n = data.Length + 3;
            body.Add((byte)(n & 0xFF)); body.Add((byte)(n >> 8)); body.Add(cmd);
            body.AddRange(data);
            int sum = 0;
            foreach (byte b in body) sum += b;
            body.Add((byte)(sum & 0xFF)); body.Add((byte)((sum >> 8) & 0xFF));
            var output = new List<byte> { 1 };
            foreach (byte b in body)
            {
                if (b >= 1 && b <= 3) { output.Add(3); output.Add((byte)(b + 3)); }
                else output.Add(b);
            }
            output.Add(2);
            return output.ToArray();
        }

        // 100 palette indexes (row-major) -> 50 bytes, first pixel in the low nibble
        public static byte[] Pack(int[] px)
        {
            var o = new byte[50];
            for (int k = 0; k < 100; k++) o[k / 2] |= (byte)((px[k] & 0x0F) << (4 * (k % 2)));
            return o;
        }

        public static int[] Unpack(byte[] data)
        {
            var px = new int[100];
            for (int k = 0; k < 100; k++) px[k] = (data[k / 2] >> (4 * (k % 2))) & 0x07;
            return px;
        }

        public static Bitmap Render(int[] px, int cell)
        {
            var bmp = new Bitmap(10 * cell, 10 * cell);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(20, 20, 20));
                int gap = cell > 3 ? 1 : 0;
                for (int k = 0; k < 100; k++)
                    using (var b = new SolidBrush(px[k] == 0 ? Color.FromArgb(40, 40, 40) : Palette[px[k]]))
                        g.FillRectangle(b, (k % 10) * cell, (k / 10) * cell, cell - gap, cell - gap);
            }
            return bmp;
        }

        public static int Nearest(Color c)
        {
            int best = 0; long bestD = long.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                long dr = c.R - Palette[i].R, dg = c.G - Palette[i].G, db = c.B - Palette[i].B;
                long d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }

    // SOCKADDR_BTH (packed): family u16, btAddr u64, serviceClassId GUID, port u32
    class BluetoothEndPoint : EndPoint
    {
        const AddressFamily Bth = (AddressFamily)32;
        static readonly Guid Spp = new Guid("00001101-0000-1000-8000-00805F9B34FB");
        readonly ulong addr; readonly int port;
        public BluetoothEndPoint(ulong addr, int port) { this.addr = addr; this.port = port; }
        public override AddressFamily AddressFamily { get { return Bth; } }
        public override SocketAddress Serialize()
        {
            var sa = new SocketAddress(Bth, 30);
            for (int i = 0; i < 8; i++) sa[2 + i] = (byte)(addr >> (8 * i));
            byte[] g = (port == 0 ? Spp : Guid.Empty).ToByteArray();
            for (int i = 0; i < 16; i++) sa[10 + i] = g[i];
            for (int i = 0; i < 4; i++) sa[26 + i] = (byte)(port >> (8 * i));
            return sa;
        }
        public override EndPoint Create(SocketAddress sa) { return this; }
    }

    class AuraBox
    {
        Socket sock;
        readonly object gate = new object();
        public bool Connected { get { return sock != null; } }

        // channel 0 = let Windows look up the serial-port service (SDP)
        public int Connect(ulong addr, int preferred)
        {
            var tries = new List<int> { 0 };
            if (preferred > 0) tries.Add(preferred);
            for (int c = 1; c <= 6; c++) if (!tries.Contains(c)) tries.Add(c);
            Exception last = null;
            foreach (int ch in tries)
            {
                var s = new Socket((AddressFamily)32, SocketType.Stream, (ProtocolType)3);
                try
                {
                    s.Connect(new BluetoothEndPoint(addr, ch));
                    s.SendTimeout = 3000;
                    sock = s;
                    StartDrain(s);
                    return ch;
                }
                catch (Exception e) { last = e; s.Close(); }
            }
            throw new IOException("Could not open the AuraBox serial channel: " + (last == null ? "" : last.Message));
        }

        // the box greets with "HELLO" and acknowledges every command; read and discard so its replies never back up
        static void StartDrain(Socket s)
        {
            var t = new Thread(delegate ()
            {
                var buf = new byte[256];
                try { while (s.Receive(buf) > 0) { } }
                catch (Exception) { }
            }) { IsBackground = true };
            t.Start();
        }

        public void Close()
        {
            lock (gate) { if (sock != null) { try { sock.Close(); } catch { } sock = null; } }
        }

        public void Send(byte cmd, byte[] data)
        {
            lock (gate)
            {
                if (sock == null) throw new IOException("Not connected");
                sock.Send(Proto.Frame(cmd, data));
                Thread.Sleep(50);
            }
        }

        public void SetTime()
        {
            DateTime t = DateTime.Now;
            Send(Proto.SetTime, new byte[] { (byte)(t.Year % 100), (byte)(t.Year / 100), (byte)t.Month, (byte)t.Day,
                (byte)t.Hour, (byte)t.Minute, (byte)t.Second, (byte)((int)t.DayOfWeek + 1) });
        }
        // what the box shows now, for the app's preview: a picture, or the name of a built-in mode
        public volatile int[] LastImage;
        public volatile string LastMode;
        volatile Action lastShow;   // repeats the last picture/mode/animation command (see Redisplay)

        // put back what the box showed before something temporary covered it (a notification icon)
        public void Redisplay() { var a = lastShow; if (a != null) a(); }

        public void SetMode(int m)
        {
            Send(Proto.SetMode, new[] { (byte)m });
            LastImage = null; LastMode = m >= 0 && m < Proto.ModeNames.Length ? Proto.ModeNames[m] : "Mode " + m;
            lastShow = () => SetMode(m);
        }
        public void SetBrightness(int v) { Send(Proto.Brightness, new[] { (byte)Math.Max(0, Math.Min(255, v)) }); }
        public void SetColor(int c)
        {
            Send(Proto.SetColor, new[] { (byte)(c & 7) });
            var px = new int[100];
            for (int k = 0; k < 100; k++) px[k] = c & 7;
            LastImage = px; LastMode = null;
            lastShow = () => SetColor(c);
        }
        public void SetTempUnit(bool fahrenheit) { Send(Proto.TempUnit, new[] { (byte)(fahrenheit ? 1 : 0) }); }
        // audio source: 0 = Bluetooth, 2 = line-in (the box refuses line-in unless a cable is in the AUX jack)
        public void SetAudioInput(bool aux) { Send(Proto.ChangeMode, new[] { (byte)(aux ? 2 : 0) }); }
        public void Notify(int code) { Send(Proto.Notify, new[] { (byte)code }); }
        public void SetAlarm(int hour, int minute, int scene, bool on)
        {
            Send(Proto.AlarmScene, new[] { (byte)hour, (byte)minute, (byte)scene, (byte)(on ? 1 : 0) });
        }
        public void SetSleep(int minutes, int scene, bool on)
        {
            Send(Proto.SleepTime, new[] { (byte)minutes, (byte)scene, (byte)(on ? 1 : 0) });
        }

        public void ShowImage(int[] px)
        {
            var d = new List<byte> { 0, 10, 10, 4 };
            d.AddRange(Proto.Pack(px));
            Send(Proto.SetImage, d.ToArray());
            var shown = (int[])px.Clone();
            LastImage = shown; LastMode = null;
            lastShow = () => ShowImage(shown);
        }

        public void ShowAnimation(List<int[]> frames, int delay)
        {
            for (int n = 0; n < frames.Count && n < 8; n++)
            {
                var d = new List<byte> { 0, 10, 10, 4, (byte)n, (byte)delay };
                d.AddRange(Proto.Pack(frames[n]));
                Send(Proto.SetAnimation, d.ToArray());
            }
            if (frames.Count > 0) { LastImage = (int[])frames[0].Clone(); LastMode = null; }
            var copy = new List<int[]>(frames);
            lastShow = () => ShowAnimation(copy, delay);
        }
    }

    // Paired Bluetooth devices via bthprops (no inquiry, instant)
    static class PairedDevices
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SearchParams
        {
            public uint dwSize; public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
            public byte cTimeoutMultiplier; public IntPtr hRadio;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct SystemTime { public ushort a, b, c, d, e, f, g, h; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DeviceInfo
        {
            public uint dwSize; public ulong Address; public uint ulClassofDevice;
            public int fConnected, fRemembered, fAuthenticated;
            public SystemTime stLastSeen, stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstDevice(ref SearchParams p, ref DeviceInfo i);
        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindNextDevice(IntPtr h, ref DeviceInfo i);
        [DllImport("bthprops.cpl")]
        static extern bool BluetoothFindDeviceClose(IntPtr h);

        public static List<KeyValuePair<string, ulong>> List()
        {
            var result = new List<KeyValuePair<string, ulong>>();
            try
            {
                var p = new SearchParams();
                p.dwSize = (uint)Marshal.SizeOf(p);
                p.fReturnAuthenticated = 1; p.fReturnRemembered = 1; p.fReturnConnected = 1;
                var i = new DeviceInfo(); i.dwSize = (uint)Marshal.SizeOf(i);
                IntPtr h = BluetoothFindFirstDevice(ref p, ref i);
                if (h == IntPtr.Zero) return result;
                do
                {
                    result.Add(new KeyValuePair<string, ulong>(i.szName, i.Address));
                    i = new DeviceInfo(); i.dwSize = (uint)Marshal.SizeOf(i);
                } while (BluetoothFindNextDevice(h, ref i));
                BluetoothFindDeviceClose(h);
            }
            catch (Exception) { }
            return result;
        }

        public static string Mac(ulong a)
        {
            var sb = new StringBuilder();
            for (int k = 5; k >= 0; k--) { sb.Append(((a >> (8 * k)) & 0xFF).ToString("X2")); if (k > 0) sb.Append(':'); }
            return sb.ToString();
        }

        public static bool TryParseMac(string s, out ulong a)
        {
            a = 0;
            string hex = s.Replace(":", "").Replace("-", "").Trim();
            return hex.Length == 12 && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out a);
        }
    }

    // Built-in designs exported from the Android app (gallery.txt, embedded in the exe)
    class GalleryItem
    {
        public string Category, Name; public List<int[]> Frames = new List<int[]>();
        public bool IsAnimation { get { return Category == "Animations" || Frames.Count > 1; } }

        public static List<GalleryItem> Load()
        {
            var list = new List<GalleryItem>();
            foreach (string res in new[] { "gallery.txt", "extras.txt" })   // Divoom's designs, then our own (originals.py)
            {
                var s = typeof(GalleryItem).Assembly.GetManifestResourceStream(res);
                if (s == null) continue;
                using (var r = new StreamReader(s))
                {
                    string line;
                    while ((line = r.ReadLine()) != null) { var it = Parse(line); if (it != null) list.Add(it); }
                }
            }
            return list;
        }

        // "I|category|name|hex,hex..." (hex = 50 packed bytes per frame) or "A|name|hex,..." for Divoom's animations
        public static GalleryItem Parse(string line)
        {
            string[] p = line.Split('|');
            var it = new GalleryItem();
            string frames;
            if (p[0] == "I" && p.Length == 4) { it.Category = p[1]; it.Name = p[2]; frames = p[3]; }
            else if (p[0] == "A" && p.Length == 3) { it.Category = "Animations"; it.Name = p[1]; frames = p[2]; }
            else return null;
            foreach (string hex in frames.Split(','))
            {
                if (hex.Length != 100) continue;
                var b = new byte[50];
                for (int k = 0; k < 50; k++) b[k] = Convert.ToByte(hex.Substring(2 * k, 2), 16);
                it.Frames.Add(Proto.Unpack(b));
            }
            return it.Frames.Count > 0 ? it : null;
        }

        public string Format(string category)
        {
            var hex = new List<string>();
            foreach (var f in Frames) hex.Add(BitConverter.ToString(Proto.Pack(f)).Replace("-", "").ToLowerInvariant());
            return "I|" + category + "|" + Name.Replace("|", "/") + "|" + string.Join(",", hex.ToArray());
        }

        // favorites live in favorites.txt next to the settings file, in the same line format
        public static List<GalleryItem> LoadFavorites()
        {
            var list = new List<GalleryItem>();
            string path = Settings.DataFile("favorites.txt");
            if (!File.Exists(path)) return list;
            foreach (string line in File.ReadAllLines(path)) { var it = Parse(line); if (it != null) list.Add(it); }
            return list;
        }

        public static void SaveFavorites(List<GalleryItem> list)
        {
            var lines = new List<string>();
            foreach (var it in list) lines.Add(it.Format("Favorites"));
            string path = Settings.DataFile("favorites.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines.ToArray());
        }
    }

    // Text -> 10-pixel-high monochrome strip using a Windows font, for the scrolling marquee
    static class TextArt
    {
        public const int Ink = -1;   // strip cell drawn in the chosen text color

        // ❤ or ♥ in the text becomes this red pixel heart (an 8 px font can't draw an emoji legibly)
        static readonly string[] Heart = { ".XX.XX.", "XXXXXXX", "XXXXXXX", ".XXXXX.", "..XXX..", "...X..." };
        const int HeartTop = 3, HeartColor = 1;   // rows 3-8: level with the capitals of the default Tahoma

        // the text as columns of 10 cells: 0 = off, Ink = text color, anything else = that palette color
        public static int[,] Strip(string text, string fontName)
        {
            if (string.IsNullOrEmpty(text)) text = " ";
            var cols = new List<int[]>();
            var run = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch != '❤' && ch != '♥') { run.Append(ch); continue; }
                AddText(cols, run.ToString(), fontName); run.Length = 0;
                cols.Add(new int[10]);
                for (int x = 0; x < Heart[0].Length; x++)
                {
                    var col = new int[10];
                    for (int y = 0; y < Heart.Length; y++) if (Heart[y][x] == 'X') col[HeartTop + y] = HeartColor;
                    cols.Add(col);
                }
                cols.Add(new int[10]);
                if (i + 1 < text.Length && text[i + 1] == '️') i++;   // the emoji-style variant selector after ❤
            }
            AddText(cols, run.ToString(), fontName);
            cols.Add(new int[10]); cols.Add(new int[10]);
            var strip = new int[cols.Count, 10];
            for (int x = 0; x < cols.Count; x++)
                for (int y = 0; y < 10; y++) strip[x, y] = cols[x][y];
            return strip;
        }

        static void AddText(List<int[]> cols, string text, string fontName)
        {
            if (text.Length == 0) return;
            using (var font = new Font(fontName, 8f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;   // keep the space before a heart
                Size sz;
                using (var probe = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(probe))
                    sz = Size.Ceiling(g.MeasureString(text, font, PointF.Empty, format));
                int w = Math.Max(1, sz.Width);
                using (var bmp = new Bitmap(w, 10))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    g.DrawString(text, font, Brushes.White, 0, 1, format);
                    for (int x = 0; x < w; x++)
                    {
                        var col = new int[10];
                        for (int y = 0; y < 10; y++) if (bmp.GetPixel(x, y).R > 128) col[y] = Ink;
                        cols.Add(col);
                    }
                }
            }
        }

        // offset 0 = blank screen; text enters from the right
        public static int[] Window(int[,] strip, int offset, int color)
        {
            var px = new int[100];
            int w = strip.GetLength(0);
            for (int x = 0; x < 10; x++)
            {
                int sx = offset + x - 10;
                if (sx < 0 || sx >= w) continue;
                for (int y = 0; y < 10; y++)
                {
                    int v = strip[sx, y];
                    if (v != 0) px[y * 10 + x] = v == Ink ? color : v;
                }
            }
            return px;
        }
    }

    class WeatherInfo
    {
        public double Temp; public int Code; public bool IsDay; public string Unit; public DateTime Fetched;

        public string Description
        {
            get
            {
                if (Code == 0) return IsDay ? "Clear" : "Clear night";
                if (Code <= 2) return "Partly cloudy";
                if (Code == 3) return "Overcast";
                if (Code == 45 || Code == 48) return "Fog";
                if (Code >= 51 && Code <= 67) return "Rain";
                if ((Code >= 71 && Code <= 77) || Code == 85 || Code == 86) return "Snow";
                if (Code >= 80 && Code <= 82) return "Showers";
                if (Code >= 95) return "Thunderstorm";
                return "Code " + Code;
            }
        }
    }

    // Weather from Open-Meteo (free, no API key): https://open-meteo.com
    static class Weather
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        static string Get(string url)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "AuraBoxControl/1.0";
                return wc.DownloadString(url);
            }
        }

        static string Field(string json, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"?([^,\"}]*)");
            return m.Success ? m.Groups[1].Value : null;
        }

        public static WeatherInfo Fetch(double lat, double lon, bool fahrenheit)
        {
            string json = Get("https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(Inv) + "&longitude=" + lon.ToString(Inv) +
                              "&current=temperature_2m,weather_code,is_day&temperature_unit=" + (fahrenheit ? "fahrenheit" : "celsius"));
            int at = json.IndexOf("\"current\":{", StringComparison.Ordinal);
            if (at < 0) throw new IOException("Unexpected weather response");
            string cur = json.Substring(at);
            var w = new WeatherInfo { Unit = fahrenheit ? "F" : "C", Fetched = DateTime.Now };
            w.Temp = double.Parse(Field(cur, "temperature_2m"), Inv);
            w.Code = int.Parse(Field(cur, "weather_code"), Inv);
            w.IsDay = Field(cur, "is_day") != "0";
            return w;
        }

        // "Springfield, IL" -> first US match (preferring the given state). Returns display name.
        public static string Geocode(string place, out double lat, out double lon)
        {
            lat = lon = 0;
            string[] parts = place.Split(',');
            string city = parts[0].Trim(), state = parts.Length > 1 ? parts[1].Trim() : "";
            string json = Get("https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + Uri.EscapeDataString(city));
            var results = System.Text.RegularExpressions.Regex.Matches(json, "\\{[^{}]*\"latitude\"[^{}]*\\}");
            string best = null;
            foreach (System.Text.RegularExpressions.Match r in results)
            {
                string admin = Field(r.Value, "admin1") ?? "", cc = Field(r.Value, "country_code") ?? "";
                bool stateOk = state.Length == 0 || admin.Equals(state, StringComparison.OrdinalIgnoreCase) ||
                               (StateCodes.ContainsKey(state.ToUpperInvariant()) && StateCodes[state.ToUpperInvariant()] == admin);
                if (best == null || stateOk)
                {
                    best = r.Value;
                    if (stateOk) break;
                }
            }
            if (best == null) throw new IOException("Place not found: " + place);
            lat = double.Parse(Field(best, "latitude"), Inv);
            lon = double.Parse(Field(best, "longitude"), Inv);
            return Field(best, "name") + ", " + (Field(best, "admin1") ?? Field(best, "country_code"));
        }

        static readonly Dictionary<string, string> StateCodes = BuildStates();
        static Dictionary<string, string> BuildStates()
        {
            string s = "AL Alabama|AK Alaska|AZ Arizona|AR Arkansas|CA California|CO Colorado|CT Connecticut|DE Delaware|FL Florida|GA Georgia|" +
                       "HI Hawaii|ID Idaho|IL Illinois|IN Indiana|IA Iowa|KS Kansas|KY Kentucky|LA Louisiana|ME Maine|MD Maryland|MA Massachusetts|" +
                       "MI Michigan|MN Minnesota|MS Mississippi|MO Missouri|MT Montana|NE Nebraska|NV Nevada|NH New Hampshire|NJ New Jersey|" +
                       "NM New Mexico|NY New York|NC North Carolina|ND North Dakota|OH Ohio|OK Oklahoma|OR Oregon|PA Pennsylvania|RI Rhode Island|" +
                       "SC South Carolina|SD South Dakota|TN Tennessee|TX Texas|UT Utah|VT Vermont|VA Virginia|WA Washington|WV West Virginia|" +
                       "WI Wisconsin|WY Wyoming|DC District of Columbia";
            var d = new Dictionary<string, string>();
            foreach (string e in s.Split('|')) d[e.Substring(0, 2)] = e.Substring(3);
            return d;
        }
    }

    // 10x10 weather pictures for the display
    static class WeatherArt
    {
        // animated icons generated by weather_icons.py --cs (each string is one 10x10 frame)
        static readonly Dictionary<string, string[]> Icons = new Dictionary<string, string[]> {
            { "sun", new[] { "0000330000000000000000033330000033333300303333330330333333030033333300000333300000000000000000330000", "3000000003030000003000033330000033333300003333330000333333000033333300000333300003000000303000000003" } },
            { "moon", new[] { "0000333300003333007000333000000333000000033300000003330070000333000000003330000700333300000000333300", "0000333300003333000000333070000333000007033300000003330000000333000000003330000000333307000000333300" } },
            { "partly", new[] { "0003300000000000000000033000003033333000303333000000037707700007777777007777777700077777700000000000", "3000000300030000300000033000000033330000003333000000037707700307777777007777777700077777700000000000" } },
            { "partly_night", new[] { "0033300000033000700033000000003300000000033000000000337707700007777777007777777700077777700000000000", "0033300000033000000033000000003300000070033000000000337707700007777777007777777700077777700000000000" } },
            { "cloud", new[] { "0000000000000000000000077000000077770770077777777077777777770777777770000000000000000000000000000000", "0000000000000000000000077000000077770770077777777077777777770777777770000000000000000000000000000000", "0000000000000000000000770000000777707700777777770077777777707777777700000000000000000000000000000000", "0000000000000000000000770000000777707700777777770077777777707777777700000000000000000000000000000000", "0000000000000000000000077000000077770770077777777077777777770777777770000000000000000000000000000000", "0000000000000000000000077000000077770770077777777077777777770777777770000000000000000000000000000000" } },
            { "rain", new[] { "0007700000007777077007777777707777777777077777777006000000000600000600000000060000006000000000600000", "0007700000007777077007777777707777777777077777777000006000000600000000060000060000000006000000600000", "0007700000007777077007777777707777777777077777777000006000000000600000060000000006000006000000000600", "0007700000007777077007777777707777777777077777777000000006000000600000000060000006000000000600000600", "0007700000007777077007777777707777777777077777777006000006000000000600000060000000006000000600000000" } },
            { "snow", new[] { "0007700000007777077007777777707777777777077777777000700000000000000070000070000000000700000000000000", "0007700000007777077007777777707777777777077777777000000000000700000000000000000700070000000000007000", "0007700000007777077007777777707777777777077777777000000070000000000000007000000000000000700000700000", "0007700000007777077007777777707777777777077777777000007000000000070000000000000007000000000000000007", "0007700000007777077007777777707777777777077777777000000000070007000000000000700000000000000070000000" } },
            { "storm", new[] { "0004400000004444044004444444404444444444044444444000000000000600000000000000000000000000600000000000", "0004400000004444044004444444404444444444044444444000000000000600000000000000000000000000600000000000", "0004400000004444044004444444404444444444044444444000000000000600000000000000000000000000600000000000", "0007700000007777077007777777707777777777077773377000003300600003333000000033000006033000000003000000", "0004400000004444044004444444404444444444044444444000000000000600000000000000000000000000600000000000", "0007700000007777077007777777707777777777077773377000003300600003333000000033000006033000000003000000", "0004400000004444044004444444404444444444044444444000000000000600000000000000000000000000600000000000", "0004400000004444044004444444404444444444044444444000000000000600000000000000000000000000600000000000" } },
            { "fog", new[] { "0000000000777777700000000000000007777777000000000077777770000000000000000777777700000000000000000000", "0000000000077777770000000000000077777770000000000007777777000000000000007777777000000000000000000000", "0000000000007777777000000000000777777700000000000000777777700000000000077777770000000000000000000000", "0000000000000777777700000000007777777000000000000000077777770000000000777777700000000000000000000000", "0000000000007777777000000000000777777700000000000000777777700000000000077777770000000000000000000000", "0000000000077777770000000000000077777770000000000007777777000000000000007777777000000000000000000000" } },
        };

        internal static readonly Dictionary<char, string> Font = new Dictionary<char, string> {
            { '0', "111101101101111" }, { '1', "010110010010111" }, { '2', "111001111100111" }, { '3', "111001111001111" },
            { '4', "101101111001001" }, { '5', "111100111001111" }, { '6', "111100111101111" }, { '7', "111001001001001" },
            { '8', "111101111101111" }, { '9', "111101111001111" }, { '-', "000000111000000" } };

        public static string IconName(WeatherInfo w)
        {
            int c = w.Code;
            if (c == 0) return w.IsDay ? "sun" : "moon";
            if (c <= 2) return w.IsDay ? "partly" : "partly_night";
            if (c == 3) return "cloud";
            if (c == 45 || c == 48) return "fog";
            if ((c >= 71 && c <= 77) || c == 85 || c == 86) return "snow";
            if (c >= 95) return "storm";
            return "rain";
        }

        public static List<int[]> IconFrames(WeatherInfo w)
        {
            var list = new List<int[]>();
            foreach (string f in Icons[IconName(w)])
            {
                var px = new int[100];
                for (int k = 0; k < 100; k++) px[k] = f[k] - '0';
                list.Add(px);
            }
            return list;
        }

        public static int[] Icon(WeatherInfo w) { return IconFrames(w)[0]; }

        // how far the icon can move (across all its frames) and stay on screen; icons that fill an axis
        // still get one pixel each way, so they bob with an edge briefly clipped
        public static void IconRoom(List<int[]> frames, out int loX, out int hiX, out int loY, out int hiY)
        {
            int left = 9, right = 0, top = 9, bottom = 0;
            foreach (int[] px in frames)
                for (int k = 0; k < 100; k++)
                    if (px[k] != 0)
                    {
                        left = Math.Min(left, k % 10); right = Math.Max(right, k % 10);
                        top = Math.Min(top, k / 10); bottom = Math.Max(bottom, k / 10);
                    }
            if (right < left) { left = right = top = bottom = 0; }
            loX = -left; hiX = 9 - right; loY = -top; hiY = 9 - bottom;
            if (hiX - loX < 2) { loX = Math.Min(loX, -1); hiX = Math.Max(hiX, 1); }
            if (hiY - loY < 2) { loY = Math.Min(loY, -1); hiY = Math.Max(hiY, 1); }
        }

        public static int[] Shift(int[] px, int dx, int dy)
        {
            var o = new int[100];
            for (int k = 0; k < 100; k++)
            {
                int x = k % 10 + dx, y = k / 10 + dy;
                if (px[k] != 0 && x >= 0 && x < 10 && y >= 0 && y < 10) o[y * 10 + x] = px[k];
            }
            return o;
        }

        // Temperature digits, colored by how warm it is, with a degree dot top-right; centered (the still picture in the app).
        public static int[] Temperature(WeatherInfo w)
        {
            int roomX, roomY;
            Temperature(w, 0, 0, out roomX, out roomY);
            return Temperature(w, roomX / 2, roomY / 2, out roomX, out roomY);
        }

        // temperature digits with the degree dot at their top right, the block's top-left corner at (x0, y0);
        // roomX / roomY tell how far the block can move and still fit on the screen (used to bounce it around)
        public static int[] Temperature(WeatherInfo w, int x0, int y0, out int roomX, out int roomY)
        {
            double f = w.Unit == "F" ? w.Temp : w.Temp * 9 / 5 + 32;
            int color = f < 32 ? 7 : f < 50 ? 6 : f < 70 ? 2 : f < 85 ? 3 : 1;
            string text = ((int)Math.Round(w.Temp)).ToString();
            int gap = text.Length >= 3 ? 0 : 1;
            int width = text.Length * 3 + (text.Length - 1) * gap;
            int dotGap = width + 2 <= 10 ? 1 : 0;           // keep a column between digits and degree dot when it fits
            int total = Math.Min(10, width + dotGap + 1);
            roomX = 10 - total; roomY = 10 - 5;
            var px = new int[100];
            int cx = x0;
            foreach (char ch in text)
            {
                string g;
                if (Font.TryGetValue(ch, out g))
                    for (int k = 0; k < 15; k++)
                    {
                        int x = cx + k % 3, y = y0 + k / 3;
                        if (g[k] == '1' && x >= 0 && x < 10 && y >= 0 && y < 10) px[y * 10 + x] = color;
                    }
                cx += 3 + gap;
            }
            int dx = x0 + total - 1;
            if (dx < 10 && y0 >= 0 && y0 < 10) px[y0 * 10 + dx] = color;   // degree dot
            return px;
        }
    }

    // Steady clock drawn by this app: hours on top, minutes below (3x5 digits), so there is no blinking colon.
    static class ClockArt
    {
        public static int[] Face(DateTime t, bool h24, int hourColor, int minuteColor)
        {
            return Face(t, h24, hourColor, minuteColor, false, false);   // right-aligned, so hour and minute digits line up
        }

        // hoursLeft / minutesLeft: push that row against the left edge instead of the right
        public static int[] Face(DateTime t, bool h24, int hourColor, int minuteColor, bool hoursLeft, bool minutesLeft)
        {
            var px = new int[100];
            int h = h24 ? t.Hour : (t.Hour % 12 == 0 ? 12 : t.Hour % 12);
            Draw(px, h24 ? h.ToString("00") : h.ToString(), 0, hourColor, hoursLeft);
            Draw(px, t.Minute.ToString("00"), 5, minuteColor, minutesLeft);
            return px;
        }

        static void Draw(int[] px, string text, int y0, int color, bool left)
        {
            int x0 = left ? 0 : 10 - (text.Length * 4 - 1);
            foreach (char ch in text)
            {
                string g = WeatherArt.Font[ch];
                for (int k = 0; k < 15; k++)
                    if (g[k] == '1') px[(y0 + k / 3) * 10 + x0 + k % 3] = color;
                x0 += 4;
            }
        }
    }

    // Settings live next to the exe (portable); fall back to %APPDATA% if that folder is read-only.
    class Settings
    {
        public string Mac = ""; public int Channel = 0; public int Brightness = 255; public bool SyncOnConnect = true;
        public bool Rotate = false; public int ClockMinutes = 2; public int WeatherMinutes = 1; public bool Fahrenheit = true;
        public string Place = "New York, New York"; public double Lat = 40.71427, Lon = -74.00597;
        public bool MinimizeToTray = true;
        public int WeatherFlip = 5; public bool AnimatedIcons = true; public int AnimDelay = 5; public string ThemeMode = "system"; public bool ThermoFahrenheit = true; public bool SteadyClock = true; public bool StartInTray = false;
        public int Clock24 = -1;            // -1 = follow Windows, 0 = 12-hour, 1 = 24-hour
        public int LavaTheme = -1;          // -1 = a new color theme every minute, else a fixed one
        public bool LavaPuddle = true, BounceTemp = true, OnlyOnItems = false;
        public bool AudioAux = true;        // audio input on connect: AUX jack (the box falls back to Bluetooth with no cable)
        public readonly Dictionary<string, int> AnimSpeeds = new Dictionary<string, int>();   // live animation id -> speed 1..10
        public const int DefaultSpeed = 6;
        // the Now page's quick-pick buttons: "clock", "weather", a live animation id, or "mode<n>"
        public const int ShowNowSlots = 8;
        public static readonly string[] DefaultShowNow = { "clock", "weather", "lavaclock", "fire", "bricks", "lava", "stars", "laos" };
        public string[] ShowNow = (string[])DefaultShowNow.Clone();
        // the alarm and sleep timer as last sent (the box can't be asked for them)
        public int AlarmHour = 7, AlarmMinute = 0, AlarmScene = 0, SleepMinutes = 30, SleepScene = 0;
        public bool AlarmOn = false, SleepOn = false;

        public int SpeedOf(string id)
        {
            lock (AnimSpeeds) { int v; return AnimSpeeds.TryGetValue(id, out v) ? v : DefaultSpeed; }
        }

        public void SetSpeed(string id, int v) { lock (AnimSpeeds) AnimSpeeds[id] = Math.Max(1, Math.Min(10, v)); }
        public List<PlayItem> Playlist = PlayItem.Defaults();
        // other data files (favorites) go wherever the settings file lives
        public static string DataFile(string name)
        {
            return Path.Combine(Path.GetDirectoryName(File.Exists(FallbackPath) && !File.Exists(PrimaryPath) ? FallbackPath : PrimaryPath), name);
        }

        static string PrimaryPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AuraBoxControl.ini"); } }
        static string FallbackPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraBoxControl", "AuraBoxControl.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            string path = File.Exists(PrimaryPath) ? PrimaryPath : FallbackPath;
            return File.Exists(path) ? FromLines(File.ReadAllLines(path)) : s;
        }

        // settings from ini lines; keys it doesn't know are skipped
        public static Settings FromLines(IEnumerable<string> lines)
        {
            var s = new Settings();
            bool hasPlaylist = false;
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                int n;
                if (k == "mac") s.Mac = v;
                else if (k == "channel" && int.TryParse(v, out n)) s.Channel = n;
                else if (k == "brightness" && int.TryParse(v, out n)) s.Brightness = n;
                else if (k == "sync_on_connect") s.SyncOnConnect = v != "0";
                else if (k == "rotate") s.Rotate = v != "0";
                else if (k == "clock_minutes" && int.TryParse(v, out n)) s.ClockMinutes = n;
                else if (k == "weather_minutes" && int.TryParse(v, out n)) s.WeatherMinutes = n;
                else if (k == "fahrenheit") s.Fahrenheit = v != "0";
                else if (k == "place") s.Place = v;
                else if (k == "minimize_to_tray") s.MinimizeToTray = v != "0";
                else if (k == "lat") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s.Lat);
                else if (k == "weather_flip" && int.TryParse(v, out n)) s.WeatherFlip = n;
                else if (k == "animated_icons") s.AnimatedIcons = v != "0";
                else if (k == "anim_delay" && int.TryParse(v, out n)) s.AnimDelay = n;
                else if (k == "theme") s.ThemeMode = v;
                else if (k == "thermo_f") s.ThermoFahrenheit = v != "0";
                else if (k == "steady_clock") s.SteadyClock = v != "0";
                else if (k == "start_tray") s.StartInTray = v != "0";
                else if (k == "clock_24" && int.TryParse(v, out n)) s.Clock24 = n;
                else if (k == "lava_theme" && int.TryParse(v, out n)) s.LavaTheme = n;
                else if (k == "lava_puddle") s.LavaPuddle = v != "0";
                else if (k == "bounce_temp") s.BounceTemp = v != "0";
                else if (k == "only_on_items") s.OnlyOnItems = v != "0";
                else if (k == "audio_aux") s.AudioAux = v != "0";
                else if (k == "alarm" || k == "sleep")   // alarm=hour:minute:scene:on, sleep=minutes:scene:on
                {
                    string[] f = v.Split(':'); int[] nums = new int[f.Length];
                    bool ok = true;
                    for (int i = 0; i < f.Length; i++) ok &= int.TryParse(f[i], out nums[i]);
                    if (ok && k == "alarm" && f.Length == 4) { s.AlarmHour = nums[0]; s.AlarmMinute = nums[1]; s.AlarmScene = nums[2]; s.AlarmOn = nums[3] != 0; }
                    if (ok && k == "sleep" && f.Length == 3) { s.SleepMinutes = nums[0]; s.SleepScene = nums[1]; s.SleepOn = nums[2] != 0; }
                }
                else if (k == "show_now")
                {
                    string[] ids = v.Split(',');
                    for (int i = 0; i < ShowNowSlots && i < ids.Length; i++) if (ids[i].Trim().Length > 0) s.ShowNow[i] = ids[i].Trim();
                }
                else if (k == "anim_speeds")
                    foreach (string part in v.Split(','))
                    {
                        string[] f = part.Split(':');
                        if (f.Length == 2 && int.TryParse(f[1], out n)) s.SetSpeed(f[0], n);
                    }
                else if (k == "playlist") { s.Playlist = PlayItem.Parse(v); hasPlaylist = true; }
                else if (k == "lon") double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s.Lon);
            }
            if (!hasPlaylist)   // older ini: carry over the clock/weather minutes
            {
                PlayItem.Find(s.Playlist, "clock").Seconds = s.ClockMinutes * 60;
                PlayItem.Find(s.Playlist, "weather").Seconds = s.WeatherMinutes * 60;
            }
            return s;
        }

        public void Save()
        {
            string text = Text();
            try { File.WriteAllText(PrimaryPath, text); }
            catch (Exception)
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)); File.WriteAllText(FallbackPath, text); }
                catch (Exception) { }
            }
        }

        // the ini text (also the first part of a settings backup)
        public string Text()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string text = "mac=" + Mac + "\r\nchannel=" + Channel + "\r\nbrightness=" + Brightness +
                          "\r\nsync_on_connect=" + (SyncOnConnect ? 1 : 0) +
                          "\r\nrotate=" + (Rotate ? 1 : 0) + "\r\nclock_minutes=" + ClockMinutes + "\r\nweather_minutes=" + WeatherMinutes +
                          "\r\nfahrenheit=" + (Fahrenheit ? 1 : 0) + "\r\nplace=" + Place +
                          "\r\nminimize_to_tray=" + (MinimizeToTray ? 1 : 0) +
                          "\r\nlat=" + Lat.ToString(inv) + "\r\nlon=" + Lon.ToString(inv) +
                          "\r\nweather_flip=" + WeatherFlip + "\r\nanimated_icons=" + (AnimatedIcons ? 1 : 0) + "\r\nanim_delay=" + AnimDelay +
                          "\r\ntheme=" + ThemeMode + "\r\nthermo_f=" + (ThermoFahrenheit ? 1 : 0) + "\r\nsteady_clock=" + (SteadyClock ? 1 : 0) + "\r\nstart_tray=" + (StartInTray ? 1 : 0) +
                          "\r\nclock_24=" + Clock24 + "\r\nlava_theme=" + LavaTheme + "\r\nlava_puddle=" + (LavaPuddle ? 1 : 0) +
                          "\r\nbounce_temp=" + (BounceTemp ? 1 : 0) + "\r\nonly_on_items=" + (OnlyOnItems ? 1 : 0) + "\r\naudio_aux=" + (AudioAux ? 1 : 0) + "\r\nanim_speeds=" + SpeedsText() + "\r\nshow_now=" + string.Join(",", ShowNow) +
                          "\r\nalarm=" + AlarmHour + ":" + AlarmMinute + ":" + AlarmScene + ":" + (AlarmOn ? 1 : 0) +
                          "\r\nsleep=" + SleepMinutes + ":" + SleepScene + ":" + (SleepOn ? 1 : 0) +
                          "\r\nplaylist=" + PlayItem.Format(Playlist) + "\r\n";
            return text;
        }

        string SpeedsText()
        {
            var parts = new List<string>();
            lock (AnimSpeeds) foreach (var kv in AnimSpeeds) parts.Add(kv.Key + ":" + kv.Value);
            return string.Join(",", parts.ToArray());
        }
    }

    class PixelGrid : Control
    {
        public int[] Pixels = new int[100];
        public int Pen = 1;
        public bool ReadOnly;
        public event EventHandler Changed;
        int Cell { get { return Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height) / 10); } }

        public PixelGrid() : this(34) { }
        public PixelGrid(int cell)
        {
            DoubleBuffered = true;
            Size = new Size(cell * 10, cell * 10);
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            for (int k = 0; k < 100; k++)
            {
                var r = new Rectangle((k % 10) * Cell + 1, (k / 10) * Cell + 1, Cell - 2, Cell - 2);
                using (var b = new SolidBrush(Proto.Palette[Pixels[k]])) e.Graphics.FillRectangle(b, r);
                if (Pixels[k] == 0) using (var p = new Pen(Color.FromArgb(45, 45, 45))) e.Graphics.DrawRectangle(p, r);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (!ReadOnly) { Capture = true; PaintAt(e); } }
        protected override void OnMouseMove(MouseEventArgs e) { if (!ReadOnly && e.Button != MouseButtons.None) PaintAt(e); }

        void PaintAt(MouseEventArgs e)
        {
            int x = e.X / Cell, y = e.Y / Cell;
            if (x < 0 || y < 0 || x >= 10 || y >= 10) return;
            int c = e.Button == MouseButtons.Right ? 0 : Pen;
            if (Pixels[y * 10 + x] == c) return;
            Pixels[y * 10 + x] = c;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        public void SetAll(int[] px) { Pixels = (int[])px.Clone(); Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    }
}
