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
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;
using Win32API.Modules;
using System.Windows.Markup;
using Newtonsoft.Json;
using System.Runtime.Serialization;
using WindowsInput;

namespace BaseLmPlugin
{
    #region EADesktopLicenseManager
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("EA Desktop", "1.0.0.0", "Manages by launching ea desktop process with auth token.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.eadesktop.png")]
    public class EADesktopLicenseManager : SteamLicenseManager,
        IExecutionDivertPlugin
    {
        readonly string[] processKillList = new[] { "EALauncher", "EALaunchHelper", "EADesktop" };

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
            var context = new DialogContext(DialogType.UserNamePassword, new EADesktopLicenseKey(), profile);
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

                bool AUTH_CODE_OBTAINED = false;
                string AUTH_CODE = null;

                try
                {
                    AUTH_CODE = GetAuthCode2Async(USERNAME, PASSWORD).GetAwaiter().GetResult();
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

                //create process
                eaDesktopProcess = new Process() { StartInfo = startInfo };

                #endregion

                #region START EA DESKTOP

                //attach state event handler
                context.ExecutionStateChaged += OnExecutionStateChaged;

                //add the process to tracked process if successfully started
                if (context.AddProcessIfStarted(eaDesktopProcess, true))
                {
                    //if we have not used auth code then automate login proccess
                    if (!AUTH_CODE_OBTAINED)
                    {
                        //send input to the process window
                        SendProcessInput(USERNAME, PASSWORD);
                    }

                    //executables process creation should not be forced
                    forceCreation = false;
                }
                else
                {
                    //detach state event handler if we failed to start the process
                    context.ExecutionStateChaged -= OnExecutionStateChaged;

                    //throw exception
                    ExceptionHelper.ThrowStartFailureException(nameof(EADesktopLicenseManager), executablePath);
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
            return new EADesktopManagerSettings();
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
                    var responseString = await client.GetAsync("https://accounts.ea.com/connect/auth?client_id=JUNO_PC_CLIENT&response_type=code&redirect_uri=qrc:///html/login_successful.html&nonce=1828&pc_sign=eyJhdiI6InYxIiwiYnNuIjoiRGVmYXVsdCBzdHJpbmciLCJnaWQiOjE4MDQ4LCJoc24iOiIwMDAwXzAwMDBfMDAwMF8wMDAwXzAwMjZfQjcyOF8yQThBXzQzRTUuIiwibWFjIjoiJDRjNzk2ZWU1NWQ0MCIsIm1pZCI6IjE2Nzk5NTYyMDkyNjkxNjY0OTAwIiwibXNuIjoiRGVmYXVsdCBzdHJpbmciLCJzdiI6InYyIiwidHMiOiIyMDIzLTEtMTYgMTY6Mzk6MjI6NDI1In0.qG10gMPTorO2iv4EiXQz8GEfaV8hR8OBX1x8_rTVVMA")
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

        private static async Task<string> GetAuthCode2Async(string userName, string password)
        {
            throw new Exception();
            string pc_sign = "eyJhdiI6InYxIiwiYnNuIjoiRGVmYXVsdCBzdHJpbmciLCJnaWQiOjE4MDQ4LCJoc24iOiIwMDAwXzAwMDBfMDAwMF8wMDAwXzAwMjZfQjcyOF8yQThBXzQzRTUuIiwibWFjIjoiJDRjNzk2ZWU1NWQ0MCIsIm1pZCI6IjE2Nzk5NTYyMDkyNjkxNjY0OTAwIiwibXNuIjoiRGVmYXVsdCBzdHJpbmciLCJzdiI6InYyIiwidHMiOiIyMDIzLTItMTcgNzozNzozMDo5OTUifQ.2bc2T6CgCN3hzXFpeUCU5Xmo2OkXNGNEDNd8A4_OwKY";
            string code_challenge = "lOvMgdYrkqnvSIiU6Tp4Srk4FPVxDUdfkENA9ZvFkpc";

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
                    //initiate connect request
                    var challengeResponse = await client.GetAsync($"https://accounts.ea.com/connect/auth?code_challenge={code_challenge}&code_challenge_method=S256&client_id=JUNO_PC_CLIENT&response_type=code%20id_token&redirect_uri=qrc:///html/login_successful.html&display=junoClient/login&locale=en_US&nonce=-618605523&pc_sign={pc_sign}&sbiod_enabled=true")
                        .ConfigureAwait(false);


                    var url = challengeResponse.RequestMessage.RequestUri.AbsoluteUri;

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

                    int codeValueStartIndex = autocode.IndexOf("code=") + "code=".Length;
                    int idTokenStartIndex = autocode.IndexOf("&id_token");
                    int codeLength = idTokenStartIndex - codeValueStartIndex;
                    var code = autocode.Substring(codeValueStartIndex, codeLength);
                    autocode = autocode.Replace(@"qrc:/html/login_successful.html#?code=", "");

                    Dictionary<string, string> tokenContent = new Dictionary<string, string>()
                    {
                        {"grant_type", "authorization_code" },
                        {"code", code },
                        {"code_verifier","42aYB8Af-wjcuOC85XawYqB4-_h3MVga2vUEzo_vaLE" },
                        { "client_id", "JUNO_PC_CLIENT"},
                        {"client_secret","4mRLtYMb6vq9qglomWEaT4ChxsXWcyqbQpuBNfMPOYOiDmYYQmjuaBsF2Zp0RyVeWkfqhE9TuGgAw7te" },
                        {"redirect_uri","qrc:///html/login_successful.html" }
                    };
                    var postResult = await client.PostAsync("https://accounts.ea.com/connect/token", new FormUrlEncodedContent(tokenContent));
                    var resultString = await postResult.Content.ReadAsStringAsync();
                    var token = JsonConvert.DeserializeObject<EADesktopToken>(resultString);
                    return code;
                }
            }
        }

        private static void SendProcessInput(string username, string password)
        {
            int LARGE_DELAY = 5000;
            int EXTREME_DELAY = 30000;
            int SMALL_DELAY = 250;
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
                NativeMethods.SetCursorPos(location.X + DEFAULT_X, location.Y + 400);
                Thread.Sleep(SMALL_DELAY);
                mouse.LeftButtonDoubleClick();
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(SMALL_DELAY);
                keyboard.TextEntry(username);

                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                keyboard.TextEntry(password);
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

        private class EADesktopToken
        {
            public string access_token { get; set; }
            public string token_type { get; set; }
            public int expires_in { get; set; }
            public string refresh_token { get; set; }
            public string id_token { get; set; }
        }

        #endregion
    }
    #endregion



    #region EADesktopManagerSettings
    [Serializable]
    public class EADesktopManagerSettings : SteamLicenseManagerSettings, IPluginSettings
    {
    }
    #endregion
}
