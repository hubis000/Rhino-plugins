using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

// This is the text Rhino shows under "Description" in the Plug-in Manager.
// Set explicitly rather than via the .csproj Description property, which did not
// reach the Plug-in Manager reliably.
[assembly: AssemblyDescription(MejzlikEdgeCut.PlugInInfo.Description)]

// Rhino uses these to fill in the Plug-in Manager entry.
[assembly: PlugInDescription(DescriptionType.Organization, "Mejzlik Propellers s.r.o.")]
[assembly: PlugInDescription(DescriptionType.Email, "")]
[assembly: PlugInDescription(DescriptionType.Phone, "")]
[assembly: PlugInDescription(DescriptionType.Address, "")]
[assembly: PlugInDescription(DescriptionType.Country, "Czech Republic")]
[assembly: PlugInDescription(DescriptionType.WebSite, "")]
[assembly: PlugInDescription(DescriptionType.UpdateUrl, "")]

// REPLACE THIS GUID with a freshly generated one before you ship the plug-in.
// Tools > Create GUID in Visual Studio, or [guid]::NewGuid() in PowerShell.
// Two plug-ins with the same GUID will collide in Rhino.
[assembly: Guid("83447e0d-2c69-4f6b-b287-5052293d3bf4")]

namespace MejzlikEdgeCut
{
    /// <summary>Version and description in one place. Attribute arguments must be
    /// compile-time constants, which is why these are const rather than read from
    /// the assembly.</summary>
    public static class PlugInInfo
    {
        public const string Version = "1.3.0";

        public const string Description =
            "v1.3.0 - Cutting surfaces for propeller blade solids.\r\n" +
            "\r\n" +
            "LE and TE surfaces are solved to a target cross-sectional area at each " +
            "spanwise station. The target can be constant or vary with r/R as a linear, " +
            "power or piecewise-linear table law, set independently for each edge.\r\n" +
            "\r\n" +
            "A spar surface can be placed at a chord fraction x/c measured from the " +
            "leading edge, using the same distribution forms.\r\n" +
            "\r\n" +
            "Cut lines run perpendicular to the local chord, parallel to world Z, or " +
            "blend between the two across an r/R transition - chord-normal over the " +
            "aerodynamic span, vertical at the root. All surfaces overhang the blade " +
            "by a settable percentage of chord or thickness.\r\n" +
            "\r\n" +
            "The blade span axis, LE/TE lines and station range are derived from the " +
            "solid automatically. Every station reports its r/R, target, achieved value " +
            "and solved offset, and the measured areas can be baked for checking.\r\n" +
            "\r\n" +
            "Command: MejzlikEdgeCut";

        public const string Summary =
            "LE/TE by r/R-varying area, spar by chord fraction, selectable cut direction.";
    }

    /// <summary>
    /// Plug-in entry point. One instance per Rhino session.
    /// </summary>
    public class MejzlikEdgeCutPlugIn : Rhino.PlugIns.PlugIn
    {
        public MejzlikEdgeCutPlugIn()
        {
            Instance = this;
        }

        public static MejzlikEdgeCutPlugIn Instance { get; private set; }

        /// <summary>Version and description live in PlugInInfo, so the Plug-in
        /// Manager text and this banner can never disagree.</summary>
        public static string Banner
        {
            get
            {
                return string.Format("MejzlikEdgeCut {0} \u2014 {1}",
                                     PlugInInfo.Version, PlugInInfo.Summary);
            }
        }

        // ---- persistent settings, survive Rhino restarts -------------------
        public int NStations
        {
            get { return Settings.GetInteger("NStations", 20); }
            set { Settings.SetInteger("NStations", value); }
        }

        public double StartFraction
        {
            get { return Settings.GetDouble("StartFraction", 0.15); }
            set { Settings.SetDouble("StartFraction", value); }
        }

        public double EndFraction
        {
            get { return Settings.GetDouble("EndFraction", 0.97); }
            set { Settings.SetDouble("EndFraction", value); }
        }

        public bool SwapLeTe
        {
            get { return Settings.GetBool("SwapLeTe", false); }
            set { Settings.SetBool("SwapLeTe", value); }
        }

        public bool DoLe
        {
            get { return Settings.GetBool("DoLe", true); }
            set { Settings.SetBool("DoLe", value); }
        }

