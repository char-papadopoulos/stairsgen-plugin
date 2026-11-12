using Rhino;
using Rhino.Commands;

namespace Stairs.Commands
{
    /// <summary>
    /// Creates stairs along a flat curve: click points along the stair, or select a curve. Opens a
    /// modeless settings dialog with live preview.
    /// </summary>
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
            options.MinStepWidth = UI.Units.Default(doc, 30, 12);
            options.AbsoluteTolerance = doc.ModelAbsoluteTolerance;
            options.RailInset = UI.Units.Default(doc, 4, 1.5);
            options.GlassThickness = UI.Units.Default(doc, 1.2, 0.5);
            options.MetalTubeDiameter = UI.Units.Default(doc, 5, 2);
            options.MetalBarDiameter = UI.Units.Default(doc, 2, 0.75);
            options.PostDiameter = UI.Units.Default(doc, 4, 1.5);
            options.PostSpacing = UI.Units.Default(doc, 110, 48);
            options.RailCementThickness = UI.Units.Default(doc, 10, 4);
            options.RailWallGap = UI.Units.Default(doc, 10, 4);
            options.BalusterDiameter = UI.Units.Default(doc, 3, 1.25);
            options.BalusterSpacing = UI.Units.Default(doc, 30);
            options.NumberStyle = doc.DimStyles.Current;

            // the stair's outer, inner or mid line
            var got = UI.CurveInput.Get(doc, "stair", UI.CurveInput.Shape.Open, true, out var curve, out var curveId);
            if (got != Result.Success)
                return got;
            var bb = curve.GetBoundingBox(true);
            if (bb.Diagonal.Z > doc.ModelAbsoluteTolerance * 10)
            {
                RhinoApp.WriteLine("StairsGen: the curve must be flat (constant Z). It deviates "
                    + bb.Diagonal.Z.ToString("F3") + " in Z.");
                return Result.Failure;
            }

            var dialog = new UI.StairsDialog(doc, curve, options, curveId);
            dialog.Owner = Rhino.UI.RhinoEtoApp.MainWindow;
            dialog.Visible = true;
            return Result.Success;
        }
    }
}
