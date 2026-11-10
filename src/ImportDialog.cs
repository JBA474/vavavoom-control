// Picture import: verifies the file, previews the 10x10 / 8-color conversion, then hands frames back to the main window.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AuraBoxControl
{
    enum ImportAction { None, SendImage, SendAnimation, Edit, AddFrames, PlayFromPC }

    class ImportDialog : Form
    {
        public ImportAction Action = ImportAction.None;
        public List<int[]> Frames = new List<int[]>();   // converted frames (1 for still pictures)
        public List<int> Delays = new List<int>();       // per-frame display time in ms (for PlayFromPC)
        const int MaxFrames = 150, MaxSide = 256;        // frames kept for PC playback; source frames are downscaled to save memory
        public int[] Current { get { return converted.Count > 0 ? converted[frameIndex] : new int[100]; } }

        readonly List<Bitmap> sourceFrames = new List<Bitmap>();
        readonly List<int[]> converted = new List<int[]>();
        readonly List<int> delays = new List<int>();
        int frameIndex;
        RectangleF crop = RectangleF.Empty;              // chosen area, as fractions of the picture (empty = none)
        PointF dragFrom; bool dragging;
        PictureBox original, preview;
        TextBox report;
        ComboBox fitBox, colorBox;
        TrackBar frameBar;
        Label frameLabel;
        Button sendImg, sendAnim, edit, addFrames, playPc;

        public static string Verify(string path, out Image image, out bool ok)
        {
            image = null; ok = false;
            var sb = new StringBuilder();
            var fi = new FileInfo(path);
            sb.AppendLine("File: " + fi.Name);
            sb.AppendLine("Size: " + (fi.Length < 1024 * 1024 ? (fi.Length / 1024.0).ToString("0.0") + " KB" : (fi.Length / 1048576.0).ToString("0.0") + " MB"));
            if (fi.Length == 0) { sb.AppendLine("✖ File is empty."); return sb.ToString(); }
            if (fi.Length > 50L * 1024 * 1024) { sb.AppendLine("✖ File is larger than 50 MB."); return sb.ToString(); }

            byte[] head = new byte[16];
            using (var fs = File.OpenRead(path)) fs.Read(head, 0, head.Length);
            string fmt = Detect(head);
            sb.AppendLine("Detected format: " + fmt);
            string ext = fi.Extension.ToLowerInvariant();
            string expected = ExtFormat(ext);
            if (expected != null && expected != fmt && fmt != "Unknown")
                sb.AppendLine("⚠ Extension " + ext + " does not match the contents (" + fmt + ").");

            if (fmt == "WebP" || fmt == "HEIC/AVIF" || fmt == "SVG")
            {
                sb.AppendLine("✖ " + fmt + " is not supported. Save it as PNG, JPEG, GIF or BMP first.");
                return sb.ToString();
            }
            try
            {
                image = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
            }
            catch (Exception e)
            {
                sb.AppendLine("✖ Windows could not decode this picture: " + e.Message);
                return sb.ToString();
            }
            sb.AppendLine("Dimensions: " + image.Width + " × " + image.Height + " px");
            sb.AppendLine("Color depth: " + Image.GetPixelFormatSize(image.PixelFormat) + "-bit" +
                          (Image.IsAlphaPixelFormat(image.PixelFormat) ? " with transparency (shown as black)" : ""));
            int frames = FrameCount(image);
            if (frames > 1)
                sb.AppendLine("Animated: " + frames + " frames" + (frames > AuraBox.MaxFrames ? " (the AuraBox holds " + AuraBox.MaxFrames + ": sending samples " + AuraBox.MaxFrames + " evenly, or play every frame from the PC)" : ""));
            if (image.Width != 10 || image.Height != 10)
                sb.AppendLine("ℹ Will be scaled to 10 × 10.");
            sb.AppendLine("ℹ Colors are reduced to the AuraBox's 8 colors.");
            if (Math.Max(image.Width, image.Height) > 4 * Math.Min(image.Width, image.Height))
                sb.AppendLine("⚠ Very wide/tall picture: drag on the original to pick the part to show.");
            sb.AppendLine("✔ Picture is OK to upload.");
            ok = true;
            return sb.ToString();
        }

        static string Detect(byte[] h)
        {
            if (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return "PNG";
            if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "JPEG";
            if (h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return "GIF";
            if (h[0] == 'B' && h[1] == 'M') return "BMP";
            if (h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0) return "ICO";
            if ((h[0] == 'I' && h[1] == 'I' && h[2] == 42) || (h[0] == 'M' && h[1] == 'M' && h[3] == 42)) return "TIFF";
            if (h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return "WebP";
            if (h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p') return "HEIC/AVIF";
            if (h[0] == '<') return "SVG";
            return "Unknown";
        }

        static string ExtFormat(string ext)
        {
            switch (ext)
            {
                case ".png": return "PNG";
                case ".jpg": case ".jpeg": case ".jfif": return "JPEG";
                case ".gif": return "GIF";
                case ".bmp": return "BMP";
                case ".ico": return "ICO";
                case ".tif": case ".tiff": return "TIFF";
                case ".webp": return "WebP";
                case ".heic": case ".avif": return "HEIC/AVIF";
                case ".svg": return "SVG";
            }
            return null;
        }

        static int FrameCount(Image img)
        {
            try
            {
                foreach (Guid g in img.FrameDimensionsList)
                    if (g == FrameDimension.Time.Guid) return img.GetFrameCount(FrameDimension.Time);
            }
            catch (Exception) { }
            return 1;
        }

        public ImportDialog(string path, Image image, string verification)
        {
            Text = "Import picture — " + Path.GetFileName(path);
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 470);

            int n = FrameCount(image);
            int[] gifDelays = ReadDelays(image, n);
            var picks = new List<int>();
            if (n <= MaxFrames) for (int i = 0; i < n; i++) picks.Add(i);
            else for (int i = 0; i < MaxFrames; i++) picks.Add(i * n / MaxFrames);
            float scale = Math.Min(1f, (float)MaxSide / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
            for (int k = 0; k < picks.Count; k++)
            {
                if (n > 1) image.SelectActiveFrame(FrameDimension.Time, picks[k]);
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, w, h);
                }
                sourceFrames.Add(bmp);
                // a kept frame lasts until the next kept one
                int end = k + 1 < picks.Count ? picks[k + 1] : n, ms = 0;
                for (int i = picks[k]; i < end; i++) ms += gifDelays[i];
                delays.Add(ms);
            }

            Controls.Add(new Label { Text = "Original", Location = new Point(12, 8), AutoSize = true });
            original = new PictureBox { Location = new Point(12, 28), Size = new Size(180, 180), SizeMode = PictureBoxSizeMode.Zoom,
                                        BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(30, 30, 30), Cursor = Cursors.Cross };
            original.MouseDown += delegate (object o, MouseEventArgs e) { dragging = true; dragFrom = ToPicture(e.Location); };
            original.MouseMove += delegate (object o, MouseEventArgs e) { if (dragging) { SetCrop(dragFrom, ToPicture(e.Location), false); original.Invalidate(); } };
            original.MouseUp += delegate (object o, MouseEventArgs e) { if (!dragging) return; dragging = false; SetCrop(dragFrom, ToPicture(e.Location), true); };
            original.Paint += PaintCrop;
            Controls.Add(original);
            Controls.Add(new Label { Text = "Drag on the original to pick an area.", Location = new Point(12, 212), AutoSize = true, Tag = "subtle" });
            Controls.Add(new Label { Text = "On the AuraBox", Location = new Point(204, 8), AutoSize = true });
            preview = new PictureBox { Location = new Point(204, 28), Size = new Size(180, 180), SizeMode = PictureBoxSizeMode.Zoom,
                                       BorderStyle = BorderStyle.FixedSingle, BackColor = Color.Black };
            Controls.Add(preview);

            Controls.Add(new Label { Text = "Sizing:", Location = new Point(400, 30), AutoSize = true });
            fitBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(470, 26), Width = 156 };
            fitBox.Items.AddRange(new object[] { "Fit (keep whole picture)", "Fill (crop to square)", "Stretch", "Area I dragged" });
            fitBox.SelectedIndex = 0;
            fitBox.SelectedIndexChanged += delegate
            {
                if (fitBox.SelectedIndex != 3) { crop = RectangleF.Empty; original.Invalidate(); }
                Convert();
            };
            Controls.Add(fitBox);
            Controls.Add(new Label { Text = "Colors:", Location = new Point(400, 64), AutoSize = true });
            colorBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(470, 60), Width = 156 };
            colorBox.Items.AddRange(new object[] { "Nearest color", "Vivid (bright colors)" });
            colorBox.SelectedIndex = 1;
            colorBox.SelectedIndexChanged += delegate { Convert(); };
            Controls.Add(colorBox);

            frameLabel = new Label { Location = new Point(400, 100), AutoSize = true };
            Controls.Add(frameLabel);
            frameBar = new TrackBar { Location = new Point(396, 120), Width = 234, Minimum = 0, Maximum = Math.Max(0, sourceFrames.Count - 1),
                                      Visible = sourceFrames.Count > 1 };
            frameBar.ValueChanged += delegate { frameIndex = frameBar.Value; ShowFrame(); };
            Controls.Add(frameBar);
            playPc = new Button { Text = "Play All Frames From PC", Location = new Point(400, 172), Size = new Size(226, 32), Enabled = sourceFrames.Count > 1 };
            playPc.Click += delegate
            {
                Action = ImportAction.PlayFromPC;
                Frames = new List<int[]>(converted); Delays = new List<int>(delays);
                DialogResult = DialogResult.OK;
            };
            Controls.Add(playPc);

            report = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(12, 236),
                                   Size = new Size(614, 174), Text = verification.Replace("\n", "\r\n").Replace("\r\r", "\r"),
                                   BackColor = SystemColors.Window };
            Controls.Add(report);

            sendImg = Btn("Send Picture", 12, ImportAction.SendImage);
            sendAnim = Btn("Send as Animation", 136, ImportAction.SendAnimation);
            sendAnim.Enabled = sourceFrames.Count > 1;
            edit = Btn("Open in Editor", 260, ImportAction.Edit);
            addFrames = Btn("Add to Animation", 384, ImportAction.AddFrames);
            var cancel = new Button { Text = "Cancel", Location = new Point(526, 424), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = sendImg;

            Convert();
        }

        Button Btn(string text, int x, ImportAction a)
        {
            var b = new Button { Text = text, Location = new Point(x, 424), Size = new Size(118, 32) };
            b.Click += delegate
            {
                Action = a;
                Frames = a == ImportAction.SendImage || a == ImportAction.Edit ? new List<int[]> { Current } : Sample8();
                DialogResult = DialogResult.OK;
            };
            Controls.Add(b);
            return b;
        }

        void Convert()
        {
            converted.Clear();
            int fit = fitBox.SelectedIndex == 3 && crop.IsEmpty ? 1 : fitBox.SelectedIndex;
            foreach (var src in sourceFrames) converted.Add(ToPixels(src, fit, colorBox.SelectedIndex == 1, crop));
            frameIndex = Math.Min(frameIndex, converted.Count - 1);
            ShowFrame();
        }

        void ShowFrame()
        {
            original.Image = sourceFrames[frameIndex];
            var old = preview.Image;
            preview.Image = Proto.Render(converted[frameIndex], 18);
            if (old != null) old.Dispose();
            frameLabel.Text = sourceFrames.Count > 1 ? "Frame " + (frameIndex + 1) + " of " + sourceFrames.Count + " (" + delays[frameIndex] + " ms)" : "";
        }

        static int[] ToPixels(Bitmap src, int fit, bool vivid, RectangleF crop)
        {
            using (var small = new Bitmap(10, 10, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(small))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                RectangleF dst = new RectangleF(0, 0, 10, 10), from = new RectangleF(0, 0, src.Width, src.Height);
                if (!crop.IsEmpty)
                    from = new RectangleF(crop.X * src.Width, crop.Y * src.Height, crop.Width * src.Width, crop.Height * src.Height);
                else if (fit == 0)
                {
                    float s = Math.Min(10f / src.Width, 10f / src.Height);
                    dst = new RectangleF((10 - src.Width * s) / 2, (10 - src.Height * s) / 2, src.Width * s, src.Height * s);
                }
                else if (fit == 1)
                {
                    float side = Math.Min(src.Width, src.Height);
                    from = new RectangleF((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                }
                g.DrawImage(src, dst, from, GraphicsUnit.Pixel);
                var px = new int[100];
                for (int k = 0; k < 100; k++)
                {
                    Color c = small.GetPixel(k % 10, k / 10);
                    // treat transparency as black
                    c = Color.FromArgb(c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255);
                    px[k] = vivid ? Vivid(c) : Proto.Nearest(c);
                }
                return px;
            }
        }

        // the AuraBox holds 12 frames: longer animations are sampled evenly
        List<int[]> Sample8()
        {
            int max = AuraBox.MaxFrames;
            if (converted.Count <= max) return new List<int[]>(converted);
            var list = new List<int[]>();
            for (int k = 0; k < max; k++) list.Add(converted[k * converted.Count / max]);
            return list;
        }

        // GIF frame delays (property 0x5100, 1/100 s per frame); browsers show tiny delays as 100 ms
        static int[] ReadDelays(Image img, int n)
        {
            var d = new int[n];
            for (int i = 0; i < n; i++) d[i] = 100;
            try
            {
                byte[] v = img.GetPropertyItem(0x5100).Value;
                for (int i = 0; i < n && i * 4 + 3 < v.Length; i++)
                {
                    int cs = BitConverter.ToInt32(v, i * 4);
                    d[i] = cs < 2 ? 100 : cs * 10;
                }
            }
            catch (Exception) { }
            return d;
        }

        // where the picture sits inside the Zoom-mode box
        RectangleF PictureArea()
        {
            var img = sourceFrames[0];
            float s = Math.Min((float)original.ClientSize.Width / img.Width, (float)original.ClientSize.Height / img.Height);
            return new RectangleF((original.ClientSize.Width - img.Width * s) / 2, (original.ClientSize.Height - img.Height * s) / 2,
                                  img.Width * s, img.Height * s);
        }

        // picture-box point -> fraction of the picture
        PointF ToPicture(Point p)
        {
            var a = PictureArea();
            return new PointF(Math.Max(0, Math.Min(1, (p.X - a.X) / a.Width)), Math.Max(0, Math.Min(1, (p.Y - a.Y) / a.Height)));
        }

        // square area (in picture pixels) from the drag start toward the pointer, kept inside the picture
        void SetCrop(PointF a, PointF b, bool done)
        {
            var img = sourceFrames[0];
            float ax = a.X * img.Width, ay = a.Y * img.Height, bx = b.X * img.Width, by = b.Y * img.Height;
            float side = Math.Min(Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)), Math.Min(img.Width, img.Height));
            if (side < 3) { if (done) { crop = RectangleF.Empty; original.Invalidate(); } return; }
            float x = bx >= ax ? ax : ax - side, y = by >= ay ? ay : ay - side;
            x = Math.Max(0, Math.Min(img.Width - side, x)); y = Math.Max(0, Math.Min(img.Height - side, y));
            crop = new RectangleF(x / img.Width, y / img.Height, side / img.Width, side / img.Height);
            if (done) { if (fitBox.SelectedIndex != 3) fitBox.SelectedIndex = 3; else Convert(); }
        }

        void PaintCrop(object sender, PaintEventArgs e)
        {
            if (crop.IsEmpty) return;
            var a = PictureArea();
            var r = new RectangleF(a.X + crop.X * a.Width, a.Y + crop.Y * a.Height, crop.Width * a.Width, crop.Height * a.Height);
            using (var shade = new Region(original.ClientRectangle))
            using (var dim = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            using (var pen = new Pen(Color.Gold, 2) { DashStyle = DashStyle.Dash })
            {
                shade.Exclude(r);
                e.Graphics.FillRegion(dim, shade);
                e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }
        }

        // Normalize to the pixel's own brightest channel, then threshold each channel: keeps hues instead of going dark.
        static int Vivid(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            if (max < 48) return 0;
            int r = c.R * 255 / max, g = c.G * 255 / max, b = c.B * 255 / max;
            return (r >= 140 ? 1 : 0) | (g >= 140 ? 2 : 0) | (b >= 140 ? 4 : 0);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var b in sourceFrames) b.Dispose();
            base.OnFormClosed(e);
        }
    }
}
