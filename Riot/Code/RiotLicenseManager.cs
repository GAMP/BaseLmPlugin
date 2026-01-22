using Client;
using Gizmo.Extensibility.Abstractions;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Riot)]
    [PluginMetadata("Riot", "1.0.0.0", "Manages by passing credentials to Riot UX process.", "BaseLmPlugin.Resources.Icons.riot.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(RiotLicenseManagerSettings), KeyType = typeof(RiotLicenseKey))]
    public class RiotLicenseManager : SteamLicenseManager
    {
        private RiotLicenseKey _currentKey;
        private IExecutionContext _executionContext;

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            var key = license.KeyAs<RiotLicenseKey>();

            //set installed key
            _currentKey = key ?? throw new ArgumentException("Invalid key type.", nameof(key));

            RiotLogin.TerminateRiotProcesses();

            _executionContext = context;

            //detach handlers
            context.ExecutionStateChanged -= OnExecutionStateChanged;

            //attach handlers
            context.ExecutionStateChanged += OnExecutionStateChanged;
        }

        public override void Uninstall(IApplicationLicense license)
        {
            //clear installed key
            _currentKey = null;
        }

        public override bool DivertExecution(IExecutionContext context)
        {
            //execution should not be diverted
            return false;
        }

        public override IPluginSettings GetSettingsInstance()
        {
            //return new instance of epic settings class
            return new RiotLicenseManagerSettings();
        }

        protected override void OnExecutionStateChanged(object sender, ExecutionContextStateArgs e)
        {
            base.OnExecutionStateChanged(sender, e);

            //if we don't have installed key then there is nothing to do
            if (_currentKey == null)
                return;

            if (e.NewState == SharedLib.ContextExecutionState.ProcessCreated)
            {
                //local process id variable
                int? processId = null;

                //try to obtain process id with process info object (new client version)
                if (TryGetProcessInfo(e.StateObject, out var processInfo))
                {
                    processId = processInfo?.ProcessId;
                    DebugMessage(string.Format("New process started {0}, path {1}", processInfo?.ProcessId.ToString() ?? "Unknown", processInfo?.ProcessFileName ?? "Unknown"));
                }

                //check if process id was obtained
                if (processId.HasValue)
                {
                    try
                    {
                        //executable name
                        string executableName = null;

                        //check if process info contains process file name
                        if (!string.IsNullOrWhiteSpace(processInfo.ProcessFileName))
                        {
                            //get executable name
                            executableName = Path.GetFileName(processInfo.ProcessFileName);
                            DebugMessage(string.Format("Riot execution context created process, file name {0}", executableName));
                        }

                        if (RiotLogin.IsRiotProcess(executableName))
                        {
                            DebugMessage(string.Format("Matched riot client executable {0}.", executableName));

                            //get username
                            string username = _currentKey?.Username;

                            //get password
                            string password = _currentKey?.Password;

                            //check for validity
                            if (username == null || password == null)
                                return;

                            var success = RiotLogin.InputLogin(processId.Value, username, password);

                            if (success)
                            {
                                //clear the key so no subsequent logins would be made on new Riot Client.exe creation
                                _currentKey = null;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        //ignore
                    }
                    catch (ArgumentException)
                    {
                        //process exited
                    }
                    catch
                    {
                        //ignore
                    }
                }
            }
        }

        private void DebugMessage(string message)
        {
#if DEBUG
            _executionContext?.WriteMessage(message);
#endif
        }
    }
}
