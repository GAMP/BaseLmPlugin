using Client;
using Gizmo.Client;
using IntegrationLib;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Windows;

namespace BaseLmPlugin
{
    #region EPICLICENSEMANAGER
    [Export(typeof(ILicenseManagerPlugin))]
    [PluginMetadata("Epic", "1.0.0.0", "Manages by launching epic launcher process with remember me user code token saved.", "BaseLmPlugin;BaseLmPlugin.Resources.Icons.epic.png")]
    public class EpicLicenseManager : SteamLicenseManager
    {
        #region CONSTRUCTOR
        [ImportingConstructor()]
        public EpicLicenseManager()
        {
        }
        #endregion

        #region FIELDS
        private bool DIVERT_EXECUTION = false;
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
            var context = new DialogContext(DialogType.UserNamePassword, new EpicLicenseKey(), profile);
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
                var key = license.KeyAs<EpicLicenseKey>();
                if (key == null)
                    throw new ArgumentException("Invalid key type.", nameof(key));

                //get context executable
                var executable = context.Executable;

                //create init parameters
                var epicInitParameters = new EpicInitParameters()
                {
                    FilePath = executable.ExecutablePath,
                    Arguments = executable.Arguments,
                    WorkingDirectory = executable.WorkingDirectory,
                    Username = key.Username,
                    Password = key.Password,
                };

                //by default reset diver execution flag
                DIVERT_EXECUTION = false;

                //create cancellation token source
                var cancellationTokenSource = new CancellationTokenSource();

                //create state handlers
                var contextStateEventHandler = new EventHandler<ExecutionContextStateArgs>((o, e) =>
                {
                    try
                    {
                        if (e.NewState == SharedLib.ContextExecutionState.Aborting)
                            cancellationTokenSource.Cancel();
                    }
                    catch
                    {
                        //ignore errors
                    }
                });

                var userStateEventHandler = new EventHandler<UserLoginStateChangeEventArgs>((o, e) =>
                {
                    try
                    {
                        if (e.State ==  Gizmo.LoginState.LoggingOut)
                            cancellationTokenSource.Cancel();
                    }
                    catch
                    {
                        //ignore errors
                    }
                });

                try
                {
                    //attach local handlers
                    //they are used within installation routine so we can act on desired events
                    //and cancel installation if required
                    context.ExecutionStateChaged += contextStateEventHandler;
                    context.Client.LoginStateChange += userStateEventHandler;

                    var result = EpicLicenseHandler.Initiate(epicInitParameters, context);

                    if (result.InitResult == EpicInitResultCode.Success)
                    {
                        var createdProcess = result.CreatedProcess;
                        if (createdProcess != null)
                        {
                            //since process is created we need to divert the execution
                            DIVERT_EXECUTION = true;
                        }
                    }
                    else if (result.InitResult == EpicInitResultCode.ManualSignInRequired)
                    {
                        //automation could not complete the sign in (e.g. Epic captcha / security check)
                        //the launcher is left running so the user can finish signing in manually;
                        //still divert execution to the launcher instead of launching the game directly
                        if (result.CreatedProcess != null)
                            DIVERT_EXECUTION = true;

                        context.WriteMessage("Epic automatic sign-in could not be completed (security check). Please sign in manually in the Epic Games Launcher.");
                    }
                    else if (result.InitResult == EpicInitResultCode.Canceled)
                    {
                        //do nothing
                    }
                    else
                    {
                        if (result.Exception != null)
                            throw result.Exception;

                        throw new Exception("Unknown Epic handling error.");
                    }
                }
                catch (OperationCanceledException)
                {
                    //do nothing
                }
                catch (Exception ex)
                {
                    //log possible exceptions here
                    context.WriteMessage($"Epic license manager error {ex.Message}");
                }
                finally
                {
                    //remove state change handlers
                    context.ExecutionStateChaged -= contextStateEventHandler;
                    context.Client.LoginStateChange -= userStateEventHandler;
                }

                //detach handlers
                context.ExecutionStateChaged -= OnExecutionStateChaged;

                //atach handlers
                context.ExecutionStateChaged += OnExecutionStateChaged;
            }
        }

        public override void Uninstall(IApplicationLicense license)
        {
            //check if user settings file exists
            if (File.Exists(EpicLicenseHandler.USER_SETTINGS_FILE_PATH))
            {
                if (!NativeMethods.WritePrivateProfileString("RememberMe", "Enable", "False", EpicLicenseHandler.USER_SETTINGS_FILE_PATH))
                    throw new Win32Exception();

                if (!NativeMethods.WritePrivateProfileString("RememberMe", "Data", null, EpicLicenseHandler.USER_SETTINGS_FILE_PATH))
                    throw new Win32Exception();
            }
        }

        public override System.Windows.Controls.UserControl GetConfigurationUI()
        {
            //return new instance of steam settings view control
            //since the settings are same with steam same view can be reused
            return new SteamSettingsView();
        }

        public override IPluginSettings GetSettingsInstance()
        {
            //return new instance of epic settings class
            return new EpicLicenseManagerSettings();
        }

        public override bool DivertExecution(IExecutionContext context)
        {
            //if divert execution was set then we will diver once
            if (DIVERT_EXECUTION)
            {
                //reset the flag after first diversion
                DIVERT_EXECUTION = false;

                //divert execution
                return true;
            }

            //no diversion should happen
            return false;
        }

        #endregion    
    }
    #endregion

    #region EPICLICENSEMANAGERSETTINGS
    [Serializable]
    public class EpicLicenseManagerSettings : SteamLicenseManagerSettings, IPluginSettings
    {
    }
    #endregion
}
