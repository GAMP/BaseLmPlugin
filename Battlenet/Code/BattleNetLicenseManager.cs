using Client;
using CoreLib.Diagnostics;
using Gizmo.Shared.Plugins;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Battlenet)]
    [PluginMetadata("Battle.NET",
        "1.0.0.0",
        "Manages license keys by sending credentials input to application window.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.battlenet.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(BattleNetLicenseManager), KeyType = typeof(BattleNetLicenseKey))]
    public class BattleNetLicenseManager : SteamLicenseManager
    {
        private readonly string[] processImageFileNames =
        [
            @"Battle.net Launcher",
            @"Battle.net",
            @"BlizzardError",
            @"Agent",
            @"Battle.net Helper",
            @"SystemSurvey"
        ];

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            if (context.HasCompleted | context.AutoLaunch)
            {
                var pluginSettings = SettingsAs<BattleNetManagerSettings>();
                var pluginLicenseKey = license.KeyAs<BattleNetLicenseKey>();

                //validate plugin settings
                if (pluginSettings == null)
                    throw new ArgumentNullException(nameof(pluginSettings));

                //validate plugin license key
                if (pluginLicenseKey == null)
                    throw new ArgumentNullException(nameof(pluginLicenseKey));

                //validate plugin license key username
                if (string.IsNullOrWhiteSpace(pluginLicenseKey.Username))
                    throw new ArgumentNullException(nameof(pluginLicenseKey.Username));

                //validate plugin license key password
                if (string.IsNullOrWhiteSpace(pluginLicenseKey.Password))
                    throw new ArgumentNullException(nameof(pluginLicenseKey.Password));

                //get full path to executable
                string executablePath = Environment.ExpandEnvironmentVariables(context.Executable.ExecutablePath);

                //ensure executable path exists
                if (!File.Exists(executablePath))
                    ExceptionHelper.ThrowExecutableNotFoundException(nameof(BattleNetLicenseManager), executablePath);

                //if working directory not specified set it to null
                string workingDirectory = string.IsNullOrWhiteSpace(context.Executable.WorkingDirectory) ? null : Environment.ExpandEnvironmentVariables(context.Executable.WorkingDirectory);

                string username = pluginLicenseKey.Username;
                string password = pluginLicenseKey.Password;

                //ensure no battlenet processes running
                foreach (var processModuleFileName in processImageFileNames)
                {
                    if (string.IsNullOrWhiteSpace(processModuleFileName))
                        continue;

                    var processList = Process.GetProcessesByName(processModuleFileName);
                    processList.ToList().ForEach(x =>
                    {
                        try
                        {
                            x.Kill();
                        }
                        catch
                        {
                            Trace.WriteLine(string.Format("Could not kill BattleNet process {0}", processModuleFileName));
                        }
                    });
                }

                string localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
                string appDataFolder = Path.Combine(localAppDataPath, "Battle.net");

                try
                {
                    if (Directory.Exists(appDataFolder))
                    {
                        Directory.Delete(appDataFolder,true);
                    }
                }
                catch (Exception)
                {
                    context.WriteMessage("Error deleting battlenet app data folder.");
                }

                //create process start info
                ProcessStartInfo startInfo = new()
                {
                    FileName = executablePath,
                    WorkingDirectory = workingDirectory
                };

                //create process class
                Process targetProcess = new() { StartInfo = startInfo };

                //attach state event handler
                context.ExecutionStateChanged += OnExecutionStateChanged;

                //try to start process and add it to execution context
                if (context.AddProcessIfStarted(targetProcess, true))
                {
                    //once process has started we will be waiting for exit
                    var targetExited = targetProcess.WaitForExit(5000);

                    int childProcessId = 0;
                    if (targetExited)
                    {
                        //once parent process has exited we will need to enumerate its children
                        var childProcesses = CoreProcess.EnumerateChildren(targetProcess.Id);

                        //first child process should be our target process
                        childProcessId = childProcesses.FirstOrDefault();
                    }
                    else
                    {
                        //check if target process has main window and use it instead of child process main window
                        if (targetProcess.MainWindowHandle != IntPtr.Zero)
                            childProcessId = targetProcess.Id;
                    }

                    //check if child process obtained
                    if (childProcessId == 0)
                        return;

                    try
                    {
                        #if RELEASE
                        //block user input
                        Win32API.Modules.User32.BlockInput(true);
                        #endif

                        //simulate login
                        BattleNetSimulatedLogin.Login(childProcessId, username, password);
                    }
                    catch
                    {
                        throw;
                    }
                    finally
                    {
#if RELEASE
                        //unblock user input
                        Win32API.Modules.User32.BlockInput(false); 
#endif
                    }
                }
                else
                {
                    //detach state event handler if we failed to start the process
                    context.ExecutionStateChanged -= OnExecutionStateChanged;

                    //throw exception
                    ExceptionHelper.ThrowStartFailureException(nameof(BattleNetLicenseManager), executablePath);
                }
            }
        }

        public override void Uninstall(IApplicationLicense license)
        {
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new BattleNetManagerSettings();
        }
    }
}
