using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace Stairs.Core
{
    public class StepInfo
    {
        public int Number;      // 1-based tread index
        public bool IsRest;     // true if this tread is a landing (rest step)
        public double S0;       // walk station at the front (walking) edge
        public double S1;       // walk station at the back edge
        public double TopZ;     // absolute world z of the tread top
        public double Depth;    // tread depth along the walking line
        public double A, B;     // side offsets of this tread (inside the band for pyramid stairs)
        public List<double> Samples; // walk stations used to facet this tread (S0..S1)
    }

    /// <summary>
    /// A sharp corner (tangent kink) of the walking curve, or a smooth bend tighter than
    /// the band reaches to its inside. Outside these zones the tread cut lines are square
    /// to the curve; inside a zone they fan out from the pivot, the inner corner of the
    /// stair band. The zone runs between the two square cut lines through the pivot and is
    /// walked along the band's center line, so winder treads keep the regular depth there
    /// for every curve role.
    /// </summary>
    public class CornerZone
    {
        public double SA, SC, SB;          // curve arc lengths: zone start, kink, zone end
        public double T0, TM, T1;          // walk stations: zone start, bisector, zone end
        public double Turn;                // signed turn angle in radians (+ = left)
        public double InnerOff, OuterOff;  // band offsets of the inner and outer side
        public Point3d Pivot;              // inner corner of the band (z = 0)
        public Vector3d UIn, UOut;         // unit vectors from the inner toward the outer side, before / after
        internal double DepthSign;         // band offset -> depth from the inner side
        internal double OffsetAfter;       // walk station minus curve arc length past this zone
        internal int Subdivisions;         // fan facets per half of the zone

        // Tight bend of a smooth curve: square cut lines would cross inside the band, so they
        // fan from the pivot (where the inner side line crosses itself) to the outer side line,
        // which keeps following the curve.
        internal bool Smooth;
        internal double MidOff;                 // band offset of the center line
        internal Func<double, Point3d> OuterAt; // outer side point (z = 0) at curve arc length s
        internal double[] WalkTable, ArcTable;  // walk stations and curve arc lengths through the zone
        internal double WalkLength, HalfLength; // walk length through the zone, and up to TM

        // A corner's fan runs from the pivot to the outer side, which is the path OuterPath: normally
        // a square cut line, the miter point and a square cut line again. Corners close together
        // change its ends and bends (see StairLayout.BuildCornerZones), and rounded corners
        // (StairOptions.RoundSharpCorners) have a polygon round the pivot as their bends.
        internal Point3d[] OuterPath; // the outer side through the zone, first and last on the zone's end cut lines
        internal Point3d[] PathPoint; // the center path: OuterPath scaled toward the pivot
        internal double[] PathT;      // walk stations of the path points
        internal double[] BendT;      // walk stations of the outer side's bends

        /// <summary>The piece of the paths at walk station t, and how far along it t is.</summary>
        void PathPiece(double t, out int i, out double f)
        {
            i = 0;
            while (i < PathT.Length - 2 && t > PathT[i + 1]) i++;
            f = Math.Min(1, Math.Max(0, (t - PathT[i]) / Math.Max(PathT[i + 1] - PathT[i], 1e-15)));
        }

        /// <summary>Curve arc length of the cut line at walk station t (tight bends).</summary>
        internal double ArcAt(double t) => Interpolate(WalkTable, ArcTable, t);

        /// <summary>Walk station of the cut line through curve arc length s (tight bends).</summary>
        internal double WalkAt(double s) => Interpolate(ArcTable, WalkTable, s);

        static double Interpolate(double[] xs, double[] ys, double x)
        {
            if (x <= xs[0]) return ys[0];
            int n = xs.Length;
            if (x >= xs[n - 1]) return ys[n - 1];
            int i = Array.BinarySearch(xs, x);
            if (i >= 0) return ys[i];
            i = ~i; // xs[i - 1] < x < xs[i]
            double f = (x - xs[i - 1]) / Math.Max(xs[i] - xs[i - 1], 1e-15);
            return ys[i - 1] + (ys[i] - ys[i - 1]) * f;
        }

        /// <summary>Point on the band center line at walk station t (T0 &lt;= t &lt;= T1).</summary>
        internal Point3d MidPoint(double t)
        {
            if (Smooth) return SidePoint(t, MidOff);
            PathPiece(t, out int i, out double f);
            return PathPoint[i] + (PathPoint[i + 1] - PathPoint[i]) * f;
        }

        /// <summary>Unit direction of the cut line at walk station t, from the pivot outward.</summary>
        internal Vector3d FanDir(double t)
        {
            var e = (Smooth ? OuterAt(ArcAt(t)) : MidPoint(t)) - Pivot;
            e.Z = 0;
            if (!e.Unitize()) e = t <= TM ? UIn : UOut;
            return e;
        }

        /// <summary>
        /// Point of the cut line at walk station t on the band side line at the given offset:
        /// straight from the pivot (inner side) to the outer side line.
        /// </summary>
        internal Point3d SidePoint(double t, double offset)
        {
            Point3d outer;
            if (Smooth) outer = OuterAt(ArcAt(t));
            else
            {
                PathPiece(t, out int i, out double u);
                outer = OuterPath[i] + (OuterPath[i + 1] - OuterPath[i]) * u;
            }
            return Pivot + (outer - Pivot) * ((offset - InnerOff) / (OuterOff - InnerOff));
        }
    }

    /// <summary>Resolves curve direction, step count, riser height, tread depths and corner zones.</summary>
    public class StairLayout
    {
        public Curve WalkCurve;          // working copy of the curve, reversed when flipped
        public double BaseZ;             // world z of the input curve (floor level)
        public Vector3d InnerDir;        // unit vector pointing toward the inner (concave) side, horizontal
        public bool InnerDirIsLeft;      // true when the inner side is on the left of the walking direction
        public double A, B;              // band side offsets along the local left-of-travel vector (A < B)
        public List<CornerZone> Corners = new List<CornerZone>();
        public List<StepInfo> Steps = new List<StepInfo>();
        public double RiserHeight;       // actual riser height
        public double TreadDepth;        // actual regular tread depth
        public double StartStation;      // walk station of the curve start
        public double UsedLength;        // total run consumed on the walking line
        public double AvailableLength;   // walking-line length of the input curve
        public List<double> FlightDepths = new List<double>(); // fitted tread depth per flight, when square corner landings split the stair
        public List<string> Warnings = new List<string>();

        /// <summary>Facets of curved treads and of corner fans turn at most this much.</summary>
        internal const double FacetTurn = Math.PI / 36.0;

        /// <summary>With RoundSharpCorners, corners turning more than this (legs under 60° apart) get a round outer side instead of a single miter,</summary>
        internal const double RoundAbove = 2 * Math.PI / 3;
        /// <summary>made of miters turning at most this much each.</summary>
        internal const double RoundSegmentTurn = Math.PI / 6;

        /// <summary>Legs turning more than this from each other do not meet: their side lines hardly cross.</summary>
        const double MaxMergedTurn = 175 * Math.PI / 180;

        double _curveLength;
        double _narrowing;          // pyramid: how much narrower each step is (0: constant width)
        PyramidAlign _align;
        double _minWidth;
        double _minGap; // shortest facet the subdivision may create
        bool _roundSharp; // corners over RoundAbove go round the pivot
        readonly List<(double Length, double Param)> _kinks = new List<(double, double)>(); // curve kinks: arc length and parameter
        readonly Dictionary<int, CornerZone> _cornerLandings = new Dictionary<int, CornerZone>(); // rest step number -> corner it fills

        public static StairLayout Compute(StairOptions o)
        {
            var layout = new StairLayout();
            var curve = o.Curve.DuplicateCurve();
            if (o.Flip) curve.Reverse();
            layout.WalkCurve = curve;
            layout._curveLength = curve.GetLength();

            var start = curve.PointAtStart;
            layout.BaseZ = start.Z;

            // inner side: toward the center of curvature; for straight curves default to the right of travel
            double turn = SignedTurn(curve, layout._curveLength);
            Vector3d t0 = curve.TangentAtStart; t0.Z = 0; t0.Unitize();
            Vector3d left = Vector3d.CrossProduct(Vector3d.ZAxis, t0);
            layout.InnerDirIsLeft = turn > Math.PI / 90;
            layout.InnerDir = layout.InnerDirIsLeft ? left : -left;

            double tol = o.AbsoluteTolerance;
            layout._minGap = Math.Max(tol * 10, 1e-9);
            layout._roundSharp = o.RoundSharpCorners;
            if (o.Pyramid && o.PyramidStep > 0)
            {
                layout._narrowing = o.PyramidStep;
                layout._align = o.PyramidAlign;
                layout._minWidth = Math.Min(Math.Max(o.MinStepWidth, tol), Math.Max(o.Width, tol));
            }
            StairGeometry.GetBandOffsets(o, layout, Math.Max(o.Width, tol), out layout.A, out layout.B);
            layout.FindCorners(tol);
            layout.StartStation = layout.WalkStation(0);
            double endStation = layout.WalkStation(layout._curveLength);
            layout.AvailableLength = endStation - layout.StartStation;

            // riser count from desired step height
            double h = Math.Max(o.DesiredStepHeight, 1e-6);
            int n = (int)Math.Max(1, Math.Round(o.TotalHeight / h));
            layout.RiserHeight = o.TotalHeight / n;
            if (layout._narrowing > 0 && layout._narrowing * (n - 1) > (layout.B - layout.A) - layout._minWidth + 1e-9)
            {
                int first = 2 + (int)Math.Floor(((layout.B - layout.A) - layout._minWidth) / layout._narrowing + 1e-9);
                layout.Warnings.Add(string.Format(
                    "Pyramid width: steps {0}+ are kept at the minimum step width {1:0.###}.", first, layout._minWidth));
            }

            // tread depth
            switch (o.DepthMode)
            {
                case TreadDepthMode.Custom:
                    layout.TreadDepth = o.DepthOverride;
                    break;
                case TreadDepthMode.FitToCurve:
                    layout.TreadDepth = layout.FitTreadDepth(o, 1, n, layout.StartStation, endStation, endStation);
                    break;
                default:
                    layout.TreadDepth = ComfortDepth(o, layout.RiserHeight);
                    if (layout.TreadDepth > o.ComfortSum - layout.RiserHeight)
                        layout.Warnings.Add("Step height is too large for the comfort formula; tread depth was clamped.");
                    break;
            }
            if (layout.TreadDepth <= 0)
            {
                layout.TreadDepth = o.ComfortSum * 0.5;
                layout.Warnings.Add("Tread depth must be positive; a default value was used.");
            }

            var regular = new double[n + 1];
            for (int i = 1; i <= n; i++) regular[i] = layout.TreadDepth;
            if (o.SquareLandings) layout.SquareCornerLandings(o, n, endStation, regular);

            // treads along the walking line
            double s = layout.StartStation;
            bool warnedMinimum = false, warnedRest = false;
            for (int i = 1; i <= n; i++)
            {
                bool isRest = o.RestStepAt(i, out RestStep rs);
                if (isRest && rs.Depth < 0 && !warnedRest)
                {
                    layout.Warnings.Add("Rest step depth must be positive; stair width used.");
                    warnedRest = true;
                }
                if (!isRest && !warnedMinimum && layout.TreadDepth < o.MinAverageDepth)
                {
                    layout.Warnings.Add(string.Format(
                        "Tread depth {0:0.###} is below the minimum average tread depth {1:0.###}; treads were widened.",
                        layout.TreadDepth, o.MinAverageDepth));
                    warnedMinimum = true;
                }
                double depth = layout.TreadDepthAt(o, i, s, regular[i], endStation);
                if (layout._cornerLandings.TryGetValue(i, out var corner) && s > corner.T0 + tol)
                {
                    // the step that starts at or before the corner would fill it square
                    var before = layout.Steps.FindLast(x => x.S0 <= corner.T0 + tol);
                    layout.Warnings.Add(string.Format(
                        "Rest step {0} starts past the corner, so its front edge follows the winders.{1}",
                        i, before == null ? "" : string.Format(" A rest step at {0} would fill the corner square.", before.Number)));
                }

                var step = new StepInfo
                {
                    Number = i,
                    IsRest = isRest,
                    S0 = s,
                    S1 = s + depth,
                    TopZ = layout.BaseZ + i * layout.RiserHeight,
                    Depth = depth,
                    Samples = new List<double>()
                };
                layout.StepOffsets(i, out step.A, out step.B);
                if (step.S1 > endStation + 1e-9)
                {
                    if (step.S0 < endStation)
                        layout.Warnings.Add(string.Format(
                            "The staircase needs more run than the curve provides. Steps {0}+ were skipped.",
                            i));
                    break;
                }
                layout.CollectSamples(step);
                layout.Steps.Add(step);
                s += depth;
            }
            if (layout.Steps.Count == 0)
                layout.Warnings.Add("No steps fit on the curve.");

            layout.UsedLength = s - layout.StartStation;
            layout.WarnCrossingCutLines(s);
            return layout;
        }

        /// <summary>
        /// The safety net behind the corner zones: neighbouring cut lines must not cross. Where they
        /// do, whatever the cause, the treads overlap, and nothing else would say so.
        /// </summary>
        void WarnCrossingCutLines(double end)
        {
            const int max = 400;
            double length = end - StartStation;
            if (length <= _minGap || B - A <= 0 || Warnings.Exists(w => w.Contains("overlap"))) return; // already said
            int n = Math.Min(max, Math.Max(16, (int)Math.Ceiling(length / ((B - A) / 12))));
            Point3d a0 = SidePoint(StartStation, A, 0), b0 = SidePoint(StartStation, B, 0);
            for (int i = 1; i <= n; i++)
            {
                double t = StartStation + length * i / n;
                Point3d a1 = SidePoint(t, A, 0), b1 = SidePoint(t, B, 0);
                if (SegmentsCross(a0, b0, a1, b1, out double u, out double v) && u > 0.01 && u < 0.99 && v > 0.01 && v < 0.99)
                {
                    Warnings.Add("The curve turns too much too quickly for a stair this wide: some treads overlap. Use a narrower stair or a smoother curve, or put the curve on the stair's other edge.");
                    return;
                }
                a0 = a1; b0 = b1;
            }
        }

        /// <summary>Tread depth from the comfort rule (riser + tread = ComfortSum), clamped for very high risers.</summary>
        public static double ComfortDepth(StairOptions o, double riser)
        {
            double depth = o.ComfortSum - riser;
            if (depth < o.AbsoluteTolerance * 10) depth = Math.Max(o.ComfortSum * 0.4, o.AbsoluteTolerance * 10);
            return depth;
        }

        /// <summary>
        /// Walk depth of tread i starting at walk station s, given the regular depth: rest steps
        /// use their own depth (stair width by default), and every tread is widened as needed
        /// for the minimum average depth.
        /// </summary>
        double TreadDepthAt(StairOptions o, int i, double s, double regular, double endStation)
        {
            double depth = regular;
            if (o.RestStepAt(i, out RestStep rs))
            {
                depth = rs.Depth > 0 ? rs.Depth : o.Width;
                // a corner landing runs at least to the square cut line after the corner
                if (_cornerLandings.TryGetValue(i, out var z)) return Math.Max(s + depth, z.T1) - s;
            }
            if (depth <= 0) depth = o.Width;
            StepOffsets(i, out double a, out double b);
            return WidenToMinimum(s, depth, o.MinAverageDepth, endStation, a, b);
        }

        /// <summary>
        /// Side offsets of tread 'number': the band, or for pyramid stairs the band narrowed
        /// by the pyramid step per tread above the first, down to the minimum step width.
        /// </summary>
        public void StepOffsets(int number, out double a, out double b)
        {
            a = A; b = B;
            if (_narrowing <= 0) return;
            double cut = Math.Min(_narrowing * (number - 1), Math.Max(0, (B - A) - _minWidth));
            switch (_align)
            {
                case PyramidAlign.Left: a += cut; break;  // the left side (B) stays straight
                case PyramidAlign.Right: b -= cut; break;
                default: a += cut / 2; b -= cut / 2; break;
            }
        }

        /// <summary>Walk station where treads first..last end, starting at walk station start with the given regular depth.</summary>
        double RunEnd(StairOptions o, int first, int last, double start, double regular, double endStation)
        {
            double s = start;
            for (int i = first; i <= last; i++)
                s += TreadDepthAt(o, i, s, regular, endStation);
            return s;
        }

        /// <summary>
        /// Regular tread depth that makes treads first..last, starting at walk station start,
        /// end exactly at walk station target: the curve end, or a corner landing. The run
        /// grows monotonically with the depth (rest steps and widened treads included), so a
        /// bracketed regula falsi converges in a few layout passes. When even the shortest
        /// treads overrun the target, the minimum depth is returned and the layout reports
        /// the skipped steps.
        /// </summary>
        double FitTreadDepth(StairOptions o, int first, int last, double start, double target, double endStation)
        {
            double tol = Math.Max(o.AbsoluteTolerance, 1e-9);
            double lo = Math.Max(o.MinAverageDepth, tol * 10);
            double fLo = RunEnd(o, first, last, start, lo, endStation) - target;
            if (fLo >= 0) return lo;
            double hi = Math.Max(lo * 2, (target - start) / Math.Max(1, last - first + 1));
            double fHi = RunEnd(o, first, last, start, hi, endStation) - target;
            for (int k = 0; fHi < 0 && k < 60; k++)
            {
                lo = hi; fLo = fHi;
                hi *= 2;
                fHi = RunEnd(o, first, last, start, hi, endStation) - target;
            }
            if (fHi < 0) return hi; // only rest steps: the depth does not change the run
            int lastSide = 0;
            for (int k = 0; k < 60 && hi - lo > tol * 1e-6; k++)
            {
                double mid = (lo * fHi - hi * fLo) / (fHi - fLo);
                if (!(mid > lo && mid < hi)) mid = (lo + hi) / 2;
                double f = RunEnd(o, first, last, start, mid, endStation) - target;
                if (f <= 0 && f > -tol * 1e-3) return mid;
                if (f < 0)
                {
                    lo = mid; fLo = f;
                    if (lastSide < 0) fHi /= 2; // Illinois step: keep the far end moving
                    lastSide = -1;
                }
                else
                {
                    hi = mid; fHi = f;
                    if (lastSide > 0) fLo /= 2;
                    lastSide = 1;
                }
            }
            return lo; // never past the target
        }

        /// <summary>
        /// Square corner landings: a rest step that reaches into a corner zone covers the whole
        /// zone, so its edges are the square cut lines of the flights before and after the
        /// corner instead of fanned ones. With fitted treads every flight is fitted on its own,
        /// to end exactly where its landing starts; with a fixed tread depth the landing
        /// stretches back along the flight to where the treads before it end. Refitting moves
        /// the later steps, so a rest step can come to reach a corner it missed before: the
        /// search repeats until no new landing turns up.
        /// </summary>
        void SquareCornerLandings(StairOptions o, int n, double endStation, double[] regular)
        {
            for (int pass = 0; pass < 8; pass++)
            {
                // the landings that reach a corner, on the stair as laid out so far
                int found = _cornerLandings.Count;
                double s = StartStation;
                for (int i = 1; i <= n && s < endStation; i++)
                {
                    double d = TreadDepthAt(o, i, s, regular[i], endStation);
                    if (o.RestStepAt(i, out _) && !_cornerLandings.ContainsKey(i))
                    {
                        var zone = Corners.Find(z => s < z.T1 - _minGap && s + d > z.T0 + _minGap);
                        if (zone != null && !_cornerLandings.ContainsValue(zone))
                        {
                            _cornerLandings[i] = zone;
                            d = TreadDepthAt(o, i, s, regular[i], endStation);
                        }
                    }
                    s += d;
                }
                if (_cornerLandings.Count == found || o.DepthMode != TreadDepthMode.FitToCurve) return;

                // each flight, up to a corner landing or the curve end, gets its own fitted depth
                FlightDepths.Clear();
                var numbers = new List<int>(_cornerLandings.Keys);
                numbers.Sort();
                numbers.Add(n + 1); // the last flight ends at the curve end
                int first = 1;
                double start = StartStation;
                foreach (int i in numbers)
                {
                    int last = Math.Min(i - 1, n);
                    double target = i <= n ? _cornerLandings[i].T0 : endStation;
                    if (last >= first && target > start)
                    {
                        double d = FitTreadDepth(o, first, last, start, target, endStation);
                        for (int k = first; k <= last; k++) regular[k] = d;
                        FlightDepths.Add(d);
                    }
                    if (i > n) break;
                    double landing = RunEnd(o, first, last, start, regular[first], endStation);
                    start = landing + TreadDepthAt(o, i, landing, regular[i], endStation);
                    first = i + 1;
                }
                if (FlightDepths.Count > 0) TreadDepth = FlightDepths[0];
            }
        }

        /// <summary>
        /// Configures the stair from its total height and curve alone: the tread depth fits the
        /// curve exactly (the last step ends at the curve end), a rest step follows every
        /// RestInterval steps, and the step count is the one whose riser + tread comes closest
        /// to the comfort rule with risers between MinRiserHeight and MaxRiserHeight. With
        /// square landings, every sharp corner also gets a landing and each flight between
        /// landings is fitted on its own, so the count is the one whose worst flight comes
        /// closest to the comfort rule. Sets DepthMode, DesiredStepHeight and RestSteps;
        /// returns the step count.
        /// </summary>
        public static int AutoConfigure(StairOptions o)
        {
            double height = Math.Max(o.TotalHeight, 1e-9);
            int nMin = Math.Max(1, (int)Math.Ceiling(height / Math.Max(o.MaxRiserHeight, 1e-9) - 1e-9));
            int nMax = Math.Max(nMin, (int)Math.Floor(height / Math.Max(o.MinRiserHeight, 1e-9) + 1e-9));
            int interval = Math.Max(1, o.RestInterval);
            double width = Math.Max(o.Width, 1e-9);

            // walking length of the curve and its corners, in walk stations
            o.DepthMode = TreadDepthMode.Comfort;
            o.RestSteps = new List<RestStep>();
            var plain = Compute(o);
            double walk = plain.AvailableLength;
            double start = plain.StartStation, end = start + walk;
            var corners = o.SquareLandings ? new List<CornerZone>(plain.Corners) : new List<CornerZone>();
            corners.Sort((x, y) => x.T0.CompareTo(y.T0));

            // With square landings every corner gets a landing, which runs from the corner's
            // start to its end (at least a stair width), so the flights between landings have
            // fixed runs whatever the step count.
            var runs = new List<double>();
            {
                double from = start;
                foreach (var z in corners)
                {
                    if (z.T0 < from) { from = Math.Max(from, z.T1); continue; } // inside the previous landing
                    runs.Add(z.T0 - from);
                    from = Math.Max(z.T0 + width, z.T1);
                }
                runs.Add(Math.Max(0, end - from));
            }
            int perRest = Math.Max(1, interval - 1);        // treads between two rests
            int Plain(int m) => m <= 0 ? 0 : (m - 1) / perRest; // rests that break up a flight of m treads

            // how far a flight of m treads is from the comfort rule with risers r
            double FlightError(int j, int m, double r)
            {
                // no treads only where there is no room for one: a single riser between two
                // landings is easily missed underfoot
                if (m <= 0) return runs[j] < o.MinAverageDepth ? 0 : double.MaxValue;
                double depth = (runs[j] - Plain(m) * width) / m;
                if (depth < o.MinAverageDepth) return double.MaxValue;
                return Math.Abs(r + depth - o.ComfortSum);
            }

            // Tread counts per flight for n steps: of the splits that add up to n (treads, rests
            // in long flights and one landing per corner), the one whose worst flight comes
            // closest to the comfort rule. Null when no split adds up.
            int[] Split(int n, out double worst)
            {
                worst = double.MaxValue;
                double r = height / n, d = Math.Max(o.ComfortSum - r, o.MinAverageDepth);
                var best = new Dictionary<int, (double Worst, int[] M)> { [0] = (0, new int[0]) };
                for (int j = 0; j < runs.Count; j++)
                {
                    var next = new Dictionary<int, (double Worst, int[] M)>();
                    int ideal = (int)Math.Round(runs[j] / (d + width / perRest));
                    for (int m = Math.Max(0, ideal - 3); m <= ideal + 3; m++)
                    {
                        double e = FlightError(j, m, r);
                        if (e == double.MaxValue) continue;
                        int count = m + Plain(m) + (j < runs.Count - 1 ? 1 : 0); // and the landing after it
                        foreach (var kv in best)
                        {
                            int k = kv.Key + count;
                            if (k > n) continue;
                            double w = Math.Max(kv.Value.Worst, e);
                            if (next.TryGetValue(k, out var cur) && cur.Worst <= w) continue;
                            var split = new int[j + 1];
                            kv.Value.M.CopyTo(split, 0);
                            split[j] = m;
                            next[k] = (w, split);
                        }
                    }
                    best = next;
                }
                if (!best.TryGetValue(n, out var found)) return null;
                worst = found.Worst;
                return found.M;
            }

            // Rest steps for n steps: without corner landings a rest every interval steps; with
            // them, a landing after each flight of the best split, and a rest every interval
            // steps inside a long flight.
            List<RestStep> Rests(int n)
            {
                var list = new List<RestStep>();
                int[] split = corners.Count > 0 ? Split(n, out _) : null;
                if (split == null)
                {
                    for (int k = interval; k < n; k += interval) list.Add(new RestStep(k, 0));
                    return list;
                }
                int step = 0;
                for (int j = 0; j < split.Length; j++)
                {
                    for (int t = 1, run = 0; t <= split[j]; t++)
                    {
                        step++;
                        if (++run == perRest && t < split[j]) { list.Add(new RestStep(++step, 0)); run = 0; }
                    }
                    if (j < split.Length - 1) list.Add(new RestStep(++step, 0)); // the corner landing
                }
                return list;
            }

            // a quick estimate per step count that ignores corner widening; only the most
            // promising counts get a full layout
            var candidates = new List<int>();
            for (int n = nMin; n <= nMax; n++) candidates.Add(n);
            double Estimate(int n)
            {
                if (corners.Count > 0) { Split(n, out double worst); return worst; }
                int rests = Rests(n).Count;
                if (n - rests <= 0) return double.MaxValue;
                double depth = (walk - rests * o.Width) / (n - rests);
                return depth <= 0 ? double.MaxValue : Math.Abs(height / n + depth - o.ComfortSum);
            }
            var estimates = new Dictionary<int, double>();
            foreach (int n in candidates) estimates[n] = Estimate(n);
            candidates.Sort((x, y) => estimates[x].CompareTo(estimates[y]));

            o.DepthMode = TreadDepthMode.FitToCurve;
            int best = nMin;
            double bestError = double.MaxValue;
            bool bestFits = false;
            foreach (int n in candidates.GetRange(0, Math.Min(corners.Count > 0 ? 8 : 4, candidates.Count)))
            {
                o.RestSteps = Rests(n);
                o.DesiredStepHeight = height / n;
                var layout = Compute(o);
                double tol = Math.Max(o.AbsoluteTolerance, 1e-9);
                // with square landings every corner must be filled by a landing
                bool fits = layout.Steps.Count == n && corners.TrueForAll(z =>
                    layout.Steps.Exists(st => st.IsRest && st.S0 <= z.T0 + tol && st.S1 >= z.T1 - tol));
                double error = 0;
                foreach (double depth in layout.FlightDepths.Count > 0 ? layout.FlightDepths : new List<double> { layout.TreadDepth })
                    error = Math.Max(error, Math.Abs(layout.RiserHeight + depth - o.ComfortSum));
                if (fits && !bestFits || fits == bestFits && error < bestError)
                {
                    best = n; bestError = error; bestFits = fits;
                }
            }
            if (!bestFits) best = nMin; // nothing fits: the fewest steps need the least run
            o.RestSteps = Rests(best);
            o.DesiredStepHeight = height / best;
            return best;
        }

        /// <summary>
        /// Walk depth for a tread starting at walk station s: the given depth, widened when
        /// needed until the tread's average depth reaches minAverage. Without this, sharp
        /// corners fan out into long, thin winders whose walking-line depth looks regular.
        /// </summary>
        double WidenToMinimum(double s, double depth, double minAverage, double endStation, double a, double b)
        {
            if (minAverage <= 0 || AverageDepth(s, s + depth, a, b) >= minAverage) return depth;
            // past the curve end the tread is skipped anyway, so stop searching there
            double cap = Math.Max(depth, endStation - s) + depth;
            double lo = depth, hi = depth;
            do { lo = hi; hi = Math.Min(hi * 1.5, cap); }
            while (hi < cap && AverageDepth(s, s + hi, a, b) < minAverage);
            if (AverageDepth(s, s + hi, a, b) < minAverage) return hi;
            for (int k = 0; k < 30; k++)
            {
                double mid = (lo + hi) / 2;
                if (AverageDepth(s, s + mid, a, b) < minAverage) lo = mid; else hi = mid;
            }
            return hi;
        }

        /// <summary>
        /// Average depth of the tread between walk stations s0 and s1: its plan area over
        /// the mean length of its two cut lines (front and back nosing).
        /// </summary>
        double AverageDepth(double s0, double s1, double a, double b)
        {
            const int n = 6; // fine enough for the area of curved treads (corner fans are exact)
            var st = new List<double>();
            for (int k = 0; k <= n; k++) st.Add(s0 + (s1 - s0) * k / n);
            foreach (var z in Corners)
            {
                foreach (var key in new[] { z.T0, z.TM, z.T1 })
                    if (key > s0 && key < s1) st.Add(key);
                foreach (var key in z.BendT)
                    if (key > s0 && key < s1) st.Add(key);
            }
            st.Sort();

            var poly = new List<Point3d>();
            foreach (var t in st) poly.Add(SidePoint(t, a, 0));
            for (int k = st.Count - 1; k >= 0; k--) poly.Add(SidePoint(st[k], b, 0));
            double area = 0;
            for (int k = 0; k < poly.Count; k++)
            {
                var p = poly[k];
                var q = poly[(k + 1) % poly.Count];
                area += p.X * q.Y - q.X * p.Y;
            }
            area = Math.Abs(area) / 2;
            double front = SidePoint(s0, a, 0).DistanceTo(SidePoint(s0, b, 0));
            double back = SidePoint(s1, a, 0).DistanceTo(SidePoint(s1, b, 0));
            double nosing = (front + back) / 2;
            return nosing > 1e-12 ? area / nosing : 0;
        }

        /// <summary>
        /// Total signed turning (radians, + = left) along the curve, summed over short
        /// steps so arcs beyond 180 degrees and U-shaped polylines keep their sign.
        /// </summary>
        private static double SignedTurn(Curve curve, double length)
        {
            const int n = 256;
            double total = 0;
            var prev = Vector3d.Zero;
            for (int i = 0; i <= n; i++)
            {
                double s = length * (i == 0 ? 1e-6 : i == n ? 1 - 1e-6 : (double)i / n);
                var t = TangentAtLength(curve, s);
                t.Z = 0;
                if (!t.Unitize()) continue;
                if (!prev.IsZero)
                    total += Math.Atan2(prev.X * t.Y - prev.Y * t.X, prev.X * t.X + prev.Y * t.Y);
                prev = t;
            }
            return total;
        }

        /// <summary>Builds a corner zone at every tangent kink of the walking curve.</summary>
        void FindCorners(double tol)
        {
            var curve = WalkCurve;
            double midOff = (A + B) / 2;
            var spans = new List<double> { 0 }; // arc lengths where the smooth stretches of the curve end
            var kinks = new List<Kink>();

            double p = curve.Domain.Min;
            while (curve.GetNextDiscontinuity(Continuity.G1_continuous, p, curve.Domain.Max, out double pk))
            {
                if (pk <= p) break;
                p = pk;
                var tIn = Horizontal(curve.DerivativeAt(pk, 1, CurveEvaluationSide.Below));
                var tOut = Horizontal(curve.DerivativeAt(pk, 1, CurveEvaluationSide.Above));
                if (tIn.IsZero || tOut.IsZero) continue;
                double cross = tIn.X * tOut.Y - tIn.Y * tOut.X;
                double dot = tIn * tOut;
                double turn = Math.Atan2(cross, dot);
                if (Math.Abs(turn) < Math.PI / 360) continue; // below half a degree: not a corner
                if (1 + dot < 1e-3)
                {
                    Warnings.Add(string.Format(
                        "The curve turns back on itself ({0:F0}° between its legs); that corner cannot be built.",
                        LegsApart(turn)));
                    continue;
                }
                double sc = curve.GetLength(new Interval(curve.Domain.Min, pk));
                _kinks.Add((sc, pk));
                spans.Add(sc);
                var at = curve.PointAt(pk);
                kinks.Add(new Kink { S = sc, Turn = turn, Point = new Point3d(at.X, at.Y, 0), TIn = tIn, TOut = tOut });
            }
            var zones = BuildCornerZones(kinks, midOff, tol);
            spans.Add(_curveLength);
            for (int k = 0; k + 1 < spans.Count; k++)
                FindTightBends(spans[k], spans[k + 1], midOff, tol, zones);

            // walk stations, in order along the curve: a zone is walked along the band center
            zones.Sort((x, y) => x.SC.CompareTo(y.SC));
            double offset = 0;
            foreach (var z in zones)
            {
                z.T0 = z.SA + offset;
                z.TM = z.T0 + z.HalfLength;
                z.T1 = z.T0 + z.WalkLength;
                if (z.Smooth)
                {
                    for (int k = 0; k < z.WalkTable.Length; k++) z.WalkTable[k] += z.T0;
                    z.BendT = new[] { z.TM };
                }
                else
                {
                    for (int k = 0; k < z.BendT.Length; k++) z.BendT[k] += z.T0;
                    for (int k = 0; k < z.PathT.Length; k++) z.PathT[k] += z.T0;
                }
                offset += z.WalkLength - (z.SB - z.SA);
                z.OffsetAfter = offset;

                if (Corners.Count > 0 && Corners[Corners.Count - 1].SB > z.SA + tol)
                    Warnings.Add("Two corners are too close together for the stair width; treads around them overlap.");
                Corners.Add(z);
            }
            WarnTightBends(tol);
        }

        /// <summary>A corner of the curve.</summary>
        sealed class Kink
        {
            public double S, Turn;      // curve arc length; signed turn (+ = left)
            public Point3d Point;       // z = 0
            public Vector3d TIn, TOut;  // unit tangents before and after
        }

        /// <summary>
        /// A corner of one of the band's side lines, where the side lines of the legs First and
        /// Last meet (leg k runs up to corner k of the curve, so it joins corners First..Last-1).
        /// </summary>
        sealed class SideCorner
        {
            public int First, Last;     // the legs that meet here
            public double Turn;         // signed turn from one to the other
            public Point3d Point;       // where they meet
            public double SIn, SOut;    // that point as arc length along each leg
        }

        /// <summary>
        /// Where the side lines at band offset 'off' of two legs meet, each leg taken as the straight
        /// line through the corner next to the meeting point, with the tangent at that corner.
        /// </summary>
        static SideCorner MeetLegs(List<Kink> kinks, int first, int last, double off)
        {
            Kink a = kinks[first], b = kinks[last - 1];
            var la = Vector3d.CrossProduct(Vector3d.ZAxis, a.TIn);
            var lb = Vector3d.CrossProduct(Vector3d.ZAxis, b.TOut);
            double turn = 0;
            for (int k = first; k < last; k++) turn += kinks[k].Turn;
            var at = LineCross(a.Point + la * off, a.TIn, b.Point + lb * off, b.TOut);
            return new SideCorner
            {
                First = first, Last = last, Turn = turn, Point = at,
                SIn = a.S + (at - a.Point) * a.TIn,
                SOut = b.S + (at - b.Point) * b.TOut,
            };
        }

        /// <summary>The angle between a corner's two legs, as seen in the drawing, in degrees (its turn in radians).</summary>
        static double LegsApart(double turn) => 180 - Math.Abs(turn) * 180 / Math.PI;

        /// <summary>
        /// Whether a fan counts as a very sharp corner for rounding: its total turn or any real corner
        /// in it turning its way. Corners sharing a fan can add up to a gentle total: a corner with
        /// 23° between its legs is still very sharp when the corner before it turns the other way.
        /// </summary>
        static bool IsSharp(List<Kink> kinks, SideCorner fan, double turn)
        {
            if (Math.Abs(turn) > RoundAbove) return true;
            for (int k = fan.First; k < fan.Last; k++)
                if (kinks[k].Turn * turn > 0 && Math.Abs(kinks[k].Turn) > RoundAbove) return true;
            return false;
        }

        /// <summary>
        /// One side line of the band, as a polygon offset of the legs: a leg whose side line would
        /// run backwards is too short for this offset and drops out, and its neighbours meet
        /// directly. Returns where the remaining legs meet.
        /// </summary>
        static List<SideCorner> SideLine(List<Kink> kinks, double off)
        {
            var legs = new List<int>();
            for (int k = 0; k <= kinks.Count; k++) legs.Add(k);
            while (true)
            {
                var corners = new List<SideCorner>();
                for (int k = 0; k + 1 < legs.Count; k++) corners.Add(MeetLegs(kinks, legs[k], legs[k + 1], off));
                int worst = -1;
                double shortest = -1e-9;
                for (int k = 1; k + 1 < legs.Count; k++)
                {
                    double length = corners[k].SIn - corners[k - 1].SOut;
                    if (length >= shortest) continue;
                    // neighbours that would turn right back on each other have no meeting point: leave the leg
                    double turn = 0;
                    for (int c = legs[k - 1]; c < legs[k + 1]; c++) turn += kinks[c].Turn;
                    if (Math.Abs(turn) > MaxMergedTurn) continue;
                    worst = k;
                    shortest = length;
                }
                if (worst < 0) return corners;
                legs.RemoveAt(worst);
            }
        }

        /// <summary>
        /// The corner zones. Each side line of the band is worked out on its own (SideLine); each
        /// of its corners that turns toward that side is a pivot, where a fan starts, reaching
        /// from the cut line square to the leg before to the one square to the leg after.
        /// Corners close together therefore need no special case on one side: their legs simply
        /// drop out and they share one pivot. Fans from opposite sides can still reach each
        /// other: both then stop at the one line through their two pivots, each pivot being on
        /// the other fan's outer side, so the cut lines never cross. A cut line far longer than
        /// the stair is wide means no stair this wide can turn there: that corner is dropped
        /// with a warning.
        /// </summary>
        List<CornerZone> BuildCornerZones(List<Kink> kinks, double midOff, double tol)
        {
            double width = B - A;
            double eps = Math.Max(tol * 1e-3, 1e-9);
            var left = SideLine(kinks, B);
            var right = SideLine(kinks, A);

            var zones = new List<CornerZone>();
            var sides = new Dictionary<CornerZone, SideCorner>();
            var starts = new Dictionary<CornerZone, Point3d>();
            var ends = new Dictionary<CornerZone, Point3d>();
            var bends = new Dictionary<CornerZone, List<Point3d>>();
            void Build(List<SideCorner> side, List<SideCorner> other, bool isLeft)
            {
                foreach (var c in side)
                {
                    if (c.Turn > 0 != isLeft) continue; // turning away from this side: the outer side of a fan
                    double inner = isLeft ? B : A, outer = isLeft ? A : B;
                    double sa = c.SIn, sb = c.SOut;
                    Kink ka = kinks[c.First], kb = kinks[c.Last - 1];
                    Vector3d tA = ka.TIn, tB = kb.TOut;
                    Point3d pA = ka.Point + tA * (sa - ka.S), pB = kb.Point + tB * (sb - kb.S);
                    double zoneTurn = c.Turn;
                    bool single = c.Last == c.First + 1;
                    if (single && (c.First == 0 || sa >= kinks[c.First - 1].S) && (c.Last == kinks.Count || sb <= kinks[c.Last].S))
                    {
                        // exact for straight legs, refined numerically when a leg is curved near the corner
                        double sc = kinks[c.First].S;
                        RefinePivot(inner, sc, ref sa, ref sb, tol);
                        CurveFrame(sa, CurveEvaluationSide.Below, out pA, out tA);
                        CurveFrame(sb, CurveEvaluationSide.Above, out pB, out tB);
                        pA.Z = 0; pB.Z = 0;
                        zoneTurn = Math.Atan2(tA.X * tB.Y - tA.Y * tB.X, tA * tB);
                        if (zoneTurn * c.Turn <= 0 || 1 + tA * tB < 1e-3)
                        {
                            Warnings.Add(string.Format(
                                "A corner with {0:F0}° between its legs is too sharp for a stair this wide: its treads overlap.",
                                LegsApart(c.Turn)));
                            continue;
                        }
                    }

                    var z = new CornerZone { Turn = zoneTurn, InnerOff = inner, OuterOff = outer, MidOff = midOff, SA = sa, SB = sb };
                    z.SC = single ? kinks[c.First].S : (kinks[c.First].S + kinks[c.Last - 1].S) / 2;
                    z.DepthSign = isLeft ? -1 : 1;
                    var leftIn = Vector3d.CrossProduct(Vector3d.ZAxis, tA);
                    var leftOut = Vector3d.CrossProduct(Vector3d.ZAxis, tB);
                    z.UIn = leftIn * z.DepthSign;
                    z.UOut = leftOut * z.DepthSign;
                    z.Pivot = pA + leftIn * inner;
                    double reach = Math.Abs(outer - inner);
                    starts[z] = z.Pivot + z.UIn * reach;
                    ends[z] = z.Pivot + z.UOut * reach;

                    // the outer side's bends
                    var path = new List<Point3d>();
                    if (_roundSharp && IsSharp(kinks, c, zoneTurn))
                    {
                        // a polygon round the pivot whose sides touch a circle; each piece is a small miter
                        int m = (int)Math.Ceiling(Math.Abs(zoneTurn) / RoundSegmentTurn - 1e-9);
                        for (int k = 0; k < m; k++)
                        {
                            var dir = z.UIn;
                            dir.Rotate(zoneTurn * (k + 0.5) / m, Vector3d.ZAxis);
                            path.Add(z.Pivot + dir * (reach / Math.Cos(Math.Abs(zoneTurn) / (2 * m))));
                        }
                    }
                    else if (single)
                        path.Add(LineCross(pA + leftIn * outer, tA, pB + leftOut * outer, tB));
                    else
                        foreach (var q in other)
                            if (q.First >= c.First && q.Last <= c.Last) path.Add(q.Point);
                    bends[z] = path;
                    sides[z] = c;
                    zones.Add(z);
                }
            }
            Build(left, right, true);
            Build(right, left, false);
            zones.Sort((x, y) => x.SC.CompareTo(y.SC));

            // fans from opposite sides that reach each other meet at the line through their pivots
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int i = 0; i + 1 < zones.Count && !changed; i++)
                {
                    CornerZone a = zones[i], b = zones[i + 1];
                    if (a.Turn * b.Turn > 0 || a.SB - b.SA <= 2 * width) continue;
                    var drop = a.SB - a.SC >= b.SC - b.SA ? a : b;
                    double half = Math.Abs(Math.Tan(drop.Turn / 2));
                    double share = Math.Abs(drop.InnerOff) / Math.Max(width, 1e-12);
                    Warnings.Add(string.Format(
                        "A corner with {0:F0}° between its legs is too sharp for a stair this wide so close to the next corner: "
                        + "its treads overlap. Up to about {1:0.##} wide fits there; or lengthen the legs, or put the curve on the stair's other edge.",
                        LegsApart(drop.Turn), (b.SC - a.SC) / half / Math.Max(share, 1e-12)));
                    zones.Remove(drop);
                    changed = true;
                }
            }
            // Fans from opposite sides reach each other when their square cut lines would cross, or
            // when one spans corners of the other, whose legs dropped out of its side line.
            bool Reach(CornerZone x, CornerZone y) => x.Turn * y.Turn < 0
                && (x.SB > y.SA + eps || sides[x].First < sides[y].Last && sides[y].First < sides[x].Last);

            // A fan inside a bigger one from the other side gets no fan of its own, and its pivot is
            // one more bend on the bigger fan's outer side, if it would have to sweep backwards or
            // if the leg its square cut line ends on has dropped out of the bigger fan's side line.
            bool StartsInside(CornerZone inner, CornerZone big) => sides[inner].First > sides[big].First && sides[inner].Last <= sides[big].Last;
            bool EndsInside(CornerZone inner, CornerZone big) => sides[inner].Last < sides[big].Last && sides[inner].First >= sides[big].First;
            bool Forward(CornerZone z, Point3d from, Point3d to)
            {
                Vector3d u = from - z.Pivot, v = to - z.Pivot;
                return Math.Atan2(u.X * v.Y - u.Y * v.X, u * v) * Math.Sign(z.Turn) > 1e-6;
            }
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int i = 0; i + 1 < zones.Count && !changed; i++)
                {
                    CornerZone a = zones[i], b = zones[i + 1];
                    if (!Reach(a, b)) continue;
                    var aStart = i > 0 && Reach(zones[i - 1], a) ? zones[i - 1].Pivot : starts[a];
                    var bEnd = i + 2 < zones.Count && Reach(b, zones[i + 2]) ? zones[i + 2].Pivot : ends[b];
                    if (StartsInside(a, b) || !Forward(a, aStart, b.Pivot))
                    {
                        bends[b].Add(a.Pivot);
                        zones.RemoveAt(i);
                        changed = true;
                    }
                    else if (EndsInside(b, a) || !Forward(b, a.Pivot, bEnd))
                    {
                        bends[a].Add(b.Pivot);
                        zones.RemoveAt(i + 1);
                        changed = true;
                    }
                }
            }
            for (int i = 0; i + 1 < zones.Count; i++)
            {
                CornerZone a = zones[i], b = zones[i + 1];
                if (!Reach(a, b)) continue;
                double meet = (a.SB + b.SA) / 2;
                a.SB = b.SA = meet;
                ends[a] = b.Pivot;
                starts[b] = a.Pivot;
            }

            foreach (var z in zones) FinishCornerZone(z, starts[z], bends[z], ends[z]);
            return zones;
        }

        /// <summary>
        /// The outer path and the center path of a corner zone. Bends beyond its end cut lines
        /// are left out, and the center path is the outer path scaled toward the pivot, so every
        /// cut line reaches both at the same walk station. Walk stations start at 0 here.
        /// </summary>
        void FinishCornerZone(CornerZone z, Point3d start, List<Point3d> bends, Point3d end)
        {
            double sign = Math.Sign(z.Turn);
            var from = start - z.Pivot;
            double Sweep(Point3d q)
            {
                var v = q - z.Pivot;
                double a = Math.Atan2(from.X * v.Y - from.Y * v.X, from * v) * sign;
                return a < 0 ? a + 2 * Math.PI : a;
            }
            double sweep = Sweep(end);
            // in order round the pivot, so the cut lines of a fan can never cross
            var inside = new List<(double Angle, Point3d Point)>();
            foreach (var q in bends)
            {
                double a = Sweep(q);
                if (a > 1e-9 && a < sweep - 1e-9) inside.Add((a, q));
            }
            inside.Sort((x, y) => x.Angle.CompareTo(y.Angle));
            var outer = new List<Point3d> { start };
            foreach (var (a, q) in inside)
                if (q.DistanceTo(outer[outer.Count - 1]) > _minGap) outer.Add(q);
            outer.Add(end);

            double f = (z.MidOff - z.InnerOff) / (z.OuterOff - z.InnerOff);
            z.OuterPath = outer.ToArray();
            z.PathPoint = new Point3d[outer.Count];
            z.PathT = new double[outer.Count];
            for (int k = 0; k < outer.Count; k++)
            {
                z.PathPoint[k] = z.Pivot + (outer[k] - z.Pivot) * f;
                z.PathT[k] = k == 0 ? 0 : z.PathT[k - 1] + z.PathPoint[k].DistanceTo(z.PathPoint[k - 1]);
            }
            z.WalkLength = z.PathT[outer.Count - 1];
            z.BendT = new double[outer.Count - 2];
            Array.Copy(z.PathT, 1, z.BendT, 0, z.BendT.Length);
            z.HalfLength = z.BendT.Length == 1 ? z.BendT[0] : z.WalkLength / 2;
            z.Subdivisions = Math.Max(1, (int)Math.Ceiling(sweep / 2 / FacetTurn - 1e-9));
        }

        /// <summary>
        /// Tight bends of the smooth stretch s0..s1 of the curve. Where the curve bends tighter
        /// than the band reaches to the inside of the bend, that side line loops back across
        /// itself and square cut lines would cross inside the stair, so the treads overlap. Such
        /// a bend becomes a zone like a corner: its cut lines fan out from the point where the
        /// side line crosses itself, and the outer side keeps following the curve.
        /// </summary>
        void FindTightBends(double s0, double s1, double midOff, double tol, List<CornerZone> zones)
        {
            if (s1 - s0 <= _minGap) return;
            foreach (double off in new[] { A, B })
            {
                if (Math.Abs(off) < 1e-12) continue; // the curve itself never loops

                // loops of the side line: a coarse pass along the whole stretch, then a fine one
                // around every bend that is too tight but showed no loop (barely too tight bends
                // make loops smaller than the coarse spacing)
                double maxStep = Math.Max(Math.Abs(off) / 4, _minGap);
                var loops = SideLoops(s0, s1, off, maxStep);
                foreach (var (c0, c1, excess) in TooTight(s0, s1, off))
                {
                    if (excess <= tol || loops.Exists(l => l.Sa <= c0 && l.Sb >= c1)) continue;
                    // near an end of the curve the loop may close only past it, on the straight
                    // extension the stair is laid out along there
                    double w = Math.Abs(off) * 4;
                    double step = Math.Min(maxStep, Math.Max(excess / 4, _minGap));
                    double a = s0 > 0 ? Math.Max(s0, c0 - w) : c0 - w;
                    double b = s1 < _curveLength ? Math.Min(s1, c1 + w) : c1 + w;
                    step = Math.Max(step, (b - a) / 2000); // keep the pairwise search bounded
                    loops.AddRange(SideLoops(a, b, off, step));
                }
                // A bend turning a half circle or more is no bend to fan: the two sides of a closed
                // curve meet at its seam or across its middle, and those crossings are not loops.
                loops.RemoveAll(l => Math.Abs(TurnBetween(l.Sa, l.Sb)) >= Math.PI);
                loops.Sort((x, y) => (y.Sb - y.Sa).CompareTo(x.Sb - x.Sa));
                var kept = new List<(double Sa, double Sb)>();
                foreach (var l in loops)
                    if (!kept.Exists(k => l.Sa < k.Sb && l.Sb > k.Sa)) kept.Add(l);

                foreach (var (estA, estB) in kept)
                {
                    double sa = estA, sb = estB;
                    RefinePivot(off, (sa + sb) / 2, ref sa, ref sb, tol);
                    if (zones.Exists(k => sa < k.SB && sb > k.SA)) continue; // inside a corner zone
                    var z = TightBend(sa, sb, off, midOff);
                    if (z != null) zones.Add(z);
                }
            }
        }

        /// <summary>
        /// Where the side line at band offset 'off' crosses itself between curve arc lengths
        /// s0 and s1, as pairs of arc lengths (before, after), sampled at most 'step' apart.
        /// </summary>
        List<(double Sa, double Sb)> SideLoops(double s0, double s1, double off, double step)
        {
            var ss = new List<double>();
            var seeds = FacetStations(WalkCurve, s0, s1, FacetTurn / 4);
            for (int i = 0; i + 1 < seeds.Count; i++)
            {
                int m = Math.Max(1, (int)Math.Ceiling((seeds[i + 1] - seeds[i]) / step));
                for (int j = 0; j < m; j++) ss.Add(seeds[i] + (seeds[i + 1] - seeds[i]) * j / m);
            }
            ss.Add(seeds[seeds.Count - 1]);
            var q = ss.ConvertAll(x => SideAtLength(x, off));
            var loops = new List<(double Sa, double Sb)>();
            for (int i = 0; i + 1 < q.Count; i++)
                for (int j = i + 2; j + 1 < q.Count; j++)
                    if (SegmentsCross(q[i], q[i + 1], q[j], q[j + 1], out double u, out double v))
                        loops.Add((ss[i] + (ss[i + 1] - ss[i]) * u, ss[j] + (ss[j + 1] - ss[j]) * v));
            return loops;
        }

        /// <summary>
        /// Stretches between s0 and s1 where the curve bends toward band offset 'off' tighter
        /// than the band reaches there, with how much tighter at most.
        /// </summary>
        List<(double C0, double C1, double Excess)> TooTight(double s0, double s1, double off)
        {
            var res = new List<(double, double, double)>();
            const int n = 400;
            double start = double.NaN, excess = 0;
            for (int i = 0; i <= n; i++)
            {
                double s = s0 + (s1 - s0) * i / n;
                double e = 0;
                if (WalkCurve.LengthParameter(s, out double prm))
                {
                    var k = WalkCurve.CurvatureAt(prm);
                    k.Z = 0;
                    var tan = WalkCurve.TangentAt(prm);
                    bool left = tan.X * k.Y - tan.Y * k.X > 0;
                    if (k.Length > 1e-12 && left == off > 0) e = Math.Abs(off) - 1 / k.Length;
                }
                if (e > 0)
                {
                    if (double.IsNaN(start)) { start = s; excess = 0; }
                    excess = Math.Max(excess, e);
                }
                else if (!double.IsNaN(start))
                {
                    res.Add((start, s, excess));
                    start = double.NaN;
                }
            }
            if (!double.IsNaN(start)) res.Add((start, s1, excess));
            return res;
        }

        /// <summary>How far the curve turns between arc lengths sa and sb (radians, + = left).</summary>
        double TurnBetween(double sa, double sb)
        {
            var stations = FacetStations(WalkCurve, sa, sb, FacetTurn / 4);
            double turn = 0;
            for (int i = 0; i + 1 < stations.Count; i++)
            {
                var t0 = TangentAtLength(WalkCurve, stations[i]); t0.Z = 0;
                var t1 = TangentAtLength(WalkCurve, stations[i + 1]); t1.Z = 0;
                turn += Math.Atan2(t0.X * t1.Y - t0.Y * t1.X, t0 * t1);
            }
            return turn;
        }

        /// <summary>The zone of a tight bend between curve arc lengths sa and sb, whose side line at 'inner' loops.</summary>
        CornerZone TightBend(double sa, double sb, double inner, double midOff)
        {
            var z = new CornerZone { Smooth = true, SA = sa, SB = sb, SC = (sa + sb) / 2, MidOff = midOff };
            bool innerIsLeft = inner > midOff;
            z.InnerOff = inner;
            z.OuterOff = innerIsLeft ? A : B;
            z.DepthSign = innerIsLeft ? -1 : 1;
            CurveFrame(sa, CurveEvaluationSide.Default, out Point3d pA, out Vector3d tA);
            CurveFrame(sb, CurveEvaluationSide.Default, out Point3d pB, out Vector3d tB);
            var leftIn = Vector3d.CrossProduct(Vector3d.ZAxis, tA);
            var leftOut = Vector3d.CrossProduct(Vector3d.ZAxis, tB);
            var qa = new Point3d(pA.X + leftIn.X * inner, pA.Y + leftIn.Y * inner, 0);
            var qb = new Point3d(pB.X + leftOut.X * inner, pB.Y + leftOut.Y * inner, 0);
            z.Pivot = (qa + qb) / 2;
            z.UIn = leftIn * z.DepthSign;
            z.UOut = leftOut * z.DepthSign;
            double outer = z.OuterOff;
            z.OuterAt = s =>
            {
                CurveFrame(s, CurveEvaluationSide.Default, out Point3d c, out Vector3d tan);
                var l = Vector3d.CrossProduct(Vector3d.ZAxis, tan);
                return new Point3d(c.X + l.X * outer, c.Y + l.Y * outer, 0);
            };

            // how far the curve turns through the bend, and the walk along the band center
            double turn = TurnBetween(sa, sb);
            if (turn * (innerIsLeft ? 1 : -1) <= 0) return null;
            z.Turn = turn;
            z.Subdivisions = Math.Max(1, (int)Math.Ceiling(Math.Abs(turn) / 2 / FacetTurn - 1e-9));
            int n = Math.Max(32, z.Subdivisions * 8);
            z.ArcTable = new double[n + 1];
            z.WalkTable = new double[n + 1];
            double f = (midOff - inner) / (outer - inner);
            Point3d prev = Point3d.Unset;
            for (int k = 0; k <= n; k++)
            {
                double s = sa + (sb - sa) * k / n;
                var mid = z.Pivot + (z.OuterAt(s) - z.Pivot) * f;
                z.ArcTable[k] = s;
                z.WalkTable[k] = k == 0 ? 0 : z.WalkTable[k - 1] + mid.DistanceTo(prev);
                prev = mid;
            }
            z.WalkLength = z.WalkTable[n];
            z.HalfLength = z.WalkLength / 2;
            if (z.WalkLength <= _minGap) return null;
            for (int k = 1; k <= n; k++) // strictly increasing, for the lookups
                if (z.WalkTable[k] <= z.WalkTable[k - 1]) z.WalkTable[k] = z.WalkTable[k - 1] + 1e-12;
            return z;
        }

        /// <summary>
        /// Warns when the curve still bends tighter than the band reaches to the inside of the
        /// bend outside every zone (a bend turning back on itself has no crossing to fan from).
        /// </summary>
        void WarnTightBends(double tol)
        {
            const int n = 400;
            for (int i = 1; i < n; i++)
            {
                double s = _curveLength * i / n;
                if (Corners.Exists(z => s > z.SA - _minGap && s < z.SB + _minGap)) continue;
                if (!WalkCurve.LengthParameter(s, out double prm)) continue;
                var k = WalkCurve.CurvatureAt(prm);
                k.Z = 0;
                if (k.Length < 1e-12) continue;
                var tan = Horizontal(WalkCurve.DerivativeAt(prm, 1));
                bool left = tan.X * k.Y - tan.Y * k.X > 0;
                double reach = left ? B : -A; // how far the band reaches to the inside of the bend
                if (reach > tol && 1 / k.Length < reach - tol)
                {
                    Warnings.Add(string.Format(
                        "The curve bends tighter (radius {0:0.###}) than the stair reaches inward ({1:0.###}); treads there overlap.",
                        1 / k.Length, reach));
                    return;
                }
            }
        }

        /// <summary>Point on the side line at band offset 'off', at curve arc length s (z = 0).</summary>
        Point3d SideAtLength(double s, double off)
        {
            CurveFrame(s, CurveEvaluationSide.Default, out Point3d p, out Vector3d tan);
            var l = Vector3d.CrossProduct(Vector3d.ZAxis, tan);
            return new Point3d(p.X + l.X * off, p.Y + l.Y * off, 0);
        }

        /// <summary>Whether the plan segments p0-p1 and q0-q1 cross; u and v are where, along each.</summary>
        static bool SegmentsCross(Point3d p0, Point3d p1, Point3d q0, Point3d q1, out double u, out double v)
        {
            u = v = 0;
            double dx = p1.X - p0.X, dy = p1.Y - p0.Y, ex = q1.X - q0.X, ey = q1.Y - q0.Y;
            double den = dx * ey - dy * ex;
            if (Math.Abs(den) < 1e-15) return false;
            double wx = q0.X - p0.X, wy = q0.Y - p0.Y;
            u = (wx * ey - wy * ex) / den;
            v = (wx * dy - wy * dx) / den;
            return u > 0 && u < 1 && v > 0 && v < 1;
        }

        /// <summary>
        /// Newton iteration for the arc lengths sa (before the kink at sc) and sb (after it)
        /// whose points on the inner side line coincide. Keeps the straight-arm estimate
        /// when the iteration does not converge.
        /// </summary>
        void RefinePivot(double innerOff, double sc, ref double sa, ref double sb, double tol)
        {
            if (Math.Abs(innerOff) < 1e-12) { sa = sb = sc; return; }
            Point3d Q(double s, CurveEvaluationSide side)
            {
                CurveFrame(s, side, out Point3d p, out Vector3d tan);
                var l = Vector3d.CrossProduct(Vector3d.ZAxis, tan);
                return new Point3d(p.X + l.X * innerOff, p.Y + l.Y * innerOff, 0);
            }
            double x = sa, y = sb;
            double h = 1e-6 * (1 + _curveLength);
            for (int it = 0; it < 30; it++)
            {
                var f = Q(x, CurveEvaluationSide.Below) - Q(y, CurveEvaluationSide.Above);
                if (f.Length < tol * 1e-4) { sa = x; sb = y; return; }
                var dx = (Q(x - h, CurveEvaluationSide.Below) - Q(x - 2 * h, CurveEvaluationSide.Below)) / h;
                var dy = (Q(y + 2 * h, CurveEvaluationSide.Above) - Q(y + h, CurveEvaluationSide.Above)) / h;
                // solve dx * u - dy * v = -f
                double det = dx.X * -dy.Y - (-dy.X) * dx.Y;
                if (Math.Abs(det) < 1e-12) break;
                double u = (-f.X * -dy.Y - (-dy.X) * -f.Y) / det;
                double v = (dx.X * -f.Y - -f.X * dx.Y) / det;
                x = Math.Min(sc, x + u);
                y = Math.Max(sc, y + v);
            }
            var check = Q(x, CurveEvaluationSide.Below) - Q(y, CurveEvaluationSide.Above);
            if (check.Length < tol * 0.01) { sa = x; sb = y; }
        }

        /// <summary>Intersection of the plan lines p + u * dp and q + v * dq (z = 0).</summary>
        static Point3d LineCross(Point3d p, Vector3d dp, Point3d q, Vector3d dq)
        {
            double den = dp.X * dq.Y - dp.Y * dq.X;
            if (Math.Abs(den) < 1e-12) return new Point3d((p.X + q.X) / 2, (p.Y + q.Y) / 2, 0);
            double u = ((q.X - p.X) * dq.Y - (q.Y - p.Y) * dq.X) / den;
            return new Point3d(p.X + dp.X * u, p.Y + dp.Y * u, 0);
        }

        static Vector3d Horizontal(Vector3d[] derivatives)
        {
            if (derivatives == null || derivatives.Length < 2) return Vector3d.Zero;
            var v = derivatives[1];
            v.Z = 0;
            return v.Unitize() ? v : Vector3d.Zero;
        }

        /// <summary>The corner zone containing walk station t, or null.</summary>
        internal CornerZone ZoneAt(double t)
        {
            foreach (var z in Corners)
                if (t >= z.T0 - 1e-9 && t <= z.T1 + 1e-9) return z;
            return null;
        }

        /// <summary>
        /// Curve arc length at walk station t outside corner zones. At a kink of the
        /// curve, 'side' selects the arm the station belongs to.
        /// </summary>
        double CurveStation(double t, out CurveEvaluationSide side)
        {
            double off = 0;
            CornerZone before = null, after = null;
            foreach (var z in Corners)
            {
                if (t >= z.T1 - 1e-9) { off = z.OffsetAfter; before = z; }
                else if (after == null) after = z;
            }
            double s = t - off;
            side = CurveEvaluationSide.Default;
            double eps = 1e-9 * (1 + _curveLength);
            if (before != null && Math.Abs(s - before.SC) < eps) side = CurveEvaluationSide.Above;
            else if (after != null && Math.Abs(s - after.SC) < eps) side = CurveEvaluationSide.Below;
            return s;
        }

        /// <summary>Curve point and horizontal unit tangent at arc length s, extended straight past the ends.</summary>
        void CurveFrame(double s, CurveEvaluationSide side, out Point3d p, out Vector3d tangent)
        {
            var c = WalkCurve;
            double sc = Math.Min(_curveLength, Math.Max(0, s));
            if (!c.LengthParameter(sc, out double prm)) prm = sc <= 0 ? c.Domain.Min : c.Domain.Max;
            // at a kink LengthParameter can land a hair past it, on the other arm; a one-sided
            // tangent needs the kink parameter itself (the pivot of an inner-edge curve sits there)
            if (side != CurveEvaluationSide.Default)
                foreach (var k in _kinks)
                    if (Math.Abs(sc - k.Length) < 1e-9 * (1 + _curveLength)) { prm = k.Param; break; }
            p = c.PointAt(prm);
            tangent = Horizontal(c.DerivativeAt(prm, 1, side));
            if (tangent.IsZero)
            {
                tangent = c.TangentAt(prm);
                tangent.Z = 0;
                if (!tangent.Unitize()) tangent = Vector3d.XAxis;
            }
            if (s != sc) p += tangent * (s - sc);
        }

        /// <summary>Walk station of curve arc length s (0..curve length).</summary>
        double WalkStation(double s)
        {
            double off = 0;
            foreach (var z in Corners)
            {
                if (s > z.SA && s < z.SB && z.Smooth) return z.WalkAt(s);
                if (s > z.SA && s < z.SB)
                {
                    // the cut line through the curve point fans from the pivot: find where it meets the center line
                    CurveFrame(s, CurveEvaluationSide.Default, out Point3d p, out Vector3d _);
                    var ray = p - z.Pivot; ray.Z = 0;
                    if (!ray.Unitize()) return z.TM;
                    double lo = z.T0, hi = z.T1;
                    for (int i = 0; i < 60; i++)
                    {
                        double mid = (lo + hi) / 2;
                        var e = z.FanDir(mid);
                        // the fan sweeps from UIn toward UOut; compare the ray against the cut line at mid
                        double c = (e.X * ray.Y - e.Y * ray.X) * Math.Sign(z.Turn);
                        if (c > 0) lo = mid; else hi = mid;
                    }
                    return (lo + hi) / 2;
                }
                if (s >= z.SB) off = z.OffsetAfter;
            }
            return s + off;
        }

        /// <summary>
        /// Point on the band side line at the given offset (along the local left-of-travel
        /// vector) on the tread cut line at walk station t.
        /// </summary>
        public Point3d SidePoint(double t, double offset, double z)
        {
            var zone = ZoneAt(t);
            if (zone != null)
            {
                var q = zone.SidePoint(t, offset);
                return new Point3d(q.X, q.Y, z);
            }
            double s = CurveStation(t, out var side);
            CurveFrame(s, side, out Point3d p, out Vector3d tan);
            var left = Vector3d.CrossProduct(Vector3d.ZAxis, tan);
            return new Point3d(p.X + left.X * offset, p.Y + left.Y * offset, z);
        }

        /// <summary>Reference point of the walking line at t: on the curve, or on the band center inside corner zones.</summary>
        public Point3d WalkPoint(double t)
        {
            var zone = ZoneAt(t);
            if (zone != null) return zone.MidPoint(t);
            double s = CurveStation(t, out var side);
            CurveFrame(s, side, out Point3d p, out Vector3d _);
            return p;
        }

        /// <summary>Horizontal unit vector along the tread cut line at t, pointing to the left of travel.</summary>
        public Vector3d Across(double t)
        {
            var zone = ZoneAt(t);
            if (zone != null) return zone.FanDir(t) * zone.DepthSign;
            double s = CurveStation(t, out var side);
            CurveFrame(s, side, out Point3d _, out Vector3d tan);
            return Vector3d.CrossProduct(Vector3d.ZAxis, tan);
        }

        /// <summary>Horizontal unit walking direction at t (square to the cut line).</summary>
        public Vector3d Direction(double t)
        {
            return Vector3d.CrossProduct(Across(t), Vector3d.ZAxis);
        }

        /// <summary>
        /// Walk stations from S0 to S1 such that facets stay small: curved stretches
        /// are split where the curve turns, corner fans at their bisector and into
        /// equal parts of at most FacetTurn each.
        /// </summary>
        void CollectSamples(StepInfo step)
        {
            // tread edges and zone ends / bisectors are kept; fan subdivisions that would
            // leave a facet only a few tolerances wide are dropped
            var keys = new List<double> { step.S0, step.S1 };
            var grid = new List<double>();
            foreach (var z in Corners)
            {
                keys.Add(z.T0); keys.Add(z.TM); keys.Add(z.T1);
                keys.AddRange(z.BendT);
                for (int k = 1; k < z.Subdivisions; k++)
                {
                    grid.Add(z.T0 + (z.TM - z.T0) * k / z.Subdivisions);
                    grid.Add(z.TM + (z.T1 - z.TM) * k / z.Subdivisions);
                }
            }
            keys.RemoveAll(x => x < step.S0 || x > step.S1);
            var breaks = new List<double>(keys);
            foreach (var g in grid)
                if (g > step.S0 && g < step.S1 && breaks.TrueForAll(x => Math.Abs(x - g) > _minGap)) breaks.Add(g);
            breaks.Sort();

            var res = new List<double>();
            for (int i = 0; i + 1 < breaks.Count; i++)
            {
                double ta = breaks[i], tb = breaks[i + 1];
                if (tb - ta < 1e-9) continue;
                res.Add(ta);
                if (ZoneAt((ta + tb) / 2) != null) continue;
                // curved stretch: facet by curve turn, in curve arc length
                double sa = CurveStation(ta, out var _), sb = CurveStation(tb, out var _);
                double lo = Math.Max(0, sa), hi = Math.Min(_curveLength, sb);
                if (hi - lo < 1e-9) continue;
                // facets only a few tolerances long would collapse when the slab faces are welded
                foreach (var s in FacetStations(WalkCurve, lo, hi, FacetTurn))
                {
                    double t = ta + (s - sa);
                    if (t > res[res.Count - 1] + _minGap && t < tb - _minGap) res.Add(t);
                }
            }
            res.Add(step.S1);
            step.Samples = res;
        }

        /// <summary>
        /// Arc-length stations from s0 to s1 such that the plan direction turns at most
        /// maxTurn between consecutive stations. Accumulates tangent turn along the curve
        /// so wiggly freeform curves are faceted robustly.
        /// </summary>
        internal static List<double> FacetStations(Curve curve, double s0, double s1, double maxTurn)
        {
            var res = new List<double> { s0 };
            int n = 64;
            // start just inside the range so a kink at s0 does not count as turning
            var tPrev = TangentAtLength(curve, s0 + (s1 - s0) * 1e-4);
            tPrev.Z = 0;
            if (!tPrev.Unitize()) tPrev = Vector3d.XAxis;
            double acc = 0;
            for (int i = 1; i <= n; i++)
            {
                double s = s0 + (s1 - s0) * i / n;
                var t = TangentAtLength(curve, i == n ? s - (s1 - s0) * 1e-4 : s);
                t.Z = 0;
                if (!t.Unitize()) t = tPrev;
                double turn = Math.Abs(Math.Atan2(tPrev.X * t.Y - tPrev.Y * t.X, tPrev.X * t.X + tPrev.Y * t.Y));
                acc += turn;
                if (acc >= maxTurn || i == n)
                {
                    res.Add(s);
                    acc = 0;
                }
                tPrev = t;
            }
            return res;
        }

        public static Point3d PointAtLength(Curve curve, double length)
        {
            if (curve.LengthParameter(length, out double t)) return curve.PointAt(t);
            return length <= 0 ? curve.PointAtStart : curve.PointAtEnd;
        }

        public static Vector3d TangentAtLength(Curve curve, double length)
        {
            if (curve.LengthParameter(length, out double t)) return curve.TangentAt(t);
            return length <= 0 ? curve.TangentAtStart : curve.TangentAtEnd;
        }
    }
}
