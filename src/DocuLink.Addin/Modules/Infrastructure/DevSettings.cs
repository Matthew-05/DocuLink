using System;
using DocuLink.Addin.Properties;

namespace DocuLink.Addin.Modules.Infrastructure
{
    /// <summary>
    /// Developer-only toggles surfaced in the Settings dialog's Development section
    /// (development and beta builds only). Values are persisted per user so a state
    /// survives Excel restarts, and changes raise an event so open surfaces can
    /// apply them without reopening the workbook.
    /// </summary>
    internal static class DevSettings
    {
        /// <summary>Raised after <see cref="ShowCharBoundingBoxes"/> changes.</summary>
        internal static event EventHandler<bool> CharBoundingBoxesChanged;

        /// <summary>
        /// Whether the document viewer draws the per-character bounding-box debug
        /// overlay built from its text cache.
        /// </summary>
        internal static bool ShowCharBoundingBoxes
        {
            get
            {
                try
                {
                    return Settings.Default.ShowCharBoundingBoxes;
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not read ShowCharBoundingBoxes: {ex.Message}");
                    return false;
                }
            }
            set
            {
                if (ShowCharBoundingBoxes == value) return;

                try
                {
                    Settings.Default.ShowCharBoundingBoxes = value;
                    Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not persist ShowCharBoundingBoxes: {ex.Message}");
                }

                CharBoundingBoxesChanged?.Invoke(null, value);
            }
        }
    }
}
