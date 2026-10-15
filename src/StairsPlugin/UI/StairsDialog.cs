using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;

namespace Stairs.UI
{
    /// <summary>Modeless stair settings dialog with live preview.</summary>
    public class StairsDialog : Form
    {
        private readonly RhinoDoc _doc;
        private readonly Core.StairOptions _o;
        private readonly StairsPreviewConduit _conduit = new StairsPreviewConduit();
        private Core.StairResult _result;
        private bool _suspend;

        private const int LabelWidth = 104;  // right-aligned row labels
        private const int FieldWidth = 96;   // numeric fields
        private const int ChoiceWidth = 228; // drop-downs and the rest-step list
        private const int StatusWidth = 440;

        private readonly NumericStepper _height = new NumericStepper();
        private readonly NumericStepper _stepHeight = new NumericStepper();
        private readonly NumericStepper _width = new NumericStepper();
        private readonly CheckBox _pyramid = new CheckBox { Text = "Narrow each step by" };
        private readonly NumericStepper _pyramidStep = new NumericStepper();
        private readonly DropDown _pyramidAlign = new DropDown { Width = FieldWidth + 24 };
        private readonly NumericStepper _depth = new NumericStepper();
        private readonly NumericStepper _steps = new NumericStepper { MinValue = 1, MaxValue = 999, DecimalPlaces = 0, Increment = 1, Width = FieldWidth };
        private readonly DropDown _depthMode = new DropDown { Width = ChoiceWidth - FieldWidth - 6 };
        private readonly Label _riserInfo = new Label { TextColor = Colors.Gray, VerticalAlignment = VerticalAlignment.Center };
        private readonly Label _autoInfo = new Label { Text = "from the height alone", TextColor = Colors.Gray, VerticalAlignment = VerticalAlignment.Center };
        private string _info = ""; // shown in the status area when there are no warnings
        private readonly DropDown _role = new DropDown { Width = ChoiceWidth };
        private readonly DropDown _structure = new DropDown { Width = ChoiceWidth };
        private readonly NumericStepper _thickness = new NumericStepper();
        private readonly Label _thicknessLabel = RowLabel("Slab depth");
        private readonly CheckBox _showNumbers = new CheckBox { Text = "Show step numbers on treads", Checked = true };
        private readonly NumericStepper _restNumber = new NumericStepper { MinValue = 1, DecimalPlaces = 0, Increment = 1, Value = 1, Width = 64 };
        private readonly CheckBox _restCustomDepth = new CheckBox { Text = "Depth" };
        private readonly NumericStepper _restDepth = new NumericStepper { Enabled = false };
        private readonly ListBox _restList = new ListBox { Size = new Size(ChoiceWidth, 62) };
        private readonly Button _removeRest = new Button { Text = "Remove", Enabled = false };
        private readonly DropDown _railSide = new DropDown { Width = FieldWidth + 24 };
        private readonly DropDown _railType = new DropDown { Width = ChoiceWidth };
        private readonly NumericStepper _railHeight = new NumericStepper();
        private readonly CheckBox _livePreview = new CheckBox { Text = "Live preview", Checked = true };
        private static readonly Color WarningColor = Color.FromArgb(230, 150, 50);
        private readonly Label _status = new Label
        {
            Wrap = WrapMode.Word,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Size = new Size(StatusWidth, 32), // fixed, so warnings never resize the window
            TextColor = WarningColor,
        };
        private readonly double _cm; // one centimetre in model units
        private readonly int _decimals; // decimals shown for lengths in model units
        private readonly System.Globalization.NumberFormatInfo _numberFormat = FieldNumberFormat();

        private Guid _curveId;       // document object the curve comes from (empty: not tracked)
        private bool _refreshPending; // a curve change is waiting to be picked up

        public StairsDialog(RhinoDoc doc, Curve curve, Core.StairOptions options)
            : this(doc, curve, options, Guid.Empty)
        {
        }

