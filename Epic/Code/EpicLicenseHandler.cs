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
using System.Text;
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

        //Minimum vertical distance the input box must move to count as a real screen change. The box
        //does not drift while stationary, and the real email -> password move is ~65px at its smallest
        //(small window; ~307px maximized), so a small floor is enough to ignore any 1-2px jitter.
        private const int INPUT_FIELD_MOVED_MIN_SHIFT = 20;

        //Empty input box fill colour, measured from live captures at both window sizes. Kept tight on
        //purpose — see IsFieldFill for why a loose range breaks the whole-window scan.
        private const int FIELD_FILL_R = 36;
        private const int FIELD_FILL_G = 36;
        private const int FIELD_FILL_B = 40;
        private const int FIELD_FILL_TOLERANCE = 6;

        //Minimum width of the field-fill run to accept it as the password input box (measured ~440px).
        private const int PASSWORD_FIELD_MIN_WIDTH = 200;

        //A launcher window must be at least this wide and tall to be the real UI window; the launcher
        //also creates 0x0 placeholder windows that would otherwise be accepted (see HasUsableSize).
        private const int MIN_USABLE_WINDOW_SIZE = 200;

        //how long to wait for an existing launcher process to actually exit after being killed
        private const int PROCESS_EXIT_TIMEOUT = 3000;

        //Solid cyan primary button ("Continue" / "Sign in"). The tolerance is deliberately looser than
        //the input box one: this is a large saturated fill whose exact value shifts slightly with the
        //GPU, colour profile and scaling filter, and being strict would fail on other machines.
        private const int BUTTON_FILL_R = 38;
        private const int BUTTON_FILL_G = 187;
        private const int BUTTON_FILL_B = 255;
        private const int BUTTON_FILL_TOLERANCE = 12;

        //a cyan run must be at least this wide and tall to be the button rather than a text link
        private const int BUTTON_MIN_WIDTH = 200;
        private const int BUTTON_MIN_HEIGHT = 28;

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
            public int Width;
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
            //Opt this process into per-monitor physical pixels BEFORE anything measures a window or
            //moves the cursor. Without it Windows virtualises coordinates for a non DPI aware process
            //on a scaled display (125%, 150%, ...): GetWindowRect reports logical coordinates while the
            //captured image and SetCursorPos work in physical pixels, so every pixel offset we compute
            //is wrong by the scale factor and the clicks land outside the fields. This is the most
            //likely reason the automation worked on a 100% scaled machine but failed elsewhere.
            EnsureDpiAware();

            try
            {
                //kill all existing epic processes, waiting for each to actually go away — a launcher
                //that is still shutting down can rewrite the settings we clear below, and can also hold
                //the window we are about to search for
                foreach (var existingProcess in Process.GetProcessesByName(EPIC_PROCESS_NAME))
                {
                    try
                    {
                        existingProcess.Kill();
                        existingProcess.WaitForExit(PROCESS_EXIT_TIMEOUT);
                    }
                    catch (Exception)
                    {
                        //we failed but that is ok
                    }
                }
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

                //wait for the process to create its windows before looking for the UI window
                if (targetProcess != null)
                    CoreProcess.WaitForWindowCreated(targetProcess.Id, LARGE_DELAY);

                //Find the real UI window. This must be validated by SIZE, not merely by being a
                //non-zero handle: the launcher creates several 0x0 "UnrealWindow" placeholders and its
                //Process.MainWindowHandle often points at one of them. A 0x0 handle is not IntPtr.Zero,
                //so taking MainWindowHandle unchecked yields a window that captures as an empty image
                //and every pixel stage below then fails for no visible reason.
                IntPtr windowHandle = IntPtr.Zero;

                for (int i = 0; i < 30; i++)
                {
                    //prefer the titled launcher window, which is the one hosting the web UI
                    var candidate = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "UnrealWindow", "Epic Games Launcher");

                    //the exact title is not guaranteed (it varies by launcher build and locale), so
                    //fall back to scanning visible windows for an Epic one
                    if (!HasUsableSize(candidate))
                        candidate = FindEpicWindowByScan();

                    //last resort: the process main window, which may still be a 0x0 placeholder
                    if (!HasUsableSize(candidate) && targetProcess != null)
                    {
                        targetProcess.Refresh();
                        candidate = targetProcess.MainWindowHandle;
                    }

                    if (HasUsableSize(candidate))
                    {
                        windowHandle = candidate;
                        break;
                    }

                    Thread.Sleep(SMALL_DELAY);
                }

                if (windowHandle == IntPtr.Zero)
                    throw new ArgumentException("Epic launcher window not found");

                Log($"using launcher window handle {windowHandle}");

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

                //locate the email input box itself and click its centre with a real mouse click so the
                //CEF web view focuses it. Its position is also remembered so we can later tell that the
                //screen has changed (the password box renders at a different height).
                PasswordFieldInfo emailField;
                using (var img = Imaging.CaptureWindowImage(window.Handle))
                    emailField = FindInputField(img);

                if (emailField == null)
                {
                    Log("email input field not found");
                    DumpSnapshot(window.Handle, "no_email_field");
                    throw new EpicManualSignInRequiredException("Epic sign-in email field was not detected; manual sign in required.", targetProcess);
                }

                Log($"email field found: center=({emailField.CenterX},{emailField.CenterY}) X={emailField.Left}..{emailField.Right} width={emailField.Width}");
                SendClickToWindow(window, emailField.CenterX, emailField.CenterY);

                //user name
                Thread.Sleep(SMALL_DELAY);
                keyboard.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.CONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);
                keyboard.TextEntry(username);

                //send enter key to switch to next state (password field)
                keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                //wait for the email screen to be replaced by the password screen before typing. The
                //signal is the INPUT BOX moving to a new vertical position and settling there — the
                //email box and the password box render at clearly different heights. A fixed delay
                //cannot do this: on a slow machine or network the email screen is still up when the
                //delay expires and the password would be typed into the email field.
                //NOTE: a null result here also covers the hCaptcha "One more step" screen, which has no
                //input box of this kind — in that case we abort without typing the password.
                Log($"waiting for password screen (input box must move away from Y={emailField.CenterY})");
                var passwordField = WaitForInputFieldMoved(window.Handle, emailField.CenterY, 4, 60, 250);

                //if the screen never changed to a settled password box, abort rather than typing blindly
                if (passwordField == null)
                {
                    Log("password screen/field not detected (input box never moved and settled)");
                    DumpSnapshot(window.Handle, "no_password_field");
                    throw new EpicManualSignInRequiredException("Epic password field did not become ready; manual sign in required.", targetProcess);
                }
                Log($"password field found: center=({passwordField.CenterX},{passwordField.CenterY}) Y={passwordField.Top}..{passwordField.Bottom} X={passwordField.Left}..{passwordField.Right} width={passwordField.Width}");

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

                window.BringToFront();

                //Submit by clicking the "Sign in" button when we can find it. The button is greyed out
                //until the field holds a password and turns solid cyan once it does, so a cyan button
                //on this screen is both proof the form accepted the input and a click target that does
                //not depend on where keyboard focus currently sits.
                var signInButton = FindCyanButton(window.Handle);

                if (signInButton != null)
                {
                    Log($"submitting by clicking sign-in button at ({signInButton.CenterX},{signInButton.CenterY})");
                    SendClickToWindow(window, signInButton.CenterX, signInButton.CenterY);
                }
                else
                {
                    //fall back to ENTER. Re-click the field first: the verification capture happens
                    //between typing and here, so the field may no longer hold keyboard focus, and a
                    //re-click restores it without clearing the already typed value.
                    Log("sign-in button not found; falling back to ENTER");
                    SendClickToWindow(window, passwordField.CenterX, passwordField.CenterY);
                    Thread.Sleep(SMALL_DELAY);
                    keyboard.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);
                }

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
        /// True if a pixel matches the input box's empty-fill colour. Measured signature is a tight
        /// R36 G36 B40, allowed a few units of tolerance.
        ///
        /// The tolerance must stay TIGHT: the card also contains other flat greys — notably an R48
        /// G48 B51 decoration that forms 520px wide runs, WIDER than the ~460px input box. A looser
        /// range (an earlier version allowed R/G up to 62) matched those too, so a whole-window scan
        /// for the widest run locked onto the decoration instead of the field.
        /// </summary>
        private static bool IsFieldFill(byte r, byte g, byte b)
        {
            return Math.Abs(r - FIELD_FILL_R) <= FIELD_FILL_TOLERANCE
                && Math.Abs(g - FIELD_FILL_G) <= FIELD_FILL_TOLERANCE
                && Math.Abs(b - FIELD_FILL_B) <= FIELD_FILL_TOLERANCE;
        }

        /// <summary>
        /// Finds the login card's text input box in a single capture, or null if none is present.
        ///
        /// The box is the widest horizontal run of the field-fill colour anywhere in the window. Both
        /// login screens draw an identical ~440px wide box (the email box and the password box), so
        /// this locates whichever one is currently on screen; callers tell them apart by position.
        /// Detecting the FIELD — rather than a nearby cyan link — is what guarantees we only click
        /// once the thing we are about to type into actually exists.
        /// </summary>
        private static PasswordFieldInfo FindInputField(Bitmap img)
        {
            int bestRun = 0, bestLeft = 0, bestRight = 0, bestY = 0;

            for (int y = 0; y < img.Height; y++)
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

            //require an absolute minimum run width to count as the input box. This is a FIXED pixel
            //floor, not a fraction of the window: the card does not scale with the window, so the box
            //stays ~440px wide whether the window is small or maximized
            if (bestRun < PASSWORD_FIELD_MIN_WIDTH)
                return null;

            //bestY is merely the widest field-fill row, which tends to land near the top of the box
            //(text-free rows are widest). Typed characters render at the box's vertical CENTRE, so we
            //must find its true top and bottom. Walk up and down from bestY at a column just inside
            //the left edge (clear of the centred text) until the fill colour ends.
            int probeX = bestLeft + 20;
            int top = bestY, bottom = bestY;
            while (top - 1 >= 0 && IsFieldFill(img.GetPixel(probeX, top - 1).R, img.GetPixel(probeX, top - 1).G, img.GetPixel(probeX, top - 1).B))
                top--;
            while (bottom + 1 < img.Height && IsFieldFill(img.GetPixel(probeX, bottom + 1).R, img.GetPixel(probeX, bottom + 1).G, img.GetPixel(probeX, bottom + 1).B))
                bottom++;

            return new PasswordFieldInfo
            {
                //X = window centre: the card (and therefore the field) is horizontally centred, which
                //holds at any resolution and avoids keying on the field's own edges (which include the
                //eye icon on the right)
                CenterX = img.Width / 2,
                CenterY = (top + bottom) / 2,
                Top = top,
                Bottom = bottom,
                Left = bestLeft,
                Right = bestRight,
                Width = bestRun,
            };
        }

        /// <summary>
        /// Waits for the login card's input box to MOVE away from <paramref name="fromCenterY"/> and
        /// settle, and returns the new box — i.e. waits for the email screen to be replaced by the
        /// password screen. Returns null on timeout.
        ///
        /// Tracking the input box is what makes this reliable. Earlier attempts keyed on cyan: first
        /// on how far the topmost cyan moved (that distance is not constant — ~65px in a small window
        /// vs ~307px maximized), then on the size of the topmost cyan block (the email screen's
        /// "Continue" button is huge, the password screen's "Forgot password?" link is small). The
        /// block-size check still misfired, because mid-transition the Continue button disappears
        /// while the email screen's OTHER small cyan link ("Create an account") is still painted —
        /// same size as the password link, so it read as "password screen" while the email screen was
        /// still up. The input box has no such ambiguity: there is exactly one, it is the element we
        /// are about to click, and its position changes unmistakably between the two screens.
        ///
        /// Any settled movement counts; no magnitude threshold is used, since a stationary box does
        /// not drift while the real change is ~65px at worst.
        /// </summary>
        private static PasswordFieldInfo WaitForInputFieldMoved(IntPtr windowHandle, int fromCenterY, int stableSamples = 4, int retries = 60, int delay = 250)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            PasswordFieldInfo lastField = null;
            int stableCount = 0;

            for (int i = 1; i <= retries; i++)
            {
                PasswordFieldInfo field;
                using (var img = Imaging.CaptureWindowImage(windowHandle))
                    field = FindInputField(img);

                //a box at a different vertical position means we are no longer on the email screen
                bool moved = field != null && Math.Abs(field.CenterY - fromCenterY) >= INPUT_FIELD_MOVED_MIN_SHIFT;

                Log($"WaitForInputFieldMoved try {i}/{retries}: fieldCenterY={(field != null ? field.CenterY.ToString() : "none")} (was {fromCenterY}) moved={moved} stableCount={stableCount}");

                if (moved && lastField != null && field.CenterY == lastField.CenterY)
                {
                    stableCount++;
                    if (stableCount >= stableSamples)
                        return field;
                }
                else
                {
                    //still on the email screen, or moved but still animating — (re)start the count
                    //only once the box is actually somewhere new
                    stableCount = moved ? 1 : 0;
                }

                lastField = moved ? field : null;
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
        /// Finds the largest solid cyan button in the window (the "Continue" / "Sign in" primary
        /// button) and returns its centre, or null if none is present.
        ///
        /// The button is matched with a looser colour tolerance than the input box because it is a
        /// large saturated fill: different GPUs, colour profiles and scaling filters shift it by a few
        /// units, and being strict here would make the check fail on machines other than the one the
        /// values were sampled on. Candidate runs must also be reasonably wide AND tall so that thin
        /// cyan links (for example "Forgot password?") can never be mistaken for the button.
        /// </summary>
        private static PasswordFieldInfo FindCyanButton(IntPtr windowHandle)
        {
            using (var img = Imaging.CaptureWindowImage(windowHandle))
            {
                PasswordFieldInfo best = null;
                int bestArea = 0;

                for (int y = 0; y < img.Height; y++)
                {
                    int run = 0, runStart = 0;

                    for (int x = 0; x <= img.Width; x++)
                    {
                        bool isButton = false;
                        if (x < img.Width)
                        {
                            var c = img.GetPixel(x, y);
                            isButton = IsButtonFill(c.R, c.G, c.B);
                        }

                        if (isButton)
                        {
                            if (run == 0)
                                runStart = x;
                            run++;
                            continue;
                        }

                        if (run >= BUTTON_MIN_WIDTH)
                        {
                            //measure the run's height at a column inside it, away from the rounded ends
                            int probeX = Math.Min(runStart + Math.Max(3, run / 6), img.Width - 1);

                            int top = y;
                            while (top - 1 >= 0 && IsButtonFill(img.GetPixel(probeX, top - 1).R, img.GetPixel(probeX, top - 1).G, img.GetPixel(probeX, top - 1).B))
                                top--;

                            int bottom = y;
                            while (bottom + 1 < img.Height && IsButtonFill(img.GetPixel(probeX, bottom + 1).R, img.GetPixel(probeX, bottom + 1).G, img.GetPixel(probeX, bottom + 1).B))
                                bottom++;

                            int height = bottom - top + 1;
                            if (height >= BUTTON_MIN_HEIGHT && run * height > bestArea)
                            {
                                bestArea = run * height;
                                best = new PasswordFieldInfo
                                {
                                    Left = runStart,
                                    Right = runStart + run - 1,
                                    Top = top,
                                    Bottom = bottom,
                                    CenterX = runStart + run / 2,
                                    CenterY = (top + bottom) / 2,
                                    Width = run,
                                };
                            }
                        }

                        run = 0;
                    }
                }

                return best;
            }
        }

        /// <summary>
        /// True if a pixel belongs to the solid cyan primary button.
        /// </summary>
        private static bool IsButtonFill(byte r, byte g, byte b)
        {
            return Math.Abs(r - BUTTON_FILL_R) <= BUTTON_FILL_TOLERANCE
                && Math.Abs(g - BUTTON_FILL_G) <= BUTTON_FILL_TOLERANCE
                && Math.Abs(b - BUTTON_FILL_B) <= BUTTON_FILL_TOLERANCE;
        }

        /// <summary>
        /// Scans visible top level windows for the Epic launcher and returns the first one with a
        /// usable size, or IntPtr.Zero.
        ///
        /// This backs up the exact-title lookup, which is brittle: the launcher's window title varies
        /// between builds and locales, and several same-class placeholder windows exist. Matching on
        /// "contains Epic" plus a usable size finds the real UI window without depending on the title
        /// being character-for-character what we expect.
        /// </summary>
        private static IntPtr FindEpicWindowByScan()
        {
            IntPtr found = IntPtr.Zero;

            try
            {
                User32.EnumWindows((handle, param) =>
                {
                    if (!User32.IsWindowVisible(handle))
                        return true;

                    var title = new StringBuilder(256);
                    User32.GetWindowText(handle, title, title.Capacity);

                    var className = new StringBuilder(256);
                    User32.GetClassName(handle, className, className.Capacity);

                    bool looksLikeEpic =
                        title.ToString().IndexOf("Epic Games Launcher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (className.ToString() == "UnrealWindow" && title.ToString().IndexOf("Epic", StringComparison.OrdinalIgnoreCase) >= 0);

                    //only accept a real window, never one of the 0x0 placeholders
                    if (looksLikeEpic && HasUsableSize(handle))
                    {
                        found = handle;
                        return false;
                    }

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
                //enumeration failed; caller falls back to other strategies
            }

            return found;
        }

        /// <summary>
        /// Opts the host process into DPI awareness so window rectangles, captured images and cursor
        /// positions are all expressed in the same (physical) pixels.
        ///
        /// A process that is not DPI aware gets virtualised coordinates on a scaled display: window
        /// rectangles come back in logical units while the screen capture and SetCursorPos operate in
        /// physical pixels. Every offset derived from the capture is then wrong by the scale factor,
        /// which is invisible at 100% scaling and breaks the automation completely at 125% or 150%.
        ///
        /// Best effort and idempotent: both calls fail harmlessly if awareness was already set (for
        /// example by the host application or its manifest), and the per-monitor call is missing
        /// entirely on older Windows, in which case we fall back to the legacy system-DPI call.
        /// </summary>
        private static void EnsureDpiAware()
        {
            try
            {
                if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return;
            }
            catch (Exception)
            {
                //API not present on this Windows version, fall through to the legacy call
            }

            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch (Exception)
            {
                //nothing else we can do; on a scaled display the coordinates may be virtualised
            }
        }

        /// <summary>
        /// True if the handle refers to a window with a real, non-empty rectangle.
        ///
        /// The launcher creates several 0x0 "UnrealWindow" placeholder windows, and its
        /// Process.MainWindowHandle frequently refers to one of them. Those handles are perfectly
        /// valid and NOT IntPtr.Zero, so a plain null check accepts them and every subsequent capture
        /// comes back empty. Requiring a usable size is what distinguishes the real UI window.
        /// </summary>
        private static bool HasUsableSize(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return false;

            try
            {
                var rectangle = new WindowInfo(windowHandle).Rectangle;
                return rectangle.Width > MIN_USABLE_WINDOW_SIZE && rectangle.Height > MIN_USABLE_WINDOW_SIZE;
            }
            catch
            {
                //a handle can go away between enumeration and inspection; treat it as unusable
                return false;
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

        //Per-monitor v2 gives correct physical coordinates on mixed-DPI setups. Only present on
        //Windows 10 1703+, hence the graceful fallback to the legacy system-DPI-aware call.
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    }

    #endregion
}
