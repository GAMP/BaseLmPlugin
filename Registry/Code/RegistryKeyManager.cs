using System;
using System.Linq;
using System.Windows.Controls;
using Microsoft.Win32;
using IntegrationLib;
using SharedLib;
using System.ComponentModel.Composition;
using System.Windows;
using System.IO;

namespace BaseLmPlugin
{
    #region RegistryLicenseManager
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("Registry", "1.0.0.0", "Manages license keys by setting key values in system registry.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.registry.png")]
    public class RegistryLicenseManager : ConfigurableLicenseManagerBase
    {
        #region OVERRIDES

        public override IApplicationLicenseKey GetLicense(ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.Registry, new RegistryLicenseKey(), profile);
            if (context.Display(owner))
            {
                return context.Key;
            }
            else
            {
                return null;
            }
        }

        public override IApplicationLicenseKey EditLicense(IApplicationLicenseKey key, ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.Registry, key, profile);
            if (context.Display(owner))
            {
                return context.Key;
            }
            else
            {
                return null;
            }
        }

        public override void Install(IApplicationLicense license, Client.IExecutionContext context, ref bool forceCreation)
        {
            #region INITIALIZE VARIABLES

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
            //we alredy check that in the upper statement but just in case
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
            //this is the value that will be writtent to registry
            object keyValue = null;

            if (settings.ValueKind == RegistryValueKind.Binary)
            {
                #region CONVERT STRING TO BINARY
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
                #endregion
            }
            else
            {
                keyValue = Environment.ExpandEnvironmentVariables(stringKeyValue);
            }

            #endregion

            #region GET BASE KEY
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
            #endregion

            #region SET VALUES

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

            #endregion
        }

        #endregion

        #region ICONFIGURABLE

        public override UserControl GetConfigurationUI()
        {
            return new RegistrySettingsView();
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new RegistryLicenseManagerSettings();
        }

        public override bool CanEdit
        {
            get
            {
                return true;
            }
        }

        #endregion
    }
    #endregion

    #region RegistryLicenseKey
    [Serializable()]
    public class RegistryLicenseKey : ApplicationLicenseKeyBase
    {
        public override bool IsValid
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Value);
            }
        }

        public override string KeyString
        {
            get
            {
                return string.IsNullOrWhiteSpace(Value) ? null : Value.Split(Environment.NewLine.ToCharArray()).FirstOrDefault();
            }
        }
    }
    #endregion

    #region RegistryLicenseManagerSettings
    [Serializable()]
    public class RegistryLicenseManagerSettings : PropertyChangedNotificator, IPluginSettings
    {
        #region FILEDS
        private string registryPath;
        private RegistryHive registryHive = RegistryHive.CurrentUser;
        RegistryValueKind valueKind = RegistryValueKind.String;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets or sets hive of this registry key.
        /// </summary>
        public RegistryHive Hive
        {
            get { return registryHive; }
            set
            {
                registryHive = value;
                RaisePropertyChanged("Hive");
            }
        }

        /// <summary>
        /// Gets or sets path to the registry key.
        /// </summary>
        public string RegistryPath
        {
            get { return registryPath; }
            set
            {
                registryPath = value;
                RaisePropertyChanged("RegistryPath");
            }
        }

        /// <summary>
        /// Gets or sets the value kind of registry key.
        /// </summary>
        public RegistryValueKind ValueKind
        {
            get { return valueKind; }
            set
            {
                valueKind = value;
                RaisePropertyChanged("ValueKind");
            }
        }

        /// <summary>
        /// Gets normalized registry path.
        /// </summary>
        public string KeyPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RegistryPath))
                {
                    string normalizedPath = RegistryPath;
                    if (normalizedPath.StartsWith(@"\"))
                    {
                        normalizedPath = normalizedPath.Remove(0, 1);
                    }
                    if (!normalizedPath.EndsWith(@"\"))
                    {
                        return Path.GetDirectoryName(normalizedPath);
                    }
                    else
                    {
                        return normalizedPath;
                    }
                }
                else
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Gets the value name.
        /// <remarks>Returns empty string if no value name is present in the registry path.</remarks>
        /// </summary>
        public string ValueName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RegistryPath))
                {
                    if (!RegistryPath.EndsWith(@"\"))
                    {
                        return Path.GetFileName(RegistryPath);
                    }
                    else
                    {
                        return string.Empty;
                    }
                }
                else
                {
                    return string.Empty;
                }
            }
        }

        #endregion
    }
    #endregion
}
