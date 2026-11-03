"""Original 'girder runner' animations for the AuraBox (our own character, not from any game).

Run:  python -X utf8 originals.py          -> print the frames as text
      python -X utf8 originals.py --line > ..\src\extras.txt   (then tools\build.cmd embeds it)
"""
import sys

ch = '.RGYBMCW'
GAP = (4, 5)          # girder gap columns
# 3x5 sprites, digit = palette color (3 yellow head, 6 cyan shirt, 7 white legs), '.' = transparent
RUN = [[".3.", "666", ".6.", ".7.", "7.7"],
       [".3.", "666", ".6.", ".7.", ".7."]]
JUMP = ["636", ".6.", ".6.", "7.7"]   # arms up, legs tucked
TURN = [".3.", "666", ".6.", ".7.", "7.7"]


def scene(x, lift, sprite):
    g = [[0] * 10 for _ in range(10)]
    for c in range(10):
        if c not in GAP:
            g[9][c] = 1                     # red girder
            if c % 2 == 0:
                g[8][c] = 1                 # rivets
    top = 8 - len(sprite) - lift
    for y, row in enumerate(sprite):
        for dx, v in enumerate(row):
            if v != '.' and 0 <= top + y < 10 and 0 <= x + dx < 10:
                g[top + y][x + dx] = int(v)
    return g


def pass_right():
    """(x, lift, sprite) steps for one crossing left to right: run, jump the gap sideways, land, run."""
    steps = [(0, 0, RUN[0]), (1, 0, RUN[1])]
    for x, lift in zip((2, 3, 4, 5), (1, 2, 2, 1)):
        steps.append((x, lift, JUMP))
    steps += [(6, 0, RUN[0]), (7, 0, RUN[1])]
    return steps


def back_and_forth():
    right = pass_right()
    left = [(7 - x, lift, sp) for x, lift, sp in right]     # mirror the path (sprites are symmetric)
    return right + [(7, 0, TURN)] + left + [(0, 0, TURN)]


def on_box():
    """8 frames: one rightward crossing; the loop wraps him back to the left edge."""
    return pass_right()


def hyperspace_frames():
    """8-frame seamless loop: 12 stars on evenly spaced rays, each restarting at the center every 8 frames
    (staggered), accelerating outward and leaving a streak. Blue far, cyan nearer, white rushing past."""
    import math
    dist = [0.9 * 1.4 ** p for p in range(8)]          # 0.9 ... 9.5: the last step leaves the screen
    frames = []
    for f in range(8):
        g = [[0] * 10 for _ in range(10)]

        def plot(a, d, c):
            x, y = math.floor(5 + math.cos(a) * d), math.floor(5 + math.sin(a) * d)
            if 0 <= x < 10 and 0 <= y < 10 and g[y][x] < c:
                g[y][x] = c
        for k in range(12):
            a = k * math.pi / 6 + 0.26
            p = (f + k * 5) % 8                          # stagger so stars are spread over all distances
            d = dist[p]
            if p == 0:
                continue                                 # newborn star: keep the center dark
            if d > 2.2 and p > 0:
                plot(a, (d + dist[p - 1]) / 2, 6 if d > 3.5 else 4)
            plot(a, d, 4 if d < 1.8 else 6 if d < 3.2 else 7)
        frames.append(g)
    return frames


def packed(fr):
    # gallery format: 2 pixels per byte, first pixel in the low nibble, as hex (same as the app's Proto.Pack)
    px = [v for row in fr for v in row]
    return bytes(px[k] | (px[k + 1] << 4) for k in range(0, 100, 2)).hex()


ITEMS = [("Girder runner", [scene(*s) for s in back_and_forth()]),
         ("Girder runner (on box)", [scene(*s) for s in on_box()]),
         ("Hyperspace (on box)", hyperspace_frames())]

if __name__ == '__main__':
    for name, frames in ITEMS:
        if '--line' in sys.argv:
            print(f'I|Originals|{name}|' + ','.join(packed(f) for f in frames))
            continue
        print(f'== {name}: {len(frames)} frames')
        for start in range(0, len(frames), 9):
            chunk = frames[start:start + 9]
            for y in range(10):
                print('  '.join(''.join(ch[v] for v in f[y]) for f in chunk))
            print()
