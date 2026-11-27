using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino.Geometry;
using Stairs.Core;

namespace Stairs.GH
{
    /// <summary>Shared plumbing: the StairsGen tab, icons, and reading a stair input.</summary>
    public abstract class StairComponentBase : GH_Component
    {
        readonly string _icon;

        protected StairComponentBase(string name, string nickname, string description, string subcategory, string icon)
            : base(name, nickname, description, "StairsGen", subcategory)
        {
            _icon = icon;
        }

        protected override System.Drawing.Bitmap Icon => Icons.Load(_icon);

        protected static void AddStairInput(GH_InputParamManager p, string description = "The stair")
            => p.AddParameter(new StairParam(), "Stair", "S", description, GH_ParamAccess.item);

        protected static void AddStairOutput(GH_OutputParamManager p, string description = "The stair")
            => p.AddParameter(new StairParam(), "Stair", "S", description, GH_ParamAccess.item);

        /// <summary>The stair on input 0, or null (with an error) if there is none.</summary>
        protected StairData GetStair(IGH_DataAccess da)
        {
            StairGoo goo = null;
            if (!da.GetData(0, ref goo) || goo?.Value == null) return null;
            return goo.Value;
        }

        protected static Param_Integer Choice(GH_InputParamManager p, string name, string nick, string description, int value, params string[] names)
        {
            int i = p.AddIntegerParameter(name, nick, description, GH_ParamAccess.item, value);
            var param = (Param_Integer)p[i];
            for (int k = 0; k < names.Length; k++) param.AddNamedValue(names[k], k);
            return param;
        }

        protected static void Optional(GH_InputParamManager p, int index) => p[index].Optional = true;
    }

    /// <summary>A stair along a flat curve: the steps, from height, riser and tread.</summary>
    public class StairComponent : StairComponentBase
    {
        public StairComponent() : base("Stair", "Stair",
            "A stair along a flat curve. Lengths left empty use round defaults in the model's units. "
            + "Corners get landings and extra curve becomes a top landing, as in the StairsGen command; "
            + "change these with Stair Landings.",
            "Stair", "stairs.png") { }

        public override Guid ComponentGuid => new Guid("59CC89C4-4F5D-4048-92E5-CC85FF43C52E");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter("Curve", "C", "Flat curve the stair follows", GH_ParamAccess.item);
            p.AddNumberParameter("Height", "H", "Total height (default 300 cm or 120\")", GH_ParamAccess.item); Optional(p, 1);
            p.AddNumberParameter("Riser", "R", "Riser height wanted; the actual one divides the height evenly (default 17 cm or 7\")", GH_ParamAccess.item); Optional(p, 2);
            p.AddNumberParameter("Tread", "T", "Tread depth. Leave empty to fit the treads to the curve.", GH_ParamAccess.item); Optional(p, 3);
            p.AddNumberParameter("Width", "W", "Stair width (default 100 cm or 36\")", GH_ParamAccess.item); Optional(p, 4);
            Choice(p, "Role", "Ro", "What the curve is: 0 middle, 1 inner edge, 2 outer edge", 0, "Middle", "Inner edge", "Outer edge");
            p.AddBooleanParameter("Flip", "F", "Climb from the other end of the curve", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            AddStairOutput(p);
            p.AddCurveParameter("Treads", "Tr", "Outline of each tread, at its height", GH_ParamAccess.list);
            p.AddIntegerParameter("Steps", "N", "Number of steps", GH_ParamAccess.item);
            p.AddNumberParameter("Riser", "R", "Actual riser height", GH_ParamAccess.item);
            p.AddNumberParameter("Tread", "T", "Tread depth used (per flight when landings split it)", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Curve curve = null;
            if (!da.GetData(0, ref curve) || curve == null) return;
            var box = curve.GetBoundingBox(true);
            if (box.Diagonal.Z > Defaults.Tolerance * 10)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The curve must be flat (constant Z).");
                return;
            }

            var o = Defaults.NewOptions();
            o.Curve = curve.DuplicateCurve();
            double height = Defaults.Length(300, 120), riser = Defaults.Length(17, 7), width = Defaults.Length(100, 36), tread = 0;
            da.GetData(1, ref height);
            da.GetData(2, ref riser);
            bool typed = da.GetData(3, ref tread);
            da.GetData(4, ref width);
            int role = 0; da.GetData(5, ref role);
            bool flip = false; da.GetData(6, ref flip);
            if (height <= 0 || riser <= 0 || width <= 0 || (typed && tread <= 0))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Height, riser, tread and width must be positive.");
                return;
            }