        /// <summary>
        /// curveId is the document object the curve was picked from; while the dialog is
        /// open, moving or editing that object updates the preview and the created stairs.
        /// </summary>
        public StairsDialog(RhinoDoc doc, Curve curve, Core.StairOptions options, Guid curveId)
        {
            _doc = doc;
            _o = options;
            _o.Curve = curve;
            _curveId = curveId;
            _cm = RhinoMath.UnitScale(UnitSystem.Centimeters, doc.ModelUnitSystem);
            _decimals = Math.Max(0, (int)Math.Round(-Math.Log10(_cm)));

            Title = "Stairs";
            Resizable = false;
            Maximizable = false;
            Minimizable = false;
            ShowInTaskbar = false;

            BuildLayout();
            LoadIntoControls();
            Rebuild(); // preview right away

            RhinoDoc.ReplaceRhinoObject += OnReplaceObject;
            RhinoDoc.AddRhinoObject += OnObjectEvent;
            RhinoDoc.DeleteRhinoObject += OnObjectEvent;
            RhinoDoc.UndeleteRhinoObject += OnObjectEvent;
            Closed += (s, e) => OnClosedHandler();
        }

        private void OnClosedHandler()
        {
            RhinoDoc.ReplaceRhinoObject -= OnReplaceObject;
            RhinoDoc.AddRhinoObject -= OnObjectEvent;
            RhinoDoc.DeleteRhinoObject -= OnObjectEvent;
            RhinoDoc.UndeleteRhinoObject -= OnObjectEvent;
            _conduit.SetResult(null);
            SaveSettings();
        }

        // Moving or editing an object replaces it under the same id; undo and delete add or
        // remove it. The new geometry is picked up once the edit is done, not inside the event.
        private void OnReplaceObject(object sender, Rhino.DocObjects.RhinoReplaceObjectEventArgs e)
        {
            if (e.ObjectId == _curveId) ScheduleCurveRefresh();
        }

        private void OnObjectEvent(object sender, Rhino.DocObjects.RhinoObjectEventArgs e)
        {
            if (e.ObjectId == _curveId) ScheduleCurveRefresh();
        }

        private void ScheduleCurveRefresh()
        {
            if (_refreshPending || _curveId == Guid.Empty) return;
            _refreshPending = true;
            Application.Instance.AsyncInvoke(RefreshCurve);
        }

        /// <summary>Takes the current shape of the tracked curve object and rebuilds the preview.</summary>
        private void RefreshCurve()
        {
            _refreshPending = false;
            var obj = _doc.Objects.FindId(_curveId);
            if (obj == null || obj.IsDeleted || !(obj.Geometry is Curve curve))
            {
                ShowWarning("The curve was deleted; the stairs keep its last shape.");
                return;
            }
            if (!IsFlat(curve, out double deviation))
            {
                ShowWarning("The curve is no longer flat (it deviates " + Length(deviation)
                    + " in Z); the stairs keep its last flat shape.");
                return;
            }
            _o.Curve = curve.DuplicateCurve();
            Rebuild();
        }

        private bool IsFlat(Curve curve, out double deviation)
        {
            deviation = curve.GetBoundingBox(true).Diagonal.Z;
            return deviation <= _doc.ModelAbsoluteTolerance * 10;
        }

        private void ShowWarning(string text)
        {
            _status.Text = text;
            _status.TextColor = WarningColor;
        }

