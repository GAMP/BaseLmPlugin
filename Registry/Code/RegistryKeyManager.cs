using Gizmo.Extensibility.Abstractions;
using IntegrationLib;
using Microsoft.Win32;
using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Registry)]
    [PluginMetadata("Registry", "1.0.0.0", "Manages license keys by setting key values in system registry.", "BaseLmPlugin.Resources.Icons.registry.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(RegistryLicenseManagerSettings), KeyType = typeof(RegistryLicenseKey))]
    public class RegistryLicenseManager : ConfigurableLicenseManagerBase
    {
        public override void Install(IApplicationLicense license, Client.IExecutionContext context, ref bool forceCreation)
        {
            //gte current settings
            var settings = SettingsAs<RegistryLicenseManagerSettings>();

            //check if correct registry path specified
            if (string.IsNullOrWhiteSpace(settings.RegistryPath))
                throw new ArgumentNullException(nameof(settings.RegistryPath));

            //get license key
            var licenseKey = license.KeyAs<RegistryLicenseKey>();
            if (licenseKey == null)
                throw new ArgumentException("Specified license key is invalid.");

            //get key path
            var keyPath = settings.KeyPath;

            //key path can be null if invalid registry path specified
            //we already check that in the upper statement but just in case
            if (string.IsNullOrWhiteSpace(keyPath))
                throw new ArgumentNullException(nameof(keyPath));

            //expand any environment variables
            keyPath = Environment.ExpandEnvironmentVariables(keyPath);

            var valueName = string.Empty;

            //value name can be null or empty
            //in that case a default value will be used
            if (!string.IsNullOrWhiteSpace(settings.ValueName))
                valueName = Environment.ExpandEnvironmentVariables(settings.ValueName);

            //check if license key contains value
            if (string.IsNullOrWhiteSpace(licenseKey.Value))
                throw new ArgumentNullException(nameof(licenseKey.Value), "License key value is invalid.");

            //assign key value to local variable
            var stringKeyValue = licenseKey.Value;

            //initialize key value
            //this is the value that will be written to registry
            object keyValue = null;

            if (settings.ValueKind == RegistryValueKind.Binary)
            {
                try
                {
                    string[] bytes = stringKeyValue.Split(',');
                    byte[] array = new byte[bytes.Count()];
                    int index = 0;
                    foreach (var stringByte in bytes)
                    {
                        array[index] = byte.Parse(stringByte, System.Globalization.NumberStyles.HexNumber);
                        index++;
                    }
                    keyValue = array;
                }
                catch (Exception ex)
                {
                    throw new Exception("Could not convert binary data.", ex);
                }
            }
            else
            {
                keyValue = Environment.ExpandEnvironmentVariables(stringKeyValue);
            }

            RegistryKey baseKey = null;
            switch (settings.Hive)
            {
                case RegistryHive.Users:
                    baseKey = Registry.Users;
                    break;
                case RegistryHive.PerformanceData:
                    baseKey = Registry.PerformanceData;
                    break;
                case RegistryHive.ClassesRoot:
                    baseKey = Registry.ClassesRoot;
                    break;
                case RegistryHive.CurrentConfig:
                    baseKey = Registry.CurrentConfig;
                    break;
                case RegistryHive.CurrentUser:
                    baseKey = Registry.CurrentUser;
                    break;
                case RegistryHive.LocalMachine:
                    baseKey = Registry.LocalMachine;
                    break;
                default:
                    throw new ArgumentException("Invalid registry hive specified.");
            }

            var destinationKey = baseKey.OpenSubKey(keyPath, true);

            try
            {
                if (destinationKey == null)
                    destinationKey = baseKey.CreateSubKey(keyPath, true);

                destinationKey.SetValue(valueName, keyValue, settings.ValueKind);
                destinationKey.Close();
            }
            catch
            {
                throw;
            }
            finally
            {
                destinationKey?.Close();
            }
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new RegistryLicenseManagerSettings();
        }
    }
}
