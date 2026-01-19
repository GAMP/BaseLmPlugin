using GizmoShell;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using WindowsInput;

namespace BaseLmPlugin
{
    public sealed class RiotLogin
    {
        public static bool InputLogin(int processId, string username, string password)
        {
            if (Monitor.TryEnter(_lock))
            {
                for (int tries = 0; tries < 3; tries++)
                {
                    try
                    {
                        if (!WaitForWindowCreated(riotClientWindowNames, 10000, out var processes, false))
                        {
                            Debug.WriteLine("Riot client window was not found after 10 seconds of wait time.");
                            continue;
                        }

                        var windowHandle = processes.Select(process =>
                        {
                            try
                            {
                                var handle = process.MainWindowHandle;
                                return handle;
                            }
                            catch
                            {
                                return IntPtr.Zero;
                            }
                        }).FirstOrDefault();

                        if (windowHandle == IntPtr.Zero)
                        {
                            Debug.WriteLine("Could not obtain main window handle.");
                            continue;
                        }

                        try
                        {
#if RELEASE
                            //block user input
                            Win32API.Modules.User32.BlockInput(true);
#endif
                            WindowInfo windowInfo = new(windowHandle);

                            windowInfo.Activate();
                            Thread.Sleep(5000);
                            KeyboardSimulator keyboard = new();
                            MouseSimulator mouse = new();

                            var x = windowInfo.Location.X + 72;
                            var y = windowInfo.Location.Y + 282;

                            System.Windows.Forms.Cursor.Position = new(x, y);

                            //this should move the cursor to username input field
                            mouse.LeftButtonClick();

                            Thread.Sleep(1000);

                            keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                            keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);

                            keyboard.TextEntry(username);
                            keyboard.KeyDown(WindowsInput.Native.VirtualKeyCode.TAB);

                            Thread.Sleep(1000);

                            keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                            keyboard.KeyUp(WindowsInput.Native.VirtualKeyCode.BACK);

                            keyboard.TextEntry(password);
                            keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                            return true;
                        }
                        catch
                        {
                            throw;
                        }
                        finally
                        {
#if RELEASE
                            //block user input
                            Win32API.Modules.User32.BlockInput(false);
#endif
                        }

                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                    finally
                    {
                        Monitor.Exit(_lock);
                    }
                }
            }

            return false;
        }

        private readonly static object _lock = new();
        private static readonly string[] riotClientWindowNames = ["Riot Client Main", "Riot Client"];

        private static bool WaitForWindowCreated(IEnumerable<string> windowTitles, int timeOut, out IEnumerable<Process> foundProcesses, bool throwOnErrors = false)
        {
            windowTitles ??= [];

            //wait period
            int wait_period = 100;

            //create time span
            TimeSpan waitSpan = TimeSpan.FromMilliseconds(timeOut);

            foundProcesses = [];

            #region Wait
            //wait until span expires
            while (waitSpan.TotalMilliseconds > 0)
            {
                try
                {
                    //get matching processes
                    foundProcesses = Process.GetProcesses().Where(process => windowTitles.Any(windowTitle => string.Compare(process.MainWindowTitle, windowTitle, StringComparison.OrdinalIgnoreCase) == 0));

                    //check if process with specified title exists
                    if (foundProcesses.Any())
                        return true;

                    //sleep for wait period
                    System.Threading.Thread.Sleep(wait_period);

                    //remove passed period from total wait span
                    waitSpan = waitSpan.Subtract(TimeSpan.FromMilliseconds(wait_period));
                }
                catch
                {
                    //throw error
                    if (throwOnErrors) { throw; }
                    //error?
                    break;
                }
            }

            //timespan expired
            return false;
            #endregion
        }

        public static bool IsRiotProcess(string executableName)
        {
            return !string.IsNullOrEmpty(executableName) &&
            string.Compare(executableName, "RiotClientServices.exe", StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(executableName, "RiotClientUxRender.exe", StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(executableName, "RiotClientUx.exe", StringComparison.OrdinalIgnoreCase) == 0 ||
            string.Compare(executableName, "RiotClient.exe", StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static readonly string[] riotProcessNames = ["RiotClientServices", "RiotClientUxRender", "RiotClientUx", "RiotClient"];

        public static void TerminateRiotProcesses()
        {
            foreach (var processName in riotProcessNames)
            {
                try
                {
                    var processes = Process.GetProcessesByName(processName);

                    foreach (var item in processes)
                    {
                        try
                        {
                            item.Kill();
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }
}