        private static Image LoadIcon(string name)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                return new Bitmap(s);
            }
        }

        private static Label RowLabel(string text)
        {
            return new Label
            {
                Text = text,
                Width = LabelWidth,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        /// <summary>A form row: right-aligned label, then the fields left to right.</summary>
        private static TableRow Row(Control label, params Control[] fields)
        {
            var line = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var f in fields) line.Items.Add(f);
            return new TableRow(label, new TableCell(line, true));
        }

        private static GroupBox Section(string title, params TableRow[] rows)
        {
            var table = new TableLayout { Spacing = new Size(8, 6), Padding = new Padding(4, 2, 4, 2) };
            foreach (var row in rows) table.Rows.Add(row);
            return new GroupBox { Text = title, Content = table };
        }

        /// <summary>Length field in model units, with limits and steps given in centimetres.</summary>
        private void SetupLength(NumericStepper n, double minCm, double incrementCm)
        {
            n.MinValue = minCm * _cm;
            n.Increment = incrementCm * _cm;
            n.DecimalPlaces = _decimals;
            n.MaximumDecimalPlaces = _decimals + 1;
            n.Width = FieldWidth;
        }

        private string Length(double value) => value.ToString("F" + (_decimals + 1), _numberFormat);

        /// <summary>
        /// Number format matching the numeric fields. On macOS Eto formats them with the
        /// system locale (e.g. a decimal comma), while .NET in Rhino runs invariant.
        /// </summary>
        private static System.Globalization.NumberFormatInfo FieldNumberFormat()
        {
            var nfi = (System.Globalization.NumberFormatInfo)System.Globalization.CultureInfo.CurrentCulture.NumberFormat.Clone();
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var nsLocale = asm.GetType("Foundation.NSLocale", false);
                    if (nsLocale == null) continue;
                    var current = nsLocale.GetProperty("CurrentLocale")?.GetValue(null);
                    if (current?.GetType().GetProperty("DecimalSeparator")?.GetValue(current) is string sep && sep.Length > 0)
                        nfi.NumberDecimalSeparator = sep;
                    break;
                }
            }
            catch (Exception) { /* keep the .NET culture */ }
            return nfi;
        }

        private void BuildLayout()
        {
            string unit = _doc.GetUnitSystemName(true, false, true, true);

            _role.Items.Add(new ListItem { Text = "Mid line", Tag = (int)Core.CurveRole.Mid });
            _role.Items.Add(new ListItem { Text = "Inner edge", Tag = (int)Core.CurveRole.Inner });
            _role.Items.Add(new ListItem { Text = "Outer edge", Tag = (int)Core.CurveRole.Outer });

            _structure.Items.Add(new ListItem { Text = "Continuous slab (concrete)", Tag = (int)Core.StairStructure.Slab });
            _structure.Items.Add(new ListItem { Text = "Planks (floating treads)", Tag = (int)Core.StairStructure.Plank });

            _railSide.Items.Add(new ListItem { Text = "None", Tag = (int)Core.RailSide.None });
            _railSide.Items.Add(new ListItem { Text = "Left", Tag = (int)Core.RailSide.Left });
            _railSide.Items.Add(new ListItem { Text = "Right", Tag = (int)Core.RailSide.Right });
            _railSide.Items.Add(new ListItem { Text = "Both", Tag = (int)Core.RailSide.Both });

            _railType.Items.Add(new ListItem { Text = "Glass with metal frame", Tag = (int)Core.RailType.Glass });
            _railType.Items.Add(new ListItem { Text = "Metal railing", Tag = (int)Core.RailType.Metal });
            _railType.Items.Add(new ListItem { Text = "Cement wall + metal handle", Tag = (int)Core.RailType.Cement });

            _depthMode.Items.Add(new ListItem { Text = "Comfort rule", Tag = (int)Core.TreadDepthMode.Comfort });
            _depthMode.Items.Add(new ListItem { Text = "Fit to curve", Tag = (int)Core.TreadDepthMode.FitToCurve });
            _depthMode.Items.Add(new ListItem { Text = "Custom", Tag = (int)Core.TreadDepthMode.Custom });

            _pyramidAlign.Items.Add(new ListItem { Text = "Center", Tag = (int)Core.PyramidAlign.Center });
            _pyramidAlign.Items.Add(new ListItem { Text = "Left side", Tag = (int)Core.PyramidAlign.Left });
            _pyramidAlign.Items.Add(new ListItem { Text = "Right side", Tag = (int)Core.PyramidAlign.Right });

            SetupLength(_height, 10, 10);
            SetupLength(_stepHeight, 5, 1);
            SetupLength(_width, 30, 5);
            SetupLength(_pyramidStep, 0.5, 1);
            SetupLength(_depth, 5, 1);
            SetupLength(_thickness, 1, 1);
            SetupLength(_railHeight, 30, 5);
            SetupLength(_restDepth, 0, 5);

            var pickBtn = new Button { Text = "Pick Curve", Image = LoadIcon("pick.png") };
            pickBtn.Click += (s, e) => PickCurve();

            var flipBtn = new Button { Text = "Flip Direction", Image = LoadIcon("flip.png") };
            flipBtn.Click += (s, e) => { _o.Flip = !_o.Flip; Rebuild(); };

            var measureBtn = new Button { Text = "Measure", Image = LoadIcon("measure.png") };
            measureBtn.Click += (s, e) => MeasureHeight();

            var autoBtn = new Button
            {
                Text = "Autoconfigure",
                Image = LoadIcon("auto.png"),
                ToolTip = "Uses only the total height and the curve: picks the number of steps by the comfort rule "
                        + "(riser + tread \u2248 46 cm), fits the treads so the last step ends exactly at the curve end, "
                        + "and adds a rest step after every " + _o.RestInterval + " steps.",
            };
            autoBtn.Click += (s, e) => Autoconfigure();

            var addRest = new Button { Text = "Add", Image = LoadIcon("plus.png") };
            addRest.Click += (s, e) => AddRestStep();
            _removeRest.Image = LoadIcon("minus.png");
            _removeRest.Click += (s, e) => RemoveRestStep();

            var createBtn = new Button { Text = "Create Stairs", Image = LoadIcon("stairs.png") };
            createBtn.Click += (s, e) => Create();
            var cancelBtn = new Button { Text = "Close" };
            cancelBtn.Click += (s, e) => Close();

            var restListRow = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Top };
            restListRow.Items.Add(_restList);
            restListRow.Items.Add(_removeRest);

            var footer = new TableLayout { Spacing = new Size(6, 0) };
            footer.Rows.Add(new TableRow(_livePreview, new TableCell(null, true), createBtn, cancelBtn));

            var root = new TableLayout { Padding = new Padding(12), Spacing = new Size(0, 8) };
            root.Rows.Add(Section("Curve",
                Row(RowLabel("The curve is the"), _role),
                Row(RowLabel(""), pickBtn, flipBtn)));
            root.Rows.Add(Section("Dimensions (" + unit + ")",
                Row(RowLabel("Total height"), _height, measureBtn),
                Row(RowLabel(""), autoBtn, _autoInfo),
                Row(RowLabel("Steps"), _steps, _riserInfo),
                Row(RowLabel("Step height"), _stepHeight),
                Row(RowLabel("Stair width"), _width),
                Row(RowLabel("Pyramid"), _pyramid, _pyramidStep),
                Row(RowLabel("Steps snap to"), _pyramidAlign),
                Row(RowLabel("Tread depth"), _depth, _depthMode)));
            root.Rows.Add(Section("Structure (" + unit + ")",
                Row(RowLabel("Type"), _structure),
                Row(_thicknessLabel, _thickness),
                Row(RowLabel(""), _showNumbers)));
            root.Rows.Add(Section("Rest steps (landings)",
                Row(RowLabel("At step"), _restNumber, _restCustomDepth, _restDepth, addRest),
                new TableRow(RowLabel(""), new TableCell(restListRow, true))));
            root.Rows.Add(Section("Railings (" + unit + ")",
                Row(RowLabel("Side"), _railSide),
                Row(RowLabel("Type"), _railType),
                Row(RowLabel("Height"), _railHeight)));
            root.Rows.Add(new TableLayout(new TableRow(_status, new TableCell(null, true)))); // left-aligned
            root.Rows.Add(footer);
            Content = root;

            _depthMode.SelectedIndexChanged += (s, e) => { _depth.Enabled = DepthMode == Core.TreadDepthMode.Custom; Rebuild(); };
            _steps.ValueChanged += (s, e) => StepsChanged();
            _structure.SelectedIndexChanged += (s, e) => { StructureChanged(); Rebuild(); };
            _restCustomDepth.CheckedChanged += (s, e) => { _restDepth.Enabled = _restCustomDepth.Checked == true; };
            _restList.SelectedIndexChanged += (s, e) => { _removeRest.Enabled = _restList.SelectedIndex >= 0; };
            _height.ValueChanged += (s, e) => Rebuild();
            _stepHeight.ValueChanged += (s, e) => Rebuild();
            _width.ValueChanged += (s, e) => Rebuild();
            _pyramid.CheckedChanged += (s, e) => { PyramidChanged(); Rebuild(); };
            _pyramidStep.ValueChanged += (s, e) => Rebuild();
            _pyramidAlign.SelectedIndexChanged += (s, e) => Rebuild();
            _depth.ValueChanged += (s, e) => Rebuild();
            _thickness.ValueChanged += (s, e) => Rebuild();
            _showNumbers.CheckedChanged += (s, e) => Rebuild();
            _railHeight.ValueChanged += (s, e) => Rebuild();
            _railSide.SelectedIndexChanged += (s, e) => { RailSideChanged(); Rebuild(); };
            _railType.SelectedIndexChanged += (s, e) => Rebuild();
            _role.SelectedIndexChanged += (s, e) => Rebuild();
            _livePreview.CheckedChanged += (s, e) =>
            {
                if (_livePreview.Checked != true) _conduit.SetResult(null);
                else Rebuild();
            };
        }

        /// <summary>Shows the slab depth or the plank thickness, whichever the structure uses.</summary>
        private void StructureChanged()
        {
            bool slab = SelectedIndexTag(_structure) == (int)Core.StairStructure.Slab;
            _thicknessLabel.Text = slab ? "Slab depth" : "Plank thickness";
            bool was = _suspend;
            _suspend = true;
            _thickness.Value = slab ? _o.SlabThickness : _o.PlankThickness;
            _suspend = was;
        }

        private void PyramidChanged()
        {
            bool on = _pyramid.Checked == true;
            _pyramidStep.Enabled = on;
            _pyramidAlign.Enabled = on;
        }

        private void RailSideChanged()
        {
            bool any = SelectedIndexTag(_railSide) != (int)Core.RailSide.None;
            _railType.Enabled = any;
            _railHeight.Enabled = any;
        }

        private static int SelectedIndexTag(DropDown dd)
        {
            int i = dd.SelectedIndex;
            if (i < 0) return 0;
            var item = dd.Items[i] as ListItem;
            return item != null && item.Tag is int t ? t : 0;
        }

        private void LoadIntoControls()
        {
            _suspend = true;
            _height.Value = _o.TotalHeight;
            _stepHeight.Value = _o.DesiredStepHeight;
            _width.Value = _o.Width;
            _pyramid.Checked = _o.Pyramid;
            _pyramidStep.Value = _o.PyramidStep;
            SelectByTag(_pyramidAlign, (int)_o.PyramidAlign);
            PyramidChanged();
            SelectByTag(_depthMode, (int)_o.DepthMode);
            _depth.Value = _o.DepthOverride;
            _depth.Enabled = _o.DepthMode == Core.TreadDepthMode.Custom;
            _steps.Value = StepCount();
            _structure.SelectedIndex = _o.Structure == Core.StairStructure.Slab ? 0 : 1;
            _showNumbers.Checked = _o.ShowNumbers;
            _railHeight.Value = _o.RailHeight;
            _restDepth.Value = _o.Width;
            SelectByTag(_railSide, (int)_o.Railing);
            SelectByTag(_railType, (int)_o.RailType);
            SelectByTag(_role, (int)_o.Role);
            StructureChanged();
            RailSideChanged();
            RefreshRestList();
            _suspend = false;
        }

        private static void SelectByTag(DropDown dd, int tag)
        {
            for (int i = 0; i < dd.Items.Count; i++)
                if (((ListItem)dd.Items[i]).Tag is int t && t == tag) { dd.SelectedIndex = i; return; }
            dd.SelectedIndex = 0;
        }

        private void SyncOptions()
        {
            _o.TotalHeight = _height.Value;
            _o.DesiredStepHeight = _stepHeight.Value;
            _o.Width = _width.Value;
            _o.Pyramid = _pyramid.Checked == true;
            _o.PyramidStep = _pyramidStep.Value;
            _o.PyramidAlign = (Core.PyramidAlign)SelectedIndexTag(_pyramidAlign);
            _o.DepthMode = DepthMode;
            if (_o.DepthMode == Core.TreadDepthMode.Custom)
                _o.DepthOverride = _depth.Value; // otherwise the field shows the computed depth
            _o.Structure = (Core.StairStructure)SelectedIndexTag(_structure);
            if (_o.Structure == Core.StairStructure.Slab) _o.SlabThickness = _thickness.Value;
            else _o.PlankThickness = _thickness.Value;
            _o.ShowNumbers = _showNumbers.Checked == true;
            _o.Railing = (Core.RailSide)SelectedIndexTag(_railSide);
            _o.RailType = (Core.RailType)SelectedIndexTag(_railType);
            _o.RailHeight = _railHeight.Value;
            _o.Role = (Core.CurveRole)SelectedIndexTag(_role);
        }

        private void RefreshRestList()
        {
            _restList.Items.Clear();
            foreach (var rs in _o.RestSteps)
            {
                string depth = rs.Depth > 0 ? "depth " + Length(rs.Depth) : "depth = stair width";
                _restList.Items.Add(string.Format("Step {0}  –  {1}", rs.StepNumber, depth));
            }
            _removeRest.Enabled = false;
        }

        private void AddRestStep()
        {
            int n = (int)_restNumber.Value;
            double depth = _restCustomDepth.Checked == true ? (double)_restDepth.Value : 0;
            _o.RestSteps.RemoveAll(r => r.StepNumber == n);
            _o.RestSteps.Add(new Core.RestStep(n, depth));
            _o.RestSteps.Sort((x, y) => x.StepNumber.CompareTo(y.StepNumber));
            RefreshRestList();
            Rebuild();
        }

        private void RemoveRestStep()
        {
            int idx = _restList.SelectedIndex;
            if (idx >= 0 && idx < _o.RestSteps.Count)
            {
                _o.RestSteps.RemoveAt(idx);
                RefreshRestList();
                Rebuild();
            }
        }

        private Core.TreadDepthMode DepthMode => (Core.TreadDepthMode)SelectedIndexTag(_depthMode);

        /// <summary>Number of steps the layout derives from the total and desired step height.</summary>
        private int StepCount()
        {
            double h = Math.Max(_stepHeight.Value, 1e-6);
            return (int)Math.Max(1, Math.Round(Math.Max(_height.Value, 0) / h));
        }

        /// <summary>A new step count sets the step height; the tread depth follows its mode.</summary>
        private void StepsChanged()
        {
            if (_suspend) return;
            int n = Math.Max(1, (int)Math.Round(_steps.Value));
            if (_height.Value > 0)
            {
                _suspend = true;
                _stepHeight.Value = _height.Value / n;
                _suspend = false;
            }
            Rebuild();
        }

        /// <summary>Step count, rest steps and a tread depth that fits the curve, from the total height alone.</summary>
        private void Autoconfigure()
        {
            SyncOptions();
            int n = Core.StairLayout.AutoConfigure(_o);
            LoadIntoControls();
            var rests = _o.RestSteps.ConvertAll(r => r.StepNumber.ToString());
            _info = string.Format("Autoconfigured: {0} steps, tread depth fitted to the curve{1}.", n,
                rests.Count == 0 ? "" : ", rest " + (rests.Count == 1 ? "step " : "steps ") + string.Join(", ", rests));
            Rebuild();
        }

        private void Rebuild()
        {
            if (_suspend) return;
            SyncOptions();
            Core.StairLayout layout = null;
            string status;
            try
            {
                if (_livePreview.Checked == true)
                {
                    _result = Core.StairGeometry.Build(_o);
                    _conduit.SetResult(_result);
                    layout = _result.Layout;
                    status = string.Join("\n", _result.Warnings);
                }
                else
                {
                    layout = Core.StairLayout.Compute(_o);
                    status = string.Join("\n", layout.Warnings);
                }
            }
            catch (Exception ex)
            {
                _conduit.SetResult(null);
                status = "Error: " + ex.Message;
            }
            UpdateDerived(layout);
            bool warn = status.Length > 0;
            _status.Text = warn ? status : _info;
            _status.TextColor = warn ? WarningColor : Colors.Gray;
            _info = "";
        }

        /// <summary>Step count, riser height, and the computed tread depth while the depth is not custom.</summary>
        private void UpdateDerived(Core.StairLayout layout)
        {
            int n = StepCount();
            double H = Math.Max(_height.Value, 0);
            double riser = H > 0 ? H / n : _stepHeight.Value;
            bool was = _suspend;
            _suspend = true;
            _steps.Value = n;
            _riserInfo.Text = "risers of " + Length(riser);
            if (DepthMode != Core.TreadDepthMode.Custom && layout != null && layout.TreadDepth > 0)
                _depth.Value = layout.TreadDepth;
            _suspend = was;
        }

        private void PickCurve()
        {
            Visible = false;
            var go = new Rhino.Input.Custom.GetObject();
            go.SetCommandPrompt("Select stair curve");
            go.GeometryFilter = Rhino.DocObjects.ObjectType.Curve;
            go.EnablePreSelect(false, true);
            go.Get();
            var picked = go.CommandResult() == Result.Success ? go.Object(0) : null;
            var curve = picked?.Curve();
            Visible = true;
            if (curve == null) return;
            if (!IsFlat(curve, out double deviation))
            {
                ShowWarning("The curve must be flat (constant Z); it deviates " + Length(deviation) + " in Z.");
                return;
            }
            _o.Curve = curve;
            _curveId = TrackableId(picked);
            Rebuild();
        }

        /// <summary>The object id to follow for a picked curve; empty for edges of other objects.</summary>
        internal static Guid TrackableId(Rhino.DocObjects.ObjRef picked)
        {
            var obj = picked?.Object();
            return obj != null && obj.Geometry is Curve ? picked.ObjectId : Guid.Empty;
        }

        private void MeasureHeight()
        {
            Visible = false;
            double measured = 0;
            try
            {
                var gp = new Rhino.Input.Custom.GetPoint();
                gp.SetCommandPrompt("Stair base point (floor level)");
                gp.Get();
                if (gp.CommandResult() == Result.Success)
                {
                    var p1 = gp.Point();
                    gp = new Rhino.Input.Custom.GetPoint();
                    gp.SetCommandPrompt("Stair top point (upper level)");
                    gp.DrawLineFromPoint(p1, true);
                    gp.Get();
                    if (gp.CommandResult() == Result.Success)
                    {
                        var p2 = gp.Point();
                        measured = Math.Abs(p2.Z - p1.Z);
                        if (measured < RhinoMath.ZeroTolerance)
                            measured = p1.DistanceTo(p2);
                    }
                }
            }
            finally { Visible = true; }
            if (measured > 0)
            {
                _suspend = true;
                _height.Value = measured;
                _suspend = false;
                Rebuild();
            }
        }

        private void Create()
        {
            SyncOptions();
            Core.StairResult result;
            try { result = Core.StairGeometry.Build(_o); }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("StairsGen: build failed: " + ex.Message);
                return;
            }
            if (result.Steps.Count == 0)
            {
                RhinoApp.WriteLine("StairsGen: nothing to create. " + string.Join("  ", result.Warnings));
                return;
            }

            var doc = RhinoDoc.ActiveDoc ?? _doc;
            uint undo = doc.BeginUndoRecord("Create stairs");
            try
            {
                int stepsLayer = EnsureLayer(doc, "Stairs::Steps", System.Drawing.Color.FromArgb(120, 120, 125));
                int numLayer = EnsureLayer(doc, "Stairs::Numbers", System.Drawing.Color.FromArgb(20, 60, 130));
                int railLayer = EnsureLayer(doc, "Stairs::Railing", System.Drawing.Color.FromArgb(70, 110, 190));
                int group = doc.Groups.Add();

                foreach (var brep in result.Steps)
                    doc.Objects.AddBrep(brep, MakeAttributes(doc, stepsLayer, group));
                foreach (var te in result.Numbers)
                    doc.Objects.AddText(te, MakeAttributes(doc, numLayer, group));
                foreach (var brep in result.RailingLeft)
                    doc.Objects.AddBrep(brep, MakeAttributes(doc, railLayer, group));
                foreach (var brep in result.RailingRight)
                    doc.Objects.AddBrep(brep, MakeAttributes(doc, railLayer, group));

                doc.Views.Redraw();
            }
            finally
            {
                doc.EndUndoRecord(undo);
            }
            SaveSettings();
            Close();
        }

        private static Rhino.DocObjects.ObjectAttributes MakeAttributes(RhinoDoc doc, int layerIndex, int group)
        {
            var attrs = new Rhino.DocObjects.ObjectAttributes();
            attrs.LayerIndex = layerIndex;
            attrs.AddToGroup(group);
            return attrs;
        }

        private static int EnsureLayer(RhinoDoc doc, string fullPath, System.Drawing.Color color)
        {
            int idx = doc.Layers.FindByFullPath(fullPath, true);
            if (idx >= 0) return idx;

            string[] parts = fullPath.Split(new[] { "::" }, System.StringSplitOptions.None);
            string parentPath = "";
            Rhino.DocObjects.Layer parentLayer = null;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                parentPath = parentPath.Length == 0 ? parts[i] : parentPath + "::" + parts[i];
                int p = doc.Layers.FindByFullPath(parentPath, true);
                if (p >= 0)
                {
                    parentLayer = doc.Layers[p];
                }
                else
                {
                    var layer = new Rhino.DocObjects.Layer
                    {
                        Name = parts[i],
                        ParentLayerId = parentLayer?.Id ?? Guid.Empty,
                        Color = System.Drawing.Color.FromArgb(90, 90, 95),
                    };
                    int added = doc.Layers.Add(layer);
                    parentLayer = doc.Layers[added];
                }
            }
            var leaf = new Rhino.DocObjects.Layer
            {
                Name = parts[parts.Length - 1],
                ParentLayerId = parentLayer?.Id ?? Guid.Empty,
                Color = color,
            };
            return doc.Layers.Add(leaf);
        }

        private void SaveSettings()
        {
            try
            {
                SyncOptions();
                var s = StairsPlugin.Instance.Settings;
                s.SetInteger("Units", (int)_doc.ModelUnitSystem);
                s.SetDouble("Height", _o.TotalHeight);
                s.SetDouble("StepHeight", _o.DesiredStepHeight);
                s.SetDouble("Width", _o.Width);
                s.SetBool("Pyramid", _o.Pyramid);
                s.SetDouble("PyramidStep", _o.PyramidStep);
                s.SetInteger("PyramidAlign", (int)_o.PyramidAlign);
                s.SetInteger("DepthMode", (int)_o.DepthMode);
                s.SetDouble("DepthOverride", _o.DepthOverride);
                s.SetInteger("Structure", (int)_o.Structure);
                s.SetDouble("SlabThickness", _o.SlabThickness);
                s.SetDouble("PlankThickness", _o.PlankThickness);
                s.SetBool("ShowNumbers", _o.ShowNumbers);
                s.SetInteger("RailSide", (int)_o.Railing);
                s.SetInteger("RailType", (int)_o.RailType);
                s.SetDouble("RailHeight", _o.RailHeight);
                s.SetInteger("Role", (int)_o.Role);
                var numbers = new System.Text.StringBuilder();
                foreach (var rs in _o.RestSteps) numbers.Append(rs.StepNumber).Append(';').Append(rs.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                s.SetString("RestSteps", numbers.ToString());
            }
            catch (Exception) { /* settings are best-effort */ }
        }

        internal static Core.StairOptions LoadSettings(RhinoDoc doc)
        {
            var o = new Core.StairOptions();
            try
            {
                double cm = RhinoMath.UnitScale(UnitSystem.Centimeters, doc.ModelUnitSystem);
                var s = StairsPlugin.Instance.Settings;
                // lengths are stored in the units of the document they were saved from
                int saved = s.GetInteger("Units", -1);
                double k = saved >= 0 ? RhinoMath.UnitScale((UnitSystem)saved, doc.ModelUnitSystem) : 1;
                double Len(string key, double defaultCm) => s.TryGetDouble(key, out double v) ? v * k : defaultCm * cm;
                o.TotalHeight = Len("Height", 300);
                o.DesiredStepHeight = Len("StepHeight", 17);
                o.Width = Len("Width", 100);
                o.Pyramid = s.GetBool("Pyramid", false);
                o.PyramidStep = Len("PyramidStep", 5);
                o.PyramidAlign = (Core.PyramidAlign)s.GetInteger("PyramidAlign", 0);
                int mode = s.GetInteger("DepthMode", -1);
                o.DepthMode = mode >= 0 ? (Core.TreadDepthMode)mode
                    : s.GetBool("AutoDepth", true) ? Core.TreadDepthMode.Comfort : Core.TreadDepthMode.Custom;
                o.DepthOverride = Len("DepthOverride", 28);
                o.Structure = (Core.StairStructure)s.GetInteger("Structure", (int)o.Structure);
                o.SlabThickness = Len("SlabThickness", 15);
                o.PlankThickness = Len("PlankThickness", 4);
                o.ShowNumbers = s.GetBool("ShowNumbers", o.ShowNumbers);
                o.Railing = (Core.RailSide)s.GetInteger("RailSide", (int)o.Railing);
                o.RailType = (Core.RailType)s.GetInteger("RailType", (int)o.RailType);
                o.RailHeight = Len("RailHeight", 90);
                o.Role = (Core.CurveRole)s.GetInteger("Role", (int)o.Role);
                string rs = s.GetString("RestSteps", "");
                if (!string.IsNullOrEmpty(rs))
                {
                    foreach (var tok in rs.Split(new[] { '|' }, System.StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = tok.Split(';');
                        if (kv.Length == 2)
                            o.RestSteps.Add(new Core.RestStep(int.Parse(kv[0]), k * double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
            }
            catch (Exception) { }
            return o;
        }
    }
}
