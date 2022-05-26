using System;
using SharedLib;
using System.Windows.Controls;
using System.Windows;
using IntegrationLib;
using System.Threading;
using SkinInterfaces;
using System.IO;
using CoreLib.Imaging;

namespace BaseLmPlugin
{
    #region ENUMERATIONS
    public enum DialogType
    {
        Instance,
        Process,
        Executable,
        Steam,
        Registry,
        UserNamePassword,
        RegistryImport,
    }
    #endregion

    #region DIALOG CONTEXT
    public class DialogContext : PropertyChangedNotificator
    {
        #region FIELDS
        private UserControl content;
        private IApplicationLicenseKey key;
        private ILicenseProfile profile;
        private SimpleCommand<object, object> acceptCommand, cancelCommand;
        private Window dialog;
        #endregion

        #region PROPERTIES

        #region COMMANDS

        public SimpleCommand<object, object> AcceptCommand
        {
            get
            {
                if (acceptCommand == null)
                    acceptCommand = new SimpleCommand<object, object>(OnCanAcceptCommand, OnAcceptCommand);
                return acceptCommand;
            }
        }

        public SimpleCommand<object, object> CancelCommand
        {
            get
            {
                if (cancelCommand == null)
                    cancelCommand = new SimpleCommand<object, object>(OnCanCancelCommand, OnCancelCommand);
                return cancelCommand;
            }
        }

        #endregion

        public UserControl Content
        {
            get { return content; }
            protected set { content = value; }
        }

        public IApplicationLicenseKey Key
        {
            get { return key; }
            protected set { key = value; }
        }

        public ILicenseProfile Profile
        {
            get { return profile; }
            protected set { profile = value; }
        }

        private Window Dialog
        {
            get { return dialog; }
            set
            {
                dialog = value;
                RaisePropertyChanged("Dialog");
            }
        }

        #endregion

        #region CONSTRUCTOR
        public DialogContext(DialogType type, IApplicationLicenseKey key, ILicenseProfile profile)
        {
            Key = key;
            Profile = profile;
            switch (type)
            {
                case DialogType.Steam:
                    Content = new AddSteamKey();
                    break;
                case DialogType.UserNamePassword:
                    Content = new AddSteamKey(false);
                    break;
                case DialogType.Executable:
                case DialogType.Process:
                    Content = new AddProcessKey();
                    break;
                case DialogType.Registry:
                    Content = new AddRegistryKey();
                    break;
                case DialogType.RegistryImport:
                    Content = new RegistryImportKey();
                    break;
                default: break;
            }
        }
        #endregion

        #region FUNCTIONS

        public bool Display(Window owner)
        {
            Dialog = new KeyDialog
            {
                DataContext = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner
            };
            return (bool)Dialog.ShowDialog();
        }

        #endregion

        #region COMMAND IMPLEMENTATION

        private bool OnCanAcceptCommand(object param)
        {
            return Dialog != null;
        }

        private bool OnCanCancelCommand(object param)
        {
            return Dialog != null;
        }

        private void OnCancelCommand(object param)
        {
            Dialog.DialogResult = false;
            Dialog.Close();
        }

        private void OnAcceptCommand(object param)
        {
            Dialog.DialogResult = true;
            Dialog.Close();
        }

        #endregion
    }
    #endregion

    #region USERNAMEPASSWORDLICENSEKEYBASE
    [Serializable()]
    public class UserNamePasswordLicenseKeyBase : ApplicationLicenseKeyBase
    {
        #region FIELDS
        private string
            username,
            password;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets or sets licenses username.
        /// </summary>
        public string Username
        {
            get { return username; }
            set
            {
                username = value;
                RaisePropertyChanged("Username");
            }
        }

        /// <summary>
        /// Gets or sets license password.
        /// </summary>
        public string Password
        {
            get { return password; }
            set
            {
                password = value;
                RaisePropertyChanged("Password");
            }
        }

