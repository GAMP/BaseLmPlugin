using System;
using System.Collections.Generic;
using System.Linq;
using IntegrationLib;
using System.ComponentModel.Composition;
using System.IO;
using Client;
using System.Windows;
using System.Diagnostics;
using System.Xml;
using System.Threading.Tasks;
using System.Net.Http;
using System.Web;

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

                #region CLEAR XML CONFIGURATION

                //get folder name
                string folderName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Origin");

                //get file name
                string fileName = Path.Combine(folderName, "local.xml");

                if (!Directory.Exists(folderName))
                    Directory.CreateDirectory(folderName);

                //create new stream and write or update its configuration data
                using (var xmlStream = new FileStream(fileName, FileMode.OpenOrCreate))
                {
                    XmlDocument document = new XmlDocument
                    {
                        //preserve white space
                        PreserveWhitespace = true
                    };

                    try
                    {
                        document.Load(xmlStream);
                    }
                    catch (XmlException)
                    {
                        //could not load
                    }

                    XmlNode settingsNode = null;

                    #region Find Settings Node
                    if (document.HasChildNodes)
                    {
                        foreach (XmlNode node in document.ChildNodes)
                        {
                            if (node.Name == "Settings")
                            {
                                settingsNode = node;
                                break;
                            }
                        }
                    }
                    #endregion

                    #region Create Settings Node
                    if (settingsNode == null)
                    {
                        settingsNode = document.CreateElement("Settings");
                        document.AppendChild(settingsNode);
                    }
                    #endregion

                    bool isSet = false;

                    #region Update Existing Node
                    if (settingsNode.HasChildNodes)
                    {
                        foreach (XmlLinkedNode el in settingsNode.ChildNodes)
                        {
                            if (el is XmlElement element)
                            {
                                if (element.HasAttribute("key") && element.Attributes["key"].Value == "AcceptedEULAVersion")
                                {
                                    if (element.Attributes["value"].Value == "0")
                                        element.Attributes["value"].Value = (2).ToString();

                                    isSet = true;
                                    break;
                                }
                            }
                        }
                    }
                    #endregion

                    #region Clear Additional Settings
                    if (settingsNode.HasChildNodes)
                    {
                        List<XmlElement> removedElements = new List<XmlElement>();
                        foreach (XmlLinkedNode el in settingsNode.ChildNodes)
                        {
                            if (el is XmlElement)
                            {
                                var element = (XmlElement)el;
                                if (element.HasAttribute("key") &&
                                    element.Attributes["key"].Value == "AutoLogin" ||
                                    element.Attributes["key"].Value == "LoginAsInvisible" ||
                                    element.Attributes["key"].Value == "LoginEmail" ||
                                    element.Attributes["key"].Value == "LoginToken" ||
                                    element.Attributes["key"].Value == "RememberMeEmail")
                                {
                                    removedElements.Add(element);
                                }
                            }
                        }

                        foreach (var element in removedElements)
                        {
                            settingsNode.RemoveChild(element);
                        }
                    }
                    #endregion

                    #region Create new node
                    if (!isSet)
                    {
                        var setting = document.CreateElement("Setting");
                        settingsNode.AppendChild(setting);
                        setting.SetAttribute("key", "AcceptedEULAVersion");
                        setting.SetAttribute("type", "2");
                        setting.SetAttribute("value", "2");
                    }
                    #endregion

                    //reset stream
                    xmlStream.SetLength(0);

                    //save document
                    document.Save(xmlStream);
                }
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

                #endregion

                #region INITIALIZE PROCESS

                var AUTH_CODE = GetCodeAsync(USERNAME, PASSWORD).Result;

                string COMMAND_LINE = context.Executable.Arguments;
                string PROCESS_COMMAND_LINE = null;

                if (!string.IsNullOrWhiteSpace(COMMAND_LINE))
                {
                    COMMAND_LINE = Environment.ExpandEnvironmentVariables(COMMAND_LINE);
                    PROCESS_COMMAND_LINE = $"{COMMAND_LINE}&authCode={AUTH_CODE}";
                }
                else
                {
                    PROCESS_COMMAND_LINE = $"origin2://library/open?&authCode={AUTH_CODE}";
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

        #region PRIVATE FUNCTIONS

        private async Task<string> GetCodeAsync(string USERNAME, string PASSWORD)
        {
            return await Task.Run(() => GetOriginCodeAsync3(USERNAME, PASSWORD)).ConfigureAwait(false);
        }

        private async Task<string> GetOriginCodeAsync(string USERNAME, string PASSWORD)
        {
            HttpClientHandler httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            try
            {
                using (var httpClient = new HttpClient(httpClientHandler))
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 6.2; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) QtWebEngine/5.6.0 Chrome/45.0.2454.101 Safari/537.36 EA Download Manager Origin/10.0.1.31806");

                    #region FIELDS
                    string REDIRECT_URL = null;
                    string REDIRECT_URL_QUERY = null;
                    string LOGIN_URL = null;
                    string LOGIN_URL_QUERY_STRING = null;

                    string BASE_REQUEST_URL = @"https://accounts.ea.com/connect/auth?client_id=ORIGIN_PC&response_type=code%20id_token&redirect_uri=qrc:///html/login_successful.html&display=originX/login&locale=en_US&nonce=1828&pc_machine_id={0}";

                    #endregion

                    #region REDIRECT URL
                    var RESPONSE = await httpClient.GetAsync(BASE_REQUEST_URL);

                    if (RESPONSE.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        REDIRECT_URL = RESPONSE.Headers.Location.AbsoluteUri;
                        REDIRECT_URL_QUERY = RESPONSE.Headers.Location.Query;
                    }
                    else
                    {
                        RESPONSE.EnsureSuccessStatusCode();
                    }
                    #endregion

                    #region LOGIN URL
                    RESPONSE = await httpClient.GetAsync(REDIRECT_URL);

                    if (RESPONSE.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        LOGIN_URL = "https://signin.ea.com" + RESPONSE.Headers.Location;
                        LOGIN_URL_QUERY_STRING = RESPONSE.Headers.Location.ToString();
                    }
                    else
                    {
                        RESPONSE.EnsureSuccessStatusCode();
                    }
                    #endregion

                    #region LOGIN

                    FormUrlEncodedContent POST_CONTENT = new(new[]
                    {
                        new KeyValuePair<string,string>("email",USERNAME),
                        new KeyValuePair<string,string>("password",PASSWORD),
                        new KeyValuePair<string, string>("_eventId","submit"),
                        new KeyValuePair<string, string>("cid","cEN5wgPwra6F2zOd26Tyevpo002bzco5%2ClMwVtTHOr9sw7dFeDIX1uxyXLAm1NFxM"),
                        new KeyValuePair<string, string>("showAgeUp","true"),
                        new KeyValuePair<string, string>("googleCaptchaResponse",""),
                        new KeyValuePair<string, string>("_rememberMe","on"),
                        new KeyValuePair<string, string>("_loginInvisible","on")
                    });

                    var LOGIN_RESULT = await httpClient.PostAsync(LOGIN_URL, POST_CONTENT);

                    #endregion

                    #region AUTH

                    var DECODED_REDIRECT_QUERY = HttpUtility.UrlDecode(REDIRECT_URL_QUERY);
                    var REDIRECT_PARAMS = HttpUtility.ParseQueryString(DECODED_REDIRECT_QUERY);
                    var DECODED_LOGIN_QUERY = HttpUtility.UrlDecode(LOGIN_URL_QUERY_STRING);
                    var LOGIN_PARAMS = HttpUtility.ParseQueryString(DECODED_LOGIN_QUERY);

                    var NONCE = LOGIN_PARAMS["nonce"];
                    var MACHINE_ID = LOGIN_PARAMS["pc_machine_id"];
                    var FID = REDIRECT_PARAMS["fid"];

                    string AUTH_URL = @"https://accounts.ea.com/connect/auth?redirect_uri=qrc%3A%2F%2F%2Fhtml%2Flogin_successful.html&locale=en_US&display=originX%2Flogin&response_type=code+id_token&nonce={0}&pc_machine_id={1}&client_id=ORIGIN_PC&fid={2}";

                    AUTH_URL = string.Format(AUTH_URL, NONCE, MACHINE_ID, FID);

                    string AUTH_RESULT_URL = null;
                    var AUTH_RESULT = await httpClient.GetAsync(AUTH_URL);
                    if (AUTH_RESULT.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        AUTH_RESULT_URL = AUTH_RESULT.Headers.Location.AbsoluteUri;
                    }
                    else
                    {
                        AUTH_RESULT.EnsureSuccessStatusCode();
                    }

                    var TOKEN_DECODED_URL = HttpUtility.UrlDecode(AUTH_RESULT_URL);
                    var PARAM_START_INDEX = AUTH_RESULT_URL.IndexOf("#") + 1;
                    var PARAMS_STRING = AUTH_RESULT_URL.Substring(PARAM_START_INDEX);
                    var AUTH_PARAMS = HttpUtility.ParseQueryString(PARAMS_STRING);

                    var CODE = AUTH_PARAMS["code"];
                    var ID_TOKEN = AUTH_PARAMS["id_token"];
                    #endregion

                    return CODE;
                }
            }
            catch (HttpRequestException)
            {
                throw new Exception("Error getting Origin auth code. Make sure account username and password are correct.");
            }
        }

        private async Task<string> GetOriginCodeAsync2(string USERNAME, string PASSWORD)
        {
            HttpClientHandler httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            try
            {
                using (var httpClient = new HttpClient(httpClientHandler))
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 6.2; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) QtWebEngine/5.6.0 Chrome/45.0.2454.101 Safari/537.36 EA Download Manager Origin/10.0.1.31806");

                    #region FIELDS
                    string REDIRECT_URL = null;
                    string REDIRECT_URL_QUERY = null;
                    string LOGIN_URL = null;
                    string LOGIN_URL_QUERY_STRING = null;

                    string BASE_REQUEST_URL = @"https://accounts.ea.com/connect/auth?client_id=ORIGIN_PC&response_type=code%20id_token&redirect_uri=qrc:///html/login_successful.html&display=originX/login&locale=en_US&nonce=629&pc_sign=eyJhdiI6InYxIiwiYnNuIjoiUEMxMTQ4VEMiLCJnaWQiOjIyODA3LCJoc24iOiIxODUwX0M4ODBfNDA0OF8wMDAxXzAwMUJfNDQ4Ql80NDFFXzIwODUuIiwibWFjIjoiJGU4NmE2NDhkYTJkMyIsIm1pZCI6IjEzOTUyMDgxNTk0MjgxMTUzMTQ0IiwibXNuIjoiTDFIRjhDVjAwQVkiLCJzdiI6InYxIiwidHMiOiIyMDIwLTA3LTExIDEzOjUxOjU4OjAwMCJ9.gMe6bSzqcbvNsi7qorEaNYgpzE93rMivRDB6fi5YKug";
                    #endregion

                    #region REDIRECT URL
                    var RESPONSE = await httpClient.GetAsync(BASE_REQUEST_URL);

                    if (RESPONSE.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        REDIRECT_URL = RESPONSE.Headers.Location.AbsoluteUri;
                        REDIRECT_URL_QUERY = RESPONSE.Headers.Location.Query;
                    }
                    else
                    {
                        RESPONSE.EnsureSuccessStatusCode();
                    }
                    #endregion

                    #region LOGIN URL
                    RESPONSE = await httpClient.GetAsync(REDIRECT_URL);

                    if (RESPONSE.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        LOGIN_URL = "https://signin.ea.com" + RESPONSE.Headers.Location;
                        LOGIN_URL_QUERY_STRING = RESPONSE.Headers.Location.ToString();
                    }
                    else
                    {
                        RESPONSE.EnsureSuccessStatusCode();
                    }
                    #endregion

                    #region LOGIN

                    FormUrlEncodedContent POST_CONTENT = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string,string>("email",USERNAME),
                        new KeyValuePair<string,string>("password",PASSWORD),
                        new KeyValuePair<string, string>("_eventId","submit"),
                        new KeyValuePair<string, string>("cid","cEN5wgPwra6F2zOd26Tyevpo002bzco5%2ClMwVtTHOr9sw7dFeDIX1uxyXLAm1NFxM"),
                        new KeyValuePair<string, string>("showAgeUp","true"),
                        new KeyValuePair<string, string>("googleCaptchaResponse",""),
                        new KeyValuePair<string, string>("thirdPartyCaptchaResponse",""),
                        new KeyValuePair<string, string>("showAgeUp",""),
                        new KeyValuePair<string, string>("_rememberMe","on"),
                        new KeyValuePair<string, string>("_loginInvisible","on")
                    });

                    var LOGIN_RESULT = await httpClient.PostAsync(LOGIN_URL, POST_CONTENT);

                    #endregion

                    #region AUTH

                    var DECODED_REDIRECT_QUERY = HttpUtility.UrlDecode(REDIRECT_URL_QUERY);
                    var REDIRECT_PARAMS = HttpUtility.ParseQueryString(DECODED_REDIRECT_QUERY);
                    var DECODED_LOGIN_QUERY = HttpUtility.UrlDecode(LOGIN_URL_QUERY_STRING);
                    var LOGIN_PARAMS = HttpUtility.ParseQueryString(DECODED_LOGIN_QUERY);

                    var NONCE = LOGIN_PARAMS["nonce"];
                    var FID = REDIRECT_PARAMS["fid"];

                    string AUTH_URL = @"https://accounts.ea.com/connect/auth?redirect_uri=qrc%3A%2F%2F%2Fhtml%2Flogin_successful.html&locale=en_US&display=originX%2Flogin&response_type=code+id_token&nonce={0}&client_id=ORIGIN_PC&fid={1}&pc_sign=eyJhdiI6InYxIiwiYnNuIjoiUEMxMTQ4VEMiLCJnaWQiOjIyODA3LCJoc24iOiIxODUwX0M4ODBfNDA0OF8wMDAxXzAwMUJfNDQ4Ql80NDFFXzIwODUuIiwibWFjIjoiJGU4NmE2NDhkYTJkMyIsIm1pZCI6IjEzOTUyMDgxNTk0MjgxMTUzMTQ0IiwibXNuIjoiTDFIRjhDVjAwQVkiLCJzdiI6InYxIiwidHMiOiIyMDIwLTA3LTExIDEzOjUxOjU4OjAwMCJ9.gMe6bSzqcbvNsi7qorEaNYgpzE93rMivRDB6fi5YKug";

                    AUTH_URL = string.Format(AUTH_URL, NONCE, FID);

                    string AUTH_RESULT_URL = null;
                    var AUTH_RESULT = await httpClient.GetAsync(AUTH_URL);
                    if (AUTH_RESULT.StatusCode == System.Net.HttpStatusCode.Found)
                    {
                        AUTH_RESULT_URL = AUTH_RESULT.Headers.Location.AbsoluteUri;
                    }
                    else
                    {
                        AUTH_RESULT.EnsureSuccessStatusCode();
                    }

                    var TOKEN_DECODED_URL = HttpUtility.UrlDecode(AUTH_RESULT_URL);
                    var PARAM_START_INDEX = AUTH_RESULT_URL.IndexOf("#") + 1;
                    var PARAMS_STRING = AUTH_RESULT_URL.Substring(PARAM_START_INDEX);
                    var AUTH_PARAMS = HttpUtility.ParseQueryString(PARAMS_STRING);

                    var CODE = AUTH_PARAMS["code"];
                    var ID_TOKEN = AUTH_PARAMS["id_token"];
                    #endregion

                    return CODE;
                }
            }
            catch (HttpRequestException)
            {
                throw new Exception("Error getting Origin auth code. Make sure account username and password are correct.");
            }
        }

        private async Task<string> GetOriginCodeAsync3(string USERNAME, string PASSWORD)
        {
            HttpClientHandler httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
            };

            try
            {
                using (var httpClient = new HttpClient(httpClientHandler))
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 6.2; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) QtWebEngine/5.6.0 Chrome/45.0.2454.101 Safari/537.36 EA Download Manager Origin/10.0.1.31806");

                    //we might need to generate one automatically
                    string pcSign = "eyJhdiI6InYxIiwiYnNuIjoiRGVmYXVsdCBzdHJpbmciLCJnaWQiOjE4MDQ4LCJoc24iOiIwMDAwXzAwMDBfMDAwMF8wMDAwXzAwMjZfQjcyOF8yQThBXzQzRTUuIiwibWFjIjoiJGQ4NWVkMzBlNjBkMiIsIm1pZCI6IjQ3NzYxNzQ5ODIzNTI1MDM2NTkiLCJtc24iOiJEZWZhdWx0IHN0cmluZyIsInN2IjoidjIiLCJ0cyI6IjIwMjItMDQtMDMgMTI6NTA6MDc6MDAxIn0.LpUos7nJNRXiAMIwhtMAbZH-DwK0cAR-sol0_Pn73Ss";

                    //generate random nonce
                    int nonce = new Random().Next(1, 1000);

                    //default base auth url
                    string AUTH_REQUEST_URL = $"https://accounts.ea.com/connect/auth?client_id=ORIGIN_PC&response_type=code%20id_token&redirect_uri=qrc:///html/login_successful.html&display=originX/login&locale=en_US&nonce={nonce}&pc_sign={pcSign}";
    
                    //execute auth
                    var authResponse = await httpClient.GetAsync(AUTH_REQUEST_URL);

                    //ensure a success code returned
                    authResponse.EnsureSuccessStatusCode();

                    //try to get self location header value
                    //it will contain current location for the subsequesnt credentials post
                    if (!authResponse.Headers.TryGetValues("selflocation", out var selfLocationHeaders))
                        throw new Exception("Could not obtain self location header value.");

                    //get login url
                    var loginUrl = selfLocationHeaders.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(loginUrl))
                        throw new ArgumentException("Could not obtain self location login value.");

                    //create parameters for login
                    FormUrlEncodedContent loginParameters = new FormUrlEncodedContent(new[]
                    {
                          new KeyValuePair<string,string>("email",USERNAME),
                          new KeyValuePair<string,string>("password",PASSWORD),
                          new KeyValuePair<string, string>("_eventId","submit"),
                          new KeyValuePair<string, string>("cid","utN2R4nOrxotmv8Te8J6KDOyZr8p92wO,OUN8R2uGzrc1Rt24zONqNwn7HqAkTKUF"),
                          new KeyValuePair<string, string>("showAgeUp","true"),
                          new KeyValuePair<string, string>("thirdPartyCaptchaResponse",""),
                          new KeyValuePair<string, string>("_rememberMe","on"),
                          new KeyValuePair<string, string>("_loginInvisible","on")
                      });

                    //initiate login
                    var loginResult = await httpClient.PostAsync(loginUrl, loginParameters);

                    //ensure a success code returned
                    loginResult.EnsureSuccessStatusCode();

                    //read the returned html content
                    var loginRedirectHtml = await loginResult.Content.ReadAsStringAsync();

                    //try to obtain redirect location
                    var windowLocationStatrt = loginRedirectHtml.IndexOf("window.location =");
                    var windowLocationEnd = loginRedirectHtml.IndexOf(";", windowLocationStatrt);

                    //extract redirect url
                    var connectAuthUrl = loginRedirectHtml.Substring(windowLocationStatrt, windowLocationEnd - windowLocationStatrt)
                        .Split(new char[] { '=' },2)[1]
                        .Trim()
                        .TrimStart('"')
                        .TrimEnd('"');

                    //execute connect auth
                    var connectAuthResult = await httpClient.GetAsync(connectAuthUrl);

                    //the response returned must be a ridirect
                    //sine we have automatic redirects enabled in this case it wont work as url returned in Headers.Location is non-standard or so it seems to be the reason
                    if (connectAuthResult.StatusCode != System.Net.HttpStatusCode.Redirect)
                        throw new ArgumentException("Invalid status code returned for connect auth request.");

                    //the response location header will contain code and id_token, we only interested in code value
                    var tokenRedirectLocationQueryParams = connectAuthResult.Headers.Location.ToString().Split('#')[1];

                    //parse query params
                    var tokenRedirectParams = HttpUtility.ParseQueryString(tokenRedirectLocationQueryParams);

                    //return found code
                    return tokenRedirectParams.Get("code");
                }
            }
            catch (HttpRequestException)
            {
                throw new Exception("Error getting Origin auth code. Make sure account username and password are correct.");
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
