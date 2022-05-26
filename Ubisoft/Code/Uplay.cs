using System;
using System.Linq;
using System.ComponentModel.Composition;
using IntegrationLib;
using Microsoft.Win32;
using System.IO;
using Client;
using System.Windows;
using GizmoShell;
using CoreLib.Diagnostics;
using System.Diagnostics;
using Win32API.Modules;
using System.Drawing;
using CoreLib.Imaging;
using System.Threading.Tasks;
using System.Threading;

namespace BaseLmPlugin
{
    #region UplayLicenseManager
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("UBI Uplay",
        "1.0.0.0",
        "Manages license keys by sending credentials input to application window.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.uplay.png")]
    public class UplayLicenseManager : SteamLicenseManager
    {
        #region Interface

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
            var context = new DialogContext(DialogType.UserNamePassword, new UplayLicenseKey(), profile);
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
                //get installation directory from registry
                string executablePath = GetUplayPath();

                if (string.IsNullOrWhiteSpace(executablePath))
                    throw new ArgumentException("Could not obtain Uplay executable path from registry. Make sure Uplay is installed.");

                //ensure executable path exists
                if (!File.Exists(executablePath))
                    ExceptionHelper.ThrowExecutableNotFoundException(nameof(UplayLicenseManager), executablePath);

                //get uplay clean process name
                string processName = Path.GetFileNameWithoutExtension(executablePath);

                //kill all exisitng uplay processes
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
                           //we failed but thats ok
                       }
                   });

                //initalize uplay process start info
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
                context.ExecutionStateChaged += OnExecutionStateChaged;

                if (context.AddProcessIfStarted(uplayProcess, true))
                {
                    //mark process created
                    forceCreation = true;

                    int windowWaitTimeout = 12000;
                    if (!CoreProcess.WaitForWindowCreated(uplayProcess, windowWaitTimeout, true))
                        ExceptionHelper.ThrowVisibleWindowTimedOut(nameof(UplayLicenseManager), windowWaitTimeout);

                    try
                    {
#if RELEASE
                        //disable input
                        User32.BlockInput(true);
#endif

                        //get window
                        WindowInfo info = new(uplayProcess.MainWindowHandle);

                        //activate origin window
                        info.BringToFront();
                        info.Activate();

                        //the color of the first pixel in the Uplay login button
                        var fieldColor = Color.FromArgb(255, 0, 119, 238);

                        //wait for the target pixel to be created
                        var pixel = WaitForPixelAsync(info.Handle, fieldColor, null, null, 50, 100)
                            .GetAwaiter()
                            .GetResult();
                        //ExceptionHelper.ThrowWindowPixelTimedOutIfPixelNull(pixel,nameof(UplayLicenseManager),)

                        //create input simulator
                        WindowsInput.KeyboardSimulator sim = new();

                        //send tab
                        sim.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.TAB);

                        //clear username filed
                        sim.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);

                        //send back to clear any possible typed value
                        sim.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);

                        //set username
                        sim.TextEntry(license.KeyAs<UserNamePasswordLicenseKeyBase>().Username);

                        //swicth field
                        sim.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);

                        //clear password filed
                        sim.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);

                        //send back to clear any possible typed value
                        sim.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);

                        //set password
                        sim.TextEntry(license.KeyAs<UserNamePasswordLicenseKeyBase>().Password);

                        //proceed with login
                        sim.KeyDown(WindowsInput.Native.VirtualKeyCode.RETURN);

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
                    context.ExecutionStateChaged -= OnExecutionStateChaged;

                    //throw start failure exception
                    ExceptionHelper.ThrowStartFailureException(nameof(UplayLicenseManager), executablePath);
                }
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
            return new UplayLicenseManagerSettings();
        }

        public override bool DivertExecution(IExecutionContext context)
        {
            return false;
        }

        #endregion

        #region Private

        private string GetUplayPath()
        {
            string modulePath = string.Empty;
            using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"Software\Ubisoft\Launcher", false))
            {
                if (key != null)
                    modulePath = Path.Combine(key.GetValue("InstallDir").ToString(), "UPC.exe");
            }
            return modulePath;
        }

        private static async Task<Pixel> WaitForPixelAsync(IntPtr windowHandle, Color color, int? x = default, int? y = null, int retries = 100, int delay = 250, CancellationToken ct = default)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            for (int i = 1; i <= retries; i++)
            {
                //get the main window instance
                WindowInfo info = new(windowHandle);

                if (info.IsMinimized)
                    info.Restore();

                info.BringToFront();

                //create window rect
                Rectangle rect = new(info.Location.X, info.Location.Y, info.Width, info.Height);

                //create bitmap image based on window size
                var screenImage = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                //copy image from screen
                using (Graphics g = Graphics.FromImage(screenImage))
                {
                    g.CopyFromScreen(rect.Left, rect.Top, 0, 0, info.Size, CopyPixelOperation.SourceCopy);
                }


                using (ImageTraverser traverser = new(screenImage))
                {
                    var query = traverser
                        .Where(e => e.Color == color);

                    if (x.HasValue)
                        query = query.Where(pixel => pixel.Location.X == x);

                    if (y.HasValue)
                        query = query.Where(pixel => pixel.Location.Y == y);

                    var foundPixel = query.FirstOrDefault();

                    if (foundPixel != null)
                        return foundPixel;
                }

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            return null;
        }


        #endregion
    }
    #endregion

    #region UplayLicenseManagerSettings
    [Serializable]
    public class UplayLicenseManagerSettings : SteamLicenseManagerSettings, IPluginSettings
    {
    }
    #endregion
}