        /// <summary>
        /// Gets if license is valid.
        /// </summary>
        public override bool IsValid
        {
            get
            {
                return !((String.IsNullOrWhiteSpace(Username) & (String.IsNullOrWhiteSpace(Password))));
            }
        }

        /// <summary>
        /// Gets license literal string representation.
        /// </summary>
        public override string KeyString
        {
            get
            {
                return Username ?? "Invalid key";
            }
        }

        #endregion

        #region OVERRIDES
        public override string ToString()
        {
            return KeyString;
        }
        #endregion
    }
    #endregion    

    #region CLASSES
    public class TerminateWaitHandle : ManualResetEventSlim
    {
        #region CONSTRUCTOR
        public TerminateWaitHandle(bool initialState)
            : base(initialState)
        { }
        #endregion

        #region PROPERTIES
        /// <summary>
        /// Gets or sets if handle is waiting for termination.
        /// </summary>
        public bool WaitingTermination
        {
            get;
            internal set;
        }
        /// <summary>
        /// Gets process name.
        /// </summary>
        public string TerminatingProcesName
        {
            get;
            internal set;
        }
        #endregion
    }
    #endregion

    public class ExceptionHelper
    {
        public const string START_FAILURE_MESSAGE = "Failed starting {0} plugin process. Executable path {1}.";
        public const string EXECUTABLE_NOT_FOUND_FAILURE_MESSAGE = "{0} plugin executable not found. Executable path {1}.";
        public const string PROCESS_WINDOW_NOT_FOUND = "{0} plugin process no visible window found.";
        public const string WINDOW_WAIT_TIMEOUT = "{0} plugin process window ({1}) wait timed out after {2} milliseconds.";
        public const string PROCESS_WINDOW_WAIT_TIMEOUT = "{0} plugin process visible window wait timed out after {1} milliseconds.";
        public const string PIXEL_WAIT_TIMEOUT = "{0} plugin process window pixel ({1}) wait timed out after {2} milliseconds.";

        public static void ThrowStartFailureException(string pluginName, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
                throw new ArgumentNullException(nameof(pluginName));

            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentNullException(nameof(executablePath));

            var message = string.Format(START_FAILURE_MESSAGE, pluginName, executablePath);

            throw new ArgumentException(message);
        }

        public static void ThrowExecutableNotFoundException(string pluginName, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
                throw new ArgumentNullException(nameof(pluginName));

            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentNullException(nameof(executablePath));

            var message = string.Format(EXECUTABLE_NOT_FOUND_FAILURE_MESSAGE, pluginName, executablePath);

            throw new FileNotFoundException(message,executablePath);
        }

        public static void ThrowWindowTimedOut(string pluginName, string windowName, int timeout=-1)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
                throw new ArgumentNullException(nameof(pluginName));

            if (string.IsNullOrWhiteSpace(windowName))
                throw new ArgumentNullException(nameof(windowName));

            var message = string.Format(WINDOW_WAIT_TIMEOUT, pluginName, windowName,timeout);

            throw new ArgumentException(message);
        }

        public static void ThrowVisibleWindowTimedOut(string pluginName, int timeout = -1)
        {
            if (string.IsNullOrWhiteSpace(pluginName))
                throw new ArgumentNullException(nameof(pluginName));

            var message = string.Format(PROCESS_WINDOW_WAIT_TIMEOUT, pluginName, timeout);

            throw new ArgumentException(message);
        }

        public static void ThrowWindowPixelTimedOutIfPixelNull (Pixel pixel, string pluginName, string pixelName, int timeout = -1)
        {
            if (pixel != null)
            {
                if (string.IsNullOrWhiteSpace(pluginName))
                    throw new ArgumentNullException(nameof(pluginName));

                if (string.IsNullOrWhiteSpace(pixelName))
                    throw new ArgumentNullException(nameof(pixelName));

                var message = string.Format(WINDOW_WAIT_TIMEOUT, pluginName, pixelName, timeout);

                throw new ArgumentException(message);
            }
        }
    }
}
