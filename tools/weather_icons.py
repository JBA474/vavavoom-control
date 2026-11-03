"""Animated 10x10 weather icons for the AuraBox (3-bit palette).

Run:  python -X utf8 weather_icons.py            -> print every icon as text
      python -X utf8 weather_icons.py --png out  -> also write an LED-style preview sheet
      python -X utf8 weather_icons.py --cs       -> print C# table for AuraBoxControl.cs
"""
import sys

# 0 off, 1 red, 2 green, 3 yellow, 4 blue, 5 magenta, 6 cyan, 7 white
K, R, G, Y, B, M, C, W = range(8)
RGB = [(0, 0, 0), (255, 0, 0), (0, 255, 0), (255, 255, 0), (0, 0, 255), (255, 0, 255), (0, 255, 255), (255, 255, 255)]


def blank():
    return [[K] * 10 for _ in range(10)]


def stamp(g, rows, x0=0, y0=0, color=None):
    """Draw a shape given as strings; '#' = color (or digit = palette index), '.' = transparent."""
    for y, row in enumerate(rows):
        for x, ch in enumerate(row):
            if ch == '.':
                continue
            xx, yy = x0 + x, y0 + y
            if 0 <= xx < 10 and 0 <= yy < 10:
                g[yy][xx] = color if ch == '#' else int(ch)
    return g


SUN_DISK = [".####.", "######", "######", "######", "######", ".####."]
CRESCENT = ["...3333...", ".3333.....", ".333......", "333.......", "333.......",
            "333.......", "333.......", ".333......", ".3333.....", "...3333..."]
SMALL_CRESCENT = ["..333", ".33..", "33...", "33...", ".33..", "..333"]
CLOUD = ["...##.....", "..####.##.", ".########.", "##########", ".########."]
SMALL_CLOUD = ["..##.##.", ".#######", "########", ".######."]
BOLT = ["..##", ".##.", "####", ".##.", "##..", "#..."]


def sun(rays):
    g = stamp(blank(), SUN_DISK, 2, 2, Y)
    if rays == 0:   # straight rays
        pts = [(4, 0), (5, 0), (4, 9), (5, 9), (0, 4), (0, 5), (9, 4), (9, 5)]
    else:           # diagonal rays
        pts = [(0, 0), (1, 1), (9, 0), (8, 1), (0, 9), (1, 8), (9, 9), (8, 8)]
    for x, y in pts:
        g[y][x] = Y
    return g


def moon(twinkle):
    g = stamp(blank(), CRESCENT, 1, 0)
    for x, y in ([(8, 1), (6, 5), (9, 7)] if twinkle == 0 else [(9, 3), (7, 8), (6, 2)]):
        g[y][x] = W
    return g


def partly(rays, night=False):
    g = blank()
    if night:
        stamp(g, SMALL_CRESCENT, 0, 0)
        g[1 if rays == 0 else 3][6 if rays == 0 else 8] = W
    else:
        stamp(g, [".##.", "####", "####", ".##."], 2, 2, Y)
        pts = [(3, 0), (4, 0), (0, 3), (0, 4), (6, 3)] if rays == 0 else [(0, 0), (1, 1), (6, 1), (7, 0), (1, 6)]
        for x, y in pts:
            g[y][x] = Y
    return stamp(g, SMALL_CLOUD, 2, 5, W)


def cloud(dx):
    return stamp(blank(), CLOUD, dx, 2, W)


def falling(g, color, cols, phase, length):
    for x, off in cols:
        for k in range(length):
            g[5 + (phase + off + k) % 5][x] = color
    return g


def rain(phase):
    return falling(stamp(blank(), CLOUD, 0, 0, W), C, [(1, 0), (4, 3), (7, 1)], phase, 2)


def snow(phase):
    g = stamp(blank(), CLOUD, 0, 0, W)
    for x, off in [(1, 0), (5, 3), (8, 1), (3, 2)]:
        y = 5 + (phase + off) % 5
        g[y][x + (y % 2)] = W
    return g


def storm(flash):
    g = stamp(blank(), CLOUD, 0, 0, W if flash else B)
    if flash:
        stamp(g, BOLT, 3, 4, Y)
    return falling(g, C, [(1, 1), (8, 3)], flash * 2, 1)


def fog(phase):
    g = blank()
    for i, y in enumerate((1, 3, 5, 7)):
        start = phase if i % 2 == 0 else 3 - phase
        for x in range(start, start + 7):
            g[y][x] = W
    return g


ICONS = {
    'sun':          ([sun(0), sun(1)], 'clear day'),
    'moon':         ([moon(0), moon(1)], 'clear night'),
    'partly':       ([partly(0), partly(1)], 'mainly clear / partly cloudy (day)'),
    'partly_night': ([partly(0, True), partly(1, True)], 'partly cloudy (night)'),
    'cloud':        ([cloud(0), cloud(0), cloud(-1), cloud(-1), cloud(0), cloud(0)], 'overcast'),
    'rain':         ([rain(p) for p in range(5)], 'drizzle / rain / showers'),
    'snow':         ([snow(p) for p in range(5)], 'snow'),
    'storm':        ([storm(0), storm(0), storm(0), storm(1), storm(0), storm(1), storm(0), storm(0)], 'thunderstorm'),
    'fog':          ([fog(p) for p in (0, 1, 2, 3, 2, 1)], 'fog'),
}


def flat(g):
    return [c for row in g for c in row]


def show_text():
    ch = '.RGYBMCW'
    for name, (frames, desc) in ICONS.items():
        print(f'== {name} ({desc}), {len(frames)} frames')
        for y in range(10):
            print('  '.join(''.join(ch[f[y][x]] * 2 for x in range(10)) for f in frames[:6]))


def save_png(path):
    from PIL import Image, ImageDraw
    cell, pad, label = 14, 18, 16
    cols = max(len(f) for f, _ in ICONS.values())
    w = pad + cols * (10 * cell + pad) + 260
    h = pad + len(ICONS) * (10 * cell + pad + label)
    img = Image.new('RGB', (w, h), (24, 24, 28))
    d = ImageDraw.Draw(img)
    for r, (name, (frames, desc)) in enumerate(ICONS.items()):
        y0 = pad + r * (10 * cell + pad + label)
        d.text((pad, y0), f'{name} - {desc}', fill=(220, 220, 220))
        for i, f in enumerate(frames):
            x0 = pad + i * (10 * cell + pad)
            d.rectangle([x0 - 3, y0 + label - 3, x0 + 10 * cell + 2, y0 + label + 10 * cell + 2], fill=(0, 0, 0))
            for y in range(10):
                for x in range(10):
                    c = RGB[f[y][x]] if f[y][x] else (30, 30, 30)
                    cx, cy = x0 + x * cell, y0 + label + y * cell
                    d.ellipse([cx + 1, cy + 1, cx + cell - 2, cy + cell - 2], fill=c)
    img.save(path)
    print('wrote', path)


def print_cs():
    for name, (frames, _) in ICONS.items():
        rows = ', '.join('"' + ''.join(str(c) for c in flat(f)) + '"' for f in frames)
        print(f'            {{ "{name}", new[] {{ {rows} }} }},')


if __name__ == '__main__':
    a = sys.argv[1:]
    if '--cs' in a:
        print_cs()
    else:
        show_text()
        if '--png' in a:
            save_png(a[a.index('--png') + 1])
