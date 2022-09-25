using System;
using System.Linq;
using IntegrationLib;
using System.ComponentModel.Composition;
using System.IO;
using Client;
using System.Windows;
using System.Diagnostics;
using CoreLib.Diagnostics;
using System.Threading;
using Win32API.Modules;
using WindowsInput;

namespace BaseLmPlugin
{
    #region ORIGINLICENSEMANAGER
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("EA Origin", "1.0.0.0", "Manages by launching origin process with user code token.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.origin.png")]
    public class OriginLicenseManager : SteamLicenseManager,
        IExecutionDivertPlugin
    {
        #region INTERFACE

        public override IApplicationLicenseKey EditLicense(IApplicationLicenseKey key, ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.UserNamePassword, key, profile);
            if (context.Display(owner))
            {
                return context.Key;
            }
            else
            {
                return null;
            }
        }

        public override IApplicationLicenseKey GetLicense(ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.UserNamePassword, new OriginLicenseKey(), profile);
            if (context.Display(owner))
            {
                return context.Key;
            }
            else
            {
                return null;
            }
        }

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            if (context.HasCompleted | context.AutoLaunch)
            {
                #region VALIDATION

                //get installation directory
                string executablePath = context.Executable.ExecutablePath;

                if (string.IsNullOrWhiteSpace(executablePath))
                    throw new ArgumentNullException("Origin client executable path cannot be null or empty.");

                executablePath = Environment.ExpandEnvironmentVariables(executablePath);

                //ensure executable path exists
                if (!File.Exists(executablePath))
                    ExceptionHelper.ThrowExecutableNotFoundException(nameof(OriginLicenseManager), executablePath);

                #endregion                

                #region VARIABLES

                //get origin key
                var originKey = license.KeyAs<OriginLicenseKey>();

                //username variable
                string USERNAME = originKey.Username;

                //pasword variable
                string PASSWORD = originKey.Password;

                #endregion

                #region TERMINATE EXISTING

                //GET EXECUTABLE NAME
                string processName = Path.GetFileNameWithoutExtension(executablePath);

                //FIND EXISTING
                var originProcess = Process.GetProcessesByName(processName)
                    .Where(x => string.Compare(x.MainModule.FileName, executablePath, true) == 0)
                    .FirstOrDefault();

                //TERMINATE EXISTING
                if (originProcess != null)
                    originProcess.Kill();

                //DELETE CONFIGURATION
                var USER_CONFIG_FILE = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Origin", "local.xml");
                var GLOBAL_CONFIG_FILE = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Origin", "local.xml");

                if (File.Exists(USER_CONFIG_FILE))
                    File.Delete(USER_CONFIG_FILE);

                if (File.Exists(GLOBAL_CONFIG_FILE))
                    File.Delete(GLOBAL_CONFIG_FILE);

                #endregion

                #region INITIALIZE PROCESS

                string COMMAND_LINE = context.Executable.Arguments;
                string PROCESS_COMMAND_LINE = COMMAND_LINE;

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = PROCESS_COMMAND_LINE,
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    ErrorDialog = false,
                    UseShellExecute = false
                };

                //create origin process
                originProcess = new Process() { StartInfo = startInfo };

                #endregion

                #region START ORIGIN

                //attach state event handler
                context.ExecutionStateChaged += OnExecutionStateChaged;

                //add the process to tracked process if successfully started
                if (context.AddProcessIfStarted(originProcess, true))
                {
                    //send input to the process window
                    SendProcessInput(originProcess, USERNAME, PASSWORD);

                    //executables process creation should not be forced
                    forceCreation = false;  
                }
                else
                {
                    //detach state event handler if we failed to start the process
                    context.ExecutionStateChaged -= OnExecutionStateChaged;

                    //throw exception
                    ExceptionHelper.ThrowStartFailureException(nameof(OriginLicenseManager), executablePath);
                }
                #endregion
            }
        }

        public override bool CanEdit
        {
            get
            {
                return true;
            }
        }

        public override System.Windows.Controls.UserControl GetConfigurationUI()
        {
            return new SteamSettingsView();
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new OriginLicenseManagerSettings();
        }

        #endregion

        #region FUNCTIONS
        private static void SendProcessInput(Process targetProcess, string username, string password)
        {
            int SMALL_DELAY = 250;
            int MEDIUM_DELAY = 1500;
            int LARGE_DELAY = 3000;
            int EXTREME_DELAY = 20000;

            //default center location based on window size
            int DEFAULT_X = 248;

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
                User32.SetCursorPos(location.X + DEFAULT_X, location.Y + 210);
                Thread.Sleep(SMALL_DELAY);
                mouse.LeftButtonDoubleClick();
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(username);

                User32.SetCursorPos(location.X + DEFAULT_X, location.Y + 255);
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
        #endregion
    }
    #endregion

    #region ORIGINLICENSEMANAGERSETTINGS
    [Serializable]
    public class OriginLicenseManagerSettings : SteamLicenseManagerSettings, IPluginSettings
    {
    }
    #endregion
}
