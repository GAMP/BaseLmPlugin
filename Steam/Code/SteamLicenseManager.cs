using Client;
using CoreLib.Diagnostics;
using Gizmo.Shared.Plugins;
using IntegrationLib;
using SharedLib;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Steam)]
    [PluginMetadata(
        "Steam",
        "1.0.0.0",
        "Manages license keys by launching Steam with login parameters.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.steam.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(SteamLicenseManagerSettings), KeyType = typeof(SteamLicenseKey))]
    public class SteamLicenseManager : ConfigurableLicenseManagerBase,
        IExecutionDivertPlugin
    {
        private readonly TerminateWaitHandle _terminateWaitHandle = new(false);
        private int _diversionCount = 0;

        /// <summary>
        /// Gets termination wait handle.
        /// </summary>
        protected TerminateWaitHandle TerminateHandle
        {
            get { return _terminateWaitHandle; }
        }

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            if (context.HasCompleted | context.AutoLaunch)
            {
                var key = license.KeyAs<SteamLicenseKey>();

                string executablePath = context.Executable.ExecutablePath;
                string workingDirectory = context.Executable.WorkingDirectory;
                string arguments = context.Executable.Arguments;

                if (string.IsNullOrWhiteSpace(executablePath))
                    throw new ArgumentNullException("Steam executable path invalid", nameof(executablePath));

                //expand executable path
                executablePath = Environment.ExpandEnvironmentVariables(executablePath);

                //ensure executable path exists
                if (!File.Exists(executablePath))
                    ExceptionHelper.ThrowExecutableNotFoundException(nameof(SteamLicenseManager), executablePath);

                //check if working directory is set
                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    //expand working directory path
                    workingDirectory = Environment.ExpandEnvironmentVariables(context.Executable.WorkingDirectory);
                }
                else
                {
                    //if working directory not set use working directory based on executable path
                    workingDirectory = Path.GetDirectoryName(executablePath);
                }

                //check if arguments are set and expand them
                if (!string.IsNullOrWhiteSpace(arguments))
                    arguments = Environment.ExpandEnvironmentVariables(arguments);

                //create custom arguments based on login configuration
                arguments = string.Format("-login {0} {1} {2}", key.Username, key.Password, arguments);

                try
                {
                    //get process name
                    string processName = Path.GetFileNameWithoutExtension(executablePath);

                    //get existing main steam process
                    var mainSteamProcess = Process.GetProcessesByName(processName)
                        .Where(x => string.Compare(x.MainModule.FileName, executablePath, true) == 0)
                        .FirstOrDefault();

                    //check if main steam process found
                    if (mainSteamProcess != null)
                    {
                        //terminate any child processes
                        CoreProcess.KillChildren(mainSteamProcess.Id);
                        mainSteamProcess.Kill();
                    }
                }
                catch (Exception ex)
                {
                    context.WriteMessage($"Could not terminate existing steam process tree. Error :{ex.Message}.");
                }

 
                try
                {
                    var autoLoginFileName = Path.Combine(workingDirectory, "config", "loginusers.vdf");
                    if (File.Exists(autoLoginFileName))
                    {
                        File.Delete(autoLoginFileName);
                    }
                }
                catch (Exception ex)
                {
                    context.WriteMessage($"Could not delete steam auto login file. Error :{ex.Message}.");
                }

                //initialize steam process
                var streamProcess = new Process();
                streamProcess.StartInfo.FileName = executablePath;
                streamProcess.StartInfo.WorkingDirectory = workingDirectory;
                streamProcess.StartInfo.Arguments = arguments;
                streamProcess.StartInfo.UseShellExecute = false;
                streamProcess.StartInfo.WindowStyle = ProcessWindowStyle.Normal;

                //attach state event handler
                context.ExecutionStateChanged += OnExecutionStateChanged;

                //try to start steam process and add it to context in case of successful start
                if (context.AddProcessIfStarted(streamProcess, true))
                {
                    //executables process creation should not be forced
                    forceCreation = false;

                    //set LICENSEKEYUSER environment variable
                    if (!string.IsNullOrWhiteSpace(key.Username))
                        Environment.SetEnvironmentVariable("LICENSEKEYUSER", key.Username);

                    //set LICENSEKEYUSERID environment variable
                    if (!string.IsNullOrWhiteSpace(key.AccountId))
                        Environment.SetEnvironmentVariable("LICENSEKEYUSERID", key.AccountId);
                }
                else
                {
                    //detach state event handler if we failed to start the process
                    context.ExecutionStateChanged -= OnExecutionStateChanged;

                    //throw start failure exception
                    ExceptionHelper.ThrowStartFailureException(nameof(SteamLicenseManager), executablePath);
                }
            }
        }

        public override void Uninstall(IApplicationLicense license)
        {
            //reset variable
            Environment.SetEnvironmentVariable("LICENSEKEYUSER", string.Empty);
            Environment.SetEnvironmentVariable("LICENSEKEYUSERID", string.Empty);
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new SteamLicenseManagerSettings();
        }

        protected virtual void OnExecutionStateChanged(object sender, ExecutionContextStateArgs e)
        {
            //check if sender is execution context
            if (sender is not IExecutionContext context)
                return;

            //get manager settings
            var settings = SettingsAs<SteamLicenseManagerSettings>();

            //if we dont have settings then child exit matching cant be done
            if (settings == null)
                return;

            //check if child termination should be handled
            if (settings.TerminateOnChildExit == false)
                return;
  
            if (e.NewState == ContextExecutionState.ProcessExited)
            {

                //default process file name variable
                string processFileName = null;

                //try to obtain file name from new process file info class (new client)
                if (TryGetProcessInfo(e.StateObject, out var processInfo))
                {
                    //assign found process file name to local variable
                    processFileName = processInfo?.ProcessFileName;
                }

                //check if we could obtain the process file name
                if (!string.IsNullOrWhiteSpace(processFileName))
                {
                    //check if file name matches our settings
                    if (settings.IsMatch(processFileName))
                    {
                        lock (TerminateHandle)
                        {
                            //start waiting termination
                            if (!TerminateHandle.WaitingTermination)
                            {
                                //set waiting flag
                                TerminateHandle.WaitingTermination = true;

                                //assign triggered process file name
                                TerminateHandle.TerminatingProcessName = processFileName;

                                //reset wait handle
                                TerminateHandle.Reset();

                                //start async waiting
                                System.Threading.Tasks.Task.Run(() => WaitForTerminateWorker(context))
                                    .ContinueWith(t =>
                                    {
                                        Trace.WriteLine($"{nameof(SteamLicenseManager)} exception in {nameof(WaitForTerminateWorker)}, exception message {t.Exception.Message}.");
                                    }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                            }
                        }
                    }
                }
            }
            else if (e.NewState == ContextExecutionState.ProcessCreated)
            {
                //default process file name variable
                string processFileName = null;

                //try to obtain file name from new process file info class (new client)
                if (TryGetProcessInfo(e.StateObject, out var processInfo))
                {
                    //assign found process file name to local variable
                    processFileName = processInfo?.ProcessFileName;
                }

                //check if valid file name was obtained
                if (!string.IsNullOrWhiteSpace(processFileName))
                {
                    //validate process name
                    if (settings.IsMatch(processFileName))
                    {
                        lock (TerminateHandle)
                        {
                            //reset waiting
                            if (TerminateHandle.WaitingTermination)
                            {
                                TerminateHandle.WaitingTermination = false;
                                TerminateHandle.Set();
                            }
                        }
                    }
                }
            }

            //when context is no longer usable detach the state handler
            else if (e.NewState == ContextExecutionState.Finalized || e.NewState == ContextExecutionState.Destroyed || e.NewState == ContextExecutionState.Released)
                context.ExecutionStateChanged -= OnExecutionStateChanged;
        }

        public virtual bool DivertExecution(IExecutionContext context)
        {
            _diversionCount++;
            return _diversionCount <= 1;
        }

        private void WaitForTerminateWorker(IExecutionContext context)
        {
            var settings = SettingsAs<SteamLicenseManagerSettings>();

            //check if settings type is valid
            if (settings == null)
                return;

            //get amount to wait from settings
            int waitMilliseconds = (int)TimeSpan.FromSeconds(settings.ChildWaitTimeout).TotalMilliseconds;

            //start waiting
            TerminateHandle.Wait(waitMilliseconds);

            //check if waiting was reset
            if (TerminateHandle.WaitingTermination)
            {
                //kill all processes in context
                context.Kill();
            }
        }

        /// <summary>
        /// Tries to obtained process info from state object.
        /// </summary>
        /// <param name="info">Process info state object.</param>
        /// <param name="processInfo">Local process info object.</param>
        /// <returns>True for success, otherwise false.</returns>
        protected bool TryGetProcessInfo(object info, out DynamicProcessInfo processInfo)
        {
            //assign default value
            processInfo = null;

            //check if object is specified
            if (info == null)
                return false;

            try
            {
                //get type info
                var typeInfo = info.GetType();

                bool isMain = (bool)typeInfo.GetProperty("IsMain").GetValue(info);
                string processFileName = (string)typeInfo.GetProperty("ProcessFileName").GetValue(info);
                int processId = (int)typeInfo.GetProperty("ProcessId").GetValue(info);

                processInfo = new DynamicProcessInfo()
                {
                    IsMain = isMain,
                    ProcessFileName = processFileName,
                    ProcessId = processId,
                };

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }

            return false;
        }
    }

    /// <summary>
    /// Maps to a client state object that we currently have no reference to and populate dynamically with reflection.
    /// </summary>
    public class DynamicProcessInfo
    {
        /// <summary>
        /// Gets process id.
        /// </summary>
        public int ProcessId
        {
            get; set;
        }

        /// <summary>
        /// Gets if process is one of the main processes.
        /// </summary>
        public bool IsMain
        {
            get; set;
        }

        /// <summary>
        /// Gets process file name.
        /// </summary>
        public string ProcessFileName
        {
            get; set;
        }
    }
}
