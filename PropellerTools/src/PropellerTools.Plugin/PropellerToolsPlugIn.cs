using System;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

namespace PropellerTools.Plugin
{
    /// <summary>
    /// Plugin entry point. The GUID must stay stable across releases - Rhino keys plugin
    /// settings and enable/disable state to it - but must differ from every other plugin.
    /// </summary>
    [Guid("92A77F9F-7218-424B-846B-0D7774D26A30")]
    public class PropellerToolsPlugIn : PlugIn
    {
        public static PropellerToolsPlugIn Instance { get; private set; }

        public PropellerToolsPlugIn() { Instance = this; }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Touch a Core type so a missing or mismatched PropellerTools.Core surfaces here
            // with a readable message, instead of as a bare "initialization failed" dialog
            // raised later during type enumeration.
            try
            {
                var t = typeof(PropellerTools.Core.RovingAreaSolver);
                if (t == null)
                {
                    errorMessage = "PropellerTools.Core could not be resolved.";
                    return LoadReturnCode.ErrorShowDialog;
                }
            }
            catch (Exception ex)
            {
                errorMessage =
                    "PropellerTools.Core failed to load. Ensure PropellerTools.Core.dll " +
                    "(version 1.0.0.0) sits in the same folder as PropellerTools.rhp. " +
                    "Details: " + ex.Message;
                return LoadReturnCode.ErrorShowDialog;
            }

            return LoadReturnCode.Success;
        }
    }
}
