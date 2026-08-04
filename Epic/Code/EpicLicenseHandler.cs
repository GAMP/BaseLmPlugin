using Client;
using CoreLib.Diagnostics;
using CoreLib.Imaging;
using GizmoShell;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Win32API.Modules;
using WindowsInput;

namespace BaseLmPlugin
{
    public class EpicLicenseHandler
    {
        #region READ ONLY FIELDS
        private static readonly string APP_DATA_PATH = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        //launcher root directory
        private static readonly string LAUNCHER_SAVED_PATH = Path.Combine(APP_DATA_PATH, "EpicGamesLauncher", "Saved");
        //settings file name
        public static readonly string USER_SETTINGS_FILE_PATH = Path.Combine(LAUNCHER_SAVED_PATH, "Config", "Windows", "GameUserSettings.ini");
        //the launcher writes its settings under either Config\Windows or Config\WindowsEditor depending
        //on the build/version, so every settings operation must consider both locations
        private static readonly string[] USER_SETTINGS_FILE_PATHS =
        [
            USER_SETTINGS_FILE_PATH,
            Path.Combine(LAUNCHER_SAVED_PATH, "Config", "WindowsEditor", "GameUserSettings.ini"),
        ];
        //ini section holding the persisted sign in state; its Enable flag controls whether the
        //launcher restores the previous session instead of showing the login screen
        private const string REMEMBER_ME_SECTION = "RememberMe";
        private const string REMEMBER_ME_ENABLE_KEY = "Enable";
        #endregion

        #region CONSTANTS
        private const string EPIC_PROCESS_NAME = "EpicGamesLauncher";

        //IMPORTANT: the Epic login CARD renders at a FIXED pixel size and simply recenters in the
        //window — it does NOT scale with the window. Verified by capturing the same screen at a small
        //(1044x581) and maximized (2576x1408) window: the input field stayed ~440px wide in both.
        //Thresholds here are therefore fixed pixel values, NOT fractions of the window (an earlier
        //fraction-of-window approach failed at high resolution: width/3 demanded 859px of a field that
        //is only ~440px, so it was never found). Values are floors chosen well clear of the real
        //measurements so they still hold if the card renders somewhat larger under display scaling.

        //Height of the band, measured downward from the topmost cyan pixel, in which cyan is counted to
        //tell the "Continue" button apart from the "Forgot password?" link.
        private const int TOP_CYAN_BAND_HEIGHT = 60;

        //If the top cyan band holds at least this many cyan pixels it is the big "Continue" button (so
        //we are still on the email screen). Measured: button ~21600 px, link ~115 px — both identical
        //across resolutions — so any value between the two works; 5000 sits safely in the gap.
        private const int CONTINUE_BUTTON_CYAN_MIN = 5000;

        //Minimum width of the field-fill run to accept it as the password input box (measured ~440px).
        private const int PASSWORD_FIELD_MIN_WIDTH = 200;

        //Minimum count of non-fill (glyph) pixels inside the field interior to consider the password
        //to have actually landed. An empty field measures zero; a few stray pixels could be a caret /
        //anti-aliasing, so require a clear signal.
        private const int FIELD_NONEMPTY_GLYPH_THRESHOLD = 20;

        //how many times to click the password field + type + verify before giving up. Each attempt
        //re-clicks the field, so a first click that landed before the field was focusable is simply
        //retried rather than failing the whole sign in
        private const int PASSWORD_ENTRY_ATTEMPTS = 3;
        #endregion

        //measured geometry of the password input box within the captured window image
        private sealed class PasswordFieldInfo
        {
            public int CenterX;
            public int CenterY;
            public int Top;
            public int Bottom;
            public int Left;
            public int Right;
        }

        #region FUNCTIONS

        public static EpicInitResult Initiate(EpicInitParameters parameters, IExecutionContext cx)
        {
            if (cx == null)
                throw new ArgumentNullException(nameof(cx));

            try
            {
                //try simulation method
                var createdProcess = StartEpicProcess(parameters.FilePath, parameters.Arguments, parameters.WorkingDirectory, parameters.Username, parameters.Password, cx);

                //check if process have started
                if (createdProcess != null)
                    return new EpicInitResult(createdProcess);
                else
                    throw new Exception("Epic launcher process was not created.");

            }
            catch (EpicManualSignInRequiredException ex)
            {
                //automation aborted (captcha / security check / unexpected screen);
                //the launcher is still running so the user can complete the sign in by hand
                return new EpicInitResult(EpicInitResultCode.ManualSignInRequired, ex.LauncherProcess, ex);
            }
            catch (Exception ex)
            {
                //process starting failed, return error result here
                return new EpicInitResult(ex);
            }
        }

