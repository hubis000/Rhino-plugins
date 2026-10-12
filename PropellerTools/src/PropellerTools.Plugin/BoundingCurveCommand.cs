using System;
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
    /// Generates the spanwise guide curve automatically, so it does not have to be drawn
    /// by hand. Output is named "roving_guide" and can be fed straight into
    /// PropRovingSections or PropCuttingSurfaces.
    /// </summary>
    public class BoundingCurveCommand : Command
    {
        public override string EnglishName => "PropBoundingCurve";

        private static int _samples = 60;
        private static double _rootSkip = 0.08;
        private static double _tipSkip = 0.03;
        private static int _cps = 12;
        private static bool _flatten = false;
        private static bool _showCentroids = false;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var gb = new GetObject();
            gb.SetCommandPrompt("Select the blade solid");
            gb.GeometryFilter = ObjectType.Brep;
            if (gb.Get() != Rhino.Input.GetResult.Object) return Result.Cancel;
            var blade = gb.Object(0).Brep();
            if (blade == null) return Result.Failure;

            if (!blade.IsSolid)
                RhinoApp.WriteLine("Warning: blade is not a closed solid; centroids may be unreliable.");

            var go = new GetOption();
            go.SetCommandPrompt("Settings");
            while (true)
            {
                go.ClearCommandOptions();
                var oSamp = new OptionInteger(_samples, 4, 500);
                var oRoot = new OptionDouble(_rootSkip, 0.0, 0.5);
                var oTip = new OptionDouble(_tipSkip, 0.0, 0.5);
                var oCps = new OptionInteger(_cps, 4, 200);
                var oFlat = new OptionToggle(_flatten, "No", "Yes");
                var oCent = new OptionToggle(_showCentroids, "No", "Yes");

                int iSamp = go.AddOptionInteger("Samples", ref oSamp);
                int iRoot = go.AddOptionDouble("RootSkip", ref oRoot);
                int iTip = go.AddOptionDouble("TipSkip", ref oTip);
                int iCps = go.AddOptionInteger("ControlPoints", ref oCps);
                int iFlat = go.AddOptionToggle("FlattenToZ", ref oFlat);
                int iCent = go.AddOptionToggle("ShowCentroids", ref oCent);
                go.AddOption("Run");

                var res = go.Get();
                if (res == Rhino.Input.GetResult.Cancel) return Result.Cancel;
                if (res != Rhino.Input.GetResult.Option) break;

                int idx = go.OptionIndex();
                if (idx == iSamp) _samples = oSamp.CurrentValue;
                else if (idx == iRoot) _rootSkip = oRoot.CurrentValue;
                else if (idx == iTip) _tipSkip = oTip.CurrentValue;
                else if (idx == iCps) _cps = oCps.CurrentValue;
                else if (idx == iFlat) _flatten = oFlat.CurrentValue;
                else if (idx == iCent) _showCentroids = oCent.CurrentValue;
                else break;
            }

            var settings = new BoundingCurveSettings
            {
                SampleCount = _samples,
                RootSkip = _rootSkip,
                TipSkip = _tipSkip,
                RebuildControlPoints = _cps,
                FlattenToZ = _flatten,
                Tolerance = doc.ModelAbsoluteTolerance
            };

            BoundingCurveResult r;
            try
            {
                r = BoundingCurveBuilder.Build(blade, settings);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("Bounding curve failed: " + ex.Message);
                return Result.Failure;
            }

            RhinoApp.WriteLine("--- PropBoundingCurve ---");
            RhinoApp.WriteLine(string.Format(
                "span axis: {0:F4}, {1:F4}, {2:F4}",
                r.SpanAxis.X, r.SpanAxis.Y, r.SpanAxis.Z));
            RhinoApp.WriteLine(string.Format(
                "sections used: {0}   missed: {1}   max fit deviation: {2:F4}",
                r.SectionsFound, r.SectionsMissed, r.MaxDeviationFromRaw));

            foreach (var w in r.Warnings)
                RhinoApp.WriteLine("  warning: " + w);

            var attr = new ObjectAttributes { Name = "roving_guide" };
            var id = doc.Objects.AddCurve(r.Curve, attr);

            if (_showCentroids)
            {
                var pattr = new ObjectAttributes { Name = "roving_guide_centroid" };
                foreach (var p in r.RawCentroids)
                    doc.Objects.AddPoint(p, pattr);
            }

            doc.Views.Redraw();

            RhinoApp.WriteLine("Guide curve added as \"roving_guide\". "
                             + "Check it in a top view before using it.");

            return id != Guid.Empty ? Result.Success : Result.Failure;
        }
    }
}
