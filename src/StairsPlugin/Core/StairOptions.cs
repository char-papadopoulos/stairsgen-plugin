using System;
using Rhino.Geometry;

namespace Stairs.Core
{
    public enum CurveRole { Mid, Inner, Outer }
    public enum StairStructure { Slab, Plank }
    public enum RailSide { None, Left, Right, Both }
    public enum RailType { Glass, Metal, Cement }

    /// <summary>Which side stays straight when a pyramid stair narrows, seen walking up.</summary>
    public enum PyramidAlign { Center, Left, Right }

    /// <summary>How the regular tread depth is chosen.</summary>
    public enum TreadDepthMode
    {
        Comfort,    // riser + tread = ComfortSum
        FitToCurve, // one depth for all regular treads, so the last step ends at the curve end
        Custom,     // DepthOverride
    }

    /// <summary>A rest step (landing). Depth &lt;= 0 means "use stair width".</summary>
    public class RestStep
    {
        public int StepNumber; // 1-based tread index that becomes a landing
        public double Depth;   // <= 0 -> default (stair width)
        public RestStep() { }
        public RestStep(int number, double depth) { StepNumber = number; Depth = depth; }
    }

    /// <summary>
    /// All lengths are in the document's model units. Field defaults are given in SI (meters)
    /// and are always meant to be overwritten by the command with doc-unit values.
    /// </summary>
    public class StairOptions
    {
        public Curve Curve;                    // flat input curve
        public CurveRole Role = CurveRole.Mid;  // what the input curve represents
        public bool Flip = false;              // reverse walking direction

        public double TotalHeight = 3.0;       // total rise
        public double DesiredStepHeight = 0.17; // desired riser height
        public double Width = 1.0;             // stair width (across treads)

        public bool Pyramid = false;           // each step narrower than the one below
        public double PyramidStep = 0.05;      // how much narrower each step is
        public PyramidAlign PyramidAlign = PyramidAlign.Center;
        public double MinStepWidth = 0.30;     // pyramid steps never get narrower than this

        public TreadDepthMode DepthMode = TreadDepthMode.Comfort;
        public double DepthOverride = 0.28;   // used in Custom depth mode
        public double ComfortSum = 0.46;       // riser + tread target (46 cm)
        public double MinAverageDepth = 0.18;  // treads are widened until area / nosing length reaches this

        // autoconfigure
        public double MinRiserHeight = 0.12;   // riser range the step count is chosen from
        public double MaxRiserHeight = 0.20;
        public int RestInterval = 15;          // a rest step after every this many steps

        public StairStructure Structure = StairStructure.Slab;
        public double SlabThickness = 0.15;    // slab (soffit) thickness
        public double PlankThickness = 0.04;   // plank tread thickness

        public bool ShowNumbers = true;
        public double NumberHeight = 0;        // 0 -> automatic
        public Rhino.DocObjects.DimensionStyle NumberStyle = null;

        public System.Collections.Generic.List<RestStep> RestSteps = new System.Collections.Generic.List<RestStep>();
        public bool SquareLandings = true;     // a rest step that reaches a sharp corner fills it, square to both flights

        public RailSide Railing = RailSide.None;
        public RailType RailType = RailType.Glass;
        public double RailHeight = 0.90;
        public double RailInset = 0.04;        // distance of railing from stair edge
        public double GlassThickness = 0.012;
        public double MetalTubeDiameter = 0.05; // handrail tube
        public double MetalBarDiameter = 0.02;  // intermediate bars
        public double PostDiameter = 0.04;
        public double PostSpacing = 1.10;      // max distance between posts (0 -> every step)
        public double RailCementThickness = 0.10;
        public double RailWallGap = 0.10;      // concrete: open height between the wall and the handrail
        public double BalusterDiameter = 0.03; // concrete: the bars in that gap
        public double BalusterSpacing = 0.30;  // concrete: max distance between bars

        public double AbsoluteTolerance = 0.01;


        public bool RestStepAt(int number, out RestStep rs)
        {
            rs = null;
            foreach (var r in RestSteps)
                if (r.StepNumber == number) { rs = r; return true; }
            return false;
        }
    }
}
