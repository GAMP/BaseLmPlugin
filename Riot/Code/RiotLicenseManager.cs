using Client;
using IntegrationLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Win32API.Headers;
using Win32API.Modules;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("Riot", "1.0.0.0", "Manages by passing credentials to Riot UX process.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.riot.png")]
    public class RiotLicenseManager : SteamLicenseManager
    {
        #region PROPERTIES

        /// <summary>
        /// Gets or sets installed key.
        /// </summary>
        private RiotLicenseKey InstalledKey
        {
            get; set;
        }

        #endregion

        #region OVERRIDES

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
            var context = new DialogContext(DialogType.UserNamePassword, new RiotLicenseKey(), profile);
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
            var key = license.KeyAs<RiotLicenseKey>();

            //set installed key
            InstalledKey = key ?? throw new ArgumentException("Invalid key type.", nameof(key));

            //detach handlers
            context.ExecutionStateChaged -= OnExecutionStateChaged;

            //atach handlers
            context.ExecutionStateChaged += OnExecutionStateChaged;
        }

        public override void Uninstall(IApplicationLicense license)
        {
            //clear installed key
            InstalledKey = null;
        }

        public override bool DivertExecution(IExecutionContext context)
        {
            //execution should not be diverted
            return false;
        }

        public override IPluginSettings GetSettingsInstance()
        {
            //return new instance of epic settings class
            return new RiotLicenseManagerSettings();
        }

        protected override void OnExecutionStateChaged(object sender, ExecutionContextStateArgs e)
        {
            base.OnExecutionStateChaged(sender, e);

            //if we dont have installed key then there is nothing to do
            if (InstalledKey == null)
                return;

            if (e.NewState == SharedLib.ContextExecutionState.ProcessCreated)
            {
                //local process id variable
                int? processId = null;

                //try to obtain process id with process info object (new client version)
                if (TryGetProcessInfo(e.StateObject, out var processInfo))
                {
                    processId = processInfo?.ProcessId;
                }

                //check if process id was obtained
                if (processId.HasValue)
                {
                    try
                    {
                        //try to obtain the ux process by id
                        var process = Process.GetProcessById(processId.Value);

                        //if process name matches initiaye login
                        if (process.ProcessName == "RiotClientUx")
                        {
                            //get username
                            string username = InstalledKey?.Username;

                            //get password
                            string password = InstalledKey?.Password;

                            //check for validity
                            if (username == null || password == null)
                                return;

                            var sucess = RiotLogin.IPCLoginAsync(process.Id, username, password)
                                .GetAwaiter()
                                .GetResult();

                            if (sucess)
                            {
                                //clear the key so no subsequent logins would be made
                                //on new RiotClientUx creation
                                InstalledKey = null;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        //ignore
                    }
                    catch (ArgumentException)
                    {
                        //process exited
                    }
                    catch
                    {
                        //ignore
                    }
                }
            }
        }

        #endregion
    }

    #region RIOTLOGIN
    public class RiotLogin
    {
        #region PUBLIC FUNCTIONS

        public static async Task<bool> IPCLoginAsync(int processId, string username, string password, string region = "")
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentNullException(nameof(username));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(password);

            try
            {
                //ipc port of riot UX client
                int ipcPort = 0;

                //remoting token of riot UX client
                string ipcToken = null;

                //try to open riot ux process handle
                if (TryOpenHandle(PROCESS_SECURITY.PROCESS_QUERY_INFORMATION | PROCESS_SECURITY.PROCESS_VM_READ, false, processId, out IntPtr processHandle))
                {
                    try
                    {
                        //get process parameters
                        RTL_USER_PROCESS_PARAMETERS rtlPar = GetRtlUserParameters(processHandle);

                        //get current ux process command line arguments
                        var uxProcessCommandLine = rtlPar.CommandLine.ReadProcessUnicodeString(processHandle);

                        //check if process has command line arguments
                        if (string.IsNullOrWhiteSpace(uxProcessCommandLine))
                            throw new ArgumentNullException(nameof(uxProcessCommandLine));

                        //convert to arguments list
                        var arguments = CreateArgs(uxProcessCommandLine);

                        //get current ipc port
                        var portArgument = arguments.Where(argument => argument.StartsWith("--app-port"))
                            .FirstOrDefault();

                        //port arguments check
                        if (portArgument == null)
                            throw new ArgumentNullException(nameof(portArgument));

                        //get current ipc auth token
                        var ipcTokenArgument = arguments.Where(argument => argument.StartsWith("--remoting-auth-token"))
                            .FirstOrDefault();

                        //parse arguments
                        ipcPort = int.Parse(portArgument.Split('=')[1]);
                        ipcToken = ipcTokenArgument.Split('=')[1];

                        //check ipc token arguments
                        if (ipcToken == null)
                            throw new ArgumentNullException(nameof(ipcTokenArgument));
                    }
                    catch
                    {
                        //could not obtain rtlparameters

                        //could not parse arguments

                        throw;
                    }
                    finally
                    {
                        Kernel32.CloseHandle(processHandle);
                    }
                }

                //create credentials url
                string credentialsUrl = $"https://127.0.0.1:{ipcPort}/rso-auth/v1/session/credentials";

                //create authorization url
                string ipcAuthUrl = $"https://127.0.0.1:{ipcPort}/rso-auth/v2/authorizations";

                //create default auth header
                //the username is riot
                //the password is passed by launcher and should be present in --remoting-auth-token
                var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{ipcToken}"));

                //create handler
                var httpClientHandler = new HttpClientHandler
                {
                    //since the ipc will be using invalid ssl cert we need to allow it
                    ServerCertificateCustomValidationCallback = (message, certificate, chain, sslPolicyErrors) => true
                };

                using (var httpClient = new HttpClient(httpClientHandler))
                {
                    //add default auth headers
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

                    #region AUTH

                    var auth = new RiotAuth();
                    var authJson = JsonConvert.SerializeObject(auth);
                    var authContent = new StringContent(authJson, Encoding.UTF8, "application/json");

                    var authResult = await httpClient.PostAsync(ipcAuthUrl, authContent);
                    authResult.EnsureSuccessStatusCode();

                    #endregion

                    #region CREDENTIALS

                    var credential = new RiotCredentials()
                    {
                        username = username,
                        password = password,
                        region = region,
                    };

                    var credentialsJson = JsonConvert.SerializeObject(credential);
                    var credentialsContent = new StringContent(credentialsJson, Encoding.UTF8, "application/json");
                    var credentialsResponse = await httpClient.PutAsync(credentialsUrl, credentialsContent);
                    credentialsResponse.EnsureSuccessStatusCode();

                    #endregion
                }
                return true;
            }
            catch
            {
                //create custom exception here
                return false;
            }
        }

        #endregion

        #region PRIVATE FUNCTIONS

        private static string[] CreateArgs(string commandLine)
        {
            StringBuilder argsBuilder = new StringBuilder(commandLine);
            bool inQuote = false;

            // Convert the spaces to a newline sign so we can split at newline later on
            // Only convert spaces which are outside the boundries of quoted text
            for (int i = 0; i < argsBuilder.Length; i++)
            {
                if (argsBuilder[i].Equals('"'))
                {
                    inQuote = !inQuote;
                }

                if (argsBuilder[i].Equals(' ') && !inQuote)
                {
                    argsBuilder[i] = '\n';
                }
            }

            // Split to args array
            string[] args = argsBuilder.ToString().Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Clean the '"' signs from the args as needed.
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = ClearQuotes(args[i]);
            }

            return args;
        }

        private static string ClearQuotes(string stringWithQuotes)
        {
            int quoteIndex;
            if ((quoteIndex = stringWithQuotes.IndexOf('"')) == -1)
            {
                // String is without quotes..
                return stringWithQuotes;
            }

            // Linear sb scan is faster than string assignemnt if quote count is 2 or more (=always)
            StringBuilder sb = new StringBuilder(stringWithQuotes);
            for (int i = quoteIndex; i < sb.Length; i++)
            {
                if (sb[i].Equals('"'))
                {
                    // If we are not at the last index and the next one is '"', we need to jump one to preserve one
                    if (i != sb.Length - 1 && sb[i + 1].Equals('"'))
                    {
                        i++;
                    }

                    // We remove and then set index one backwards.
                    // This is because the remove itself is going to shift everything left by 1.
                    sb.Remove(i--, 1);
                }
            }

            return sb.ToString();
        }

        private static unsafe RTL_USER_PROCESS_PARAMETERS GetRtlUserParameters(IntPtr processHandle, IntPtr baseAddress)
        {
            RTL_USER_PROCESS_PARAMETERS peb = new RTL_USER_PROCESS_PARAMETERS();
            if (!Kernel32.ReadProcessMemory(processHandle, baseAddress, &peb, Marshal.SizeOf(peb), out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return peb;
        }

        private static unsafe RTL_USER_PROCESS_PARAMETERS GetRtlUserParameters(IntPtr processHandle)
        {
            PROCESS_BASIC_INFORMATION info = GetBasicInformation(processHandle);
            PEB peb = GetPeb(processHandle, info.PebBaseAddress);
            return GetRtlUserParameters(processHandle, peb.ProcessParameters);
        }

        private static unsafe PROCESS_BASIC_INFORMATION GetBasicInformation(IntPtr processHandle)
        {
            PROCESS_BASIC_INFORMATION info = new PROCESS_BASIC_INFORMATION();
            uint structSize = 0;
            if (Win32API.Modules.Ntdll.NtQueryInformationProcess(processHandle,
                ProcessInformationClass.ProcessBasicInformation, &info, (uint)Marshal.SizeOf(info), out structSize) >= NtStatus.Error)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return info;
        }

        private static unsafe PEB GetPeb(IntPtr processHandle, IntPtr baseAddress)
        {
            PEB peb = new PEB();
            if (!Kernel32.ReadProcessMemory(processHandle, baseAddress, &peb, Marshal.SizeOf(peb), out int read))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return peb;
        }

        public static IntPtr OpenHandle(PROCESS_SECURITY acess, bool inheritHandle, int processId)
        {
            return Kernel32.OpenProcess(acess, inheritHandle, processId);
        }

        public static bool TryOpenHandle(PROCESS_SECURITY acess, bool inheritHandle, int processId, out IntPtr handle)
        {
            handle = OpenHandle(acess, inheritHandle, processId);
            return handle != IntPtr.Zero;
        }

        #endregion

        #region CLASSES

        #region RiotCredentials
        class RiotCredentials
        {
            [JsonProperty("username")]
            public string username { get; set; }
            [JsonProperty("password")]
            public string password { get; set; }
            [JsonProperty("persistLogin")]
            public bool persistLogin { get; set; }
            [JsonProperty("region")]
            public string region { get; set; }
        }
        #endregion

        #region RiotAuth
        class RiotAuth
        {
            [JsonProperty("clientId")]
            public string clientId { get; set; } = "riot-client";
            [JsonProperty("trustLevels")]
            public List<string> trustLevels { get; set; } = new List<string>() { "always_trusted" };
        }
        #endregion

        #region Multifactor
        class Multifactor
        {
            [JsonProperty("email")]
            public string email { get; set; }
            [JsonProperty("method")]
            public string method { get; set; }
        }
        #endregion

        #endregion
    }
    #endregion

    #region RIOTLICENSEMANAGERSETTINGS
    [Serializable]
    public class RiotLicenseManagerSettings : SteamLicenseManagerSettings, IPluginSettings
    {
    }
    #endregion
}