        /// <summary>
        /// Disables the persisted Epic sign in so the launcher presents the login screen on the
        /// next start.
        ///
        /// Without this the launcher restores the previous session and goes straight to the
        /// library — no login screen is ever rendered, the automation below never finds its anchor
        /// pixel, and the sign in appears to fail even though nothing is actually wrong.
        ///
        /// Only the [RememberMe] Enable flag is written; the stored token itself is left in place.
        /// Note this means the credentials remain on disk and the launcher is trusted to honour the
        /// flag.
        ///
        /// NOTE: the launcher must be stopped before calling this, otherwise it rewrites the file
        /// from memory when it exits and the flag is set back to True.
        /// </summary>
        public static void ClearLoginState(IExecutionContext cx)
        {
            foreach (var settingsFilePath in USER_SETTINGS_FILE_PATHS)
            {
                try
                {
                    if (!File.Exists(settingsFilePath))
                        continue;

                    //a key level write is used rather than a section delete: passing a null key name
                    //to delete the whole section returns TRUE but leaves the file untouched, which
                    //would silently report success while the sign in state stayed active
                    if (!NativeMethods.WritePrivateProfileString(REMEMBER_ME_SECTION, REMEMBER_ME_ENABLE_KEY, "False", settingsFilePath))
                        throw new Win32Exception();
                }
                catch (Exception ex)
                {
                    //failing to clear the state is not fatal, the launcher may simply restore the
                    //previous session and require a manual sign in
                    cx?.WriteMessage($"Failed to clear Epic login state. {ex.Message}");
                }
            }
        }

        public static Process StartEpicProcess(string fileName, string arguments, string workingDirectory, string username, string password, IExecutionContext cx)
        {
            try
            {
                //kill all existing epic processes
                Process.GetProcessesByName(EPIC_PROCESS_NAME)
                    .ToList()
                    .ForEach(proc => proc.Kill());
            }
            catch (Exception)
            {
                //we failed but that is ok
            }

            //drop any saved session so the launcher is forced to show the login screen.
            //this must happen after the processes above are killed, otherwise the exiting
            //launcher writes its in memory settings back over the cleared sections
            ClearLoginState(cx);

            int SMALL_DELAY = 1000;
            int LARGE_DELAY = 10000;

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
            {
                //even if we failed to create a new process proceed with the login initiation
            }

            try
            {
#if RELEASE
                //block user input. NOTE: BlockInput(true) exempts the calling thread, so the
                //synthetic mouse injection (SetCursorPos / mouse_event) issued below from this same
                //thread still works — it only blocks a physical user from interfering mid-login.
                User32.BlockInput(true);
#endif

                //wait for child processes to be created
                Thread.Sleep(5000);

                //get first epic process
                Process targetProcess = Process.GetProcessesByName(EPIC_PROCESS_NAME)
                    .Where(p => !p.HasExited)
                    .FirstOrDefault();

                IntPtr windowHandle = IntPtr.Zero;

                //no process found
                if (targetProcess != null)
                {
                    //wait for the process window to be created
                    if (CoreProcess.WaitForWindowCreated(targetProcess.Id, LARGE_DELAY))
                        windowHandle = targetProcess.MainWindowHandle;
                }

                //if we failed to obtain window process try to find it in system
                if (windowHandle == IntPtr.Zero)
                {
                    for (int i = 0; i < 30; i++)
                    {
                        windowHandle = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "UnrealWindow", "Epic Games Launcher");
                        if (windowHandle != IntPtr.Zero)
                            break;

                        Thread.Sleep(SMALL_DELAY);
                    }
                }

                if (windowHandle == IntPtr.Zero)
                    throw new ArgumentException("Epic launcher window not found");

                //get the main window instance
                WindowInfo window = new(windowHandle);

                //check if window is minimized and restore it
                if (window.IsMinimized)
                    User32.ShowWindow(window.Handle, Win32API.Headers.WinUser.Enumerations.SW.SW_RESTORE);
     

                //bring main window to front
                window.BringToFront();

                //wait for Epic web UI to load by finding the cyan "Continue" button
                //this also gives us an anchor point since the form is fixed-size
                var cyanColor = Color.FromArgb(255, 38, 187, 255);
                var continueButtonPixel = WaitForPixel(window.Handle, [cyanColor], null, null, 30, 1000);

                //if the email screen never rendered its cyan anchor we cannot proceed safely;
                //abort cleanly and leave the launcher on-screen for a manual sign in
                if (continueButtonPixel == null)
                    throw new EpicManualSignInRequiredException("Epic sign-in email screen was not detected; manual sign in required.", targetProcess);

                //create simulators
                KeyboardSimulator keyboard = new();
                MouseSimulator mouse = new();

                //bring main window to front
                window.BringToFront();

                //click on the email input field with a real mouse click so the CEF web view focuses it
                //(the field sits ~50px above the cyan "Continue" anchor in the captured image)
                using (var img = Imaging.CaptureWindowImage(window.Handle))
                {
                    int clickX = img.Width / 2;
                    int clickY = continueButtonPixel.Location.Y - 50;
                    SendClickToWindow(window, clickX, clickY);
                }

                //user name
                Thread.Sleep(SMALL_DELAY);
                keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);
                keyboard.TextEntry(username);

