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

        /// <summary>Raised after <see cref="ShowValues"/> changes.</summary>
        internal static event EventHandler<bool> ValuesChanged;

        /// <summary>Raised after <see cref="ShowReferences"/> changes.</summary>
        internal static event EventHandler<bool> ReferencesChanged;

        /// <summary>Raised after <see cref="ShowValueNoise"/> changes.</summary>
        internal static event EventHandler<bool> ValueNoiseChanged;

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

        internal static bool ShowValues
        {
            get
            {
                try { return Settings.Default.ShowValues; }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not read ShowValues: {ex.Message}");
                    return false;
                }
            }
            set
            {
                if (ShowValues == value) return;
                try
                {
                    Settings.Default.ShowValues = value;
                    Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not persist ShowValues: {ex.Message}");
                }
                ValuesChanged?.Invoke(null, value);
            }
        }

        /// <summary>
        /// Whether the document viewer draws the reference layer: spans that identify
        /// rather than measure -- an invoice number, an area code, a citation naming a
        /// note. References are click targets whether or not this is on, exactly as
        /// values are, and on every document alike; this decides only whether their
        /// boxes are visible.
        /// </summary>
        internal static bool ShowReferences
        {
            get
            {
                try { return Settings.Default.ShowReferences; }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not read ShowReferences: {ex.Message}");
                    return false;
                }
            }
            set
            {
                if (ShowReferences == value) return;
                try
                {
                    Settings.Default.ShowReferences = value;
                    Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not persist ShowReferences: {ex.Message}");
                }
                ReferencesChanged?.Invoke(null, value);
            }
        }

        /// <summary>
        /// Whether the document viewer draws the spans the value detector
        /// recognized and then refused, each labelled with the rule that refused it.
        /// Independent of <see cref="ShowValues"/>: the noise overlay is inert, so
        /// it can be left on while linking values.
        /// </summary>
        internal static bool ShowValueNoise
        {
            get
            {
                try { return Settings.Default.ShowValueNoise; }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not read ShowValueNoise: {ex.Message}");
                    return false;
                }
            }
            set
            {
                if (ShowValueNoise == value) return;
                try
                {
                    Settings.Default.ShowValueNoise = value;
                    Settings.Default.Save();
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not persist ShowValueNoise: {ex.Message}");
                }
                ValueNoiseChanged?.Invoke(null, value);
            }
        }
    }
}
