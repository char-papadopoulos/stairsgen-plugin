using Rhino;
using Rhino.Commands;

namespace Stairs.Commands
{
    /// <summary>Creates stairs along a flat curve. Opens a modeless settings dialog with live preview.</summary>
    [System.Runtime.InteropServices.Guid("1EB5AD2F-57FF-4855-9641-9015E2DDDB38")]
    public class StairsCommand : Rhino.Commands.Command
    {
        public override string EnglishName => "StairsGen";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // defaults scaled to the model units
            double cm = RhinoMath.UnitScale(UnitSystem.Centimeters, doc.ModelUnitSystem);
            var options = UI.StairsDialog.LoadSettings(doc);
            options.ComfortSum = 46 * cm;
            options.MinAverageDepth = 18 * cm;
            options.MinRiserHeight = 12 * cm;
            options.MaxRiserHeight = 20 * cm;
            options.MinStepWidth = 30 * cm;
            options.AbsoluteTolerance = doc.ModelAbsoluteTolerance;
            options.RailInset = 4 * cm;
            options.GlassThickness = 1.2 * cm;
            options.MetalTubeDiameter = 5 * cm;
            options.MetalBarDiameter = 2 * cm;
            options.PostDiameter = 4 * cm;
            options.PostSpacing = 110 * cm;
            options.RailCementThickness = 10 * cm;
            options.RailWallGap = 10 * cm;
            options.BalusterDiameter = 3 * cm;
            options.BalusterSpacing = 30 * cm;
            options.NumberStyle = doc.DimStyles.Current;

            var go = new Rhino.Input.Custom.GetObject();
            go.SetCommandPrompt("Select stair curve (outer, inner or mid line of the staircase)");
            go.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
            go.EnablePreSelect(true, true);
            go.Get();
            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            var curve = go.Object(0).Curve();
            if (curve == null)
            {
                RhinoApp.WriteLine("StairsGen: the selected object is not a curve.");
                return Result.Failure;
            }
            var bb = curve.GetBoundingBox(true);
            if (bb.Diagonal.Z > doc.ModelAbsoluteTolerance * 10)
            {
                RhinoApp.WriteLine("StairsGen: the curve must be flat (constant Z). It deviates "
                    + bb.Diagonal.Z.ToString("F3") + " in Z.");
                return Result.Failure;
            }

            var dialog = new UI.StairsDialog(doc, curve, options, UI.StairsDialog.TrackableId(go.Object(0)));
            dialog.Owner = Rhino.UI.RhinoEtoApp.MainWindow;
            dialog.Visible = true;
            return Result.Success;
        }
    }
}
