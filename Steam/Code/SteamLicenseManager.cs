using System;
using IntegrationLib;
using System.ComponentModel.Composition;
using SharedLib;
using System.Windows;
using Client;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;

namespace BaseLmPlugin
{
    #region STEAMLICENSEMANAGER
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata(
        "Steam",
        "1.0.0.0",
        "Manages license keys by launching Steam with login parameters.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.steam.png")]
    public class SteamLicenseManager : ConfigurableLicenseManagerBase,
        IExecutionDivertPlugin
    {
        #region FIELDS
        private readonly TerminateWaitHandle terminateWaitHandle = new(false);
        private int diversionCount = 0;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets termination wait handle.
        /// </summary>
        protected TerminateWaitHandle TerminateHandle
        {
            get { return terminateWaitHandle; }
        }

        #endregion

        #region OVERRIDES

        public override IApplicationLicenseKey EditLicense(IApplicationLicenseKey key, ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.Steam, key, profile);
            return context.Display(owner) ? context.Key : null;
        }

        public override IApplicationLicenseKey GetLicense(ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.Steam, new SteamLicenseKey(), profile);
            return context.Display(owner) ? context.Key : null;
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

                //initialize steam process
                var streamProcess = new Process();
                streamProcess.StartInfo.FileName = executablePath;
                streamProcess.StartInfo.WorkingDirectory = workingDirectory;
                streamProcess.StartInfo.Arguments = arguments;
                streamProcess.StartInfo.UseShellExecute = false;
                streamProcess.StartInfo.WindowStyle = ProcessWindowStyle.Normal;

                //attach state event handler
                context.ExecutionStateChaged += OnExecutionStateChaged;

                //try to start steam process and add it to context in case of successful strart
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
                    context.ExecutionStateChaged -= OnExecutionStateChaged;

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

        public override bool CanEdit
        {
            get
            {
                return true;
            }
        }

        public override UserControl GetConfigurationUI()
        {
            return new SteamSettingsView();
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new SteamLicenseManagerSettings();
        }

        #endregion

        #region VIRTUAL

        protected virtual void OnExecutionStateChaged(object sender, ExecutionContextStateArgs e)
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

            #region CHILD EXIT
            if (e.NewState == ContextExecutionState.ProcessExited)
            {
                //default process file name variable
                string processFileName = null;

                //obtain exited process id
                if (e.StateObject is int exitedProcessId)
                {
                    //get the file name of exited process, old client method
                    //if we cant obtain file name here then we dont need to process further
                    if (!context.TryGetProcessFileName(exitedProcessId, out processFileName))
                        return;                        
                }
                else
                {
                    //try to onbtain file name from new process file info class (new client)
                    if (TryGetProcessInfo(e.StateObject, out var processInfo))
                    {
                        //assign found process file name to local variable
                        processFileName = processInfo?.ProcessFileName;
                    }
                }

                //check if we could obtain the proces file name
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

                                //assign triggered proces file name
                                TerminateHandle.TerminatingProcesName = processFileName;

                                //reset wait handle
                                TerminateHandle.Reset();

                                //async begin wating
                                Action<IExecutionContext> del = new(WaitForTerminateWorker);
                                del.BeginInvoke(context, WaitForTerminateCallback, del);
                            }
                        }
                    }
                }
            }
            #endregion

            #region CHILD CREATED
            else if (e.NewState == ContextExecutionState.ProcessCreated)
            {
                //default process file name variable
                string processFileName = null;

                //obtain exited process id
                if (e.StateObject is int exitedProcessId)
                {
                    //get the file name of exited process, old client method
                    //if we cant obtain file name here then we dont need to process further
                    if (!context.TryGetProcessFileName(exitedProcessId, out processFileName))
                        return;
                }
                else
                {
                    //try to onbtain file name from new process file info class (new client)
                    if (TryGetProcessInfo(e.StateObject, out var processInfo))
                    {
                        //assign found process file name to local variable
                        processFileName = processInfo?.ProcessFileName;
                    }
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
            #endregion

            //when context is no longer usable detach the state handler
            else if (e.NewState == ContextExecutionState.Finalized || e.NewState == ContextExecutionState.Destroyed || e.NewState == ContextExecutionState.Released)
                context.ExecutionStateChaged -= OnExecutionStateChaged;
        }

        public virtual bool DivertExecution(IExecutionContext context)
        {
            diversionCount++;
            return diversionCount <= 1;
        }

        #endregion

        #region PRIVATE FUNCTIONS

        private void WaitForTerminateWorker(IExecutionContext context)
        {
            var settings = SettingsAs<SteamLicenseManagerSettings>();

            //check if settings type is valid
            if (settings == null)
                return;

            //get amount to wait from settings
            int waitMiliseconds = (int)TimeSpan.FromSeconds(settings.ChildWaitTimeout).TotalMilliseconds;

            //start waiting
            TerminateHandle.Wait(waitMiliseconds);

            //check if waiting was reset
            if (TerminateHandle.WaitingTermination)
            {
                //kill all processes in context
                context.Kill();
            }
        }

        private void WaitForTerminateCallback(IAsyncResult result)
        {
            try
            {
                //check if correct delegate is passed in the async state
                if (result.AsyncState is Action<IExecutionContext> del)
                {
                    //end invoke
                    del.EndInvoke(result);
                }
                else
                {
                    Trace.WriteLine($"{nameof(SteamLicenseManager)} invalid object passed in async state of {nameof(WaitForTerminateCallback)}.");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"{nameof(SteamLicenseManager)} exception in {nameof(WaitForTerminateCallback)}, exception message {ex.Message}.");
            }
        }

        /// <summary>
        /// Tries to obtaine process info from state object.
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
                    IsMain = isMain ,
                    ProcessFileName = processFileName ,
                    ProcessId = processId ,
                };

                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }

            return false;
        }

        #endregion
    }
    #endregion

    #region STEAMLICENSEMANAGERSETTINGS
    [Serializable]
    public class SteamLicenseManagerSettings : PropertyChangedNotificator, IPluginSettings
    {
        #region FILEDS
        private bool terminate = true;
        private string childIgnoreList = string.Empty;
        private int childWaitTimeout = 5;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets or sets if context should be terminated once a child exists.
        /// </summary>
        public bool TerminateOnChildExit
        {
            get { return terminate; }
            set
            {
                terminate = value;
                RaisePropertyChanged("TerminateOnChildExit");
            }
        }

        /// <summary>
        /// List of processes seperated by that should trigger context termination once exited.
        /// <remarks>Full name or process name can be specified. Process names should be seperated by ; mark.</remarks>
        /// </summary>
        public string ChildIgnoreList
        {
            get { return this.childIgnoreList; }
            set
            {
                this.childIgnoreList = value;
                this.RaisePropertyChanged("ChildIgnoreList");
            }
        }

        /// <summary>
        /// Amount of time to wait before terminating context.
        /// </summary>
        public int ChildWaitTimeout
        {
            get { return this.childWaitTimeout; }
            set
            {
                this.childWaitTimeout = value;
                this.RaisePropertyChanged("ChildWaitTimeout");
            }
        }

        #endregion

        #region FUNCTIONS

        /// <summary>
        /// Matches file name with current ignored child processes names.
        /// </summary>
        /// <param name="fileName">Process file name, this can be process full file name, process name with or without extnsion.</param>
        /// <returns>True if sepecified <paramref name="fileName"/>matches one of ignored child processe names.</returns>
        public bool IsMatch(string fileName)
        {
            if (!string.IsNullOrWhiteSpace(ChildIgnoreList))
            {
                string processName = Path.GetFileName(fileName);
                string processNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                string processNameWithExtension = Path.GetFileNameWithoutExtension(fileName) + ".exe";
                foreach (string childName in ChildIgnoreList.Split(';'))
                {
                    if (!string.IsNullOrWhiteSpace(childName))
                    {
                        if (processName.ToLower() == childName.ToLower() ||
                            processNameWithoutExtension.ToLower() == childName.ToLower() ||
                            processNameWithExtension.ToLower() == childName.ToLower())
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        #endregion
    }
    #endregion

    #region STEAMLICENSEKEY
    [Serializable()]
    public class SteamLicenseKey : ApplicationLicenseKeyBase
    {
        #region FIELDS
        private string
            username,
            password,
            accountId;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets or sets licenses username.
        /// </summary>
        public string Username
        {
            get { return this.username; }
            set
            {
                username = value;
                RaisePropertyChanged("Username");
            }
        }

        /// <summary>
        /// Gets or sets license password.
        /// </summary>
        public string Password
        {
            get { return password; }
            set
            {
                password = value;
                RaisePropertyChanged("Password");
            }
        }

        /// <summary>
        /// Gets or sets account id.
        /// </summary>
        public string AccountId
        {
            get { return accountId; }
            set
            {
                accountId = value;
                RaisePropertyChanged("AccountId");
            }
        }

        /// <summary>
        /// Gets if license is valid.
        /// </summary>
        public override bool IsValid
        {
            get
            {
                //both username and password must not be null or empty in order to be considered valid
                return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
            }
        }

        /// <summary>
        /// Gets license literal string representation.
        /// </summary>
        public override string KeyString
        {
            get
            {
                //by default only username will be shown as key representation.
                return Username ?? "Invalid key";
            }
        }

        #endregion

        #region OVERRIDES
        public override string ToString()
        {
            return this.KeyString;
        }
        #endregion
    }
    #endregion

    #region DYNAMICPROCESSINFO
    /// <summary>
    /// Maps to a client state object that we currently have no reference to and populate dynamically with reflection.
    /// </summary>
    public class DynamicProcessInfo
    {
        #region PROPERTIES

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
        
        #endregion
    } 
    #endregion
}
