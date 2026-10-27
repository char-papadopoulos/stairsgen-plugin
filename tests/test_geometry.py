#! python3
"""
Regression suite for the Stairs plug-in geometry. Runs inside Rhino through the
script server; use tests/run_tests.sh, which builds the DLL, runs this script
and prints the result. Pure geometry checks: nothing is added to the open document.

Every scenario is built three times: in metres with a 1 mm and with a 1 cm tolerance,
and in millimetres (tolerance 0.01 mm). Each build is checked for:
  - slab: exactly one valid closed solid; plank: one valid closed solid per tread
  - the expected tread count, and one number per tread
  - railing pieces on the requested sides only, every piece a valid brep
  - treads tiling the stair in plan (checked on the plank build's tread tops):
    no overlaps, no gaps or holes; on polyline curves every tread's area is
    width x its walking-line depth, also for winders
  - every tread's average depth (area / mean nosing length) at least 18 cm, and
    treads only ever widened beyond the regular depth, never narrowed
  - pyramid stairs: every tread's width, the side that stays straight, and railing
    posts standing the inset inside the stair's edge (which narrows tread by tread)
  - the depth rule: comfort (riser + tread = 46 cm), custom, or fitted so the last
    step ends exactly at the curve end; autoconfigure (height only) fits the curve,
    keeps risers within 12-20 cm and riser + tread near 46 cm, and places a rest
    step after every 15 steps
  - the expected warnings, and no others
run_tests.sh passes the DLL to test in /tmp/stairs_test_config.json:
{"dll": absolute path, "only": [name filters]}.
"""
import clr, System, traceback, time, math, json, os
clr.AddReference("RhinoCommon")
import Rhino
from Rhino import RhinoDoc
from Rhino.Geometry import (Point3d, Vector3d, Plane, Arc, ArcCurve, LineCurve, PolylineCurve, PolyCurve,
                            NurbsCurve, Curve, AreaMassProperties, Transform, BoundingBox)

OUT = "/tmp/stairs_test_result.txt"
CONFIG = "/tmp/stairs_test_config.json"
PROFILES = [("m", 1.0, 0.001), ("mm", 1000.0, 0.01), ("m 1cm", 1.0, 0.01)]  # name, length scale from metres, absolute tolerance

# stair defaults (metres), shared by all scenarios
WIDTH, STEP_H, COMFORT, SLAB_T, PLANK_T = 1.0, 0.17, 0.46, 0.15, 0.04
MIN_AVG = 0.18  # minimum average tread depth: area / mean nosing length
MIN_RISER, MAX_RISER, REST_EVERY = 0.12, 0.20, 15  # autoconfigure
MIN_STEP_WIDTH = 0.30  # pyramid stairs
GAP, BALUSTER, BALUSTER_SPACING = 0.10, 0.03, 0.30  # concrete railing: open height under the handrail, its bars


# --- curves, in metres -----------------------------------------------------------------

def line(length):
    return LineCurve(Point3d(0, 0, 0), Point3d(length, 0, 0))

def arc(radius, degrees, right=False):
    """Flat arc from the origin heading +X, turning left (or right)."""
    plane = Plane(Point3d(0, radius, 0), Vector3d.ZAxis)  # the vector is the plane NORMAL
    plane.Rotate(-math.pi / 2, Vector3d.ZAxis)
    c = ArcCurve(Arc(plane, radius, math.radians(degrees)))
    if right:
        c.Transform(Transform.Mirror(Plane.WorldZX))
    return c

def polyline(*pts):
    return PolylineCurve([Point3d(x, y, 0) for x, y in pts])

def corner(l1, degrees, l2):
    """Two straight legs with a sharp turn (+ = left)."""
    a = math.radians(degrees)
    return polyline((0, 0), (l1, 0), (l1 + l2 * math.cos(a), l2 * math.sin(a)))

def arc_then_corner():
    """A left arc meeting a straight leg at a sharp 90-degree kink."""
    a = arc(3, 60)
    end, tan = a.PointAtEnd, a.TangentAtEnd
    pc = PolyCurve()
    pc.Append(a)
    pc.Append(LineCurve(end, end + Vector3d(-tan.Y, tan.X, 0) * 3))
    return pc

