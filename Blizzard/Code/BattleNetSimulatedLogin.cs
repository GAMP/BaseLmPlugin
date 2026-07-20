using CoreLib.Imaging;
using GizmoShell;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsInput;

namespace BaseLmPlugin
{
    public static class BattleNetSimulatedLogin
    {
        #region CONSTANTS

        // Blue color of the "Continue" and "Log in" buttons
        private static readonly Color COLOR_BUTTON_BLUE = Color.FromArgb(255, 42, 117, 227);

        // Fuzzy match tolerance for the button color.
        private const int COLOR_TOLERANCE = 20;

        // Windows 11 GetWindowRect includes an invisible drop-shadow border.
        // Clicks must be shifted down by this offset to land in the correct position.
        private const int SHADOW_OFFSET_Y = 4;

        // Bump this when you rebuild — appears in the debug log so we know which DLL is loaded.
        private const string BuildStamp = "2026-06-03-log-rotate-v8";

        #endregion

        #region FUNCTIONS

        // Single log file in %TEMP%, appended on every step. Survives DLL replacement, doesn't need
        // a debugger attached, and lets the user just send us one file when they hit an issue.
        // Rotated when it exceeds MaxLogBytes — keeps one .prev file so we always have at least
        // the last attempt available.
        private static readonly string DebugLogPath = Path.Combine(Path.GetTempPath(), "battlenet_login.log");
        private const long MaxLogBytes = 256 * 1024; // 256 KB — generous for a few dozen attempts

        private static void LogStep(string message)
        {
            try
            {
                RotateLogIfTooLarge();
                File.AppendAllText(DebugLogPath,
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}{2}", DateTime.Now, message, Environment.NewLine));
            }
            catch { /* logging must never throw */ }
            Trace.WriteLine("BattleNet: " + message);
        }

        private static void RotateLogIfTooLarge()
        {
            try
            {
                var fi = new FileInfo(DebugLogPath);
                if (!fi.Exists || fi.Length < MaxLogBytes)
                    return;
                string prev = DebugLogPath + ".prev";
                if (File.Exists(prev))
                    File.Delete(prev);
                File.Move(DebugLogPath, prev);
            }
            catch { /* rotation is best-effort */ }
        }

        public static void Login(int targetProcessId, string username, string password)
        {
            LogStep(string.Format("==== Login() entered (build {0}) ====", BuildStamp));
            var targetProcess = Process.GetProcessById(targetProcessId);

            // Wait for any visible window from this process
            IntPtr mainWindowHandle = IntPtr.Zero;
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

            if (mainWindowHandle == IntPtr.Zero)
                throw new ArgumentException("Could not obtain main window handle.", nameof(mainWindowHandle));

            WindowInfo info = new(targetProcess.MainWindowHandle);
            info.BringToFront();
            info.Activate();
            LogStep(string.Format("window x={0} y={1} w={2} h={3}",
                info.Location.X, info.Location.Y, info.Width, info.Height));

            // Wait for the blue button in the 35-55% vertical band — confirms the login screen is ready.
            // Timeout: 60 seconds (120 × 500ms).
            var buttonBounds = WaitForButtonBoundsAsync(info.Handle, COLOR_BUTTON_BLUE, COLOR_TOLERANCE, 120, 500)
                .GetAwaiter()
                .GetResult();

            if (buttonBounds == null)
            {
                LogStep("FAIL: button not found within 60s — throwing TimeoutException");
                throw new TimeoutException("Battle.net launcher did not reach the login screen within the expected time.");
            }
            LogStep(string.Format("Screen 1 button bounds top={0} bottom={1}", buttonBounds.Value.TopY, buttonBounds.Value.BottomY));

            Thread.Sleep(100);
            info.BringToFront();
            info.Activate();

            int centerX = info.Location.X + info.Width / 2;

            // Locate the input field above the button by scanning for its top & bottom borders.
            // Screen 1: email field present. Screen 3: password field present (also above the button).
            var fieldRange = FindInputFieldRange(info, buttonBounds.Value.TopY);
            bool isScreen1 = fieldRange != null;

            LogStep(string.Format("Screen 1 fieldRange={0} isScreen1={1}",
                fieldRange.HasValue ? fieldRange.Value.ToString() : "<null>", isScreen1));

            KeyboardSimulator sim = new();
            MouseSimulator msim = new();

            if (isScreen1)
            {
                info.BringToFront();
                info.Activate();
                Thread.Sleep(100);

                int emailScreenY = info.Location.Y + (fieldRange.Value.TopY + fieldRange.Value.BottomY) / 2;
                LogStep(string.Format("Screen 1 click email at ({0}, {1})", centerX, emailScreenY));
                ClickAt(centerX, emailScreenY, msim);
                Thread.Sleep(100);

                sim.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
                Thread.Sleep(80);
                sim.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);
                Thread.Sleep(80);

                sim.TextEntry(username);
                Thread.Sleep(150);

                sim.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);