        public bool DoTe
        {
            get { return Settings.GetBool("DoTe", true); }
            set { Settings.SetBool("DoTe", value); }
        }

        public bool BakeCutLines
        {
            get { return Settings.GetBool("BakeCutLines", true); }
            set { Settings.SetBool("BakeCutLines", value); }
        }

        public bool BakeEdgeCurves
        {
            get { return Settings.GetBool("BakeEdgeCurves", true); }
            set { Settings.SetBool("BakeEdgeCurves", value); }
        }

        /// <summary>Bake the closed nose regions the LE/TE areas were measured on.</summary>
        public bool BakeAreaRegions
        {
            get { return Settings.GetBool("BakeAreaRegions", true); }
            set { Settings.SetBool("BakeAreaRegions", value); }
        }

        /// <summary>Spar chord fraction x/c from the LE, as a function of r/R.</summary>
        public AreaDistribution SparDistribution
        {
            get
            {
                return AreaDistribution.Deserialize(
                    Settings.GetString("SparDistribution", ""),
                    AreaDistribution.ConstantAt(0.30));
            }
            set { Settings.SetString("SparDistribution", value.Serialize()); }
        }

        /// <summary>Extra length at each end of every cut line, as a fraction of
        /// its length. 0.10 pushes the surfaces 10% clear of the blade surfaces.</summary>
        public double ExtensionFraction
        {
            get { return Settings.GetDouble("ExtensionFraction", 0.10); }
            set { Settings.SetDouble("ExtensionFraction", value); }
        }

        /// <summary>What the extension percentage is measured against.</summary>
        public ExtensionBasis Basis
        {
            get
            {
                var s = Settings.GetString("ExtensionBasis", ExtensionBasis.LocalChord.ToString());
                ExtensionBasis b;
                return Enum.TryParse(s, out b) ? b : ExtensionBasis.LocalChord;
            }
            set { Settings.SetString("ExtensionBasis", value.ToString()); }
        }

        /// <summary>Which way the cut lines run inside each section plane.</summary>
        public CutDirection Direction
        {
            get
            {
                var s = Settings.GetString("CutDirection", CutDirection.Mixed.ToString());
                CutDirection d;
                return Enum.TryParse(s, out d) ? d : CutDirection.Mixed;
            }
            set { Settings.SetString("CutDirection", value.ToString()); }
        }

        public double TransitionROverR
        {
            get { return Settings.GetDouble("TransitionROverR", 0.25); }
            set { Settings.SetDouble("TransitionROverR", value); }
        }

        public double TransitionWidth
        {
            get { return Settings.GetDouble("TransitionWidth", 0.10); }
            set { Settings.SetDouble("TransitionWidth", value); }
        }

        public bool WorldZInboard
        {
            get { return Settings.GetBool("WorldZInboard", true); }
            set { Settings.SetBool("WorldZInboard", value); }
        }

        public bool DoSpar
        {
            get { return Settings.GetBool("DoSpar", false); }
            set { Settings.SetBool("DoSpar", value); }
        }

        public RadiusDatum Datum        {
            get
            {
                var s = Settings.GetString("Datum", RadiusDatum.WorldOrigin.ToString());
                RadiusDatum d;
                return Enum.TryParse(s, out d) ? d : RadiusDatum.WorldOrigin;
            }
            set { Settings.SetString("Datum", value.ToString()); }
        }

        /// <summary>Leading-edge target area as a function of r/R.</summary>
        public AreaDistribution LeDistribution
        {
            get
            {
                return AreaDistribution.Deserialize(
                    Settings.GetString("LeDistribution", ""),
                    AreaDistribution.ConstantAt(1.0));
            }
            set { Settings.SetString("LeDistribution", value.Serialize()); }
        }

        /// <summary>Trailing-edge target area as a function of r/R.
        /// Stored separately from the LE distribution.</summary>
        public AreaDistribution TeDistribution
        {
            get
            {
                return AreaDistribution.Deserialize(
                    Settings.GetString("TeDistribution", ""),
                    AreaDistribution.ConstantAt(1.0));
            }
            set { Settings.SetString("TeDistribution", value.Serialize()); }
        }
    }
}
