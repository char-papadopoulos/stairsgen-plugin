using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace Stairs.Core
{
    /// <summary>
    /// Builds railing geometry along one side of the stair band.
    /// The handrail, glass top edge and intermediate bars follow one continuous
    /// pitch line (straight from the first nosing to the last), not the stepped profile.
    /// </summary>
    internal static class RailBuilder
    {
        /// <summary>left == true builds on the left side of the walking direction (band offset b).</summary>
        internal static List<Brep> Build(StairOptions o, StairLayout layout, double a, double b, bool left, double tol)
        {
            var output = new List<Brep>();
            var steps = layout.Steps;
            int n = steps.Count;
            if (n == 0) return output;

            double inward = left ? -1 : 1; // toward the stair body, along the local left-of-travel vector
            double inset = Math.Max(o.RailInset, tol);
            double h = Math.Max(o.RailHeight, tol);
            double sEnd = steps[n - 1].S1;

            // Offset of the railing at walk station s. It sits the inset inside each tread's edge
            // at that tread's nosing and moves linearly to the next tread's edge, so on pyramid
            // stairs it follows the narrowing outline while always staying on the tread below
            // it (the next tread is never wider). On other stairs it is constant.
            double RailOffset(double s)
            {
                for (int i = 0; i < n; i++)
                {
                    var st = steps[i];
                    if (s > st.S1 && i < n - 1) continue;
                    double e0 = left ? st.B : st.A;
                    double e1 = i < n - 1 ? (left ? steps[i + 1].B : steps[i + 1].A) : e0;
                    double f = st.Depth > 1e-12 ? Math.Min(1, Math.Max(0, (s - st.S0) / st.Depth)) : 0;
                    return e0 + (e1 - e0) * f + inward * inset;
                }
                return (left ? steps[n - 1].B : steps[n - 1].A) + inward * inset;
            }

            Point3d Pt(double s, double z)
            {
                var p = StairGeometry.BandPoint(layout, s, RailOffset(s), 0);
                return new Point3d(p.X, p.Y, z);
            }

            // Rail stations: tread nosings plus corner bisectors, where the side lines
            // are mitered, so pipes bend around corners instead of cutting across them.
            var railStations = new List<double>();
            foreach (var st in steps) railStations.Add(st.S0);
            railStations.Add(sEnd);
            foreach (var z in layout.Corners)
                foreach (var bend in z.BendT)
                    if (bend > steps[0].S0 && bend < sEnd) railStations.Add(bend);
            railStations.Sort();

            // Continuous rail line at fraction f of the rail height above each nosing:
            // piecewise linear from (first nosing, z1 + f*h) to (last nosing, zN + f*h).
            List<Point3d> RailLine(double f)
            {
                var pts = new List<Point3d>();
                foreach (var s in railStations)
                    pts.Add(Pt(s, RailZ(s, f)));
                return pts;
            }

            List<Point3d> RailLineRaised(double f, double dz)
            {
                var pts = RailLine(f);
                for (int i = 0; i < pts.Count; i++) pts[i] = new Point3d(pts[i].X, pts[i].Y, pts[i].Z + dz);
                return pts;
            }

            // Height of the continuous rail line at arc length s.
            double RailZ(double s, double f)
            {
                var st = steps[n - 1];
                for (int i = 0; i < n; i++)
                    if (s <= steps[i].S1) { st = steps[i]; break; }
                double t = st.Depth > tol ? Math.Min(1, Math.Max(0, (s - st.S0) / st.Depth)) : 0;
                double zEnd = st.Number < n ? st.TopZ + layout.RiserHeight : st.TopZ;
                return st.TopZ + h * f + t * (zEnd - st.TopZ);
            }

            // Stepped line: follows the treads (used for the glass bottom shoe),
            // bending around hard corners at the local tread height.
            List<Point3d> SteppedLine(double f)
            {
                var stations = new List<double>();
                foreach (var st in steps)
                {
                    stations.Add(st.S0);
                    stations.Add(st.S1);
                }
                foreach (var s in railStations)
                    if (!stations.Contains(s)) stations.Add(s);
                stations.Sort();
                var pts = new List<Point3d>();
                foreach (var s in stations)
                {
                    var st = steps[n - 1];
                    for (int i = 0; i < n; i++)
                        if (s <= steps[i].S1) { st = steps[i]; break; }
                    pts.Add(Pt(s, st.TopZ + h * f));
                }
                return pts;
            }

            void AddPipe(List<Point3d> pts, double radius)
            {
                if (pts == null || pts.Count < 2 || radius < tol) return;
                var c = new PolylineCurve(pts.ToArray());
                var pipes = Brep.CreatePipe(c, radius, false, PipeCapMode.Round, true, tol, Rhino.RhinoMath.ToRadians(2));
                if (pipes != null) output.AddRange(pipes);
            }

            void AddCylinder(Point3d basePt, double topZ, double radius)
            {
                var axis = new LineCurve(basePt, new Point3d(basePt.X, basePt.Y, topZ));
                var cyl = Brep.CreatePipe(axis, radius, false, PipeCapMode.Round, true, tol, Rhino.RhinoMath.ToRadians(2));
                if (cyl != null) output.AddRange(cyl);
            }

            // Vertical posts standing on the treads, reaching the sloped handrail.
            // Spacing is measured along the rail in plan: around a corner fan the
            // outer rail runs much longer than the walking line, the inner one shorter.
            void AddPosts(double radius)
            {
                double spacing = o.PostSpacing > 0 ? o.PostSpacing : double.MaxValue;
                double r = radius + tol * 10; // keep the post fully on the tread
                var last = steps[n - 1];
                double se = last.S1 - r;
                double lastPost = double.NaN;
                foreach (var st in steps)
                {
                    double here = RailLength(st.S0);
                    if (double.IsNaN(lastPost) || here - lastPost >= spacing - tol)
                    {
                        // posts closer than a few diameters to the end post are left out
                        if (!double.IsNaN(lastPost) && RailLength(se) - here < radius * 6) break;
                        double sp = st.S0 + r;
                        AddCylinder(Pt(sp, st.TopZ), RailZ(sp, 1.0), radius);
                        lastPost = here;
                    }
                }
                AddCylinder(Pt(se, last.TopZ), RailZ(se, 1.0), radius);
            }

            // Plan length of the rail from its start to walk station s.
            double RailLength(double s)
            {
                double len = 0;
                var prev = Pt(steps[0].S0, 0);
                foreach (var rs in railStations)
                {
                    if (rs <= steps[0].S0) continue;
                    double upTo = Math.Min(rs, s);
                    if (upTo <= steps[0].S0) break;
                    var p = Pt(upTo, 0);
                    len += prev.DistanceTo(p);
                    prev = p;
                    if (rs >= s) break;
                }
                return len;
            }

            // A solid band (glass pane or concrete wall) from the treads up to the
            // sloped rail line, or drop below it. Tread-by-tread pieces tile without gaps; each piece is
            // joined directly from planar faces (no booleans, which left mistrimmed edges).
            void AddPanel(double halfThickness, double drop = 0)
            {
                foreach (var st in steps)
                {
                    double topA = RailZ(st.S0, 1.0) - drop;
                    double topB = RailZ(st.S1, 1.0) - drop;
                    double zBot = st.TopZ;
                    for (int k = 0; k < st.Samples.Count - 1; k++)
                    {
                        double sa = st.Samples[k], sb = st.Samples[k + 1];
                        double ta = topA + (topB - topA) * (sa - st.S0) / Math.Max(st.Depth, tol);
                        double tb = topA + (topB - topA) * (sb - st.S0) / Math.Max(st.Depth, tol);

                        double oa = RailOffset(sa), ob = RailOffset(sb);
                        var plan = StairGeometry.CleanPolygon(new List<Point3d>
                        {
                            layout.SidePoint(sa, oa - halfThickness, zBot), layout.SidePoint(sb, ob - halfThickness, zBot),
                            layout.SidePoint(sb, ob + halfThickness, zBot), layout.SidePoint(sa, oa + halfThickness, zBot),
                        }, tol);
                        // around a corner pivot the inner rail's facets shrink to nothing
                        if (plan.Count < 3 || !IsConvex(plan)) continue;

                        // top plane through the rail line, level across the panel thickness
                        var pa = Pt(sa, ta);
                        var pb = Pt(sb, tb);
                        var along = pb - pa;
                        var nrm = Vector3d.CrossProduct(along, new Vector3d(-along.Y, along.X, 0));
                        if (!nrm.Unitize() || Math.Abs(nrm.Z) < 1e-9) continue;
                        Point3d Top(Point3d p) => new Point3d(p.X, p.Y,
                            pa.Z - (nrm.X * (p.X - pa.X) + nrm.Y * (p.Y - pa.Y)) / nrm.Z);

                        var faces = new List<List<Point3d>> { plan, plan.Select(Top).ToList() };
                        for (int i = 0; i < plan.Count; i++)
                        {
                            var p0 = plan[i];
                            var p1 = plan[(i + 1) % plan.Count];
                            faces.Add(new List<Point3d> { p0, p1, Top(p1), Top(p0) });
                        }
                        var piece = StairGeometry.JoinPlanarFaces(faces, tol);
                        if (piece != null) output.Add(piece);
                    }
                }
            }

            // Walk station at plan length len along the rail (the inverse of RailLength).
            double StationAt(double len)
            {
                double lo = steps[0].S0, hi = sEnd;
                for (int i = 0; i < 60; i++)
                {
                    double mid = (lo + hi) / 2;
                    if (RailLength(mid) < len) lo = mid; else hi = mid;
                }
                return (lo + hi) / 2;
            }

            // Plumb bars from a concrete wall drop below the rail line up to rise above it (the
            // handrail's centre), spread evenly along the rail in plan, never more than spacing apart. The end
            // bars stand inset from the rail's ends; each foot sinks one radius into the sloping wall.
            void AddBalusters(double drop, double rise, double radius, double spacing, double inset)
            {
                double total = RailLength(sEnd);
                double usable = total - 2 * inset;
                var at = new List<double>();
                if (usable <= tol) at.Add(total / 2);
                else
                {
                    int count = Math.Max(1, (int)Math.Ceiling(usable / Math.Max(spacing, tol) - 1e-9));
                    for (int i = 0; i <= count; i++) at.Add(inset + usable * i / count);
                }
                foreach (double len in at)
                {
                    double s = StationAt(len);
                    double zTop = RailZ(s, 1.0);
                    var foot = Pt(s, zTop - drop - radius);
                    var cyl = new Cylinder(new Circle(new Plane(foot, Vector3d.ZAxis), radius), drop + radius + rise);
                    var bar = cyl.ToBrep(true, true);
                    if (bar != null) output.Add(bar);
                }
            }

            bool IsConvex(List<Point3d> poly)
            {
                int sign = 0;
                for (int i = 0; i < poly.Count; i++)
                {
                    var p0 = poly[i];
                    var p1 = poly[(i + 1) % poly.Count];
                    var p2 = poly[(i + 2) % poly.Count];
                    double c = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
                    if (Math.Abs(c) < 1e-12) continue;
                    int sc = Math.Sign(c);
                    if (sign == 0) sign = sc;
                    else if (sc != sign) return false;
                }
                return sign != 0;
            }

            double tubeR = Math.Max(o.MetalTubeDiameter, tol) / 2;
            double barR = Math.Max(o.MetalBarDiameter, tol) / 2;
            double postR = Math.Max(o.PostDiameter, tol) / 2;

            switch (o.RailType)
            {
                case RailType.Glass:
                    AddPanel(Math.Max(o.GlassThickness, tol) / 2);
                    AddPipe(RailLine(1.0), tubeR);          // continuous top frame tube
                    AddPipe(SteppedLine(0.0), tubeR * 0.8); // bottom shoe follows the treads
                    AddPosts(postR);
                    break;
                case RailType.Metal:
                    AddPosts(postR);
                    AddPipe(RailLine(1.0), tubeR);         // continuous handrail
                    AddPipe(RailLine(0.55), barR);         // continuous mid bar
                    AddPipe(RailLine(0.1), barR);          // continuous low bar
                    break;
                case RailType.Cement:
                {
                    // the wall stops short of the handrail; balusters fill the gap up to the handrail's centre
                    double wallHalf = Math.Max(o.RailCementThickness, tol) / 2;
                    double drop = h - CementWallTop(h, o.RailWallGap);
                    AddPanel(wallHalf, drop);                    // concrete wall with sloped top
                    AddPipe(RailLineRaised(1.0, tubeR), tubeR);  // handrail, its underside at the rail height
                    AddBalusters(drop, tubeR, Math.Max(o.BalusterDiameter, tol) / 2, o.BalusterSpacing, wallHalf);
                    break;
                }
            }
            return output;
        }

        /// <summary>Top of a concrete railing's wall: gap below the underside of a handrail at height h.</summary>
        internal static double CementWallTop(double h, double gap) => h > 2 * gap ? h - Math.Max(gap, 0) : h / 2;
    }
}
