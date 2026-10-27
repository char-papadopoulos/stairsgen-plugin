using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;

namespace Stairs.UI
{
    /// <summary>
    /// The curve a tool's command runs along, got the way the door and window commands get their
    /// points: click points to draw a polyline (Enter finishes it), or use the SelectCurve option
    /// to pick a curve already in the model. A curve selected before the command starts is used
    /// straight away.
    /// </summary>
    internal static class CurveInput
    {
        public enum Shape
        {
            /// <summary>An open curve.</summary>
            Open,
            /// <summary>A closed curve: a drawn polyline is closed back to its first point.</summary>
            Closed,
            /// <summary>Either: a drawn polyline stays open unless the Close option ends it.</summary>
            Either,
        }

        /// <summary>
        /// Gets the curve. `what` names it in the prompts ("wall", "roof outline"). Flat keeps every
        /// clicked point at the first one's height. `id` is the picked curve's object, for the
        /// dialog to follow, or empty for a drawn polyline.
        /// </summary>
        public static Result Get(RhinoDoc doc, string what, Shape shape, bool flat, out Curve curve, out Guid id)
        {
            curve = null;
            id = Guid.Empty;

            // a curve selected before the command started
            var selected = new List<Rhino.DocObjects.RhinoObject>();
            foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
                if (obj.Geometry is Curve) selected.Add(obj);
            if (selected.Count == 1 && Fits((Curve)selected[0].Geometry, shape))
            {
                var picked = new Rhino.DocObjects.ObjRef(selected[0]);
                curve = picked.Curve();
                id = StairsDialog.TrackableId(picked);
                return curve != null ? Result.Success : Result.Failure;
            }

            var points = new List<Point3d>();
            bool closed = shape == Shape.Closed;
            int least = shape == Shape.Closed ? 3 : 2;
            var color = System.Drawing.Color.FromArgb(47, 128, 237);
            while (true)
            {
                var gp = new Rhino.Input.Custom.GetPoint();
                gp.SetCommandPrompt(points.Count == 0
                    ? "Start of the " + what + ": click a point"
                    : "Next point of the " + what + (points.Count >= least ? " (Enter when done)" : ""));
                int selectOption = points.Count == 0 ? gp.AddOption("SelectCurve") : -1;
                int undoOption = points.Count > 0 ? gp.AddOption("Undo") : -1;
                int closeOption = shape == Shape.Either && points.Count >= 2 ? gp.AddOption("Close") : -1;
                gp.AcceptNothing(points.Count >= least);
                if (points.Count > 0)
                {
                    var last = points[points.Count - 1];
                    gp.SetBasePoint(last, true);
                    gp.DrawLineFromPoint(last, true);
                    if (flat) gp.Constrain(new Plane(new Point3d(0, 0, points[0].Z), Vector3d.ZAxis), false);
                    gp.DynamicDraw += (s, e) =>
                    {
                        if (points.Count > 1) e.Display.DrawPolyline(points, color, 2);
                        // a closed outline: show the edge back to the start
                        if (shape == Shape.Closed && points.Count >= 2) e.Display.DrawLine(new Line(e.CurrentPoint, points[0]), color, 1);
                    };
                }

                var got = gp.Get();
                if (got == GetResult.Cancel) return Result.Cancel;
                if (got == GetResult.Nothing) break;
                if (got == GetResult.Option)
                {
                    int option = gp.OptionIndex();
                    if (option == selectOption) return Select(doc, what, shape, out curve, out id);
                    if (option == undoOption) points.RemoveAt(points.Count - 1);
                    if (option == closeOption) { closed = true; break; }
                    continue;
                }
                if (got != GetResult.Point) return Result.Cancel;
                var p = gp.Point();
                if (flat && points.Count > 0) p.Z = points[0].Z;
                if (points.Count > 0 && p.DistanceTo(points[points.Count - 1]) <= doc.ModelAbsoluteTolerance) continue;
                points.Add(p);
            }

            if (points.Count < least) return Result.Cancel;
            if (closed) points.Add(points[0]);
            curve = new PolylineCurve(points);
            return Result.Success;
        }

        private static bool Fits(Curve c, Shape shape) => shape != Shape.Closed || c.IsClosed;

        /// <summary>Picks a curve already in the model, as the commands did before.</summary>
        private static Result Select(RhinoDoc doc, string what, Shape shape, out Curve curve, out Guid id)
        {
            curve = null;
            id = Guid.Empty;
            var go = new Rhino.Input.Custom.GetObject();
            go.SetCommandPrompt("Select the curve for the " + what);
            go.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
            if (shape == Shape.Closed) go.GeometryAttributeFilter = Rhino.Input.Custom.GeometryAttributeFilter.ClosedCurve;
            go.EnablePreSelect(false, true);
            go.Get();
            if (go.CommandResult() != Result.Success) return go.CommandResult();
            curve = go.Object(0).Curve();
            if (curve == null) return Result.Failure;
            id = StairsDialog.TrackableId(go.Object(0));
            doc.Objects.UnselectAll();
            return Result.Success;
        }
    }
}
