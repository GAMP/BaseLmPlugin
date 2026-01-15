using IntegrationLib;
using System.ComponentModel.Composition;
using System.Windows;

namespace BaseLmPlugin
{
    #region InstanceManagementPlugin
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata(
        "Instance",
        "1.0.0.0",
        "Manages license by limiting application istances.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.instance.png")]
    public class InstanceManagementPlugin : LicenseManagerBase
    {  
    } 
    #endregion
}
