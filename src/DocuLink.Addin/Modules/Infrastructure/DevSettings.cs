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

        internal static event EventHandler<bool> FsValuesChanged;

        /// <summary>Raised after <see cref="ShowFsValueNoise"/> changes.</summary>
        internal static event EventHandler<bool> FsValueNoiseChanged;

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

        internal static bool ShowFsValues
        {
            get
            {
                try { return Settings.Default.ShowFsValues; }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not read ShowFsValues: {ex.Message}");
                    return false;
                }
            }
            set
            {
                if (ShowFsValues == value) return;
                try
                {
                    Settings.Default.ShowFsValues = value;
                    Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not persist ShowFsValues: {ex.Message}");
                }
                FsValuesChanged?.Invoke(null, value);
            }
        }

        /// <summary>
        /// Whether the document viewer draws the spans the financial-value detector
        /// recognized and then refused, each labelled with the rule that refused it.
        /// Independent of <see cref="ShowFsValues"/>: the noise overlay is inert, so
        /// it can be left on while linking values.
        /// </summary>
        internal static bool ShowFsValueNoise
        {
            get
            {
                try { return Settings.Default.ShowFsValueNoise; }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not read ShowFsValueNoise: {ex.Message}");
                    return false;
                }
            }
            set
            {
                if (ShowFsValueNoise == value) return;
                try
                {
                    Settings.Default.ShowFsValueNoise = value;
                    Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not persist ShowFsValueNoise: {ex.Message}");
                }
                FsValueNoiseChanged?.Invoke(null, value);
            }
        }
    }
}