                // Wait for button to disappear (spinner phase), then reappear on Screen 3
                WaitForButtonGoneAsync(info.Handle, COLOR_BUTTON_BLUE, COLOR_TOLERANCE, 40, 150)
                    .GetAwaiter()
                    .GetResult();

                info.BringToFront();
                info.Activate();

                buttonBounds = WaitForButtonBoundsAsync(info.Handle, COLOR_BUTTON_BLUE, COLOR_TOLERANCE, 200, 200)
                    .GetAwaiter()
                    .GetResult();

                if (buttonBounds == null)
                {
                    LogStep("FAIL: Screen 3 button not found — throwing TimeoutException");
                    throw new TimeoutException("Battle.net did not reach the password screen after submitting email.");
                }
                LogStep(string.Format("Screen 3 button bounds top={0} bottom={1}", buttonBounds.Value.TopY, buttonBounds.Value.BottomY));

                Thread.Sleep(100);
                info.BringToFront();
                info.Activate();

                fieldRange = FindInputFieldRange(info, buttonBounds.Value.TopY);
                LogStep(string.Format("Screen 3 fieldRange={0}", fieldRange.HasValue ? fieldRange.Value.ToString() : "<null>"));
                if (fieldRange == null)
                {
                    LogStep("FAIL: password field not found — throwing InvalidOperationException");
                    throw new InvalidOperationException("Could not locate password field above Log in button.");
                }
            }

            // If isScreen1 was false (Screen 1 detection failed), fieldRange is still null here —
            // we never had a field to click. Explicit guard so we get a clear message instead of
            // the cryptic Nullable<T>.Value system throw.
            if (fieldRange == null)
            {
                LogStep("FAIL: Screen 1 field not found, cannot proceed to password input — throwing InvalidOperationException");
                throw new InvalidOperationException("Battle.net login screen detected but no input field found above the button. Check %TEMP%\\battlenet_login.log and battlenet_login_*.png for details.");
            }

            int passwordScreenY = info.Location.Y + (fieldRange.Value.TopY + fieldRange.Value.BottomY) / 2;
            LogStep(string.Format("Password click at ({0}, {1})", centerX, passwordScreenY));
            ClickAt(centerX, passwordScreenY, msim);
            Thread.Sleep(100);

            sim.ModifiedKeyStroke(WindowsInput.Native.VirtualKeyCode.LCONTROL, WindowsInput.Native.VirtualKeyCode.VK_A);
            Thread.Sleep(80);
            sim.KeyPress(WindowsInput.Native.VirtualKeyCode.BACK);
            Thread.Sleep(80);

            sim.TextEntry(password);
            Thread.Sleep(150);

