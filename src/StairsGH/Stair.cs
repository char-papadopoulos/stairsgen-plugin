using System;
using System.Collections.Generic;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Stairs.Core;

namespace Stairs.GH
{
    /// <summary>
    /// A stair passed between the components: its settings and the layout computed from them.
    /// Never changed once made; components that change a setting make a new one.
    /// </summary>
    public class StairData
    {
        public StairOptions Options { get; }
        public StairLayout Layout { get; }

        public StairData(StairOptions options)
        {
            Options = options;
            Layout = StairLayout.Compute(options);
        }

        /// <summary>A copy of the settings to change, for a new stair.</summary>
        public StairOptions CopyOptions() => Defaults.Copy(Options);

        public override string ToString()
        {
            var l = Layout;
            return l == null ? "Stair" : string.Format("Stair: {0} steps, riser {1:0.###}, tread {2:0.###}", l.Steps.Count, l.RiserHeight, l.TreadDepth);
        }
    }

    public class StairGoo : GH_Goo<StairData>
    {
        public StairGoo() { }
        public StairGoo(StairData value) : base(value) { }

        public override bool IsValid => Value?.Options?.Curve != null;
        public override string TypeName => "Stair";
        public override string TypeDescription => "A StairsGen stair: its settings and layout.";
        public override IGH_Goo Duplicate() => new StairGoo(Value); // the stair itself never changes
        public override string ToString() => Value?.ToString() ?? "Null stair";
    }

    /// <summary>The Stair parameter, for wiring stairs between components.</summary>
    public class StairParam : GH_Param<StairGoo>
    {
        public StairParam() : base("Stair", "S", "A StairsGen stair", "StairsGen", "Params", GH_ParamAccess.item) { }
        public override Guid ComponentGuid => new Guid("418D6186-56E4-4CEE-B5A8-F0DFB9961F7A");
        protected override System.Drawing.Bitmap Icon => Icons.Load("stairs.png");
        public override GH_Exposure Exposure => GH_Exposure.primary;
    }

    /// <summary>Unit-aware defaults and helpers shared by the components.</summary>
    internal static class Defaults
    {
        static RhinoDoc Doc => RhinoDoc.ActiveDoc;

        public static UnitSystem Units => Doc?.ModelUnitSystem ?? UnitSystem.Meters;
        public static double Tolerance => Doc?.ModelAbsoluteTolerance ?? 0.001;

        static bool Imperial
        {
            get
            {
                switch (Units)
                {
                    case UnitSystem.Inches: case UnitSystem.Feet: case UnitSystem.Yards:
                    case UnitSystem.Miles: case UnitSystem.Mils: case UnitSystem.Microinches:
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>A round default in the model's units: cm in a metric model, inches in an imperial one.</summary>
        public static double Length(double cm, double inches)
            => Imperial ? inches * RhinoMath.UnitScale(UnitSystem.Inches, Units) : cm * RhinoMath.UnitScale(UnitSystem.Centimeters, Units);

        /// <summary>Centimetres in the model's units.</summary>
        public static double Cm(double cm) => cm * RhinoMath.UnitScale(UnitSystem.Centimeters, Units);

        /// <summary>Settings for a new stair, as the StairsGen command sets them up.</summary>
        public static StairOptions NewOptions()
        {
            var o = new StairOptions
            {
                ComfortSum = Cm(46),
                MinAverageDepth = Cm(18),
                MinRiserHeight = Cm(12),
                MaxRiserHeight = Cm(20),
                MinStepWidth = Length(30, 12),
                AbsoluteTolerance = Tolerance,
                RailInset = Length(4, 1.5),
                GlassThickness = Length(1.2, 0.5),
                MetalTubeDiameter = Length(5, 2),
                MetalBarDiameter = Length(2, 0.75),
                PostDiameter = Length(4, 1.5),
                PostSpacing = Length(110, 48),
                RailCementThickness = Length(10, 4),
                RailWallGap = Length(10, 4),
                BalusterDiameter = Length(3, 1.25),
                BalusterSpacing = Length(30, 12),
                RailHeight = Length(90, 36),
                SlabThickness = Length(15, 6),
                PlankThickness = Length(4, 1.5),
                PyramidStep = Length(5, 2),
                // as the dialog starts: landings at corners, extra curve as a top landing
                LandingsAtCorners = true,
                SquareLandings = true,
                ExtraCurve = ExtraCurve.TopLanding,
                ShowNumbers = false,
                Railing = RailSide.None,
            };
            return o;
        }

        /// <summary>A copy of the settings that shares nothing that can change.</summary>
        public static StairOptions Copy(StairOptions o)
        {
            var c = new StairOptions();
            foreach (var f in typeof(StairOptions).GetFields(BindingFlags.Public | BindingFlags.Instance))
                f.SetValue(c, f.GetValue(o));
            c.Curve = o.Curve?.DuplicateCurve();
            c.RestSteps = new List<RestStep>();
            foreach (var r in o.RestSteps) c.RestSteps.Add(new RestStep(r.StepNumber, r.Depth));
            c.WinderTurns = new HashSet<int>(o.WinderTurns);
            return c;
        }

        /// <summary>Shows the layout's warnings on the component.</summary>
        public static void Warn(GH_Component component, IEnumerable<string> warnings)
        {
            foreach (var w in warnings) component.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, w);
        }
    }
}
