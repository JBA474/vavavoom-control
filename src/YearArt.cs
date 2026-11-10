// Brings the year drawn in a 16 x 16 design up to date ("Happy New Year 2021" -> 2027): finds the year's digits in
// each frame, checks they read as the year in the design's name, and redraws the year with the new digits in the
// design's own size, spacing and colors (a digit the design already has is copied; a missing one is drawn from a
// small font scaled to fit). Countdown animations (2026 turning into 2027) move along with it. When it can't read
// the year with confidence it leaves the design alone; years that scroll past or digits drawn touching each other
// are left as they are.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AuraBoxControl
{
    static class YearArt
    {
        // the year written in a name (1950-2039, standing apart from other digits), or 0
        public static int YearIn(string name)
        {
            var m = Regex.Match(name ?? "", @"(?<!\d)(19[5-9]\d|20[0-3]\d)(?!\d)");
            return m.Success ? int.Parse(m.Value) : 0;
        }

        // the year a design should show now: New Year designs the coming year from July on, the rest this year
        public static int TargetFor(string name)
        {
            var now = DateTime.Now;
            return IsNewYear(name) && now.Month >= 7 ? now.Year + 1 : now.Year;
        }

        public static bool IsNewYear(string name)
        {
            return Regex.IsMatch(name ?? "", @"new\s*year|neujahr|nouvel\s*an|a[ñn]o\s*nuevo|ano\s*novo|capodanno|новы[йм]\s*год|新年|元旦|春节|春節|새해|silvester",
                                 RegexOptions.IgnoreCase);
        }

        // the year drawn in the design when its name doesn't say: four digits read without doubt, as 1950-2039 (0 when
        // none is); read from the last frames first, since a countdown ends on its year
        public static int YearDrawn(List<int[]> frames)
        {
            if (frames == null || frames.Count == 0 || frames[0].Length != 256) return 0;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                var f = frames[i];
                int bg = Background(f);
                foreach (bool byColor in new[] { false, true })
                {
                    var pieces = Pieces(f, bg, byColor).FindAll(g => g.H >= 4 && g.H <= 12 && g.W <= 8 && g.H >= g.W && g.Pixels.Count >= g.H);
                    foreach (var g in pieces) Read(g);
                    pieces.Sort((a, b) => a.L != b.L ? a.L.CompareTo(b.L) : a.T.CompareTo(b.T));
                    foreach (var group in Groups(pieces))
                    {
                        bool sure = true;
                        var sb = new System.Text.StringBuilder();
                        foreach (var g in group) { if (g.Miss > 2) sure = false; sb.Append(g.Digit); }
                        int year = YearIn(sb.ToString());
                        if (sure && year > 0 && Reads(group, sb.ToString())) return year;
                    }
                }
            }
            return 0;
        }

        // 3 x 5 digit shapes for reading (common pixel-font forms of each digit); the first one is used for drawing
        static readonly string[][] Shapes = {
            new[] { "111101101101111", "010101101101010", "011101101101110" },   // 0
            new[] { "010110010010111", "010110010010010", "110010010010111" },   // 1
            new[] { "111001111100111", "110001010100111", "111001010100111" },   // 2
            new[] { "111001111001111", "110001010001110", "111001011001111" },   // 3
            new[] { "101101111001001", "001011101111001", "100101111001001" },   // 4
            new[] { "111100111001111", "111100110001110", "111100111001110" },   // 5
            new[] { "111100111101111", "011100111101111", "111100111101010" },   // 6
            new[] { "111001001001001", "111001010010010", "111001001010010" },   // 7
            new[] { "111101111101111", "010101010101010", "111101010101111" },   // 8
            new[] { "111101111001111", "111101111001110", "010101011001010" } }; // 9

        class Glyph
        {
            public int L, T, R, B;                  // box, inclusive
            public List<int> Pixels = new List<int>();
            public int W { get { return R - L + 1; } }
            public int H { get { return B - T + 1; } }
            public int Digit = -1, Miss = 99;       // best reading and how many of the 15 cells disagree
            public string Mask()                     // the lit pattern inside the box
            {
                var set = new HashSet<int>(Pixels);
                var sb = new System.Text.StringBuilder();
                for (int y = T; y <= B; y++) { for (int x = L; x <= R; x++) sb.Append(set.Contains(y * 16 + x) ? '1' : '0'); sb.Append('/'); }
                return sb.ToString();
            }
        }

        // one frame's change: the old digits (where, and exactly how they looked) and the pixels drawn for each
        class Change
        {
            public List<Glyph> Old;
            public List<KeyValuePair<int, int>>[] Drawn = new List<KeyValuePair<int, int>>[4];
            public string[] Looks = new string[4];   // each old digit's pixels with colors, to spot it in other frames
        }

        // new frames with the year moved from "from" to "to" (a countdown frame showing from - 1 moves to to - 1),
        // or null when the year can't be found reliably in most frames
        public static List<int[]> Update(List<int[]> frames, int from, int to)
        {
            if (frames == null || frames.Count == 0 || frames[0].Length != 256 || from == to || from <= 0) return null;
            int lit = 0, changed = 0, firstChanged = -1;
            var results = new int[frames.Count][];
            var whole = new bool[frames.Count];   // the whole year was read and redrawn in this frame
            var changes = new List<Change>();
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                int bg = Background(f);
                bool any = false; foreach (int c in f) if (Far(c, bg)) { any = true; break; }
                if (any) lit++;
                Change ch;
                results[i] = UpdateFrame(f, bg, from, to, out ch);
                if (results[i] != null) { changed++; changes.Add(ch); whole[i] = true; if (firstChanged < 0) firstChanged = i; }
            }
            if (changed == 0) return null;
            // frames without the whole year (a digit falling into place, the year building up): the digits that look
            // exactly like ones changed elsewhere change the same way
            int partial = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                if (results[i] != null) continue;
                results[i] = Partial(frames[i], Background(frames[i]), changes);
                if (results[i] != null) partial++;
            }
            if ((changed + partial) * 10 < lit * 6) return null;   // most frames with something on them must follow
            // years only tick from to - 1 to to (2026 to 2027): no frame may still show another year
            var last = changes[0].Old[3];
            var lastColors = new List<int>();   // the year's last digit's color in each frame it was read (a countdown's falling digit has it)
            for (int i = 0, k = 0; i < frames.Count; i++) if (whole[i]) lastColors.Add(MainColor(frames[i], changes[k++].Old[3]));
            var yearBoxes = new List<Glyph>();
            foreach (var ch in changes) yearBoxes.AddRange(ch.Old);
            var list = new List<int[]>();
            for (int i = 0; i < frames.Count; i++)
            {
                var nf = results[i] ?? frames[i];
                // a digit falling into the last place (a countdown) changes too
                var lone = LoneDigit(nf, Background(nf), from, to, last, lastColors, yearBoxes);
                if (lone != null) nf = lone;
                int shown = YearShown(nf);
                if (shown != 0 && shown != to && shown != to - 1) return null;
                list.Add(nf);
            }
            return list;
        }

        // a year that can be read in a frame without doubt (0 when none)
        static int YearShown(int[] f)
        {
            int bg = Background(f);
            foreach (bool byColor in new[] { false, true })
            {
                var pieces = Pieces(f, bg, byColor).FindAll(g => g.H >= 4 && g.H <= 12 && g.W <= 8 && g.H >= g.W && g.Pixels.Count >= g.H);
                foreach (var g in pieces) Read(g);
                pieces.Sort((a, b) => a.L != b.L ? a.L.CompareTo(b.L) : a.T.CompareTo(b.T));
                foreach (var group in Groups(pieces))
                {
                    bool sure = true;
                    var sb = new System.Text.StringBuilder();
                    foreach (var g in group) { if (g.Miss > 1) sure = false; sb.Append(g.Digit); }
                    int year = YearIn(sb.ToString());
                    if (sure && year > 0 && Reads(group, sb.ToString())) return year;
                }
            }
            return 0;
        }

        // a single digit the size of the year's digits, in the last digit's column and not part of a whole year, that
        // reads as the old year's last digit (or the one before's): redrawn as the new one's, in its own colors
        // (only one in the last digit's color, and not one of the year's own digits, like the top of "20 over 21")
        static int[] LoneDigit(int[] f, int bg, int from, int to, Glyph last, List<int> lastColors, List<Glyph> yearBoxes)
        {
            int[] nf = null;
            foreach (bool byColor in new[] { false, true })
            {
                foreach (var g in Pieces(f, bg, byColor))
                {
                    if (Math.Abs(g.H - last.H) > 1 || g.R < last.L - 1 || g.L > last.R + 1 || g.Pixels.Count < g.H || g.W > 8) continue;
                    if (g.T <= last.B && g.B >= last.T) continue;   // sitting in the year's own row: part of the year
                    bool ownDigit = false;
                    foreach (var y in yearBoxes) if (g.L <= y.R && g.R >= y.L && g.T <= y.B && g.B >= y.T) { ownDigit = true; break; }
                    int main = MainColor(f, g);
                    if (ownDigit || !lastColors.Exists(c => Distance(c, main) <= 60)) continue;
                    Read(g);
                    if (g.Miss > 2) continue;
                    int d;
                    if (g.Digit == from % 10) d = to % 10;
                    else if (g.Digit == (from + 9) % 10) d = (to + 9) % 10;
                    else continue;
                    if (d == g.Digit) continue;
                    if (nf == null) nf = (int[])f.Clone();
                    foreach (int p in g.Pixels) nf[p] = bg;
                    int w = d == 1 ? Math.Max(1, Math.Min(g.W, 2)) : Math.Max(3, g.W), left = Math.Min(g.L, 16 - w);
                    for (int y = 0; y < g.H; y++)
                    {
                        int color = RowColor(f, g, g.T + y);
                        for (int x = 0; x < w; x++)
                            if (d == 1 && w <= 2 || Shapes[d][0][(y * 5 / g.H) * 3 + (x * 3 / w)] == '1') nf[(g.T + y) * 16 + left + x] = color;
                    }
                }
                if (nf != null) return nf;
            }
            return null;
        }

        // the most common color, if it covers a good part of the picture; otherwise black
        static int Background(int[] f)
        {
            var counts = new Dictionary<int, int>();
            int best = 0, most = 0;
            foreach (int c in f) { int n; counts.TryGetValue(c, out n); counts[c] = ++n; if (n > most) { most = n; best = c; } }
            return most >= 64 ? best : 0;
        }

        static int Distance(int a, int b)
        {
            return Math.Abs((a >> 16 & 255) - (b >> 16 & 255)) + Math.Abs((a >> 8 & 255) - (b >> 8 & 255)) + Math.Abs((a & 255) - (b & 255));
        }

        static bool Far(int a, int b) { return Distance(a, b) > 60; }

        // pieces of everything that isn't background, joined at edges and corners; byColor: only pixels of about the
        // same color join (so fireworks touching a digit stay apart from it)
        static List<Glyph> Pieces(int[] f, int bg, bool byColor)
        {
            var seen = new bool[256];
            var list = new List<Glyph>();
            for (int k = 0; k < 256; k++)
            {
                if (seen[k] || !Far(f[k], bg)) continue;
                var g = new Glyph { L = 16, T = 16, R = -1, B = -1 };
                var stack = new Stack<int>(); stack.Push(k); seen[k] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop(), x = p % 16, y = p / 16;
                    g.Pixels.Add(p);
                    g.L = Math.Min(g.L, x); g.R = Math.Max(g.R, x); g.T = Math.Min(g.T, y); g.B = Math.Max(g.B, y);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx > 15 || ny > 15) continue;
                            int q = ny * 16 + nx;
                            if (!seen[q] && Far(f[q], bg) && (!byColor || Distance(f[q], f[p]) <= 90)) { seen[q] = true; stack.Push(q); }
                        }
                }
                list.Add(g);
            }
            return list;
        }

        // read a piece as a digit: shrink it to 3 x 5 and compare with the shapes
        static void Read(Glyph g)
        {
            if (g.W * 3 <= g.H) { g.Digit = 1; g.Miss = 0; return; }   // a thin upright stroke: a one
            var set = new HashSet<int>(g.Pixels);
            var cells = new char[15];
            for (int cy = 0; cy < 5; cy++)
                for (int cx = 0; cx < 3; cx++)
                {
                    double x0 = g.L + cx * g.W / 3.0, x1 = g.L + (cx + 1) * g.W / 3.0, y0 = g.T + cy * g.H / 5.0, y1 = g.T + (cy + 1) * g.H / 5.0;
                    double on = 0, all = 0;
                    for (int y = (int)y0; y < y1; y++)
                        for (int x = (int)x0; x < x1; x++)
                        {
                            double w = (Math.Min(x1, x + 1) - Math.Max(x0, x)) * (Math.Min(y1, y + 1) - Math.Max(y0, y));
                            all += w; if (set.Contains(y * 16 + x)) on += w;
                        }
                    cells[cy * 3 + cx] = all > 0 && on / all >= 0.5 ? '1' : '0';
                }
            for (int d = 0; d < 10; d++)
                foreach (var s in Shapes[d])
                {
                    int miss = 0;
                    for (int i = 0; i < 15; i++) if (s[i] != cells[i]) miss++;
                    if (miss < g.Miss) { g.Miss = miss; g.Digit = d; }
                }
        }

        // the year's digits in one frame (four in a row, or two over two), redrawn; null when not found
        static int[] UpdateFrame(int[] f, int bg, int from, int to, out Change change)
        {
            change = null;
            foreach (bool byColor in new[] { false, true })
            {
                var pieces = Pieces(f, bg, byColor).FindAll(g => g.H >= 4 && g.H <= 12 && g.W <= 8 && g.H >= g.W && g.Pixels.Count >= g.H);
                foreach (var g in pieces) Read(g);
                pieces.Sort((a, b) => a.L != b.L ? a.L.CompareTo(b.L) : a.T.CompareTo(b.T));
                // every group that could be the year, cleanest reading first (other digits nearby, like a clock's,
                // can pair up with the year's into a group that reads almost right)
                var found = new List<KeyValuePair<int, KeyValuePair<List<Glyph>, int>>>();
                foreach (var group in Groups(pieces))
                {
                    // the year, or the year before (a countdown shows it first): whichever reads with fewer doubts
                    int best = 99, shift = 0;
                    foreach (int s in new[] { 0, -1 })
                    {
                        int doubts = Doubts(group, (from + s).ToString());
                        if (doubts < best) { best = doubts; shift = s; }
                    }
                    bool stacked = group[2].T > group[1].B;   // two over two: only an exact reading counts
                    if (best > (stacked ? 0 : 1)) continue;
                    found.Add(new KeyValuePair<int, KeyValuePair<List<Glyph>, int>>(best, new KeyValuePair<List<Glyph>, int>(group, shift)));
                }
                found.Sort((a, b) => a.Key.CompareTo(b.Key));
                foreach (var c in found)
                {
                    var group = c.Value.Key;
                    int shift = c.Value.Value;
                    string old = (from + shift).ToString(), now = (to + shift).ToString();
                    var nf = Redraw(f, bg, group, old, now, false, out change);
                    // digits sitting in background-colored edges (a black frame around each, with colored stripes
                    // behind): a wider new digit may take its edge with it over the stripes
                    if (nf == null && Edged(f, bg, group)) nf = Redraw(f, bg, group, old, now, true, out change);
                    if (nf != null) return nf;
                }
            }
            return null;
        }

        // four pieces of one height: in a row, or two above two
        static IEnumerable<List<Glyph>> Groups(List<Glyph> p)
        {
            for (int a = 0; a < p.Count; a++)
                for (int b = 0; b < p.Count; b++)
                {
                    if (b == a || !Beside(p[a], p[b])) continue;
                    for (int c = 0; c < p.Count; c++)
                    {
                        if (c == a || c == b) continue;
                        if (Beside(p[b], p[c]))   // a row
                            for (int d = 0; d < p.Count; d++)
                                if (d != a && d != b && d != c && Beside(p[c], p[d])) yield return new List<Glyph> { p[a], p[b], p[c], p[d] };
                        // two over two: c under a, d under b
                        if (Math.Abs(p[c].L - p[a].L) <= 1 && p[c].T > p[a].B && p[c].T - p[a].B <= 6 && Math.Abs(p[c].H - p[a].H) <= 1)
                            for (int d = 0; d < p.Count; d++)
                                if (d != a && d != b && d != c && Beside(p[c], p[d]) && Math.Abs(p[d].L - p[b].L) <= 2)
                                    yield return new List<Glyph> { p[a], p[b], p[c], p[d] };
                    }
                }
        }

        static bool Beside(Glyph a, Glyph b)
        {
            int gap = b.L - a.R - 1;   // up to 5: digits on tiles ("20" over "21") sit well apart
            return gap >= 0 && gap <= 5 && Math.Abs(a.T - b.T) <= 1 && Math.Abs(a.B - b.B) <= 1;
        }

        // the four pieces spell the year: every digit close to its shape (at most one doubtful), and digits that are
        // the same in the year look exactly the same
        static bool Reads(List<Glyph> g, string year) { return Doubts(g, year) <= 1; }

        // how many digits only roughly match the year (99 when it can't be this year at all)
        static int Doubts(List<Glyph> g, string year)
        {
            int doubtful = 0;
            for (int i = 0; i < 4; i++)
                if (g[i].Digit != year[i] - '0' || g[i].Miss > 3)
                {
                    if (++doubtful > 1 || DigitMiss(g[i], year[i] - '0') > 5) return 99;
                }
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    if (year[i] == year[j] && g[i].W == g[j].W && g[i].H == g[j].H && g[i].Mask() != g[j].Mask()) return 99;
            return doubtful;
        }

        // how far a piece is from one digit's shapes (sampled at the middle of each 3 x 5 cell)
        static int DigitMiss(Glyph g, int d)
        {
            var set = new HashSet<int>(g.Pixels);
            int best = 99;
            foreach (var s in Shapes[d])
            {
                int miss = 0;
                for (int cy = 0; cy < 5; cy++)
                    for (int cx = 0; cx < 3; cx++)
                    {
                        int x = g.L + (int)((cx + 0.5) * g.W / 3.0), y = g.T + (int)((cy + 0.5) * g.H / 5.0);
                        if ((s[cy * 3 + cx] == '1') != set.Contains(y * 16 + x)) miss++;
                    }
                best = Math.Min(best, miss);
            }
            return best;
        }

        // every digit has background all around it while the picture also has other colors right next to that edge
        static bool Edged(int[] f, int bg, List<Glyph> g)
        {
            foreach (var x in g)
            {
                var ring = Ring(new HashSet<int>(x.Pixels), false);
                int edge = ring.FindAll(q => f[q] == bg).Count;
                if (ring.Count == 0 || edge * 10 < ring.Count * 8) return false;
            }
            return true;
        }

        // the year written again with the new digits: same spacing and rows, each digit in its old place's colors;
        // null when it doesn't fit or would draw over something else. edged: a changed digit may cover what's next to
        // it, and gets a background-colored edge of its own (the tile it sits on, redrawn)
        static int[] Redraw(int[] f, int bg, List<Glyph> g, string old, string now, bool edged, out Change change)
        {
            change = null;
            int width = 0, oneWidth = 0;
            foreach (var x in g) { if (x.Digit != 1) width = Math.Max(width, x.W); else oneWidth = Math.Max(oneWidth, x.W); }
            if (width == 0) width = 3;
            // each new digit's shape: its own (unchanged), a twin's from this year, or the small font
            var masks = new bool[4][,];
            for (int i = 0; i < 4; i++)
            {
                int d = now[i] - '0';
                Glyph src = old[i] == now[i] ? g[i] : null;
                if (src == null) for (int j = 0; j < 4; j++) if (old[j] - '0' == d && Math.Abs(g[j].H - g[i].H) <= 1) { src = g[j]; break; }
                if (src != null)
                {
                    var set = new HashSet<int>(src.Pixels);
                    var m = new bool[src.W, g[i].H];
                    for (int y = 0; y < g[i].H && y < src.H; y++)
                        for (int x = 0; x < src.W; x++) m[x, y] = set.Contains((src.T + y) * 16 + src.L + x);
                    masks[i] = m;
                }
                else
                {
                    int w = d == 1 ? (oneWidth > 0 ? oneWidth : Math.Max(1, width / 3)) : width, h = g[i].H;
                    var m = new bool[w, h];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++) m[x, y] = d == 1 && w <= 2 || Shapes[d][0][(y * 5 / h) * 3 + (x * 3 / w)] == '1';
                    masks[i] = m;
                }
            }
            // where each goes: rows keep their left edge and gaps, moved left if they no longer fit
            var left = new int[4];
            for (int i = 0; i < 4; i++)
            {
                bool rowStart = i == 0 || g[i].T > g[i - 1].B;
                left[i] = rowStart ? g[i].L : left[i - 1] + masks[i - 1].GetLength(0) + (g[i].L - g[i - 1].R - 1);
                // a thin one usually sits at the right of a full digit's space: a wider digit taking its place keeps
                // that right edge (and at least a pixel's gap) instead of the wide gap before the one
                if (!rowStart && old[i] == '1' && masks[i].GetLength(0) > g[i].W)
                {
                    int prevRight = left[i - 1] + masks[i - 1].GetLength(0) - 1;
                    left[i] = Math.Max(prevRight + 2, Math.Min(left[i], g[i].R - masks[i].GetLength(0) + 1));
                }
            }
            for (int i = 3; i >= 0; i--)
            {
                int right = left[i] + masks[i].GetLength(0) - 1;
                if (right <= 15) continue;
                int over = right - 15;
                for (int j = i; j >= 0; j--) { left[j] -= over; if (j == 0 || g[j].T > g[j - 1].B) break; }
            }
            foreach (int l in left) if (l < 0) return null;
            var nf = (int[])f.Clone();
            var cleared = new HashSet<int>();
            // digits drawn with an outline of one color (a black edge around each): the outline goes with the digit
            var outline = new int[4]; var diagonal = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                outline[i] = Outline(f, bg, g[i], out diagonal[i]);
                foreach (int p in g[i].Pixels) { nf[p] = bg; cleared.Add(p); }
                if (outline[i] >= 0)
                    foreach (int p in Ring(new HashSet<int>(g[i].Pixels), diagonal[i])) if (f[p] == outline[i]) { nf[p] = bg; cleared.Add(p); }
            }
            change = new Change { Old = g };
            var drawnAll = new HashSet<int>();
            for (int i = 0; i < 4; i++)
            {
                change.Looks[i] = Look(f, g[i]);
                change.Drawn[i] = new List<KeyValuePair<int, int>>();
                var m = masks[i];
                bool same = old[i] == now[i] && left[i] == g[i].L;
                for (int y = 0; y < m.GetLength(1); y++)
                {
                    int row = RowColor(f, g[i], g[i].T + y);
                    for (int x = 0; x < m.GetLength(0); x++)
                    {
                        if (!m[x, y]) continue;
                        int p = (g[i].T + y) * 16 + left[i] + x;
                        if (!cleared.Contains(p) && Far(f[p], bg) && !(edged && old[i] != now[i])) return null;   // something else is there
                        if (drawnAll.Contains(p)) return null;   // runs into another new digit
                        int color = same ? f[p] : row;
                        nf[p] = color;
                        drawnAll.Add(p);
                        change.Drawn[i].Add(new KeyValuePair<int, int>(p, color));
                    }
                }
                if (edged && old[i] != now[i])   // the new digit's own edge, over whatever was next to it
                {
                    var own = new HashSet<int>();
                    foreach (var kv in change.Drawn[i]) own.Add(kv.Key);
                    foreach (int p in Ring(own, false))
                        if (!drawnAll.Contains(p)) { nf[p] = bg; change.Drawn[i].Add(new KeyValuePair<int, int>(p, bg)); }
                }
                if (outline[i] < 0) continue;
                // the outline around the new digit, over background only
                var mine = new HashSet<int>();
                foreach (var kv in change.Drawn[i]) mine.Add(kv.Key);
                foreach (int p in Ring(mine, diagonal[i]))
                    if (nf[p] == bg || nf[p] == outline[i])
                    {
                        nf[p] = outline[i];
                        change.Drawn[i].Add(new KeyValuePair<int, int>(p, outline[i]));
                    }
            }
            return nf;
        }

        // the pixels touching a shape from outside: above, below and beside (and at the corners when diagonal)
        static List<int> Ring(HashSet<int> shape, bool diagonal)
        {
            var ring = new List<int>();
            var added = new HashSet<int>();
            foreach (int p in shape)
            {
                int x = p % 16, y = p / 16;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 || !diagonal && dx != 0 && dy != 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx > 15 || ny > 15) continue;
                        int q = ny * 16 + nx;
                        if (!shape.Contains(q) && added.Add(q)) ring.Add(q);
                    }
            }
            return ring;
        }

        // the color of a digit's outline: nearly all pixels touching it share one color that is neither the background
        // nor the digit's own (-1 when it has none); diagonal: the corners are outlined too
        static int Outline(int[] f, int bg, Glyph g, out bool diagonal)
        {
            diagonal = false;
            var shape = new HashSet<int>(g.Pixels);
            var ring = Ring(shape, false);
            if (ring.Count == 0) return -1;
            var counts = new Dictionary<int, int>();
            int best = -1, most = 0;
            foreach (int p in ring) { int n; counts.TryGetValue(f[p], out n); counts[f[p]] = ++n; if (n > most) { most = n; best = f[p]; } }
            if (most * 10 < ring.Count * 8 || !Far(best, bg) || !Far(best, f[g.Pixels[0]])) return -1;
            var corners = Ring(shape, true).FindAll(p => !ring.Contains(p));
            int on = corners.FindAll(p => f[p] == best).Count;
            diagonal = corners.Count > 0 && on * 10 >= corners.Count * 6;
            return best;
        }

        // a digit's pixels and colors, as text
        static string Look(int[] f, Glyph g)
        {
            var sb = new System.Text.StringBuilder();
            var px = new List<int>(g.Pixels); px.Sort();
            foreach (int p in px) sb.Append(p).Append(':').Append(f[p]).Append(',');
            return sb.ToString();
        }

        // a frame where the whole year couldn't be read: each old digit that is there exactly as in a changed frame
        // (same place, same pixels, same colors) changes the same way; null when none is
        static int[] Partial(int[] f, int bg, List<Change> changes)
        {
            foreach (var ch in changes)
            {
                int[] nf = null;
                var cleared = new HashSet<int>();
                for (int i = 0; i < 4; i++)
                {
                    var g = ch.Old[i];
                    bool there = true;
                    foreach (int p in g.Pixels) if (!Far(f[p], bg)) { there = false; break; }
                    if (!there || Look(f, g) != ch.Looks[i]) continue;
                    if (nf == null) nf = (int[])f.Clone();
                    foreach (int p in g.Pixels) { nf[p] = bg; cleared.Add(p); }
                }
                if (nf == null) continue;
                bool ok = true;
                for (int i = 0; i < 4 && ok; i++)
                {
                    var g = ch.Old[i];
                    bool wasThere = true;
                    foreach (int p in g.Pixels) if (!cleared.Contains(p)) { wasThere = false; break; }
                    if (!wasThere) continue;
                    foreach (var kv in ch.Drawn[i])
                    {
                        if (!cleared.Contains(kv.Key) && Far(f[kv.Key], bg)) { ok = false; break; }
                        nf[kv.Key] = kv.Value;
                    }
                }
                if (ok) return nf;
            }
            return null;
        }

        // a digit's most common color
        static int MainColor(int[] f, Glyph g)
        {
            var counts = new Dictionary<int, int>();
            int best = 0, most = 0;
            foreach (int p in g.Pixels) { int n; counts.TryGetValue(f[p], out n); counts[f[p]] = ++n; if (n > most) { most = n; best = f[p]; } }
            return best;
        }

        // the main color of a digit's row (gradients follow along), or of the whole digit
        static int RowColor(int[] f, Glyph g, int y)
        {
            var counts = new Dictionary<int, int>();
            int best = -1, most = 0;
            foreach (int p in g.Pixels)
            {
                if (p / 16 != y) continue;
                int n; counts.TryGetValue(f[p], out n); counts[f[p]] = ++n;
                if (n > most) { most = n; best = f[p]; }
            }
            if (best >= 0) return best;
            foreach (int p in g.Pixels) { int n; counts.TryGetValue(f[p], out n); counts[f[p]] = ++n; if (n > most) { most = n; best = f[p]; } }
            return best;
        }
    }
}
