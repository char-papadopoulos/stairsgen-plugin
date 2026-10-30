#!/usr/bin/env python3
"""Draws the stair dialog's picture-button icons as light- and dark-theme SVGs in
src/StairsPlugin/assets/stairs. Pure stdlib; rerun after changing a shape or a colour:

    python3 tools/make_stair_icons.py

- role_mid, role_inner, role_outer: an L-shaped stair in plan, with the picked curve (blue)
  along its middle, its inner edge or its outer edge.
- struct_slab, struct_plank: a flight in side view, as a concrete slab or as floating planks.
- rail_none: the bare balcony edge of the railing icons (src/StairsPlugin/assets/railings),
  for "no railing" next to them.
"""
import math, os

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "HarisBimTools", "assets", "stairs")

THEMES = {
    "": {"stroke": "#3C3C3C", "band": "#E2E2E2", "tread": "#9A9A9A", "curve": "#2F80ED",
         "concrete": "#BDBDB8", "concrete_dark": "#A7A7A2", "wood": "#C99A63", "metal": "#3C4654",
         "slab": "#D9D9D9", "slab_side": "#B9B9B9"},
    "_dark": {"stroke": "#1E1E1E", "band": "#9A9A9A", "tread": "#5E5E5E", "curve": "#5CA8FF",
              "concrete": "#8A8A85", "concrete_dark": "#72726D", "wood": "#B08454", "metal": "#D2D8E0",
              "slab": "#9A9A9A", "slab_side": "#7C7C7C"},
}


def f(v):
    return ("%.2f" % v).rstrip("0").rstrip(".")


def poly(pts, fill, stroke, sw=1.0):
    d = " ".join(("M" if i == 0 else "L") + "%s %s" % (f(x), f(y)) for i, (x, y) in enumerate(pts)) + "Z"
    return '  <path d="%s" fill="%s" stroke="%s" stroke-width="%s" stroke-linejoin="round"/>' % (d, fill, stroke, f(sw))


def line(pts, stroke, sw, cap="round"):
    d = " ".join(("M" if i == 0 else "L") + "%s %s" % (f(x), f(y)) for i, (x, y) in enumerate(pts))
    return '  <path d="%s" fill="none" stroke="%s" stroke-width="%s" stroke-linecap="%s" stroke-linejoin="round"/>' % (
        d, stroke, f(sw), cap)


def svg(parts):
    return '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">\n%s\n</svg>\n' % "\n".join(parts)


# --- the curve: an L-shaped stair in plan, walking up then right --------------------------
X0, X1 = 6, 20      # the up leg spans x 6..20
Y0, Y1 = 6, 20      # the right leg spans y 6..20 (y grows downward)
BOTTOM, RIGHT = 44, 44


def role(kind, t):
    out = [poly([(X0, BOTTOM), (X1, BOTTOM), (X1, Y1), (RIGHT, Y1), (RIGHT, Y0), (X0, Y0)], t["band"], t["stroke"])]
    treads = [line([(X0, y), (X1, y)], t["tread"], 1) for y in (38, 32, 26)]
    treads += [line([(x, Y0), (x, Y1)], t["tread"], 1) for x in (26, 32, 38)]
    treads += [line([(X1, Y1), (X0, 13)], t["tread"], 1), line([(X1, Y1), (13, Y0)], t["tread"], 1)]
    out += treads
    at = {"mid": ((X0 + X1) / 2, (Y0 + Y1) / 2), "inner": (X1, Y1), "outer": (X0, Y0)}[kind]
    x, y = at
    out.append(line([(x, BOTTOM - 1), (x, y), (RIGHT - 1, y)], t["curve"], 2.6))
    return out


# --- the structure: a flight of four steps in side view, rising to the right ----------------
RISE, RUN, BASE, LEFT = 8, 9, 42, 4


def nosings():
    return [(LEFT + i * RUN, BASE - (i + 1) * RISE) for i in range(4)]


def structure(kind, t):
    out = []
    if kind == "slab":
        top = [(LEFT, BASE)]
        for i, (x, y) in enumerate(nosings()):
            top += [(x, y), (x + RUN if i < 3 else 44, y)]
        last_y = top[-1][1]
        # the soffit runs parallel to the flight, a slab's depth below the nosings
        out.append(poly(top + [(44, last_y + 7), (LEFT + 13, BASE)], t["concrete"], t["stroke"]))
    else:
        out.append(line([(LEFT + 3, BASE), (40, BASE - 30)], t["metal"], 2.2))  # the stringer under the planks
        for i, (x, y) in enumerate(nosings()):
            w = RUN + 2 if i < 3 else 44 - x
            out.append(poly([(x, y), (x + w, y), (x + w, y + 3), (x, y + 3)], t["wood"], t["stroke"]))
    return out


# --- no railing: the balcony edge of the railing icons, bare --------------------------------
L, DEPTH, SLAB = 2.0, 0.9, 0.14  # as in tools/make_railing_icons.py
SIZE, MARGIN = 48, 2.5


def iso(p):
    x, y, z = p
    c, s = math.cos(math.radians(30)), math.sin(math.radians(30))
    return ((x - y) * c, -(x + y) * s - z)


def rail_none(t):
    top = [(0, 0, 0), (L, 0, 0), (L, DEPTH, 0), (0, DEPTH, 0)]
    front = [(0, 0, -SLAB), (L, 0, -SLAB), (L, 0, 0), (0, 0, 0)]
    side = [(L, 0, -SLAB), (L, DEPTH, -SLAB), (L, DEPTH, 0), (L, 0, 0)]
    # framed like the railing icons: their railing reaches 0.85 above the slab
    frame = [iso(p) for p in top + front + side + [(0, 0, 0.85), (L, 0, 0.85), (0, DEPTH, 0.85)]]
    xs, ys = [p[0] for p in frame], [p[1] for p in frame]
    k = (SIZE - 2 * MARGIN) / max(max(xs) - min(xs), max(ys) - min(ys))
    ox = (SIZE - (max(xs) - min(xs)) * k) / 2 - min(xs) * k
    oy = (SIZE - (max(ys) - min(ys)) * k) / 2 - min(ys) * k
    P = lambda face: [(iso(p)[0] * k + ox, iso(p)[1] * k + oy) for p in face]
    out = [poly(P(top), t["slab"], t["stroke"]), poly(P(front), t["slab_side"], t["stroke"]),
           poly(P(side), t["slab_side"], t["stroke"])]
    # a slashed circle above the edge: nothing there
    cx, cy = (SIZE / 2, SIZE / 2 - 9)
    out.append('  <circle cx="%s" cy="%s" r="8" fill="none" stroke="%s" stroke-width="2"/>' % (f(cx), f(cy), t["tread"]))
    out.append(line([(cx - 5.6, cy + 5.6), (cx + 5.6, cy - 5.6)], t["tread"], 2))
    return out


def main():
    os.makedirs(OUT, exist_ok=True)
    for suffix, t in THEMES.items():
        icons = {"role_" + k: role(k, t) for k in ("mid", "inner", "outer")}
        icons.update({"struct_" + k: structure(k, t) for k in ("slab", "plank")})
        icons["rail_none"] = rail_none(t)
        for name, parts in icons.items():
            with open(os.path.join(OUT, name + suffix + ".svg"), "w") as fh:
                fh.write(svg(parts))
    print("wrote", len(os.listdir(OUT)), "icons to", os.path.normpath(OUT))


if __name__ == "__main__":
    main()
