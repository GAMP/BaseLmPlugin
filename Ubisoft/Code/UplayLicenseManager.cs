using Client;
using CoreLib.Diagnostics;
using Gizmo.Shared.Plugins;
using GizmoShell;
using IntegrationLib;
using Microsoft.Win32;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Win32API.Modules;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Uplay)]
    [PluginMetadata("UBI Uplay",
        "1.0.0.0",
        "Manages license keys by sending credentials input to application window.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.uplay.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(UplayLicenseManagerSettings), KeyType = typeof(UplayLicenseKey))]
    public class UplayLicenseManager : SteamLicenseManager
    {
        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            if (context.HasCompleted | context.AutoLaunch)
            {
                //get installation directory from registry
                string executablePath = GetUplayPath();

                if (string.IsNullOrWhiteSpace(executablePath))
                    throw new ArgumentException("Could not obtain Uplay executable path from registry. Make sure Uplay is installed.");

                //ensure executable path exists
                if (!File.Exists(executablePath))
                    ExceptionHelper.ThrowExecutableNotFoundException(nameof(UplayLicenseManager), executablePath);

                //get uplay clean process name
                string processName = Path.GetFileNameWithoutExtension(executablePath);

                //kill all existing uplay processes
                Process.GetProcessesByName(processName)
                   .Where(x => string.Compare(x.MainModule.FileName, executablePath, true) == 0)
                   .ToList()
                   .ForEach(proc =>
                   {
                       try
                       {
                           //kill found process
                           proc.Kill();
                       }
                       catch (Exception)
                       {
                           //we failed but that's ok
                       }
                   });

                //clear credentials
                ClearLoginData();

                //initialize uplay process start info
                ProcessStartInfo startInfo = new()
                {
                    FileName = executablePath,
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    ErrorDialog = false,
                    UseShellExecute = false,
                };

                //create uplay process
                var uplayProcess = new Process()
                {
                    StartInfo = startInfo
                };

                //attach state event handler
                context.ExecutionStateChanged += OnExecutionStateChanged;

                if (context.AddProcessIfStarted(uplayProcess, true))
                {
                    //mark process created
                    forceCreation = true;

                    int processWindowCreateTimeout = 15000;
                    if (!CoreProcess.WaitForWindowCreated(uplayProcess, processWindowCreateTimeout, true))
                        ExceptionHelper.ThrowVisibleWindowTimedOut(nameof(UplayLicenseManager), processWindowCreateTimeout);

                    IntPtr targetWindowHandle = IntPtr.Zero;

                    for (int i = 0; i < 15; i++)
                    {
                        targetWindowHandle = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Chrome_WidgetWin_1", "Ubisoft Connect");
                        if (targetWindowHandle != IntPtr.Zero)
                            break;

                        Thread.Sleep(1000);
                    }

                    //window was not found
                    if (targetWindowHandle == IntPtr.Zero)
                        return;

                    try
                    {
#if RELEASE
                        //disable input
                        User32.BlockInput(true);
#endif

                        //get window
                        WindowInfo info = new(targetWindowHandle);

                        //activate origin window
                        info.BringToFront();
                        info.Activate();

                        //this will allow us to determine if the splash screen is shown
                        for (int i = 0; i < 10; i++)
                        {
                            try
                            {
                                var mainWindow = new WindowInfo(uplayProcess.MainWindowHandle);
                                if (mainWindow.IsVisible)
                                {
                                    Thread.Sleep(1000);
                                }
                            }
                            catch
                            {
                                break;
                            }
                        }


                        Thread.Sleep(3000);
                        User32.SetWindowPos(info.Handle, Win32API.Headers.WinUser.Enumerations.HWND.HWND_TOPMOST, 0, 0, 1280, 778, Win32API.Headers.WinUser.Enumerations.SWP.SWP_NOREPOSITION | Win32API.Headers.WinUser.Enumerations.SWP.SWP_NOMOVE);

                        var startLocation = new System.Drawing.Point(335, 110);

                        var loginPoint = new System.Drawing.Point(info.Location.X + startLocation.X + 250, info.Location.Y + startLocation.Y + 160);
                        var passwordPoint = new System.Drawing.Point(info.Location.X + startLocation.X + 250, info.Location.Y + startLocation.Y + 260);
                        var loginButtonPoint = new System.Drawing.Point(info.Location.X + startLocation.X + 250, info.Location.Y + startLocation.Y + 440);

                        //create input simulator
                        WindowsInput.KeyboardSimulator keyboard = new();
                        WindowsInput.MouseSimulator mouse = new();

                        System.Windows.Forms.Cursor.Position = loginPoint;
                        mouse.LeftButtonClick();
                        Thread.Sleep(1000);

                        //clear username filed
                        keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                        //send back to clear any possible typed value
                        keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                        //set username
                        keyboard.TextEntry(license.KeyAs<UserNamePasswordLicenseKeyBase>().Username);
                        Thread.Sleep(1000);

                        System.Windows.Forms.Cursor.Position = passwordPoint;
                        mouse.LeftButtonClick();
                        Thread.Sleep(1000);

                        //clear password filed
                        keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                        //send back to clear any possible typed value
                        keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                        //set password
                        keyboard.TextEntry(license.KeyAs<UserNamePasswordLicenseKeyBase>().Password);
                        Thread.Sleep(1000);

                        System.Windows.Forms.Cursor.Position = loginButtonPoint;
                        mouse.LeftButtonClick();
                        Thread.Sleep(1000);

                        //set environment variable
                        Environment.SetEnvironmentVariable("LICENSEKEYUSER", license.KeyAs<UplayLicenseKey>().Username);
                    }
                    catch
                    {
                        throw;
                    }
                    finally
                    {
#if RELEASE
                        //enable input
                        User32.BlockInput(false);
#endif
                    }
                }
                else
                {
                    //detach state event handler if we failed to start the process
                    context.ExecutionStateChanged -= OnExecutionStateChanged;

                    //throw start failure exception
                    ExceptionHelper.ThrowStartFailureException(nameof(UplayLicenseManager), executablePath);
                }
            }
        }

        public override IPluginSettings GetSettingsInstance()
        {
            return new UplayLicenseManagerSettings();
        }

        public override bool DivertExecution(IExecutionContext context)
        {
            return false;
        }

        private static string GetUplayPath()
        {
            string modulePath = string.Empty;
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"Software\Ubisoft\Launcher", false))
            {
                if (key != null)
                    modulePath = Path.Combine(key.GetValue("InstallDir").ToString(), "UPC.exe");
            }
            return modulePath;
        }

        private static void ClearLoginData()
        {
            try
            {
                var credentialsFileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ubisoft Game Launcher", "ConnectSecureStorage.dat");
                if (File.Exists(credentialsFileName))
                    File.Delete(credentialsFileName);
            }
            catch
            {
                //ignore
            }
        }
    }
}
