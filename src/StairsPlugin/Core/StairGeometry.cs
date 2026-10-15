using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace Stairs.Core
{
    public class StairResult
    {
        public List<Brep> Steps = new List<Brep>();
        public List<TextEntity> Numbers = new List<TextEntity>();
        public List<Brep> RailingLeft = new List<Brep>();
        public List<Brep> RailingRight = new List<Brep>();
        public List<string> Warnings = new List<string>();
        public BoundingBox Box = BoundingBox.Unset;
        public StairLayout Layout;  // step count, riser and regular tread depth actually used
    }

    public static class StairGeometry
    {
        /// <summary>
        /// Boolean tolerance: a multiple of the document tolerance, but never larger
        /// than a fraction of the smallest feature being cut, so loose-tolerance
        /// documents (1 cm) cannot shred small geometry.
        /// </summary>
        internal static double BoolTol(double tol, double feature)
        {
            return Math.Min(tol * 10, Math.Max(tol, feature * 0.2));
        }

        /// <summary>Builds all stair geometry from the given options. Pure RhinoCommon, no document access.</summary>
        public static StairResult Build(StairOptions o)
        {
            var result = new StairResult();
            var layout = StairLayout.Compute(o);
            result.Layout = layout;
            result.Warnings.AddRange(layout.Warnings);
            if (layout.Steps.Count == 0) return result;

            double tol = o.AbsoluteTolerance;
            double a = layout.A, b = layout.B; // band boundaries, offsets along the local left-of-travel vector

            // a slab staircase is joined directly from planar faces as one solid; only if that
            // fails is it built step by step and unioned
            Brep wholeSlab = o.Structure == StairStructure.Slab
                ? JoinPlanarFaces(SlabFaces(o, layout, layout.Steps, tol), tol)
                : null;
            if (wholeSlab != null) result.Steps.Add(wholeSlab);

            for (int i = 0; i < layout.Steps.Count; i++)
            {
                var step = layout.Steps[i];
                var plan = TreadPolygon(layout, step, step.A, step.B, tol);

                if (o.Structure == StairStructure.Plank)
                {
                    if (plan.Count < 3) continue;
                    double t = Math.Min(Math.Max(o.PlankThickness, tol), step.Depth * 0.5);
                    var brep = Prism(plan, step.TopZ - t, step.TopZ, tol);
                    if (brep != null) result.Steps.Add(brep);
                }
                else if (wholeSlab == null)
                {
                    var solid = BuildSlabStep(o, layout, step, tol);
                    if (solid != null)
                        foreach (var s in solid) result.Steps.Add(s);
                }

                if (o.ShowNumbers)
                    result.Numbers.Add(MakeNumber(layout, o, step, plan, step.A, step.B, tol));
            }

            if (o.Structure == StairStructure.Slab && wholeSlab == null && result.Steps.Count > 1)
            {
                // Unioning abutting step solids only succeeds reliably on straight runs; on curves the
                // coplanar overlapping riser faces make Rhino booleans flaky. Only accept a merge
                // when every resulting piece is a closed solid, otherwise keep the (closed) pieces.
                Brep[] merged = null;
                foreach (double f in new[] { 10.0, 25.0, 50.0 })
                {
                    var m = TryMerge(result.Steps, Math.Min(tol * f, o.Width * 0.1));
                    if (m != null && m.Length < result.Steps.Count && Array.TrueForAll(m, x => x.IsSolid))
                    {
                        merged = m;
                        break;
                    }
                }
                if (merged != null)
                {
                    if (merged.Length == 1)
                        result.Steps = merged.ToList();
                    else
                        result.Warnings.Add("Slab solids were merged into " + merged.Length + " solids.");
                }
                else
                    result.Warnings.Add("Slab steps were created as separate (touching) closed solids.");
            }

            if (o.Railing != RailSide.None)
            {
                if (o.Railing == RailSide.Left || o.Railing == RailSide.Both)
                    result.RailingLeft = RailBuilder.Build(o, layout, a, b, true, tol);
                if (o.Railing == RailSide.Right || o.Railing == RailSide.Both)
                    result.RailingRight = RailBuilder.Build(o, layout, a, b, false, tol);
            }

            var boxes = new List<BoundingBox>();
            foreach (var brep in result.Steps) boxes.Add(brep.GetBoundingBox(true));
            foreach (var brep in result.RailingLeft) boxes.Add(brep.GetBoundingBox(true));
            foreach (var brep in result.RailingRight) boxes.Add(brep.GetBoundingBox(true));
            boxes.RemoveAll(x => !x.IsValid);
            if (boxes.Count > 0)
            {
                var box = boxes[0];
                foreach (var bb in boxes) box = BoundingBox.Union(box, bb);
                result.Box = box;
            }

            return result;
        }

        internal static void GetBandOffsets(StairOptions o, StairLayout layout, double w, out double a, out double b)
        {
            // offsets are along S(s) = left of travel; inner side sign tells which side is concave
            bool innerIsLeft = layout.InnerDirIsLeft;
            switch (o.Role)
            {
                case CurveRole.Inner: // the curve is the inner edge; the body lies away from it
                    if (innerIsLeft) { a = -w; b = 0; } else { a = 0; b = w; }
                    break;
                case CurveRole.Outer: // the curve is the outer edge; the body lies toward the inner side
                    if (innerIsLeft) { a = 0; b = w; } else { a = -w; b = 0; }
                    break;
                default: // Mid
                    a = -w / 2; b = w / 2;
                    break;
            }
        }

        /// <summary>Point on the band side line at 'offset', on the tread cut line at walk station s.</summary>
        internal static Point3d BandPoint(StairLayout layout, double s, double offset, double z)
        {
            return layout.SidePoint(s, offset, z);
        }

        /// <summary>
        /// Plan polygon of one tread: bounded by the cut lines at S0 and S1 and the two
        /// band sides between them. Cut lines never cross, so treads tile the band
        /// without gaps or overlaps, also around corners.
        /// </summary>
        internal static List<Point3d> TreadPolygon(StairLayout layout, StepInfo step, double a, double b, double tol)
        {
            return SpanPolygon(layout, step.Samples, a, b, 0, tol);
        }

        /// <summary>Plan polygon between the first and last of the given walk stations, at height z.</summary>
        internal static List<Point3d> SpanPolygon(StairLayout layout, List<double> stations, double a, double b, double z, double tol)
        {
            var pts = new List<Point3d>();
            foreach (var t in stations) pts.Add(layout.SidePoint(t, a, z));
            for (int i = stations.Count - 1; i >= 0; i--) pts.Add(layout.SidePoint(stations[i], b, z));
            return CleanPolygon(pts, tol);
        }

        /// <summary>
        /// Removes repeated and collinear vertices of a closed polygon (given without the
        /// closing point). Around a corner pivot the inner side collapses to one point.
        /// </summary>
        internal static List<Point3d> CleanPolygon(List<Point3d> pts, double tol)
        {
            var res = new List<Point3d>();
            foreach (var p in pts)
                if (res.Count == 0 || res[res.Count - 1].DistanceTo(p) > tol * 0.01) res.Add(p);
            while (res.Count > 1 && res[res.Count - 1].DistanceTo(res[0]) <= tol * 0.01) res.RemoveAt(res.Count - 1);
            bool removed = true;
            while (removed && res.Count > 3)
            {
                removed = false;
                for (int i = 0; i < res.Count && res.Count > 3; i++)
                {
                    var p0 = res[(i + res.Count - 1) % res.Count];
                    var p1 = res[i];
                    var p2 = res[(i + 1) % res.Count];
                    var d = p2 - p0;
                    double len = d.Length;
                    if (len < 1e-12) continue;
                    double off = Math.Abs((p1.X - p0.X) * d.Y - (p1.Y - p0.Y) * d.X) / len;
                    double along = ((p1 - p0) * d) / (len * len);
                    if (off < tol * 0.01 && along > 0 && along < 1)
                    {
                        res.RemoveAt(i);
                        removed = true;
                        i--;
                    }
                }
            }
            return res;
        }

        /// <summary>Vertical prism from zBottom to zTop over a closed planar polygon (points carry z = zBottom).</summary>
        internal static Brep Prism(List<Point3d> planAtBottom, double zBottom, double zTop, double tol)
        {
            double h = zTop - zBottom;
            if (Math.Abs(h) < tol) return null;
            var pts = planAtBottom.Select(p => new Point3d(p.X, p.Y, zBottom)).ToList();
            pts.Add(pts[0]);
            var pl = new PolylineCurve(pts.ToArray());
            if (!pl.IsClosed) return null;
            var ext = Extrusion.Create(pl, h, true);
            if (ext == null) return null;
            var bb = ext.GetBoundingBox(false);
            // Extrusion.Create may extrude along the negative plane normal; detect and flip.
            bool wentDown = bb.Max.Z <= zBottom + Math.Max(tol, Math.Abs(h) * 1e-6) && bb.Min.Z < zBottom - tol;
            bool wentUp = bb.Min.Z >= zBottom - Math.Max(tol, Math.Abs(h) * 1e-6);
            if (h > 0 && !wentUp || h < 0 && !wentDown)
            {
                ext = Extrusion.Create(pl, -h, true);
                if (ext == null) return null;
            }
            return ext.ToBrep();
        }

        static Brep[] TryMerge(List<Brep> breps, double tolerance)
        {
            try { return Brep.CreateBooleanUnion(breps, tolerance); }
            catch { return null; }
        }

        /// <summary>
        /// Slab stair solid for one step, joined from planar faces; falls back to
        /// prism-and-wedge booleans when the faces do not join into a closed solid.
        /// </summary>
        static List<Brep> BuildSlabStep(StairOptions o, StairLayout layout, StepInfo step, double tol)
        {
            var solid = JoinPlanarFaces(SlabFaces(o, layout, new List<StepInfo> { step }, tol), tol);
            if (solid != null) return new List<Brep> { solid };
            return BuildSlabStepByBooleans(o, layout, step, step.A, step.B, tol);
        }

        /// <summary>
        /// Planar faces bounding the slab under consecutive steps: treads on top, vertical
        /// faces along each tread's sides, a soffit sloped along the walking line (slab
        /// thickness below the nosings), and the faces in the vertical plane of every cut
        /// line. Each tread has its own width (pyramid stairs), so the solid is the union of
        /// the steps. Every facet's soffit is level along its cut lines, except that in a
        /// corner fan the pivot end of a facet stays at the facet's start height: consecutive
        /// facets then meet in vertical seams at the pivot, like a helical newel.
        /// </summary>
        static List<List<Point3d>> SlabFaces(StairOptions o, StairLayout layout, List<StepInfo> steps, double tol)
        {
            double t = Math.Max(o.SlabThickness, tol);
            var facets = new List<SlabFacet>();
            var faces = new List<List<Point3d>>();
            Point3d P(double s, double offset, double z) => layout.SidePoint(s, offset, z);

            foreach (var step in steps)
            {
                double zPrev = step.Number == 1 ? layout.BaseZ : step.TopZ - layout.RiserHeight;
                double bottomA = zPrev - t; // soffit at the front edge (bearing below the floor for step 1)
                double bottomB = step.TopZ - t; // soffit at the back edge
                double H(double s) => bottomA + (bottomB - bottomA) * (s - step.S0) / Math.Max(step.Depth, tol);
                var st = step.Samples;
                for (int k = 0; k + 1 < st.Count; k++)
                {
                    var f = new SlabFacet { T0 = st[k], T1 = st[k + 1], A = step.A, B = step.B, Top = step.TopZ };
                    double h0 = H(st[k]), h1 = H(st[k + 1]);
                    f.AStart = f.BStart = h0;
                    f.AEnd = f.BEnd = h1;
                    var zone = layout.ZoneAt((st[k] + st[k + 1]) / 2);
                    if (zone != null)
                    {
                        if (Math.Abs(zone.InnerOff - layout.A) < Math.Abs(zone.InnerOff - layout.B)) f.AEnd = h0;
                        else f.BEnd = h0;
                    }
                    facets.Add(f);
                }

                var tread = st.Select(s => P(s, step.A, step.TopZ)).ToList();
                for (int k = st.Count - 1; k >= 0; k--) tread.Add(P(st[k], step.B, step.TopZ));
                faces.Add(tread);
            }

            for (int k = 0; k < facets.Count; k++)
            {
                var f = facets[k];
                Point3d a0 = P(f.T0, f.A, 0), a1 = P(f.T1, f.A, 0), b0 = P(f.T0, f.B, 0), b1 = P(f.T1, f.B, 0);
                Point3d Z(Point3d p, double z) => new Point3d(p.X, p.Y, z);
                faces.Add(new List<Point3d> { Z(a0, f.Top), Z(a1, f.Top), Z(a1, f.AEnd), Z(a0, f.AStart) });
                faces.Add(new List<Point3d> { Z(b0, f.Top), Z(b1, f.Top), Z(b1, f.BEnd), Z(b0, f.BStart) });
                faces.Add(new List<Point3d> { Z(a0, f.AStart), Z(a1, f.AEnd), Z(b1, f.BEnd) });
                faces.Add(new List<Point3d> { Z(a0, f.AStart), Z(b1, f.BEnd), Z(b0, f.BStart) });
                faces.AddRange(CutLineFaces(layout, f.T0, k > 0 ? facets[k - 1] : null, f));
            }
            var last = facets[facets.Count - 1];
            faces.AddRange(CutLineFaces(layout, last.T1, last, null));
            return faces;
        }

        /// <summary>One facet of a slab step: between two cut lines, with its soffit heights at both sides.</summary>
        sealed class SlabFacet
        {
            public double T0, T1;            // walk stations of its cut lines
            public double A, B;              // side offsets
            public double Top;               // tread height
            public double AStart, AEnd, BStart, BEnd; // soffit heights at the A and B side, at T0 and T1

            internal double BottomAt(double offset, bool atEnd)
            {
                double za = atEnd ? AEnd : AStart, zb = atEnd ? BEnd : BStart;
                return za + (zb - za) * (offset - A) / Math.Max(B - A, 1e-12);
            }
        }

        /// <summary>
        /// Faces in the vertical plane of the cut line at walk station s, between the facet
        /// ending there ('before') and the one starting there ('after'); either may be null at
        /// the ends of the stair. In the plane's (offset, height) coordinates each facet covers
        /// its width from its soffit up to its tread, and the boundary of the solid there is
        /// the symmetric difference: the riser, a soffit seam, and the exposed ends of
        /// whichever step is wider.
        /// </summary>
        static IEnumerable<List<Point3d>> CutLineFaces(StairLayout layout, double s, SlabFacet before, SlabFacet after)
        {
            var cuts = new List<double>();
            foreach (var f in new[] { before, after })
                if (f != null) { cuts.Add(f.A); cuts.Add(f.B); }
            cuts.Sort();
            var faces = new List<List<Point3d>>();
            List<Point3d> Quad(double u0, double z00, double z01, double u1, double z10, double z11)
            {
                return new List<Point3d>
                {
                    layout.SidePoint(s, u0, z00), layout.SidePoint(s, u1, z10),
                    layout.SidePoint(s, u1, z11), layout.SidePoint(s, u0, z01),
                };
            }
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double u0 = cuts[i], u1 = cuts[i + 1];
                if (u1 - u0 < 1e-12) continue;
                double um = (u0 + u1) / 2;
                bool inBefore = before != null && um > before.A && um < before.B;
                bool inAfter = after != null && um > after.A && um < after.B;
                if (inBefore && inAfter)
                {
                    // soffit seam (the two soffits never cross: the later one starts at or above
                    // where the earlier one ends) and the riser
                    faces.Add(Quad(u0, before.BottomAt(u0, true), after.BottomAt(u0, false),
                                   u1, before.BottomAt(u1, true), after.BottomAt(u1, false)));
                    faces.Add(Quad(u0, before.Top, after.Top, u1, before.Top, after.Top));
                }
                else if (inBefore)
                    faces.Add(Quad(u0, before.BottomAt(u0, true), before.Top, u1, before.BottomAt(u1, true), before.Top));
                else if (inAfter)
                    faces.Add(Quad(u0, after.BottomAt(u0, false), after.Top, u1, after.BottomAt(u1, false), after.Top));
            }
            return faces;
        }

        /// <summary>
        /// Joins planar polygons into a closed brep. Degenerate polygons are dropped and
        /// vertices lying on another polygon's edge are inserted there, so edges match
        /// one to one. Returns null unless the result is a valid closed solid.
        /// </summary>
        internal static Brep JoinPlanarFaces(List<List<Point3d>> polygons, double tol)
        {
            // Faces are built and joined at a fraction of the document tolerance: real features
            // can be about as small as the tolerance (a pyramid step-in of 1 cm in a document
            // with a 1 cm tolerance), and must not collapse. Points closer than that are the
            // same point computed twice, and are welded so every face uses one copy.
            double eps = tol * 0.01;
            var welded = new List<Point3d>();
            Point3d Weld(Point3d p)
            {
                foreach (var q in welded)
                    if (q.DistanceTo(p) < eps) return q;
                welded.Add(p);
                return p;
            }
            polygons = polygons.Select(poly => poly.Select(Weld).ToList()).ToList();
            var all = welded;
            var pieces = new List<Brep>();
            foreach (var raw in polygons)
            {
                var poly = new List<Point3d>();
                foreach (var p in raw)
                    if (poly.Count == 0 || poly[poly.Count - 1].DistanceTo(p) > eps) poly.Add(p);
                while (poly.Count > 1 && poly[poly.Count - 1].DistanceTo(poly[0]) <= eps) poly.RemoveAt(poly.Count - 1);
                if (poly.Count < 3) continue;

                // split edges at T-junctions
                var split = new List<Point3d>();
                for (int i = 0; i < poly.Count; i++)
                {
                    var p0 = poly[i];
                    var p1 = poly[(i + 1) % poly.Count];
                    split.Add(p0);
                    var d = p1 - p0;
                    double len2 = d * d;
                    if (len2 < eps * eps) continue;
                    var onEdge = new List<Tuple<double, Point3d>>();
                    foreach (var v in all)
                    {
                        double f = ((v - p0) * d) / len2;
                        if (f <= 1e-9 || f >= 1 - 1e-9) continue;
                        if ((p0 + d * f).DistanceTo(v) > eps) continue;
                        if (v.DistanceTo(p0) <= eps || v.DistanceTo(p1) <= eps) continue;
                        onEdge.Add(Tuple.Create(f, v));
                    }
                    foreach (var x in onEdge.OrderBy(x => x.Item1))
                        if (split[split.Count - 1].DistanceTo(x.Item2) > eps) split.Add(x.Item2);
                }
                split.Add(split[0]);
                var curve = new PolylineCurve(split);
                var amp = AreaMassProperties.Compute(curve);
                if (amp == null || amp.Area < eps * eps) continue; // collapsed at a pivot
                var planar = Brep.CreatePlanarBreps(curve, eps);
                if (planar == null || planar.Length != 1) return null;
                pieces.Add(planar[0]);
            }
            var joined = Brep.JoinBreps(pieces, eps);
            if (joined == null || joined.Length != 1 || !joined[0].IsSolid || !joined[0].IsValid) return null;
            var brep = joined[0];
            brep.MergeCoplanarFaces(tol);
            if (brep.SolidOrientation == BrepSolidOrientation.Inward) brep.Flip();
            return brep.IsValid && brep.IsSolid ? brep : null;
        }

        /// <summary>Fallback slab solid for one step: prism + sloped soffit wedge cuts, one per facet of the tread.</summary>
        static List<Brep> BuildSlabStepByBooleans(StairOptions o, StairLayout layout, StepInfo step, double a, double b, double tol)
        {
            double t = Math.Max(o.SlabThickness, tol);
            double zTop = step.TopZ;
            double zPrev = step.Number == 1 ? layout.BaseZ : step.TopZ - layout.RiserHeight;
            double bottomA = zPrev - t; // soffit at the front edge (bearing below the floor for step 1)
            double bottomB = step.TopZ - t; // soffit at the back edge

            var pieces = new List<Brep>();
            for (int k = 0; k < step.Samples.Count - 1; k++)
            {
                double sa = step.Samples[k], sb = step.Samples[k + 1];
                double ba = bottomA + (bottomB - bottomA) * (sa - step.S0) / Math.Max(step.Depth, tol);
                double bb = bottomA + (bottomB - bottomA) * (sb - step.S0) / Math.Max(step.Depth, tol);
                double floorZ = Math.Min(ba, bb) - tol * 10;

                var plan = SpanPolygon(layout, new List<double> { sa, sb }, a, b, floorZ, tol);
                if (plan.Count < 3) continue;
                var prism = Prism(plan, floorZ, zTop, tol);
                if (prism == null) continue;

                var wedge = SoffitCutter(layout, sa, ba, sb, bb, o, tol);
                if (wedge == null) { pieces.Add(prism); continue; }

                var diff = Brep.CreateBooleanDifference(new[] { prism }, new[] { wedge }, BoolTol(tol, o.SlabThickness));
                if (diff != null && diff.Length > 0)
                    pieces.Add(diff.OrderByDescending(y => y.GetVolume(tol, tol * 10)).First());
                else
                    pieces.Add(prism); // uncut fallback: still a valid solid, just too deep at the front
            }
            if (pieces.Count > 1)
            {
                var union = Brep.CreateBooleanUnion(pieces, BoolTol(tol, o.SlabThickness));
                if (union != null && union.Length > 0) pieces = union.ToList();
            }
            return pieces;
        }

        /// <summary>
        /// Cutter below the soffit of one facet (walk stations sa..sb at soffit heights za..zb).
        /// In a corner fan the soffit is the plane through the outer ends of both cut lines and
        /// the pivot, so neighboring fan facets meet along their shared cut line at the outside.
        /// </summary>
        static Brep SoffitCutter(StairLayout layout, double sa, double za, double sb, double zb, StairOptions o, double tol)
        {
            var zone = layout.ZoneAt((sa + sb) / 2);
            if (zone == null)
                return CutWedge(layout, sa, za, sb, zb, o, tol, true);
            var pa = layout.SidePoint(sa, zone.OuterOff, za);
            var pb = layout.SidePoint(sb, zone.OuterOff, zb);
            var pv = new Point3d(zone.Pivot.X, zone.Pivot.Y, (za + zb) / 2);
            var n = Vector3d.CrossProduct(pa - pv, pb - pv);
            if (!n.Unitize()) return CutWedge(layout, sa, za, sb, zb, o, tol, true);
            double big = (Math.Abs(sb - sa) + o.Width + Math.Abs(zb - za)) * 8;
            return HalfSpaceBox(new Point3d((pa.X + pb.X + pv.X) / 3, (pa.Y + pb.Y + pv.Y) / 3, (pa.Z + pb.Z + pv.Z) / 3), n, big, true);
        }

        /// <summary>
        /// Big oriented box covering everything below (cutBelow) or above (cutAbove)
        /// the plane through (s=sa, z=za) and (s=sb, z=zb) on the walking line that
        /// contains the horizontal cut-line direction, used as a boolean cutter.
        /// </summary>
        internal static Brep CutWedge(StairLayout layout, double sa, double za, double sb, double zb,
            StairOptions o, double tol, bool cutBelow)
        {
            var pa = layout.WalkPoint(sa); pa.Z = za;
            var pb = layout.WalkPoint(sb); pb.Z = zb;
            var dir = pb - pa;
            if (dir.Length < tol) return null;
            // the across direction degenerates when the chord crosses a corner,
            // so fall back from the mid station to the end stations
            var n = Vector3d.Zero;
            foreach (var s in new[] { (sa + sb) / 2, sa, sb })
            {
                var across = layout.Across(s);
                var candidate = Vector3d.CrossProduct(dir, across);
                if (candidate.Length > tol) { n = candidate; break; }
            }
            if (n.Length < tol) return null;
            n.Unitize();
            double big = (Math.Abs(sb - sa) + o.Width + Math.Abs(zb - za)) * 8;
            return HalfSpaceBox((pa + pb) / 2, n, big, cutBelow);
        }

        /// <summary>Big box filling the half-space below (or above) the plane through 'origin' with normal 'n'.</summary>
        internal static Brep HalfSpaceBox(Point3d origin, Vector3d n, double big, bool below)
        {
            if (n.Z < 0) n.Reverse(); // normal points up
            var plane = new Plane(origin, n);
            var box = new Box(plane,
                new Interval(-big, big),
                new Interval(-big, big),
                below ? new Interval(-big, 0) : new Interval(0, big));
            return Brep.CreateFromBox(box);
        }

        static TextEntity MakeNumber(StairLayout layout, StairOptions o, StepInfo step,
            List<Point3d> plan, double a, double b, double tol)
        {
            // place the number at the plan centroid of the tread so it is always centered,
            // also for landings, corner treads and inner/outer curve roles
            Point3d pos;
            PolylineCurve pl = null;
            if (plan != null && plan.Count >= 3)
            {
                var closed = new List<Point3d>(plan) { plan[0] };
                pl = new PolylineCurve(closed.ToArray());
            }
            var ap = pl != null && pl.IsClosed ? AreaMassProperties.Compute(pl) : null;
            if (ap != null && ap.Centroid.IsValid)
                pos = ap.Centroid;
            else
                pos = BandPoint(layout, (step.S0 + step.S1) / 2, (a + b) / 2, 0);

            var tan = layout.Direction((step.S0 + step.S1) / 2);
            var right = Vector3d.CrossProduct(tan, Vector3d.ZAxis); // reader's left-to-right when facing up-slope
            var plane = new Plane(new Point3d(pos.X, pos.Y, step.TopZ), right, tan);
            var style = o.NumberStyle ?? new Rhino.DocObjects.DimensionStyle();
            var te = TextEntity.Create(step.Number.ToString(), plane, style, false, 0, 0);
            // size from the regular tread depth so landings do not get oversized numbers
            double h = o.NumberHeight > 0 ? o.NumberHeight : Math.Min(o.Width, layout.TreadDepth) * 0.28;
            te.TextHeight = h;
            te.Justification = TextJustification.MiddleCenter;
            return te;
        }
    }
}
