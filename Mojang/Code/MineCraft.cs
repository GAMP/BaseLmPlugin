using Client;
using IntegrationLib;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("Minecraft",
        "1.0.0.0",
        "Manages license keys by writing auth token to application configuration.",
        "BaseLmPlugin;BaseLmPlugin.Resources.Icons.minecraft.png")]
    public class MineCraftLicenseManager : LicenseManagerBase
    {
        #region CONSTRUCTOR
        public MineCraftLicenseManager()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnJSONAssemblyResolve;
        }
        #endregion

        #region FIELDS
        private static readonly string API_PATH = @"https://authserver.mojang.com/authenticate";
        private static readonly string API_REFRESH_PATH = @"https://authserver.mojang.com/refresh";
        private static readonly string TARGET_DIRECTORY_PATH = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        private static readonly string PROFILES_FILE_PATH = Path.Combine(TARGET_DIRECTORY_PATH, "launcher_profiles.json");
        private static readonly string ACCOUNTS_FILE_PATH = Path.Combine(TARGET_DIRECTORY_PATH, "launcher_accounts.json");
        #endregion

        #region OVERRIDES

        public override bool CanAdd
        {
            get
            {
                return true;
            }
        }

        public override bool CanEdit
        {
            get
            {
                return true;
            }
        }

        public override IApplicationLicenseKey EditLicense(IApplicationLicenseKey key, ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.UserNamePassword, key, profile);
            return context.Display(owner) ? context.Key : null;
        }

        public override IApplicationLicenseKey GetLicense(ILicenseProfile profile, ref bool additionHandled, Window owner)
        {
            var context = new DialogContext(DialogType.UserNamePassword, new MineCraftLicenseKey(), profile);
            return context.Display(owner) ? context.Key : null;
        }

        public override void Install(IApplicationLicense license, IExecutionContext context, ref bool forceCreation)
        {
            if (context.HasCompleted | context.AutoLaunch)
            {
                var key = license.KeyAs<MineCraftLicenseKey>();

                #region VALIDATION

                if (key == null)
                    throw new ArgumentNullException("Key");

                if (string.IsNullOrWhiteSpace(key.Username))
                    throw new ArgumentNullException("Username");

                if (string.IsNullOrWhiteSpace(key.Password))
                    throw new ArgumentNullException("Password");

                #endregion

                #region AUTH
                var auth = new AuthenticateRequest
                {
                    ClientToken = Guid.NewGuid().ToString(),
                    Username = key.Username,
                    Password = key.Password,
                    RequestUser = true,
                };

                WebRequest request = WebRequest.Create(API_PATH);
                request.ContentType = "application/json";
                request.Method = "POST";

                try
                {
                    using (var requestStream = request.GetRequestStream())
                    {
                        string input = Newtonsoft.Json.JsonConvert.SerializeObject(auth);
                        byte[] requestPayload = Encoding.Default.GetBytes(input);
                        requestStream.Write(requestPayload, 0, requestPayload.Length);
                    };
                }
                catch (WebException)
                {
                    throw;
                }
                #endregion

                #region RESPONSE
                AuthenticateRespnse authResponse;

                try
                {
                    var response = request.GetResponse();
                    using (var responseStream = response.GetResponseStream())
                    {
                        using (StreamReader reader = new StreamReader(responseStream))
                        {
                            var respnseString = reader.ReadToEnd();
                            authResponse = Newtonsoft.Json.JsonConvert.DeserializeObject<AuthenticateRespnse>(respnseString);
                        }
                    }
                }
                catch (WebException)
                {
                    throw;
                }
                #endregion

                #region REFRESH

                request = WebRequest.Create(API_REFRESH_PATH);
                request.ContentType = "application/json";
                request.Method = "POST";

                try
                {
                    var refreshPayload = new RefreshPayload();
                    refreshPayload.accessToken = authResponse.AccessToken;
                    refreshPayload.clientToken = authResponse.ClientToken;

                    using (var requestStream = request.GetRequestStream())
                    {
                        string input = Newtonsoft.Json.JsonConvert.SerializeObject(refreshPayload);
                        byte[] requestPayload = Encoding.Default.GetBytes(input);
                        requestStream.Write(requestPayload, 0, requestPayload.Length);
                    };
                }
                catch (WebException)
                {
                    throw;
                }



                try
                {
                    var response = request.GetResponse();
                    using (var responseStream = response.GetResponseStream())
                    {
                        using (StreamReader reader = new StreamReader(responseStream))
                        {
                            var respnseString = reader.ReadToEnd();

                        }
                    }
                }
                catch (WebException)
                {
                    throw;
                }
                #endregion

                #region PROFILE

                //get selected profile
                var selectedProfile = authResponse.SelectedProfile;

                if (selectedProfile == null)
                {
                    selectedProfile = new MinecraftProfile()
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "Demo"
                    };
                }

                //create profile
                var launcherProfile = new LauncherProfile()
                {
                    ClientToken = authResponse.ClientToken,
                };

                //get user instance
                var user = authResponse.User;

                //check if user is present
                if (user == null)
                {
                    user = new MinecraftProfile()
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "Demo",
                    };
                }

                //create auth database entry
                launcherProfile.AuthenticationDatabase.Add(user.Id, new Authentication()
                {
                    Username = auth.Username,
                    AccessToken = authResponse.AccessToken,
                    Profiles = new Dictionary<string, MinecraftUserProfile>()
                    {
                        {  selectedProfile.Id , new MinecraftUserProfile() { DisplayName = selectedProfile.Name} }
                    }
                });

                launcherProfile.SelectedUser = new MineCraftUser()
                {
                    Account = user.Id,
                    Profile = selectedProfile.Id,
                };

                //create destination directory if required
                if (!Directory.Exists(TARGET_DIRECTORY_PATH))
                    Directory.CreateDirectory(TARGET_DIRECTORY_PATH);

                //delete existing profile
                try
                {
                    if (File.Exists(ACCOUNTS_FILE_PATH))
                        File.Delete(ACCOUNTS_FILE_PATH);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed deleting {PROFILES_FILE_PATH}, error message {ex.Message}");
                }

                var output = Newtonsoft.Json.JsonConvert.SerializeObject(launcherProfile, Newtonsoft.Json.Formatting.Indented);

                //write to configuration file
                using (var launcher_profile_stream = new FileStream(PROFILES_FILE_PATH, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    //clear all file contents
                    launcher_profile_stream.SetLength(0);
                    //write data
                    using (StreamWriter writer = new StreamWriter(launcher_profile_stream))
                    {
                        writer.Write(output);
                    }
                }
                #endregion
            }
        }

        public override void Uninstall(IApplicationLicense license)
        {
            if (File.Exists(PROFILES_FILE_PATH))
            {
                try
                {
                    //delete configuration file
                    File.Delete(PROFILES_FILE_PATH);
                }
                catch
                { }
            }
        }

        #endregion

        #region EVENT HANDLERS

        private Assembly OnJSONAssemblyResolve(object sender, ResolveEventArgs args)
        {
            //check if request is coming from current assembly
            if (args.RequestingAssembly != args.RequestingAssembly)
                return null;

            //get requested assembly name
            var REQUESTED_NAME = args.Name?.Split(',').FirstOrDefault();

            //check if name supplied
            if (string.IsNullOrWhiteSpace(REQUESTED_NAME))
                return null;

            //compare to desired assembly
            if (string.Compare(REQUESTED_NAME, "Newtonsoft.Json", true) == 0)
            {
                //we no longer need to handle resolve event
                AppDomain.CurrentDomain.AssemblyResolve -= OnJSONAssemblyResolve;

                //try to obtain json assembly
                return AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Where(assembly => assembly.FullName.StartsWith(REQUESTED_NAME, StringComparison.CurrentCultureIgnoreCase))
                    .FirstOrDefault();
            }

            //not desired assembly
            return null;
        }

        #endregion
    }

    #region LauncherProfile
    [Serializable()]
    [DataContract()]
    public class LauncherProfile
    {
        public LauncherProfile()
        {
            AuthenticationDatabase = new Dictionary<string, Authentication>();
        }

        [DataMember(Name = "authenticationDatabase", Order = 0)]
        public Dictionary<string, Authentication> AuthenticationDatabase
        {
            get;
            set;
        }

        [DataMember(Name = "selectedUser", Order = 1)]
        public MineCraftUser SelectedUser
        {
            get; set;
        }

        [DataMember(Name = "clientToken", Order = 2)]
        public string ClientToken
        {
            get;
            set;
        }
    }
    #endregion

    #region MineCraftUser
    [Serializable()]
    [DataContract()]
    public class MineCraftUser
    {
        [DataMember(Name = "account")]
        public string Account
        {
            get; set;
        }

        [DataMember(Name = "profile")]
        public string Profile
        {
            get; set;
        }
    }
    #endregion

    #region MinecraftUserProfile
    [Serializable()]
    [DataContract()]
    public class MinecraftUserProfile
    {
        [DataMember(Name = "displayName")]
        public string DisplayName
        {
            get;
            set;
        }
    }
    #endregion

    #region Authentication
    [Serializable()]
    [DataContract()]
    public class Authentication
    {
        [DataMember(Name = "username")]
        public string Username
        {
            get;
            set;
        }

        [DataMember(Name = "accessToken")]
        public string AccessToken
        {
            get;
            set;
        }

        [DataMember(Name = "profiles")]
        public Dictionary<string, MinecraftUserProfile> Profiles
        {
            get; set;
        }
    }
    #endregion

    #region AuthenticateRequest
    [DataContract()]
    [Serializable()]
    public class AuthenticateRequest
    {
        public AuthenticateRequest()
        {
            Agent = new Agent
            {
                Version = 1,
                Name = "Minecraft"
            };
        }

        [DataMember(Name = "agent")]
        public Agent Agent
        {
            get;
            set;
        }

        [DataMember(Name = "username")]
        public string Username
        {
            get;
            set;
        }

        [DataMember(Name = "password")]
        public string Password
        {
            get;
            set;
        }

        [DataMember(Name = "clientToken")]
        public string ClientToken
        {
            get;
            set;
        }

        [DataMember(Name = "requestUser")]
        public bool RequestUser
        {
            get; set;
        }
    }
    #endregion

    #region AuthenticateRespnse
    [Serializable()]
    [DataContract()]
    public class AuthenticateRespnse
    {
        public AuthenticateRespnse()
        {
            AvailableProfiles = new List<MinecraftProfile>();
        }

        [DataMember(Name = "availableProfiles")]
        public List<MinecraftProfile> AvailableProfiles
        {
            get;
            set;
        }

        [DataMember(Name = "selectedProfile")]
        public MinecraftProfile SelectedProfile
        {
            get;
            set;
        }

        [DataMember(Name = "user")]
        public MinecraftProfile User
        {
            get; set;
        }

        [DataMember(Name = "accessToken")]
        public string AccessToken
        {
            get;
            set;
        }

        [DataMember(Name = "clientToken")]
        public string ClientToken
        {
            get;
            set;
        }
    }
    #endregion

    #region Agent
    [DataContract()]
    [Serializable()]
    public class Agent
    {
        [DataMember(Name = "name")]
        public string Name
        {
            get;
            set;
        }

        [DataMember(Name = "version")]
        public int Version
        {
            get;
            set;
        }
    }
    #endregion

    #region MinecraftProfile
    [Serializable()]
    [DataContract()]
    public class MinecraftProfile
    {
        [DataMember(Name = "id")]
        public string Id
        {
            get;
            set;
        }

        [DataMember(Name = "name")]
        public string Name
        {
            get;
            set;
        }
    }
    #endregion

    public class SelectedProfile
    {
        public string id { get; set; }
        public string name { get; set; }
    }

    public class RefreshPayload
    {
        public string accessToken { get; set; }
        public string clientToken { get; set; }
        public SelectedProfile selectedProfile { get; set; }
        public bool requestUser { get; set; }
    }

}
