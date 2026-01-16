using Client;
using Gizmo.Shared.Plugins;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Obsolete()]
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Minecraft)]
    [PluginMetadata("Minecraft",
        "1.0.0.0",
        "Manages license keys by writing auth token to application configuration.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.minecraft.png")]
    [LicenseManagerPlugin(KeyType = typeof(MineCraftLicenseKey))]
    public class MineCraftLicenseManager : LicenseManagerBase
    {
        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            throw new NotImplementedException();
        }

        public override void Uninstall(IApplicationLicense license)
        {
            throw new NotImplementedException();
        }
    }
}