            o.TotalHeight = height;
            o.DesiredStepHeight = riser;
            o.Width = width;
            o.Role = (CurveRole)Math.Max(0, Math.Min(2, role));
            o.Flip = flip;
            o.DepthMode = typed ? TreadDepthMode.Custom : TreadDepthMode.FitToCurve;
            if (typed) o.DepthOverride = tread;

            var stair = new StairData(o);
            Defaults.Warn(this, stair.Layout.Warnings);
            Outputs.Stair(da, stair);
        }
    }

    /// <summary>Landings: at corners or as winders, rest steps, and what happens to extra curve.</summary>
    public class LandingsComponent : StairComponentBase
    {
        public LandingsComponent() : base("Stair Landings", "Landings",
            "Landings at the corners or winders, rest steps partway along a flight, the curve left past the last step, and rounded sharp corners.",
            "Stair", "reststep.png") { }

        public override Guid ComponentGuid => new Guid("E382C96B-9817-4D7B-B3B5-26F5A2843FBB");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            AddStairInput(p);
            p.AddBooleanParameter("Corners", "L", "Every corner gets a landing (false: every corner gets winders)", GH_ParamAccess.item, true);
            p.AddIntegerParameter("Winders", "Wi", "Corners (by index, see the Corners output) that keep winders instead of a landing", GH_ParamAccess.list); Optional(p, 2);
            p.AddIntegerParameter("Rest steps", "RS", "Steps that become rest landings, numbered from 1", GH_ParamAccess.list); Optional(p, 3);
            p.AddNumberParameter("Rest depth", "RD", "Depth of the rest landings (default: the stair width)", GH_ParamAccess.item); Optional(p, 4);
            Choice(p, "Extra curve", "E", "Curve past the last step: 0 top landing, 1 left unused", 0, "Top landing", "Leave unused");
            p.AddBooleanParameter("Rounded", "Rd", "Corners sharper than 60° go round the inner corner instead of ending in a point", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            AddStairOutput(p);
            p.AddPointParameter("Corners", "Co", "Where the stair turns, in order; their index is the one Winders uses", GH_ParamAccess.list);
            p.AddBooleanParameter("Landing", "La", "Whether each corner has a landing", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var input = GetStair(da);
            if (input == null) return;
            var o = input.CopyOptions();

            bool corners = true; da.GetData(1, ref corners);
            var winders = new List<int>(); da.GetDataList(2, winders);
            var rests = new List<int>(); da.GetDataList(3, rests);
            double restDepth = 0; da.GetData(4, ref restDepth);
            int extra = 0; da.GetData(5, ref extra);
            bool rounded = false; da.GetData(6, ref rounded);

            o.LandingsAtCorners = corners;
            o.SquareLandings = corners; // rest steps at a corner fill it too, as in the dialog
            o.WinderTurns = new HashSet<int>(winders);
            o.RestSteps = rests.Where(n => n >= 1).Distinct().OrderBy(n => n).Select(n => new RestStep(n, Math.Max(0, restDepth))).ToList();
            o.ExtraCurve = extra == 1 ? ExtraCurve.Unused : ExtraCurve.TopLanding;
            o.RoundSharpCorners = rounded;

            var stair = new StairData(o);
            Defaults.Warn(this, stair.Layout.Warnings);
            da.SetData(0, new StairGoo(stair));
            var turns = stair.Layout.Turns.Where(t => t.IsTurn).ToList();
            da.SetDataList(1, turns.Select(t => t.Center));
            da.SetDataList(2, turns.Select(t => t.Landing));
        }
    }

    /// <summary>Pyramid width: each step narrower than the one below.</summary>
    public class PyramidComponent : StairComponentBase
    {
        public PyramidComponent() : base("Stair Pyramid", "Pyramid",
            "Each step narrower than the one below, keeping the middle, the left or the right side straight.",
            "Stair", "stairs.png") { }

        public override Guid ComponentGuid => new Guid("00B4AB4F-B65D-4969-832B-BD5267176F22");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            AddStairInput(p);
            p.AddNumberParameter("Narrowing", "N", "How much narrower each step is (default 5 cm or 2\"; 0 for none)", GH_ParamAccess.item); Optional(p, 1);
            Choice(p, "Straight side", "A", "Which side stays straight, walking up: 0 middle, 1 left, 2 right", 0, "Middle", "Left", "Right");
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) => AddStairOutput(p);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var input = GetStair(da);
            if (input == null) return;
            var o = input.CopyOptions();
            double step = Defaults.Length(5, 2); da.GetData(1, ref step);
            int align = 0; da.GetData(2, ref align);
            o.Pyramid = step > 0;
            o.PyramidStep = Math.Max(0, step);
            o.PyramidAlign = (PyramidAlign)Math.Max(0, Math.Min(2, align));
            var stair = new StairData(o);
            Defaults.Warn(this, stair.Layout.Warnings);
            da.SetData(0, new StairGoo(stair));
        }
    }

    /// <summary>Autoconfigure: the steps, rests and tread depth from the height alone.</summary>
    public class AutoconfigureComponent : StairComponentBase
    {
        public AutoconfigureComponent() : base("Stair Autoconfigure", "Auto",
            "Chooses the number of steps, the rest steps and the tread depth from the height alone, "
            + "by the comfort rule (riser + tread close to 46 cm), with the last step at the curve end.",
            "Stair", "auto.png") { }

        public override Guid ComponentGuid => new Guid("E910BD8A-2ADA-4489-B7F2-E2C8D4341223");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            AddStairInput(p);
            p.AddIntegerParameter("Rest every", "Re", "A rest step after every this many steps", GH_ParamAccess.item, 15);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            AddStairOutput(p);
            p.AddIntegerParameter("Steps", "N", "Number of steps chosen", GH_ParamAccess.item);
            p.AddIntegerParameter("Rest steps", "RS", "The rest steps chosen", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var input = GetStair(da);
            if (input == null) return;
            var o = input.CopyOptions();
            int every = 15; da.GetData(1, ref every);
            o.RestInterval = Math.Max(1, every);
            int n = StairLayout.AutoConfigure(o);
            var stair = new StairData(o);
            Defaults.Warn(this, stair.Layout.Warnings);
            da.SetData(0, new StairGoo(stair));
            da.SetData(1, n);
            da.SetDataList(2, o.RestSteps.Select(r => r.StepNumber));
        }
    }

    /// <summary>The structure: a slab or floating planks.</summary>
    public class StructureComponent : StairComponentBase
    {
        public StructureComponent() : base("Stair Structure", "Structure",
            "The stair's body: a continuous slab with a sloped underside, or floating planks.",
            "Parts", "slab.png") { }

        public override Guid ComponentGuid => new Guid("26301042-3883-429D-A645-ACBDDC707F6B");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            AddStairInput(p);
            Choice(p, "Type", "Ty", "0 slab, 1 planks", 0, "Slab", "Planks");
            p.AddNumberParameter("Thickness", "Th", "Slab depth (default 15 cm or 6\") or plank thickness (default 4 cm or 1.5\")", GH_ParamAccess.item); Optional(p, 2);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
            => p.AddBrepParameter("Structure", "B", "The slab, or one plank per step", GH_ParamAccess.list);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var input = GetStair(da);
            if (input == null) return;
            var o = input.CopyOptions();
            int type = 0; da.GetData(1, ref type);
            o.Structure = type == 1 ? StairStructure.Plank : StairStructure.Slab;
            double t = o.Structure == StairStructure.Plank ? Defaults.Length(4, 1.5) : Defaults.Length(15, 6);
            da.GetData(2, ref t);
            if (t <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Thickness must be positive."); return; }
            if (o.Structure == StairStructure.Plank) o.PlankThickness = t; else o.SlabThickness = t;
            o.Railing = RailSide.None;
            o.ShowNumbers = false;
            var result = StairGeometry.Build(o);
            Defaults.Warn(this, result.Warnings);
            da.SetDataList(0, result.Steps);
        }
    }

    /// <summary>Railings on either side or both.</summary>
    public class RailingComponent : StairComponentBase
    {
        public RailingComponent() : base("Stair Railing", "Railing",
            "Railings along the stair: glass in a metal frame, metal posts and bars, or a concrete wall with metal balusters.",
            "Parts", "railing.png") { }

        public override Guid ComponentGuid => new Guid("9975E6D9-4CFB-4F14-8603-BF274B21B811");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            AddStairInput(p);
            Choice(p, "Side", "Si", "0 both sides, 1 left, 2 right (walking up)", 0, "Both sides", "Left", "Right");
            Choice(p, "Type", "Ty", "0 glass, 1 metal, 2 concrete", 1, "Glass", "Metal", "Concrete");
            p.AddNumberParameter("Height", "H", "Railing height (default 90 cm or 36\")", GH_ParamAccess.item); Optional(p, 3);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddBrepParameter("Left", "L", "The left railing's parts", GH_ParamAccess.list);
            p.AddBrepParameter("Right", "R", "The right railing's parts", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var input = GetStair(da);
            if (input == null) return;
            var layout = input.Layout;
            if (layout.Steps.Count == 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The stair has no steps."); return; }
            var o = input.CopyOptions(); // railing settings do not change the layout, so it is reused
            int side = 0; da.GetData(1, ref side);
            int type = 1; da.GetData(2, ref type);
            double h = o.RailHeight; da.GetData(3, ref h);
            if (h <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Height must be positive."); return; }
            o.RailType = (Stairs.Core.RailType)Math.Max(0, Math.Min(2, type));
            o.RailHeight = h;
            double tol = o.AbsoluteTolerance;
            if (side == 0 || side == 1) da.SetDataList(0, RailBuilder.Build(o, layout, layout.A, layout.B, true, tol));
            if (side == 0 || side == 2) da.SetDataList(1, RailBuilder.Build(o, layout, layout.A, layout.B, false, tol));
        }
    }

    /// <summary>Step numbers, laid flat on each tread.</summary>
    public class NumbersComponent : StairComponentBase
    {
        public NumbersComponent() : base("Step Numbers", "Numbers",
            "Each step's number, flat on its tread, drawn in the preview. The planes and sizes are there for Text Tag 3D or your own text.",
            "Parts", "numbers.png") { }

        public override Guid ComponentGuid => new Guid("67D1B025-5AD1-4E64-89B8-9D761F8E6CE1");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        readonly List<Rhino.Display.Text3d> _preview = new List<Rhino.Display.Text3d>();
        BoundingBox _previewBox = BoundingBox.Empty;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            AddStairInput(p);
            p.AddNumberParameter("Size", "Sz", "Text height (default: from the tread depth)", GH_ParamAccess.item); Optional(p, 1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("Text", "T", "Each step's number", GH_ParamAccess.list);
            p.AddPlaneParameter("Plane", "P", "Where each number lies, centred, reading up the stair (not previewed: a plane draws as a large grid)", GH_ParamAccess.list);
            p.AddNumberParameter("Size", "Sz", "Text height", GH_ParamAccess.list);
            ((IGH_PreviewObject)p[1]).Hidden = true;
        }

        protected override void BeforeSolveInstance()
        {
            _preview.Clear();
            _previewBox = BoundingBox.Empty;
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var input = GetStair(da);
            if (input == null) return;
            var o = input.CopyOptions();
            double size = 0; da.GetData(1, ref size);
            o.NumberHeight = Math.Max(0, size);
            // the height MakeNumber gives the text (reading it back from the text gives the style's)
            double height = o.NumberHeight > 0 ? o.NumberHeight : Math.Min(o.Width, input.Layout.TreadDepth) * 0.28;
            var texts = new List<string>(); var planes = new List<Plane>(); var sizes = new List<double>();
            foreach (var step in input.Layout.Steps)
            {
                var te = StairGeometry.MakeNumber(input.Layout, o, step, step.A, step.B);
                string text = step.Number.ToString();
                texts.Add(text);
                planes.Add(te.Plane);
                sizes.Add(height);
                _preview.Add(new Rhino.Display.Text3d(text, te.Plane, height)
                {
                    HorizontalAlignment = Rhino.DocObjects.TextHorizontalAlignment.Center,
                    VerticalAlignment = Rhino.DocObjects.TextVerticalAlignment.Middle,
                });
                _previewBox.Union(te.Plane.Origin);
            }
            if (_previewBox.IsValid) _previewBox.Inflate(height);
            da.SetDataList(0, texts);
            da.SetDataList(1, planes);
            da.SetDataList(2, sizes);
        }

        // only the numbers: the hidden planes would otherwise count at their large preview size
        public override BoundingBox ClippingBox => _previewBox;

        // files saved before the plane preview was turned off open with it off too
        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            bool ok = base.Read(reader);
            if (Params.Output.Count > 1) ((IGH_PreviewObject)Params.Output[1]).Hidden = true;
            return ok;
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);
            if (Hidden || Locked) return;
            var colour = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            foreach (var t in _preview) args.Display.Draw3dText(t, colour);
        }
    }

    /// <summary>Everything about a stair's layout, step by step.</summary>
    public class DeconstructStairComponent : StairComponentBase
    {
        public DeconstructStairComponent() : base("Deconstruct Stair", "DeStair",
            "The stair's layout: each step's outline, height, depth and whether it is a landing, the riser and treads, and its warnings.",
            "Stair", "info.png") { }

        public override Guid ComponentGuid => new Guid("881A5152-0CCB-452D-A388-CF991DC5AE2C");
        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override void RegisterInputParams(GH_InputParamManager p) => AddStairInput(p);

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter("Treads", "Tr", "Outline of each step, at its height", GH_ParamAccess.list);
            p.AddNumberParameter("Heights", "Z", "Height of each step's top", GH_ParamAccess.list);
            p.AddNumberParameter("Depths", "D", "Depth of each step along the walking line", GH_ParamAccess.list);
            p.AddBooleanParameter("Landings", "La", "Whether each step is a landing", GH_ParamAccess.list);
            p.AddNumberParameter("Riser", "R", "Actual riser height", GH_ParamAccess.item);
            p.AddNumberParameter("Tread", "T", "Tread depth used (per flight when landings split it)", GH_ParamAccess.list);
            p.AddNumberParameter("Top landing", "TL", "Depth of the top landing (0 for none)", GH_ParamAccess.item);
            p.AddNumberParameter("Shortfall", "Sh", "More curve the stair needs, when steps were left out", GH_ParamAccess.item);
            p.AddTextParameter("Warnings", "W", "The layout's warnings", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var input = GetStair(da);
            if (input == null) return;
            var l = input.Layout;
            da.SetDataList(0, Outputs.Treads(input));
            da.SetDataList(1, l.Steps.Select(s => s.TopZ));
            da.SetDataList(2, l.Steps.Select(s => s.Depth));
            da.SetDataList(3, l.Steps.Select(s => s.IsRest));
            da.SetData(4, l.RiserHeight);
            da.SetDataList(5, Outputs.TreadDepths(l));
            da.SetData(6, l.TopLanding);
            da.SetData(7, l.Shortfall);
            da.SetDataList(8, l.Warnings);
        }
    }

    /// <summary>Outputs shared by more than one component.</summary>
    internal static class Outputs
    {
        public static void Stair(IGH_DataAccess da, StairData stair)
        {
            da.SetData(0, new StairGoo(stair));
            da.SetDataList(1, Treads(stair));
            da.SetData(2, stair.Layout.Steps.Count);
            da.SetData(3, stair.Layout.RiserHeight);
            da.SetDataList(4, TreadDepths(stair.Layout));
        }

        public static List<double> TreadDepths(StairLayout l)
            => l.FlightDepths.Count > 0 ? new List<double>(l.FlightDepths) : new List<double> { l.TreadDepth };

        /// <summary>Each step's plan outline, closed, at the height of its top.</summary>
        public static List<Curve> Treads(StairData stair)
        {
            var list = new List<Curve>();
            var l = stair.Layout;
            double tol = stair.Options.AbsoluteTolerance;
            foreach (var step in l.Steps)
            {
                var pts = StairGeometry.TreadPolygon(l, step, step.A, step.B, tol);
                if (pts.Count < 3) continue;
                var poly = new Polyline(pts);
                if (!poly.IsClosed) poly.Add(poly[0]);
                var curve = new PolylineCurve(poly);
                curve.Translate(0, 0, step.TopZ - pts[0].Z);
                list.Add(curve);
            }
            return list;
        }
    }
}