def s_curve():
    pts = [Point3d(0, 0, 0), Point3d(2, 0, 0), Point3d(3, 1.5, 0), Point3d(5, 2.5, 0), Point3d(7, 2.5, 0)]
    return NurbsCurve.CreateInterpolatedCurve(pts, 3)


# --- scenarios: (name, curve, options, expectations) ------------------------------------
# options: role mid|inner|outer, structure slab|plank, rail none|left|right|both,
#          rail_type glass|metal|cement, rest [(step, depth)], flip, height, depth (custom),
#          width, pyramid (narrowing per step, center|left|right), mode comfort|fit|custom, steps (step count, as the dialog's Steps field), auto (autoconfigure)
# expectations: warn [substrings], steps, tiling (default True), comfort (autoconfigure: riser +
#               tread within 3 cm of the comfort sum, default True), regular (polyline, and no
#               pyramid narrowing in a corner fan: tread
#               area == width x walking-line depth), analytic_volume, outside_of (center, radius: tread
#               centroids must lie outside that circle)

OVERFLOW = "needs more run"

def build_scenarios():
    scenarios = [
        ("straight slab", line(6), {}, {"analytic_volume": True}),
        ("straight plank metal left", line(6), {"structure": "plank", "rail": "left", "rail_type": "metal"}, {"regular": True}),
        ("straight slab cement right", line(6), {"rail": "right", "rail_type": "cement"}, {"regular": True}),
        ("straight slab glass both", line(6), {"rail": "both", "rail_type": "glass"}, {"regular": True}),
        ("straight flip outer plank", line(6), {"flip": True, "role": "outer", "structure": "plank"}, {"regular": True}),
        ("straight depth override", line(6), {"depth": 0.25, "structure": "plank"}, {"regular": True}),
        ("straight rest steps metal both", line(8), {"rest": [(5, 0.0), (10, 1.5)], "rail": "both", "rail_type": "metal"}, {"regular": True}),
        ("short curve overflow", line(1), {}, {"warn": [OVERFLOW], "steps": 3}),
        ("depth override below minimum", line(6), {"depth": 0.12, "structure": "plank"}, {"warn": ["below the minimum average"], "regular": True}),

        ("arc left mid glass both", arc(3, 120), {"rail": "both", "rail_type": "glass"}, {}),
        ("arc right mid metal both", arc(3, 120, right=True), {"rail": "both", "rail_type": "metal"}, {}),
        ("arc inner cement both", arc(3, 120), {"role": "inner", "rail": "both", "rail_type": "cement"}, {}),
        ("spiral cement both", arc(2, 270), {"rail": "both", "rail_type": "cement"}, {}),
        ("arc outer plank", arc(3, 120), {"role": "outer", "structure": "plank"}, {}),
        ("arc right inner plank", arc(3, 120, right=True), {"role": "inner", "structure": "plank"}, {}),
        ("arc flip slab", arc(3, 120), {"flip": True}, {}),
        ("arc over 180 inner", arc(1, 200), {"role": "inner"}, {"warn": [OVERFLOW], "steps": 11, "outside_of": ((0, 1), 1)}),
        ("arc tall 300 glass both", arc(3, 300), {"height": 8.0, "rail": "both", "rail_type": "glass"}, {}),
        ("s-curve metal both", s_curve(), {"rail": "both", "rail_type": "metal"}, {}),
        ("arc rest step glass both", arc(3, 160), {"rest": [(7, 0.0)], "rail": "both", "rail_type": "glass"}, {}),
        ("arc into corner", arc_then_corner(), {}, {}),
        ("arc 170 wide metal both", arc(3.2, 170), {"width": 1.1, "height": 3.4, "rail": "both", "rail_type": "metal"}, {}),

        ("pyramid center slab", line(6), {"pyramid": (0.03, "center")}, {"analytic_volume": True, "regular": True}),
        ("pyramid left plank metal both", line(6), {"pyramid": (0.03, "left"), "structure": "plank", "rail": "both", "rail_type": "metal"}, {"regular": True, "posts_on_edge": True}),
        ("pyramid right glass both", line(6), {"pyramid": (0.03, "right"), "rail": "both", "rail_type": "glass"}, {"analytic_volume": True, "regular": True, "posts_on_edge": True}),
        ("pyramid clamped at minimum width", line(6), {"pyramid": (0.1, "center")}, {"warn": ["Pyramid width"], "analytic_volume": True, "regular": True}),
        ("pyramid corner, inner side straight", corner(3.5, 90, 3.5), {"pyramid": (0.02, "left"), "rail": "both", "rail_type": "glass"}, {}),
        ("pyramid corner, inner side narrows", corner(3.5, 90, 3.5), {"pyramid": (0.02, "right"), "rail": "both", "rail_type": "cement"}, {}),
        ("pyramid arc cement both", arc(3, 120), {"pyramid": (0.02, "center"), "rail": "both", "rail_type": "cement"}, {}),
        ("pyramid autoconfigure zigzag", polyline((0, 0), (0, 3), (2.4, 0.4), (2.4, 6), (6, 6)), {"auto": True, "height": 7.0, "pyramid": (0.01, "right"), "rail": "left", "rail_type": "metal"}, {}),

        ("U-turn metal both", polyline((0, 0), (3, 0), (3, 1.2), (0, 1.2)), {"height": 3.4, "rail": "both", "rail_type": "metal"}, {"regular": True}),
        ("square spiral glass both", polyline((0, 0), (3, 0), (3, 3), (0, 3), (0, 0.8)), {"height": 6.0, "rail": "both", "rail_type": "glass"}, {"regular": True}),
        ("U-turn too tight", polyline((0, 0), (3, 0), (3, 0.6), (0, 0.6)), {"height": 3.4, "structure": "plank"}, {"warn": ["too close together"], "tiling": False}),
        ("corner near start metal both", corner(0.3, 90, 6), {"structure": "plank", "rail": "both", "rail_type": "metal"}, {"regular": True}),
        ("corner near end", corner(5.2, 90, 0.2), {"structure": "plank"}, {"regular": True}),
        ("zigzag", polyline((0, 0), (2.5, 0), (2.5, 2), (5, 2)), {}, {"regular": True}),
        ("rest step on corner metal right", corner(3.5, 90, 3.5), {"rest": [(11, 1.2)], "rail": "right", "rail_type": "metal"}, {"regular": True}),
        ("corner +160 mid", corner(5, 160, 5), {"structure": "plank"}, {"regular": True}),
        ("double acute zigzag glass both", polyline((0, 0), (0, 3), (2.4, 0.4), (2.4, 6), (6, 6)), {"height": 7.0, "rail": "both", "rail_type": "glass"}, {"regular": True}),

        ("steps field 20", line(7), {"steps": 20}, {"steps": 20, "regular": True}),
        ("fit straight", line(6), {"mode": "fit"}, {"regular": True}),
        ("fit arc glass both", arc(3, 120), {"mode": "fit", "rail": "both", "rail_type": "glass"}, {}),
        ("fit corner metal both", corner(3.5, 90, 3.5), {"mode": "fit", "rail": "both", "rail_type": "metal"}, {"regular": True}),
        ("fit double acute zigzag", polyline((0, 0), (0, 3), (2.4, 0.4), (2.4, 6), (6, 6)), {"mode": "fit", "height": 7.0, "structure": "plank"}, {"regular": True}),
        ("fit too short", line(3), {"mode": "fit"}, {"warn": [OVERFLOW], "steps": 16}),
        ("autoconfigure straight", line(6), {"auto": True}, {"regular": True}),
        ("autoconfigure corner metal both", corner(3.5, 90, 3.5), {"auto": True, "rail": "both", "rail_type": "metal"}, {"regular": True}),
        ("autoconfigure tall arc", arc(3, 300), {"auto": True, "height": 8.0}, {}),
        ("autoconfigure double acute zigzag", polyline((0, 0), (0, 3), (2.4, 0.4), (2.4, 6), (6, 6)), {"auto": True, "height": 7.0, "rail": "both", "rail_type": "glass"}, {"regular": True}),
        ("autoconfigure long low curve", line(20), {"auto": True, "height": 1.0, "structure": "plank"}, {"regular": True, "comfort": False}),
        # square corner landings: a rest step that reaches a sharp corner fills it, square to both flights
        ("square landing inner L fit", polyline((0, 0), (0, 3.5), (2.2, 3.5)), {"role": "inner", "rest": [(11, 0.0)], "mode": "fit", "steps": 17}, {"square_landing": [11], "regular": True}),
        ("square landing corner comfort", corner(3.5, 90, 3.5), {"rest": [(11, 0.0)], "rail": "both", "rail_type": "glass"}, {"square_landing": [11], "regular": True}),
        ("square landing corner fit cement", corner(3.5, 90, 3.5), {"rest": [(11, 0.0)], "mode": "fit", "rail": "both", "rail_type": "cement"}, {"square_landing": [11], "regular": True}),
        ("square landings off", corner(3.5, 90, 3.5), {"rest": [(11, 0.0)], "square": False}, {"fan_landing": [11]}),
        ("rest step past the corner", corner(3.5, 90, 3.5), {"rest": [(13, 0.0)]}, {"warn": ["starts past the corner"], "fan_landing": [13]}),
        ("square landing zigzag fit", polyline((0, 0), (0, 3), (2.4, 0.4), (2.4, 6), (6, 6)), {"rest": [(9, 0.0), (15, 0.0)], "mode": "fit", "height": 7.0, "structure": "plank"}, {"square_landing": [9]}),
        ("autoconfigure zigzag without square landings", polyline((0, 0), (0, 3), (2.4, 0.4), (2.4, 6), (6, 6)), {"auto": True, "height": 7.0, "square": False}, {"regular": True}),
    ]
    # sharp corners: every role, both turn directions, 60/90/135 degrees, rotating rail types
    for i, (role, deg) in enumerate([(r, d) for r in ("mid", "inner", "outer") for d in (90, -90, 60, -60, 135, -135)]):
        rail_type = ("glass", "metal", "cement")[i % 3]
        exp = {"regular": True}
        if role == "outer" and abs(deg) == 135:
            exp.update(warn=[OVERFLOW], steps=14)  # the corner zones eat most of the 3.5 m legs
        scenarios.append(("corner %+d %s %s both" % (deg, role, rail_type), corner(3.5, deg, 3.5),
                          {"role": role, "rail": "both", "rail_type": rail_type}, exp))
    return scenarios


