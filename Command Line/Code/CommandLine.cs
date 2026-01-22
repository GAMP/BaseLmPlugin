using Gizmo.Extensibility.Abstractions;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.CommandLine)]
    [PluginMetadata(
        "Command Line",
        "1.0.0.0",
        "Manages license by launching application executable with license key and executable command line parameters.",
        "BaseLmPlugin.Resources.Icons.cmd.png")]
    [LicenseManagerPlugin(KeyType = typeof(CommandLineLicenseKey))]
    public class CommandLineLicenseManager : LicenseManagerBase
    {
        public override void Install(IApplicationLicense license, Client.IExecutionContext context, ref bool forceCreation)
        {
            if (context.HasCompleted | context.AutoLaunch)
            {
                var pluginLicenseKey = license.KeyAs<ProcessLicenseKey>();

                //validate plugin license key
                if (pluginLicenseKey == null)
                    throw new ArgumentNullException(nameof(pluginLicenseKey));

                //create process for executable
                var process = context.Executable.GetProcessForExecutable(context.Profile);

                //get path to executable we used to create new process
                string executablePath = process.StartInfo.FileName;

                //ensure executable path exists
                if (!File.Exists(executablePath))
                    ExceptionHelper.ThrowExecutableNotFoundException(nameof(CommandLineLicenseManager), executablePath);

                //get executable arguments
                string executableArgument = process.StartInfo.Arguments;

                //get expanded key arguments            
                string newArguments = Environment.ExpandEnvironmentVariables(pluginLicenseKey.Value);

                if (!string.IsNullOrWhiteSpace(executableArgument))
                {
                    //compile parameters
                    process.StartInfo.Arguments = string.Format("{0} {1}", newArguments, executableArgument);
                }
                else
                {
                    //only license arguments passed
                    process.StartInfo.Arguments = newArguments;
                }

                //start process and add to context if it was started
                if (context.AddProcessIfStarted(process, true))
                {
                    //executables process creation should not be forced
                    forceCreation = false;
                }
                else
                {
                    //throw exception
                    ExceptionHelper.ThrowStartFailureException(nameof(CommandLineLicenseManager), executablePath);
                }
            }
        }
    }
}
