using Client;
using Gizmo.Client;
using Gizmo.Extensibility.Abstractions;
using IntegrationLib;
using System;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using System.Threading;

namespace BaseLmPlugin
{
    [Export(typeof(ILicenseManagerPlugin))]
    [Guid(Identifiers.Epic)]
    [PluginMetadata("Epic", "1.0.0.0", "Manages by launching epic launcher process with remember me user code token saved.", "BaseLmPlugin.Resources.Icons.epic.png")]
    [LicenseManagerPlugin(ConfigurationType = typeof(EpicLicenseManagerSettings), KeyType = typeof(EpicLicenseKey))]
    public class EpicLicenseManager : SteamLicenseManager
    {
        private bool _divertExecution = false;

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
                _divertExecution = false;

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
                        if (e.State == LoginState.LoggingOut)
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
                    context.ExecutionStateChanged += contextStateEventHandler;
                    context.Client.LoginStateChange += userStateEventHandler;

                    var result = EpicLicenseHandler.Initiate(epicInitParameters, context);

                    if (result.InitResult == EpicInitResultCode.Success)
                    {
                        var createdProcess = result.CreatedProcess;
                        if (createdProcess != null)
                        {
                            //since process is created we need to divert the execution
                            _divertExecution = true;
                        }
                    }
                    else if (result.InitResult == EpicInitResultCode.ManualSignInRequired)
                    {
                        //automation could not complete the sign in (e.g. Epic captcha / security check)
                        //the launcher is left running so the user can finish signing in manually;
                        //still divert execution to the launcher instead of launching the game directly
                        if (result.CreatedProcess != null)
                            _divertExecution = true;

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
                    context.ExecutionStateChanged -= contextStateEventHandler;
                    context.Client.LoginStateChange -= userStateEventHandler;
                }

                //detach handlers
                context.ExecutionStateChanged -= OnExecutionStateChanged;

                //attach handlers
                context.ExecutionStateChanged += OnExecutionStateChanged;
            }
        }

        public override void Uninstall(IApplicationLicense license)
        {
            //drop any persisted sign in state so the next user is not signed in with this account
            EpicLicenseHandler.ClearLoginState(null);
        }

        public override IPluginSettings GetSettingsInstance()
        {
            //return new instance of epic settings class
            return new EpicLicenseManagerSettings();
        }

        public override bool DivertExecution(IExecutionContext context)
        {
            //if divert execution was set then we will diver once
            if (_divertExecution)
            {
                //reset the flag after first diversion
                _divertExecution = false;

                //divert execution
                return true;
            }

            //no diversion should happen
            return false;
        }  
    } 
}
