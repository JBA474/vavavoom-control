// VaVaVoom Control - portable Windows app for Divoom pixel displays (AuraBox, Tivoo) over Bluetooth SPP.
// Build (no SDK needed): tools\build.cmd  ->  VaVaVoomControl.exe (in the project folder)
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
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("VaVaVoom Control")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]

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

        // a picture's side: 10 (palette indexes), 16 or 128 (RGB colors)
        public static int Side(int[] px) { return px.Length == 256 ? 16 : px.Length == 16384 ? 128 : 10; }

        public static Color ColorAt(int[] px, int k)
        {
            return px.Length != 100 ? Color.FromArgb(px[k] >> 16 & 0xFF, px[k] >> 8 & 0xFF, px[k] & 0xFF) : Palette[px[k] & 7];
        }

        // a 128 x 128 picture made 16 x 16: each pixel the average of the 8 x 8 it covers
        public static int[] Shrink128(int[] px)
        {
            var o = new int[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int yy = 0; yy < 8; yy++)
                        for (int xx = 0; xx < 8; xx++) { int c = px[(y * 8 + yy) * 128 + x * 8 + xx]; r += c >> 16 & 255; g += c >> 8 & 255; b += c & 255; }
                    o[y * 16 + x] = (r / 64) << 16 | (g / 64) << 8 | b / 64;
                }
            return o;
        }

        // cell: pixel size for a 10 x 10 picture; a 16 x 16 one is drawn the same overall size. AuraBox pictures
        // (10 x 10) get its LED look: each pixel lit in the middle and darker toward its edges.
        public static Bitmap Render(int[] px, int cell)
        {
            int n = Side(px), size = 10 * cell, c = Math.Max(1, size / n);
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(20, 20, 20));
                if (n > size)
                {   // bigger than the picture (128 x 128): each pixel of it shows the nearest one
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            var col = ColorAt(px, (y * n / size) * n + x * n / size);
                            bmp.SetPixel(x, y, col.R + col.G + col.B == 0 ? Color.FromArgb(40, 40, 40) : col);
                        }
                    return bmp;
                }
                int gap = c > 3 ? 1 : 0, off = (size - c * n) / 2;
                for (int k = 0; k < n * n; k++)
                {
                    var col = ColorAt(px, k);
                    var r = new Rectangle(off + (k % n) * c, off + (k / n) * c, c - gap, c - gap);
                    if (n == 10 && col.R + col.G + col.B > 0) { Led(g, r, col); continue; }
                    using (var b = new SolidBrush(col.R + col.G + col.B == 0 ? Color.FromArgb(40, 40, 40) : col)) g.FillRectangle(b, r);
                }
            }
            return bmp;
        }

        // one AuraBox LED: its color full in the middle, fading to a third toward the edges and darkest in the corners
        // (the box's diffuser lights each pixel from a point behind its center)
        public static void Led(Graphics g, Rectangle r, Color c)
        {
            if (r.Width < 4 || r.Height < 4) { using (var b = new SolidBrush(c)) g.FillRectangle(b, r); return; }
            using (var path = new GraphicsPath())
            {
                float gx = r.Width * 0.2f, gy = r.Height * 0.2f;
                path.AddEllipse(r.X - gx, r.Y - gy, r.Width + 2 * gx, r.Height + 2 * gy);
                using (var b = new PathGradientBrush(path))
                {
                    b.CenterPoint = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
                    b.CenterColor = Color.FromArgb(c.R + (255 - c.R) / 6, c.G + (255 - c.G) / 6, c.B + (255 - c.B) / 6);   // a touch hotter at the core
                    b.SurroundColors = new[] { Color.FromArgb(c.R / 4, c.G / 4, c.B / 4) };
                    b.FocusScales = new PointF(0.3f, 0.3f);   // a small bright core before the falloff
                    g.FillRectangle(b, r);
                }
            }
        }

        // a 10 x 10 picture stretched to 16 x 16 (some rows and columns doubled) as RGB; a 16 x 16 one as it is
        public static int[] ToRgb16(int[] px)
        {
            if (px.Length == 256) return px;
            if (px.Length == 16384) return Shrink128(px);
            var o = new int[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++) o[y * 16 + x] = Palette[px[(y * 10 / 16) * 10 + x * 10 / 16] & 7].ToArgb() & 0xFFFFFF;
            return o;
        }

        // a 16 x 16 color picture squeezed onto the AuraBox: 10 x 10 in the 8 colors. Each new pixel covers 1.6 x 1.6
        // old ones: every covered pixel is snapped to the 8 colors and counts by how much of it is covered. The lit
        // color with the most weight wins when lit pixels cover at least a third of the area, so one-pixel lines and
        // outlines survive (sampling a single pixel loses them).
        public static int[] To10(int[] px)
        {
            if (px.Length == 16384) px = Shrink128(px);
            if (px.Length != 256) return px;
            var o = new int[100];
            // a muted gray filling much of the picture is its background: off, not bright white (the box only has
            // full-strength colors); the same gray in a smaller part (a robot's body) still shows
            var counts = new Dictionary<int, int>();
            int bg = -1, most = 0;
            foreach (int c in px) { int n; counts.TryGetValue(c, out n); counts[c] = ++n; if (n > most) { most = n; bg = c; } }
            if (most < 256 * 2 / 5 || !Muted(bg)) bg = -1;
            var snapped = new int[256];
            for (int k = 0; k < 256; k++) snapped[k] = px[k] == bg ? 0 : Snap8(px[k]);
            var weight = new double[8];
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                {
                    Array.Clear(weight, 0, 8);
                    double y0 = y * 1.6, y1 = y0 + 1.6, x0 = x * 1.6, x1 = x0 + 1.6;
                    for (int sy = (int)y0; sy < y1 && sy < 16; sy++)
                    {
                        double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                        for (int sx = (int)x0; sx < x1 && sx < 16; sx++)
                            weight[snapped[sy * 16 + sx]] += wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                    }
                    int best = 0; double lit = 0;
                    for (int c = 1; c < 8; c++) { lit += weight[c]; if (weight[c] > weight[best] || best == 0) best = c; }
                    if (lit >= 1.6 * 1.6 / 3) o[y * 10 + x] = best;
                }
            return o;
        }

        // a dim, nearly gray color
        static bool Muted(int c)
        {
            int r = c >> 16 & 0xFF, g = c >> 8 & 0xFF, b = c & 0xFF, m = Math.Max(r, Math.Max(g, b)), lo = Math.Min(r, Math.Min(g, b));
            return m < 140 && m - lo < 45;
        }

        // one 0xRRGGBB color to the nearest of the AuraBox's 8 (off when dark)
        static int Snap8(int c)
        {
            int r = c >> 16 & 0xFF, g = c >> 8 & 0xFF, b = c & 0xFF, m = Math.Max(r, Math.Max(g, b));
            if (m < 60) return 0;
            double t = m * 0.55;
            return (r >= t ? 1 : 0) | (g >= t ? 2 : 0) | (b >= t ? 4 : 0);
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

    // A Divoom display on a Bluetooth serial link. The app draws in 10 x 10 palette pictures (Proto.Palette indexes);
    // each kind of device sends them its own way. AuraBox: 10 x 10, 8 colors. Tivoo: 16 x 16, full color.
    abstract class Device
    {
        Socket sock;
        readonly object gate = new object();
        public bool Connected { get { return sock != null; } }
        public abstract string Kind { get; }   // "aurabox" or "tivoo" (saved in the settings)
        public abstract int Size { get; }      // pixels across
        public double BrightnessScale = 1;     // full brightness on the slider gives this share of the device's own full (Ditoo Pro)

        // a device for a paired Bluetooth name or a saved kind
        public static Device Create(string nameOrKind)
        {
            string kind = KindOf(nameOrKind);
            return kind == "tiivoo2" ? new Tiivoo2() : kind == "tivoo" ? (Device)new Tivoo() : new AuraBox();
        }

        public static string KindOf(string nameOrKind)
        {
            string n = (nameOrKind ?? "").ToLowerInvariant();
            if (n.Contains("tiivoo")) return "tiivoo2";   // the Tiivoo 2 pairs as "Divoom Tiivoo 2-Audio"
            foreach (string k in new[] { "tivoo", "timebox evo", "timebox-evo", "pixoo", "ditoo" }) if (n.Contains(k)) return "tivoo";
            return "aurabox";
        }

        // a Divoom display by its Bluetooth name (the device list leaves out phones, headphones and the like)
        public static bool IsDisplay(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            return KindOf(n) == "tivoo" || n.Contains("aurabox") || n.Contains("timebox") || n.Contains("divoom");
        }

        // channel 0 = let Windows look up the serial-port service (SDP)
        // the serial channels to try, in order (null: the Windows lookup, then 1 to 6)
        protected virtual int[] Channels { get { return null; } }

        public int Connect(ulong addr, int preferred)
        {
            var tries = new List<int>();
            if (Channels != null) tries.AddRange(Channels);
            else
            {
                tries.Add(0);
                if (preferred > 0) tries.Add(preferred);
                for (int c = 1; c <= 6; c++) if (!tries.Contains(c)) tries.Add(c);
            }
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
                    try { OnConnected(); } catch (Exception) { }
                    return ch;
                }
                catch (Exception e) { last = e; s.Close(); }
            }
            throw new IOException("Could not open the serial channel: " + (last == null ? "" : last.Message));
        }

        // the device greets and acknowledges every command; read them so its replies never back up (a device that
        // answers back looks at them in Received)
        void StartDrain(Socket s)
        {
            var t = new Thread(delegate ()
            {
                var buf = new byte[1024];
                try { int n; while ((n = s.Receive(buf)) > 0) Received(buf, n); }
                catch (Exception) { }
            }) { IsBackground = true };
            t.Start();
        }

        protected virtual void Received(byte[] buf, int n) { }

        // just connected, its reader running: a device that wants to be asked something first does it here
        protected virtual void OnConnected() { }

        // a framed command sent as it is, without the rest after it (for many pieces in a row)
        protected void SendRaw(byte[] frame)
        {
            lock (gate)
            {
                if (sock == null) throw new IOException("Not connected");
                sock.Send(frame);
            }
        }

        public void Close()
        {
            lock (gate) { if (sock != null) { try { sock.Close(); } catch { } sock = null; } }
        }

        // one framed command, in pieces of at most `piece` bytes; then a short rest so the device keeps up
        protected void SendFrame(byte[] frame, int piece)
        {
            lock (gate)
            {
                if (sock == null) throw new IOException("Not connected");
                for (int at = 0; at < frame.Length; at += piece)
                {
                    int n = Math.Min(piece, frame.Length - at);
                    sock.Send(frame, at, n, SocketFlags.None);
                }
                Thread.Sleep(50);
            }
        }

        // what the device shows now, for the app's preview: a picture, or the name of a built-in mode
        public volatile int[] LastImage;
        public volatile string LastMode;
        protected volatile Action lastShow;   // repeats the last picture/mode/animation command (see Redisplay)

        // put back what the device showed before something temporary covered it (a notification icon)
        public void Redisplay() { var a = lastShow; if (a != null) a(); }

        protected void Remember(int[] img, string mode, Action again) { LastImage = img; LastMode = mode; lastShow = again; }

        protected static byte[] TimeData()
        {
            DateTime t = DateTime.Now;
            return new byte[] { (byte)(t.Year % 100), (byte)(t.Year / 100), (byte)t.Month, (byte)t.Day,
                (byte)t.Hour, (byte)t.Minute, (byte)t.Second, (byte)((int)t.DayOfWeek + 1) };
        }

        public abstract void SetTime();
        public abstract void SetMode(int m);          // a built-in mode, numbered as on the AuraBox (Proto.ModeNames)
        public abstract void SetBrightness(int v);    // 0 - 255
        public abstract void SetColor(int c);         // the whole screen in one palette color
        public abstract void SetTempUnit(bool fahrenheit);
        public abstract void Notify(int code);        // Proto.NotifyNames
        public abstract void ShowImage(int[] px);     // 100 palette indexes, row by row
        public abstract void ShowAnimation(List<int[]> frames, int delay);
        public virtual void SetAudioInput(bool aux) { }
        public virtual void SetAlarm(int hour, int minute, int scene, bool on) { }
        public virtual void SetSleep(int minutes, int scene, bool on) { }
    }

    class AuraBox : Device
    {
        public override string Kind { get { return "aurabox"; } }
        public override int Size { get { return 10; } }

        void Send(byte cmd, byte[] data) { SendFrame(Proto.Frame(cmd, data), int.MaxValue); }

        public override void SetTime() { Send(Proto.SetTime, TimeData()); }

        public override void SetMode(int m)
        {
            if (m == 0) { ShowClock(); return; }
            Send(Proto.SetMode, new[] { (byte)m });
            Remember(null, m >= 0 && m < Proto.ModeNames.Length ? Proto.ModeNames[m] : "Mode " + m, () => SetMode(m));
        }
        public override void SetBrightness(int v) { Send(Proto.Brightness, new[] { (byte)Math.Max(0, Math.Min(255, v)) }); }
        public override void SetColor(int c)
        {
            Send(Proto.SetColor, new[] { (byte)(c & 7) });
            var px = new int[100];
            for (int k = 0; k < 100; k++) px[k] = c & 7;
            Remember(px, null, () => SetColor(c));
        }
        public override void SetTempUnit(bool fahrenheit) { Send(Proto.TempUnit, new[] { (byte)(fahrenheit ? 1 : 0) }); }
        // audio source: 0 = Bluetooth, 2 = line-in (the box refuses line-in unless a cable is in the AUX jack)
        public override void SetAudioInput(bool aux) { Send(Proto.ChangeMode, new[] { (byte)(aux ? 2 : 0) }); }
        public override void Notify(int code) { Send(Proto.Notify, new[] { (byte)code }); }
        public override void SetAlarm(int hour, int minute, int scene, bool on)
        {
            // on = 0xFF, as github.com/d03n3rfr1tz3/hass-divoom sends it (the box ignored 1)
            Send(Proto.AlarmScene, new[] { (byte)hour, (byte)minute, (byte)scene, (byte)(on ? 0xFF : 0) });
        }
        public override void SetSleep(int minutes, int scene, bool on)
        {
            Send(Proto.SleepTime, new[] { (byte)minutes, (byte)scene, (byte)(on ? 0xFF : 0) });
        }

        public override void ShowImage(int[] px)
        {
            px = Proto.To10(px);
            var d = new List<byte> { 0, 10, 10, 4 };
            d.AddRange(Proto.Pack(px));
            Send(Proto.SetImage, d.ToArray());
            var shown = (int[])px.Clone();
            Remember(shown, null, () => ShowImage(shown));
        }

        public const int MaxFrames = 12;   // the most the box holds

        // the built-in clock in 12- or 24-hour time and a palette color (view 00, 24-hour flag, R G B)
        public static volatile bool ClockH24 = true;
        public static volatile int ClockColor = 7;
        public void ShowClock()
        {
            var c = Proto.Palette[ClockColor & 7];
            Send(Proto.SetMode, new[] { (byte)0, (byte)(ClockH24 ? 1 : 0), c.R, c.G, c.B });
            Remember(null, "Clock", ShowClock);
        }

        public override void ShowAnimation(List<int[]> frames, int delay)
        {
            for (int n = 0; n < frames.Count && n < MaxFrames; n++)
            {
                var d = new List<byte> { 0, 10, 10, 4, (byte)n, (byte)delay };   // delay in tenths of a second
                d.AddRange(Proto.Pack(Proto.To10(frames[n])));
                Send(Proto.SetAnimation, d.ToArray());
            }
            var copy = new List<int[]>(frames);
            Remember(frames.Count > 0 ? (int[])frames[0].Clone() : LastImage, null, () => ShowAnimation(copy, delay));
        }
    }

    // Divoom Tivoo (also Timebox Evo / Pixoo / Ditoo): 16 x 16, full color, newer protocol (from the Divoom app
    // 3.8.50 and github.com/RomRider/node-divoom-timebox-evo PROTOCOL.md):
    //   frame = 01 | len(LE16) | cmd data... | sum(LE16) | 02, no escaping; len = cmd + data + 2, sum covers len..data
    //   long frames go out in pieces of at most 666 bytes
    class Tivoo : Device
    {
        public override string Kind { get { return "tivoo"; } }
        public override int Size { get { return 16; } }

        public static byte[] Frame(byte cmd, byte[] data)
        {
            int len = data.Length + 3;
            var f = new List<byte> { 1, (byte)(len & 0xFF), (byte)(len >> 8), cmd };
            f.AddRange(data);
            int sum = 0;
            for (int i = 1; i < f.Count; i++) sum += f[i];
            f.Add((byte)(sum & 0xFF)); f.Add((byte)((sum >> 8) & 0xFF)); f.Add(2);
            return f.ToArray();
        }

        void Send(byte cmd, byte[] data) { SendFrame(Frame(cmd, data), 666); }

        public override void SetTime() { Send(0x18, TimeData()); }
        public override void SetBrightness(int v) { Send(0x74, new[] { (byte)Math.Round(Math.Max(0, Math.Min(255, v)) * 100 / 255.0 * BrightnessScale) }); }
        public override void SetTempUnit(bool fahrenheit) { Send(0x2B, new[] { (byte)(fahrenheit ? 1 : 0) }); }
        public override void Notify(int code) { Send(0x50, new[] { (byte)(code >= 8 ? code + 1 : code) }); }   // as the Divoom app numbers them

        // the Tivoo's own screens, standing in for the AuraBox modes of the same number
        public override void SetMode(int m)
        {
            byte[] d;
            switch (m)
            {
                case 1: d = ClockData(true); break;                                  // Temperature: the clock with the temperature
                case 2: d = LightData(Color.White); break;                           // Color Light
                case 3: d = new byte[] { 4, 0 }; break;                              // Sound Light: the music visualizer
                case 4: case 5: case 6: d = new byte[] { 3, (byte)(m - 4) }; break;  // effects
                default: ShowClock(); return;                                        // Clock (and Countdown)
            }
            Send(0x45, d);
            Remember(null, m >= 0 && m < Proto.ModeNames.Length ? Proto.ModeNames[m] : "Mode " + m, () => SetMode(m));
        }

        // ---- the Tivoo's own screens (from github.com/d03n3rfr1tz3/hass-divoom) ----
        public static readonly string[] ClockStyles = { "Full Screen", "Rainbow", "With Box", "Analog Square", "Full Screen Negative", "Analog Round" };
        public static readonly string[] LightEffects = { "Plain", "Love", "Plants", "No Mosquito", "Sleeping" };
        // the clock as set on the Original Modes page (shared by every Tivoo; set from Settings)
        public static volatile int ClockStyle;
        public static volatile bool ClockWeather, ClockTemp, ClockCalendar, Hour24;
        public static int ClockColor = 0xFFFFFF;

        // time channel: 00, 24-hour, style, then show time, weather, temperature, calendar, then the color
        static byte[] ClockData(bool temperature)
        {
            int c = ClockColor;
            return new byte[] { 0, (byte)(Hour24 ? 1 : 0), (byte)ClockStyle, 1, (byte)(ClockWeather ? 1 : 0), (byte)(temperature || ClockTemp ? 1 : 0),
                                (byte)(ClockCalendar ? 1 : 0), (byte)(c >> 16), (byte)(c >> 8), (byte)c };
        }

        public void ShowClock()
        {
            Send(0x2D, new[] { (byte)(Hour24 ? 1 : 0) });
            Send(0x45, ClockData(false));
            Remember(null, "Clock (" + ClockStyles[Math.Max(0, Math.Min(ClockStyles.Length - 1, ClockStyle))] + ")", ShowClock);
        }

        // the weather in full: no time, just the weather picture and temperature the Tivoo draws from SendWeather
        public void ShowWeather()
        {
            int c = ClockColor;
            Send(0x45, new byte[] { 0, (byte)(Hour24 ? 1 : 0), (byte)ClockStyle, 0, 1, 1, 0, (byte)(c >> 16), (byte)(c >> 8), (byte)c });
            Remember(null, "Weather", ShowWeather);
        }

        // the current weather for the Tivoo's clock and weather screens: degrees Celsius and a Divoom weather type
        // (1 clear, 3 cloudy, 5 thunderstorm, 6 rain, 8 snow, 9 fog); fahrenheit sets the unit it shows
        public void SendWeather(int celsius, int type, bool fahrenheit)
        {
            Send(0x5F, new[] { (byte)(sbyte)Math.Max(-128, Math.Min(127, celsius)), (byte)type });
            Send(0x2B, new[] { (byte)(fahrenheit ? 1 : 0) });
        }

        // Open-Meteo weather code -> Divoom weather type
        public static int WeatherType(int code)
        {
            if (code == 0) return 1;
            if (code <= 3) return 3;
            if (code == 45 || code == 48) return 9;
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return 8;
            if (code >= 95) return 5;
            return 6;
        }

        public void ShowLight(Color c, int effect)
        {
            Send(0x45, new byte[] { 1, c.R, c.G, c.B, 100, (byte)effect, 1, 0, 0, 0 });
            Remember(null, "Light: " + LightEffects[Math.Max(0, Math.Min(LightEffects.Length - 1, effect))], () => ShowLight(c, effect));
        }

        public void ShowEffect(int n) { Send(0x45, new byte[] { 3, (byte)n }); Remember(null, "Effect " + (n + 1), () => ShowEffect(n)); }
        public virtual void ShowVisualizer(int n) { Send(0x45, new byte[] { 4, (byte)n }); Remember(null, "Visualizer " + (n + 1), () => ShowVisualizer(n)); }

        // tools: 0 stopwatch, 1 scoreboard, 2 noise meter, 3 countdown
        public void ShowScoreboard(int red, int blue)
        {
            red = Math.Max(0, Math.Min(999, red)); blue = Math.Max(0, Math.Min(999, blue));
            Send(0x72, new byte[] { 1, 1, (byte)red, (byte)(red >> 8), (byte)blue, (byte)(blue >> 8) });
            Remember(null, "Scoreboard " + red + " : " + blue, () => ShowScoreboard(red, blue));
        }
        public void ShowStopwatch() { Send(0x72, new byte[] { 0, 1 }); Remember(null, "Stopwatch", ShowStopwatch); }
        // the Divoom app's stopwatch buttons: 0 pause, 1 start, 2 reset
        public void StopwatchControl(int status) { Send(0x72, new byte[] { 0, (byte)status }); }
        public void ShowNoiseMeter() { Send(0x72, new byte[] { 2, 1 }); Remember(null, "Noise Meter", ShowNoiseMeter); }
        public void NoiseMeterOff() { Send(0x72, new byte[] { 2, 2 }); }
        public void StopCountdown() { Send(0x72, new byte[] { 3, 0, 0, 0 }); }
        public void ShowCountdown(int minutes, int seconds)
        {
            Send(0x72, new byte[] { 3, 1, (byte)Math.Min(99, minutes), (byte)Math.Min(59, seconds) });
            Remember(null, "Countdown " + minutes + ":" + seconds.ToString("00"), null);   // restarting it on its own would be wrong
        }

        // alarm (slot 0, every day): slot, on, hh, mm, weekdays (Sun = 1 ... Sat = 64), mode, trigger, FM frequency, volume
        public override void SetAlarm(int hour, int minute, int scene, bool on)
        {
            Send(0x43, new byte[] { 0, (byte)(on ? 1 : 0), (byte)hour, (byte)minute, 0x7F, (byte)scene, 0, 0, 0, 100 });
        }

        // sleep: minutes, mode, on, FM frequency, volume, light color (R G B), brightness
        public override void SetSleep(int minutes, int scene, bool on)
        {
            Send(0x40, new byte[] { (byte)minutes, (byte)scene, (byte)(on ? 1 : 0), 0, 0, 50, 0xFF, 0x80, 0x20, 40 });
        }

        // built-in games, and their buttons (1 left, 2 right, 3 up, 4 down, 5 OK; 0 = Go)
        public void ShowGame(int n) { Send(0xA0, new byte[] { 1, (byte)n }); Remember(null, "Game " + (n + 1), null); }
        public void GameKey(int key)
        {
            if (key == 0) { Send(0x88, new byte[0]); return; }
            Send(0x17, new[] { (byte)key });
            Thread.Sleep(100);
            Send(0x21, new[] { (byte)key });
        }

        // lighting channel: color, brightness, plain, on
        static byte[] LightData(Color c) { return new byte[] { 1, c.R, c.G, c.B, 100, 0, 1, 0, 0, 0 }; }

        public override void SetColor(int c)
        {
            Send(0x45, LightData(Proto.Palette[c & 7]));
            var px = new int[100];
            for (int k = 0; k < 100; k++) px[k] = c & 7;
            Remember(px, null, () => SetColor(c));
        }

        public override void ShowImage(int[] px)
        {
            var d = new List<byte> { 0, 10, 10, 4 };
            d.AddRange(Picture(Proto.ToRgb16(px), 0));
            Send(0x44, d.ToArray());
            var shown = (int[])Proto.ToRgb16(px).Clone();
            Remember(shown, null, () => ShowImage(shown));
        }

        public const int MaxFrames = 60;

        // an endless loop the Tivoo plays by itself (delay in tenths of a second): all frames, sent in 200-byte packets
        public override void ShowAnimation(List<int[]> frames, int delay) { ShowAnimationMs(frames, Math.Max(20, delay * 100)); }

        public virtual void ShowAnimationMs(List<int[]> frames, int ms)
        {
            if (frames.Count == 0) return;
            if (frames.Count > MaxFrames) frames = frames.GetRange(0, MaxFrames);
            if (frames.Count == 1) { ShowImage(frames[0]); return; }
            int delay = ms;
            var all = new List<byte>();
            foreach (var f in frames) all.AddRange(Picture(Proto.ToRgb16(f), Math.Min(0xFFFF, ms)));
            for (int at = 0, n = 0; at < all.Count; at += 200, n++)
            {
                var d = new List<byte> { (byte)(all.Count & 0xFF), (byte)(all.Count >> 8), (byte)n };
                d.AddRange(all.GetRange(at, Math.Min(200, all.Count - at)));
                Send(0x49, d.ToArray());
            }
            var copy = new List<int[]>(frames);
            Remember((int[])Proto.ToRgb16(frames[0]).Clone(), null, () => ShowAnimationMs(copy, delay));
        }

        // one picture: AA, size, time (ms), 00 (new palette), color count, the colors, then each pixel's color number
        // packed into as few bits as the count needs, lowest bits first
        // color boost for everything the app draws or sends (0 off, 1 vivid, 2 extra vivid; set from Settings). LEDs give
        // light in proportion to the value, so mid-tones and pastels look pale next to a monitor: saturation is pushed
        // up (each channel moved away from the brightest one) and a gamma curve deepens the in-between values.
        // The curve works on each channel next to the pixel's brightest one, so colors get richer without the whole
        // picture going dark; brightness itself gets only a gentle curve (a full 2.1 gamma on every channel crushed
        // navy, dim gray and dark shading to near black on the real Tivoo; changed 2026-11-11).
        public static volatile int Vivid = 1;
        static readonly double[] Saturation = { 1, 1.3, 1.6 }, Gamma = { 1, 1.6, 2.1 }, Darken = { 1, 1.1, 1.25 };

        public static int Boost(int c)
        {
            int v = Vivid;
            if (v <= 0 || v > 2) return c;
            int r = c >> 16 & 0xFF, g = c >> 8 & 0xFF, b = c & 0xFF, m = Math.Max(r, Math.Max(g, b));
            if (m == 0) return 0;
            double k = Saturation[v], top = 255 * Math.Pow(m / 255.0, Darken[v]);
            Func<int, int> ch = x => (int)Math.Round(top * Math.Pow(Math.Max(0, m - (m - x) * k) / m, Gamma[v]));
            return ch(r) << 16 | ch(g) << 8 | ch(b);
        }

        public static byte[] Picture(int[] rgb, int ms)
        {
            if (Vivid > 0) { var o2 = new int[rgb.Length]; for (int i = 0; i < rgb.Length; i++) o2[i] = Boost(rgb[i]); rgb = o2; }
            var colors = new List<int>();
            var index = new int[rgb.Length];
            for (int i = 0; i < rgb.Length; i++)
            {
                int at = colors.IndexOf(rgb[i]);
                if (at < 0) { at = colors.Count; colors.Add(rgb[i]); }
                index[i] = at;
            }
            int bits = 1;
            while ((1 << bits) < colors.Count) bits++;
            var pixels = new List<byte>();
            int acc = 0, have = 0;
            foreach (int i in index)
            {
                acc |= i << have; have += bits;
                while (have >= 8) { pixels.Add((byte)(acc & 0xFF)); acc >>= 8; have -= 8; }
            }
            if (have > 0) pixels.Add((byte)(acc & 0xFF));
            int size = 7 + colors.Count * 3 + pixels.Count;
            var o = new List<byte> { 0xAA, (byte)(size & 0xFF), (byte)(size >> 8), (byte)(ms & 0xFF), (byte)(ms >> 8), 0, (byte)(colors.Count & 0xFF) };
            foreach (int c in colors) { o.Add((byte)(c >> 16)); o.Add((byte)(c >> 8)); o.Add((byte)c); }
            o.AddRange(pixels);
            return o.ToArray();
        }
    }

    // The Tiivoo 2 ("Divoom Tiivoo 2-Audio"): a 128 x 128 color LCD on newer firmware, with its own way of taking
    // pictures (worked out from the Divoom app 3.8.50, which calls it TivooLcd, and tried on the device 2026-11-12).
    // A picture or animation goes up as one upload (command 0x8B): the app announces it (00, total length), the
    // device asks for it (8B 55 00), then it goes in 256-byte pieces (01, total length, piece number, the bytes); the
    // device may ask again for a piece (8B 55 01 <n>) and says when it has it all (BD 55 13). Inside:
    //   16 x 16: 25 <frames> <ms BE16> 01 01 <length BE32> <zstd frame of the RGB pixels>; the device scales it up
    //   128 x 128: 23 <frames> <ms BE16> 08 08, then per frame 01 <length BE32> <JPEG>
    // The Tivoo's own commands (its clock, light and so on) do nothing here, so everything the app shows a Tivoo
    // comes as pictures. An LCD shows colors as they are: no color boost.
    class Tiivoo2 : Tivoo
    {
        public override string Kind { get { return "tiivoo2"; } }
        protected override int[] Channels { get { return new[] { 1 }; } }   // it also offers 10, which takes nothing

        public const int Pixels = 128;
        public const int Wide = 160;   // its screen is 160 x 128: a 128 x 128 picture shows with bars at the sides
        readonly object upload = new object();
        readonly ManualResetEvent asked = new ManualResetEvent(false), received = new ManualResetEvent(false);
        volatile byte[][] pieces;
        readonly List<byte> inbox = new List<byte>();

        // brightness: the Tivoo's own command (0x74, 0-100). Not 0x32, which the community notes give: that is the app's
        // "adjust level", and each one steps the brightness round (50, 100, 0...) whatever level it is given.

        public override void Notify(int code) { }   // no notification icons of its own

        // its own sound and screens (tried on the device 2026-11-12). JSON commands go as command 01 with the JSON text.
        void Json(string json) { SendFrame(Frame(1, Encoding.UTF8.GetBytes(json)), int.MaxValue); }

        // white noise: 8 sounds it mixes, each with its own volume (0-100), for some minutes (0 = until stopped)
        public static readonly string[] NoiseSounds = { "Fan", "Night Crickets", "Campfire", "Ocean Waves", "Rainstorm", "Mountain Stream", "Birds", "Singing Bowl" };
        public void WhiteNoise(int[] volumes, int minutes, bool on)
        {
            var v = new string[8];
            for (int i = 0; i < 8; i++) v[i] = (volumes != null && i < volumes.Length ? Math.Max(0, Math.Min(100, volumes[i])) : 0).ToString();
            Json("{\"Command\":\"WhiteNoise/Set\",\"OnOff\":" + (on ? 1 : 0) + ",\"Time\":" + Math.Max(0, minutes) + ",\"EndStatus\":0,\"Volume\":[" + string.Join(",", v) + "]}");
            if (on) Remember(null, "White Noise", null);
        }

        // FM radio: its radio mode (work mode 1), then the frequency in tenths of a MHz (low two digits, then the rest);
        // back to Bluetooth audio is work mode 0
        public void FmTune(double mhz)
        {
            int f = (int)Math.Round(Math.Max(87.5, Math.Min(108.0, mhz)) * 10);
            SendFrame(Frame(0x05, new byte[] { 1 }), int.MaxValue);
            SendFrame(Frame(0x61, new[] { (byte)(f % 100), (byte)(f / 100) }), int.MaxValue);
            Remember(null, "FM " + (f / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), () => FmTune(mhz));
        }
        public void BluetoothAudio() { SendFrame(Frame(0x05, new byte[] { 0 }), int.MaxValue); }

        // its own screens: song lyrics (for what plays through it) and its photo album
        public void ShowLyrics() { Json("{\"Command\":\"Lyric/Enter\"}"); Remember(null, "Lyrics", ShowLyrics); }
        public void ShowPhotos() { Json("{\"Command\":\"Photo/Enter\"}"); Remember(null, "Photos", ShowPhotos); }

        // the lyrics screen's look: background 0-15 and how each line arrives (LyricEffects)
        public static readonly string[] LyricEffects = { "Mixing", "Fade Out", "Fly Up", "Fly Out to the Left", "Rotation", "No Effect" };
        public void ShowLyrics(int background, int effect)
        {
            Json("{\"Command\":\"Lyric/SetConfig\",\"Background\":" + Math.Max(0, Math.Min(15, background)) + ",\"TextEffect\":" + Math.Max(0, Math.Min(5, effect)) + "}");
            ShowLyrics();
        }

        // a Pomodoro timer (the app's "Tomato Clock"), minutes each; rounds: 0 work only, 1-4 rounds of work and a short
        // rest, a long rest after the last. It holds the screen until it ends or is stopped on the device (tried
        // 2026-11-12: neither the app nor the PC can end it early)
        public void Pomodoro(int work, int shortRest, int longRest, int rounds, bool sound)
        {
            Json("{\"Command\":\"Tomato/Set\",\"TomatoId\":1,\"TomatoName\":\"Focus\",\"TomatoExplain\":\"\",\"WorkTime\":" + Math.Max(1, work) +
                 ",\"ShortRestTime\":" + Math.Max(1, shortRest) + ",\"LongRestTime\":" + Math.Max(1, longRest) + ",\"TimeCyclerType\":" + Math.Max(0, Math.Min(4, rounds)) +
                 ",\"ImageFileId\":\"\",\"SoundOnOff\":" + (sound ? 1 : 0) + ",\"SoundType\":0,\"SoundSystem\":0,\"SoundVoiceFileId\":\"\",\"SoundVolume\":50,\"FinishFlag\":0,\"TargetDate\":0,\"EstimateTime\":0}");
            Thread.Sleep(300);
            Json("{\"Command\":\"Tomato/Start\",\"TomatoId\":1}");
            Remember(null, "Pomodoro " + work + " min", null);
        }

        // its music visualizers (numbered from 0) and its SD card music (work mode 3)
        public override void ShowVisualizer(int n) { Json("{\"Command\":\"Channel/SetEqPosition\",\"EqPosition\":" + Math.Max(0, n) + "}"); Remember(null, "Visualizer " + (n + 1), () => ShowVisualizer(n)); }
        public void SdCardMusic() { SendFrame(Frame(0x05, new byte[] { 3 }), int.MaxValue); Remember(null, "SD Card Music", null); }

        // a clock face from Divoom's clock store, by its number (ClockStore). The device keeps only the number: it then
        // asks for the face, and Received fetches each part from Divoom the way the phone app does
        public void ShowClockFace(int id, string name)
        {
            // reset first: it then asks for the face's layout again, which names its pictures afresh (FileTag), so a
            // picture it kept broken from before is fetched again
            Json("{\"Command\":\"Channel/ResetClock\",\"ClockId\":" + id + "}");
            Thread.Sleep(300);
            ClockChannel();
            Json("{\"Command\":\"Channel/SetClockSelectId\",\"ClockId\":" + id + "}");
            Remember(null, "Clock: " + name, () => ShowClockFace(id, name));
        }

        // a clock face of our own, kept on this PC and never uploaded: the device asks for its layout (BD 2A) and its
        // background (BD 28/29/30 with the name in DeviceImageUrl) just as for a store face, and Received answers from
        // here. The background is Divoom's full-screen format 1F: 1F <frames> <ms BE16> 08 0A, then each frame as
        // <length BE32> <JPEG 160 x 128>. items: the layout's entries, "x,y,w,h,kind,3,1,font,color,color2,0,"
        // (kind 4: the time as HH:MM in the font)
        class LocalFace { public string Json; public byte[] Name, File; public Dictionary<string, byte[]> Extra; }
        static readonly Dictionary<int, LocalFace> localFaces = new Dictionary<int, LocalFace>();
        public volatile bool LocalFaceAsked, LocalFaceSent;
        public void ShowLocalClock(int id, string title, List<int[]> frames, int ms, string[] items) { ShowLocalClock(id, title, LocalClockFile(frames, ms), items); }

        // a face background in format 1F, under the device's 580 KB
        public static byte[] LocalClockFile(List<int[]> frames, int ms)
        {
            List<byte> blob;
            for (long q = 82; ; q -= 8)
            {
                blob = new List<byte> { 0x1F, (byte)Math.Min(255, frames.Count) };
                blob.AddRange(BE(Math.Max(20, Math.Min(0xFFFF, ms)), 2));
                blob.Add(Pixels / 16); blob.Add(Wide / 16);
                for (int fi = 0; fi < frames.Count && fi < 255; fi++)
                {
                    var jpg = Jpeg(frames[fi], Wide, Pixels, q);
                    blob.AddRange(BE(jpg.Length, 4)); blob.AddRange(jpg);
                }
                if (blob.Count <= 580000 || q <= 50) return blob.ToArray();
            }
        }

        // extra: more pictures the items name (digit pictures for kinds 59-62), by name; each name must change with its
        // picture, as the device keeps files by name (and only about 51 characters of it)
        public void ShowLocalClock(int id, string title, byte[] file, string[] items, Dictionary<string, byte[]> extra = null)
        {
            uint h = 2166136261; foreach (var b in file) h = (h ^ b) * 16777619;   // a new picture gets a new name: the device keeps files it has
            string name = "vavavoom/" + id + "/" + h.ToString("x8");
            // and a new face number: it keeps each face's layout too, by number (id .. id + 49999)
            foreach (var it in items) foreach (var ch in it) h = (h ^ ch) * 16777619;
            if (extra != null) foreach (var k in extra.Keys) foreach (var ch in k) h = (h ^ ch) * 16777619;
            id += (int)(h % 50000);
            var quoted = new List<string>(); foreach (var it in items) quoted.Add("\"" + it + "\"");
            long stamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            string json = "{\"ReturnCode\":0,\"ReturnMessage\":\"\",\"ClockId\":" + id + ",\"Hardware\":600,\"ItemList\":[" + string.Join(",", quoted.ToArray()) +
                          "],\"UpdateTime\":" + stamp + ",\"ItemList2\":[],\"AppList\":[],\"AppNum\":0,\"DeviceImageUrl\":\"" + name +
                          "\",\"LcdIndex\":0,\"DeviceEffect\":0,\"DeviceType\":5,\"PixelDialType\":0,\"ClockType\":2,\"NameCn\":\"\",\"NameEn\":\"" + title +
                          "\",\"ReviewImageUrl\":\"\",\"IsAppCommand\":0,\"LcdIndependence\":0,\"EffectClockId\":824,\"DeviceId\":" + ClockStore.DeviceId +
                          ",\"Command\":\"Device/GetClockInfo\"}";
            lock (localFaces) localFaces[id] = new LocalFace { Json = json, Name = Encoding.UTF8.GetBytes(name), File = file, Extra = extra ?? new Dictionary<string, byte[]>() };
            LocalFaceAsked = LocalFaceSent = false;
            ClockChannel();
            Json("{\"Command\":\"Channel/SetClockSelectId\",\"ClockId\":" + id + "}");
            Remember(FacePreview(file), "Clock: " + title, () => { ClockChannel(); Json("{\"Command\":\"Channel/SetClockSelectId\",\"ClockId\":" + id + "}"); });
        }

        // the app's preview of a face background (1F): its first frame's middle 128 x 128, shrunk, as ShowWide's
        static int[] FacePreview(byte[] file)
        {
            try
            {
                if (file == null || file.Length < 10 || file[0] != 0x1F) return null;
                int len = file[6] << 24 | file[7] << 16 | file[8] << 8 | file[9];
                using (var mem = new MemoryStream(file, 10, len))
                using (var bmp = new System.Drawing.Bitmap(mem))
                {
                    var mid = new int[Pixels * Pixels]; int x0 = (bmp.Width - Pixels) / 2;
                    for (int y = 0; y < Pixels && y < bmp.Height; y++)
                        for (int x = 0; x < Pixels; x++) mid[y * Pixels + x] = bmp.GetPixel(x0 + x, y).ToArgb() & 0xFFFFFF;
                    return Proto.Shrink128(mid);
                }
            }
            catch (Exception) { return null; }
        }

        // its clock channel (Channel/SetIndex 0), from another screen such as a tool; picking a face alone leaves it there
        public void SendJsonForTest(string json) { Json(json); }   // for test tools
        public void ClockChannel() { Json("{\"Command\":\"Channel/SetIndex\",\"SelectIndex\":0}"); }

        // its Divoom registration (id and password): BD 2B 00 asks, once it has settled after connecting (it ignores
        // commands for a moment); the answer arrives in Received
        protected override void OnConnected()
        {
            ClockStore.DeviceId = 0;
            Later(delegate { for (int i = 0; i < 4 && ClockStore.DeviceId == 0; i++) { Thread.Sleep(1200); SendRaw(Frame(0xBD, new byte[] { 0x2B, 0 })); } });
        }

        static void Later(Action a) { ThreadPool.QueueUserWorkItem(delegate { try { a(); } catch (Exception e) { var tr = Trace; if (tr != null) tr("> failed: " + e.GetType().Name + ": " + e.Message); } }); }
        void JsonText(string json) { SendRaw(Frame(1, Encoding.UTF8.GetBytes(json))); }

        // a file for one of its clock faces: BE 00 <length LE32> <name length> <name>; it answers BE 55 00 for the pieces
        // (01 <length LE32> <number LE16> <up to 256 bytes>) or BE 55 01 <number LE16> for one again
        volatile byte[][] facePieces;
        // the gap between a file's pieces. The app's is 40 ms (DeviceFunction U0); tested clean at 15, 10 and 6 ms on
        // 2026-11-13 (a 546 KB picture: 90 s at 40 ms, 42 s at 15, 35 s at 6, where Bluetooth itself is the limit; no
        // pieces missed). Pieces it misses it asks for again (BE 55 01), counted in FaceResends
        public static int FacePaceMs = 10;
        public volatile int FaceResends;
        volatile byte[] piecesFor, localSending;   // the file the pieces are of; the one of our own being sent
        readonly object faceGate = new object();
        void SendFaceFile(byte[] blob, byte[] name)
        {
            lock (faceGate)
            {
                var list = new List<byte[]>();
                for (int i = 0, at = 0; at < blob.Length; i++, at += 256)
                {
                    var d = new List<byte> { 1 }; d.AddRange(LE(blob.Length, 4)); d.AddRange(LE(i, 2));
                    var part = new byte[Math.Min(256, blob.Length - at)]; Array.Copy(blob, at, part, 0, part.Length); d.AddRange(part);
                    list.Add(Frame(0xBE, d.ToArray()));
                }
                facePieces = list.ToArray(); piecesFor = blob;
                var tr = Trace; if (tr != null) tr("> file " + Encoding.UTF8.GetString(name) + ": " + blob.Length + " bytes, " + list.Count + " pieces, head " + BitConverter.ToString(blob, 0, Math.Min(12, blob.Length)));
                var head = new List<byte> { 0 }; head.AddRange(LE(blob.Length, 4)); head.Add((byte)name.Length); head.AddRange(name);
                SendRaw(Frame(0xBE, head.ToArray()));
            }
        }

        // what its clock faces ask the phone app for (the Divoom app's bluetooth/u.java and g.java); Trace, when set,
        // hears each request (for testing)
        public static Action<string> Trace;
        const string FileTag = "grOup";   // stands in for "group" in store pictures' names in layouts (BD 2A answers); another (same length) makes it fetch them all afresh
        void FaceRequest(byte[] f)
        {
            byte cmd = f[4];
            if (cmd == 0x01)
            {   // JSON it wants passed on to Divoom
                string j = Encoding.UTF8.GetString(f, 6, f.Length - 9);
                var m = Regex.Match(j, "\"Command\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return;
                string c = m.Groups[1].Value;
                { var tr = Trace; if (tr != null) tr("> json " + j.Replace("\n", " ").Replace("\t", "").Substring(0, Math.Min(160, j.Length))); }
                if (!DivoomAccount.SignedIn) return;
                if (c == "Channel/DeviceGetMyClock")
                    Later(delegate
                    {
                        var ids = new List<string>(); foreach (var face in ClockStore.Mine()) ids.Add(face.Id.ToString());
                        JsonText("{\"Command\":\"Channel/DeviceGetMyClock\",\"ClockList\":[" + string.Join(",", ids.ToArray()) + "],\"DeviceId\":" + ClockStore.DeviceId + "}");
                    });
                else if (c == "Channel/StoreClockGetList" || c == "Device/GetFileVersion" || c == "Channel/GetOneCustom")
                {
                    string rest = Regex.Replace(j.Trim().TrimStart('{').TrimEnd('}'), "\"(Command|Token|UserId|DeviceId|DevicePassword)\"\\s*:\\s*(\"[^\"]*\"|-?\\d+)\\s*,?", "").Trim().TrimEnd(',');
                    Later(delegate { JsonText(ClockStore.Post(c, rest)); });
                }
                return;
            }
            if (cmd == 0xBE && f.Length > 7)
            {
                var all = facePieces;
                if (all == null) return;
                var forFile = piecesFor;
                if (f[6] == 0) Later(delegate
                {
                    foreach (var p in all) { SendRaw(p); Thread.Sleep(FacePaceMs); }
                    if (forFile != null && forFile == localSending) LocalFaceSent = true;
                });
                else if (f[6] == 1 && f.Length > 9) { FaceResends++; int k = f[7] | f[8] << 8; if (k < all.Length) Later(delegate { SendRaw(all[k]); }); }
                return;
            }
            if (cmd != 0xBD || f.Length < 8) return;
            byte sub = f[6];
            if (sub == 0x2B && f.Length >= 18)
            {   // its registration: id LE32, password LE32, type; none yet (0) and signed in: register it as the app would
                long id = (uint)(f[7] | f[8] << 8 | f[9] << 16 | f[10] << 24);
                int pw = f[11] | f[12] << 8 | f[13] << 16 | f[14] << 24, type = f[15];
                if (id != 0) { ClockStore.DeviceId = id; ClockStore.DevicePassword = pw; }
                else if (DivoomAccount.SignedIn)
                    Later(delegate
                    {
                        long nid; int npw; ClockStore.Register(type, 0, out nid, out npw);
                        ClockStore.DeviceId = nid; ClockStore.DevicePassword = npw;
                        var d = new List<byte> { 0x2B, 1 }; d.AddRange(LE(nid, 4)); d.AddRange(LE(npw, 4));
                        SendRaw(Frame(0xBD, d.ToArray()));
                    });
                return;
            }
            // our own faces (ShowLocalClock): answered from here
            if (sub == 0x2A && f.Length >= 11)
            {
                int id = f[7] | f[8] << 8 | f[9] << 16 | f[10] << 24; LocalFace lf;
                lock (localFaces) localFaces.TryGetValue(id, out lf);
                if (lf != null) { Later(delegate { JsonText(lf.Json); }); return; }
            }
            if ((sub == 0x28 || sub == 0x29 || sub == 0x30) && f.Length > 9)
            {
                int len = Math.Min(f[7], f.Length - 11);
                string asked = Encoding.UTF8.GetString(f, 8, Math.Max(0, len));
                LocalFace lf = null; byte[] extraFile = null;
                lock (localFaces)
                {
                    foreach (var x in localFaces.Values)
                    {
                        if (Encoding.UTF8.GetString(x.Name) == asked) lf = x;
                        byte[] e; if (x.Extra != null && x.Extra.TryGetValue(asked, out e)) extraFile = e;
                    }
                }
                if (extraFile != null)
                {   // one of its digit pictures (or another extra)
                    var nm = Encoding.UTF8.GetBytes(asked);
                    { var tr = Trace; if (tr != null) tr("> asked (" + sub.ToString("X2") + ") " + asked); }
                    Later(delegate { SendFaceFile(extraFile, nm); }); return;
                }
                lock (localFaces)
                {
                    // a picture of ours from before a restart (it keeps asking for one cut off mid-way, and asks for
                    // nothing else meanwhile): the current one under that name, so it can move on
                    if (lf == null && asked.StartsWith("vavavoom/"))
                        foreach (var x in localFaces.Values) lf = new LocalFace { Json = x.Json, Name = Encoding.UTF8.GetBytes(asked), File = x.File };
                }
                if (lf != null) { LocalFaceAsked = true; localSending = lf.File; Later(delegate { SendFaceFile(lf.File, lf.Name); }); return; }
            }
            if (!DivoomAccount.SignedIn || ClockStore.DeviceId == 0) return;
            if (sub == 0x2A && f.Length >= 11)
            {   // a face's layout
                int id = f[7] | f[8] << 8 | f[9] << 16 | f[10] << 24;
                // its pictures under new names ("group" as FileTag): it keeps files by name, including ones that arrived
                // broken (sent too fast, before the 40 ms pace), and never asks for those again. Same length: it keeps only
                // about 51 characters of a name, and Divoom's are about 50
                Later(delegate { JsonText(Regex.Replace(ClockStore.Post("Device/GetClockInfo", "\"ClockId\":" + id), "\\bgroup(\\d+/M\\d+/)", FileTag + "$1")); });
            }
            else if (sub == 0x2D && f.Length >= 11)
            {   // a font it draws a face's text in
                int id = f[7] | f[8] << 8 | f[9] << 16 | f[10] << 24;
                // lowercase "id", as the app sends it (its FontIdsJson has no JSONField name); "Id" gets an SQL error back
                Later(delegate { JsonText(ClockStore.Post("Device/GetSomeFontInfo", "\"FontIds\":[{\"id\":" + id + "}]")); });
            }
            else if ((sub == 0x28 || sub == 0x29 || sub == 0x30) && f.Length > 9)
            {   // a picture: <name length> <file id>; 29 wants the file as it is, the others in its own format
                int len = Math.Min(f[7], f.Length - 11);
                var name = new byte[len]; Array.Copy(f, 8, name, 0, len);
                string fileId = Encoding.UTF8.GetString(name);
                if (fileId.StartsWith(FileTag)) fileId = "group" + fileId.Substring(FileTag.Length);
                { var tr = Trace; if (tr != null) tr("> asked (" + sub.ToString("X2") + ") " + Encoding.UTF8.GetString(name)); }
                Later(delegate
                {
                    var file = ClockStore.File(fileId);
                    var blob = sub == 0x29 ? file : DivoomFiles.ForDevice(file);
                    if (blob != null) SendFaceFile(blob, name);
                    else { var tr = Trace; if (tr != null) tr("> no file for " + fileId + " (type " + (file == null || file.Length == 0 ? -1 : file[0]) + ")"); }
                });
            }
        }

        public override void SetColor(int c)
        {
            var px = new int[100];
            for (int k = 0; k < 100; k++) px[k] = c & 7;
            ShowImage(px);
        }

        // Its screen is 160 x 128; designs are square (Divoom's gallery has no wider ones). They go out full screen: the
        // design exact in the middle (16 x 16 ones enlarged 8 times, crisp), the 16-pixel strips each side a soft,
        // dimmed glow of its own edge colors, fading toward the outer edges.
        static int[] Up16(int[] rgb16)
        {
            var o = new int[Pixels * Pixels];
            for (int y = 0; y < Pixels; y++) for (int x = 0; x < Pixels; x++) o[y * Pixels + x] = rgb16[(y / 8) * 16 + x / 8];
            return o;
        }
        static int[] Fill(int[] px)
        {
            int side = (Wide - Pixels) / 2;
            var o = new int[Wide * Pixels];
            // the glow: the design's outer columns averaged in soft blocks (blurred), mirrored out into the strips
            const int R = 6;
            for (int y = 0; y < Pixels; y++)
            {
                for (int x = 0; x < Pixels; x++) o[y * Wide + side + x] = px[y * Pixels + x];
                for (int s = 0; s < 2; s++)
                    for (int d = 1; d <= side; d++)
                    {
                        int srcX = s == 0 ? Math.Min(Pixels - 1, d - 1) : Math.Max(0, Pixels - d);
                        double rr = 0, gg = 0, bb = 0; int n = 0;
                        for (int yy = Math.Max(0, y - R); yy <= Math.Min(Pixels - 1, y + R); yy++)
                            for (int xx = Math.Max(0, srcX - R); xx <= Math.Min(Pixels - 1, srcX + R); xx++)
                            { int c = px[yy * Pixels + xx]; rr += c >> 16 & 255; gg += c >> 8 & 255; bb += c & 255; n++; }
                        double k = 0.5 * (1 - 0.6 * d / (double)side) / n;
                        int x0 = s == 0 ? side - d : side + Pixels - 1 + d;
                        o[y * Wide + x0] = (int)(rr * k) << 16 | (int)(gg * k) << 8 | (int)(bb * k);
                    }
            }
            return o;
        }
        static List<int[]> Filled(List<int[]> frames)
        {
            var o = new List<int[]>();
            foreach (var f in frames) o.Add(Fill(f.Length == Pixels * Pixels ? f : Up16(Proto.ToRgb16(f))));
            return o;
        }

        public override void ShowImage(int[] px)
        {
            if (px.Length == Wide * Pixels) { ShowWide(new List<int[]> { px }, 1000, 92); return; }
            ShowWide(Filled(new List<int[]> { px }), 1000, 92);
        }

        public override void ShowAnimationMs(List<int[]> frames, int ms)
        {
            if (frames.Count == 0) return;
            if (frames.Count > MaxFrames) frames = frames.GetRange(0, MaxFrames);
            if (frames[0].Length == Wide * Pixels) { ShowWide(frames, ms, 90); return; }
            ShowWide(Filled(frames), ms, 90);
        }

        // full-size pictures: each frame 128 x 128 RGB (0xRRGGBB, row by row); a single frame is a still picture.
        // preview: what the app shows for it (16 x 16)
        public void ShowBig(List<int[]> frames, int ms, int[] preview, long quality = 92)
        {
            if (frames.Count == 0) return;
            if (frames[0].Length == Pixels * Pixels) { ShowWide(Filled(frames), ms, quality); return; }   // full screen
            var blob = new List<byte> { 0x23, (byte)Math.Min(255, frames.Count) };
            blob.AddRange(BE(Math.Max(20, Math.Min(0xFFFF, ms)), 2));
            blob.Add(Pixels / 16); blob.Add(Pixels / 16);
            for (int fi = 0; fi < frames.Count && fi < 255; fi++)
            {
                var f = frames[fi];
                var jpg = Jpeg(f, quality);
                blob.Add(1); blob.AddRange(BE(jpg.Length, 4)); blob.AddRange(jpg);
            }
            Upload(blob.ToArray());
            var copy = new List<int[]>(frames); int delay = ms; var look = preview == null ? null : (int[])preview.Clone();
            Remember(look, null, () => ShowBig(copy, delay, look, quality));
        }

        // full-screen pictures: each frame 160 x 128 RGB (0xRRGGBB, row by row), sent as JPEG, 10 x 8 blocks of 16
        public void ShowWide(List<int[]> frames, int ms, long quality)
        {
            if (frames.Count == 0) return;
            // the device takes about 600 KB in one go (the Divoom app's limit): a busy loop gets a lower JPEG quality
            List<byte> blob;
            for (long q = quality; ; q -= 8)
            {
                blob = new List<byte> { 0x23, (byte)Math.Min(255, frames.Count) };
                blob.AddRange(BE(Math.Max(20, Math.Min(0xFFFF, ms)), 2));
                blob.Add(Pixels / 16); blob.Add(Wide / 16);
                for (int fi = 0; fi < frames.Count && fi < 255; fi++)
                {
                    var jpg = Jpeg(frames[fi], Wide, Pixels, q);
                    blob.Add(1); blob.AddRange(BE(jpg.Length, 4)); blob.AddRange(jpg);
                }
                if (blob.Count <= 580000 || q <= 50) break;
            }
            Upload(blob.ToArray());
            // the app's preview: the middle 128 x 128, shrunk
            var mid = new int[Pixels * Pixels];
            for (int y = 0; y < Pixels; y++) Array.Copy(frames[0], y * Wide + (Wide - Pixels) / 2, mid, y * Pixels, Pixels);
            var copy = new List<int[]>(frames); int delay = ms;
            Remember(Proto.Shrink128(mid), null, () => ShowWide(copy, delay, quality));
        }

        // a loop made for this screen (Loop128): JPEG for smooth color, else Divoom's own lossless format
        public void ShowLoop(List<int[]> frames, int ms, bool photo)
        {
            if (frames.Count == 0) return;
            if (frames[0].Length == Wide * Pixels) { ShowWide(frames, ms, photo ? 82 : 92); return; }
            var look = Proto.Shrink128(frames[0]);
            if (photo) ShowBig(frames, ms, look, 80);
            else ShowDivoom128(Divoom128Writer.File(frames, ms), look);
        }

        // a 128 x 128 Divoom gallery file (Divoom128), sent as it is: 22 <frames> <ms BE16> 08 08 <length BE32>, then the
        // end of each frame (BE32, counted from after this table) and the frames themselves
        public void ShowDivoom128(byte[] file, int[] preview)
        {
            if (file == null || file.Length < 10 || file[0] != 0x1A) return;
            int fileMs; var decoded = Divoom128.Decode(file, out fileMs);
            if (decoded != null) { ShowWide(Filled(decoded), fileMs > 0 ? fileMs : 100, 92); return; }   // full screen; else as it is
            int count = file[1], at = 6;
            var offsets = new List<byte>(); var frames = new List<byte>(); int end = 0;
            for (int f = 0; f < count && at + 4 <= file.Length; f++)
            {
                int len = file[at] << 24 | file[at + 1] << 16 | file[at + 2] << 8 | file[at + 3];
                if (len <= 0 || at + 4 + len > file.Length) break;
                for (int i = 0; i < len; i++) frames.Add(file[at + 4 + i]);
                end += len; offsets.AddRange(BE(end, 4));
                at += 4 + len;
            }
            var blob = new List<byte> { 0x22, (byte)(offsets.Count / 4), file[2], file[3], 8, 8 };
            blob.AddRange(BE(offsets.Count + frames.Count, 4)); blob.AddRange(offsets); blob.AddRange(frames);
            Upload(blob.ToArray());
            var look = preview == null ? null : (int[])preview.Clone();
            Remember(look, null, () => ShowDivoom128(file, look));
        }

        // 16 x 16 frames (or 10 x 10 palette ones), for the device to scale up
        static byte[] Small(List<int[]> frames, int ms)
        {
            var rgb = new byte[frames.Count * 768];
            for (int f = 0; f < frames.Count; f++)
            {
                var px = Proto.ToRgb16(frames[f]);
                for (int k = 0; k < 256; k++) { rgb[f * 768 + k * 3] = (byte)(px[k] >> 16); rgb[f * 768 + k * 3 + 1] = (byte)(px[k] >> 8); rgb[f * 768 + k * 3 + 2] = (byte)px[k]; }
            }
            var z = Zstd.Stored(rgb);
            var blob = new List<byte> { 0x25, (byte)frames.Count };
            blob.AddRange(BE(Math.Max(20, Math.Min(0xFFFF, ms)), 2));
            blob.Add(1); blob.Add(1);
            blob.AddRange(BE(z.Length, 4)); blob.AddRange(z);
            return blob.ToArray();
        }

        static byte[] Jpeg(int[] rgb, long quality) { return Jpeg(rgb, Pixels, Pixels, quality); }
        static byte[] Jpeg(int[] rgb, int w, int h, long quality)
        {
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) bmp.SetPixel(x, y, Color.FromArgb(rgb[y * w + x] | unchecked((int)0xFF000000)));
                System.Drawing.Imaging.ImageCodecInfo enc = null;
                foreach (var e in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()) if (e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) enc = e;
                var ps = new System.Drawing.Imaging.EncoderParameters(1);
                ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                using (var ms = new MemoryStream()) { bmp.Save(ms, enc, ps); return ms.ToArray(); }
            }
        }

        static byte[] BE(long v, int n) { var o = new byte[n]; for (int i = 0; i < n; i++) o[n - 1 - i] = (byte)(v >> (8 * i)); return o; }
        static byte[] LE(long v, int n) { var o = new byte[n]; for (int i = 0; i < n; i++) o[i] = (byte)(v >> (8 * i)); return o; }

        // one upload: announce, wait for the device to ask (a moment longer right after it starts up), send the pieces,
        // then wait for it to say it has them all, so the next upload doesn't run into this one
        void Upload(byte[] blob)
        {
            lock (upload)
            {
                int total = blob.Length;
                var list = new List<byte[]>();
                for (int i = 0, at = 0; at < total; i++, at += 256)
                {
                    var d = new List<byte> { 1 }; d.AddRange(LE(total, 4)); d.AddRange(LE(i, 2));
                    var part = new byte[Math.Min(256, total - at)]; Array.Copy(blob, at, part, 0, part.Length); d.AddRange(part);
                    list.Add(Frame(0x8B, d.ToArray()));
                }
                pieces = list.ToArray();
                asked.Reset(); received.Reset();
                var start = new List<byte> { 0 }; start.AddRange(LE(total, 4));
                SendRaw(Frame(0x8B, start.ToArray()));
                if (!asked.WaitOne(4000))
                {   // not ready yet: once more
                    SendRaw(Frame(0x8B, start.ToArray()));
                    if (!asked.WaitOne(6000)) throw new IOException("The Tiivoo 2 didn't ask for the picture");
                }
                foreach (var p in pieces) { SendRaw(p); Thread.Sleep(6); }
                received.WaitOne(3000);
            }
        }

        // its replies: 01 <len LE16> 04 <command> 55 <data> <checksum> 02
        protected override void Received(byte[] buf, int n)
        {
            lock (inbox)
            {
                for (int i = 0; i < n; i++) inbox.Add(buf[i]);
                while (true)
                {
                    int st = inbox.IndexOf(1);
                    if (st < 0) { inbox.Clear(); return; }
                    if (st > 0) inbox.RemoveRange(0, st);
                    if (inbox.Count < 3) return;
                    int end = (inbox[1] | inbox[2] << 8) + 3;
                    if (end > 4096) { inbox.RemoveAt(0); continue; }   // not a frame start
                    if (inbox.Count <= end) return;
                    var f = inbox.GetRange(0, end + 1).ToArray();
                    inbox.RemoveRange(0, end + 1);
                    if (f.Length < 8 || f[3] != 4 || f[5] != 0x55) continue;
                    var tr = Trace;
                    if (tr != null && f[4] != 0xF7) tr(f[4] == 0x01 ? "json from it: " + Encoding.UTF8.GetString(f, 6, Math.Max(0, f.Length - 9)).Replace("\n", " ").Replace("\t", "")
                                                       : BitConverter.ToString(f, 4, Math.Min(f.Length - 4, 24)));
                    if (f[4] == 0x8B && f[6] == 0) asked.Set();
                    else if (f[4] == 0x8B && f[6] == 1 && f.Length > 9)
                    {
                        int k = f[7] | f[8] << 8; var all = pieces;
                        if (all != null && k < all.Length) ThreadPool.QueueUserWorkItem(delegate { try { SendRaw(all[k]); } catch (Exception) { } });
                    }
                    else if (f[4] == 0xBD && f[6] == 0x13) received.Set();
                    else if (f[4] == 0x01 || f[4] == 0xBD || f[4] == 0xBE) FaceRequest(f);
                }
            }
        }
    }

    // zstd frames that keep the data as it is ("raw" blocks), which any zstd reader takes (RFC 8878)
    static class Zstd
    {
        public static byte[] Stored(byte[] data)
        {
            var o = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD, 0xA0 };   // magic; one segment, 4-byte content size
            for (int i = 0; i < 4; i++) o.Add((byte)(data.Length >> (8 * i)));
            for (int at = 0; ; at += 65536)
            {
                int n = Math.Min(65536, data.Length - at); bool last = at + n >= data.Length;
                int header = (last ? 1 : 0) | (n << 3);   // block type 0: raw
                o.Add((byte)header); o.Add((byte)(header >> 8)); o.Add((byte)(header >> 16));
                for (int i = 0; i < n; i++) o.Add(data[at + i]);
                if (last) break;
            }
            return o.ToArray();
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
        public int Ms;   // a 16 x 16 animation's time per frame (0: use the Draw & Animate delay)
        public string Key;    // a downloaded design: its key in DesignCache (null for built-in designs and favorites)
        public string Look;   // the Gallery's fingerprint of how it looks, worked out once (see MainForm.LookOf)
        public bool IsAnimation { get { return Category == "Animations" || Frames.Count > 1; } }
        public byte[] Raw;    // a 128 x 128 design: its file as Divoom made it (sent to a Tiivoo 2 as it is, and saved)
        public bool Big { get { return Frames.Count > 0 && Frames[0].Length == Divoom128.N * Divoom128.N; } }

        // a copy for favorites or a list
        public GalleryItem CopyAs(string category, string name)
        {
            var copy = new GalleryItem { Category = category, Name = name, Ms = Ms, Raw = Raw };
            foreach (var f in Frames) copy.Frames.Add((int[])f.Clone());
            return copy;
        }

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
            if (p[0] == "D" && p.Length == 5)   // D|category|name|ms|the 128 x 128 Divoom file, base64
            {
                it.Category = p[1]; it.Name = p[2]; int.TryParse(p[3], out it.Ms);
                try { it.Raw = Convert.FromBase64String(p[4]); } catch (FormatException) { return null; }
                int time; var all = Divoom128.Decode(it.Raw, out time);
                if (all == null) return null;
                it.Frames.AddRange(all);
                return it;
            }
            if (p[0] == "R" && p.Length == 5)   // R|category|name|ms|frames: 16 x 16 colors, 768 hex digits a frame
            {
                it.Category = p[1]; it.Name = p[2]; int.TryParse(p[3], out it.Ms);
                foreach (string hex in p[4].Split(','))
                {
                    if (hex.Length != 1536) continue;
                    var px = new int[256];
                    for (int k = 0; k < 256; k++) px[k] = Convert.ToInt32(hex.Substring(6 * k, 6), 16);
                    it.Frames.Add(px);
                }
                return it.Frames.Count > 0 ? it : null;
            }
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
            if (Raw != null && Big)
                return "D|" + category + "|" + Name.Replace("|", "/") + "|" + Ms + "|" + Convert.ToBase64String(Raw);
            if (Frames.Count > 0 && Frames[0].Length == 256)
            {
                var rgb = new List<string>();
                foreach (var f in Frames) { var sb = new StringBuilder(1536); foreach (int c in f) sb.Append(c.ToString("x6")); rgb.Add(sb.ToString()); }
                return "R|" + category + "|" + Name.Replace("|", "/") + "|" + Ms + "|" + string.Join(",", rgb.ToArray());
            }
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

        // Emoji in the text become these pixel pictures (an 8 px font can't draw them legibly). Letters are palette
        // colors (R red, G green, Y yellow, B blue, M magenta, C cyan, W white), "." is off. At most 6 rows, centered
        // on rows 3-8: level with the capitals of the default Tahoma. Emoji lists them in the order the Text page shows.
        public static readonly KeyValuePair<char, string>[] Emoji = {
            new KeyValuePair<char, string>('❤', "Heart"), new KeyValuePair<char, string>('★', "Star"),
            new KeyValuePair<char, string>('☺', "Smiley"), new KeyValuePair<char, string>('☀', "Sun"),
            new KeyValuePair<char, string>('☁', "Cloud"), new KeyValuePair<char, string>('⚡', "Lightning"),
            new KeyValuePair<char, string>('♪', "Music Note"), new KeyValuePair<char, string>('✓', "Check Mark") };
        static readonly Dictionary<char, string[]> Pictures = new Dictionary<char, string[]> {
            { '❤', new[] { ".RR.RR.", "RRRRRRR", "RRRRRRR", ".RRRRR.", "..RRR..", "...R..." } },
            { '★', new[] { "...Y...", "..YYY..", "YYYYYYY", ".YYYYY.", ".YY.YY.", "YY...YY" } },
            { '☺', new[] { ".YYYYY.", "YY.Y.YY", "YYYYYYY", "Y.YYY.Y", "YY...YY", ".YYYYY." } },
            { '☀', new[] { ".Y.Y.Y.", "..YYY..", "YYYYYYY", "..YYY..", ".Y.Y.Y." } },
            { '☁', new[] { "..WW....", ".WWWWWW.", "WWWWWWWW", "WWWWWWWW" } },
            { '⚡', new[] { "..YY", ".YY.", "YYYY", ".YY.", "YY..", "Y..." } },
            { '♪', new[] { "...CC.", "...C.C", "...C..", ".CCC..", "CCCC..", ".CC..." } },
            { '✓', new[] { "......G", ".....GG", "G...GG.", "GG.GG..", ".GGG...", "..G...." } } };
        static char Canon(char ch) { return ch == '♥' ? '❤' : ch; }   // ♥ draws as ❤

        // a picture's cells as a 10-row column list (for the strip, and for the Text page's buttons); on 16 rows
        // each cell is doubled (2 x 2), centered on rows 2-13
        public static List<int[]> PictureColumns(char emoji) { return PictureColumns(emoji, 10); }
        public static List<int[]> PictureColumns(char emoji, int n)
        {
            string[] rows = Pictures[Canon(emoji)];
            int k = n == 16 ? 2 : 1;
            int top = n == 16 ? 2 + (6 - rows.Length) : 3 + (6 - rows.Length) / 2;
            var cols = new List<int[]>();
            for (int x = 0; x < rows[0].Length * k; x++)
            {
                var col = new int[n];
                for (int y = 0; y < rows.Length * k; y++) col[top + y] = "RGYBMCW".IndexOf(rows[y / k][x / k]) + 1;   // "." -> 0, off
                cols.Add(col);
            }
            return cols;
        }

        // the text as columns of n cells (10, or 16 for the Tivoo): 0 = off, Ink = text color, anything else = that palette color
        public static int[,] Strip(string text, string fontName) { return Strip(text, fontName, 10); }
        public static int[,] Strip(string text, string fontName, int n)
        {
            if (string.IsNullOrEmpty(text)) text = " ";
            var cols = new List<int[]>();
            var run = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (!Pictures.ContainsKey(Canon(ch))) { run.Append(ch); continue; }
                AddText(cols, run.ToString(), fontName, n); run.Length = 0;
                cols.Add(new int[n]);
                cols.AddRange(PictureColumns(ch, n));
                cols.Add(new int[n]);
                if (i + 1 < text.Length && text[i + 1] == '️') i++;   // the emoji-style variant selector (as in ❤️)
            }
            AddText(cols, run.ToString(), fontName, n);
            cols.Add(new int[n]); cols.Add(new int[n]);
            var strip = new int[cols.Count, n];
            for (int x = 0; x < cols.Count; x++)
                for (int y = 0; y < n; y++) strip[x, y] = cols[x][y];
            return strip;
        }

        static void AddText(List<int[]> cols, string text, string fontName, int n)
        {
            if (text.Length == 0) return;
            using (var font = new Font(fontName, n == 16 ? 12f : 8f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;   // keep the space before a heart
                Size sz;
                using (var probe = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(probe))
                    sz = Size.Ceiling(g.MeasureString(text, font, PointF.Empty, format));
                int w = Math.Max(1, sz.Width);
                using (var bmp = new Bitmap(w, n))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Black);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    g.DrawString(text, font, Brushes.White, 0, n == 16 ? 0 : 1, format);   // 16 rows: room for descenders
                    for (int x = 0; x < w; x++)
                    {
                        var col = new int[n];
                        for (int y = 0; y < n; y++) if (bmp.GetPixel(x, y).R > 128) col[y] = Ink;
                        cols.Add(col);
                    }
                }
            }
        }

        // offset 0 = blank screen; text enters from the right. A 16-row strip gives a 16 x 16 RGB picture.
        public static int[] Window(int[,] strip, int offset, int color)
        {
            int n = strip.GetLength(1);
            var px = new int[n * n];
            int w = strip.GetLength(0);
            for (int x = 0; x < n; x++)
            {
                int sx = offset + x - n;
                if (sx < 0 || sx >= w) continue;
                for (int y = 0; y < n; y++)
                {
                    int v = strip[sx, y];
                    if (v != 0) px[y * n + x] = v == Ink ? color : v;
                }
            }
            if (n == 16) for (int k = 0; k < px.Length; k++) px[k] = Proto.Palette[px[k] & 7].ToArgb() & 0xFFFFFF;
            return px;
        }
    }

    class WeatherInfo
    {
        public double Temp; public int Code; public bool IsDay; public string Unit; public DateTime Fetched;
        // tomorrow's forecast: high, low and conditions (HasTomorrow false when the forecast didn't come)
        public bool HasTomorrow; public double TomorrowHigh, TomorrowLow; public int TomorrowCode;

        public string Description { get { return Describe(Code, IsDay); } }
        public string TomorrowDescription { get { return Describe(TomorrowCode, true); } }

        public static string Describe(int Code, bool IsDay)
        {
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
                wc.Headers[HttpRequestHeader.UserAgent] = "VaVaVoomControl/1.0";
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
                              "&current=temperature_2m,weather_code,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=2&timezone=auto" +
                              "&temperature_unit=" + (fahrenheit ? "fahrenheit" : "celsius"));
            int at = json.IndexOf("\"current\":{", StringComparison.Ordinal);
            if (at < 0) throw new IOException("Unexpected weather response");
            string cur = json.Substring(at);
            var w = new WeatherInfo { Unit = fahrenheit ? "F" : "C", Fetched = DateTime.Now };
            w.Temp = double.Parse(Field(cur, "temperature_2m"), Inv);
            w.Code = int.Parse(Field(cur, "weather_code"), Inv);
            w.IsDay = Field(cur, "is_day") != "0";
            // tomorrow: the second entry of each daily list
            try
            {
                int d = json.IndexOf("\"daily\":{", StringComparison.Ordinal);
                if (d >= 0)
                {
                    string daily = json.Substring(d);
                    Func<string, string> second = name =>
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(daily, "\"" + name + "\"\\s*:\\s*\\[\\s*([^,\\]]+)\\s*,\\s*([^,\\]]+)");
                        return m.Success ? m.Groups[2].Value.Trim() : null;
                    };
                    string code = second("weather_code"), hi = second("temperature_2m_max"), lo = second("temperature_2m_min");
                    if (code != null && hi != null && lo != null && code != "null")
                    {
                        w.TomorrowCode = (int)double.Parse(code, Inv); w.TomorrowHigh = double.Parse(hi, Inv); w.TomorrowLow = double.Parse(lo, Inv);
                        w.HasTomorrow = true;
                    }
                }
            }
            catch (Exception) { w.HasTomorrow = false; }
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
            int n = Proto.Side(frames[0]), left = n - 1, right = 0, top = n - 1, bottom = 0;   // 10 x 10, or 16 x 16 (WeatherArt16)
            foreach (int[] px in frames)
                for (int k = 0; k < n * n; k++)
                    if (px[k] != 0)
                    {
                        left = Math.Min(left, k % n); right = Math.Max(right, k % n);
                        top = Math.Min(top, k / n); bottom = Math.Max(bottom, k / n);
                    }
            if (right < left) { left = right = top = bottom = 0; }
            loX = -left; hiX = n - 1 - right; loY = -top; hiY = n - 1 - bottom;
            if (hiX - loX < 2) { loX = Math.Min(loX, -1); hiX = Math.Max(hiX, 1); }
            if (hiY - loY < 2) { loY = Math.Min(loY, -1); hiY = Math.Max(hiY, 1); }
        }

        public static int[] Shift(int[] px, int dx, int dy)
        {
            int n = Proto.Side(px);
            var o = new int[n * n];
            for (int k = 0; k < n * n; k++)
            {
                int x = k % n + dx, y = k / n + dy;
                if (px[k] != 0 && x >= 0 && x < n && y >= 0 && y < n) o[y * n + x] = px[k];
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

        // 5 x 7 digits for a 16 x 16 face, row by row ("#" lit)
        internal static readonly string[] Big = {
            ".###.#...##...##...##...##...#.###.", "..#...##....#....#....#....#...###.", ".###.#...#....#...#...#...#...#####",
            ".###.#...#....#..##.....##...#.###.", "...#...##..#.#.#..#.#####...#....#.", "######....####.....#....##...#.###.",
            "..##..#...#....####.#...##...#.###.", "#####....#...#...#...#....#....#...", ".###.#...##...#.###.#...##...#.###.",
            ".###.#...##...#.####....#...#..##.." };

        // the time on 16 x 16: hours in rows 0-6, minutes in rows 9-15, each row right-aligned unless asked otherwise
        public static int[] Face16(DateTime t, bool h24, int hourColor, int minuteColor, bool hoursLeft, bool minutesLeft)
        {
            var px = new int[256];
            int h = h24 ? t.Hour : (t.Hour % 12 == 0 ? 12 : t.Hour % 12);
            Draw16(px, h24 ? h.ToString("00") : h.ToString(), 0, hourColor, hoursLeft);
            Draw16(px, t.Minute.ToString("00"), 9, minuteColor, minutesLeft);
            return px;
        }

        static void Draw16(int[] px, string text, int y0, int color, bool left)
        {
            int x0 = left ? 0 : 16 - (text.Length * 6 - 1);
            foreach (char ch in text)
            {
                string g = Big[ch - '0'];
                for (int k = 0; k < 35; k++)
                    if (g[k] == '#') px[(y0 + k / 5) * 16 + x0 + k % 5] = color;
                x0 += 6;
            }
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

    // one display's own settings: which paired device it is, its serial channel, and its own playlist
    class DeviceSettings
    {
        public string Mac = "", Kind = "aurabox";
        public int Channel;
        public bool Rotate;   // the playlist was running when the app closed: run it again at the next start
        public List<PlayItem> Playlist = PlayItem.Defaults();
    }

    // Settings live next to the exe (portable); fall back to %APPDATA% if that folder is read-only.
    class Settings
    {
        // each display's own settings; the first is written with the original keys (mac=, playlist=...), others as devN
        public readonly List<DeviceSettings> Devices = new List<DeviceSettings> { new DeviceSettings() };
        public string Mac { get { return Devices[0].Mac; } set { Devices[0].Mac = value; } }
        public int Channel { get { return Devices[0].Channel; } set { Devices[0].Channel = value; } }
        public bool Rotate { get { return Devices[0].Rotate; } set { Devices[0].Rotate = value; } }
        public List<PlayItem> Playlist { get { return Devices[0].Playlist; } set { Devices[0].Playlist = value; } }
        public int Brightness = 255; public bool SyncOnConnect = true;
        public int ClockMinutes = 2; public int WeatherMinutes = 1; public bool Fahrenheit = true;
        public string Place = "New York, New York"; public double Lat = 40.71427, Lon = -74.00597;
        public bool MinimizeToTray = true;
        public int WeatherFlip = 5; public bool AnimatedIcons = true; public int AnimDelay = 5; public string ThemeMode = "system"; public bool ThermoFahrenheit = true; public bool SteadyClock = true; public bool StartInTray = false;
        public int Clock24 = -1;            // -1 = follow Windows, 0 = 12-hour, 1 = 24-hour
        public int LavaTheme = -1;          // -1 = a new color theme every minute, else a fixed one
        public bool LavaPuddle = true, BounceTemp = true, OnlyOnItems = false;
        public int CountdownMinutes = 5;    // the Countdown animation counts down from this
        public double FmMhz = 101.1;      // the Tiivoo 2's FM radio station
        public int GalleryPageSize = 30;   // designs per Gallery page (10 to 150)
        public int DitooBrightness = 100;  // the Ditoo Pro's full brightness as a percent of its own (lower it if it outshines the Tivoo)
        public int TivooVivid = 1, AuraClockColor = 7;   // color boost: Vivid (Extra Vivid came out too dark on the real Tivoo, 2026-11-11)
        public bool TranslateNames = true, GalleryAnimatedOnly = true; public string DivoomEmail = ""; public long DivoomUserId, DivoomToken;
        public int TivooClockStyle = 0, TivooClockColor = 0xFFFFFF; public bool TivooClockWeather, TivooClockTemp, TivooClockCalendar;
        public bool CountdownToDate = false; public DateTime CountdownUntil = new DateTime(DateTime.Now.Year + 1, 1, 1);   // ...or to this
        public bool AudioAux = true;        // audio input on connect: AUX jack (the box falls back to Bluetooth with no cable)
        public readonly Dictionary<string, int> AnimSpeeds = new Dictionary<string, int>();   // live animation id -> speed 1..10
        public const int DefaultSpeed = 6;
        // the Now page's quick-pick buttons: "clock", "weather", a live animation id, or "mode<n>"
        public const int ShowNowSlots = 8;
        public static readonly string[] DefaultShowNow = { "clock", "weather", "lavaclock", "fire", "bricks", "lava", "stars", "blocks" };
        public string[] ShowNow = (string[])DefaultShowNow.Clone();
        // the alarm and sleep timer as last sent (the box can't be asked for them)
        public int AlarmHour = 7, AlarmMinute = 0, AlarmScene = 0, SleepMinutes = 30, SleepScene = 0;
        public bool AlarmOn = false, SleepOn = false;
        public bool ShowNotifications = true;   // the app's own Windows notifications (startup in the tray, closing to the tray)
        public bool HasWindowPos = false; public int WindowX, WindowY;   // where the window was when it last closed (screen pixels)
        public int WindowW, WindowH; public bool WindowMax;   // the window's size when it last closed (0 = the standard size)
        public bool GalleryAnimate = true;                   // the Gallery plays every animated thumbnail
        public int FavoriteSort;                             // Favorites order: 0 A to Z, 1 newest first, 2 oldest first
        public bool CheckUpdates = true;                     // look for a newer release on GitHub when the app starts
        public string SelectedDevice = "";                   // the display picked at the top when the app last ran (its address)
        // the Text page's scrolling message (also the playlist's Scrolling Text item)
        public string TextMessage = "I ❤️ U!", TextFont = "Tahoma";
        public int TextColor = 7, TextSpeed = 5;

        // animations that start faster than DefaultSpeed until a speed is picked for them on the Animations page
        static readonly Dictionary<string, int> StartSpeeds = new Dictionary<string, int> { { "blocks", 10 }, { "fire", 8 }, { "bricks", 10 }, { "countdown", 10 }, { "heart", 5 } };

        public int SpeedOf(string id)
        {
            lock (AnimSpeeds)
            {
                int v;
                if (AnimSpeeds.TryGetValue(id, out v)) return v;
                return StartSpeeds.TryGetValue(id, out v) ? v : DefaultSpeed;
            }
        }

        public void SetSpeed(string id, int v) { lock (AnimSpeeds) AnimSpeeds[id] = Math.Max(1, Math.Min(10, v)); }

        // where each display stands, for the All Displays page: MAC -> left, top and screen width, in millimeters
        public readonly Dictionary<string, double[]> Layout = new Dictionary<string, double[]>();
        // displays left out of the animations across all displays (MACs): they keep to their own playlists
        public readonly HashSet<string> WallOut = new HashSet<string>();

        string WallOutText() { lock (WallOut) return string.Join(";", new List<string>(WallOut).ToArray()); }

        string LayoutText()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture; var parts = new List<string>();
            lock (Layout) foreach (var kv in Layout) parts.Add(kv.Key + "@" + kv.Value[0].ToString("0.#", inv) + "," + kv.Value[1].ToString("0.#", inv) + "," + kv.Value[2].ToString("0.#", inv));
            return string.Join(";", parts.ToArray());
        }
        // other data files (favorites) go wherever the settings file lives
        public static string DataFile(string name)
        {
            return Path.Combine(Path.GetDirectoryName(File.Exists(FallbackPath) && !File.Exists(PrimaryPath) ? FallbackPath : PrimaryPath), name);
        }

        // the settings file and favorites live in a "settings" folder next to the program
        const string IniName = "VaVaVoomControl.ini", OldIniName = "AuraBoxControl.ini";   // the app was AuraBox Control until 2026-11-09
        static string PrimaryPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings", IniName); } }
        static string FallbackPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VaVaVoomControl", IniName); }
        }

        // Older builds kept the files next to the exe (before 2026-11-03), or used the old name: move or rename them
        // into settings\ once, so nothing resets.
        static void MoveOldFiles()
        {
            string dir = Path.GetDirectoryName(PrimaryPath), old = AppDomain.CurrentDomain.BaseDirectory;
            var moves = new[] {
                new[] { Path.Combine(dir, OldIniName), PrimaryPath },
                new[] { Path.Combine(old, OldIniName), PrimaryPath },
                new[] { Path.Combine(old, "favorites.txt"), Path.Combine(dir, "favorites.txt") } };
            foreach (var m in moves)
                try { if (File.Exists(m[0]) && !File.Exists(m[1])) { Directory.CreateDirectory(dir); File.Move(m[0], m[1]); } }
                catch (Exception) { }   // read-only folder: Load falls back below
        }

        public static Settings Load()
        {
            var s = new Settings();
            MoveOldFiles();
            string oldFallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuraBoxControl", OldIniName);
            foreach (string path in new[] { PrimaryPath, Path.Combine(Path.GetDirectoryName(PrimaryPath), OldIniName),
                                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, OldIniName), FallbackPath, oldFallback })
                if (File.Exists(path)) return FromLines(File.ReadAllLines(path));
            return s;
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
                else if (k == "device_kind") s.Devices[0].Kind = Device.KindOf(v);
                else if (k.StartsWith("dev") && k.Length > 3 && char.IsDigit(k[3]))   // dev1=mac|kind|channel|rotate, dev1_playlist=...
                {
                    string[] f = k.Split('_');
                    int idx;
                    if (!int.TryParse(f[0].Substring(3), out idx) || idx < 1 || idx > 8) continue;
                    while (s.Devices.Count <= idx) s.Devices.Add(new DeviceSettings());
                    var d = s.Devices[idx];
                    if (f.Length == 1)
                    {
                        string[] parts = v.Split('|');
                        d.Mac = parts[0];
                        if (parts.Length > 1) d.Kind = Device.KindOf(parts[1]);
                        if (parts.Length > 2 && int.TryParse(parts[2], out n)) d.Channel = n;
                        if (parts.Length > 3) d.Rotate = parts[3] != "0";
                    }
                    else if (f[1] == "playlist") d.Playlist = PlayItem.Parse(v);
                }
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
                else if (k == "countdown" && int.TryParse(v, out n)) s.CountdownMinutes = Math.Max(1, Math.Min(99, n));
                else if (k == "countdown_to_date") s.CountdownToDate = v != "0";
                else if (k == "gallery_english") s.TranslateNames = v != "0";
                else if (k == "gallery_animated_only") s.GalleryAnimatedOnly = v != "0";
            else if (k == "divoom_account")   // email|user id|token
            {
                string[] f = v.Split('|'); long uid, tok;
                if (f.Length == 3 && long.TryParse(f[1], out uid) && long.TryParse(f[2], out tok)) { s.DivoomEmail = f[0]; s.DivoomUserId = uid; s.DivoomToken = tok; }
            }
            else if (k == "aura_clock_color" && int.TryParse(v, out n)) s.AuraClockColor = Math.Max(1, Math.Min(7, n));
            else if (k == "tivoo_vivid" && int.TryParse(v, out n)) s.TivooVivid = Math.Max(0, Math.Min(2, n));
            else if (k == "tivoo_clock")   // style|weather|temperature|calendar|RRGGBB
                {
                    string[] f = v.Split('|');
                    if (f.Length == 5 && int.TryParse(f[0], out n))
                    {
                        s.TivooClockStyle = n; s.TivooClockWeather = f[1] == "1"; s.TivooClockTemp = f[2] == "1"; s.TivooClockCalendar = f[3] == "1";
                        int c; if (int.TryParse(f[4], System.Globalization.NumberStyles.HexNumber, null, out c)) s.TivooClockColor = c;
                    }
                }
                else if (k == "fm_mhz") { double d; if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.FmMhz = Math.Max(87.5, Math.Min(108, d)); }
                else if (k == "gallery_page_size" && int.TryParse(v, out n)) s.GalleryPageSize = Math.Max(10, Math.Min(150, n / 10 * 10));
                else if (k == "ditoo_brightness" && int.TryParse(v, out n)) s.DitooBrightness = Math.Max(10, Math.Min(100, n));
                else if (k == "countdown_until")
                {
                    DateTime d;
                    if (DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d)) s.CountdownUntil = d;
                }
                else if (k == "show_notifications") s.ShowNotifications = v != "0";
                else if (k == "window_size")   // width,height[,max]
                {
                    string[] wh = v.Split(','); int ww, hh;
                    if (wh.Length >= 2 && int.TryParse(wh[0], out ww) && int.TryParse(wh[1], out hh)) { s.WindowW = ww; s.WindowH = hh; s.WindowMax = wh.Length > 2 && wh[2] == "max"; }
                }
                else if (k == "gallery_animate") s.GalleryAnimate = v != "0";
                else if (k == "check_updates") s.CheckUpdates = v != "0";
                else if (k == "selected_device") s.SelectedDevice = v.Trim();
                else if (k == "favorite_sort" && int.TryParse(v, out n)) s.FavoriteSort = Math.Max(0, Math.Min(2, n));
                else if (k == "window")
                {
                    string[] xy = v.Split(','); int x, y;
                    if (xy.Length == 2 && int.TryParse(xy[0], out x) && int.TryParse(xy[1], out y)) { s.WindowX = x; s.WindowY = y; s.HasWindowPos = true; }
                }
                else if (k == "text_message") s.TextMessage = line.Substring(eq + 1);   // untrimmed: spaces are part of the message
                else if (k == "text_font") s.TextFont = v;
                else if (k == "text_color" && int.TryParse(v, out n)) s.TextColor = Math.Max(1, Math.Min(7, n));
                else if (k == "text_speed" && int.TryParse(v, out n)) s.TextSpeed = Math.Max(1, Math.Min(10, n));
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
                else if (k == "layout")
                    foreach (string part in v.Split(';'))
                    {
                        string[] f = part.Split('@');
                        if (f.Length != 2) continue;
                        string[] nums = f[1].Split(','); double x, y, w;
                        var inv = System.Globalization.CultureInfo.InvariantCulture; var st = System.Globalization.NumberStyles.Float;
                        if (nums.Length == 3 && double.TryParse(nums[0], st, inv, out x) && double.TryParse(nums[1], st, inv, out y) && double.TryParse(nums[2], st, inv, out w))
                            s.Layout[f[0].Trim()] = new[] { x, y, Math.Max(20, Math.Min(300, w)) };
                    }
                else if (k == "wall_out")
                    foreach (string mac in v.Split(';')) { if (mac.Trim().Length > 0) s.WallOut.Add(mac.Trim()); }
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
            s.Devices.RemoveAll(d => d != s.Devices[0] && d.Mac.Length == 0);   // gaps left by removed devices
            return s;
        }

        public void Save()
        {
            string text = Text();
            try { Directory.CreateDirectory(Path.GetDirectoryName(PrimaryPath)); File.WriteAllText(PrimaryPath, text); }
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
                          "\r\nbounce_temp=" + (BounceTemp ? 1 : 0) + "\r\nonly_on_items=" + (OnlyOnItems ? 1 : 0) + "\r\naudio_aux=" + (AudioAux ? 1 : 0) + "\r\ncountdown=" + CountdownMinutes + "\r\ncountdown_to_date=" + (CountdownToDate ? 1 : 0) +
                          "\r\ngallery_animate=" + (GalleryAnimate ? 1 : 0) + "\r\ncheck_updates=" + (CheckUpdates ? 1 : 0) + "\r\nselected_device=" + SelectedDevice +"\r\nfavorite_sort=" + FavoriteSort + (WindowW > 0 ? "\r\nwindow_size=" + WindowW + "," + WindowH + (WindowMax ? ",max" : "") : "") + "\r\ngallery_english=" + (TranslateNames ? 1 : 0) + "\r\ngallery_animated_only=" + (GalleryAnimatedOnly ? 1 : 0) + "\r\ndivoom_account=" + DivoomEmail.Replace("|", "") + "|" + DivoomUserId + "|" + DivoomToken + "\r\naura_clock_color=" + AuraClockColor + "\r\ntivoo_vivid=" + TivooVivid + "\r\nditoo_brightness=" + DitooBrightness + "\r\ngallery_page_size=" + GalleryPageSize + "\r\nfm_mhz=" + FmMhz.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "\r\ntivoo_clock=" + TivooClockStyle + "|" + (TivooClockWeather ? 1 : 0) + "|" + (TivooClockTemp ? 1 : 0) + "|" + (TivooClockCalendar ? 1 : 0) + "|" + TivooClockColor.ToString("X6") + "\r\ncountdown_until=" + CountdownUntil.ToString("yyyy-MM-dd HH:mm", inv) + "\r\nshow_notifications=" + (ShowNotifications ? 1 : 0) + (HasWindowPos ? "\r\nwindow=" + WindowX + "," + WindowY : "") +
                          "\r\ntext_message=" + TextMessage.Replace("\r", " ").Replace("\n", " ") + "\r\ntext_font=" + TextFont +
                          "\r\ntext_color=" + TextColor + "\r\ntext_speed=" + TextSpeed + "\r\nanim_speeds=" + SpeedsText() + "\r\nlayout=" + LayoutText() + "\r\nwall_out=" + WallOutText() + "\r\nshow_now=" + string.Join(",", ShowNow) +
                          "\r\nalarm=" + AlarmHour + ":" + AlarmMinute + ":" + AlarmScene + ":" + (AlarmOn ? 1 : 0) +
                          "\r\nsleep=" + SleepMinutes + ":" + SleepScene + ":" + (SleepOn ? 1 : 0) +
                          "\r\nplaylist=" + PlayItem.Format(Playlist) + "\r\ndevice_kind=" + Devices[0].Kind + "\r\n";
            for (int i = 1; i < Devices.Count; i++)
            {
                var d = Devices[i];
                text += "dev" + i + "=" + d.Mac + "|" + d.Kind + "|" + d.Channel + "|" + (d.Rotate ? 1 : 0) + "\r\n" +
                        "dev" + i + "_playlist=" + PlayItem.Format(d.Playlist) + "\r\n";
            }
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

        int[] shown16;   // a 16 x 16 color picture on show instead of Pixels

        protected override void OnPaint(PaintEventArgs e)
        {
            if (shown16 != null)
            {
                int c = Math.Max(1, Math.Min(ClientSize.Width, ClientSize.Height) / 16), off = (Math.Min(ClientSize.Width, ClientSize.Height) - c * 16) / 2;
                for (int k = 0; k < 256; k++)
                {
                    var r = new Rectangle(off + (k % 16) * c + 1, off + (k / 16) * c + 1, Math.Max(1, c - 1), Math.Max(1, c - 1));
                    var col = Proto.ColorAt(shown16, k);
                    using (var b = new SolidBrush(col.R + col.G + col.B == 0 ? Color.FromArgb(25, 25, 25) : col)) e.Graphics.FillRectangle(b, r);
                }
                return;
            }
            for (int k = 0; k < 100; k++)
            {
                var r = new Rectangle((k % 10) * Cell + 1, (k / 10) * Cell + 1, Cell - 2, Cell - 2);
                if (Pixels[k] != 0) { Proto.Led(e.Graphics, r, Proto.Palette[Pixels[k]]); continue; }   // lit like the AuraBox's LEDs
                using (var b = new SolidBrush(Proto.Palette[0])) e.Graphics.FillRectangle(b, r);
                using (var p = new Pen(Color.FromArgb(45, 45, 45))) e.Graphics.DrawRectangle(p, r);
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

        public void SetAll(int[] px)
        {
            if (px.Length == 256) { shown16 = (int[])px.Clone(); Invalidate(); return; }   // just shown; drawing stays 10 x 10
            shown16 = null;
            Pixels = (int[])px.Clone(); Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }
}
