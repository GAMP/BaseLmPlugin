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
using Win32API.Com.Shell32;
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
                    throw new Exception("Epic launcher process was not cereated.");

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
                //kill all exisitng epic processes
                Process.GetProcessesByName(EPIC_PROCESS_NAME)
                    .ToList()
                    .ForEach(proc => proc.Kill());
            }
            catch (Exception)
            {
                //we failed but thats ok
            }

            int SMALL_DELAY = 1000;
            int MEDIUM_DELAY = 5000;
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
                //block user input
                User32.BlockInput(true);               
#endif

                //wait for child processes to be created
                Thread.Sleep(LARGE_DELAY);

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

                //the color of the first pixel in the EPIC internal window
                var fieldColor = Color.FromArgb(255, 16, 74, 130);

                //wait for the target pixel to be created
                var pixel = WaitForPixel(window.Handle, fieldColor, null, null, 20, 1000);

                //check if pixel is found
                if (pixel == null)
                {
                    //even if we did not find the correct pixel proceed anyway
                }

                //create simulators
                KeyboardSimulator keyboard = new();
                MouseSimulator mouse = new();

                //bring main window to front
                window.BringToFront();

                System.Windows.Forms.Cursor.Position = new Point(window.Location.X + (window.Width /2), window.Location.Y + (window.Height /2));

                Thread.Sleep(SMALL_DELAY);
                mouse.LeftButtonClick();
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB); 

                //add medium delay to allow the screen switch
                
                Thread.Sleep(SMALL_DELAY);

                //user name

                keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(username);

                //the login button some times takes more time to respons so a delay is required
                Thread.Sleep(SMALL_DELAY);

                //send enter key to initiate login
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.RETURN);

                //password
                Thread.Sleep(MEDIUM_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                keyboard.TextEntry(password);

                //the color of the first pixel in the EPIC login button
                var loginButtonColor = Color.FromArgb(255, 0, 116, 228);

                //wait for the target pixel to be created
                pixel = WaitForPixel(window.Handle, loginButtonColor, null, null, 50, 250);

                //check if pixel is found
                if (pixel == null)
                {
                    //pixel is not found, since some quite big delay already passed we can just press login button
                }

                //the login button some times takes more time to respons so a delay is required
                Thread.Sleep(SMALL_DELAY);

                //send enter key to initiate login
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.RETURN);

                //keep the keyboard locked for little more time so the password copying would not be possible
                Thread.Sleep(MEDIUM_DELAY);

                return targetProcess;
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
#endif
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
    }

    #endregion
}
