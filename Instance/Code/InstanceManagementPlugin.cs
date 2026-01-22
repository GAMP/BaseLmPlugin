using Gizmo.Extensibility.Abstractions;
using IntegrationLib;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Instance)]
    [PluginMetadata(
        "Instance",
        "1.0.0.0",
        "Manages license by limiting application instances.",
        "BaseLmPlugin.Resources.Icons.instance.png")]
    [LicenseManagerPlugin(KeyType = typeof(InstanceKey))]
    public class InstanceManagementPlugin : LicenseManagerBase
    {  
    } 
}
