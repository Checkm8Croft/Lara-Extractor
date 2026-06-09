using System;
using TombLib.Utils;

namespace Lara_Extractor
{
    /// <summary>
    /// Implementazione silenziosa di IDialogHandler che non apre nessuna finestra UI.
    /// Necessario perché TombLib.ImportFromFile internamente può chiamare RaiseDialog
    /// per mostrare warning/errori durante il parsing del livello. Passare null causa
    /// blocchi o NullReferenceException su alcuni percorsi di codice.
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
            // Scarta silenziosamente tutti i dialog. Se abbiamo un logger,
            // logghiamo almeno il tipo di dialog per debug.
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
                // Prova a leggere proprietà comuni tramite reflection
                var type = description.GetType();
                var msgProp = type.GetProperty("Message") ?? type.GetProperty("Text") ?? type.GetProperty("Exception");
                if (msgProp != null)
                {
                    var val = msgProp.GetValue(description);
                    return val?.ToString() ?? string.Empty;
                }
            }
            catch { /* ignora */ }
            return description.ToString() ?? string.Empty;
        }
    }
}