# --- harness ----------------------------------------------------------------------------

lines = []
def log(s): lines.append(str(s))

def planar_outline(brep, tol):
    """Outline of a plank's top face, projected to z = 0."""
    bb = brep.GetBoundingBox(True)
    for f in brep.Faces:
        fb = f.GetBoundingBox(True)
        if abs(fb.Min.Z - bb.Max.Z) < tol and abs(fb.Max.Z - bb.Max.Z) < tol:
            c = f.OuterLoop.To3dCurve()
            c.Transform(Transform.PlanarProjection(Plane.WorldXY))
            return bb.Max.Z, c
    return bb.Max.Z, None

def area(c):
    ap = AreaMassProperties.Compute(c)
    return ap.Area if ap else 0.0

config = {}
if os.path.exists(CONFIG):
    try: config = json.load(open(CONFIG))
    except Exception: config = {}
dll = config.get("dll") or ""
only = config.get("only") or []
passed = failed = 0
log("RUN %s dll=%s" % (time.strftime("%Y-%m-%d %H:%M:%S"), dll))
with open(OUT, "w") as f:  # lets the runner see the script started
    f.write(lines[0])
try:
    if not os.path.isfile(dll):
        raise Exception("No DLL to test (%r); run this through tests/run_tests.sh" % dll)
    doc = RhinoDoc.ActiveDoc or RhinoDoc.CreateHeadless(None)  # Rhino may have no document open
    asm = System.Reflection.Assembly.Load(System.IO.File.ReadAllBytes(dll))
    T = lambda n: asm.GetType("Stairs.Core." + n)
    build = T("StairGeometry").GetMethod("Build", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
    layout_of = T("StairLayout").GetMethod("Compute")
    autoconfigure = T("StairLayout").GetMethod("AutoConfigure")
    def enum(type_name, name): return System.Enum.Parse(T(type_name), name, True)

    def make_options(curve, k, tol, opt, structure=None):
        o = System.Activator.CreateInstance(T("StairOptions"))
        c = curve.DuplicateCurve()
        c.Transform(Transform.Scale(Point3d.Origin, k))
        o.Curve = c
        o.TotalHeight = opt.get("height", 3.0) * k
        o.DesiredStepHeight = STEP_H * k
        o.Width = opt.get("width", WIDTH) * k
        if "pyramid" in opt:
            o.Pyramid = True
            o.PyramidStep = opt["pyramid"][0] * k
            o.PyramidAlign = enum("PyramidAlign", opt["pyramid"][1])
            o.MinStepWidth = MIN_STEP_WIDTH * k
        o.ComfortSum = COMFORT * k
        if hasattr(o, "MinAverageDepth"):
            o.MinAverageDepth = MIN_AVG * k
            o.MinRiserHeight = MIN_RISER * k
            o.MaxRiserHeight = MAX_RISER * k
        mode = opt.get("mode", "custom" if "depth" in opt else "comfort")
        o.DepthMode = enum("TreadDepthMode", {"comfort": "Comfort", "fit": "FitToCurve", "custom": "Custom"}[mode])
        o.DepthOverride = opt.get("depth", 0.28) * k
        if "steps" in opt:
            o.DesiredStepHeight = o.TotalHeight / opt["steps"]
        o.AbsoluteTolerance = tol
        o.SlabThickness = SLAB_T * k
        o.PlankThickness = PLANK_T * k
        o.RailInset = 0.04 * k
        o.GlassThickness = 0.012 * k
        o.MetalTubeDiameter = 0.05 * k
        o.MetalBarDiameter = 0.02 * k
        o.PostDiameter = 0.04 * k
        o.PostSpacing = 1.10 * k
        o.RailCementThickness = 0.10 * k
        o.RailWallGap = GAP * k
        o.BalusterDiameter = BALUSTER * k
        o.BalusterSpacing = BALUSTER_SPACING * k
        o.RailHeight = 0.9 * k
        o.Role = enum("CurveRole", opt.get("role", "mid"))
        o.Structure = enum("StairStructure", structure or opt.get("structure", "slab"))
        o.Railing = enum("RailSide", opt.get("rail", "none"))
        o.RailType = enum("RailType", opt.get("rail_type", "glass"))
        o.Flip = opt.get("flip", False)
        o.SquareLandings = opt.get("square", True)
        o.ShowNumbers = True
        o.NumberStyle = doc.DimStyles.Current
        for number, depth in opt.get("rest", []):
            rs = System.Activator.CreateInstance(T("RestStep"))
            rs.StepNumber = number
            rs.Depth = depth * k
            o.RestSteps.Add(rs)
        if opt.get("auto"):
            autoconfigure.Invoke(None, [o])
        return o

    def check(name, curve, opt, exp, k, tol):
        errors = []
        o = make_options(curve, k, tol, opt)
        res = build.Invoke(None, [o])
        height = opt.get("height", 3.0)
        n_total = int(max(1, round(o.TotalHeight / o.DesiredStepHeight)))
        riser = height / n_total
        n_steps = exp.get("steps", n_total)

        # warnings: every expected one present, nothing else
        warns = list(res.Warnings)
        for w in exp.get("warn", []):
            if not any(w in x for x in warns): errors.append("missing warning '%s'" % w)
        for x in warns:
            if not any(w in x for w in exp.get("warn", [])): errors.append("unexpected warning '%s'" % x)

        # steps
        steps = list(res.Steps)
        if opt.get("structure", "slab") == "slab":
            if len(steps) != 1: errors.append("slab is %d breps, expected one solid" % len(steps))
        elif len(steps) != n_steps:
            errors.append("%d planks, expected %d" % (len(steps), n_steps))
        bad = [i for i, b in enumerate(steps) if not (b.IsValid and b.IsSolid)]
        if bad: errors.append("step breps not valid closed solids: %s" % bad[:6])
        if res.Numbers.Count != n_steps: errors.append("%d numbers, expected %d" % (res.Numbers.Count, n_steps))

        # railings
        side = opt.get("rail", "none")
        for label, pieces, wanted in (("left", list(res.RailingLeft), side in ("left", "both")),
                                      ("right", list(res.RailingRight), side in ("right", "both"))):
            if wanted and not pieces: errors.append("no %s railing" % label)
            if not wanted and pieces: errors.append("unrequested %s railing" % label)
            invalid = sum(1 for b in pieces if not b.IsValid)
            if invalid: errors.append("%d invalid %s railing breps" % (invalid, label))

        # tread tiling, on the plank build's tread tops
        plank_options = make_options(curve, k, tol, dict(opt, rail="none"), "plank")
        planks = build.Invoke(None, [plank_options])
        lay = layout_of.Invoke(None, [plank_options])
        lay_steps = list(lay.Steps)
        if exp.get("analytic_volume") and len(steps) == 1:
            depth = (COMFORT - riser) * k
            want = sum((st.B - st.A) for st in lay_steps) * depth * (riser + 2 * SLAB_T) * k / 2
            got = steps[0].GetVolume(tol * 10, tol)
            if abs(got - want) > want * 1e-6: errors.append("slab volume %.6g, expected %.6g" % (got, want))
        if exp.get("posts_on_edge"):
            # straight run along +X: the left-of-travel offset is +Y. A post is a thin vertical
            # brep standing on a tread; it must stand the inset inside that tread's edge.
            inset = 0.04 * k
            posts = 0
            for side_name, pieces in (("left", list(res.RailingLeft)), ("right", list(res.RailingRight))):
                for b in pieces:
                    bb = b.GetBoundingBox(True)
                    if bb.Max.X - bb.Min.X > 0.06 * k or bb.Max.Y - bb.Min.Y > 0.06 * k or bb.Max.Z - bb.Min.Z < 0.3 * k:
                        continue
                    posts += 1
                    y = (bb.Min.Y + bb.Max.Y) / 2
                    x = (bb.Min.X + bb.Max.X) / 2
                    st = min(lay_steps, key=lambda q: abs(x - (q.S0 + q.S1) / 2) if q.S0 <= x <= q.S1 else 1e18)
                    # the railing runs from this tread's edge at its nosing to the next tread's edge
                    nxt = next((q for q in lay_steps if q.Number == st.Number + 1), st)
                    e0, e1 = (st.B, nxt.B) if side_name == "left" else (st.A, nxt.A)
                    edge = e0 + (e1 - e0) * (x - st.S0) / st.Depth
                    want = edge - inset if side_name == "left" else edge + inset
                    if abs(y - want) > 0.002 * k:
                        errors.append("%s post on step %d is %.4g from the stair edge, expected the inset %.4g"
                                      % (side_name, st.Number, abs(y - edge), inset)); break
            if posts == 0: errors.append("no railing posts found")
        if opt.get("rail_type") == "cement":
            # balusters: plumb bars from one radius inside the wall's top up to the handrail's
            # centre, never more than the spacing apart along the rail
            tall = (GAP + BALUSTER / 2 + 0.05 / 2) * k
            for side_name, pieces in (("left", list(res.RailingLeft)), ("right", list(res.RailingRight))):
                if not pieces: continue
                bars = []
                for b in pieces:
                    bb = b.GetBoundingBox(True)
                    if bb.Max.X - bb.Min.X < BALUSTER * k * 1.01 and bb.Max.Y - bb.Min.Y < BALUSTER * k * 1.01:
                        bars.append(bb)
                if len(bars) < 2:
                    errors.append("%d balusters on the %s railing" % (len(bars), side_name)); continue
                wrong = [bb for bb in bars if abs((bb.Max.Z - bb.Min.Z) - tall) > 1e-6 * k]
                if wrong:
                    errors.append("%s baluster %.4g tall, expected %.4g" % (side_name, wrong[0].Max.Z - wrong[0].Min.Z, tall))
                centres = [((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2) for bb in bars]
                gap = max(min(math.hypot(p[0] - q[0], p[1] - q[1]) for q in centres if q is not p) for p in centres)
                if gap > BALUSTER_SPACING * k * (1 + 1e-6):
                    errors.append("%s balusters %.4g apart, more than %.4g" % (side_name, gap, BALUSTER_SPACING * k))
        if "pyramid" in opt:
            step_w, side = opt["pyramid"]
            base = opt.get("width", WIDTH) * k
            for st in lay_steps:
                want_w = max(base - step_w * k * (st.Number - 1), MIN_STEP_WIDTH * k)
                if abs((st.B - st.A) - want_w) > 1e-9 * k:
                    errors.append("step %d width %.6g, expected %.6g" % (st.Number, st.B - st.A, want_w)); break
                straight = {"center": abs((st.A + st.B) - (lay.A + lay.B)) < 1e-9 * k,
                            "left": abs(st.B - lay.B) < 1e-9 * k, "right": abs(st.A - lay.A) < 1e-9 * k}[side]
                if not straight:
                    errors.append("step %d is not snapped to the %s" % (st.Number, side)); break
        outlines = sorted((planar_outline(b, tol) for b in planks.Steps), key=lambda x: x[0])
        polys = [c for _, c in outlines]
        if any(c is None for c in polys):
            errors.append("plank without a flat top face")
            polys = [c for c in polys if c is not None]
        if len(polys) != n_steps: errors.append("%d tread outlines, expected %d" % (len(polys), n_steps))
        areas = [area(c) for c in polys]
        area_eps = tol * WIDTH * k
        if exp.get("tiling", True) and len(polys) > 1:
            boxes = [c.GetBoundingBox(True) for c in polys]
            overlap = 0.0
            for i in range(len(polys)):
                for j in range(i + 1, len(polys)):
                    if BoundingBox.Intersection(boxes[i], boxes[j]).IsValid:
                        for x in Curve.CreateBooleanIntersection(polys[i], polys[j], tol) or []:
                            overlap += area(x)
            if overlap > area_eps: errors.append("treads overlap by %.6g" % overlap)
            union = Curve.CreateBooleanUnion(polys, tol) or []
            if len(union) != 1:
                errors.append("tread union is %d curves (gap or hole)" % len(union))
            elif abs(area(union[0]) - sum(areas)) > area_eps:
                errors.append("tread union area %.6g != sum %.6g" % (area(union[0]), sum(areas)))
        # depth rule
        mode = opt.get("mode", "custom" if "depth" in opt else "comfort")
        if opt.get("auto"): mode = "fit"
        regular = lay.TreadDepth
        if mode == "comfort" and abs(regular - (COMFORT - riser) * k) > 1e-9 * k:
            errors.append("tread depth %.6g does not follow the comfort rule" % regular)
        if mode == "custom" and abs(regular - opt["depth"] * k) > 1e-9 * k:
            errors.append("tread depth %.6g is not the custom depth" % regular)
        if mode == "fit" and len(lay_steps) == n_total:
            end = lay.StartStation + lay.AvailableLength
            if abs(lay_steps[-1].S1 - end) > tol:
                errors.append("last step ends %.6g before the curve end" % (end - lay_steps[-1].S1))
        # square corner landings: a rest step that fills a corner, from its start to its end
        square = opt.get("square", True)
        corners = list(lay.Corners) if square else []
        def fills(st, z):
            return st.IsRest and st.S0 <= z.T0 + tol and st.S1 >= z.T1 - tol
        def fills_corner(st):
            return any(fills(st, z) for z in corners)
        flights = list(lay.FlightDepths) or [regular]
        if opt.get("auto"):
            if not (MIN_RISER * k - 1e-9 <= riser * k <= MAX_RISER * k + 1e-9):
                errors.append("autoconfigured riser %.6g is outside the riser range" % (riser * k))
            worst = max(flights, key=lambda d: abs(riser * k + d - COMFORT * k))
            if exp.get("comfort", True) and abs(riser * k + worst - COMFORT * k) > 0.03 * k:
                errors.append("autoconfigured riser + tread %.6g is far from the comfort sum" % (riser * k + worst))
            got_rests = [st.Number for st in lay_steps if st.IsRest]
            if not corners:
                want_rests = list(range(REST_EVERY, n_total, REST_EVERY))
                if got_rests != want_rests:
                    errors.append("rest steps %s, expected %s" % (got_rests, want_rests))
            else:
                # a landing in every corner, starting where the corner starts
                for z in corners:
                    if not any(fills(st, z) and abs(st.S0 - z.T0) <= tol for st in lay_steps):
                        errors.append("no square landing in the corner %.4g..%.4g (rests %s)" % (z.T0, z.T1, got_rests)); break
                run = longest = 0
                for st in lay_steps:
                    run = 0 if st.IsRest else run + 1
                    longest = max(longest, run)
                if longest > REST_EVERY:
                    errors.append("%d treads in a row without a rest" % longest)
                if any(a.IsRest and b.IsRest for a, b in zip(lay_steps, lay_steps[1:])):
                    errors.append("two landings joined by a single riser (rests %s)" % got_rests)
        for number in exp.get("square_landing", []):
            st = next((x for x in lay_steps if x.Number == number), None)
            if st is None or not st.IsRest or not fills_corner(st):
                errors.append("rest step %d does not fill a corner square" % number)
            elif mode == "fit" and not any(fills(st, z) and abs(st.S0 - z.T0) <= tol for z in corners):
                errors.append("rest step %d does not start where the corner starts" % number)
        for number in exp.get("fan_landing", []):
            st = next((x for x in lay_steps if x.Number == number), None)
            if st is None or not st.IsRest or any(st.S0 <= z.T0 + tol and st.S1 >= z.T1 - tol for z in lay.Corners):
                errors.append("rest step %d should follow the winders, not fill the corner" % number)

        rest = dict(opt.get("rest", []))
        if opt.get("auto"):
            rest = dict((st.Number, 0.0) for st in lay_steps if st.IsRest)
        # the regular depth of each tread is its flight's: flights end at corner landings
        flight_of, fi, had = {}, 0, False
        for st in lay_steps:
            if st.IsRest and fills_corner(st):
                if had: fi, had = fi + 1, False
            elif not st.IsRest:
                had = True
            flight_of[st.Number] = flights[min(fi, len(flights) - 1)]
        for i, st in enumerate(lay_steps[:len(areas)]):
            nominal = ((rest[i + 1] or WIDTH) * k if (i + 1) in rest else max(flight_of[st.Number], MIN_AVG * k))
            if st.Depth < nominal * (1 - 1e-6):
                errors.append("tread %d depth %.6g is below the regular %.6g" % (i + 1, st.Depth, nominal))
                break
            if exp.get("regular"):
                want = (st.B - st.A) * st.Depth
                if abs(areas[i] - want) > want * 1e-3:
                    errors.append("tread %d area %.6g, expected width x depth %.6g" % (i + 1, areas[i], want))
                    break
            if hasattr(lay, "SidePoint"):
                cut = lambda t: lay.SidePoint(t, st.A, 0).DistanceTo(lay.SidePoint(t, st.B, 0))
                nosing = (cut(st.S0) + cut(st.S1)) / 2
                if areas[i] / nosing < MIN_AVG * k * (1 - 1e-3):
                    errors.append("tread %d average depth %.6g is below %.6g" % (i + 1, areas[i] / nosing, MIN_AVG * k))
                    break
        if "outside_of" in exp:
            (cx, cy), r = exp["outside_of"]
            for i, c in enumerate(polys):
                p = AreaMassProperties.Compute(c).Centroid
                if math.hypot(p.X - cx * k, p.Y - cy * k) < r * k:
                    errors.append("tread %d lies inside the curve's arc" % (i + 1))
                    break
        return errors

    for name, curve, opt, exp in build_scenarios():
        if only and not any(f in name for f in only): continue
        for pname, k, tol in PROFILES:
            t0 = time.time()
            try:
                errors = check(name, curve, opt, exp, k, tol)
            except Exception as ex:
                errors = ["exception: %s" % ex]
            dt = time.time() - t0
            if errors:
                failed += 1
                log("FAIL %-36s [%s] %5.2fs  %s" % (name, pname, dt, "; ".join(errors)))
            else:
                passed += 1
                log("PASS %-36s [%s] %5.2fs" % (name, pname, dt))
except Exception:
    failed += 1
    log("ERROR\n" + traceback.format_exc())

log("SUMMARY passed=%d failed=%d" % (passed, failed))
log("DONE")
with open(OUT, "w") as f:
    f.write("\n".join(lines))
