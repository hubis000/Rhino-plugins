using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using PropellerTools.Core;

namespace PropellerTools.Plugin
{
    /// <summary>
    /// Thin Rhino front end. All geometry work lives in PropellerTools.Core so the solver
    /// can be unit tested without Rhino running.
    /// </summary>
    public class RovingSectionsCommand : Command
    {
        public override string EnglishName => "PropRovingSections";

        private static double _targetArea = 20.0;
        private static int _stations = 40;
        private static double _maxFraction = 0.55;
        private static PocketRule _rule = PocketRule.InwardOffset;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            ObjRef bladeRef;
            var gb = new GetObject();
            gb.SetCommandPrompt("Select the blade solid");
            gb.GeometryFilter = ObjectType.Brep;
            if (gb.Get() != Rhino.Input.GetResult.Object) return Result.Cancel;
            bladeRef = gb.Object(0);
            var blade = bladeRef.Brep();
            if (blade == null) return Result.Failure;

            if (!blade.IsSolid)
                RhinoApp.WriteLine("Warning: blade is not a closed solid. Sections may be open.");

            var gc = new GetObject();
            gc.SetCommandPrompt("Select the spanwise bounding curve");
            gc.GeometryFilter = ObjectType.Curve;
            if (gc.Get() != Rhino.Input.GetResult.Object) return Result.Cancel;
            var guide = gc.Object(0).Curve();
            if (guide == null) return Result.Failure;

            // Options.
            var go = new GetOption();
            go.SetCommandPrompt("Settings");
            while (true)
            {
                go.ClearCommandOptions();
                var oArea = new OptionDouble(_targetArea, 0.001, 1e6);
                var oSt = new OptionInteger(_stations, 2, 500);
                var oFrac = new OptionDouble(_maxFraction, 0.05, 0.95);
                int iArea = go.AddOptionDouble("TargetArea", ref oArea);
                int iSt = go.AddOptionInteger("Stations", ref oSt);
                int iFrac = go.AddOptionDouble("MaxAreaFraction", ref oFrac);
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
                else if (idx == iRule)
                    _rule = go.Option().CurrentListOptionIndex == 0
                          ? PocketRule.InwardOffset : PocketRule.CentroidScale;
                else break;
            }

            var settings = new SolverSettings
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
                stations = RovingAreaSolver.Solve(blade, guide, settings);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Solver failed: " + ex.Message);
                return Result.Failure;
            }

            int ok = stations.Count(r => r.PocketOutline != null);
            int sat = stations.Count(r => r.Saturated);
            int bad = stations.Count(r => r.PocketOutline == null);

            if (ok < 2)
            {
                RhinoApp.WriteLine(string.Format("Only {0} usable station(s); nothing to loft.", ok));
                foreach (var r in stations.Where(x => !string.IsNullOrEmpty(x.Note)))
                    RhinoApp.WriteLine(string.Format("  span {0:F3}: {1}", r.NormalisedSpan, r.Note));
                return Result.Failure;
            }

            // Report before adding geometry, so a bad run is obvious in the command line.
            RhinoApp.WriteLine("--- PropRovingSections ---");
            RhinoApp.WriteLine(string.Format(
                "stations solved: {0}   clamped: {1}   failed: {2}", ok, sat, bad));
            foreach (var r in stations.Where(x => x.PocketOutline != null))
            {
                // RhinoApp.WriteLine has no overload beyond 4 format arguments,
                // so format first and pass a single string.
                RhinoApp.WriteLine(string.Format(
                    "  span {0:F3}  section {1:F2}  target {2:F2}  achieved {3:F2}  param {4:F4}{5}",
                    r.NormalisedSpan, r.BladeSectionArea, r.TargetArea,
                    r.AchievedArea, r.SolvedParameter, r.Saturated ? "  [CLAMPED]" : ""));
            }
            foreach (var r in stations.Where(x => !string.IsNullOrEmpty(x.Note)))
                RhinoApp.WriteLine(string.Format("  note @ span {0:F3}: {1}", r.NormalisedSpan, r.Note));

            var attrSection = new ObjectAttributes { Name = "roving_section" };
            foreach (var r in stations.Where(x => x.PocketOutline != null))
                doc.Objects.AddCurve(r.PocketOutline, attrSection);

            var breps = RovingAreaSolver.BuildCuttingSurface(stations, guide);
            if (breps.Length == 0)
                RhinoApp.WriteLine("Loft failed. Outlines were added so you can inspect them.");
            else
                foreach (var b in breps)
                    doc.Objects.AddBrep(b, new ObjectAttributes { Name = "roving_cut_surface" });

            doc.Views.Redraw();
            return Result.Success;
        }
    }
}