                //send enter key to switch to next state (password field)
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                //wait for the email screen to be replaced by the password screen before touching the
                //password field. The two screens are told apart by the SIZE of the topmost cyan block:
                //the email screen's is the big "Continue" button, the password screen's is the tiny
                //"Forgot password?" link. A fixed delay cannot do this — on a slow machine or network
                //the email screen is still up when the delay expires and the password would be typed
                //into the email field.
                //NOTE: a null result here also covers the hCaptcha "One more step" screen (no cyan),
                //in which case we abort without typing the password.
                Log("waiting for password screen (top cyan block must shrink from button to link)");
                var forgotPasswordPixel = WaitForPasswordScreen(window.Handle, [cyanColor], 4, 60, 250);

                //if the password screen never appeared (captcha / verification / unexpected screen)
                //abort cleanly: do NOT type the password into whatever is on screen, and leave the
                //launcher running so a human can complete the sign in manually
                if (forgotPasswordPixel == null)
                {
                    Log("password screen NOT detected (top cyan never moved+settled)");
                    DumpSnapshot(window.Handle, "no_password_screen");
                    throw new EpicManualSignInRequiredException("Epic password screen was not detected (possible security check); manual sign in required.", targetProcess);
                }
                Log($"password screen detected; forgot-password anchor at ({forgotPasswordPixel.Location.X},{forgotPasswordPixel.Location.Y})");

                //bring main window to front
                window.BringToFront();

                //The cyan "Forgot password?" link paints as part of the incoming screen's layout
                //BEFORE the password input field becomes interactive. Clicking on the strength of the
                //link alone lands the click (and the typed password) while the field is not ready, so
                //the password silently goes nowhere. Locate the field box itself and wait until it is
                //actually present before clicking — the field sits just above the anchor.
                var passwordField = WaitForPasswordField(window.Handle, forgotPasswordPixel.Location.Y, 40, 250);

                //if the field never rendered, abort rather than typing the password blindly
                if (passwordField == null)
                {
                    Log("password FIELD not found (no wide field-fill run above the anchor)");
                    DumpSnapshot(window.Handle, "no_password_field");
                    throw new EpicManualSignInRequiredException("Epic password field did not become ready; manual sign in required.", targetProcess);
                }
                Log($"password field found: center=({passwordField.CenterX},{passwordField.CenterY}) X={passwordField.Left}..{passwordField.Right} width={passwordField.Right - passwordField.Left}");

                //type the password, then VERIFY it actually landed in the field, retrying the whole
                //click + type a few times. This is what makes the flow robust to slow paint / focus
                //lag regardless of machine speed: if the field is still empty after typing, the click
                //was too early, so we click again and retry rather than proceeding with an empty field.
                bool passwordEntered = false;
                for (int attempt = 0; attempt < PASSWORD_ENTRY_ATTEMPTS && !passwordEntered; attempt++)
                {
                    window.BringToFront();

                    //click the field's true centre (X = window centre, since the login card is centred;
                    //Y = the detected field centre) with a real mouse click so the CEF view focuses it
                    Log($"attempt {attempt + 1}/{PASSWORD_ENTRY_ATTEMPTS}: clicking field at ({passwordField.CenterX},{passwordField.CenterY})");
                    SendClickToWindow(window, passwordField.CenterX, passwordField.CenterY);

                    Thread.Sleep(SMALL_DELAY);
                    keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                    keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);
                    keyboard.TextEntry(password);

