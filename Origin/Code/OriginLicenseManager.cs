using Client;
using Gizmo.Extensibility;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Obsolete()]
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Origin)]
    [PluginMetadata("EA Origin", "1.0.0.0", "Manages by launching origin process with user code token.", "BaseLmPlugin.Resources.Icons.origin.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(OriginLicenseManagerSettings), KeyType = typeof(OriginLicenseKey))]
    public class OriginLicenseManager : SteamLicenseManager,
        IExecutionDivertPlugin
    {
        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            throw new NotImplementedException();
        }

        public override IPluginSettings GetSettingsInstance()
        {
            throw new NotImplementedException();
        }    
    }    
}
