using Client;
using CoreLib.Diagnostics;
using CoreLib.Imaging;
using GizmoShell;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Win32API.Modules;
using WindowsInput;

namespace BaseLmPlugin
{
    public class EpicLicenseHandler
    {
        #region READ ONLY FIELDS
        private static readonly string APP_DATA_PATH = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        //settings file name
        public static readonly string USER_SETTINGS_FILE_PATH = Path.Combine(APP_DATA_PATH, "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");
        #endregion

        #region CONSTANTS
        private const string EPIC_PROCESS_NAME = "EpicGamesLauncher";
        #endregion

        #region FUNCTIONS

        public static EpicInitResult Initiate(EpicInitParameters parameters, IExecutionContext cx)
        {
            if (cx == null)
                throw new ArgumentNullException(nameof(cx));

            try
            {
                //try simulation method
                var createdProcess = StartEpicProcess(parameters.FilePath, parameters.Arguments, parameters.WorkingDirectory, parameters.Username, parameters.Password, cx);

                //check if process have started
                if (createdProcess != null)
                    return new EpicInitResult(createdProcess);
                else
                    throw new Exception("Epic launcher process was not created.");

            }
            catch (EpicManualSignInRequiredException ex)
            {
                //automation aborted (captcha / security check / unexpected screen);
                //the launcher is still running so the user can complete the sign in by hand
                return new EpicInitResult(EpicInitResultCode.ManualSignInRequired, ex.LauncherProcess, ex);
            }
            catch (Exception ex)
            {
                //process starting failed, return error result here
                return new EpicInitResult(ex);
            }
        }

        public static Process StartEpicProcess(string fileName, string arguments, string workingDirectory, string username, string password, IExecutionContext cx)
        {
            try
            {
                //kill all existing epic processes
                Process.GetProcessesByName(EPIC_PROCESS_NAME)
                    .ToList()
                    .ForEach(proc => proc.Kill());
            }
            catch (Exception)
            {
                //we failed but that is ok
            }

            int SMALL_DELAY = 1000;
            int LARGE_DELAY = 10000;

            //create a start info for the new process
            var startInfo = new ProcessStartInfo()
            {
                FileName = Environment.ExpandEnvironmentVariables(fileName),
                Arguments = !string.IsNullOrEmpty(arguments) ? Environment.ExpandEnvironmentVariables(arguments) : null,
                WorkingDirectory = !string.IsNullOrEmpty(workingDirectory) ? Environment.ExpandEnvironmentVariables(workingDirectory) : null,
            };

            //create configured launcher process
            var launcherProcess = new Process { StartInfo = startInfo };

            //start it within context and add it to the context process list
            //if we fail here we need to report the error back to the calling code by returning null
            if (!cx.AddProcessIfStarted(launcherProcess, true))
            {
                //even if we failed to create a new process proceed with the login initiation
            }

            try
            {
#if RELEASE
                //block user input. NOTE: BlockInput(true) exempts the calling thread, so the
                //synthetic mouse injection (SetCursorPos / mouse_event) issued below from this same
                //thread still works — it only blocks a physical user from interfering mid-login.
                User32.BlockInput(true);
#endif

                //wait for child processes to be created
                Thread.Sleep(5000);

                //get first epic process
                Process targetProcess = Process.GetProcessesByName(EPIC_PROCESS_NAME)
                    .Where(p => !p.HasExited)
                    .FirstOrDefault();

                IntPtr windowHandle = IntPtr.Zero;

                //no process found
                if (targetProcess != null)
                {
                    //wait for the process window to be created
                    if (CoreProcess.WaitForWindowCreated(targetProcess.Id, LARGE_DELAY))
                        windowHandle = targetProcess.MainWindowHandle;
                }

                //if we failed to obtain window process try to find it in system
                if (windowHandle == IntPtr.Zero)
                {
                    for (int i = 0; i < 30; i++)
                    {
                        windowHandle = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "UnrealWindow", "Epic Games Launcher");
                        if (windowHandle != IntPtr.Zero)
                            break;

                        Thread.Sleep(SMALL_DELAY);
                    }
                }

                if (windowHandle == IntPtr.Zero)
                    throw new ArgumentException("Epic launcher window not found");

                //get the main window instance
                WindowInfo window = new(windowHandle);

                //check if window is minimized and restore it
                if (window.IsMinimized)
                    User32.ShowWindow(window.Handle, Win32API.Headers.WinUser.Enumerations.SW.SW_RESTORE);
     

                //bring main window to front
                window.BringToFront();

                //wait for Epic web UI to load by finding the cyan "Continue" button
                //this also gives us an anchor point since the form is fixed-size
                var cyanColor = Color.FromArgb(255, 38, 187, 255);
                var continueButtonPixel = WaitForPixel(window.Handle, [cyanColor], null, null, 30, 1000);

                //if the email screen never rendered its cyan anchor we cannot proceed safely;
                //abort cleanly and leave the launcher on-screen for a manual sign in
                if (continueButtonPixel == null)
                    throw new EpicManualSignInRequiredException("Epic sign-in email screen was not detected; manual sign in required.", targetProcess);

                //create simulators
                KeyboardSimulator keyboard = new();
                MouseSimulator mouse = new();

                //bring main window to front
                window.BringToFront();

                //click on the email input field with a real mouse click so the CEF web view focuses it
                //(the field sits ~50px above the cyan "Continue" anchor in the captured image)
                using (var img = Imaging.CaptureWindowImage(window.Handle))
                {
                    int clickX = img.Width / 2;
                    int clickY = continueButtonPixel.Location.Y - 50;
                    SendClickToWindow(window, clickX, clickY);
                }

                //user name
                Thread.Sleep(SMALL_DELAY);
                keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);
                keyboard.TextEntry(username);

