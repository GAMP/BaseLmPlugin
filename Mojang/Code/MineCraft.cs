using Client;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BaseLmPlugin
{
    [Obsolete()]
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("Minecraft",
        "1.0.0.0",
        "Manages license keys by writing auth token to application configuration.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.minecraft.png")]
    public class MineCraftLicenseManager : LicenseManagerBase
    {
        #region CONSTRUCTOR
        public MineCraftLicenseManager()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnJSONAssemblyResolve;
        }
        #endregion

        #region FIELDS
        private static readonly string API_PATH = @"https://authserver.mojang.com/authenticate";
        private static readonly string API_REFRESH_PATH = @"https://authserver.mojang.com/refresh";
        private static readonly string TARGET_DIRECTORY_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        private static readonly string PROFILES_FILE_PATH = Path.Combine(TARGET_DIRECTORY_PATH, "launcher_profiles.json");
        private static readonly string ACCOUNTS_FILE_PATH = Path.Combine(TARGET_DIRECTORY_PATH, "launcher_accounts.json");
        #endregion

        #region OVERRIDES

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            throw new NotImplementedException();
        }

        public override void Uninstall(IApplicationLicense license)
        {
            if (File.Exists(PROFILES_FILE_PATH))
            {
                try
                {
                    //delete configuration file
                    File.Delete(PROFILES_FILE_PATH);
                }
                catch
                { }
            }
        }

        #endregion

        #region EVENT HANDLERS

        private Assembly OnJSONAssemblyResolve(object sender, ResolveEventArgs args)
        {
            //check if request is coming from current assembly
            if (args.RequestingAssembly != args.RequestingAssembly)
                return null;

            //get requested assembly name
            var REQUESTED_NAME = args.Name?.Split(',').FirstOrDefault();

            //check if name supplied
            if (string.IsNullOrWhiteSpace(REQUESTED_NAME))
                return null;

            //compare to desired assembly
            if (string.Compare(REQUESTED_NAME, "Newtonsoft.Json", true) == 0)
            {
                //we no longer need to handle resolve event
                AppDomain.CurrentDomain.AssemblyResolve -= OnJSONAssemblyResolve;

                //try to obtain json assembly
                return AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Where(assembly => assembly.FullName.StartsWith(REQUESTED_NAME, StringComparison.CurrentCultureIgnoreCase))
                    .FirstOrDefault();
            }

            //not desired assembly
            return null;
        }

        #endregion
    }

}
