using Client;
using CoreLib;
using CoreLib.Diagnostics;
using CoreLib.Imaging;
using GizmoShell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        //epic data directory
        private static readonly string EPIC_DATA_DIRECTORY = Path.GetDirectoryName(USER_SETTINGS_FILE_PATH);

        private static readonly string XSRF_TOKEN_NAME = "XSRF-TOKEN";
        private static readonly string XSRF_TOKEN_NAME_HEADER_NAME = "X-XSRF-TOKEN";
        private static readonly string USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) EpicGamesLauncher/10.10.8-10982219+++Portal+Release-Live UnrealEngine/4.21.0-10982219+++Portal+Release-Live Chrome/59.0.3071.15 Safari/537.36";
        private static readonly string JSON_MISSING_ARGUMENT_MESSAGE = "Argument not specified in json response object.";
        private static readonly string PORTAL_AUTH_ID = "34a02cf8f4414e29b15921876da36f9a";
        private static readonly string PORTAL_AUTH_KEY = "daafbccc737745039dffe53d94fc76cf";

        //portal basic auth
        private static readonly string PORTAL_BASIC_AUTH = $"{PORTAL_AUTH_ID}:{PORTAL_AUTH_KEY}";

        //convert auth string to base64
        private static readonly string PORTAL_BASIC_AUTH_JSON = Convert.ToBase64String(Encoding.UTF8.GetBytes(PORTAL_BASIC_AUTH.ToString()));

        #endregion

        #region CONSTANTS

        private const string EPIC_PROCESS_NAME = "EpicGamesLauncher";

        #endregion

        #region FUNCTIONS

        public static async Task<EpicInitResult> InitiateAsync(EpicInitParameters parameters,IExecutionContext cx, CancellationToken ct = default)
        {
            if (cx == null)
                throw new ArgumentNullException(nameof(cx));

            await Task.Yield();

            //#region TOKEN BASED
            //try
            //{
            //    //configure browser
            //    IEFeatures.SetEmulationVersion(IEFeatures.IEVersion.IE11001);

            //    //try to obtain token
            //    var userToken = await UserTokenGetAsync(parameters.Username, parameters.Password, ct)
            //        .ConfigureAwait(false);

            //    #region SAVE TOKEN                         

            //    //check if epic data directory exists
            //    if (!Directory.Exists(EPIC_DATA_DIRECTORY))
            //        Directory.CreateDirectory(EPIC_DATA_DIRECTORY);

            //    //create user settings ini if one dont exist
            //    if (!File.Exists(USER_SETTINGS_FILE_PATH))
            //    {
            //        //create file stream
            //        using (var file = File.Create(USER_SETTINGS_FILE_PATH))
            //        {
            //            //dispose and close file stream
            //        }
            //    }

            //    if (!NativeMethods.WritePrivateProfileString("RememberMe", "Enable", "True", USER_SETTINGS_FILE_PATH))
            //        throw new Win32Exception();

            //    if (!NativeMethods.WritePrivateProfileString("RememberMe", "Data", userToken, USER_SETTINGS_FILE_PATH))
            //        throw new Win32Exception();
            //    #endregion

            //    //return success
            //    return new EpicInitResult(userToken);
            //}
            //catch (EpicException epicException)
            //{
            //    //if epic reports account lock or invalid credentials we must not proceed and return the error
            //    if (epicException.EpicErrorCode == EpicApiErrorCodes.ACCOUNT_LOCKED || epicException.EpicErrorCode == EpicApiErrorCodes.INVALID_ACCOUNT_CREDENTIALS)
            //        return new EpicInitResult(epicException);
            //}
            //catch (ArgumentException aex)
            //{
            //    //if some of the arguments not valid then we must not proceed and return the error
            //    return new EpicInitResult(aex);
            //}
            //catch (HttpRequestException)
            //{
            //    //ignore http request exceptions and proceed with simulated login
            //}
            //catch (Exception)
            //{
            //    //ignore any other exceptions and proceed with simulated login
            //} 
            //#endregion

            //since we reached this line means we have failed obtaining token

            try
            {
                //check if cancellation was requested
                if (!ct.IsCancellationRequested)
                {
                    //try simulation method
                    var createdProcess = StartEpicProcess(parameters.FilePath, parameters.Arguments, parameters.WorkingDirectory, parameters.Username, parameters.Password,cx);

                    //check if process have started
                    if (createdProcess != null)
                        return new EpicInitResult(createdProcess);
                }
            }
            catch (Exception ex)
            {
                //process starting failed, return error result here 
                return new EpicInitResult(ex);
            }

            //check for cancelation
            if (ct.IsCancellationRequested)
                return new EpicInitResult(EpicInitResultCode.Canceled);

            //reuturn erro result
            return new EpicInitResult();
        }

        private static async Task<string> UserTokenGetAsync(string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentNullException(nameof(username));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));

            //create cookie container
            var cookieContainer = new CookieContainer();
            //create http client handler
            using (var handler = new HttpClientHandler() { CookieContainer = cookieContainer })
            //create http client
            using (var client = new HttpClient(handler))
            {
                #region ADD DEFAULT REQUEST HEADERS

                //add user agent
                client.DefaultRequestHeaders.Add("User-Agent", USER_AGENT);
                //add user agent
                client.DefaultRequestHeaders.Add("Referer", "https://www.epicgames.com/id/login");

                #endregion

                //xsrf token variable
                string xsrf_token = null;

                #region LOGIN

                #region ARKOSE REQUEST HANDLING

                //create random double
                var arkoseRandom = new Random().NextDouble();

                //create default params
                var arkoseParams = new Dictionary<string, string>
                {
                    {"bda","eyJjdCI6IlA0enVKeGpJZWdXMWFtQjBYcEVBYVlyL2ovd2d1U1ZBNVBQS3o2TVNKRjlpM3EwaW95cDN6bFNEMkc3WEgzakNqWGc3d1BXa3NRL253SzJiNlkwR1VxM2JydjRaZ1pVS01Ra1VtT2xhT1lJQzJMK0J2ckNLdTJSakcxTDFaK0pkUi9xeFc5c3JUQ1o2cXpGZy9rbStDMjIxcFJPNm0xaG91cEVXa0NZUmY2cTI4T1NpZ3o3SVhZQU1XRnRodFVLY3ozT3VDZTNISm8yNFZyNkpNSXRETWprSU82MXpiSXZBYU50bVN5Y2JLUGIzemVPYnZHTzNRb1RiSy9raE4yUzBjWWhFeHpCS2FsTU1pUWVuZk9RT09tYXNRYkJySzVwRC83OFRxK1lGVFpPU0ZFcm1pMExlVjFHbnNhUUptcTlBRkRUSmMyV01iY2hMblNaMytkRkhwK1pmRE1xM2xZQkIrQUFzekRHNTJ4UE1DMENmOWd6UGI5NjBta0lhdGgrS2tmMjZhTE1sSG1lUFBPZlk2anI0NFp6Zyt4dVhuem5YMXpCU0R2eVJidU9ydUV4dGI5L1hISFVSVDdFQzZWT0I3cU1ia2VNNEJGSHBLL3U3OWFZTFJ4dnRqT1E4TXgrUC9yQmNTcnJ1RmhKL1RVUWdnc3diMkp2TTUzR1k2bEF0KzNkZy9xb05CODdTVWZOYVhicFRtcFZ1N1F1NSs4MzNZOEF6QzRkbjRRRUpPUzcvYjd3ZElmNFRaL0k1N09Gcmx1NTlaKzBnQ1p3WlMyRlZiZW4yWVNUbHV0bXNLQnpiSnFkZjlrSEFrSk9TN2ZONEtod1VyNkNTTVFtZzJaTW0wZGthaytiYTVkSzZ1MnYrd2dTaDhpNTE0aTNPdENpekdtT2NWekFYdGtuamRmaXBONm91c3NpQi9vd29EMFIwdEg5SnVuZGI1K3Zscy9wT1dMNDhGZUVUaU5yZnlvSlRzeWRvNmx4b0pPR1poMVJLT3VQRUhIbnVoemxSTFZHNEhYMVNsS05pZ0lRNFljSkpPS3h5UmR5Z0RaT0lWamVzdlk5SnJxaE5URVduMUM5TXBaMXhjcWdtbHJTbk1PQjFlWFNCcTk3TWdQRFVFak1FaStEYmtxS21PSjVkL0l6MlJNK0JHbUxiSVR3NFNITXd4U1p1Y25nbk9oM01nWFJ1c25VVWtCaG9LSEFkSDk4SVZlYnR3dUNzb2U5bnhOTVkvRjV0NThrK1Vhd0hTTzBNVHdPMHEyMjRuek51Z2dreU1GMWEyVGxYQ2NPZEROUTVyV3M4aFR6eGVhZ2ZrQVg5R2tIZG94MkpnRUh5TXIrM25WVDlQdzhaTDFDc3NPNldGODZsbmIvQzdaalpseFlXWVcvSTM1QkkyR0ZDa0VGRnY3SHhvZExIRklIYXN5czZ2S3dtTmM3MVo1VzQxYy94S0Y1d3dMeDdPeFJCTXQ1SHpuMWJlZmY4OXQxYUs0NG52cjFBeHhXbWRDaWdmMzZCWHByRjFndGdybzhxdXVmMWdUYWpjaEN5RGVpVVNORU5MRW8vRThXQlFUZ2hubGY5ZGhKajNPU0hQT3RONGx6aG1ObjRyVkZFaWM4OEltSkZCU20xdG05Z2FXSGZVSEtSTUt0V3RmR1lZUmI5T0d5dHkyclc5NlptUk02UEN1V1MxeHd3dmpneU8vdEh6L2NsOVBxV3pVKzduTlRndFQveFBMUityZVhlNVlYaS85czI1NmJBRXorVGlIa0cybWYrdldhRWpMdnphUGNjcWFJUGhwVE5kSTQzenBJMDFEU280Z082bjhYZ2o3OTdJa0xYRmdYSzRSelRvREtIVExNSFBDWk04cGV6MTFMdVJFakt2OGRXRk8wWTlhR1ByMHE5WVFhQWh0ZXQvMnAvM20zZ2tJLy9jNmRrblVjaE4zOWdxaXZuMEFsM0NYUTZvcGV4dFpWU2IyYmZTSXVxeDY0Q3EyV1JoZ204MEtqa3lkdzNnTkx4Y1I2T3hkc0F6KzhXeW5mVDFkMFM0K3BBM1k3MlVFNHV6Y1dCcUIzdzVVT01FTjFBVEhCaWVxSitQcGQzam43MmNuZ0RKYXVzNnlJRXYwUVRGU3kzS05UNlRwZmdBVlF1ZnFnc2RtZjNNenlGN0JCS3llV0U0UldocHFIamdERkVYU05rdkZoYVY2MXBXTGltV0k3VnFTTzIyNGcvSWtKcEh5bWM4UXRQSTRYZEpNUHAvMmM2NlZWcjJCY0dWZm1JSXlFWEMvdGhzd3RpRlZ6dWwreGhOcjBNRXYrNnIyTWlHRkZDZ0hkT0tLMEVNL3JiOWZxY1E0Y1Nsd1NBSzVvY0Eya2Q1MmJobkp6ZTdaYWd4d1dWRW5oMHpMRUFoSGRuT2FFTGZoY3JRMFplZlQ0aTlGS2Fxell2NXdGSlVQd1p5OVJ1ZHhKZUJtVjBJTUp2bmtjZTdmeklhYmNrT1B4bHJOendOazFrYmU2K3NVOVVHaXRlZ3p5akhqeisrS2I0dXc4N051ST0iLCJpdiI6IjE4OGE5NWU5NjIyOGIxNmE2MGM5MGIwOWJmMjgxODQ4IiwicyI6Ijk2NjhhNDMzNWQyNTQ1NmMifQ==" },
                    {"public_key","37D033EB-6489-3763-2AE1-A228C04103F5" },
                    {"site","https://epic-games-api.arkoselabs.com"},
                    {"userbrowser","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) EpicGamesLauncher/10.12.4-11217871+++Portal+Release-Live UnrealEngine/4.21.0-11217871+++Portal+Release-Live Chrome/59.0.3071.15 Safari/537.36" },
                    {"simulate_rate_limit", "0"},
                    {"simulated","0"},
                    {"language","en"},
                    {"rnd",$"{arkoseRandom}" },
                    {"data[blob]","I369Lut8cFYR63NE.PR1o4RNab4zP82mj7txWUMMiF9gIhxxOmd/GZjyjbBA/ObnXAlJW3r3+l+Akv3h6t31dXjWtSafFxxwAYLd+X3OYc5T3gmX5HUWyvhgcNMfO3V95vYPwa//BiC4YglUUji67hWNXzZbK8aDD44ulPUFIrluyxtWvafpcJ/IusVJP" }
                };

                //arkose request content
                var arkoseContent = new FormUrlEncodedContent(arkoseParams);

                //send the request
                var arkoseResult = await client.PostAsync("https://epic-games-api.arkoselabs.com/fc/gt2/public_key/37D033EB-6489-3763-2AE1-A228C04103F5", arkoseContent, ct);

                //esnure success code
                //the api should always return true
                arkoseResult.EnsureSuccessStatusCode();

                //get arkose token
                var arkoseTokenResult = await arkoseResult.Content.ReadAsStringAsync();

                //parse json response to token class
                var arkoseToken = JsonConvert.DeserializeObject<AkroseTokenResult>(arkoseTokenResult);

                #endregion

                //try to obtain xsrf token
                xsrf_token = await CRFSTokenGetAsync(client, cookieContainer, ct);

                //create login parameters
                var loginParameters = new Dictionary<string, object>()
                {
                    {"email", username }, //use provided username
                    {"password", password }, //use provided password
                    {"rememberMe", true },
                    {"captcha", arkoseToken.Token } //use arkose token
                };

                //get default login parameters json
                var json = JsonConvert.SerializeObject(loginParameters);

                //create json body content
                var jsonContent = new StringContent(json, Encoding.UTF8, "application/json");

                //create initial login request message
                var loginRequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://www.epicgames.com/id/api/login")
                {
                    Content = jsonContent
                };

                //add obtained xsrf token to the request headers
                loginRequestMessage.Headers.Add(XSRF_TOKEN_NAME_HEADER_NAME, xsrf_token);

                //make the request
                var loginResult = await client.SendAsync(loginRequestMessage, ct);

                //check status code
                if (loginResult.StatusCode != HttpStatusCode.OK)
                {

                    var loginError = await loginResult.Content.ReadAsStringAsync();
                    var epicApiError = JsonConvert.DeserializeObject<EpicApiError>(loginError);

                    if (epicApiError.ErrorCode != EpicApiErrorCodes.REPUTATION_SESSION_INVALIDATED)
                        throw new EpicException(epicApiError.Message, epicApiError.ErrorCode);

                    //usually the error code is conflict

                    //conflict happens when normal login cannot be completed due to invalidated session

                    //ask user to pass the captcha
                    var akroseCaptchaResult = await EpicCaptchaShowAsync(ct);

                    //throw exception if operation canceled
                    if (akroseCaptchaResult.DialogResult == null)
                        throw new EpicException("Captcha operation canceled.");

                    if (akroseCaptchaResult.DialogResult == false)
                        throw new EpicException("User canceled captcha.");

                    //try to obtain new xsrf token
                    xsrf_token = await CRFSTokenGetAsync(client, cookieContainer, ct);

                    //update captcha value
                    loginParameters["captcha"] = akroseCaptchaResult.Token;

                    //get updated default login parameters json
                    json = JsonConvert.SerializeObject(loginParameters);

                    //create new request content with default login parameters
                    jsonContent = new StringContent(json, Encoding.UTF8, "application/json");

                    //crate login request message
                    loginRequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://www.epicgames.com/id/api/login")
                    {
                        Content = jsonContent
                    };

                    //add xsrf token to request headers
                    loginRequestMessage.Headers.Add(XSRF_TOKEN_NAME_HEADER_NAME, xsrf_token);

                    //send login message
                    loginResult = await client.SendAsync(loginRequestMessage, ct);

                    //check status code
                    if (loginResult.StatusCode != HttpStatusCode.OK)
                    {
                        loginError = await loginResult.Content.ReadAsStringAsync();
                        epicApiError = JsonConvert.DeserializeObject<EpicApiError>(loginError);

                        //crate login request message
                        loginRequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://www.epicgames.com/id/api/login")
                        {
                            Content = jsonContent
                        };

                        //send login message
                        loginResult = await client.SendAsync(loginRequestMessage, ct);

                        //throw expic exception here
                        throw new EpicException(epicApiError.Message, epicApiError.ErrorCode);
                    }
                }

                #endregion

                #region EXCHANGE CODE

                //authenticate
                var authenticateResult = await client.GetAsync("https://www.epicgames.com/id/api/authenticate", ct);

                //ensure success code
                authenticateResult.EnsureSuccessStatusCode();

                //get new xsrf token
                xsrf_token = await CRFSTokenGetAsync(client, cookieContainer, ct);

                //create exchange message
                var exchangeMessage = new HttpRequestMessage(HttpMethod.Post, "https://www.epicgames.com/id/api/exchange/generate");

                //add xsrf token to the headers
                exchangeMessage.Headers.Add(XSRF_TOKEN_NAME_HEADER_NAME, xsrf_token);

                //execute exchange
                var exchangeResult = await client.SendAsync(exchangeMessage, ct);

                //esnure sucess
                exchangeResult.EnsureSuccessStatusCode();

                //get exchange code string
                var exchangeCodeString = await exchangeResult.Content.ReadAsStringAsync();

                //parse to exchange code class
                var exchangeCode = JsonConvert.DeserializeObject<ExchangeCodeResult>(exchangeCodeString);

                #endregion

                #region USER TOKEN

                HttpResponseMessage responseResult;
                JObject jsonResponse;

                Dictionary<string, string> requestParameters = new Dictionary<string, string>()
                {
                    {"grant_type", "exchange_code" },
                    {"exchange_code", exchangeCode.Code },
                    {"includePerms", "true" },
                    {"token_type","eg1" },
                };

                //create new content from parameters
                var content = new FormUrlEncodedContent(requestParameters);

                //switch to basic authorization
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", PORTAL_BASIC_AUTH_JSON);

                //make the request
                responseResult = await client.PostAsync("https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token", content, ct);

                //ensure success code
                responseResult.EnsureSuccessStatusCode();

                //get response string
                string responseString = await responseResult.Content.ReadAsStringAsync();

                //get json response object
                jsonResponse = JObject.Parse(responseString);

                if (!jsonResponse.TryGetValue("refresh_token", out var refreshJToken))
                    throw new ArgumentException(JSON_MISSING_ARGUMENT_MESSAGE, "refresh_token");

                string refreshToken = refreshJToken.ToString();

                if (!jsonResponse.TryGetValue("account_id", out var accountIdJToken))
                    throw new ArgumentException(JSON_MISSING_ARGUMENT_MESSAGE, "account_id");

                string accountId = accountIdJToken.ToString();

                if (!jsonResponse.TryGetValue("access_token", out var accessJToken))
                    throw new ArgumentException(JSON_MISSING_ARGUMENT_MESSAGE, "access_token");

                string accessToken = accessJToken.ToString();

                //switch authentication to Bearer
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                //make request
                responseResult = await client.GetAsync($"https://account-public-service-prod03.ol.epicgames.com/account/api/public/account/{accountId}", ct);

                //esnure success
                responseResult.EnsureSuccessStatusCode();

                responseString = await responseResult.Content.ReadAsStringAsync();
                jsonResponse = JObject.Parse(responseString);

                //create json object list
                var jsonList = new List<JObject>();

                //create json object
                JObject outJObject = new JObject
                {
                    { "Region", "Prod" },
                    { "Email", jsonResponse["email"].ToString() },
                    { "Name", jsonResponse["name"].ToString() },
                    { "LastName", jsonResponse["lastName"].ToString() },
                    { "DisplayName", jsonResponse["displayName"] },
                    { "Token", refreshToken },
                    { "bHasPasswordAuth", true }
                };

                //add json object to list
                jsonList.Add(outJObject);

                //serialize json list
                var outJson = JsonConvert.SerializeObject(jsonList);

                //get plain text byte array
                var plainTextBytes = Encoding.UTF8.GetBytes(outJson.ToString());

                //get base64 encoded string
                string base64Encoded = Convert.ToBase64String(plainTextBytes);

                //retrun encoded string
                return base64Encoded;

                #endregion
            }
        }

        private static async Task<string> CRFSTokenGetAsync(HttpClient client, CookieContainer cookieContainer, CancellationToken ct = default)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(HttpClient));

            //create csrf request message
            var csrfRequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri("https://www.epicgames.com/id/api/csrf"));

            //make initial CSRF request
            var csrfRequest = await client.SendAsync(csrfRequestMessage, ct);

            //ensure sucess
            csrfRequest.EnsureSuccessStatusCode();

            //get cookies
            var cookies = cookieContainer.GetCookies(new Uri("https://www.epicgames.com/id/api/csrf"));

            //try to obtain token from cookies
            var xsrf_token = cookies.OfType<Cookie>()
                .Where(cookie => cookie.Name == XSRF_TOKEN_NAME)
                .Select(cookie => cookie.Value)
                .FirstOrDefault();

            //token not found? throw an error
            if (string.IsNullOrWhiteSpace(xsrf_token))
                throw new ArgumentException(nameof(XSRF_TOKEN_NAME));

            return xsrf_token;
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
            int MEDIUM_DELAY = 1500;
            int LARGE_DELAY = 3000;
            int EXTRA_LARGE_DELAY = 5000;

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
                return null;

            //wait for process to exit, launcher exits once its spawned new child processes
            launcherProcess.WaitForExit(LARGE_DELAY);

            //get first epic process
            var targetProcess = Process.GetProcessesByName(EPIC_PROCESS_NAME)
                .FirstOrDefault();

            //no process found
            if (targetProcess == null)
                return null;

            //wait for the process window to be created
            if (CoreProcess.WaitForWindowCreated(targetProcess.Id, LARGE_DELAY) == false)
                return null;

            //add a large delay so the main window can initialize
            Thread.Sleep(LARGE_DELAY);

            //get the main window instance
            WindowInfo window = new(targetProcess.MainWindowHandle);

            try
            {
#if RELEASE
                //block user input
                User32.BlockInput(true); 
#endif

                //check if window is minimized and restore it
                if (window.IsMinimized)
                    User32.ShowWindow(window.Handle, Win32API.Headers.WinUser.Enumerations.SW.SW_RESTORE);

                //bring main window to front
                window.BringToFront();

                //add medium dealy to allow window to activate
                Thread.Sleep(MEDIUM_DELAY);

                //the color of the first pixel in the EPIC internal window
                var fieldColor = Color.FromArgb(255, 32, 32, 32);

                //wait for the target pixel to be created
                var pixel = WaitForPixelAsync(window.Handle, fieldColor, null, null, 50, 100)
                    .GetAwaiter()
                    .GetResult();

                //check if pixel is found
                if (pixel == null)
                    return null;

                //bring main window to front
                window.BringToFront();

                //create simulators
                KeyboardSimulator keyboard = new KeyboardSimulator();
                MouseSimulator mouse = new MouseSimulator();

                //initial screen
                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                //bring main window to front
                window.BringToFront();

                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                //bring main window to front
                window.BringToFront();

                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                //bring main window to front
                window.BringToFront();

                Thread.Sleep(SMALL_DELAY);
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.RETURN);
                //bring main window to front
                window.BringToFront();

                //login screen

                //add medium delay to allow the screen switch
                Thread.Sleep(MEDIUM_DELAY);

                //user name
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);
                keyboard.TextEntry(username);

                //password
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
                keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);
                keyboard.TextEntry(password);

                //Color[A = 255, R = 0, G = 116, B = 228]

                //the color of the first pixel in the EPIC login button
                var loginButtonColor = Color.FromArgb(255, 0, 116, 228);

                //wait for the target pixel to be created
                pixel = WaitForPixelAsync(window.Handle, loginButtonColor, null, null, 50, 250)
                    .GetAwaiter()
                    .GetResult();

                //check if pixel is found
                if (pixel == null)
                {
                    //pixel is not found, since some quite big delay already passed we can just press login button
                }

                //the login button some times takes more time to respons so a delay is required
                Thread.Sleep(LARGE_DELAY);

                //send enter key to initiate login
                keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.RETURN);

                //keep the keyboard locked for little more time so the password copying would not be possible
                Thread.Sleep(EXTRA_LARGE_DELAY);
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

            return targetProcess;
        }

        private static async Task<CaptchaResult> EpicCaptchaShowAsync(CancellationToken ct = default)
        {
            //get executing assembly
            var exeAssembly = Assembly.GetExecutingAssembly();

            //get assembly name
            var assemblyName = Path.GetFileNameWithoutExtension(exeAssembly.GetName().Name);

            //create variable to hold our HTML content
            string HTML_CONTENT = null;

            //open default resource stream in current assembly
            using (Stream sourceStream = exeAssembly.GetManifestResourceStream($"{assemblyName}.Epic.arkose.html"))
            {
                if (sourceStream == null)
                    throw new ArgumentNullException(nameof(sourceStream));

                StringBuilder sb = new StringBuilder();

                byte[] buffer = new byte[0x1000];
                int numRead;
                while ((numRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                {
                    string text = Encoding.UTF8.GetString(buffer, 0, numRead);
                    sb.Append(text);
                }

                HTML_CONTENT = sb.ToString();
            }

            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                //create captcha host window
                EpicCaptchaWindow window = new EpicCaptchaWindow();

                //create new script manager for JS interop
                ScriptManager sm = new ScriptManager(window);

                //register cancellation callback
                ct.Register(async () =>
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        window.DialogResult = null;
                        window.Close();
                    });
                });

                //set as script manager of our browser window
                window.BROWSER_WINDOW.ObjectForScripting = sm;

                //on loaded event set navigate browser window to HTML Content
                window.Loaded += (s, e) =>
                {
                    window.BROWSER_WINDOW.NavigateToString(HTML_CONTENT);
                };

                try
                {
                    //show the dialog windows
                    if (window.ShowDialog() == true)
                    {
                        //if we succeeded return success code
                        return new CaptchaResult() { DialogResult = true, Token = sm.Token };
                    }
                }
                catch
                {
                    throw;
                }
                finally
                {
                    //try to get rid of the web browser component
                    try
                    {
                        //try to dispose the web browser
                        window.BROWSER_WINDOW.Source = null;
                        window.BROWSER_WINDOW.Dispose();
                    }
                    catch
                    {
                        //igonre any errors
                    }
                }

                return new CaptchaResult() { DialogResult = window.DialogResult };
            });
        }

        private static async Task<Pixel> WaitForPixelAsync(IntPtr windowHandle, Color color, int? x = default, int? y = null, int retries = 100, int delay = 250, CancellationToken ct = default)
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

                    await Task.Delay(delay, ct);
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

    #region EPICEXCEPTION
    public class EpicException : Exception
    {
        #region CONSTRUCTOR

        public EpicException(string message) : base(message)
        { }

        public EpicException(string message, string epicErrorCode) : base(message)
        {
            EpicErrorCode = epicErrorCode;
        }

        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets epic error code.
        /// </summary>
        public string EpicErrorCode
        {
            get; protected set;
        }

        #endregion

        #region OVERRIDES
        public override string ToString()
        {
            return $"Epic error message {Message}, Epic error code {EpicErrorCode}";
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

    #region CAPTCHARESULT
    /// <summary>
    /// Captcha result.
    /// </summary>
    /// <remarks>
    /// Provides result for epic captcha function.
    /// </remarks>
    public class CaptchaResult
    {
        #region PROPERTIES

        /// <summary>
        /// Gets captcha window dialog result.
        /// </summary>
        public bool? DialogResult
        {
            get; internal set;
        }

        /// <summary>
        /// Gets captcha token.
        /// </summary>
        public string Token
        {
            get; internal set;
        }

        #endregion
    }
    #endregion

    #region EPICCAPTCHASCRIPTMANAGER

    /// <summary>
    /// Captcha script manager.
    /// </summary>
    /// <remarks>
    /// The class is used for java script interop.
    /// </remarks>
    [ComVisible(true)]
    public class ScriptManager
    {
        #region CONSTRUCTOR
        /// <summary>
        /// Default constructor.
        /// </summary>
        /// <param name="window">Associated window.</param>
        public ScriptManager(Window window)
        {
            // Save the form so it can be referenced later.
            captchaWindow = window;
        }
        #endregion

        #region FIELDS
        private Window captchaWindow;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Resulted token value.
        /// </summary>
        public string Token { get; protected set; }

        #endregion

        #region JAVA SCRIPT CALLBACKS

        /// <summary>
        /// The method will be called from java script once captcha verification completes.
        /// </summary>
        /// <param name="token">Resulted token.</param>
        public void OnCaptchaCompleted(string token)
        {
            if (captchaWindow == null)
                return;

            Token = token;
            captchaWindow.DialogResult = true;
            captchaWindow.Close();
            captchaWindow = null;
        }

        /// <summary>
        /// This will be called if user closes the window.
        /// </summary>
        public void OnWebWindowClosed()
        {
            if (captchaWindow == null)
                return;

            Token = null;
            captchaWindow.DialogResult = false;
            captchaWindow.Close();
        }

        #endregion
    }

    #endregion

    #region EXCHANGECODERESULT
    [DataContract()]
    public class ExchangeCodeResult
    {
        #region PROPERTIES
        [DataMember(Name = "code")]
        public string Code
        {
            get; set;
        }
        #endregion
    }
    #endregion

    #region REDIRECTRESULT
    [DataContract()]
    public class RedirectResult
    {
        #region PROPERTIES

        [DataMember(Name = "redirectUrl")]
        public string RedirectUrl { get; set; }

        [DataMember(Name = "sid")]
        public Guid Sid
        {
            get; set;
        }

        #endregion
    }
    #endregion

    #region AKROSETOKENRESULT
    public class AkroseTokenResult
    {
        #region PROPERTIES
        [DataMember(Name = "token")]
        public string Token
        {
            get; set;
        }
        #endregion
    }
    #endregion

    #region EPICAPIERROR
    public class EpicApiError
    {
        #region PROPERTIES
        [DataMember(Name = "errorCode")]
        public string ErrorCode
        {
            get; set;
        }

        [DataMember(Name = "message")]
        public string Message
        {
            get; set;
        }
        #endregion
    }
    #endregion

    #region EPICAPIERRORCODES
    public class EpicApiErrorCodes
    {
        #region CONST
        public const string INVALID_ACCOUNT_CREDENTIALS = "errors.com.epicgames.account.invalid_account_credentials";
        public const string ACCOUNT_LOCKED = "errors.com.epicgames.account.account_locked";
        public const string INVALID_CAPTCHA = "errors.com.epicgames.accountportal.captcha_invalid";
        public const string REPUTATION_SESSION_INVALIDATED = "errors.com.epicgames.accountportal.reputation_session_invalidated";
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

        [DllImport("User32.Dll")]
        public static extern long SetCursorPos(int x, int y);

        [DllImport("User32.Dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }
    }

    #endregion
}