                //send enter key to switch to next state (password field)
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                //give the email screen time to start transitioning away before we look for the
                //password anchor. The email and password screens share the same cyan color, so
                //acting too early would lock onto the still-visible email screen.
                Thread.Sleep(2 * SMALL_DELAY);

                //wait for the password screen to load AND settle. We look for the cyan "Forgot
                //password?" link, but only accept it once its position has been stable for several
                //consecutive reads — otherwise we could click a mid-transition frame (the anchor's
                //Y jumps around while the new screen animates in) before the password field exists.
                //NOTE: a null result here also covers the hCaptcha "One more step" screen, which
                //contains no cyan pixels — in that case we abort without typing the password.
                cyanColor = Color.FromArgb(255, 38, 187, 255);
                var forgotPasswordPixel = WaitForStablePixel(window.Handle, [cyanColor], 4, 60, 250);

                //if the password screen never appeared (captcha / verification / unexpected screen)
                //abort cleanly: do NOT type the password into whatever is on screen, and leave the
                //launcher running so a human can complete the sign in manually
                if (forgotPasswordPixel == null)
                    throw new EpicManualSignInRequiredException("Epic password screen was not detected (possible security check); manual sign in required.", targetProcess);

                //bring main window to front
                window.BringToFront();

                //click on the password input field with a real mouse click so the CEF web view focuses it
                //(the field sits ~50px above the cyan "Forgot password?" anchor in the captured image)
                using (var img = Imaging.CaptureWindowImage(window.Handle))
                {
                    int clickX = img.Width / 2;
                    int clickY = forgotPasswordPixel.Location.Y - 50;
                    SendClickToWindow(window, clickX, clickY);
                }

                //password
                Thread.Sleep(SMALL_DELAY);
                keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);
                keyboard.TextEntry(password);

                //add a delay to ensure the UI is ready
                Thread.Sleep(SMALL_DELAY);

