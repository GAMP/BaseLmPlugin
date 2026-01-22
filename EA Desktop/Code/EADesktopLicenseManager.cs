using Client;
using CoreLib.Diagnostics;
using Gizmo.Extensibility;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Win32API.Modules;
using WindowsInput;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.EADesktop)]
    [PluginMetadata("EA Desktop", "1.0.0.0", "Manages by launching ea desktop process with auth token.", "BaseLmPlugin.Resources.Icons.eadesktop.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(EADesktopLicenseManager), KeyType = typeof(EADesktopLicenseKey))]
    public class EADesktopLicenseManager : SteamLicenseManager,
        IExecutionDivertPlugin
    {
        readonly string[] processKillList = ["EALauncher", "EALaunchHelper", "EADesktop"];

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            if (context.HasCompleted | context.AutoLaunch)
            {
                #region VALIDATION

                //get installation directory
                string executablePath = context.Executable.ExecutablePath;

                if (string.IsNullOrWhiteSpace(executablePath))
                    throw new ArgumentNullException("EA Desktop client executable path cannot be null or empty.");

                executablePath = Environment.ExpandEnvironmentVariables(executablePath);

                //ensure executable path exists
                if (!File.Exists(executablePath))
                    ExceptionHelper.ThrowExecutableNotFoundException(nameof(EADesktopLicenseManager), executablePath);

                #endregion                

                #region VARIABLES

                //get key
                var eADesktopLicenseKey = license.KeyAs<EADesktopLicenseKey>();

                //username variable
                string USERNAME = eADesktopLicenseKey.Username;

                //pasword variable
                string PASSWORD = eADesktopLicenseKey.Password;

                #endregion

                #region TERMINATE EXISTING

                //GET EXECUTABLE NAME
                string processName = Path.GetFileNameWithoutExtension(executablePath);

                //FIND EXISTING
                var eaDesktopProcess = Process.GetProcessesByName(processName)
                    .Where(x => string.Compare(x.MainModule.FileName, executablePath, true) == 0)
                    .FirstOrDefault();

                //TERMINATE EXISTING
                if (eaDesktopProcess != null)
                    eaDesktopProcess.Kill();

                //terminate any potential unwanted processes
                //if one of such processes might be running the login procedure will fail
                try
                {
                    var terminateList = Process.GetProcesses()
                        .Where(process => processKillList.Any(processName => string.Compare(process.ProcessName, processName, true) == 0))
                        .ToList();

                    foreach (var process in terminateList)
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            //failed to terminate process
                        }
                    }
                }
                catch
                {
                    //failed to obtain termination list
                }

                #endregion

                #region AUTOLOGIN CLEAR
                //delete any cookie files to avoid remember me
                try
                {
                    var cookieFileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Electronic Arts\EA Desktop\cookie.ini");
                    if (File.Exists(cookieFileName))
                    {
                        File.Delete(cookieFileName);
                    }
                }
                catch (Exception ex)
                {
                    context.WriteMessage($"Failed deleting EA Desktop cookie file. {ex.Message}");
                }
                #endregion

                #region INITIALIZE PROCESS

                //get current command line
                string COMMAND_LINE = context.Executable.Arguments;

                //check if command line is empty
                if (!string.IsNullOrWhiteSpace(COMMAND_LINE))
                {
                    COMMAND_LINE = Environment.ExpandEnvironmentVariables(COMMAND_LINE);
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = COMMAND_LINE,
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    ErrorDialog = false,
                    UseShellExecute = false
                };

                //create process
                eaDesktopProcess = new Process() { StartInfo = startInfo };

                #endregion

                #region START EA DESKTOP

                //attach state event handler
                context.ExecutionStateChanged += OnExecutionStateChanged;

                //add the process to tracked process if successfully started
                if (context.AddProcessIfStarted(eaDesktopProcess, true))
                {
                    //send input to the process window
                    SendProcessInput(USERNAME, PASSWORD);


                    //executables process creation should not be forced
                    forceCreation = false;
                }
                else
                {
                    //detach state event handler if we failed to start the process
                    context.ExecutionStateChanged -= OnExecutionStateChanged;

                    //throw exception
                    ExceptionHelper.ThrowStartFailureException(nameof(EADesktopLicenseManager), executablePath);
                }
                #endregion
            }
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new EADesktopManagerSettings();
        }

        private static void SendProcessInput(string username, string password)
        {
            int LARGE_DELAY = 5000;
            int EXTREME_DELAY = 30000;
            int SMALL_DELAY = 1000;
            //default center location based on window size
            int DEFAULT_X = 250;

            if (!CoreProcess.WaitForProcessCreated("EADesktop", EXTREME_DELAY, false))
            {
                //ea desktop process was not created within timeout
            }

            var eaDesktopProcess = Process.GetProcessesByName("EADesktop");

            Process targetProcess = null;

            foreach (var eaProcess in eaDesktopProcess)
            {
                if (CoreProcess.WaitForWindowCreated(eaProcess, EXTREME_DELAY, false))
                {
                    targetProcess = eaProcess;
                    break;
                }
            }

            if (targetProcess == null)
            {
                //no ea desktop process with window where found
                return;
            }

            //get the main window instance
            GizmoShell.WindowInfo window = new(targetProcess.MainWindowHandle);

            try
            {
#if RELEASE
                //block user input
                User32.BlockInput(true);
                User32.ShowCursor(false);
#endif
                //add a large delay so the main window can initialize
                Thread.Sleep(LARGE_DELAY);

                //check if window is minimized and restore it
                if (window.IsMinimized)
                    User32.ShowWindow(window.Handle, Win32API.Headers.WinUser.Enumerations.SW.SW_RESTORE);

                //bring main window to front
                window.BringToFront();

                //create simulators
                KeyboardSimulator keyboard = new();
                MouseSimulator mouse = new();

                //bring main window to front
                window.BringToFront();

                var location = window.Location;
                NativeMethods.SetCursorPos(location.X + DEFAULT_X, location.Y + 440);
                Thread.Sleep(SMALL_DELAY);
                mouse.LeftButtonDoubleClick();
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(username);
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.RETURN);

                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(password);
                Thread.Sleep(SMALL_DELAY);
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
}
