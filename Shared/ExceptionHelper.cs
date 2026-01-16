using System;
using System.IO;
using CoreLib.Imaging;

namespace BaseLmPlugin
{
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
