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
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;

namespace BaseLmPlugin
{
    #region ORIGINLICENSEMANAGER
    [Obsolete()]
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("EA Origin", "1.0.0.0", "Manages by launching origin process with user code token.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.origin.png")]
    public class OriginLicenseManager : SteamLicenseManager,
        IExecutionDivertPlugin
    {
        #region INTERFACE

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

                bool AUTH_CODE_OBTAINED = false;
                string AUTH_CODE = null;

                try
                {
                    AUTH_CODE = GetAuthCodeAsync(USERNAME, PASSWORD).GetAwaiter().GetResult();
                    AUTH_CODE_OBTAINED = true;
                }
                catch
                {
                    //failed to obtain auth code
                    AUTH_CODE_OBTAINED = false;
                }

                //get current command line
                string COMMAND_LINE = context.Executable.Arguments;

                //check if command line is empty
                if (!string.IsNullOrWhiteSpace(COMMAND_LINE))
                {
                    COMMAND_LINE = Environment.ExpandEnvironmentVariables(COMMAND_LINE);
                }

                //by default use the current command line parameters
                string PROCESS_COMMAND_LINE = COMMAND_LINE;

                //check if we got auth code and update current command line arguments
                if (AUTH_CODE_OBTAINED)
                {
                    if (!string.IsNullOrWhiteSpace(COMMAND_LINE))
                    {
                        PROCESS_COMMAND_LINE = $"{COMMAND_LINE}&authCode={AUTH_CODE}";
                    }
                    else
                    {
                        PROCESS_COMMAND_LINE = $"origin2://library/open?&authCode={AUTH_CODE}";
                    }
                }

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
                    //if we have not used auth code then automate login proccess
                    if (!AUTH_CODE_OBTAINED)
                    {
                        //send input to the process window
                        SendProcessInput(originProcess, USERNAME, PASSWORD);
                    }

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

        public override IPluginSettings GetSettingsInstance()
        {
            return new OriginLicenseManagerSettings();
        }

        #endregion

        #region FUNCTIONS

        private static async Task<string> GetAuthCodeAsync(string userName, string password)
        {
            var cookieContainer = new CookieContainer();
            using (var clienthandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = cookieContainer
            })
            {
                using (var client = new HttpClient(clienthandler))
                {
                    var responseString = await client.GetAsync("https://accounts.ea.com/connect/auth?client_id=ORIGIN_PC&response_type=code&redirect_uri=qrc:///html/login_successful.html&nonce=1828")
                        .ConfigureAwait(false);
                    var url = responseString.RequestMessage.RequestUri.AbsoluteUri;

                    var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
                    var stringChars = new char[32];
                    var random = new Random();

                    for (int i = 0; i < stringChars.Length; i++)
                    {
                        stringChars[i] = chars[random.Next(chars.Length)];
                    }

                    var randomCID = new string(stringChars);

                    var values = new Dictionary<string, string>
                    {
                        { "email", userName },
                        { "regionCode", "US" },
                        { "phoneNumber", "" },
                        { "password", password },
                        { "_eventId", "submit" },
                        { "cid", randomCID },
                        { "showAgeUp", "true" },
                        { "thirdPartyCaptchaResponse", "" },
                        { "loginMethod", "emailPassword" },
                        { "_rememberMe", "on" },
                        { "rememberMe", "on" },
                    };

                    var content = new FormUrlEncodedContent(values);
                    var response = await client.PostAsync(url, content).ConfigureAwait(false);
                    var pageContents = await response.Content.ReadAsStringAsync();

                    string search = @"window.location*.*";
                    Match match = Regex.Match(pageContents, search);

                    string authUrl = match.Value.Replace("window.location = \"", "");
                    authUrl = authUrl.Replace("\";", "");

                    var result = await client.GetAsync(authUrl);
                    var autocode = result.Headers.Location.ToString();
                    autocode = autocode.Replace(@"qrc:/html/login_successful.html?code=", "");

                    return autocode;
                }
            }
        }

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
                NativeMethods.SetCursorPos(location.X + DEFAULT_X, location.Y + 210);
                Thread.Sleep(SMALL_DELAY);
                mouse.LeftButtonDoubleClick();
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(username);

                NativeMethods.SetCursorPos(location.X + DEFAULT_X, location.Y + 255);
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
