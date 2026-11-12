using System;
using System.Text.RegularExpressions;
using Rhino;

namespace Stairs.UI
{
    /// <summary>
    /// Lengths as the model shows them: in its units and its distance display (decimal, fractional,
    /// or feet and inches), and typed in any unit Rhino understands ("7in", "18cm", "6'-8"").
    /// Defaults and step sizes are round numbers in either system: 17 cm in a metric model, 7" in
    /// an imperial one, never a converted 6.69".
    /// </summary>
    internal static class Units
    {
        /// <summary>Whether the model works in inches, feet or another imperial unit.</summary>
        public static bool IsImperial(RhinoDoc doc)
        {
            switch (doc.ModelUnitSystem)
            {
                case UnitSystem.Inches:
                case UnitSystem.Feet:
                case UnitSystem.Yards:
                case UnitSystem.Miles:
                case UnitSystem.Mils:
                case UnitSystem.Microinches:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The model unit's short name: "cm", "in", "ft".</summary>
        public static string Abbreviation(RhinoDoc doc) => doc.GetUnitSystemName(true, false, true, true);

        /// <summary>
        /// A length in model units as the model shows it, with its unit: "17.5 cm", "7 in",
        /// "6'-8"", "7-1/16 in". Decimal lengths keep the model's display precision, but also
        /// enough decimals for a tenth of a millimetre (or a hundredth of an inch), and drop
        /// trailing zeros.
        /// </summary>
        public static string Format(RhinoDoc doc, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "";
            var mode = doc.ModelDistanceDisplayMode;
            if (mode == Rhino.UI.DistanceDisplayMode.FeetInches)
                return doc.FormatNumber(value);
            if (mode == Rhino.UI.DistanceDisplayMode.Fractional)
                return doc.FormatNumber(value, true, true);

            double fine = IsImperial(doc)
                ? RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem) / 100
                : RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem) / 10;
            int digits = Math.Max(doc.ModelDistanceDisplayPrecision, (int)Math.Ceiling(-Math.Log10(fine) - 1e-9));
            digits = Math.Max(0, Math.Min(digits, 8));
            string text = value.ToString("F" + digits, NumberFormat);
            if (digits > 0) text = text.TrimEnd('0').TrimEnd(NumberFormat.NumberDecimalSeparator.ToCharArray());
            if (text == "-0") text = "0";
            return text + " " + Abbreviation(doc);
        }

        /// <summary>A point as "x, y, z" in the model's display, with the unit once at the end in decimal display.</summary>
        public static string FormatPoint(RhinoDoc doc, Rhino.Geometry.Point3d p)
        {
            if (doc.ModelDistanceDisplayMode != Rhino.UI.DistanceDisplayMode.Decimal)
                return Format(doc, p.X) + ", " + Format(doc, p.Y) + ", " + Format(doc, p.Z);
            string unit = " " + Abbreviation(doc);
            string Bare(double v) { string t = Format(doc, v); return t.EndsWith(unit) ? t.Substring(0, t.Length - unit.Length) : t; }
            return Bare(p.X) + ", " + Bare(p.Y) + ", " + Bare(p.Z) + unit;
        }

        /// <summary>
        /// Reads a typed length into model units. A bare number is in model units; units and
        /// feet-and-inches are understood ("7in", "18 cm", "6'-8"", "6 1/2""), and so is a
        /// decimal comma. False if the text is not a length.
        /// </summary>
        public static bool TryParse(RhinoDoc doc, string text, out double value)
        {
            value = 0;
            if (text == null) return false;
            string t = text.Trim();
            if (t.Length == 0) return false;
            if (t.IndexOf('.') < 0 && Regex.IsMatch(t, @"^-?\d+,\d+")) t = t.Replace(',', '.'); // 17,5
            t = Regex.Replace(t, @"(\d)\s+(\d+/\d+)", "$1-$2");         // 6 1/2" -> 6-1/2"
            t = Regex.Replace(t, @"'\s+(\d)", "'-$1");                   // 6' 8"  -> 6'-8"
            try
            {
                int used = Rhino.Input.StringParser.ParseLengthExpession(
                    t, Rhino.Input.StringParserSettings.DefaultParseSettings, doc.ModelUnitSystem, out value);
                return used > 0 && t.Substring(used).Trim().Length == 0 && !double.IsNaN(value) && Math.Abs(value) < 1e300;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A default length in model units: the centimetre value in a metric model; in an imperial
        /// one the inch value when given, otherwise the centimetre value rounded to a round
        /// number of inches.
        /// </summary>
        public static double Default(RhinoDoc doc, double cm, double inches = double.NaN)
        {
            if (!IsImperial(doc)) return cm * RhinoMath.UnitScale(UnitSystem.Centimeters, doc.ModelUnitSystem);
            double inch = double.IsNaN(inches) ? RoundInches(cm / 2.54) : inches;
            return inch * RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem);
        }

        /// <summary>A step size in model units: the centimetre value, or the nearest usual inch step.</summary>
        public static double Increment(RhinoDoc doc, double cm)
        {
            if (!IsImperial(doc)) return cm * RhinoMath.UnitScale(UnitSystem.Centimeters, doc.ModelUnitSystem);
            double want = cm / 2.54, best = Steps[0];
            foreach (double s in Steps)
                if (Math.Abs(Math.Log(s / want)) < Math.Abs(Math.Log(best / want))) best = s;
            return best * RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem);
        }

        private static readonly double[] Steps = { 1.0 / 16, 1.0 / 8, 1.0 / 4, 1.0 / 2, 1, 2, 3, 6, 12, 24 };

        /// <summary>Inches rounded the way a drawing would: to 1/16" below 1", 1/4" below 6", whole inches above.</summary>
        private static double RoundInches(double inches)
        {
            double a = Math.Abs(inches);
            double step = a < 1 ? 1.0 / 16 : a < 6 ? 0.25 : 1;
            double r = Math.Round(inches / step) * step;
            return r == 0 && inches != 0 ? Math.Sign(inches) * step : r;
        }

        /// <summary>The number format of the fields: the system's decimal separator (see StairsDialog.FieldNumberFormat).</summary>
        internal static readonly System.Globalization.NumberFormatInfo NumberFormat = StairsDialog.FieldNumberFormat();
    }
}
