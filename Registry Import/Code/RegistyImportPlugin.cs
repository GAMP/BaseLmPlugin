using Gizmo.Extensibility.Abstractions;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.RegistryImport)]
    [PluginMetadata("Registry Import","1.0.0.0","Manages license keys by importing registry file in system registry.","BaseLmPlugin.Resources.Icons.registry.png")]
    [LicenseManagerPlugin(KeyType = typeof(RegistryLicenseKey))]
    public class RegistyImportPlugin : LicenseManagerBase
    {
        public override void Install(IApplicationLicense license, Client.IExecutionContext context, ref bool processCreated)
        {
            //get the license key
            RegistryLicenseKey key = license.KeyAs<RegistryLicenseKey>();

            if (key == null || string.IsNullOrWhiteSpace(key.Value))
                throw new ArgumentNullException("Invalid key or key value");

            //expand and get environment string
            string registryString = Environment.ExpandEnvironmentVariables(key.Value);

            //create registry file
            var regFile = new CoreLib.Registry.CoreRegistryFile();

            //load file from string
            regFile.LoadFromString(registryString);

            //import into registry
            regFile.Import();
        }
    }
}