            sim.KeyPress(WindowsInput.Native.VirtualKeyCode.RETURN);
        }

        // (TopY, BottomY) window-relative range of a horizontal feature.
        private readonly struct YRange
        {
            public readonly int TopY;
            public readonly int BottomY;
            public YRange(int top, int bottom) { TopY = top; BottomY = bottom; }
            public override string ToString() => string.Format("[{0}..{1}]", TopY, BottomY);
        }

        // Captures the window into a Bitmap. Caller disposes.
        private static Bitmap CaptureWindow(WindowInfo info)
        {
            Rectangle rect = new(info.Location.X, info.Location.Y, info.Width, info.Height);
            var bmp = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, info.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        // Finds the active input field above the button using horizontal-edge detection.
        //
        // Detection is contrast-based: a "field-edge row" is a row where many sample X columns
        // show a pixel that's significantly brighter than the pixel ROWGAP rows above and below it.
        // This is the defining feature of a horizontal border line (top or bottom edge of an input
        // box) and is robust to:
        //   - Focused vs unfocused borders (any border color produces an edge)
        //   - Thin or dim border lines (what matters is local contrast, not absolute brightness)
        //   - Filled vs hollow field interiors
        //   - Theme / DPI variations
        //
        // Left-aligned text ("Forgot password?") produces edges too, but only on 1-2 sample columns
        // where letters appear; the field's continuous horizontal line hits all columns at once.
        //
        // Algorithm:
        //   1. From just above the button, find the highest-Y row with ≥MIN_MATCHING columns
        //      showing a horizontal edge — that's the field bottom border.
        //   2. Look further up within the plausible field-height bracket for another edge row —
        //      that's the field top border. Take the lowest-Y match (= topmost border row).
        //
        // The field-height bracket is 3%..15% of window height (covers Battle.net field sizes at
        // any resolution while rejecting unrelated UI). Returns null if no field can be found.
        private static YRange? FindInputFieldRange(WindowInfo info, int buttonTopY)
        {
            int scanExtent = Math.Min(buttonTopY, (int)(info.Height * 0.30));
            if (scanExtent <= 10)
                return null;

            // 9 sample columns spanning 15%..85% of dialog width — wider coverage to catch the
            // field border even when the visible field box doesn't extend fully to the edges.
            int[] sampleX =
            {
                (int)(info.Width * 0.15),
                (int)(info.Width * 0.25),
                (int)(info.Width * 0.35),
                (int)(info.Width * 0.42),
                (int)(info.Width * 0.50),
                (int)(info.Width * 0.58),
                (int)(info.Width * 0.65),
                (int)(info.Width * 0.75),
                (int)(info.Width * 0.85),
            };
            const int MIN_MATCHING_COLUMNS = 5;
            const int ROW_GAP = 2;            // compare against rows ±ROW_GAP away
            const int EDGE_DELTA = 15;        // min brightness delta to count as an edge

            int topScanLimit = Math.Max(ROW_GAP, buttonTopY - scanExtent);
            int minFieldHeight = Math.Max(12, (int)(info.Height * 0.03));
            int maxFieldHeight = Math.Max(minFieldHeight + 15, (int)(info.Height * 0.15));

            using (var bmp = CaptureWindow(info))
            using (ImageTraverser traverser = new(bmp))
            {
                int bottomY = -1;
                for (int probeY = buttonTopY - ROW_GAP; probeY >= topScanLimit; probeY--)
                {
                    if (CountEdgeColumns(traverser, sampleX, probeY, ROW_GAP, EDGE_DELTA) >= MIN_MATCHING_COLUMNS)
                    {
                        bottomY = probeY;
                        break;
                    }
                }
                if (bottomY < 0)
                {
                    Trace.WriteLine("BattleNet: no field bottom edge found in scan range");
                    DumpDebugSnapshot(info, buttonTopY, null, "no-bottom-edge");
                    return null;
                }

                // Look for top edge within the field-height bracket. Take the highest matching row.
                int upperBoundY = Math.Max(topScanLimit, bottomY - maxFieldHeight);
                int topY = -1;
                for (int y = bottomY - minFieldHeight; y >= upperBoundY; y--)
                {
                    if (CountEdgeColumns(traverser, sampleX, y, ROW_GAP, EDGE_DELTA) >= MIN_MATCHING_COLUMNS)
                        topY = y;
                }

                if (topY < 0)
                {
                    Trace.WriteLine(string.Format("BattleNet: bottom edge at y={0} but no top edge found in bracket {1}..{2}",
                        bottomY, minFieldHeight, maxFieldHeight));
                    DumpDebugSnapshot(info, buttonTopY, new YRange(bottomY, bottomY), "no-top-edge");
                    return null;
                }

                int height = bottomY - topY;
                Trace.WriteLine(string.Format("BattleNet: field top={0} bottom={1} height={2} (bracket {3}..{4})",
                    topY, bottomY, height, minFieldHeight, maxFieldHeight));
                return new YRange(topY, bottomY);
            }
        }

        // Counts how many sample columns at row y look like a horizontal edge — i.e. the pixel at
        // (x, y) is noticeably brighter than the pixels at (x, y-rowGap) AND (x, y+rowGap).
        // This isolates horizontal lines (field borders) and is invariant to the line's color.
        private static int CountEdgeColumns(ImageTraverser traverser, int[] sampleX, int y, int rowGap, int minDelta)
        {
            if (y - rowGap < 0 || y + rowGap >= traverser.ImageHeight)
                return 0;
            int count = 0;
            foreach (int x in sampleX)
            {
                if (x < 0 || x >= traverser.ImageWidth)
                    continue;
                int hereLum = Luminance(traverser.GetPixel(x, y));
                int aboveLum = Luminance(traverser.GetPixel(x, y - rowGap));
                int belowLum = Luminance(traverser.GetPixel(x, y + rowGap));
                if (hereLum - aboveLum >= minDelta && hereLum - belowLum >= minDelta)
                    count++;
            }
            return count;
        }

        private static int Luminance(Color c)
        {
            // Approximate perceived brightness — Rec.601 weighting, kept as ints for speed.
            return (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
        }

        // Saves an annotated PNG of the window capture to %TEMP%\battlenet_login_<reason>_<ts>.png
        // with rectangles drawn at the detected button-top line and field range. Lets us see
        // exactly what the algorithm did when a user reports a failure.
        private static void DumpDebugSnapshot(WindowInfo info, int buttonTopY, YRange? fieldRange, string reason)
        {
            try
            {
                using (var bmp = CaptureWindow(info))
                using (var g = Graphics.FromImage(bmp))
                {
                    using (var buttonPen = new Pen(Color.Lime, 2))
                        g.DrawLine(buttonPen, 0, buttonTopY, bmp.Width - 1, buttonTopY);

                    if (fieldRange.HasValue)
                    {
                        using (var fieldPen = new Pen(Color.Red, 2))
                        {
                            g.DrawLine(fieldPen, 0, fieldRange.Value.TopY, bmp.Width - 1, fieldRange.Value.TopY);
                            g.DrawLine(fieldPen, 0, fieldRange.Value.BottomY, bmp.Width - 1, fieldRange.Value.BottomY);
                        }
                    }

                    string path = Path.Combine(Path.GetTempPath(),
                        string.Format("battlenet_login_{0}_{1:yyyyMMdd_HHmmss}.png", reason, DateTime.Now));
                    bmp.Save(path, ImageFormat.Png);
                    Trace.WriteLine(string.Format("BattleNet: debug snapshot saved to {0}", path));
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(string.Format("BattleNet: failed to save debug snapshot: {0}", ex.Message));
            }
        }

        // Waits until no blue pixel exists in the button zone — button has disappeared.
        private static async Task WaitForButtonGoneAsync(IntPtr windowHandle, Color target, int tolerance, int retries, int delayMs, CancellationToken ct = default)
        {
            for (int i = 1; i <= retries; i++)
            {
                WindowInfo info = new(windowHandle);
                if (info.IsMinimized) info.Restore();
                info.BringToFront();

                int yMin = (int)(info.Height * 0.35);
                int yMax = (int)(info.Height * 0.55);

                using (var bmp = CaptureWindow(info))
                using (ImageTraverser traverser = new(bmp))
                {
                    bool stillVisible = false;
                    int cx = info.Width / 2;
                    for (int py = yMin; py <= yMax && !stillVisible; py++)
                    {
                        if (IsColorMatch(traverser.GetPixel(cx, py), target, tolerance))
                            stillVisible = true;
                    }
                    if (!stillVisible)
                        return;
                }

                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        // Waits for the blue button to appear and returns its top & bottom edges.
        //
        // Vertical band: 25%..75% of window height — wide enough to cover both the email screen
        // (button at ~45%) and the password screen (button at ~55%) across resolutions.
        //
        // Detection strategy: for each row in the band, find the LONGEST CONTIGUOUS HORIZONTAL RUN
        // of blue pixels. A real button row has a run spanning most of the button's width
        // (≥40% of dialog width). Small blue UI (links, icons, gear) produces runs that are at most
        // ~25% wide. Centered white label text ("Log in", "Continue") splits the blue into two
        // shorter runs — we use the LEFT run only (from the button's left edge up to the text), so
        // text doesn't shorten the detected run below the threshold as long as the button is wide.
        //
        // We then require the detected blue band to be vertically tall enough to be a real button
        // (≥3% of window height = ~20px at 720). This rejects 1-2px horizontal slivers of blue
        // that might exist elsewhere (e.g., link underlines).
        private static async Task<YRange?> WaitForButtonBoundsAsync(IntPtr windowHandle, Color target, int tolerance, int retries, int delayMs, CancellationToken ct = default)
        {
            if (windowHandle == IntPtr.Zero)
                throw new ArgumentException("Invalid window handle.", nameof(windowHandle));

            for (int i = 1; i <= retries; i++)
            {
                WindowInfo info = new(windowHandle);
                if (info.IsMinimized) info.Restore();
                info.BringToFront();

                int yMin = (int)(info.Height * 0.25);
                int yMax = (int)(info.Height * 0.75);
                int minRunWidth = (int)(info.Width * 0.30);    // button row must have ≥30% wide blue run
                int minBandHeight = Math.Max(8, (int)(info.Height * 0.025));

                using (var bmp = CaptureWindow(info))
                using (ImageTraverser traverser = new(bmp))
                {
                    int candidateTop = -1;
                    int candidateBottom = -1;

                    for (int py = yMin; py <= yMax; py++)
                    {
                        if (!RowHasBlueRun(traverser, py, target, tolerance, minRunWidth))
                        {
                            if (candidateTop >= 0 && candidateBottom - candidateTop >= minBandHeight)
                            {
                                Trace.WriteLine(string.Format("BattleNet: button top={0} bottom={1} h={2} retry={3}",
                                    candidateTop, candidateBottom, candidateBottom - candidateTop, i));
                                return new YRange(candidateTop, candidateBottom);
                            }
                            candidateTop = -1;
                            candidateBottom = -1;
                            continue;
                        }

                        if (candidateTop < 0)
                            candidateTop = py;
                        candidateBottom = py;
                    }

                    // Trailing candidate (band extends to yMax).
                    if (candidateTop >= 0 && candidateBottom - candidateTop >= minBandHeight)
                    {
                        Trace.WriteLine(string.Format("BattleNet: button top={0} bottom={1} h={2} retry={3} (trailing)",
                            candidateTop, candidateBottom, candidateBottom - candidateTop, i));
                        return new YRange(candidateTop, candidateBottom);
                    }

                    // Every 10 retries (~5s), dump pixel samples so we can see what colors are
                    // actually in the button band when detection fails.
                    if (i % 10 == 0)
                        DumpRowSamples(traverser, info, yMin, yMax, minRunWidth, i);
                }

                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }

            return null;
        }

        // Samples a few representative rows in the scan band and dumps pixel colors at multiple X
        // positions. Lets us see what the algorithm is actually seeing when it can't find a button.
        private static void DumpRowSamples(ImageTraverser traverser, WindowInfo info, int yMin, int yMax, int minRunWidth, int retry)
        {
            int[] rowsToSample = {
                yMin,
                yMin + (yMax - yMin) / 4,
                (yMin + yMax) / 2,
                yMin + 3 * (yMax - yMin) / 4,
                yMax,
            };
            int[] xToSample = {
                (int)(info.Width * 0.10),
                (int)(info.Width * 0.25),
                (int)(info.Width * 0.50),
                (int)(info.Width * 0.75),
                (int)(info.Width * 0.90),
            };

            LogStep(string.Format("button scan retry {0}: yMin={1} yMax={2} minRunWidth={3} samples follow",
                retry, yMin, yMax, minRunWidth));
            foreach (int y in rowsToSample)
            {
                if (y < 0 || y >= traverser.ImageHeight) continue;
                var sb = new System.Text.StringBuilder();
                sb.AppendFormat("  y={0}:", y);
                foreach (int x in xToSample)
                {
                    if (x < 0 || x >= traverser.ImageWidth) continue;
                    Color c = traverser.GetPixel(x, y);
                    sb.AppendFormat(" x{0}=#{1:X2}{2:X2}{3:X2}", x, c.R, c.G, c.B);
                }
                // Also measure the longest blue run on this row.
                int longestRun = 0;
                int currentRun = 0;
                for (int x = 0; x < traverser.ImageWidth; x++)
                {
                    if (IsColorMatch(traverser.GetPixel(x, y), COLOR_BUTTON_BLUE, COLOR_TOLERANCE))
                    {
                        currentRun++;
                        if (currentRun > longestRun) longestRun = currentRun;
                    }
                    else
                    {
                        currentRun = 0;
                    }
                }
                sb.AppendFormat(" longestBlueRun={0}", longestRun);
                LogStep(sb.ToString());
            }
        }

        // Returns true if row y contains a contiguous horizontal run of blue ≥minRunWidth pixels long.
        private static bool RowHasBlueRun(ImageTraverser traverser, int y, Color target, int tolerance, int minRunWidth)
        {
            if (y < 0 || y >= traverser.ImageHeight)
                return false;

            int currentStart = -1;
            int width = traverser.ImageWidth;
            for (int x = 0; x < width; x++)
            {
                if (IsColorMatch(traverser.GetPixel(x, y), target, tolerance))
                {
                    if (currentStart < 0)
                        currentStart = x;
                    if (x - currentStart + 1 >= minRunWidth)
                        return true;
                }
                else
                {
                    currentStart = -1;
                }
            }
            return false;
        }

        // Matches the Battle.net button blue. Tolerant of display calibration / gamma / DPI:
        // the button is rendered with high G+B and low R, but the exact R value can vary widely
        // between machines (observed #2A75E3 on the original dev box, #0074E0 on a user's box —
        // R differs by 42, way outside any reasonable per-channel tolerance).
        //
        // So we require:
        //   - R is low enough to be a "blue" pixel (not a pastel), AND
        //   - G and B are within ±tolerance of the target.
        // This catches all reasonable renderings of the button without losing specificity
        // (random blue UI accents would have to hit both G and B closely, which they won't).
        private static bool IsColorMatch(Color c, Color target, int tolerance)
        {
            return c.R <= target.R + tolerance
                && Math.Abs(c.G - target.G) <= tolerance
                && Math.Abs(c.B - target.B) <= tolerance;
        }

        private static void ClickAt(int screenX, int screenY, MouseSimulator msim)
        {
            System.Windows.Forms.Cursor.Position = new Point(screenX, screenY + SHADOW_OFFSET_Y);
            Thread.Sleep(50);
            msim.LeftButtonClick();
        }

        #endregion
    }
}
