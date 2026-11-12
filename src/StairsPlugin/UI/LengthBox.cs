using System;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace Stairs.UI
{
    /// <summary>
    /// A length field in model units: a text box with up and down arrows, standing in for a
    /// NumericStepper. It shows its value the way the model displays lengths (Units.Format) and
    /// reads what is typed in any unit Rhino understands (Units.TryParse), when Enter is pressed
    /// or the field loses focus. Up and down (arrows or keys) step by Increment.
    /// </summary>
    internal sealed class LengthBox : Panel
    {
        private const int StepperWidth = 16;
        private readonly TextBox _text = new TextBox();
        private readonly Stepper _stepper = new Stepper();
        private RhinoDoc _doc;
        private double _value;
        private string _shown = ""; // the text the value was last shown as

        public double MinValue { get; set; } = double.MinValue;
        public double MaxValue { get; set; } = double.MaxValue;
        public double Increment { get; set; } = 1;

        /// <summary>Raised when the value changes, typed, stepped or set.</summary>
        public event EventHandler<EventArgs> ValueChanged;

        public LengthBox()
        {
            var row = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2, VerticalContentAlignment = VerticalAlignment.Center };
            row.Items.Add(new StackLayoutItem(_text, true));
            row.Items.Add(_stepper);
            Content = row;
            _text.KeyDown += (s, e) =>
            {
                if (e.Key == Keys.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Keys.Up) { Step(1); e.Handled = true; }
                else if (e.Key == Keys.Down) { Step(-1); e.Handled = true; }
            };
            _text.LostFocus += (s, e) => Commit();
            _stepper.Step += (s, e) => Step(e.Direction == StepperDirection.Up ? 1 : -1);
        }

        /// <summary>
        /// Ties the field to the model's units: limits and step in centimetres, converted, with the
        /// step a usual inch step in an imperial model (see Units.Increment).
        /// </summary>
        public void Setup(RhinoDoc doc, double minCm, double incrementCm, int width)
        {
            _doc = doc;
            MinValue = minCm * RhinoMath.UnitScale(UnitSystem.Centimeters, doc.ModelUnitSystem);
            Increment = Units.Increment(doc, incrementCm);
            Width = width;
            Show();
        }

        public new int Width
        {
            get => base.Width;
            set
            {
                base.Width = value;
                _text.Width = Math.Max(30, value - StepperWidth - 2);
            }
        }

        public double Value
        {
            get => _value;
            set => SetValue(value);
        }

        public new bool Enabled
        {
            get => _text.Enabled;
            set { _text.Enabled = value; _stepper.Enabled = value; }
        }

        public new string ToolTip
        {
            get => _text.ToolTip;
            set { _text.ToolTip = value; _stepper.ToolTip = value; }
        }

        /// <summary>Kept for the places that set it on a NumericStepper; lengths show as the model displays them.</summary>
        public int DecimalPlaces { get; set; }
        public int MaximumDecimalPlaces { get; set; }

        private void SetValue(double v)
        {
            if (double.IsNaN(v)) return;
            v = Math.Max(MinValue, Math.Min(MaxValue, v));
            bool changed = v != _value;
            _value = v;
            Show();
            if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Show()
        {
            var doc = _doc ?? RhinoDoc.ActiveDoc;
            _shown = doc != null ? Units.Format(doc, _value) : _value.ToString("0.####", Units.NumberFormat);
            _text.Text = _shown;
        }

        /// <summary>Reads the typed text; text left as shown keeps the exact value behind it.</summary>
        private void Commit()
        {
            string t = _text.Text ?? "";
            if (t == _shown) return;
            var doc = _doc ?? RhinoDoc.ActiveDoc;
            if (doc != null && Units.TryParse(doc, t, out double v)) SetValue(v);
            else Show(); // not a length: back to the value
        }

        private void Step(int direction)
        {
            Commit();
            double inc = Increment > 0 ? Increment : 1;
            // to the next whole step, so 7 1/16" steps up to 7 1/4", not to 7 5/16"
            double k = _value / inc;
            double next = direction > 0 ? Math.Floor(k + 1e-6) + 1 : Math.Ceiling(k - 1e-6) - 1;
            SetValue(next * inc);
        }
    }
}
