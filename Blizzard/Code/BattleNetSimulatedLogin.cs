using CoreLib.Imaging;
using GizmoShell;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsInput;

namespace BaseLmPlugin
{
    public static class BattleNetSimulatedLogin
    {
        #region FUNCTIONS

        public static void Login(int targetProcessId, string username, string password)
        {
            //get child process
            var targetProcess = Process.GetProcessById(targetProcessId);

            //create main window handle
            IntPtr mainWindowHandle = IntPtr.Zero;

            //try to obtain child window handle
            for (int i = 0; i <= 100; i++)
            {
                var windowHandles = WindowEnumerator.ListVisibleHandles(targetProcessId);
                if (windowHandles.Any())
                {
                    mainWindowHandle = windowHandles.FirstOrDefault();
                    break;
                }

                Thread.Sleep(300);
            }

            //check if we obtained child window handle
            if (mainWindowHandle == IntPtr.Zero)
                throw new ArgumentException("Could not obtain main window handle.", nameof(mainWindowHandle));

            //get the main window instance
            WindowInfo info = new(targetProcess.MainWindowHandle);

            #region MAIN WINDOW
            
            //reactivate
            info.BringToFront();
            info.Activate();

            //the color of the first pixel in the Battlenet internal window
            var fieldColor = Color.FromArgb(255, 54, 56, 62);

            //wait for the target pixel to be created
            var pixel = WaitForPixelAsync(info.Handle, fieldColor, null, null, 50, 100)
                .GetAwaiter()
                .GetResult();

            //check if pixel is found
            if (pixel == null)
            {
                //add manual wait period
                Thread.Sleep(300);
            } 

            #endregion

            //reactivate
            info.BringToFront();
            info.Activate();

            //the color of the first pixel in the Battlenet login buton
            fieldColor = Color.FromArgb(255, 24, 74, 102);

            //wait for the target pixel to be created
            pixel = WaitForPixelAsync(info.Handle, fieldColor, 30, 343, 60, 100)
                .GetAwaiter()
                .GetResult();
   
            //check if pixel is found
            if (pixel == null)
            {
                //since some times the pixel cant be detected we can wait for fixed time and then proceed with input normally
                Thread.Sleep(3000);
            }                

            //create input simulators
            KeyboardSimulator sim = new();
            MouseSimulator msim = new();

            var x = info.Location.X + 180;
            var y = info.Location.Y + 225;
            System.Windows.Forms.Cursor.Position = new Point(x, y);

            msim.LeftButtonClick();

            #region REMEMBER ME

            //add small delay after click
            Thread.Sleep(250);

            fieldColor = Color.FromArgb(255, 16, 41, 67);

            //wait for the target pixel to be created
            pixel = GetPixelAtLocation(mainWindowHandle, 32, 307);

            if (pixel != null && pixel.Color == fieldColor)
            {
                System.Windows.Forms.Cursor.Position = new Point(info.Location.X + 32, info.Location.Y + 307);
                msim.LeftButtonClick();
            } 

            #endregion

            //clear username filed
            sim.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);

            //add small delay after click
            Thread.Sleep(250);

            //send back to clear any possible typed value
            sim.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);

            //add small delay after click
            Thread.Sleep(250);

            //set username
            sim.TextEntry(username);

            //add small delay after click
            Thread.Sleep(250);

            //press tab so we can cycle away from the username input
            sim.KeyPress(WindowsInput.Native.VirtualKeyCode.TAB);

            //reactivate
            info.BringToFront();
            info.Activate();

            x = info.Location.X + info.Width - 150;
            y = info.Location.Y + 270;
            System.Windows.Forms.Cursor.Position = new Point(x, y);

            msim.LeftButtonClick();

            //add small delay after click
            Thread.Sleep(250);

            //clear password filed
            sim.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);

            //add small delay after click
            Thread.Sleep(250);

            //send back to clear any possible typed value
            sim.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);

            //set password
            sim.TextEntry(password);       

            //proceed with login
            sim.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);
        }

        private static async Task<Pixel> WaitForPixelAsync(IntPtr windowHandle, Color color, int? x = default, int? y = null, int retries = 100, int delay = 250, CancellationToken ct = default)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            for (int i = 1; i <= retries; i++)
            {
                //get the main window instance
                WindowInfo info = new(windowHandle);

                if (info.IsMinimized)
                    info.Restore();

                info.BringToFront();

                //create window rect
                Rectangle rect = new(info.Location.X, info.Location.Y, info.Width, info.Height);
                
                //create bitmap image based on window size
                var screenImage = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                //copy image from screen
                using (Graphics g = Graphics.FromImage(screenImage))
                {
                    g.CopyFromScreen(rect.Left, rect.Top, 0, 0, info.Size, CopyPixelOperation.SourceCopy);
                }


                using (ImageTraverser traverser = new(screenImage))
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
                }

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            return null;
        }

        private static Pixel GetPixelAtLocation(IntPtr windowHandle,int x,int y)
        {
            //get the main window instance
            WindowInfo info = new(windowHandle);

            if (info.IsMinimized)
                info.Restore();

            info.BringToFront();

            //create window rect
            Rectangle rect = new(info.Location.X, info.Location.Y, info.Width, info.Height);

            //create bitmap image based on window size
            var screenImage = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            //copy image from screen
            using (Graphics g = Graphics.FromImage(screenImage))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, info.Size, CopyPixelOperation.SourceCopy);
            }

            using (ImageTraverser traverser = new(screenImage))
            {
                var query = traverser
                    .Where(pixel => pixel.Location.X == x)
                    .Where(pixel => pixel.Location.Y == y);

                return query.FirstOrDefault();
            }
        }

        #endregion
    }
}
