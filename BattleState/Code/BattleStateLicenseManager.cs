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
    [Guid(Identifiers.BattleState)]
    [PluginMetadata("Battle State (Experimental)", "1.0.0.0", "Manages license keys by obtaining login tokens and using them for auto login.", "BaseLmPlugin.Resources.Icons.battlestate.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(BattleStateLicenseManagerSettings), KeyType = typeof(BattleStateLicenseKey))]
    public class BattleStateLicenseManager : SteamLicenseManager
    {
        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            throw new NotImplementedException();
        }

        public override void Uninstall(IApplicationLicense license)
        {
            throw new NotImplementedException();
        }

        public override bool DivertExecution(IExecutionContext context)
        {
            //execution should not be diverted
            return false;
        }

        public override IPluginSettings GetSettingsInstance()
        {
            //return new instance of epic settings class
            return new BattleStateLicenseManagerSettings();
        }
    }
}
