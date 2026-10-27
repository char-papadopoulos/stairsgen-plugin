#!/usr/bin/env python3
"""Generate simple 32x32 PNG icons for the Stairs Rhino plugin (pure stdlib)."""
import zlib, struct, os, math

SIZE = 32

class Canvas:
    def __init__(self, size=SIZE):
        self.size = size
        self.px = [[(0, 0, 0, 0)] * size for _ in range(size)]

    def set(self, x, y, c):
        if 0 <= x < self.size and 0 <= y < self.size:
            self.px[y][x] = c

    def get(self, x, y):
        if 0 <= x < self.size and 0 <= y < self.size:
            return self.px[y][x]
        return (0, 0, 0, 0)

    def line(self, x0, y0, x1, y1, c, w=1):
        n = max(abs(x1 - x0), abs(y1 - y0), 1)
        for i in range(n + 1):
            x = x0 + (x1 - x0) * i / n
            y = y0 + (y1 - y0) * i / n
            if w <= 1:
                self.set(int(round(x)), int(round(y)), c)
            else:
                r = (w - 1) / 2
                for dx in range(-int(math.ceil(r)), int(math.ceil(r)) + 1):
                    for dy in range(-int(math.ceil(r)), int(math.ceil(r)) + 1):
                        if dx * dx + dy * dy <= r * r + 0.25:
                            self.set(int(round(x)) + dx, int(round(y)) + dy, c)

    def rect(self, x0, y0, x1, y1, c, fill=True):
        for x in range(min(x0, x1), max(x0, x1) + 1):
            for y in range(min(y0, y1), max(y0, y1) + 1):
                if fill or x in (x0, x1) or y in (y0, y1):
                    self.set(x, y, c)

    def poly(self, pts, c, fill=True):
        # scanline fill for closed polygon
        if fill:
            ys = [p[1] for p in pts]
            for y in range(min(ys), max(ys) + 1):
                xs = []
                for i in range(len(pts)):
                    x0, y0 = pts[i]
                    x1, y1 = pts[(i + 1) % len(pts)]
                    if (y0 <= y < y1) or (y1 <= y < y0):
                        xs.append(x0 + (y - y0) * (x1 - x0) / (y1 - y0))
                xs.sort()
                for k in range(0, len(xs) - 1, 2):
                    for x in range(int(math.ceil(xs[k])), int(math.floor(xs[k + 1])) + 1):
                        self.set(x, y, c)
        for i in range(len(pts)):
            a = pts[i]
            b = pts[(i + 1) % len(pts)]
            self.line(a[0], a[1], b[0], b[1], c, 1)

    def circle(self, cx, cy, r, c, fill=True):
        for x in range(self.size):
            for y in range(self.size):
                d = math.hypot(x - cx, y - cy)
                if fill:
                    if d <= r:
                        self.set(x, y, c)
                elif abs(d - r) < 0.7:
                    self.set(x, y, c)

    def digit(self, d, x, y, c, scale=2):
        F = {
            '0': [" ### ", "#   #", "#   #", "#   #", " ### "],
            '1': ["  #  ", " ##  ", "  #  ", "  #  ", " ### "],
            '2': ["#### ", "    #", "  ## ", " #   ", "#####"],
            '3': ["#### ", "    #", " ### ", "    #", "#### "],
            '4': ["   # ", "  ## ", " # # ", "#####", "   # "],
            '5': ["#####", "#    ", "#### ", "    #", "#### "],
            '6': [" ### ", "#    ", "#### ", "#   #", " ### "],
            '7': ["#####", "    #", "   # ", "  #  ", "  #  "],
            '8': [" ### ", "#   #", " ### ", "#   #", " ### "],
            '9': [" ### ", "#   #", " ####", "    #", " ### "],
        }
        rows = F[str(d)]
        for ry, row in enumerate(rows):
            for rx, ch in enumerate(row):
                if ch == '#':
                    for sy in range(scale):
                        for sx in range(scale):
                            self.set(x + rx * scale + sx, y + ry * scale + sy, c)

    def save(self, path):
        raw = b''
        for row in self.px:
            raw += b'\x00'
            for (r, g, b, a) in row:
                raw += bytes((r, g, b, a))
        def chunk(tag, data):
            out = struct.pack('>I', len(data)) + tag + data
            out += struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
            return out
        ihdr = struct.pack('>IIBBBBB', self.size, self.size, 8, 6, 0, 0, 0)
        png = b'\x89PNG\r\n\x1a\n'
        png += chunk(b'IHDR', ihdr)
        png += chunk(b'IDAT', zlib.compress(raw, 9))
        png += chunk(b'IEND', b'')
        with open(path, 'wb') as f:
            f.write(png)