                //send enter key to initiate login
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                return targetProcess;
            }
            catch
            {
                throw;
            }
            finally
            {
                //always unblock input (no-op if it was never blocked); pairs with the RELEASE
                //BlockInput(true) above and guards against leaving input blocked on any exit path
                User32.BlockInput(false);
            }
        }

        private static Pixel WaitForPixel(IntPtr windowHandle, Color color, int? x = default, int? y = null, int retries = 100, int delay = 250)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            for (int i = 1; i <= retries; i++)
            {
                var screenImage = Imaging.CaptureWindowImage(windowHandle);

                using (ImageTraverser traverser = new ImageTraverser(screenImage))
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

                    Thread.Sleep(delay);
                }
            }

            return null;
        }

        private static Pixel WaitForPixel(IntPtr windowHandle, Color[] color, int? x = default, int? y = null, int retries = 100, int delay = 250)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            for (int i = 1; i <= retries; i++)
            {
                using (var screenImage = Imaging.CaptureWindowImage(windowHandle))
                {
                    using (ImageTraverser traverser = new ImageTraverser(screenImage))
                    {
                        var query = traverser
                            .Where(e => color.Contains(e.Color));

                        if (x.HasValue)
                            query = query.Where(pixel => pixel.Location.X == x);

                        if (y.HasValue)
                            query = query.Where(pixel => pixel.Location.Y == y);

                        var foundPixel = query.FirstOrDefault();

                        if (foundPixel != null)
                            return foundPixel;

                        Thread.Sleep(delay);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Waits until the topmost matching pixel's Y position holds STILL for
        /// <paramref name="stableSamples"/> consecutive reads, then returns it.
        ///
        /// Rationale: the email and password screens share the same cyan color, and during the
        /// screen transition the anchor's Y jumps around (email anchor -> transient -> password
        /// anchor) before settling. A plain WaitForPixel returns instantly and can lock onto the
        /// still-visible email screen or a mid-animation frame, causing the click (and typed
        /// password) to land on the wrong screen before the password field exists. Requiring the
        /// anchor to be stable across several samples guarantees we only act once the new screen
        /// has fully rendered and stopped moving.
        /// </summary>
        private static Pixel WaitForStablePixel(IntPtr windowHandle, Color[] color, int stableSamples = 4, int retries = 60, int delay = 250)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            Pixel lastPixel = null;
            int stableCount = 0;

            for (int i = 1; i <= retries; i++)
            {
                Pixel foundPixel;
                using (var screenImage = Imaging.CaptureWindowImage(windowHandle))
                using (ImageTraverser traverser = new ImageTraverser(screenImage))
                {
                    foundPixel = traverser
                        .Where(e => color.Contains(e.Color))
                        .FirstOrDefault();
                }

                if (foundPixel != null && lastPixel != null && foundPixel.Location.Y == lastPixel.Location.Y)
                {
                    stableCount++;
                    if (stableCount >= stableSamples)
                        return foundPixel;
                }
                else
                {
                    //position changed (or nothing found yet) — reset the stability counter
                    stableCount = foundPixel != null ? 1 : 0;
                }

                lastPixel = foundPixel;
                Thread.Sleep(delay);
            }

            //timed out without stabilizing — return whatever we last saw (may be null)
            return lastPixel;
        }

        /// <summary>
        /// Performs a real hardware-level left click at the given window-relative coordinates.
        /// The coordinates are relative to <see cref="Imaging.CaptureWindowImage(IntPtr)"/> output,
        /// which is captured from the window DC (origin = window top-left, includes borders), so we
        /// convert to screen coordinates by adding the window rectangle origin.
        ///
        /// A synthetic WM_LBUTTONDOWN/WM_LBUTTONUP (SendMessage) is NOT used here: Epic's login UI is
        /// a Chromium/CEF web view, and a posted click does not set keyboard focus on its input
        /// fields, so any text typed afterwards is silently dropped. Injecting a genuine mouse click
        /// via SetCursorPos + mouse_event focuses the field the same way a physical click would.
        ///
        /// NOTE: this still works while User32.BlockInput(true) is in effect because BlockInput
        /// exempts the calling thread — the injection below runs on that same thread.
        /// </summary>
        private static void SendClickToWindow(WindowInfo window, int windowRelativeX, int windowRelativeY)
        {
            //map window-relative coordinates to absolute screen coordinates
            var windowRect = window.Rectangle;
            int screenX = windowRect.Left + windowRelativeX;
            int screenY = windowRect.Top + windowRelativeY;

            //move the cursor and inject a real left click
            NativeMethods.SetCursorPos(screenX, screenY);
            Thread.Sleep(60);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        #endregion
    }

    #region EPICINITPARAMETERS
    /// <summary>
    /// Epic login parameters.
    /// </summary>
    public class EpicInitParameters
    {
        #region PROPERTIES

        /// <summary>
        /// Gets or sets username.
        /// </summary>
        public string Username
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets password.
        /// </summary>
        public string Password
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets file path.
        /// </summary>
        public string FilePath
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets working directory.
        /// </summary>
        public string WorkingDirectory
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets arguments.
        /// </summary>
        public string Arguments
        {
            get; set;
        }

        #endregion
    }
    #endregion    

    #region EPICINITRESULTCODE
    /// <summary>
    /// Init function result codes.
    /// </summary>
    public enum EpicInitResultCode
    {
        Success = 0,
        Failure = 1,
        Canceled = 2,
        //automation could not complete (e.g. Epic captcha / security check);
        //the launcher is left running for the user to sign in manually
        ManualSignInRequired = 3,
    }
    #endregion

    #region EPICINITRESULT
    /// <summary>
    /// Init function result.
    /// </summary>
    public class EpicInitResult
    {
        #region CONSTRUCTOR

        public EpicInitResult() : this(EpicInitResultCode.Success)
        { }

        public EpicInitResult(EpicInitResultCode resultCode)
        {
            InitResult = resultCode;
        }

        public EpicInitResult(Process process) : this(EpicInitResultCode.Success)
        {
            //process is required
            CreatedProcess = process ?? throw new ArgumentNullException(nameof(process));
        }

        public EpicInitResult(string userToken) : this(EpicInitResultCode.Success)
        {
            //user token is required
            UserToken = userToken ?? throw new ArgumentNullException(nameof(userToken));
        }

        public EpicInitResult(Exception exception) : this(EpicInitResultCode.Failure)
        {
            //user token is required
            Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        }

        public EpicInitResult(EpicInitResultCode resultCode, Process process, Exception exception) : this(resultCode)
        {
            //process and exception are optional for this overload (used by the manual sign-in path)
            CreatedProcess = process;
            Exception = exception;
        }

        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets init result.
        /// </summary>
        public EpicInitResultCode InitResult
        {
            get; protected set;
        }

        /// <summary>
        /// Gets created process, this value is only set in case simulated login occured.
        /// </summary>
        public Process CreatedProcess
        {
            get; protected set;
        }

        /// <summary>
        /// Gets user token.
        /// </summary>
        public string UserToken
        {
            get; protected set;
        }

        /// <summary>
        /// Gets exception, this value is only set in case of failure.
        /// </summary>
        public Exception Exception
        {
            get; protected set;
        }

        #endregion
    }
    #endregion

    /// <summary>
    /// Thrown when the Epic sign-in cannot be completed automatically and requires the user to
    /// finish signing in by hand (for example when Epic presents its "One more step" captcha /
    /// security check, or the expected login screen is not detected). The launcher is left running.
    /// </summary>
    public class EpicManualSignInRequiredException : Exception
    {
        public EpicManualSignInRequiredException(string message, Process launcherProcess = null) : base(message)
        {
            LauncherProcess = launcherProcess;
        }

        /// <summary>
        /// Gets the running Epic launcher process, if one was created before the automation aborted.
        /// The launcher is intentionally left running so the user can finish signing in manually.
        /// </summary>
        public Process LauncherProcess
        {
            get;
        }
    }

    #region WIN32

    class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool WritePrivateProfileString(
            string lpAppName, string lpKeyName, string lpString, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetPrivateProfileString(
            string lpAppName, string lpKeyName, string lpDefault, string lpReturnedString,
            uint nSize, string lpFileName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos([In] int X, [In] int Y);

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    }

    #endregion
}
