using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MejzlikEdgeCut
{
    public enum DistributionKind
    {
        /// <summary>Same area at every station. The original behaviour.</summary>
        Constant,

        /// <summary>Linear in r/R between a root value and a tip value.</summary>
        Linear,

        /// <summary>Root value scaled by a power law toward the tip value:
        /// A = Atip + (Aroot - Atip) * (1 - x)^exponent, x = r/R.</summary>
        Power,

        /// <summary>Piecewise-linear through user control points, clamped
        /// outside the first and last point.</summary>
        Table
    }

    /// <summary>
    /// Target cross-sectional area as a function of x = r/R.
    /// Used independently for the leading and trailing edge.
    /// </summary>
    public class AreaDistribution
    {
        public DistributionKind Kind = DistributionKind.Constant;

        /// <summary>Constant value, and the root value for Linear and Power.</summary>
        public double RootValue = 1.0;

        /// <summary>Tip value for Linear and Power.</summary>
        public double TipValue = 1.0;

        /// <summary>Exponent for Power. 1.0 degenerates to Linear.
        /// Greater than 1 holds the root value further outboard.</summary>
        public double Exponent = 2.0;

        /// <summary>Control points for Table, as (r/R, area). Kept sorted.</summary>
        public List<KeyValuePair<double, double>> Points =
            new List<KeyValuePair<double, double>>();

        public static AreaDistribution ConstantAt(double value)
        {
            return new AreaDistribution
            {
                Kind = DistributionKind.Constant,
                RootValue = value,
                TipValue = value
            };
        }

        /// <summary>Target area at x = r/R. x is clamped to [0,1].</summary>
        public double Evaluate(double x)
        {
            if (double.IsNaN(x)) x = 0.0;
            x = Math.Max(0.0, Math.Min(1.0, x));

            switch (Kind)
            {
                case DistributionKind.Constant:
                    return RootValue;

                case DistributionKind.Linear:
                    return RootValue + (TipValue - RootValue) * x;

                case DistributionKind.Power:
                    {
                        double e = Exponent <= 0.0 ? 1.0 : Exponent;
                        return TipValue + (RootValue - TipValue) * Math.Pow(1.0 - x, e);
                    }

                case DistributionKind.Table:
                    return EvaluateTable(x);

                default:
                    return RootValue;
            }
        }

        private double EvaluateTable(double x)
        {
            if (Points == null || Points.Count == 0) return RootValue;
            if (Points.Count == 1) return Points[0].Value;

            var pts = Points.OrderBy(p => p.Key).ToList();

            if (x <= pts[0].Key) return pts[0].Value;
            if (x >= pts[pts.Count - 1].Key) return pts[pts.Count - 1].Value;

            for (int i = 0; i < pts.Count - 1; i++)
            {
                double x0 = pts[i].Key, x1 = pts[i + 1].Key;
                if (x >= x0 && x <= x1)
                {
                    double span = x1 - x0;
                    if (span <= 1e-12) return pts[i + 1].Value;
                    double t = (x - x0) / span;
                    return pts[i].Value + (pts[i + 1].Value - pts[i].Value) * t;
                }
            }
            return pts[pts.Count - 1].Value;
        }

        /// <summary>True if every value the distribution can return is positive.</summary>
        public bool IsPositive(out string problem)
        {
            return IsWithin(1e-12, double.MaxValue, out problem);
        }

        /// <summary>True if every value the distribution can return lies in [min,max].
        /// Sampled at 21 stations, so a pathological table between samples can slip
        /// through; the per-station report is the real check.</summary>
        public bool IsWithin(double min, double max, out string problem)
        {
            problem = null;
            for (int i = 0; i <= 20; i++)
            {
                double x = i / 20.0;
                double v = Evaluate(x);
                if (v < min || v > max)
                {
                    problem = string.Format(CultureInfo.InvariantCulture,
                        "value is {0:0.######} at r/R = {1:0.00}; it must stay between {2:0.######} and {3:0.######}",
                        v, x, min, max);
                    return false;
                }
            }
            return true;
        }

        // =================================================================
        // table parsing / formatting, for command-line entry
        // =================================================================

        /// <summary>Parses "0.2:12, 0.5:9.5, 1.0:4" into control points.
        /// Accepts ':' or '=' between the pair, and ',' or ';' between pairs.
        /// Decimal separator is always '.' regardless of Windows locale.</summary>
        public static bool TryParseTable(string text,
                                         out List<KeyValuePair<double, double>> points,
                                         out string error)
        {
            return TryParseTable(text, double.MaxValue, out points, out error);
        }

        /// <summary>As above, with an upper bound on the value — 1.0 for chord
        /// fractions, unbounded for areas.</summary>
        public static bool TryParseTable(string text, double maxValue,
                                         out List<KeyValuePair<double, double>> points,
                                         out string error)
        {
            points = new List<KeyValuePair<double, double>>();
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "empty input";
                return false;
            }

            var chunks = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in chunks)
            {
                var chunk = raw.Trim();
                if (chunk.Length == 0) continue;

                var parts = chunk.Split(new[] { ':', '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    error = string.Format("cannot read \"{0}\"; expected rR:area", chunk);
                    return false;
                }

                double r, a;
                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out r))
                {
                    error = string.Format("\"{0}\" is not a number", parts[0].Trim());
                    return false;
                }
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out a))
                {
                    error = string.Format("\"{0}\" is not a number", parts[1].Trim());
                    return false;
                }
                if (r < 0.0 || r > 1.0)
                {
                    error = string.Format("r/R = {0} is outside 0..1", r);
                    return false;
                }
                if (a <= 0.0)
                {
                    error = string.Format("value {0} at r/R = {1} must be positive", a, r);
                    return false;
                }
                if (a > maxValue)
                {
                    error = string.Format("value {0} at r/R = {1} exceeds the limit of {2}",
                                          a, r, maxValue);
                    return false;
                }

                points.Add(new KeyValuePair<double, double>(r, a));
            }

            if (points.Count < 2)
            {
                error = "need at least two control points";
                return false;
            }

            points = points.OrderBy(p => p.Key).ToList();
            return true;
        }

        public string TableToString()
        {
            if (Points == null || Points.Count == 0) return "";
            return string.Join(", ", Points.OrderBy(p => p.Key).Select(p =>
                string.Format(CultureInfo.InvariantCulture, "{0:0.###}:{1:0.######}",
                              p.Key, p.Value)));
        }

        /// <summary>One-line description for the command line and the report.</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case DistributionKind.Constant:
                    return string.Format(CultureInfo.InvariantCulture,
                        "constant {0:0.######}", RootValue);
                case DistributionKind.Linear:
                    return string.Format(CultureInfo.InvariantCulture,
                        "linear {0:0.######} at root -> {1:0.######} at tip", RootValue, TipValue);
                case DistributionKind.Power:
                    return string.Format(CultureInfo.InvariantCulture,
                        "power {0:0.######} -> {1:0.######}, exponent {2:0.###}",
                        RootValue, TipValue, Exponent);
                case DistributionKind.Table:
                    return "table " + TableToString();
                default:
                    return "unknown";
            }
        }

        // =================================================================
        // settings round-trip
        // =================================================================
        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Kind.ToString()).Append('|');
            sb.Append(RootValue.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(TipValue.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(Exponent.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(TableToString());
            return sb.ToString();
        }

        public static AreaDistribution Deserialize(string s, AreaDistribution fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            var parts = s.Split('|');
            if (parts.Length < 4) return fallback;

            var d = new AreaDistribution();
            DistributionKind k;
            if (!Enum.TryParse(parts[0], out k)) return fallback;
            d.Kind = k;

            double v;
            d.RootValue = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback.RootValue;
            d.TipValue = double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback.TipValue;
            d.Exponent = double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback.Exponent;

            if (parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[4]))
            {
                List<KeyValuePair<double, double>> pts;
                string err;
                if (AreaDistribution.TryParseTable(parts[4], out pts, out err))
                    d.Points = pts;
            }
            return d;
        }
    }
}
