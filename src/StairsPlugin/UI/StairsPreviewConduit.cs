using System;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace Stairs.UI
{
    /// <summary>Draws the stair preview in viewports while the dialog is open.</summary>
    public class StairsPreviewConduit : Rhino.Display.DisplayConduit
    {
        private Core.StairResult _result;
        private readonly DisplayMaterial _stepMaterial = new DisplayMaterial
        {
            Diffuse = System.Drawing.Color.FromArgb(210, 214, 220),
            Transparency = 0.35,
        };
        private readonly DisplayMaterial _railMaterial = new DisplayMaterial
        {
            Diffuse = System.Drawing.Color.FromArgb(90, 130, 200),
            Transparency = 0.25,
        };
        private readonly DisplayMaterial _glassMaterial = new DisplayMaterial
        {
            Diffuse = System.Drawing.Color.FromArgb(160, 210, 255),
            Transparency = 0.55,
        };

        public void SetResult(Core.StairResult result)
        {
            _result = result;
            Enabled = result != null;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (_result == null || !_result.Box.IsValid) return;
            var box = _result.Box;
            box.Inflate(Math.Max(box.Diagonal.Length * 0.05, 1.0));
            e.IncludeBoundingBox(box);
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (_result == null) return;
            foreach (var brep in _result.Steps)
                e.Display.DrawBrepShaded(brep, _stepMaterial);
            var railMat = _railMaterial;
            foreach (var brep in _result.RailingLeft) e.Display.DrawBrepShaded(brep, railMat);
            foreach (var brep in _result.RailingRight) e.Display.DrawBrepShaded(brep, railMat);
            foreach (var te in _result.Numbers)
            {
                var t3d = new Text3d(te.PlainText, te.Plane, te.TextHeight);
                e.Display.Draw3dText(t3d, System.Drawing.Color.FromArgb(20, 60, 130));
            }
        }
    }
}
