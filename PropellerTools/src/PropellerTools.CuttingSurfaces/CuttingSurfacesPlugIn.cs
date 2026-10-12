using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.PlugIns;
using PropellerTools.Core;

namespace PropellerTools.CuttingSurfaces
{
    /// <summary>
    /// Separate .rhp from PropellerTools. Must reference the SAME PropellerTools.Core
    /// assembly rather than carrying its own copy of the solver, otherwise the two plugins
    /// will drift and report different areas for the same blade.
    ///
    /// This GUID is generated and distinct from PropellerToolsPlugIn. Keep it stable across
    /// releases; Rhino keys plugin settings to it.
    /// </summary>
    [Guid("3CF5C1F5-5601-4938-9AF9-186F0627B3FB")]
    public class CuttingSurfacesPlugIn : PlugIn
    {
        public static CuttingSurfacesPlugIn Instance { get; private set; }

        public CuttingSurfacesPlugIn() { Instance = this; }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Fail loudly here rather than letting Rhino report a bare
            // "initialization failed" with no detail.
            try
            {
                var probe = typeof(RovingAreaSolver);
                if (probe == null)
                {
                    errorMessage = "PropellerTools.Core could not be resolved.";
                    return LoadReturnCode.ErrorShowDialog;
                }
            }
            catch (Exception ex)
            {
                errorMessage = "PropellerTools.Core failed to load: " + ex.Message;
                return LoadReturnCode.ErrorShowDialog;
            }

