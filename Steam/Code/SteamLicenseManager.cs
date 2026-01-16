using System;
using IntegrationLib;
using System.ComponentModel.Composition;
using SharedLib;
using System.Windows;
using Client;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Threading;
using Win32API.Modules;
using WindowsInput;
using CoreLib.Diagnostics;
using System.Linq;

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

                #region KILL EXISTING

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

                #endregion

                #region DELETE AUTO LOGIN DATA

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

                #endregion

                //initialize steam process
                var streamProcess = new Process();
                streamProcess.StartInfo.FileName = executablePath;
                streamProcess.StartInfo.WorkingDirectory = workingDirectory;
                streamProcess.StartInfo.Arguments = arguments;
                streamProcess.StartInfo.UseShellExecute = false;
                streamProcess.StartInfo.WindowStyle = ProcessWindowStyle.Normal;

                //attach state event handler
                context.ExecutionStateChaged += OnExecutionStateChanged;

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

                    ////send proccess input
                    //SendProcessInput(streamProcess,key.Username,key.Password);
                }
                else
                {
                    //detach state event handler if we failed to start the process
                    context.ExecutionStateChaged -= OnExecutionStateChanged;

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

        #endregion

        #region VIRTUAL

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

            #region CHILD EXIT
            if (e.NewState == ContextExecutionState.ProcessExited)
            {

                //default process file name variable
                string processFileName = null;

                //try to onbtain file name from new process file info class (new client)
                if (TryGetProcessInfo(e.StateObject, out var processInfo))
                {
                    //assign found process file name to local variable
                    processFileName = processInfo?.ProcessFileName;
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
            #endregion

            #region CHILD CREATED
            else if (e.NewState == ContextExecutionState.ProcessCreated)
            {
                //default process file name variable
                string processFileName = null;

                //try to onbtain file name from new process file info class (new client)
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
            #endregion

            //when context is no longer usable detach the state handler
            else if (e.NewState == ContextExecutionState.Finalized || e.NewState == ContextExecutionState.Destroyed || e.NewState == ContextExecutionState.Released)
                context.ExecutionStateChaged -= OnExecutionStateChanged;
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

        #endregion

        private static void SendProcessInput(Process targetProcess, string username, string password)
        {
            int SMALL_DELAY = 250;
            int MEDIUM_DELAY = 1500;
            int LARGE_DELAY = 3000;
            int EXTREME_DELAY = 20000;

            //default center location based on window size
            int DEFAULT_X = 300;

            //wait for the process window to be created
            if (CoreProcess.WaitForWindowCreated(targetProcess.Id, EXTREME_DELAY) == false)
                return;

            //add a large delay so the main window can initialize
            Thread.Sleep(LARGE_DELAY);

            //get the main window instance
            GizmoShell.WindowInfo window = new(targetProcess.MainWindowHandle);

            try
            {
#if RELEASE
                //block user input
                User32.BlockInput(true);
                User32.ShowCursor(false);
#endif
                //check if window is minimized and restore it
                if (window.IsMinimized)
                    User32.ShowWindow(window.Handle, Win32API.Headers.WinUser.Enumerations.SW.SW_RESTORE);

                //bring main window to front
                window.BringToFront();

                //add medium dealy to allow window to activate
                Thread.Sleep(MEDIUM_DELAY);

                //bring main window to front
                window.BringToFront();

                //create simulators
                KeyboardSimulator keyboard = new();
                MouseSimulator mouse = new();

                //initial screen
                Thread.Sleep(SMALL_DELAY);
                //bring main window to front
                window.BringToFront();

                var location = window.Location;
                NativeMethods.SetCursorPos(location.X + DEFAULT_X, location.Y + 140);
                Thread.Sleep(SMALL_DELAY);
                mouse.LeftButtonDoubleClick();
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(username);

                NativeMethods.SetCursorPos(location.X + DEFAULT_X, location.Y + 220);
                Thread.Sleep(SMALL_DELAY);
                mouse.LeftButtonDoubleClick();
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(password);

                //send enter key to initiate login
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.RETURN);
            }
            catch
            {
                throw;
            }
            finally
            {
#if RELEASE
                //unlock user input
                User32.BlockInput(false);
                User32.ShowCursor(true);
#endif
            }

        }
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
                        if (string.Compare(processName, childName, StringComparison.OrdinalIgnoreCase) == 0 ||
                            string.Compare(processNameWithoutExtension, childName, StringComparison.OrdinalIgnoreCase) == 0 ||
                            string.Compare(processNameWithExtension, childName, StringComparison.OrdinalIgnoreCase) == 0)
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