# palette
BLUE = (44, 123, 229, 255)
DARK = (51, 63, 80, 255)
GREY = (130, 139, 150, 255)
LGREY = (200, 205, 210, 255)
GLASS = (150, 205, 255, 150)
REDD = (224, 74, 74, 255)
WHITE = (245, 247, 250, 255)
GOLD = (240, 180, 40, 255)

def stairs_profile(step=6, x0=4, y0=26, w=8, n=3):
    """Return polygon of a stair silhouette (bottom-left going right-up)."""
    pts = [(x0, y0)]
    x, y = x0, y0
    for i in range(n):
        pts.append((x, y - step))       # up
        y -= step
        pts.append((x + w, y))         # right
        x += w
    pts.append((x, y0))
    return pts

def ic_stairs():
    c = Canvas()
    c.poly(stairs_profile(), BLUE)
    c.line(0, 27, 31, 27, DARK, 1)
    return c

def ic_pick():
    c = Canvas()
    pts = []
    for t in range(0, 21):
        ang = math.pi * t / 20
        pts.append((6 + t, 24 - 12 * math.sin(ang) - 2 * t / 20))
    for i in range(len(pts) - 1):
        c.line(int(pts[i][0]), int(pts[i][1]), int(pts[i + 1][0]), int(pts[i + 1][1]), BLUE, 2)
    # cursor arrow
    c.poly([(22, 4), (22, 14), (25, 11), (27, 16), (29, 15), (27, 10), (30, 10)], DARK)
    return c

def ic_measure():
    c = Canvas()
    # diagonal ruler band
    c.poly([(3, 24), (24, 3), (29, 8), (8, 29)], DARK)
    c.poly([(6, 22), (22, 6), (26, 10), (10, 26)], WHITE)
    for i in range(5):
        ax, ay = 8 + i * 3, 20 - i * 3
        bx, by = ax - 3, ay + 3
        c.line(ax, ay, bx, by, DARK, 1)
    return c

def ic_flip():
    c = Canvas()
    c.line(4, 11, 26, 11, BLUE, 2)
    c.poly([(26, 6), (31, 11), (26, 16)], BLUE)
    c.line(27, 21, 5, 21, DARK, 2)
    c.poly([(5, 16), (0, 21), (5, 26)], DARK)
    return c

def ic_numbers():
    c = Canvas()
    c.digit(1, 3, 4, BLUE)
    c.digit(2, 14, 4, DARK)
    c.digit(3, 25, 4, BLUE)
    c.line(2, 20, 30, 20, LGREY, 1)
    c.line(2, 24, 30, 24, LGREY, 1)
    c.line(2, 28, 30, 28, LGREY, 1)
    return c

def ic_hide_numbers():
    c = ic_numbers()
    c.line(3, 29, 29, 2, REDD, 3)
    return c

def ic_reststep():
    c = Canvas()
    # stair, wide flat platform in middle
    c.poly([(3, 28), (3, 24), (7, 24), (7, 18), (11, 18), (11, 12),
            (21, 12), (21, 11), (22, 11), (22, 12), (23, 12), (23, 6), (27, 6),
            (27, 28)], BLUE)
    c.line(0, 29, 31, 29, DARK, 1)
    return c

def ic_slab():
    c = Canvas()
    pts = stairs_profile(step=7, x0=3, y0=27, w=9, n=3)
    c.poly(pts, BLUE)
    # sloped soffit line under steps
    c.line(3, 27, 30, 10, DARK, 1)
    return c

def ic_plank():
    c = Canvas()
    for i, y in enumerate((24, 15, 6)):
        x = 4 + i * 5
        c.rect(x, y, x + 11, y + 3, BLUE)
    c.line(0, 29, 31, 29, LGREY, 1)
    return c

def ic_railing():
    c = Canvas()
    c.line(3, 8, 28, 8, GREY, 2)          # handrail
    for x in (5, 14, 24):
        c.line(x, 8, x, 27, GREY, 2)     # posts
    c.line(0, 28, 31, 28, DARK, 2)       # floor
    return c

def ic_glass():
    c = Canvas()
    c.rect(4, 4, 27, 26, DARK, fill=False)
    for x in (12, 19):
        c.line(x, 4, x, 26, DARK, 1)
    for x0 in (4, 13, 20):
        for y in range(5, 26):
            for xx in range(x0 + 1, min(x0 + 8, 27)):
                c.set(xx, y, GLASS)
    return c

