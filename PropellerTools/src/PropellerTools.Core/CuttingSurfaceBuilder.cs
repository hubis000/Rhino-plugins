using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace PropellerTools.Core
{
    /// <summary>
    /// Which side of the blade a cutting surface lies on. "Upper" and "Lower" are relative
    /// to a reference direction carried along the span, not to any global axis.
    /// </summary>
    public enum BladeSide { Upper, Lower }

    public sealed class SplitOutline
    {
        public double NormalisedSpan;
        public Plane SectionPlane;
        public Curve Upper;          // open curve, LE -> TE
        public Curve Lower;          // open curve, LE -> TE
        public Point3d LeadingEdge;
        public Point3d TrailingEdge;
        public string Note;
    }

    public sealed class CuttingSurfaceSettings
    {
        /// <summary>
        /// Tangent extension added at each end of every half-curve, in model units.
        /// The surfaces must overhang the blade or the trim leaves slivers at LE and TE.
        /// </summary>
        public double EdgeExtension = 5.0;

        /// <summary>
        /// Extra extension at the outboard end of the loft, so the surface passes clear
        /// of the tip.
        /// </summary>
        public double TipExtension = 0.0;

        public double Tolerance = 0.001;
    }

    public sealed class CuttingSurfaceResult
    {
        public List<SplitOutline> Outlines = new List<SplitOutline>();
        public Brep[] UpperSurface = new Brep[0];
        public Brep[] LowerSurface = new Brep[0];
        public List<string> Warnings = new List<string>();
    }

    public static class CuttingSurfaceBuilder
    {
        /// <summary>
        /// Takes solved constant-area outlines and produces two open cutting surfaces
        /// instead of one closed tube.
        /// </summary>
        public static CuttingSurfaceResult Build(
            IEnumerable<StationResult> stations,
            CuttingSurfaceSettings cs)
        {
            var outcome = new CuttingSurfaceResult();

            var usable = stations
                .Where(r => r != null && r.PocketOutline != null && r.PocketOutline.IsClosed)
                .ToList();

            if (usable.Count < 2)
            {
                outcome.Warnings.Add("Fewer than two closed outlines; nothing to loft.");
                return outcome;
            }

            // Reference direction for side classification. Seeded from the first station and
            // then carried forward, so a twisted blade cannot flip the assignment mid-span.
            Vector3d reference = Vector3d.Unset;

            foreach (var st in usable)
            {
                var so = SplitOne(st, cs, ref reference);
                if (so != null) outcome.Outlines.Add(so);
                else outcome.Warnings.Add(
                    string.Format("Split failed at span {0:F3}.", st.NormalisedSpan));
            }

            var upper = outcome.Outlines.Where(o => o.Upper != null)
                                        .Select(o => o.Upper).ToList();
            var lower = outcome.Outlines.Where(o => o.Lower != null)
                                        .Select(o => o.Lower).ToList();

            if (upper.Count >= 2)
                outcome.UpperSurface = LoftHalves(upper, cs);
            else
                outcome.Warnings.Add("Not enough upper halves to loft.");

            if (lower.Count >= 2)
                outcome.LowerSurface = LoftHalves(lower, cs);
            else
                outcome.Warnings.Add("Not enough lower halves to loft.");

            return outcome;
        }

        // ---- splitting ----------------------------------------------------

        private static SplitOutline SplitOne(
            StationResult st, CuttingSurfaceSettings cs, ref Vector3d reference)
        {
            Curve c = st.PocketOutline.DuplicateCurve();
            Plane plane = st.SectionPlane;

            var amp = AreaMassProperties.Compute(c);
            if (amp == null) return null;
            Point3d centroid = amp.Centroid;

            // Step 1: trailing edge = outline point farthest from the area centroid.
            double tTe = FarthestParameter(c, centroid);
            Point3d te = c.PointAt(tTe);

            // Step 2: leading edge = outline point farthest from the trailing edge.
            // Two steps rather than a supplied chord direction, so this works on
            // cambered and highly twisted sections without extra input.
            double tLe = FarthestParameter(c, te);
            Point3d le = c.PointAt(tLe);

            if (le.DistanceTo(te) < cs.Tolerance * 10) return null;

            Curve[] pieces = c.Split(new[] { tLe, tTe });
            if (pieces == null || pieces.Length < 2) return null;

            // Split can return more than two fragments if a parameter lands on the seam;
            // keep the two longest.
            var halves = pieces.Where(p => p != null)
                               .OrderByDescending(p => p.GetLength())
                               .Take(2).ToList();
            if (halves.Count < 2) return null;

            Vector3d chord = te - le;
            chord.Unitize();

            // In-plane direction perpendicular to the chord. Side classification is done
            // against this, projected onto the carried reference.
            Vector3d across = Vector3d.CrossProduct(plane.Normal, chord);
            if (!across.Unitize()) return null;

            if (reference == Vector3d.Unset)
                reference = across;
            else if (across * reference < 0)
                across.Reverse();   // keep continuity with previous stations

            reference = across;

            // Classify by the signed offset of each half's midpoint from the chord line.
            Curve a = halves[0], b = halves[1];
            double sa = SignedOffset(a, le, across);
            double sb = SignedOffset(b, le, across);

            Curve upper = sa >= sb ? a : b;
            Curve lower = sa >= sb ? b : a;

            OrientLeToTe(upper, le);
            OrientLeToTe(lower, le);

            return new SplitOutline
            {
                NormalisedSpan = st.NormalisedSpan,
                SectionPlane = plane,
                Upper = upper,
                Lower = lower,
                LeadingEdge = le,
                TrailingEdge = te
            };
        }

        private static double FarthestParameter(Curve c, Point3d from)
        {
            // Sampled search then local refinement. Curve.ClosestPoint has no farthest-point
            // counterpart, so this is done by hand.
            const int samples = 400;
            var dom = c.Domain;
            double bestT = dom.T0, bestD = -1;

            for (int i = 0; i <= samples; i++)
            {
                double t = dom.ParameterAt((double)i / samples);
                double d = c.PointAt(t).DistanceTo(from);
                if (d > bestD) { bestD = d; bestT = t; }
            }

            double span = dom.Length / samples;
            for (int iter = 0; iter < 40; iter++)
            {
                double tm = bestT - span, tp = bestT + span;
                double dm = dom.IncludesParameter(tm) ? c.PointAt(tm).DistanceTo(from) : -1;
                double dp = dom.IncludesParameter(tp) ? c.PointAt(tp).DistanceTo(from) : -1;

                if (dm > bestD) { bestD = dm; bestT = tm; }
                else if (dp > bestD) { bestD = dp; bestT = tp; }
                else span *= 0.5;
            }
            return bestT;
        }

        private static double SignedOffset(Curve half, Point3d le, Vector3d across)
        {
            Point3d mid = half.PointAtNormalizedLength(0.5);
            return (mid - le) * across;
        }

        private static void OrientLeToTe(Curve half, Point3d le)
        {
            if (half.PointAtStart.DistanceTo(le) > half.PointAtEnd.DistanceTo(le))
                half.Reverse();
        }

        // ---- lofting ------------------------------------------------------

        private static Brep[] LoftHalves(List<Curve> halves, CuttingSurfaceSettings cs)
        {
            var prepared = new List<Curve>(halves.Count);

            foreach (var h in halves)
            {
                Curve c = h.DuplicateCurve();

                if (cs.EdgeExtension > 0)
                {
                    // Smooth (curvature-following) extension keeps the overhang tangent to
                    // the pocket wall, so the trim stays clean where it exits the blade.
                    Curve ext = c.Extend(CurveEnd.Both,
                                         cs.EdgeExtension,
                                         CurveExtensionStyle.Smooth);
                    if (ext != null) c = ext;
                }

                prepared.Add(c);
            }

            // Open curves: no seam to harmonise, but directions still must agree.
            for (int i = 1; i < prepared.Count; i++)
            {
                if (!Curve.DoDirectionsMatch(prepared[i - 1], prepared[i]))
                    prepared[i].Reverse();
            }

            var breps = Brep.CreateFromLoft(prepared, Point3d.Unset, Point3d.Unset,
                                            LoftType.Normal, false);
            return breps ?? new Brep[0];
        }
    }
}
