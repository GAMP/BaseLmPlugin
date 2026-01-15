using Client;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;

namespace BaseLmPlugin
{
    [Obsolete()]
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("Battle State (Experimental)", "1.0.0.0", "Manages license keys by obtaining login tokens and using them for auto login.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.battlestate.png")]
    public class BattleStateLicenseManager : SteamLicenseManager
    {
        #region OVERRIDES

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            var key = license.KeyAs<BattleStateLicenseKey>();
            if (key == null)
                throw new ArgumentException("Invalid battle station key specified.", nameof(key));

            string username = key.Username;
            string password = key.Password;

            //validate parameters here

            try
            {
                BattleStateLogin.LoginAsync(username, password)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (BattleStateLoginException ex)
            {
                throw new Exception(ex.Message);
            }
            catch
            {
                throw;
            }

            //detach handlers
            context.ExecutionStateChaged -= OnExecutionStateChaged;

            //atach handlers
            context.ExecutionStateChaged += OnExecutionStateChaged;
        }

        public override void Uninstall(IApplicationLicense license)
        {
            //do nothing
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

        #endregion
    }

    #region BATTLESTATELICENSEMANAGERSETTINGS
    [Serializable]
    public class BattleStateLicenseManagerSettings : SteamLicenseManagerSettings, IPluginSettings
    {
    }
    #endregion
}
