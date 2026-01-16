using Client;
using GizmoShell;
using IntegrationLib;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using WindowsInput;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("Riot", "1.0.0.0", "Manages by passing credentials to Riot UX process.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.riot.png")]
    public class RiotLicenseManager : SteamLicenseManager
    {
        #region PROPERTIES

        /// <summary>
        /// Gets or sets installed key.
        /// </summary>
        private RiotLicenseKey InstalledKey
        {
            get; set;
        }

        private IExecutionContext _executionContext;

        #endregion

        #region OVERRIDES

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            var key = license.KeyAs<RiotLicenseKey>();

            //set installed key
            InstalledKey = key ?? throw new ArgumentException("Invalid key type.", nameof(key));

            RiotLogin.TerminateRiotProcesses();

            _executionContext = context;

            //detach handlers
            context.ExecutionStateChaged -= OnExecutionStateChanged;

            //atach handlers
            context.ExecutionStateChaged += OnExecutionStateChanged;
        }

        public override void Uninstall(IApplicationLicense license)
        {
            //clear installed key
            InstalledKey = null;
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

            //if we dont have installed key then there is nothing to do
            if (InstalledKey == null)
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
                            string username = InstalledKey?.Username;

                            //get password
                            string password = InstalledKey?.Password;

                            //check for validity
                            if (username == null || password == null)
                                return;

                            var sucess = RiotLogin.InputLogin(processId.Value, username, password);

                            if (sucess)
                            {
                                //clear the key so no subsequent logins would be made on new Riot Client.exe creation
                                InstalledKey = null;
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



        #endregion
    }

    #region RIOTLOGIN
    public class RiotLogin
    {
        #region PUBLIC FUNCTIONS

        public static bool InputLogin(int processId, string username, string password)
        {
            if (Monitor.TryEnter(_lock))
            {
                for (int tries = 0; tries < 3; tries++)
                {
                    try
                    {
                        if (!WaitForWindowCreated(riotClientWindowNames, 10000, out var processes, false))
                        {
                            Debug.WriteLine("Riot client window was not found after 10 seconds of wait time.");
                            continue;
                        }

                        var windowHandle = processes.Select(process =>
                        {
                            try
                            {
                                var handle = process.MainWindowHandle;
                                return handle;
                            }
                            catch
                            {
                                return IntPtr.Zero;
                            }
                        }).FirstOrDefault();

                        if (windowHandle == IntPtr.Zero)
                        {
                            Debug.WriteLine("Could not obtain main window handle.");
                            continue;
                        }

                        try
                        {
#if RELEASE
                            //block user input
                            User32.BlockInput(true);
#endif
                            WindowInfo windowInfo = new WindowInfo(windowHandle);

                            windowInfo.Activate();
                            Thread.Sleep(5000);
                            KeyboardSimulator keyboard = new();
                            MouseSimulator mouse = new();

                            var x = windowInfo.Location.X + 72;
                            var y = windowInfo.Location.Y + 282;

                            System.Windows.Forms.Cursor.Position = new(x, y);

                            //this should move the cursor to username input field
                            mouse.LeftButtonClick();

                            Thread.Sleep(1000);

                            keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                            keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);

                            keyboard.TextEntry(username);
                            keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);

                            Thread.Sleep(1000);

                            keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                            keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);

                            keyboard.TextEntry(password);
                            keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                            return true;
                        }
                        catch
                        {
                            throw;
                        }
                        finally
                        {
#if RELEASE
                            //block user input
                            User32.BlockInput(false);
#endif
                        }

                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                    finally
                    {
                        Monitor.Exit(_lock);
                    }
                }
            }

            return false;
        }

        private readonly static object _lock = new();
        private static string[] riotClientWindowNames = ["Riot Client Main", "Riot Client"];

        private static bool WaitForWindowCreated(IEnumerable<string> windowTitles, int timeOut, out IEnumerable<Process> foundProcesses, bool throwOnErrors = false)
        {
            windowTitles ??= [];

            //wait period
            int wait_period = 100;

            //create time span
            TimeSpan waitSpan = TimeSpan.FromMilliseconds(timeOut);

            foundProcesses = new List<Process>();

            #region Wait
            //wait untill span expires
            while (waitSpan.TotalMilliseconds > 0)
            {
                try
                {
                    //get matching processes
                    foundProcesses = Process.GetProcesses().Where(process => windowTitles.Any(windowTitle => string.Compare(process.MainWindowTitle, windowTitle, StringComparison.OrdinalIgnoreCase) == 0));

                    //check if process with specified title exists
                    if (foundProcesses.Count() > 0)
                        return true;

                    //sleep for wait period
                    System.Threading.Thread.Sleep(wait_period);

                    //remove passed period from total wait span
                    waitSpan = waitSpan.Subtract(TimeSpan.FromMilliseconds(wait_period));
                }
                catch
                {
                    //throw error
                    if (throwOnErrors) { throw; }
                    //error?
                    break;
                }
            }

            //timespan expired
            return false;
            #endregion
        }

        public static bool IsRiotProcess(string executableName)
        {
            return !string.IsNullOrEmpty(executableName) &&
            string.Compare(executableName, "RiotClientServices.exe", StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(executableName, "RiotClientUxRender.exe", StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(executableName, "RiotClientUx.exe", StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(executableName, "RiotClient.exe", StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string[] riotProcessNames = new[] { "RiotClientServices", "RiotClientUxRender", "RiotClientUx", "RiotClient" };

        public static void TerminateRiotProcesses()
        {
            foreach (var processName in riotProcessNames)
            {
                try
                {
                    var processes = Process.GetProcessesByName(processName);

                    foreach (var item in processes)
                    {
                        try
                        {
                            item.Kill();
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
        }

        #endregion
    }
    #endregion

    #region RIOTLICENSEMANAGERSETTINGS
    [Serializable]
    public class RiotLicenseManagerSettings : SteamLicenseManagerSettings, IPluginSettings
    {
    }
    #endregion
}
