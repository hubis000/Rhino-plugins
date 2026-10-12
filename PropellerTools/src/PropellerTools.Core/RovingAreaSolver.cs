using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace PropellerTools.Core
{
    /// <summary>
    /// Result for a single spanwise station.
    /// </summary>
    public sealed class StationResult
    {
        public double CurveParameter;      // parameter on the bounding curve
        public double NormalisedSpan;      // 0 at root end, 1 at tip end
        public Plane SectionPlane;         // plane normal to the bounding curve
        public Curve BladeSection;         // blade outline in that plane
        public Curve PocketOutline;        // solved roving outline
        public double BladeSectionArea;    // area of the full blade section
        public double TargetArea;          // area actually requested (after tip taper)
        public double AchievedArea;        // area actually obtained
        public double SolvedParameter;     // wall distance d, or scale factor k
        public bool Saturated;             // target could not be met even at the limit
        public string Note;
    }

    public enum PocketRule
    {
        /// <summary>Inward offset of the blade section by a wall distance. Physical, needs bisection.</summary>
        InwardOffset,
        /// <summary>Uniform scale about the section area centroid. Closed-form, k = sqrt(A/A0).</summary>
        CentroidScale
    }

    public sealed class SolverSettings
    {
        public double TargetArea;               // mm^2, constant along the span
        public int StationCount = 40;
        public PocketRule Rule = PocketRule.InwardOffset;
        public double Tolerance = 0.001;        // model tolerance, mm
        public double AreaTolerance = 0.01;     // mm^2, convergence target on area
        public int MaxIterations = 60;

        /// <summary>
        /// Largest fraction of the local blade section area the pocket may occupy.
        /// Caps the pocket near the tip so a skin laminate always remains.
        /// </summary>
        public double MaxAreaFraction = 0.55;

        /// <summary>
        /// Smoothing width for the tip taper, as a fraction of TargetArea. Larger = softer
        /// blend into the tip, at the cost of losing constant area slightly earlier.
        /// </summary>
        public double TaperSmoothing = 0.15;
    }

    public static class RovingAreaSolver
    {
        /// <summary>
        /// Generates constant-cross-section-area roving outlines along a blade.
        /// </summary>
        /// <param name="blade">Closed blade solid.</param>
        /// <param name="boundingCurve">Spanwise guide curve. Sections are taken normal to it,
        /// so it must run root-to-tip and be tangent-continuous; a kink produces a section
        /// plane discontinuity and a visible crease in the lofted cutting surface.</param>
        public static List<StationResult> Solve(Brep blade, Curve boundingCurve, SolverSettings s)
        {
            if (blade == null) throw new ArgumentNullException(nameof(blade));
            if (boundingCurve == null) throw new ArgumentNullException(nameof(boundingCurve));
            if (s.TargetArea <= 0) throw new ArgumentException("TargetArea must be positive.");

            var results = new List<StationResult>();

            double[] ts = boundingCurve.DivideByCount(Math.Max(2, s.StationCount) - 1, true);
            if (ts == null || ts.Length == 0)
                throw new InvalidOperationException("Could not divide the bounding curve.");

            // Pass 1: sections and their areas. Needed before tapering, because the taper
            // depends on the local blade section area.
            foreach (double t in ts)
            {
                var r = new StationResult { CurveParameter = t };

                Plane frame;
                if (!boundingCurve.PerpendicularFrameAt(t, out frame))
                {
                    r.Note = "No perpendicular frame at this parameter.";
                    results.Add(r);
                    continue;
                }
                r.SectionPlane = frame;

                Curve section = SectionAt(blade, frame, s.Tolerance);
                if (section == null)
                {
                    r.Note = "Section plane did not produce a closed blade section.";
                    results.Add(r);
                    continue;
                }

                r.BladeSection = section;
                r.BladeSectionArea = PlanarArea(section);
                results.Add(r);
            }

            // Normalised span, used only for reporting.
            for (int i = 0; i < results.Count; i++)
                results[i].NormalisedSpan = results.Count == 1 ? 0.0 : (double)i / (results.Count - 1);

            // Pass 2: tapered target, then solve.
            double k = Math.Max(1e-9, s.TaperSmoothing * s.TargetArea);

            foreach (var r in results)
            {
                if (r.BladeSection == null) continue;

                double ceiling = s.MaxAreaFraction * r.BladeSectionArea;

                // Smooth minimum of the constant target and the local ceiling. A hard
                // Math.Min would put a slope discontinuity at the crossover, which the loft
                // turns into a crease; this blend is C1 so the cutting surface stays fair.
                r.TargetArea = SmoothMin(s.TargetArea, ceiling, k);

                if (r.TargetArea <= 0)
                {
                    r.Saturated = true;
                    r.Note = "Blade section too small for any pocket.";
                    continue;
                }

                switch (s.Rule)
                {
                    case PocketRule.CentroidScale:
                        SolveByCentroidScale(r);
                        break;
                    default:
                        SolveByInwardOffset(r, s);
                        break;
                }
            }

            return results;
        }

        // ---- pocket rules -------------------------------------------------

        /// <summary>
        /// Closed form. Scaling a planar region about any point multiplies its area by k^2,
        /// so the required factor is exact and no iteration is needed.
        /// </summary>
        private static void SolveByCentroidScale(StationResult r)
        {
            var amp = AreaMassProperties.Compute(r.BladeSection);
            if (amp == null) { r.Note = "Area computation failed."; return; }

            double kScale = Math.Sqrt(r.TargetArea / amp.Area);
            if (kScale >= 1.0)
            {
                kScale = 1.0;
                r.Saturated = true;
                r.Note = "Target area equals or exceeds the blade section area.";
            }

            Curve c = r.BladeSection.DuplicateCurve();
            c.Transform(Transform.Scale(amp.Centroid, kScale));

            r.PocketOutline = c;
            r.SolvedParameter = kScale;
            r.AchievedArea = PlanarArea(c);
        }

        /// <summary>
        /// Bisection on wall distance d. Area is monotonically decreasing in d, so bisection
        /// is unconditionally convergent once bracketed — no derivatives, cannot diverge.
        /// </summary>
        private static void SolveByInwardOffset(StationResult r, SolverSettings s)
        {
            // Offsetting by zero is the section itself. If even that is too small the
            // target is unreachable here.
            if (r.BladeSectionArea <= r.TargetArea)
            {
                r.PocketOutline = r.BladeSection.DuplicateCurve();
                r.SolvedParameter = 0.0;
                r.AchievedArea = r.BladeSectionArea;
                r.Saturated = true;
                r.Note = "Target area not reachable; pocket clamped to the full section.";
                return;
            }

            // Upper bracket: grow d until the area falls below target or the offset dies.
            double lo = 0.0, hi = 0.0;
            double step = Math.Sqrt(r.BladeSectionArea) * 0.02;
            Curve best = null;
            double bestArea = r.BladeSectionArea;

            for (int i = 0; i < 40; i++)
            {
                hi += step;
                Curve c = OffsetInward(r.BladeSection, r.SectionPlane, hi, s.Tolerance);
                double a = c == null ? 0.0 : PlanarArea(c);
                if (a <= r.TargetArea) { best = c; bestArea = a; break; }
                lo = hi;
                step *= 1.5;
            }

            if (best == null && bestArea > r.TargetArea)
            {
                r.Note = "Could not bracket the target area; inward offset failed.";
                return;
            }

            // Bisection.
            Curve solved = null;
            double solvedArea = 0.0, mid = 0.0;

            for (int i = 0; i < s.MaxIterations; i++)
            {
                mid = 0.5 * (lo + hi);
                Curve c = OffsetInward(r.BladeSection, r.SectionPlane, mid, s.Tolerance);
                double a = c == null ? 0.0 : PlanarArea(c);

                if (c != null) { solved = c; solvedArea = a; }

                if (Math.Abs(a - r.TargetArea) <= s.AreaTolerance) break;

                if (a > r.TargetArea) lo = mid; else hi = mid;
            }

            if (solved == null)
            {
                r.Note = "Inward offset produced no usable curve during bisection.";
                return;
            }

            r.PocketOutline = solved;
            r.SolvedParameter = mid;
            r.AchievedArea = solvedArea;

            if (Math.Abs(solvedArea - r.TargetArea) > 10.0 * s.AreaTolerance)
                r.Note = "Converged loosely; check the section for a thin trailing edge.";
        }

        // ---- geometry helpers --------------------------------------------

        /// <summary>
        /// Intersects the blade with a plane and returns the closed section nearest the
        /// plane origin. A twisted blade can give more than one loop per plane near the
        /// root fillet, so picking by proximity matters.
        /// </summary>
        private static Curve SectionAt(Brep blade, Plane plane, double tol)
        {
            Curve[] curves;
            Point3d[] pts;
            if (!Intersection.BrepPlane(blade, plane, tol, out curves, out pts))
                return null;
            if (curves == null || curves.Length == 0) return null;

            var closed = curves.Where(c => c != null && c.IsClosed).ToList();
            if (closed.Count == 0)
            {
                // Try to close near-closed fragments before giving up.
                foreach (var c in curves.Where(c => c != null))
                {
                    if (c.PointAtStart.DistanceTo(c.PointAtEnd) < tol * 100 && c.MakeClosed(tol * 100))
                        closed.Add(c);
                }
                if (closed.Count == 0) return null;
            }

            Curve bestCurve = null;
            double bestDist = double.MaxValue;
            foreach (var c in closed)
            {
                double t;
                if (!c.ClosestPoint(plane.Origin, out t)) continue;
                double d = c.PointAt(t).DistanceTo(plane.Origin);
                if (d < bestDist) { bestDist = d; bestCurve = c; }
            }
            return bestCurve;
        }

        /// <summary>
        /// Inward offset of a closed planar curve. Direction is resolved by testing which
        /// side loses area, because curve orientation from a Brep/plane intersection is
        /// not guaranteed.
        /// </summary>
        private static Curve OffsetInward(Curve section, Plane plane, double d, double tol)
        {
            if (d <= 0) return section.DuplicateCurve();

            Curve a = PickLargestClosed(section.Offset(plane, d, tol, CurveOffsetCornerStyle.Sharp));
            Curve b = PickLargestClosed(section.Offset(plane, -d, tol, CurveOffsetCornerStyle.Sharp));

            double aa = a == null ? double.MaxValue : PlanarArea(a);
            double ab = b == null ? double.MaxValue : PlanarArea(b);

            if (a == null && b == null) return null;
            return aa <= ab ? a : b;
        }

        /// <summary>
        /// Inward offsets of thin sections fragment and self-intersect; keep the largest
        /// closed loop and discard the debris.
        /// </summary>
        private static Curve PickLargestClosed(Curve[] candidates)
        {
            if (candidates == null || candidates.Length == 0) return null;
            Curve best = null;
            double bestArea = -1;
            foreach (var c in candidates)
            {
                if (c == null || !c.IsClosed) continue;
                double a = PlanarArea(c);
                if (a > bestArea) { bestArea = a; best = c; }
            }
            return best;
        }

        private static double PlanarArea(Curve c)
        {
            if (c == null) return 0.0;
            var amp = AreaMassProperties.Compute(c);
            return amp == null ? 0.0 : Math.Abs(amp.Area);
        }

        /// <summary>
        /// C1 smooth minimum (quadratic polynomial blend). Reduces to Math.Min as k -> 0.
        /// </summary>
        private static double SmoothMin(double a, double b, double k)
        {
            double h = Math.Max(0.0, Math.Min(1.0, 0.5 + 0.5 * (b - a) / k));
            return b * (1.0 - h) + a * h - k * h * (1.0 - h);
        }

        // ---- lofting ------------------------------------------------------

        /// <summary>
        /// Lofts the solved outlines into the cutting surface. Seams and directions are
        /// harmonised first: plane-intersection curves come back with arbitrary seam
        /// location and orientation, and lofting them raw is the usual cause of a twisted
        /// or self-intersecting result.
        /// </summary>
        public static Brep[] BuildCuttingSurface(IEnumerable<StationResult> stations, Curve seamGuide = null)
        {
            var curves = stations
                .Where(r => r != null && r.PocketOutline != null)
                .Select(r => r.PocketOutline.DuplicateCurve())
                .ToList();

            if (curves.Count < 2) return new Brep[0];

            for (int i = 0; i < curves.Count; i++)
            {
                var c = curves[i];
                if (!c.IsClosed) continue;

                // Seam: nearest point to the guide, else carry the previous seam forward.
                Point3d anchor;
                if (seamGuide != null)
                {
                    double gt;
                    seamGuide.ClosestPoint(c.PointAtStart, out gt);
                    anchor = seamGuide.PointAt(gt);
                }
                else
                {
                    anchor = i == 0 ? c.PointAtStart : curves[i - 1].PointAtStart;
                }

                double t;
                if (c.ClosestPoint(anchor, out t))
                    c.ChangeClosedCurveSeam(t);

                if (i > 0 && !Curve.DoDirectionsMatch(curves[i - 1], c))
                    c.Reverse();
            }

            return Brep.CreateFromLoft(curves, Point3d.Unset, Point3d.Unset,
                                       LoftType.Normal, false) ?? new Brep[0];
        }
    }
}