            return LoadReturnCode.Success;
        }
    }

    public class CuttingSurfacesCommand : Command
    {
        public override string EnglishName => "PropCuttingSurfaces";

        private static double _targetArea = 20.0;
        private static int _stations = 40;
        private static double _maxFraction = 0.55;
        private static double _edgeExt = 5.0;
        private static PocketRule _rule = PocketRule.InwardOffset;
        private static bool _keepOutlines = false;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var gb = new GetObject();
            gb.SetCommandPrompt("Select the blade solid");
            gb.GeometryFilter = ObjectType.Brep;
            if (gb.Get() != Rhino.Input.GetResult.Object) return Result.Cancel;
            var blade = gb.Object(0).Brep();
            if (blade == null) return Result.Failure;

            if (!blade.IsSolid)
                RhinoApp.WriteLine("Warning: blade is not a closed solid; sections may be open.");

            var gc = new GetObject();
            gc.SetCommandPrompt("Select the spanwise bounding curve");
            gc.GeometryFilter = ObjectType.Curve;
            if (gc.Get() != Rhino.Input.GetResult.Object) return Result.Cancel;
            var guide = gc.Object(0).Curve();
            if (guide == null) return Result.Failure;

            var go = new GetOption();
            go.SetCommandPrompt("Settings");
            while (true)
            {
                go.ClearCommandOptions();
                var oArea = new OptionDouble(_targetArea, 0.001, 1e6);
                var oSt = new OptionInteger(_stations, 2, 500);
                var oFrac = new OptionDouble(_maxFraction, 0.05, 0.95);
                var oExt = new OptionDouble(_edgeExt, 0.0, 1e4);
                var oKeep = new OptionToggle(_keepOutlines, "No", "Yes");

                int iArea = go.AddOptionDouble("TargetArea", ref oArea);
                int iSt = go.AddOptionInteger("Stations", ref oSt);
                int iFrac = go.AddOptionDouble("MaxAreaFraction", ref oFrac);
                int iExt = go.AddOptionDouble("EdgeExtension", ref oExt);
                int iKeep = go.AddOptionToggle("KeepOutlines", ref oKeep);
                int iRule = go.AddOptionList("Rule",
                    new[] { "InwardOffset", "CentroidScale" },
                    _rule == PocketRule.InwardOffset ? 0 : 1);
                go.AddOption("Run");

                var res = go.Get();
                if (res == Rhino.Input.GetResult.Cancel) return Result.Cancel;
                if (res != Rhino.Input.GetResult.Option) break;

                int idx = go.OptionIndex();
                if (idx == iArea) _targetArea = oArea.CurrentValue;
                else if (idx == iSt) _stations = oSt.CurrentValue;
                else if (idx == iFrac) _maxFraction = oFrac.CurrentValue;
                else if (idx == iExt) _edgeExt = oExt.CurrentValue;
                else if (idx == iKeep) _keepOutlines = oKeep.CurrentValue;
                else if (idx == iRule)
                    _rule = go.Option().CurrentListOptionIndex == 0
                          ? PocketRule.InwardOffset : PocketRule.CentroidScale;
                else break;
            }

            var solverSettings = new SolverSettings
            {
                TargetArea = _targetArea,
                StationCount = _stations,
                Rule = _rule,
                MaxAreaFraction = _maxFraction,
                Tolerance = doc.ModelAbsoluteTolerance
            };

            List<StationResult> stations;
            try
            {
                stations = RovingAreaSolver.Solve(blade, guide, solverSettings);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Solver failed: " + ex.Message);
                return Result.Failure;
            }

            var csSettings = new CuttingSurfaceSettings
            {
                EdgeExtension = _edgeExt,
                Tolerance = doc.ModelAbsoluteTolerance
            };

            CuttingSurfaceResult built;
            try
            {
                built = CuttingSurfaceBuilder.Build(stations, csSettings);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Surface build failed: " + ex.Message);
                return Result.Failure;
            }

            RhinoApp.WriteLine("--- PropCuttingSurfaces ---");
            RhinoApp.WriteLine(string.Format("stations solved: {0}   split: {1}   clamped: {2}",
                stations.Count(r => r.PocketOutline != null),
                built.Outlines.Count,
                stations.Count(r => r.Saturated)));

            foreach (var r in stations.Where(x => x.PocketOutline != null))
            {
                // RhinoApp.WriteLine has no overload beyond 4 format arguments,
                // so format first and pass a single string.
                RhinoApp.WriteLine(string.Format(
                    "  span {0:F3}  section {1:F2}  target {2:F2}  achieved {3:F2}{4}",
                    r.NormalisedSpan, r.BladeSectionArea, r.TargetArea,
                    r.AchievedArea, r.Saturated ? "  [CLAMPED]" : ""));
            }

            foreach (var w in built.Warnings)
                RhinoApp.WriteLine("  warning: " + w);
            foreach (var r in stations.Where(x => !string.IsNullOrEmpty(x.Note)))
                RhinoApp.WriteLine(string.Format("  note @ span {0:F3}: {1}", r.NormalisedSpan, r.Note));

            if (built.UpperSurface.Length == 0 && built.LowerSurface.Length == 0)
            {
                RhinoApp.WriteLine("No cutting surfaces produced. Adding split outlines for inspection.");
                AddOutlines(doc, built);
                doc.Views.Redraw();
                return Result.Failure;
            }

            var upAttr = new ObjectAttributes { Name = "roving_cut_upper" };
            var loAttr = new ObjectAttributes { Name = "roving_cut_lower" };

            foreach (var b in built.UpperSurface) doc.Objects.AddBrep(b, upAttr);
            foreach (var b in built.LowerSurface) doc.Objects.AddBrep(b, loAttr);

            if (_keepOutlines) AddOutlines(doc, built);

            doc.Views.Redraw();
            return Result.Success;
        }

        private static void AddOutlines(RhinoDoc doc, CuttingSurfaceResult built)
        {
            var a = new ObjectAttributes { Name = "roving_half_upper" };
            var b = new ObjectAttributes { Name = "roving_half_lower" };
            foreach (var o in built.Outlines)
            {
                if (o.Upper != null) doc.Objects.AddCurve(o.Upper, a);
                if (o.Lower != null) doc.Objects.AddCurve(o.Lower, b);
            }
        }
    }
}