                    //give the field a moment to render the typed characters, then check the field
                    //interior for glyphs — an empty field is a perfectly uniform dark fill, so any
                    //non-fill pixels mean the masked password characters actually landed
                    Thread.Sleep(SMALL_DELAY);
                    int fillGlyphs = CountFieldGlyphs(window.Handle, passwordField);
                    passwordEntered = fillGlyphs > FIELD_NONEMPTY_GLYPH_THRESHOLD;
                    Log($"attempt {attempt + 1}: field glyph pixels={fillGlyphs} (threshold {FIELD_NONEMPTY_GLYPH_THRESHOLD}) -> entered={passwordEntered}");
                    if (!passwordEntered)
                        DumpSnapshot(window.Handle, $"empty_after_type_attempt{attempt + 1}");
                }

                //if after all attempts the field is still empty, the CEF view never took the input;
                //do not press enter on an empty password, leave the launcher for a manual sign in
                if (!passwordEntered)
                    throw new EpicManualSignInRequiredException("Epic password could not be entered into the field; manual sign in required.", targetProcess);

                //re-focus the field before submitting. IsFieldNonEmpty captured the window to verify
                //the text landed, and the enter below fires outside the type loop, so the field may no
                //longer hold keyboard focus. Re-click it (this does not clear the already-typed value)
                //so the enter is delivered to the field the same way a manual keypress would be. A
                //manual enter with the field focused submits reliably; this reproduces that focus.
                window.BringToFront();
                SendClickToWindow(window, passwordField.CenterX, passwordField.CenterY);
                Thread.Sleep(SMALL_DELAY);

