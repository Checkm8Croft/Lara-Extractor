using System;
using TombLib.Utils;

namespace Lara_Extractor
{
    /// <summary>
    /// Silent implementation of IDialogHandler that never opens a UI window.
    /// Needed because TombLib.ImportFromFile can internally call RaiseDialog
    /// to show warnings/errors while parsing the level. Passing null causes
    /// hangs or NullReferenceException on some code paths.
    /// </summary>
    public class SilentDialogHandler : IDialogHandler
    {
        private readonly Action<string>? _logger;

        public SilentDialogHandler(Action<string>? logger = null)
        {
            _logger = logger;
        }

        public void RaiseDialog(IDialogDescription description)
        {
            // Silently discard all dialogs. If we have a logger, at least log
            // the dialog type for debugging.
            if (_logger != null)
            {
                string typeName = description?.GetType().Name ?? "Unknown";
                string message = TryGetMessage(description);
                if (!string.IsNullOrEmpty(message))
                    _logger($"[Wad2 Dialog/{typeName}] {message}");
            }
        }

        private static string TryGetMessage(IDialogDescription? description)
        {
            if (description == null) return string.Empty;
            try
            {
                // Try reading common properties via reflection
                var type = description.GetType();
                var msgProp = type.GetProperty("Message") ?? type.GetProperty("Text") ?? type.GetProperty("Exception");
                if (msgProp != null)
                {
                    var val = msgProp.GetValue(description);
                    return val?.ToString() ?? string.Empty;
                }
            }
            catch { /* ignore */ }
            return description.ToString() ?? string.Empty;
        }
    }
}
