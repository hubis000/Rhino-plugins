using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace MejzlikEdgeCut
{
    public enum RadiusDatum
    {
        /// <summary>r is measured from the world origin along the span axis.
        /// Correct when the blade sits in its as-mounted position with the
        /// rotation axis through the world origin.</summary>
        WorldOrigin,

        /// <summary>r is measured from the blade's own root end. Use only if
        /// the blade is modelled away from the hub axis; r/R then means
        /// "fraction of the modelled blade length", not true radius ratio.</summary>
        BladeRoot
    }

    public enum ExtensionBasis
    {
        /// <summary>The percentage is of the local chord. A blade is far wider than
        /// it is thick, so this gives a visible overhang past the upper and lower
        /// surfaces. Use this when you want the cutting surface to stand clear.</summary>
        LocalChord,

        /// <summary>The percentage is of the cut line's own length, which equals the
        /// local blade thickness. Gives a small, thickness-proportional overshoot.</summary>
        CutLineLength
    }

    public enum CutDirection
    {
        /// <summary>World Z projected into the section plane everywhere.</summary>
        WorldZ,

        /// <summary>Perpendicular to the local chord everywhere, so the cut tilts
        /// with the blade's twist.</summary>
        PerpendicularToChord,

        /// <summary>Both: chord-perpendicular over the aerodynamic part of the blade
        /// and parallel to world Z over the other part, switching at
        /// TransitionROverR. Which side gets Z is set by WorldZInboard.</summary>
        Mixed
    }

    public class EdgeCutOptions
    {
        public int NStations = 20;
        public double StartFraction = 0.15;
        public double EndFraction = 0.97;
        public bool SwapLeTe = false;
        public Vector3d SpanAxisOverride = Vector3d.Unset;

        /// <summary>Target area as a function of r/R, leading edge.</summary>
        public AreaDistribution LeDistribution = AreaDistribution.ConstantAt(1.0);

        /// <summary>Target area as a function of r/R, trailing edge.
        /// Independent of the LE distribution.</summary>
        public AreaDistribution TeDistribution = AreaDistribution.ConstantAt(1.0);

        /// <summary>Spar position as a chord fraction x/c measured FROM THE LEADING
        /// EDGE, as a function of r/R. Same distribution forms as the edges, but the
        /// values are fractions in (0,1), not areas.</summary>
        public AreaDistribution SparDistribution = AreaDistribution.ConstantAt(0.30);

        public RadiusDatum Datum = RadiusDatum.WorldOrigin;

        /// <summary>Each cut line is lengthened by this fraction at BOTH ends, so the
        /// lofted surface stands clear of the upper and lower blade surfaces and
        /// trims cleanly. What the fraction is measured against is set by
        /// ExtensionBasis. 0.10 = 10% at each end.</summary>
        public double ExtensionFraction = 0.10;

        public ExtensionBasis Basis = ExtensionBasis.LocalChord;

        /// <summary>Direction of the cut line within each section plane.</summary>
        public CutDirection Direction = CutDirection.Mixed;

        /// <summary>r/R where the cut direction changes over, for Mixed.</summary>
        public double TransitionROverR = 0.25;

        /// <summary>For Mixed: true means world Z INBOARD of the transition and
        /// chord-perpendicular outboard, which is the usual case — the aerodynamic
        /// part of the blade keeps the chord-normal cut and the root end goes
        /// vertical. False swaps the two sides.</summary>
        public bool WorldZInboard = true;

        /// <summary>Width in r/R over which Mixed rotates from one direction to the
        /// other, centred on TransitionROverR. 0 gives a hard switch, which leaves a
        /// kink in the lofted surface; a small band blends it out.</summary>
        public double TransitionWidth = 0.10;

        public double AreaTolFraction = 1e-4;
        public int MaxBisectionIterations = 60;
        public double Tolerance = 0.001;
    }

    /// <summary>Span axis and the radial extents needed to turn a point into r/R.
    /// Built once per run so LE and TE share the same normalisation.</summary>
    public class RadialFrame
    {
        public Vector3d Axis;
        public double SpanMin;   // span coordinate of the blade root end
        public double SpanMax;   // span coordinate of the blade tip
        public double RadiusOrigin;  // span coordinate where r = 0
        public double TipRadius;     // R

        public double ROverR(Point3d p)
        {
            if (TipRadius <= 1e-12) return 0.0;
            double s = new Vector3d(p) * Axis;
            return (s - RadiusOrigin) / TipRadius;
        }
    }

    public class StationResult
    {
        public int Index;
        public bool Ok;
        public string Message = "";
        public double ROverR;      // radial position used to evaluate the distribution
        public double TargetArea;  // target at this station (edges)
        public double ChordFraction; // x/c from the LE (spar)
        public double ChordLength;   // local chord, for the spar report
        public double Offset;      // solved distance d from the edge point
        public double Area;        // achieved nose area
        public Line CutLine;

        /// <summary>The closed nose region the area was measured on. Kept so it
        /// can be baked for checking.</summary>
        public Curve AreaRegion;
    }

    public class EdgeResult
    {
        public Curve EdgeCurve;
        public List<Point3d> EdgePoints = new List<Point3d>();
        public List<StationResult> Stations = new List<StationResult>();
        public List<Line> CutLines = new List<Line>();

        /// <summary>The closed nose regions, one per solved station, in the order
        /// they were solved. Empty for the spar, which measures no area.</summary>
        public List<Curve> AreaRegions = new List<Curve>();

        public Brep[] LoftedSurfaces;
    }

    /// <summary>
    /// All geometry. No Rhino document access, no UI, so it can be unit tested
    /// or driven from Grasshopper later.
    /// </summary>
    public static class EdgeCutCore
    {
        // =================================================================
        // span axis
        // =================================================================
        public static Vector3d SpanAxisFromBoundingBox(Brep brep, out BoundingBox bbox)
        {
            bbox = brep.GetBoundingBox(true);
            Vector3d d = bbox.Diagonal;
            double[] dims = { Math.Abs(d.X), Math.Abs(d.Y), Math.Abs(d.Z) };
            int i = 0;
            if (dims[1] > dims[i]) i = 1;
            if (dims[2] > dims[i]) i = 2;
            if (i == 0) return Vector3d.XAxis;
            if (i == 1) return Vector3d.YAxis;
            return Vector3d.ZAxis;
        }

        // =================================================================
        // sectioning
        // =================================================================
        public static List<Curve> SectionCurves(Brep brep, Plane plane, double tol)
        {
            var result = new List<Curve>();
            Curve[] crvs;
            Point3d[] pts;
            if (!Intersection.BrepPlane(brep, plane, tol, out crvs, out pts))
                return result;
            if (crvs == null || crvs.Length == 0)
                return result;

            var joined = Curve.JoinCurves(crvs, tol);
            if (joined == null) return result;
            foreach (var c in joined)
            {
                if (c != null && c.IsClosed && c.IsPlanar(tol))
                    result.Add(c);
            }
            return result;
        }

        /// <summary>Largest-area curve. Discards small slivers from fillets etc.</summary>
        public static Curve Biggest(List<Curve> crvs)
        {
            Curve best = null;
            double bestArea = -1.0;
            foreach (var c in crvs)
            {
                var amp = AreaMassProperties.Compute(c);
                if (amp != null && amp.Area > bestArea)
                {
                    best = c;
                    bestArea = amp.Area;
                }
            }
            return best;
        }

        public static List<Point3d> Sample(Curve crv, int n)
        {
            var pts = new List<Point3d>();
            var ts = crv.DivideByCount(n, true);
            if (ts == null) return pts;
            foreach (var t in ts) pts.Add(crv.PointAt(t));
            return pts;
        }

        /// <summary>Chord = longest point-to-point distance on the section.</summary>
        public static bool ChordEndpoints(Curve crv, out Point3d a, out Point3d b)
        {
            a = Point3d.Unset;
            b = Point3d.Unset;
            var pts = Sample(crv, 250);
            if (pts.Count < 3) return false;

            double best = -1.0;
            for (int i = 0; i < pts.Count; i++)
            {
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double d = pts[i].DistanceTo(pts[j]);
                    if (d > best) { best = d; a = pts[i]; b = pts[j]; }
                }
            }
            return best > 0.0;
        }

        /// <summary>Thickness perpendicular to the chord, at a fraction of the chord.
        /// Only used for the LE-vs-TE bluntness heuristic.</summary>
        private static double ThicknessAt(Curve crv, Plane plane, Point3d origin,
                                          Vector3d chordDir, double chordLen,
                                          double fraction, double tol)
        {
            Point3d at = origin + chordDir * (chordLen * fraction);
            Vector3d perp = Vector3d.CrossProduct(plane.Normal, chordDir);
            if (!perp.Unitize()) return 0.0;

            double big = chordLen * 2.0;
            var probe = new LineCurve(at - perp * big, at + perp * big);
            var ev = Intersection.CurveCurve(crv, probe, tol, tol);
            if (ev == null || ev.Count < 2) return 0.0;

            var ps = new List<Point3d>();
            for (int i = 0; i < ev.Count; i++) ps.Add(ev[i].PointA);

            double m = 0.0;
            for (int i = 0; i < ps.Count; i++)
                for (int j = i + 1; j < ps.Count; j++)
                    m = Math.Max(m, ps[i].DistanceTo(ps[j]));
            return m;
        }

        /// <summary>Blunter chord end is taken as the leading edge.</summary>
        public static bool GuessLeTe(Curve crv, Plane plane, bool swap, double tol,
                                     out Point3d le, out Point3d te)
        {
            le = Point3d.Unset;
            te = Point3d.Unset;

            Point3d a, b;
            if (!ChordEndpoints(crv, out a, out b)) return false;

            Vector3d ab = b - a;
            double len = ab.Length;
            if (len < tol) return false;

            Vector3d dAb = ab; dAb.Unitize();
            Vector3d dBa = -ab; dBa.Unitize();

            double tA = ThicknessAt(crv, plane, a, dAb, len, 0.10, tol);
            double tB = ThicknessAt(crv, plane, b, dBa, len, 0.10, tol);

            if (tA >= tB) { le = a; te = b; }
            else { le = b; te = a; }

            if (swap) { var tmp = le; le = te; te = tmp; }
            return true;
        }

        // =================================================================
        // nose region and the constant-area solve
        // =================================================================
        /// <summary>Direction the cut line runs, inside the section plane.
        ///
        /// For Mixed, the direction rotates from world Z to the chord perpendicular
        /// across a band of width TransitionWidth centred on TransitionROverR, so the
        /// lofted surface has no kink where the two regimes meet.
        ///
        /// World Z is used only where it can actually work: if Z is nearly normal to
        /// the section plane, or its projection lands nearly along the chord, that
        /// station falls back to the chord perpendicular.</summary>
        public static Vector3d ProbeDirection(EdgeCutOptions opt, Plane plane,
                                              Vector3d chordDir, double rOverR)
        {
            Vector3d perp = Vector3d.CrossProduct(plane.Normal, chordDir);
            perp.Unitize();

            if (opt.Direction == CutDirection.PerpendicularToChord)
                return perp;

            // World Z projected into the section plane.
            Vector3d n = plane.Normal;
            n.Unitize();
            Vector3d z = Vector3d.ZAxis;
            Vector3d zInPlane = z - n * (z * n);

            bool zUsable = zInPlane.Length >= 1e-6 && zInPlane.Unitize()
                           && Math.Abs(zInPlane * chordDir) <= 0.95;
            if (!zUsable) return perp;

            if (opt.Direction == CutDirection.WorldZ)
                return zInPlane;

            // --- Mixed ---------------------------------------------------
            // w = 1 means fully world Z, 0 means fully chord-perpendicular.
            double w = MixWeight(opt, rOverR);
            if (w >= 1.0) return zInPlane;
            if (w <= 0.0) return perp;

            // Line direction is unsigned, so align the two before blending or the
            // interpolation can collapse to zero.
            Vector3d p = perp;
            if (p * zInPlane < 0.0) p = -p;

            Vector3d mixed = zInPlane * w + p * (1.0 - w);
            if (!mixed.Unitize()) return perp;
            return mixed;
        }

        /// <summary>Fraction of world Z in the blend at this radius: 1 fully Z,
        /// 0 fully chord-perpendicular.</summary>
        public static double MixWeight(EdgeCutOptions opt, double rOverR)
        {
            double x0 = opt.TransitionROverR;
            double half = Math.Max(0.0, opt.TransitionWidth) * 0.5;

            double t;   // 0 inboard of the band, 1 outboard of it
            if (half <= 1e-9)
            {
                t = rOverR < x0 ? 0.0 : 1.0;
            }
            else
            {
                t = (rOverR - (x0 - half)) / (2.0 * half);
                t = Math.Max(0.0, Math.Min(1.0, t));
                t = t * t * (3.0 - 2.0 * t);   // smoothstep, so the surface has no kink
            }

            // t is "how far outboard". Z inboard means Z weight falls with t.
            return opt.WorldZInboard ? (1.0 - t) : t;
        }

        private static LineCurve CutProbe(Plane plane, Point3d atPt, Vector3d probeDir,
                                          double chordLen)
        {
            Vector3d dir = probeDir;
            if (!dir.Unitize()) return null;
            double big = chordLen * 2.0;
            return new LineCurve(atPt - dir * big, atPt + dir * big);
        }

        /// <summary>Closed nose region cut off at distance d, and the two points
        /// where the cut crosses the section.</summary>
        public static Curve NoseRegion(Curve sec, Plane plane, Point3d edgePt,
                                       Vector3d chordDir, Vector3d probeDir,
                                       double chordLen, double d,
                                       double tol, out Point3d p0, out Point3d p1)
        {
            p0 = Point3d.Unset;
            p1 = Point3d.Unset;

            // Placed at distance d ALONG THE CHORD from the edge point, but running
            // in probeDir, which may be world Z rather than the chord perpendicular.
            var probe = CutProbe(plane, edgePt + chordDir * d, probeDir, chordLen);
            if (probe == null) return null;

            var ev = Intersection.CurveCurve(sec, probe, tol, tol);
            if (ev == null || ev.Count < 2) return null;

            // Furthest-apart pair, so a tangential graze does not win.
            double best = -1.0, t0 = 0.0, t1 = 0.0;
            for (int i = 0; i < ev.Count; i++)
            {
                for (int j = i + 1; j < ev.Count; j++)
                {
                    double dd = ev[i].PointA.DistanceTo(ev[j].PointA);
                    if (dd > best)
                    {
                        best = dd;
                        p0 = ev[i].PointA; t0 = ev[i].ParameterA;
                        p1 = ev[j].PointA; t1 = ev[j].ParameterA;
                    }
                }
            }
            if (best <= 0.0) return null;

            var pieces = sec.Split(new[] { t0, t1 });
            if (pieces == null || pieces.Length < 2) return null;

            // Nose piece = the split whose midpoint sits nearest the edge point.
            Curve nose = null;
            double nearest = double.MaxValue;
            foreach (var p in pieces)
            {
                Point3d mid = p.PointAtNormalizedLength(0.5);
                double dd = mid.DistanceTo(edgePt);
                if (dd < nearest) { nearest = dd; nose = p; }
            }
            if (nose == null) return null;

            var closer = new LineCurve(nose.PointAtEnd, nose.PointAtStart);
            var joined = Curve.JoinCurves(new Curve[] { nose, closer }, tol);
            if (joined == null || joined.Length != 1 || !joined[0].IsClosed) return null;

            return joined[0];
        }

        private static double? NoseArea(Curve sec, Plane plane, Point3d edgePt,
                                        Vector3d chordDir, Vector3d probeDir,
                                        double chordLen, double d, double tol)
        {
            Point3d p0, p1;
            var reg = NoseRegion(sec, plane, edgePt, chordDir, probeDir, chordLen, d,
                                 tol, out p0, out p1);
            if (reg == null) return null;
            var amp = AreaMassProperties.Compute(reg);
            if (amp == null) return null;
            return amp.Area;
        }

        /// <summary>Bisection on the cut offset so the nose area equals the target.
        /// Assumes area grows monotonically with the offset.</summary>
        public static bool SolveOffsetForArea(Curve sec, Plane plane, Point3d edgePt,
                                              Vector3d chordDir, Vector3d probeDir,
                                              double chordLen,
                                              double target, EdgeCutOptions opt,
                                              out double offset, out double area)
        {
            offset = 0.0;
            area = 0.0;

            double lo = 0.0;
            double hi = chordLen * 0.98;

            var aHi = NoseArea(sec, plane, edgePt, chordDir, probeDir, chordLen, hi, opt.Tolerance);
            if (!aHi.HasValue || aHi.Value < target)
                return false;   // target unreachable in this section

            double atol = target * opt.AreaTolFraction;

            for (int k = 0; k < opt.MaxBisectionIterations; k++)
            {
                double mid = 0.5 * (lo + hi);
                var a = NoseArea(sec, plane, edgePt, chordDir, probeDir, chordLen, mid, opt.Tolerance);
                if (!a.HasValue) { lo = mid; continue; }   // degenerate near the nose

                if (Math.Abs(a.Value - target) <= atol)
                {
                    offset = mid;
                    area = a.Value;
                    return true;
                }
                if (a.Value < target) lo = mid; else hi = mid;
            }

            offset = 0.5 * (lo + hi);
            var final = NoseArea(sec, plane, edgePt, chordDir, probeDir, chordLen, offset, opt.Tolerance);
            if (!final.HasValue) return false;
            area = final.Value;
            return true;
        }

        // =================================================================
        // edge curve extraction
        // =================================================================
        public static bool ExtractEdges(Brep brep, EdgeCutOptions opt,
                                        out List<Point3d> lePts, out List<Point3d> tePts,
                                        out Curve leCrv, out Curve teCrv,
                                        out RadialFrame frame)
        {
            lePts = new List<Point3d>();
            tePts = new List<Point3d>();
            leCrv = null;
            teCrv = null;
            frame = null;

            BoundingBox bbox;
            Vector3d axis;
            if (opt.SpanAxisOverride.IsValid && !opt.SpanAxisOverride.IsZero)
            {
                axis = opt.SpanAxisOverride;
                axis.Unitize();
                bbox = brep.GetBoundingBox(true);
            }
            else
            {
                axis = SpanAxisFromBoundingBox(brep, out bbox);
            }

            var corners = bbox.GetCorners();
            double sMin = double.MaxValue, sMax = double.MinValue;
            foreach (var c in corners)
            {
                double s = new Vector3d(c) * axis;
                sMin = Math.Min(sMin, s);
                sMax = Math.Max(sMax, s);
            }

            double span = sMax - sMin;
            if (span <= opt.Tolerance) return false;

            frame = new RadialFrame
            {
                Axis = axis,
                SpanMin = sMin,
                SpanMax = sMax,
                RadiusOrigin = (opt.Datum == RadiusDatum.BladeRoot) ? sMin : 0.0
            };
            frame.TipRadius = frame.SpanMax - frame.RadiusOrigin;
            if (frame.TipRadius <= opt.Tolerance) return false;

            foreach (var plane in StationPlanes(opt, frame))
            {
                var sec = Biggest(SectionCurves(brep, plane, opt.Tolerance));
                if (sec == null) continue;

                Point3d le, te;
                if (!GuessLeTe(sec, plane, opt.SwapLeTe, opt.Tolerance, out le, out te)) continue;

                lePts.Add(le);
                tePts.Add(te);
            }

            if (lePts.Count > 2) leCrv = Curve.CreateInterpolatedCurve(lePts, 3);
            if (tePts.Count > 2) teCrv = Curve.CreateInterpolatedCurve(tePts, 3);

            return leCrv != null && teCrv != null;
        }

        /// <summary>The spanwise station planes, perpendicular to the span axis.
        /// These carry the true airfoil sections, which is where chord fractions
        /// are meaningful.</summary>
        public static List<Plane> StationPlanes(EdgeCutOptions opt, RadialFrame frame)
        {
            var planes = new List<Plane>();
            Point3d origin = Point3d.Origin + frame.Axis * frame.SpanMin;
            double span = frame.SpanMax - frame.SpanMin;

            int n = Math.Max(2, opt.NStations);
            for (int k = 0; k < n; k++)
            {
                double f = opt.StartFraction +
                           (opt.EndFraction - opt.StartFraction) * (k / (double)(n - 1));
                planes.Add(new Plane(origin + frame.Axis * (span * f), frame.Axis));
            }
            return planes;
        }

        // =================================================================
        // per-edge processing
        // =================================================================
        public static EdgeResult BuildEdge(Brep brep, Curve edgeCrv, List<Point3d> edgePts,
                                           EdgeCutOptions opt, RadialFrame frame,
                                           AreaDistribution dist)
        {
            var res = new EdgeResult { EdgeCurve = edgeCrv, EdgePoints = edgePts };

            for (int i = 0; i < edgePts.Count; i++)
            {
                var sr = new StationResult { Index = i };
                Point3d p = edgePts[i];

                // Radial position drives the target area at this station.
                sr.ROverR = frame.ROverR(p);
                double target = dist.Evaluate(sr.ROverR);
                sr.TargetArea = target;

                if (target <= 0.0)
                {
                    sr.Message = "target area is not positive";
                    res.Stations.Add(sr);
                    continue;
                }

                double t;
                if (!edgeCrv.ClosestPoint(p, out t))
                {
                    sr.Message = "no parameter on edge curve";
                    res.Stations.Add(sr);
                    continue;
                }

                Vector3d tan = edgeCrv.TangentAt(t);
                if (!tan.Unitize())
                {
                    sr.Message = "degenerate tangent";
                    res.Stations.Add(sr);
                    continue;
                }

                // The section plane normal to the LE/TE line -- the plane your
                // manual workflow draws the red polylines in.
                var plane = new Plane(p, tan);
                var sec = Biggest(SectionCurves(brep, plane, opt.Tolerance));
                if (sec == null)
                {
                    sr.Message = "no section in normal plane";
                    res.Stations.Add(sr);
                    continue;
                }

                Point3d a, b;
                if (!ChordEndpoints(sec, out a, out b))
                {
                    sr.Message = "no chord";
                    res.Stations.Add(sr);
                    continue;
                }

                // In this plane, the chord end nearer the station point is our edge.
                Point3d edgePt, otherPt;
                if (a.DistanceTo(p) <= b.DistanceTo(p)) { edgePt = a; otherPt = b; }
                else { edgePt = b; otherPt = a; }

                Vector3d cv = otherPt - edgePt;
                double chordLen = cv.Length;
                Vector3d chordDir = cv;
                if (!chordDir.Unitize())
                {
                    sr.Message = "zero chord";
                    res.Stations.Add(sr);
                    continue;
                }

                var ampAll = AreaMassProperties.Compute(sec);
                double secArea = ampAll != null ? ampAll.Area : 0.0;
                if (secArea < target)
                {
                    sr.Message = string.Format(
                        "section area {0:0.0000} < target {1:0.0000}", secArea, target);
                    res.Stations.Add(sr);
                    continue;
                }

                Vector3d probeDir = ProbeDirection(opt, plane, chordDir, sr.ROverR);

                double d, gotArea;
                if (!SolveOffsetForArea(sec, plane, edgePt, chordDir, probeDir, chordLen,
                                        target, opt, out d, out gotArea))
                {
                    sr.Message = "no convergence";
                    res.Stations.Add(sr);
                    continue;
                }

                Point3d q0, q1;
                var reg = NoseRegion(sec, plane, edgePt, chordDir, probeDir, chordLen, d,
                                     opt.Tolerance, out q0, out q1);
                if (reg == null)
                {
                    sr.Offset = d;
                    sr.Area = gotArea;
                    sr.Message = "cut line lost";
                    res.Stations.Add(sr);
                    continue;
                }

                sr.Ok = true;
                sr.Message = "ok";
                sr.Offset = d;
                sr.Area = gotArea;
                // The region stays exactly as measured; only the cut line is
                // extended, so the baked areas still match the reported numbers.
                var rawCut = new Line(q0, q1);
                sr.CutLine = ExtendLine(rawCut, opt.ExtensionFraction,
                                        ExtensionReference(opt, rawCut, chordLen));
                sr.AreaRegion = reg;
                res.Stations.Add(sr);
                res.CutLines.Add(sr.CutLine);
                res.AreaRegions.Add(reg);
            }

            res.LoftedSurfaces = LoftCutLines(res.CutLines);
            return res;
        }

        /// <summary>
        /// Spar cutting surface. At each spanwise station the local chord is
        /// measured in the true airfoil section, a point is placed at x/c from the
        /// LEADING EDGE, and a line perpendicular to the chord is drawn through it
        /// and trimmed to the section. Those lines are lofted.
        ///
        /// Note the difference from the edge surfaces: those work in planes normal
        /// to the LE/TE curves, because that is where your manual polygons live.
        /// The spar works in the spanwise section planes, because a chord fraction
        /// is only meaningful in the airfoil section itself.
        /// </summary>
        public static EdgeResult BuildSpar(Brep brep, EdgeCutOptions opt, RadialFrame frame,
                                           AreaDistribution dist)
        {
            var res = new EdgeResult();
            var planes = StationPlanes(opt, frame);

            for (int i = 0; i < planes.Count; i++)
            {
                var sr = new StationResult { Index = i };
                var plane = planes[i];

                sr.ROverR = frame.ROverR(plane.Origin);
                double frac = dist.Evaluate(sr.ROverR);
                sr.ChordFraction = frac;

                if (frac <= 0.0 || frac >= 1.0)
                {
                    sr.Message = string.Format("chord fraction {0:0.####} is outside (0,1)", frac);
                    res.Stations.Add(sr);
                    continue;
                }

                var sec = Biggest(SectionCurves(brep, plane, opt.Tolerance));
                if (sec == null)
                {
                    sr.Message = "no section at this station";
                    res.Stations.Add(sr);
                    continue;
                }

                Point3d le, te;
                if (!GuessLeTe(sec, plane, opt.SwapLeTe, opt.Tolerance, out le, out te))
                {
                    sr.Message = "could not identify LE/TE";
                    res.Stations.Add(sr);
                    continue;
                }

                Vector3d cv = te - le;
                double chordLen = cv.Length;
                sr.ChordLength = chordLen;

                Vector3d chordDir = cv;
                if (!chordDir.Unitize())
                {
                    sr.Message = "zero chord";
                    res.Stations.Add(sr);
                    continue;
                }

                // x/c measured from the LE, along the chord line.
                double d = chordLen * frac;
                sr.Offset = d;

                Point3d q0, q1;
                Vector3d probeDir = ProbeDirection(opt, plane, chordDir, sr.ROverR);

                if (!CrossSectionAt(sec, plane, le, chordDir, probeDir, chordLen, d,
                                    opt.Tolerance, out q0, out q1))
                {
                    sr.Message = "perpendicular did not cross the section twice";
                    res.Stations.Add(sr);
                    continue;
                }

                sr.Ok = true;
                sr.Message = "ok";
                var rawCut = new Line(q0, q1);
                sr.CutLine = ExtendLine(rawCut, opt.ExtensionFraction,
                                        ExtensionReference(opt, rawCut, chordLen));
                res.Stations.Add(sr);
                res.CutLines.Add(sr.CutLine);
            }

            res.LoftedSurfaces = LoftCutLines(res.CutLines);
            return res;
        }

        /// <summary>The two points where a line perpendicular to the chord, at
        /// distance d from the reference point, crosses the section. Furthest-apart
        /// pair wins, so a tangential graze near the nose does not.</summary>
        public static bool CrossSectionAt(Curve sec, Plane plane, Point3d refPt,
                                          Vector3d chordDir, Vector3d probeDir,
                                          double chordLen, double d,
                                          double tol, out Point3d p0, out Point3d p1)
        {
            p0 = Point3d.Unset;
            p1 = Point3d.Unset;

            var probe = CutProbe(plane, refPt + chordDir * d, probeDir, chordLen);
            if (probe == null) return false;

            var ev = Intersection.CurveCurve(sec, probe, tol, tol);
            if (ev == null || ev.Count < 2) return false;

            double best = -1.0;
            for (int i = 0; i < ev.Count; i++)
            {
                for (int j = i + 1; j < ev.Count; j++)
                {
                    double dd = ev[i].PointA.DistanceTo(ev[j].PointA);
                    if (dd > best)
                    {
                        best = dd;
                        p0 = ev[i].PointA;
                        p1 = ev[j].PointA;
                    }
                }
            }
            return best > tol;
        }

        /// <summary>Lengthens a line at both ends. The amount added to each end is
        /// fraction * referenceLength, where the reference is the local chord or the
        /// line's own length depending on ExtensionBasis. Chord-based extension is
        /// what makes the cutting surface stand well clear of the blade, as a blade
        /// is much wider than it is thick.</summary>
        public static Line ExtendLine(Line ln, double fraction, double referenceLength)
        {
            if (fraction <= 0.0) return ln;

            Vector3d v = ln.To - ln.From;
            if (v.Length < 1e-12) return ln;
            if (!v.Unitize()) return ln;

            double e = referenceLength * fraction;
            if (e <= 0.0) return ln;

            return new Line(ln.From - v * e, ln.To + v * e);
        }

        /// <summary>Reference length for the extension at one station.</summary>
        public static double ExtensionReference(EdgeCutOptions opt, Line cut, double chordLen)
        {
            return opt.Basis == ExtensionBasis.LocalChord ? chordLen : cut.Length;
        }

        /// <summary>Orients all cut lines the same way, then lofts.</summary>
        public static Brep[] LoftCutLines(List<Line> lines)
        {
            if (lines == null || lines.Count < 2) return null;

            Vector3d refDir = lines[0].To - lines[0].From;
            if (!refDir.Unitize()) return null;

            var crvs = new List<Curve>();
            foreach (var ln in lines)
            {
                Vector3d v = ln.To - ln.From;
                if (!v.Unitize()) continue;
                var use = (v * refDir < 0) ? new Line(ln.To, ln.From) : ln;
                crvs.Add(new LineCurve(use));
            }
            if (crvs.Count < 2) return null;

            return Brep.CreateFromLoft(crvs, Point3d.Unset, Point3d.Unset,
                                       LoftType.Normal, false);
        }
    }
}
