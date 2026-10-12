using System;
using System.Collections.Generic;
using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MejzlikEdgeCut
{
    /// <summary>
    /// Command name typed in Rhino: MejzlikEdgeCut
    /// </summary>
    public class MejzlikEdgeCutCommand : Command
    {
        public override string EnglishName => "MejzlikEdgeCut";

        private const string LayerLeSurface = "CUT_LE_SURFACE";
        private const string LayerTeSurface = "CUT_TE_SURFACE";
        private const string LayerSparSurface = "CUT_SPAR_SURFACE";
        private const string LayerLeAreas = "CUT_LE_AREAS";
        private const string LayerTeAreas = "CUT_TE_AREAS";
        private const string LayerCutLines = "CUT_LINES";
        private const string LayerEdges = "DERIVED_EDGES";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var plugin = MejzlikEdgeCutPlugIn.Instance;
            RhinoApp.WriteLine(MejzlikEdgeCutPlugIn.Banner);

            // ---- pick the blade -------------------------------------------
            var go = new GetObject();
            go.SetCommandPrompt("Select blade solid");
            go.GeometryFilter = ObjectType.Brep;
            go.SubObjectSelect = false;
            go.Get();
            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            var brep = go.Object(0).Brep();
            if (brep == null)
            {
                RhinoApp.WriteLine("Selected object is not a brep.");
                return Result.Failure;
            }

            var opt = new EdgeCutOptions
            {
                NStations = plugin.NStations,
                StartFraction = plugin.StartFraction,
                EndFraction = plugin.EndFraction,
                SwapLeTe = plugin.SwapLeTe,
                Datum = plugin.Datum,
                ExtensionFraction = plugin.ExtensionFraction,
                Basis = plugin.Basis,
                Direction = plugin.Direction,
                TransitionROverR = plugin.TransitionROverR,
                TransitionWidth = plugin.TransitionWidth,
                WorldZInboard = plugin.WorldZInboard,
                LeDistribution = plugin.LeDistribution,
                TeDistribution = plugin.TeDistribution,
                SparDistribution = plugin.SparDistribution,
                Tolerance = doc.ModelAbsoluteTolerance
            };

            bool doLe = plugin.DoLe;
            bool doTe = plugin.DoTe;
            bool doSpar = plugin.DoSpar;
            bool bakeAreas = plugin.BakeAreaRegions;
            bool bakeLines = plugin.BakeCutLines;
            bool bakeEdges = plugin.BakeEdgeCurves;

            string unit = doc.GetUnitSystemName(false, false, true, true);

            // ---- main option loop -----------------------------------------
            var optStations = new OptionInteger(opt.NStations, 2, 500);
            var optRoot = new OptionDouble(opt.StartFraction, 0.0, 0.99);
            var optTip = new OptionDouble(opt.EndFraction, 0.01, 1.0);
            var optExt = new OptionDouble(opt.ExtensionFraction, 0.0, 5.0);
            var optTrans = new OptionDouble(opt.TransitionROverR, 0.0, 1.0);
            var optBand = new OptionDouble(opt.TransitionWidth, 0.0, 1.0);
            var optZIn = new OptionToggle(opt.WorldZInboard, "Outboard", "Inboard");
            var optSwap = new OptionToggle(opt.SwapLeTe, "No", "Yes");
            var optLe = new OptionToggle(doLe, "Off", "On");
            var optTe = new OptionToggle(doTe, "Off", "On");
            var optSpar = new OptionToggle(doSpar, "Off", "On");
            var optAreas = new OptionToggle(bakeAreas, "Off", "On");
            var optLines = new OptionToggle(bakeLines, "Off", "On");
            var optEdges = new OptionToggle(bakeEdges, "Off", "On");

            while (true)
            {
                RhinoApp.WriteLine("  LE area: {0}", opt.LeDistribution.Describe());
                RhinoApp.WriteLine("  TE area: {0}", opt.TeDistribution.Describe());
                RhinoApp.WriteLine("  Spar x/c from LE: {0}", opt.SparDistribution.Describe());
                RhinoApp.WriteLine("  Cut lines run: " + DescribeDirection(opt));
                RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  Extension: {0:0.###} of the {1} at each end",
                    optExt.CurrentValue,
                    opt.Basis == ExtensionBasis.LocalChord ? "local chord" : "cut line length"));

                var gp = new GetOption();
                gp.SetCommandPrompt("Set the distributions, then Run");
                gp.AcceptNothing(true);

                int iRun = gp.AddOption("Run");
                int iLeDist = gp.AddOption("LeAreaDistribution");
                int iTeDist = gp.AddOption("TeAreaDistribution");
                int iSparDist = gp.AddOption("SparChordDistribution");
                int iCopy = gp.AddOption("CopyLeToTe");
                int iDatum = gp.AddOption("RadiusDatum");
                int iBasis = gp.AddOption("ExtensionBasis");
                int iDir = gp.AddOption("CutDirection");
                gp.AddOptionDouble("TransitionRR", ref optTrans,
                    "r/R where the cut direction changes over (Mixed only)");
                gp.AddOptionDouble("TransitionBand", ref optBand,
                    "Width in r/R over which the direction rotates; 0 = hard switch");
                gp.AddOptionToggle("WorldZSide", ref optZIn);
                gp.AddOptionInteger("Stations", ref optStations, "Number of spanwise stations");
                gp.AddOptionDouble("RootFraction", ref optRoot, "Fraction of span skipped at the root");
                gp.AddOptionDouble("TipFraction", ref optTip, "Fraction of span where stations stop");
                gp.AddOptionDouble("Extension", ref optExt,
                    "Overhang at each end of every cut line, as a fraction of the basis");
                gp.AddOptionToggle("SwapLeTe", ref optSwap);
                gp.AddOptionToggle("LeadingEdge", ref optLe);
                gp.AddOptionToggle("TrailingEdge", ref optTe);
                gp.AddOptionToggle("Spar", ref optSpar);
                gp.AddOptionToggle("BakeCutLines", ref optLines);
                gp.AddOptionToggle("BakeAreaRegions", ref optAreas);
                gp.AddOptionToggle("BakeEdgeCurves", ref optEdges);

                var gr = gp.Get();

                if (gr == GetResult.Nothing) break;          // Enter = run
                if (gr == GetResult.Cancel) return Result.Cancel;
                if (gr != GetResult.Option) continue;

                int idx = gp.OptionIndex();
                if (idx == iRun) break;
                if (idx == iLeDist)
                {
                    var d = EditDistribution("LE", opt.LeDistribution,
                                             string.Format("area ({0}\u00B2)", unit),
                                             1e-9, double.MaxValue);
                    if (d != null) opt.LeDistribution = d;
                }
                else if (idx == iTeDist)
                {
                    var d = EditDistribution("TE", opt.TeDistribution,
                                             string.Format("area ({0}\u00B2)", unit),
                                             1e-9, double.MaxValue);
                    if (d != null) opt.TeDistribution = d;
                }
                else if (idx == iSparDist)
                {
                    var d = EditDistribution("Spar", opt.SparDistribution,
                                             "chord fraction x/c from the LE",
                                             0.001, 0.999);
                    if (d != null) opt.SparDistribution = d;
                }
                else if (idx == iCopy)
                {
                    opt.TeDistribution = AreaDistribution.Deserialize(
                        opt.LeDistribution.Serialize(), opt.TeDistribution);
                    RhinoApp.WriteLine("TE distribution copied from LE.");
                }
                else if (idx == iDatum)
                {
                    opt.Datum = AskDatum(opt.Datum);
                }
                else if (idx == iBasis)
                {
                    opt.Basis = AskBasis(opt.Basis);
                }
                else if (idx == iDir)
                {
                    opt.Direction = AskDirection(opt.Direction);
                }
            }

            opt.NStations = optStations.CurrentValue;
            opt.StartFraction = optRoot.CurrentValue;
            opt.EndFraction = optTip.CurrentValue;
            opt.ExtensionFraction = optExt.CurrentValue;
            opt.TransitionROverR = optTrans.CurrentValue;
            opt.TransitionWidth = optBand.CurrentValue;
            opt.WorldZInboard = optZIn.CurrentValue;
            opt.SwapLeTe = optSwap.CurrentValue;
            doLe = optLe.CurrentValue;
            doTe = optTe.CurrentValue;
            doSpar = optSpar.CurrentValue;
            bakeLines = optLines.CurrentValue;
            bakeAreas = optAreas.CurrentValue;
            bakeEdges = optEdges.CurrentValue;

            if (opt.EndFraction <= opt.StartFraction)
            {
                RhinoApp.WriteLine("TipFraction must be greater than RootFraction.");
                return Result.Failure;
            }
            if (!doLe && !doTe && !doSpar)
            {
                RhinoApp.WriteLine("LE, TE and Spar are all switched off. Nothing to do.");
                return Result.Nothing;
            }

            string problem;
            if (doLe && !opt.LeDistribution.IsPositive(out problem))
            {
                RhinoApp.WriteLine("LE distribution is invalid: {0}", problem);
                return Result.Failure;
            }
            if (doTe && !opt.TeDistribution.IsPositive(out problem))
            {
                RhinoApp.WriteLine("TE distribution is invalid: {0}", problem);
                return Result.Failure;
            }
            if (doSpar && !opt.SparDistribution.IsWithin(0.001, 0.999, out problem))
            {
                RhinoApp.WriteLine("Spar distribution is invalid: {0}", problem);
                return Result.Failure;
            }

            // persist for next run
            plugin.NStations = opt.NStations;
            plugin.StartFraction = opt.StartFraction;
            plugin.EndFraction = opt.EndFraction;
            plugin.SwapLeTe = opt.SwapLeTe;
            plugin.Datum = opt.Datum;
            plugin.ExtensionFraction = opt.ExtensionFraction;
            plugin.Basis = opt.Basis;
            plugin.Direction = opt.Direction;
            plugin.TransitionROverR = opt.TransitionROverR;
            plugin.TransitionWidth = opt.TransitionWidth;
            plugin.WorldZInboard = opt.WorldZInboard;
            plugin.LeDistribution = opt.LeDistribution;
            plugin.TeDistribution = opt.TeDistribution;
            plugin.SparDistribution = opt.SparDistribution;
            plugin.DoLe = doLe;
            plugin.DoTe = doTe;
            plugin.DoSpar = doSpar;
            plugin.BakeCutLines = bakeLines;
            plugin.BakeAreaRegions = bakeAreas;
            plugin.BakeEdgeCurves = bakeEdges;

            // ---- derive the LE / TE lines ---------------------------------
            List<Point3d> lePts, tePts;
            Curve leCrv, teCrv;
            RadialFrame frame;
            if (!EdgeCutCore.ExtractEdges(brep, opt, out lePts, out tePts,
                                          out leCrv, out teCrv, out frame))
            {
                RhinoApp.WriteLine("Could not derive LE/TE curves. Check the span axis " +
                                   "(is the blade aligned to a world axis?) and the station range.");
                return Result.Failure;
            }

            RhinoApp.WriteLine("Derived {0} LE points and {1} TE points.", lePts.Count, tePts.Count);
            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Span axis {0}. r measured from {1}; R = {2:0.###} {3}.",
                frame.Axis,
                opt.Datum == RadiusDatum.WorldOrigin ? "the world origin" : "the blade root",
                frame.TipRadius, unit));
            if (opt.Datum == RadiusDatum.WorldOrigin && Math.Abs(frame.SpanMin) < 1e-9)
                RhinoApp.WriteLine("  Note: the blade root sits at the world origin, so r/R " +
                                   "equals the span fraction. If the hub axis is elsewhere, " +
                                   "move the blade or switch RadiusDatum.");

            if (bakeEdges)
            {
                int li = EnsureLayer(doc, LayerEdges);
                var att = new ObjectAttributes { LayerIndex = li };
                doc.Objects.AddCurve(leCrv, att);
                doc.Objects.AddCurve(teCrv, att);
            }

            if (doLe)
            {
                var res = EdgeCutCore.BuildEdge(brep, leCrv, lePts, opt, frame, opt.LeDistribution);
                Report("LE", opt.LeDistribution, res);
                Bake(doc, res, LayerLeSurface, LayerLeAreas, bakeLines, bakeAreas);
            }

            if (doTe)
            {
                var res = EdgeCutCore.BuildEdge(brep, teCrv, tePts, opt, frame, opt.TeDistribution);
                Report("TE", opt.TeDistribution, res);
                Bake(doc, res, LayerTeSurface, LayerTeAreas, bakeLines, bakeAreas);
            }

            if (doSpar)
            {
                var res = EdgeCutCore.BuildSpar(brep, opt, frame, opt.SparDistribution);
                ReportSpar(opt.SparDistribution, res);
                Bake(doc, res, LayerSparSurface, null, bakeLines, false);
            }

            doc.Views.Redraw();
            return Result.Success;
        }

        // =================================================================
        // distribution editing
        // =================================================================
        private static AreaDistribution EditDistribution(string label,
                                                         AreaDistribution current,
                                                         string valueLabel,
                                                         double minValue,
                                                         double maxValue)
        {
            var gk = new GetOption();
            gk.SetCommandPrompt(string.Format("{0} distribution (now: {1})",
                                              label, current.Describe()));
            int iConst = gk.AddOption("Constant");
            int iLin = gk.AddOption("Linear");
            int iPow = gk.AddOption("Power");
            int iTab = gk.AddOption("Table");

            if (gk.Get() != GetResult.Option) return null;
            int idx = gk.OptionIndex();

            var d = new AreaDistribution
            {
                RootValue = current.RootValue,
                TipValue = current.TipValue,
                Exponent = current.Exponent,
                Points = current.Points
            };

            if (idx == iConst)
            {
                d.Kind = DistributionKind.Constant;
                double v = current.RootValue;
                if (RhinoGet.GetNumber(string.Format("{0} {1}", label, valueLabel),
                                       false, ref v, minValue, maxValue) != Result.Success)
                    return null;
                d.RootValue = v;
                d.TipValue = v;
                return d;
            }

            if (idx == iLin || idx == iPow)
            {
                d.Kind = (idx == iLin) ? DistributionKind.Linear : DistributionKind.Power;

                double root = current.RootValue;
                if (RhinoGet.GetNumber(string.Format("{0} {1} at r/R = 0", label, valueLabel),
                                       false, ref root, minValue, maxValue) != Result.Success)
                    return null;

                double tip = current.TipValue;
                if (RhinoGet.GetNumber(string.Format("{0} {1} at r/R = 1", label, valueLabel),
                                       false, ref tip, minValue, maxValue) != Result.Success)
                    return null;

                d.RootValue = root;
                d.TipValue = tip;

                if (d.Kind == DistributionKind.Power)
                {
                    double e = current.Exponent <= 0.0 ? 2.0 : current.Exponent;
                    if (RhinoGet.GetNumber("Exponent (1 = linear, higher holds the root value further out)",
                                           false, ref e, 0.05, 20.0) != Result.Success)
                        return null;
                    d.Exponent = e;
                }
                return d;
            }

            if (idx == iTab)
            {
                d.Kind = DistributionKind.Table;
                string seed = current.Points != null && current.Points.Count > 0
                    ? current.TableToString()
                    : string.Format(CultureInfo.InvariantCulture, "0.2:{0:0.####}, 1.0:{1:0.####}",
                                    current.RootValue, current.RootValue * 0.4);

                RhinoApp.WriteLine(string.Format(
                    "Enter control points as rR:value, separated by commas. Value = {0}.",
                    valueLabel));
                RhinoApp.WriteLine(maxValue <= 1.0
                    ? "Example: 0.2:0.35, 0.6:0.30, 1.0:0.25"
                    : "Example: 0.2:12.0, 0.5:9.5, 0.8:6.0, 1.0:3.5");
                RhinoApp.WriteLine("Use a dot as the decimal separator. Values are held flat " +
                                   "outside the first and last point.");

                string text = seed;
                if (RhinoGet.GetString("Control points", false, ref text) != Result.Success)
                    return null;

                List<KeyValuePair<double, double>> pts;
                string err;
                if (!AreaDistribution.TryParseTable(text, maxValue, out pts, out err))
                {
                    RhinoApp.WriteLine("Could not read the table: {0}", err);
                    return null;
                }
                d.Points = pts;
                return d;
            }

            return null;
        }

        private static ExtensionBasis AskBasis(ExtensionBasis current)
        {
            var g = new GetOption();
            g.SetCommandPrompt(string.Format(
                "Extension percentage is measured against what? (now: {0})", current));
            int iChord = g.AddOption("LocalChord");
            int iThick = g.AddOption("CutLineLength");
            if (g.Get() != GetResult.Option) return current;
            int idx = g.OptionIndex();
            if (idx == iChord) return ExtensionBasis.LocalChord;
            if (idx == iThick) return ExtensionBasis.CutLineLength;
            return current;
        }

        private static string DescribeDirection(EdgeCutOptions opt)
        {
            if (opt.Direction == CutDirection.WorldZ)
                return "parallel to world Z everywhere";
            if (opt.Direction == CutDirection.PerpendicularToChord)
                return "perpendicular to the local chord everywhere";

            string zSide = opt.WorldZInboard ? "inboard" : "outboard";
            string pSide = opt.WorldZInboard ? "outboard" : "inboard";
            return string.Format(CultureInfo.InvariantCulture,
                "world Z {0} of r/R {1:0.###}, chord-perpendicular {2}, blended over {3:0.###}",
                zSide, opt.TransitionROverR, pSide, opt.TransitionWidth);
        }

        private static CutDirection AskDirection(CutDirection current)
        {
            var g = new GetOption();
            g.SetCommandPrompt(string.Format(
                "Which way do the cut lines run? (now: {0})", current));
            int iZ = g.AddOption("WorldZ");
            int iPerp = g.AddOption("PerpendicularToChord");
            int iMix = g.AddOption("Mixed");
            if (g.Get() != GetResult.Option) return current;
            int idx = g.OptionIndex();
            if (idx == iZ) return CutDirection.WorldZ;
            if (idx == iPerp) return CutDirection.PerpendicularToChord;
            if (idx == iMix) return CutDirection.Mixed;
            return current;
        }

        private static RadiusDatum AskDatum(RadiusDatum current)
        {
            var g = new GetOption();
            g.SetCommandPrompt(string.Format("Where is r = 0? (now: {0})", current));
            int iWorld = g.AddOption("WorldOrigin");
            int iRoot = g.AddOption("BladeRoot");
            if (g.Get() != GetResult.Option) return current;
            int idx = g.OptionIndex();
            if (idx == iWorld) return RadiusDatum.WorldOrigin;
            if (idx == iRoot) return RadiusDatum.BladeRoot;
            return current;
        }

        // =================================================================
        // document side
        // =================================================================
        private static int EnsureLayer(RhinoDoc doc, string name)
        {
            int idx = doc.Layers.FindByFullPath(name, -1);
            if (idx >= 0) return idx;
            var layer = new Layer { Name = name };
            return doc.Layers.Add(layer);
        }

        private static void Report(string label, AreaDistribution dist, EdgeResult res)
        {
            int ok = 0;
            foreach (var s in res.Stations) if (s.Ok) ok++;

            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "--- {0}: {1}/{2} stations solved, {3}",
                label, ok, res.Stations.Count, dist.Describe()));

            foreach (var s in res.Stations)
            {
                if (s.Ok)
                    RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "    st {0:00}  r/R={1:0.000}  target={2:0.000000}  got={3:0.000000}  d={4:0.0000}",
                        s.Index, s.ROverR, s.TargetArea, s.Area, s.Offset));
                else
                    RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "    st {0:00}  r/R={1:0.000}  SKIPPED: {2}",
                        s.Index, s.ROverR, s.Message));
            }
        }

        private static void ReportSpar(AreaDistribution dist, EdgeResult res)
        {
            int ok = 0;
            foreach (var s in res.Stations) if (s.Ok) ok++;

            RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "--- SPAR: {0}/{1} stations solved, x/c from LE = {2}",
                ok, res.Stations.Count, dist.Describe()));

            foreach (var s in res.Stations)
            {
                if (s.Ok)
                    RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "    st {0:00}  r/R={1:0.000}  x/c={2:0.0000}  chord={3:0.000}  x={4:0.000}",
                        s.Index, s.ROverR, s.ChordFraction, s.ChordLength, s.Offset));
                else
                    RhinoApp.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "    st {0:00}  r/R={1:0.000}  SKIPPED: {2}",
                        s.Index, s.ROverR, s.Message));
            }
        }

        private static void Bake(RhinoDoc doc, EdgeResult res, string surfaceLayer,
                                 string areaLayer, bool bakeLines, bool bakeAreas)
        {
            if (bakeLines && res.CutLines.Count > 0)
            {
                int li = EnsureLayer(doc, LayerCutLines);
                var att = new ObjectAttributes { LayerIndex = li };
                foreach (var ln in res.CutLines)
                    doc.Objects.AddLine(ln, att);
            }

            // The closed nose regions the bisection measured against. Baked as
            // planar surfaces so _Area on any one of them reproduces the number
            // in the report.
            if (bakeAreas && areaLayer != null && res.AreaRegions.Count > 0)
            {
                int ai = EnsureLayer(doc, areaLayer);
                var att = new ObjectAttributes { LayerIndex = ai };
                foreach (var reg in res.AreaRegions)
                {
                    if (reg == null) continue;
                    var planar = Brep.CreatePlanarBreps(reg, doc.ModelAbsoluteTolerance);
                    if (planar != null && planar.Length > 0)
                    {
                        foreach (var b in planar)
                            doc.Objects.AddBrep(b, att);
                    }
                    else
                    {
                        // Fall back to the boundary curve if the region will not
                        // cap, so the check geometry is never silently lost.
                        doc.Objects.AddCurve(reg, att);
                    }
                }
            }

            if (res.LoftedSurfaces == null || res.LoftedSurfaces.Length == 0)
            {
                RhinoApp.WriteLine("Loft produced nothing for layer {0}.", surfaceLayer);
                return;
            }

            int ls = EnsureLayer(doc, surfaceLayer);
            var satt = new ObjectAttributes { LayerIndex = ls };
            foreach (var b in res.LoftedSurfaces)
                doc.Objects.AddBrep(b, satt);
        }
    }
}