                //send enter key to initiate login
                Log("password entered and verified; submitting with ENTER");
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                return targetProcess;
            }
            catch
            {
                throw;
            }
            finally
            {
                //always unblock input (no-op if it was never blocked); pairs with the RELEASE
                //BlockInput(true) above and guards against leaving input blocked on any exit path
                User32.BlockInput(false);
            }
        }

        private static Pixel WaitForPixel(IntPtr windowHandle, Color color, int? x = default, int? y = null, int retries = 100, int delay = 250)
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

                    Thread.Sleep(delay);
                }
            }

            return null;
        }

        private static Pixel WaitForPixel(IntPtr windowHandle, Color[] color, int? x = default, int? y = null, int retries = 100, int delay = 250)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            for (int i = 1; i <= retries; i++)
            {
                using (var screenImage = Imaging.CaptureWindowImage(windowHandle))
                {
                    using (ImageTraverser traverser = new ImageTraverser(screenImage))
                    {
                        var query = traverser
                            .Where(e => color.Contains(e.Color));

                        if (x.HasValue)
                            query = query.Where(pixel => pixel.Location.X == x);

                        if (y.HasValue)
                            query = query.Where(pixel => pixel.Location.Y == y);

                        var foundPixel = query.FirstOrDefault();

                        if (foundPixel != null)
                            return foundPixel;

                        Thread.Sleep(delay);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Waits for the login screen to change from the EMAIL screen to the PASSWORD screen and
        /// returns the topmost cyan pixel of the password screen once it has settled, or null on
        /// timeout.
        ///
        /// The reliable, resolution-independent discriminator between the two screens is the SIZE of
        /// the topmost cyan block, not its position:
        ///   - email screen: the topmost cyan is the big solid "Continue" BUTTON — a huge block
        ///     (measured ~21600 cyan px in the 60px band below its top, identical at 1044x581 and
        ///     2576x1408).
        ///   - password screen: the topmost cyan is the thin "Forgot password?" LINK — a tiny block
        ///     (measured ~115 px, also identical across resolutions).
        /// An earlier approach keyed on how far the top cyan MOVED, but that distance is not constant
        /// (~64px in a small window vs ~306px maximized, because the card is positioned differently),
        /// so it failed at small sizes. The block size differs ~190x between the screens and does not
        /// change with resolution, which makes it a clean signal.
        ///
        /// We accept a frame once the topmost cyan block has shrunk below the button threshold (so we
        /// are on the password screen, not the email screen) AND its position has held still for
        /// <paramref name="stableSamples"/> reads (so the transition animation has finished and the
        /// field exists to click). Null return => screen never changed/settled (captcha / unexpected
        /// screen); the caller aborts to a manual sign in rather than typing the password blindly.
        /// </summary>
        private static Pixel WaitForPasswordScreen(IntPtr windowHandle, Color[] color, int stableSamples = 4, int retries = 60, int delay = 250)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            Pixel lastPixel = null;
            int stableCount = 0;

            for (int i = 1; i <= retries; i++)
            {
                Pixel topPixel;
                int topBandCyan;
                using (var screenImage = Imaging.CaptureWindowImage(windowHandle))
                using (ImageTraverser traverser = new ImageTraverser(screenImage))
                {
                    topPixel = traverser
                        .Where(e => color.Contains(e.Color))
                        .FirstOrDefault();

                    //count cyan within the band just below the topmost cyan pixel — huge for the
                    //Continue button, tiny for the "Forgot password?" link
                    topBandCyan = 0;
                    if (topPixel != null)
                    {
                        int bandBottom = Math.Min(topPixel.Location.Y + TOP_CYAN_BAND_HEIGHT, screenImage.Height);
                        foreach (var e in traverser)
                        {
                            if (e.Location.Y >= topPixel.Location.Y && e.Location.Y < bandBottom && color.Contains(e.Color))
                                topBandCyan++;
                        }
                    }
                }

                //on the password screen the top cyan block is the small link, well under the button size
                bool onPasswordScreen = topPixel != null && topBandCyan < CONTINUE_BUTTON_CYAN_MIN;

                Log($"WaitForPasswordScreen try {i}/{retries}: topCyanY={(topPixel != null ? topPixel.Location.Y.ToString() : "none")} topBandCyan={topBandCyan} onPasswordScreen={onPasswordScreen} stableCount={stableCount}");

                if (onPasswordScreen && lastPixel != null && topPixel.Location.Y == lastPixel.Location.Y)
                {
                    stableCount++;
                    if (stableCount >= stableSamples)
                        return topPixel;
                }
                else
                {
                    //still the email screen, or on the password screen but still animating — (re)start
                    //the stability count only once we are actually on the password screen
                    stableCount = onPasswordScreen ? 1 : 0;
                }

                lastPixel = onPasswordScreen ? topPixel : null;
                Thread.Sleep(delay);
            }

            //timed out without the screen changing and settling. Return null: acting now would click
            //the still-visible email screen (or a mid transition frame) and type the password into
            //the wrong field. Callers treat null as "abort and ask for a manual sign in".
            return null;
        }

        /// <summary>
        /// True if a pixel matches the password field's empty-fill colour — a flat, slightly-lighter
        /// than the card dark grey where the red and green channels are close (neutral grey). Measured
        /// signature: R,G in [33,62], B in [37,74], |R-G| small. The card background, text and the
        /// cyan link all fall outside this, so a long horizontal run of it marks the input box.
        /// </summary>
        private static bool IsFieldFill(byte r, byte g, byte b)
        {
            return r >= 33 && r <= 62
                && g >= 33 && g <= 62
                && b >= 37 && b <= 74
                && Math.Abs(r - g) <= 12;
        }

        /// <summary>
        /// Finds the password input box and waits until it has actually rendered, returning its
        /// measured centre and horizontal extent, or null on timeout.
        ///
        /// The box is located as the widest horizontal run of the field-fill colour on the rows just
        /// above the "Forgot password?" anchor (the field sits directly above the link). Detecting the
        /// field itself — rather than trusting the cyan link — is what guarantees we only click once
        /// the thing we are about to type into actually exists. Deriving the geometry from the capture
        /// keeps it correct across resolutions instead of relying on fixed pixel offsets.
        /// </summary>
        private static PasswordFieldInfo WaitForPasswordField(IntPtr windowHandle, int anchorY, int retries = 40, int delay = 250)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            for (int i = 1; i <= retries; i++)
            {
                using (var img = Imaging.CaptureWindowImage(windowHandle))
                {
                    //the field spans a band roughly 30-70px above the anchor; scan that band for the
                    //widest field-fill run and take the row whose run is widest as the field centre
                    int scanTop = Math.Max(0, anchorY - 80);
                    int scanBottom = Math.Max(0, anchorY - 15);

                    int bestRun = 0, bestLeft = 0, bestRight = 0, bestY = 0;

                    for (int y = scanTop; y < scanBottom && y < img.Height; y++)
                    {
                        int run = 0, runStart = 0, curLeft = 0, curRight = 0, curBest = 0;
                        for (int x = 0; x < img.Width; x++)
                        {
                            var c = img.GetPixel(x, y);
                            if (IsFieldFill(c.R, c.G, c.B))
                            {
                                if (run == 0)
                                    runStart = x;
                                run++;
                                if (run > curBest)
                                {
                                    curBest = run;
                                    curLeft = runStart;
                                    curRight = x;
                                }
                            }
                            else
                            {
                                run = 0;
                            }
                        }

                        if (curBest > bestRun)
                        {
                            bestRun = curBest;
                            bestLeft = curLeft;
                            bestRight = curRight;
                            bestY = y;
                        }
                    }

                    //log what the widest run was this iteration so a failure shows whether the field
                    //was simply never found vs found-but-too-narrow vs a colour-match problem
                    Log($"WaitForPasswordField try {i}/{retries}: img={img.Width}x{img.Height} scan Y={scanTop}..{scanBottom} bestRun={bestRun} (floor {PASSWORD_FIELD_MIN_WIDTH}) at Y={bestY} X={bestLeft}..{bestRight}");

                    //require an absolute minimum run width to count as the field box. This is a FIXED
                    //pixel floor, not a fraction of the window: the card does not scale with the
                    //window, so the field stays ~440px wide whether the window is small or maximized
                    if (bestRun >= PASSWORD_FIELD_MIN_WIDTH)
                    {
                        //bestY is merely the widest field-fill row, which tends to land near the top of
                        //the box (text-free rows are widest). The typed characters render at the box's
                        //vertical CENTRE, so we must find the field's true top and bottom to sample the
                        //right rows later. Walk up and down from bestY at a column just inside the left
                        //edge (clear of the centred text) until the fill colour ends.
                        int probeX = bestLeft + 20;
                        int top = bestY, bottom = bestY;
                        while (top - 1 >= 0 && IsFieldFill(img.GetPixel(probeX, top - 1).R, img.GetPixel(probeX, top - 1).G, img.GetPixel(probeX, top - 1).B))
                            top--;
                        while (bottom + 1 < img.Height && IsFieldFill(img.GetPixel(probeX, bottom + 1).R, img.GetPixel(probeX, bottom + 1).G, img.GetPixel(probeX, bottom + 1).B))
                            bottom++;

                        int centerY = (top + bottom) / 2;
                        Log($"password field vertical extent Y={top}..{bottom} -> centerY={centerY}");

                        return new PasswordFieldInfo
                        {
                            //X = window centre: the card (and therefore the field) is horizontally
                            //centred, which holds at any resolution and avoids keying on the field's
                            //own edges (which include the eye icon on the right)
                            CenterX = img.Width / 2,
                            CenterY = centerY,
                            Top = top,
                            Bottom = bottom,
                            Left = bestLeft,
                            Right = bestRight,
                        };
                    }
                }

                Thread.Sleep(delay);
            }

            return null;
        }

        /// <summary>
        /// Counts the "glyph" pixels inside the password field — pixels that are NOT the field's
        /// uniform empty fill. An empty field measures zero such pixels across its interior, so a
        /// meaningful count means the masked password characters have actually landed. The interior
        /// is sampled left-of-centre to avoid the eye (show password) icon on the field's right edge.
        /// Returned as a count (rather than a bool) so the caller can log the actual value.
        /// </summary>
        private static int CountFieldGlyphs(IntPtr windowHandle, PasswordFieldInfo field)
        {
            using (var img = Imaging.CaptureWindowImage(windowHandle))
            {
                //sample horizontally from just inside the left edge to just left of centre — where the
                //first typed characters appear, clear of the eye (show password) icon on the right
                int left = field.Left + 10;
                int right = field.CenterX - 10;
                if (right <= left)
                    return 0;

                //sample a band around the field's true vertical centre, but kept a few pixels INSIDE
                //the field's top and bottom so it never touches the field border or the card panel
                //above/below (those are non-fill too and would false-positive on an empty field)
                int bandTop = Math.Max(field.Top + 4, field.CenterY - 8);
                int bandBottom = Math.Min(field.Bottom - 4, field.CenterY + 8);

                int nonFill = 0;
                for (int y = bandTop; y <= bandBottom; y++)
                {
                    if (y < 0 || y >= img.Height)
                        continue;

                    for (int x = left; x < right && x < img.Width; x++)
                    {
                        var c = img.GetPixel(x, y);
                        if (!IsFieldFill(c.R, c.G, c.B))
                            nonFill++;
                    }
                }

                return nonFill;
            }
        }

        /// <summary>
        /// Writes a diagnostic line for the Epic login automation. Routed through Trace so it lands in
        /// the standard client trace log (matching the other license plugins).
        /// </summary>
        private static void Log(string message)
        {
            Trace.WriteLine($"Epic login: {message}");
        }

        /// <summary>
        /// Saves the current window capture to the temp folder for post-mortem diagnosis of a failed
        /// automation, and logs its path. Best-effort: never throws.
        /// </summary>
        private static void DumpSnapshot(IntPtr windowHandle, string reason)
        {
            try
            {
                using (var bmp = Imaging.CaptureWindowImage(windowHandle))
                {
                    string path = Path.Combine(Path.GetTempPath(), $"epic_login_{reason}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Log($"snapshot saved: {path}");
                }
            }
            catch (Exception ex)
            {
                Log($"failed to save snapshot ({reason}): {ex.Message}");
            }
        }

        /// <summary>
        /// Performs a real hardware-level left click at the given window-relative coordinates.
        /// The coordinates are relative to <see cref="Imaging.CaptureWindowImage(IntPtr)"/> output,
        /// which is captured from the window DC (origin = window top-left, includes borders), so we
        /// convert to screen coordinates by adding the window rectangle origin.
        ///
        /// A synthetic WM_LBUTTONDOWN/WM_LBUTTONUP (SendMessage) is NOT used here: Epic's login UI is
        /// a Chromium/CEF web view, and a posted click does not set keyboard focus on its input
        /// fields, so any text typed afterwards is silently dropped. Injecting a genuine mouse click
        /// via SetCursorPos + mouse_event focuses the field the same way a physical click would.
        ///
        /// NOTE: this still works while User32.BlockInput(true) is in effect because BlockInput
        /// exempts the calling thread — the injection below runs on that same thread.
        /// </summary>
        private static void SendClickToWindow(WindowInfo window, int windowRelativeX, int windowRelativeY)
        {
            //map window-relative coordinates to absolute screen coordinates
            var windowRect = window.Rectangle;
            int screenX = windowRect.Left + windowRelativeX;
            int screenY = windowRect.Top + windowRelativeY;

            //move the cursor and inject a real left click
            NativeMethods.SetCursorPos(screenX, screenY);
            Thread.Sleep(60);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
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

    #region EPICINITRESULTCODE
    /// <summary>
    /// Init function result codes.
    /// </summary>
    public enum EpicInitResultCode
    {
        Success = 0,
        Failure = 1,
        Canceled = 2,
        //automation could not complete (e.g. Epic captcha / security check);
        //the launcher is left running for the user to sign in manually
        ManualSignInRequired = 3,
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

        public EpicInitResult(EpicInitResultCode resultCode, Process process, Exception exception) : this(resultCode)
        {
            //process and exception are optional for this overload (used by the manual sign-in path)
            CreatedProcess = process;
            Exception = exception;
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

    /// <summary>
    /// Thrown when the Epic sign-in cannot be completed automatically and requires the user to
    /// finish signing in by hand (for example when Epic presents its "One more step" captcha /
    /// security check, or the expected login screen is not detected). The launcher is left running.
    /// </summary>
    public class EpicManualSignInRequiredException : Exception
    {
        public EpicManualSignInRequiredException(string message, Process launcherProcess = null) : base(message)
        {
            LauncherProcess = launcherProcess;
        }

        /// <summary>
        /// Gets the running Epic launcher process, if one was created before the automation aborted.
        /// The launcher is intentionally left running so the user can finish signing in manually.
        /// </summary>
        public Process LauncherProcess
        {
            get;
        }
    }

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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos([In] int X, [In] int Y);

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    }

    #endregion
}
