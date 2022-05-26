using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowsInput;

namespace BaseLmPlugin
{
    public static class BattleStateLogin
    {
        #region READ ONLY FIELDS

        //key used to encrypt and decrypt access and refresh token
        static readonly byte[] salt = new byte[] { 107, 205, 79, 161, 91, 191, 138, 144, 206, 128, 69, 43, 77, 99, 255, 16, 115, 45, 4, 223, 150, 239, 16, 248, 7, 93, 210, 169, 124, 136, 141, 195 };
        static readonly string SETTINGS_FILE_NAME = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Battlestate Games\BsgLauncher", "Settings");
        const string LOGIN_URL = "https://launcher.escapefromtarkov.com/launcher/login?launcherVersion=0.9.3.1023";

        #endregion

        #region PUBLIC FUNCTIONS

        public async static Task LoginAsync(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentNullException(nameof(email));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentNullException(nameof(password));

            using (var httpClient = new HttpClient())
            {
                var hardwareId = GetHardwareId();

                //hash the password
                string hashedPassword = null;

                //use md5 to hash the password
                using (var md5 = MD5.Create())
                {
                    var passwordBytes = Encoding.Default.GetBytes(password);
                    var hashBytes = md5.ComputeHash(passwordBytes);

                    //convert to hex
                    hashedPassword = hashBytes.ToHex(false);
                }

                var loginParameters = new BattleStateLoginParameters()
                {
                    Email = email,
                    Pass = hashedPassword,
                    HwCode = hardwareId
                };

                var loginParametersJson = JsonConvert.SerializeObject(loginParameters, Formatting.None, new JsonSerializerSettings()
                {
                    Formatting = Formatting.None,
                    NullValueHandling = NullValueHandling.Include,
                    StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
                });

                var loginParametersArray = Encoding.UTF8.GetBytes(loginParametersJson);
                var loginParametersData = Compress(loginParametersArray);
                var loginContent = new ByteArrayContent(loginParametersData);

                var loginResult = await httpClient.PostAsync(LOGIN_URL, loginContent);
                if (loginResult.IsSuccessStatusCode)
                {
                    var resultData = await loginResult.Content.ReadAsByteArrayAsync();
                    var errorResult = DeserializeCompressedResponse<BattleStateLoginError>(resultData);
                    if (errorResult.Err == 0)
                    {
                        var sucessData = errorResult.Data.ToString();
                        var loginResponse = JsonConvert.DeserializeObject<BattleStateLoginResult>(sucessData);

                        using (var settingsStream = new FileStream(SETTINGS_FILE_NAME, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        using (var streamReader = new StreamReader(settingsStream))
                        using (var jsonReader = new JsonTextReader(streamReader))
                        {
                            //create serializer
                            var serializer = new JsonSerializer();

                            //get current settings
                            var currentSettings = serializer.Deserialize<BattleStateSettings>(jsonReader);

                            //if file did not contain 
                            currentSettings ??= new BattleStateSettings();

                            //set our email as login
                            currentSettings.Login = email;

                            //add expiration time
                            currentSettings.Atet = DateTime.UtcNow.AddSeconds(loginResponse.ExpiresIn);

                            //set our encrypted access token
                            currentSettings.At = EncryptValue(loginResponse.AccessToken);

                            //set our encrypted refresh token
                            currentSettings.Rt = EncryptValue(loginResponse.RefreshToken);

                            //keep logged int must be enabled so the launcher would be using out access tokens
                            currentSettings.KeepLoggedIn = true;

                            //save login info
                            currentSettings.SaveLogin = true;

                            //write the settings
                            using (var streamWriter = new StreamWriter(settingsStream))
                            using (var jsonWriter = new JsonTextWriter(streamWriter))
                            {
                                //clear current file contents
                                settingsStream.SetLength(0);

                                //seek to the stream beggining
                                settingsStream.Seek(0, SeekOrigin.Begin);

                                //serialize the settings
                                serializer.Serialize(jsonWriter, currentSettings);
                            }
                        }
                    }
                    else
                    {
                        //create default error message
                        string errorMessage = $"Login failed with {errorResult.Err} code and error message {errorResult.Errmsg ?? "None"}.";

                        //try to create error message based on error code

                        //214 ENTER CAPTCHA
                        //230 MAX LOGIN COUNT

                        switch (errorResult.Err)
                        {
                            case 214:
                                errorMessage = "Captcha required.";
                                break;
                            case 230:
                                errorMessage = "Maximum logins reached.";
                                break;
                            default:
                                break;
                        }

                        throw new BattleStateLoginException(errorMessage);
                    }
                }

            }
        }

        public static async Task LoginAsync(int processId, string email, string password)
        {
            var targetProcess = Process.GetProcessById(processId);
            await Task.Delay(3000);
            Win32API.Modules.User32.SetForegroundWindowEx(targetProcess.MainWindowHandle);

            KeyboardSimulator keyboard = new KeyboardSimulator();

            keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
            Thread.Sleep(1000);
            keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);

            keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
            keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
            keyboard.TextEntry(email);

            Win32API.Modules.User32.SetForegroundWindowEx(targetProcess.MainWindowHandle);
            keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.TAB);
            keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);
            keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
            keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.BACK);
            keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);
            keyboard.TextEntry(password);

            await Task.CompletedTask;
        }

        #endregion

        #region PRIVATE FUNCTIONS

        public static string EncryptValue(string value)
        {
            using MemoryStream memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(value), false);
            using var memoryStream2 = new MemoryStream();
            using (AesManaged aesManaged = new AesManaged { Key = salt })
            {
                byte[] iv = aesManaged.IV;
                memoryStream2.Write(iv, 0, iv.Length);
                memoryStream2.Flush();
                ICryptoTransform transform = aesManaged.CreateEncryptor(salt, iv);
                using (CryptoStream cryptoStream = new CryptoStream(memoryStream2, transform, CryptoStreamMode.Write))
                {
                    memoryStream.CopyTo(cryptoStream);
                }

                return Convert.ToBase64String(memoryStream2.ToArray());
            }
        }

        public static string DecryptValue(string value)
        {
            using MemoryStream memoryStream = new MemoryStream(Convert.FromBase64String(value), false);
            using MemoryStream memoryStream2 = new MemoryStream();
            using (AesManaged aesManaged = new AesManaged { Key = salt })
            {
                byte[] array = new byte[16];

                if (memoryStream.Read(array, 0, 16) < 16)
                    throw new ArgumentException("String is smaller than expected.");

                ICryptoTransform transform = aesManaged.CreateDecryptor(salt, array);
                using (CryptoStream cryptoStream = new CryptoStream(memoryStream, transform, CryptoStreamMode.Read))
                {
                    cryptoStream.CopyTo(memoryStream2);
                }
                return Encoding.UTF8.GetString(memoryStream2.ToArray());
            }
        }

        private static T DeserializeCompressedResponse<T>(byte[] data)
        {
            var decompressedData = Decompress(data);
            var dataString = Encoding.Default.GetString(decompressedData);
            return JsonConvert.DeserializeObject<T>(dataString);
        }

        public static byte[] Decompress(byte[] data)
        {
            int startIndex = 0;
            int dataLength = data.Length;
            if (data[0] == 120)
            {
                startIndex = 2;
                dataLength -= 2;
            }

            using MemoryStream decompressedStream = new MemoryStream();
            using (MemoryStream compressStream = new MemoryStream(data, startIndex, dataLength))
            {
                using (DeflateStream deflateStream = new DeflateStream(compressStream, CompressionMode.Decompress))
                {
                    deflateStream.CopyTo(decompressedStream);
                }
            }
            return decompressedStream.ToArray();
        }

        public static byte[] Compress(byte[] data)
        {
            using (var input = new MemoryStream(data))
            using (var compressStream = new MemoryStream())
            using (var compressor = new DeflateStream(compressStream, CompressionMode.Compress, true))
            {
                //write zlib header
                compressStream.Write(new byte[] { 120, 156 }, 0, 2);
                input.CopyTo(compressor);
                compressStream.Flush();

                // write adler footer
                const uint A32Mod = 65521;
                uint s1 = 1, s2 = 0;
                foreach (byte b in data)
                {
                    s1 = (s1 + b) % A32Mod;
                    s2 = (s2 + s1) % A32Mod;
                }

                int adler32 = unchecked((int)((s2 << 16) + s1));
                var adlerData = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(adler32));

                compressStream.Flush();
                compressor.Close();

                compressStream.Write(adlerData, 0, 4);

                return compressStream.ToArray();
            }
        }

        private static string HashDictionary(HashAlgorithm hasher, Dictionary<string, string> dictionary)
        {
            string text = string.Concat(dictionary.Values);
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentNullException(nameof(text));

            return hasher.ComputeHash(Encoding.UTF8.GetBytes(text)).ToHex(false);
        }

        #endregion

        #region EXTENSION METHODS

        public static string ToHex(this byte[] bytes, bool upperCase)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);

            for (int i = 0; i < bytes.Length; i++)
                result.Append(bytes[i].ToString(upperCase ? "X2" : "x2"));

            return result.ToString();
        }

        public static long ToUnixTimeStamp(this DateTime dateTime)
        {
            return (long)dateTime.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
        }

        #endregion

        #region HARDWARE FUNCTIONS

        public static string GetHardwareId()
        {
            using (SHA1 sha = SHA1.Create())
            {
                long num = DateTime.UtcNow.ToUnixTimeStamp() / 1000000L;
                string[] array = new string[]
                {
                    "#1",
                    null,
                    GetBiosInfo(sha),
                    GetBaseBoardInfo(sha),
                    GetProcessorInfo(sha),
                    GetOsInfo(sha),
                    GetSerialNumbers(),
                };

                string str = string.Concat(array);
                array[1] = string.Join(":", new byte[][]
                {
                    sha.ComputeHash(Encoding.UTF8.GetBytes((num - 1L).ToString() + str)),
                    sha.ComputeHash(Encoding.UTF8.GetBytes(num.ToString() + str)),
                    sha.ComputeHash(Encoding.UTF8.GetBytes((num + 1L).ToString() + str))
                }.Select(new Func<byte[], string>(e => e.ToHex(false))));

                return string.Join("-", array);
            }
        }

        private static string GetBiosInfo(HashAlgorithm hasher)
        {
            return HashDictionary(hasher, QueryHardwareInfo<string>("SELECT Manufacturer, Name, SerialNumber FROM Win32_BIOS", new string[]
            {
                "Manufacturer",
                "Name",
                "SerialNumber"
            }));
        }

        private static string GetBaseBoardInfo(HashAlgorithm hasher)
        {
            return HashDictionary(hasher, QueryHardwareInfo<string>("SELECT Manufacturer,Name,Product,SerialNumber FROM Win32_BaseBoard", new string[]
            {
                "Manufacturer",
                "Name",
                "Product",
                "SerialNumber",
            }));
        }

        private static string GetProcessorInfo(HashAlgorithm hasher)
        {
            return HashDictionary(hasher, QueryHardwareInfo<string>("SELECT Manufacturer,Name,SerialNumber,UniqueId FROM Win32_Processor", new string[]
            {
                "Manufacturer",
                "Name",
                "SerialNumber",
                "UniqueId",
            }));
        }

        private static string GetOsInfo(HashAlgorithm hasher)
        {
            return HashDictionary(hasher, QueryHardwareInfo<string>("SELECT Manufacturer,SerialNumber FROM Win32_OperatingSystem", new string[]
            {
                "Manufacturer",
                "SerialNumber",
            }));
        }

        private static string GetSerialNumbers()
        {
            string string1 = QueryHardwareInfo<string>("SELECT SerialNumber FROM Win32_BaseBoard", new string[] { "SerialNumber" })["SerialNumber"];
            string string2 = QueryHardwareInfo<string>("SELECT SerialNumber FROM Win32_BIOS", new string[] { "SerialNumber" })["SerialNumber"];
            string string3 = QueryHardwareInfo<string>("SELECT UniqueId FROM Win32_Processor", new string[] { "UniqueId" })["UniqueId"];
            string string4 = QueryHardwareInfo<string>("SELECT SerialNumber FROM Win32_OperatingSystem", new string[] { "SerialNumber" })["SerialNumber"];

            using (SHA1 sha = SHA1.Create())
            {
                string s = sha.ComputeHash(Encoding.UTF8.GetBytes(string1 + string2 + string3 + string4)).ToHex(false);
                using (MD5 md = MD5.Create())
                {
                    return md.ComputeHash(Encoding.UTF8.GetBytes(s)).ToHex(false);
                }
            }
        }

        private static Dictionary<string, T> QueryHardwareInfo<T>(string query, string[] parameters) where T : class
        {
            Dictionary<string, T> dictionary = new Dictionary<string, T>();
            ManagementObject managementObject;
            try
            {
                managementObject = new ManagementObjectSearcher(query).Get()
                    .Cast<ManagementObject>()
                    .FirstOrDefault<ManagementObject>();
            }
            catch
            {
                throw;
                //List<string> list = new List<string>();
                //try
                //{
                //    foreach (PropertyData propertyData in new ManagementObjectSearcher(Regex.Replace(string_0, Class82.smethod_0(32476), Class82.smethod_0(29913))).Get().OfType<ManagementObject>().First<ManagementObject>().Properties)
                //    {
                //        List<string> list2 = list;
                //        string name = propertyData.Name;
                //        string str = Class82.smethod_0(32432);
                //        object value = propertyData.Value;
                //        list2.Add(name + str + ((value != null) ? value.ToString() : null));
                //    }
                //}
                //catch
                //{
                //    list.Add(Class82.smethod_0(32434));
                //}
                //this.ilogger_0.ForContext(Class82.smethod_0(32399), list).Error(exc, Class82.smethod_0(32408), new object[]
                //{
                //    string_0,
                //    Environment.OSVersion.VersionString
                //});
                //result = dictionary;
                //return result;
            }


            if (managementObject == null)
            {
                return dictionary;
            }
            foreach (string text in parameters)
            {
                try
                {
                    dictionary.Add(text, managementObject[text] as T);
                }
                catch
                {
                    throw;
                }
            }
            return dictionary;
        }

        #endregion
    }

    #region BattleStateLoginParameters
    public class BattleStateLoginParameters
    {

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("pass")]
        public string Pass { get; set; }

        [JsonProperty("hwCode")]
        public string HwCode { get; set; }

        [JsonProperty("captcha")]
        public object Captcha { get; set; }
    }
    #endregion

    #region BattleStateLoginError
    public class BattleStateLoginError
    {

        [JsonProperty("err")]
        public int Err { get; set; }

        [JsonProperty("errmsg")]
        public string Errmsg { get; set; }

        [JsonProperty("data")]
        public object Data { get; set; }
    }
    #endregion

    #region BattleStateLoginResult
    public class BattleStateLoginResult
    {
        [JsonProperty("aid")]
        public string Aid { get; set; }

        [JsonProperty("lang")]
        public string Lang { get; set; }

        [JsonProperty("region")]
        public object Region { get; set; }

        [JsonProperty("gameVersion")]
        public object GameVersion { get; set; }

        [JsonProperty("dataCenters")]
        public IList<object> DataCenters { get; set; }

        [JsonProperty("ipRegion")]
        public string IpRegion { get; set; }

        [JsonProperty("checkLegal")]
        public bool CheckLegal { get; set; }

        [JsonProperty("token_type")]
        public string TokenType { get; set; }

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }
    }
    #endregion

    #region BattleStateBranch
    public class BattleStateBranch
    {
        [JsonProperty("gameRootDir")]
        public object GameRootDir { get; set; }

        [JsonProperty("isSelected")]
        public bool IsSelected { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }
    #endregion

    #region BattleStateSettings
    public class BattleStateSettings
    {
        [JsonProperty("tempFolder")]
        public string TempFolder { get; set; }

        [JsonProperty("hardwareHash")]
        public string HardwareHash { get; set; }

        [JsonProperty("branches")]
        public IList<BattleStateBranch> Branches { get; set; }

        [JsonProperty("login")]
        public string Login { get; set; }

        [JsonProperty("at")]
        public string At { get; set; }

        [JsonProperty("atet")]
        public DateTime Atet { get; set; }

        [JsonProperty("rt")]
        public string Rt { get; set; }

        [JsonProperty("keepLoggedIn")]
        public bool KeepLoggedIn { get; set; }

        [JsonProperty("saveLogin")]
        public bool SaveLogin { get; set; }

        [JsonProperty("language")]
        public string Language { get; set; }

        [JsonProperty("launchOnStartup")]
        public bool LaunchOnStartup { get; set; }

        [JsonProperty("launchMinimized")]
        public bool LaunchMinimized { get; set; }

        [JsonProperty("closeBehavior")]
        public int CloseBehavior { get; set; }

        [JsonProperty("gameStartBehavior")]
        public int GameStartBehavior { get; set; }

        [JsonProperty("enableGameAutoUpdate")]
        public bool EnableGameAutoUpdate { get; set; }

        [JsonProperty("gameProcessId")]
        public int GameProcessId { get; set; }

        [JsonProperty("enableP2P")]
        public bool EnableP2P { get; set; }

        [JsonProperty("maxDownloadSpeed")]
        public int MaxDownloadSpeed { get; set; }

        [JsonProperty("maxUploadSpeed")]
        public int MaxUploadSpeed { get; set; }

        [JsonProperty("queueNotifyWithSound")]
        public bool QueueNotifyWithSound { get; set; }

        [JsonProperty("queueAutoLogIn")]
        public bool QueueAutoLogIn { get; set; }

        [JsonProperty("volumeValue")]
        public int VolumeValue { get; set; }

        [JsonProperty("lastSendingLogsTime")]
        public DateTime LastSendingLogsTime { get; set; }

        [JsonProperty("settingsVersion")]
        public int SettingsVersion { get; set; }
    }
    #endregion

    #region BattleStateLoginException
    /// <summary>
    /// Battle state login exception.
    /// </summary>
    [Serializable()]
    public class BattleStateLoginException : Exception
    {
        #region CONSTRUCTOR

        public BattleStateLoginException() : base()
        { }

        public BattleStateLoginException(string message) : base(message)
        { }

        protected BattleStateLoginException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }

        #endregion
    }
    #endregion
}
