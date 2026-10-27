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
    /// A sharp corner (tangent kink) of the walking curve. Outside corner zones the
    /// tread cut lines are square to the curve; inside a zone they fan out from the
    /// pivot, the inner corner of the stair band. The zone runs between the two
    /// square cut lines through the pivot and is walked along the band's center
    /// line, so winder treads keep the regular depth there for every curve role.
    /// </summary>
    public class CornerZone
    {
        public double SA, SC, SB;          // curve arc lengths: zone start, kink, zone end
        public double T0, TM, T1;          // walk stations: zone start, bisector, zone end
        public double Turn;                // signed turn angle in radians (+ = left)
        public double InnerOff, OuterOff;  // band offsets of the inner and outer side
        public Point3d Pivot;              // inner corner of the band (z = 0)
        public Point3d MidA, MidM, MidB;   // band center line through the zone (z = 0)
        public Vector3d UIn, UOut;         // unit vectors from the inner toward the outer side, before / after
        internal double DepthSign;         // band offset -> depth from the inner side
        internal double OffsetAfter;       // walk station minus curve arc length past this zone
        internal int Subdivisions;         // fan facets per half of the zone

        /// <summary>Point on the band center line at walk station t (T0 &lt;= t &lt;= T1).</summary>
        internal Point3d MidPoint(double t)
        {
            if (t <= TM)
            {
                double f = TM - T0 > 1e-12 ? Math.Min(1, Math.Max(0, (t - T0) / (TM - T0))) : 1;
                return MidA + (MidM - MidA) * f;
            }
            double g = T1 - TM > 1e-12 ? Math.Min(1, Math.Max(0, (t - TM) / (T1 - TM))) : 0;
            return MidM + (MidB - MidM) * g;
        }

        /// <summary>Unit direction of the cut line at walk station t, from the pivot outward.</summary>
        internal Vector3d FanDir(double t)
        {
            var e = MidPoint(t) - Pivot;
            e.Z = 0;
            if (!e.Unitize()) e = t <= TM ? UIn : UOut;
            return e;
        }

        /// <summary>
        /// Point of the cut line at walk station t on the band side line at the given
        /// offset. Side lines are mitered at the bisector, so the point lies on the
        /// side line of the arm the cut line leans toward.
        /// </summary>
        internal Point3d SidePoint(double t, double offset)
        {
            var e = FanDir(t);
            double depth = (offset - InnerOff) * DepthSign;
            double k = Math.Max(e * UIn, e * UOut);
            return Pivot + e * (depth / Math.Max(k, 1e-9));
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

        double _curveLength;
        double _narrowing;          // pyramid: how much narrower each step is (0: constant width)
        PyramidAlign _align;
        double _minWidth;
        double _minGap; // shortest facet the subdivision may create
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
            return layout;
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
                foreach (var key in new[] { z.T0, z.TM, z.T1 })
                    if (key > s0 && key < s1) st.Add(key);
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
            double offset = 0;
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
                        "The curve turns back on itself ({0:F0} degrees); that corner cannot be built.",
                        Math.Abs(turn) * 180 / Math.PI));
                    continue;
                }

                var z = new CornerZone();
                bool innerIsLeft = turn > 0;
                z.InnerOff = innerIsLeft ? B : A;
                z.OuterOff = innerIsLeft ? A : B;
                z.DepthSign = innerIsLeft ? -1 : 1;
                double sc = curve.GetLength(new Interval(curve.Domain.Min, pk));
                z.SC = sc;
                _kinks.Add((sc, pk));

                // the pivot is where the inner side lines of both arms cross; exact for straight
                // arms, refined numerically when an arm is curved near the kink
                double e = Math.Abs(z.InnerOff) * Math.Tan(Math.Abs(turn) / 2);
                double sa = sc - e, sb = sc + e;
                RefinePivot(z.InnerOff, sc, ref sa, ref sb, tol);
                CurveFrame(sa, CurveEvaluationSide.Below, out Point3d pA, out Vector3d tA);
                CurveFrame(sb, CurveEvaluationSide.Above, out Point3d pB, out Vector3d tB);
                pA.Z = 0; pB.Z = 0;
                double zoneTurn = Math.Atan2(tA.X * tB.Y - tA.Y * tB.X, tA * tB);
                if (zoneTurn * turn <= 0 || 1 + tA * tB < 1e-3) continue;
                z.Turn = zoneTurn;
                var leftIn = Vector3d.CrossProduct(Vector3d.ZAxis, tA);
                var leftOut = Vector3d.CrossProduct(Vector3d.ZAxis, tB);
                z.UIn = leftIn * z.DepthSign;
                z.UOut = leftOut * z.DepthSign;
                z.Pivot = pA + leftIn * z.InnerOff;
                z.MidA = pA + leftIn * midOff;
                z.MidB = pB + leftOut * midOff;
                z.MidM = LineCross(z.MidA, tA, z.MidB, tB);
                z.SA = sa;
                z.SB = sb;
                double l1 = z.MidA.DistanceTo(z.MidM), l2 = z.MidM.DistanceTo(z.MidB);
                z.T0 = z.SA + offset;
                z.TM = z.T0 + l1;
                z.T1 = z.TM + l2;
                offset += (l1 + l2) - (z.SB - z.SA);
                z.OffsetAfter = offset;
                z.Subdivisions = Math.Max(1, (int)Math.Ceiling(Math.Abs(z.Turn) / 2 / FacetTurn - 1e-9));

                if (Corners.Count > 0 && Corners[Corners.Count - 1].SB > z.SA + tol)
                    Warnings.Add("Two corners are too close together for the stair width; treads around them overlap.");
                Corners.Add(z);
            }
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
