using IntegrationLib;
using Microsoft.Win32;
using SharedLib;
using System;
using System.IO;

namespace BaseLmPlugin
{
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
}
