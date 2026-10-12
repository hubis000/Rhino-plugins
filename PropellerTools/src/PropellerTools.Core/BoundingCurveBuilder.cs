using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace PropellerTools.Core
{
    public sealed class BoundingCurveSettings
    {
        /// <summary>Number of centroid samples taken along the span.</summary>
        public int SampleCount = 60;

        /// <summary>
        /// Fraction of the span skipped at the root. The hub and root fillet produce
        /// section centroids that have nothing to do with the blade, so they must be
        /// excluded or they drag the curve sideways.
        /// </summary>
        public double RootSkip = 0.08;

        /// <summary>Fraction of the span skipped at the tip, where sections vanish.</summary>
        public double TipSkip = 0.03;

        /// <summary>
        /// Control point count for the rebuilt curve. Interpolating every raw centroid
        /// reproduces sampling noise as waviness, and because the solver takes section
        /// planes from this curve's tangent, waviness becomes wobbling section planes.
        /// A low count fits a fair curve through the trend instead.
        /// </summary>
        public int RebuildControlPoints = 12;

        /// <summary>
        /// Project the curve into a constant-Z plane, giving a pure top-view curve.
        /// Loses twist and coning information; see FlattenWarning.
        /// </summary>
        public bool FlattenToZ = false;

        /// <summary>Z height used when FlattenToZ is true. NaN = blade bounding box mid Z.</summary>
        public double FlattenZ = double.NaN;

        /// <summary>Span direction override. Unset = detect automatically.</summary>
        public Vector3d SpanAxisOverride = Vector3d.Unset;

        public double Tolerance = 0.001;
    }

    public sealed class BoundingCurveResult
    {
        public Curve Curve;
        public Vector3d SpanAxis;
        public List<Point3d> RawCentroids = new List<Point3d>();
        public int SectionsFound;
        public int SectionsMissed;
        public double MaxDeviationFromRaw;   // how far the fitted curve sits from the samples
        public bool Flattened;
        public List<string> Warnings = new List<string>();
    }

    public static class BoundingCurveBuilder
    {
        /// <summary>
        /// Builds a spanwise guide curve through the area centroids of the blade's
        /// cross sections. The result is intended as input to RovingAreaSolver.
        /// </summary>
        public static BoundingCurveResult Build(Brep blade, BoundingCurveSettings s)
        {
            if (blade == null) throw new ArgumentNullException(nameof(blade));

            var outcome = new BoundingCurveResult();

            // --- span axis -----------------------------------------------------
            Vector3d span = s.SpanAxisOverride.IsValid && !s.SpanAxisOverride.IsZero
                          ? s.SpanAxisOverride
                          : DetectSpanAxis(blade, outcome);

            if (!span.Unitize())
                throw new InvalidOperationException("Could not establish a span direction.");
            outcome.SpanAxis = span;

            // --- span extent ---------------------------------------------------
            var bbox = blade.GetBoundingBox(true);
            if (!bbox.IsValid) throw new InvalidOperationException("Invalid blade bounding box.");

            double dMin = double.MaxValue, dMax = double.MinValue;
            foreach (var c in bbox.GetCorners())
            {
                double d = c.X * span.X + c.Y * span.Y + c.Z * span.Z;
                if (d < dMin) dMin = d;
                if (d > dMax) dMax = d;
            }

            double total = dMax - dMin;
            if (total <= s.Tolerance)
                throw new InvalidOperationException("Blade has no measurable span.");

            double start = dMin + total * Math.Max(0.0, s.RootSkip);
            double end = dMax - total * Math.Max(0.0, s.TipSkip);
            if (end <= start)
                throw new InvalidOperationException("RootSkip and TipSkip leave no span to sample.");

            // --- sample centroids ----------------------------------------------
            int n = Math.Max(4, s.SampleCount);
            var pts = new List<Point3d>(n);

            for (int i = 0; i < n; i++)
            {
                double d = start + (end - start) * i / (n - 1.0);
                var plane = new Plane(new Point3d(span.X * d, span.Y * d, span.Z * d), span);

                Curve[] curves;
                Point3d[] ipts;
                if (!Intersection.BrepPlane(blade, plane, s.Tolerance, out curves, out ipts)
                    || curves == null || curves.Length == 0)
                {
                    outcome.SectionsMissed++;
                    continue;
                }

                // A plane can cut the blade in more than one loop near the root fillet.
                // Weight the centroids by area so the dominant loop wins, rather than
                // averaging the blade with a fillet sliver.
                double areaSum = 0.0;
                var acc = new Point3d(0, 0, 0);

                foreach (var c in curves.Where(c => c != null && c.IsClosed))
                {
                    var amp = AreaMassProperties.Compute(c);
                    if (amp == null) continue;
                    double a = Math.Abs(amp.Area);
                    if (a <= 0) continue;
                    acc += amp.Centroid * a;
                    areaSum += a;
                }

                if (areaSum <= 0) { outcome.SectionsMissed++; continue; }

                pts.Add(acc / areaSum);
                outcome.SectionsFound++;
            }

            if (pts.Count < 4)
                throw new InvalidOperationException(
                    string.Format("Only {0} usable section(s); cannot fit a curve.", pts.Count));

            outcome.RawCentroids = pts;

            // --- optional flatten ----------------------------------------------
            if (s.FlattenToZ)
            {
                double z = double.IsNaN(s.FlattenZ)
                         ? 0.5 * (bbox.Min.Z + bbox.Max.Z)
                         : s.FlattenZ;
                for (int i = 0; i < pts.Count; i++)
                    pts[i] = new Point3d(pts[i].X, pts[i].Y, z);

                outcome.Flattened = true;
                outcome.Warnings.Add(
                    "Curve flattened to constant Z. Section planes will ignore blade twist " +
                    "and coning, so measured areas carry a 1/cos error where the roving path " +
                    "leaves the XY plane. Prefer the unflattened curve for area work.");
            }

            // --- fit -------------------------------------------------------------
            Curve interp = Curve.CreateInterpolatedCurve(pts, 3);
            if (interp == null)
                throw new InvalidOperationException("Interpolation through the centroids failed.");

            // Rebuild to a low control point count. The solver differentiates this curve
            // to get section planes, so fairness matters more than passing exactly
            // through every sampled centroid.
            Curve fitted = interp;
            int cps = Math.Max(4, Math.Min(s.RebuildControlPoints, pts.Count));
            var rebuilt = interp.Rebuild(cps, 3, true);
            if (rebuilt != null) fitted = rebuilt;
            else outcome.Warnings.Add("Rebuild failed; using the raw interpolated curve. "
                                    + "Check for wobble in the section planes.");

            // Report how far the fit strayed, so a bad fit is visible rather than silent.
            double maxDev = 0.0;
            foreach (var p in outcome.RawCentroids)
            {
                double t;
                if (!fitted.ClosestPoint(p, out t)) continue;
                double dev = fitted.PointAt(t).DistanceTo(p);
                if (dev > maxDev) maxDev = dev;
            }
            outcome.MaxDeviationFromRaw = maxDev;

            double diag = bbox.Diagonal.Length;
            if (diag > 0 && maxDev > 0.01 * diag)
                outcome.Warnings.Add(string.Format(
                    "Fitted curve deviates up to {0:F3} from the sampled centroids " +
                    "({1:F1}% of the bounding box diagonal). Raise RebuildControlPoints.",
                    maxDev, 100.0 * maxDev / diag));

            if (outcome.SectionsMissed > 0)
                outcome.Warnings.Add(string.Format(
                    "{0} of {1} sample planes produced no usable section.",
                    outcome.SectionsMissed, n));

            outcome.Curve = fitted;
            return outcome;
        }

        /// <summary>
        /// Detects the span direction as the principal axis of the blade's planform seen
        /// down world Z, via a 2x2 covariance eigenproblem on points sampled from the
        /// Brep's edges. This works for a blade lying at any angle in XY, unlike simply
        /// taking the longest bounding box edge.
        /// </summary>
        private static Vector3d DetectSpanAxis(Brep blade, BoundingCurveResult outcome)
        {
            var samples = new List<Point2d>();

            foreach (var edge in blade.Edges)
            {
                if (edge == null) continue;
                var dom = edge.Domain;
                for (int i = 0; i <= 12; i++)
                {
                    var p = edge.PointAt(dom.ParameterAt(i / 12.0));
                    samples.Add(new Point2d(p.X, p.Y));
                }
            }

            if (samples.Count < 3)
            {
                outcome.Warnings.Add("Too few edge samples; falling back to world X.");
                return Vector3d.XAxis;
            }

            double mx = samples.Average(p => p.X);
            double my = samples.Average(p => p.Y);

            double sxx = 0, syy = 0, sxy = 0;
            foreach (var p in samples)
            {
                double dx = p.X - mx, dy = p.Y - my;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            int m = samples.Count;
            sxx /= m; syy /= m; sxy /= m;

            // Largest eigenvector of [[sxx,sxy],[sxy,syy]], closed form.
            double tr = sxx + syy;
            double det = sxx * syy - sxy * sxy;
            double disc = Math.Max(0.0, tr * tr * 0.25 - det);
            double lambda = tr * 0.5 + Math.Sqrt(disc);

            Vector3d axis;
            if (Math.Abs(sxy) > 1e-12)
                axis = new Vector3d(lambda - syy, sxy, 0.0);
            else
                axis = sxx >= syy ? Vector3d.XAxis : Vector3d.YAxis;

            if (!axis.Unitize())
            {
                outcome.Warnings.Add("Span axis degenerate; falling back to world X.");
                return Vector3d.XAxis;
            }

            // Orient root-to-tip: point away from the world origin, which for a blade
            // modelled with its hub near the origin puts the tip at the far end.
            var bbox = blade.GetBoundingBox(true);
            var centre = bbox.Center;
            if (centre.X * axis.X + centre.Y * axis.Y < 0) axis.Reverse();

            return axis;
        }
    }
}