def ic_metal():
    c = Canvas()
    c.line(3, 6, 28, 6, GREY, 2)
    c.line(3, 12, 28, 12, GREY, 2)
    c.line(3, 18, 28, 18, GREY, 2)
    for x in (5, 26):
        c.line(x, 4, x, 27, GREY, 2)
    c.line(0, 28, 31, 28, DARK, 2)
    return c

def ic_cement():
    c = Canvas()
    c.rect(6, 18, 26, 27, GREY)           # the wall stops short of the handrail
    for x in (8, 12, 16, 20, 24):         # balusters fill the gap
        c.line(x, 18, x, 7, DARK, 1)
    c.line(5, 7, 27, 7, DARK, 3)          # handrail
    c.line(0, 28, 31, 28, DARK, 2)
    return c

def ic_plus():
    c = Canvas()
    c.line(10, 16, 22, 16, BLUE, 3)
    c.line(16, 10, 16, 22, BLUE, 3)
    return c

def ic_minus():
    c = Canvas()
    c.line(10, 16, 22, 16, REDD, 3)
    return c

def ic_auto():
    c = Canvas()
    # stair silhouette with a sparkle: configured automatically
    c.poly(stairs_profile(step=6, x0=2, y0=29, w=6, n=3), BLUE)
    c.line(23, 2, 23, 14, GOLD, 2)
    c.line(17, 8, 29, 8, GOLD, 2)
    c.line(20, 5, 26, 11, GOLD, 1)
    c.line(26, 5, 20, 11, GOLD, 1)
    return c

def ic_info():
    c = Canvas()
    c.circle(16, 16, 13, DARK, fill=False)
    c.circle(16, 10, 2, DARK)
    c.line(16, 14, 16, 22, DARK, 2)
    return c

ICONS = {
    'stairs': ic_stairs,
    'auto': ic_auto,
    'pick': ic_pick,
    'measure': ic_measure,
    'flip': ic_flip,
    'numbers': ic_numbers,
    'hide_numbers': ic_hide_numbers,
    'reststep': ic_reststep,
    'slab': ic_slab,
    'plank': ic_plank,
    'railing': ic_railing,
    'glass': ic_glass,
    'metal': ic_metal,
    'cement': ic_cement,
    'plus': ic_plus,
    'minus': ic_minus,
    'info': ic_info,
}

def write_ico(pngs, path):
    """Minimal .ico with PNG-compressed entries."""
    header = struct.pack('<HHH', 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    entries = b''
    data = b''
    for (w, png) in pngs:
        entries += struct.pack('<BBBBHHII', w if w < 256 else 0, 0, 0, 0,
                               1, 32, len(png), offset)
        data += png
        offset += len(png)
    with open(path, 'wb') as f:
        f.write(header + entries + data)

if __name__ == '__main__':
    out = os.path.join(os.path.dirname(__file__), '..', 'src', 'StairsPlugin', 'assets', 'icons')
    out = os.path.normpath(out)
    os.makedirs(out, exist_ok=True)
    for name, fn in ICONS.items():
        fn().save(os.path.join(out, name + '.png'))
    # plugin icon: .ico with several PNG entries
    def png_bytes(size):
        for maker in [ic_stairs]:
            c = maker()
            # crude downscale for small sizes from the 32px master
            if size < 32:
                small = Canvas(size)
                for y in range(size):
                    for x in range(size):
                        small.set(x, y, c.get(x * 32 // size, y * 32 // size))
                import io
                p = os.path.join(out, '_tmp.png')
                small.save(p)
                data = open(p, 'rb').read()
                os.remove(p)
                return data
            elif size == 32:
                p = os.path.join(out, 'stairs.png')
                return open(p, 'rb').read()
            else:
                big = Canvas(size)
                for y in range(size):
                    for x in range(size):
                        big.set(x, y, c.get(x * 32 // size, y * 32 // size))
                p = os.path.join(out, '_tmp.png')
                big.save(p)
                data = open(p, 'rb').read()
                os.remove(p)
                return data
        raise RuntimeError()
    write_ico([(16, png_bytes(16)), (24, png_bytes(24)), (32, png_bytes(32)), (48, png_bytes(48)), (256, png_bytes(256))],
              os.path.join(out, 'StairsPlugin.ico'))
    print('icons written to', out)
    print(sorted(os.listdir(out)))
