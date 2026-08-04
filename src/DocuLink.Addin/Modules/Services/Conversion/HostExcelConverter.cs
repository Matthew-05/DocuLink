using System;
using System.IO;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services.Conversion
{
    /// <summary>
    /// Exports spreadsheets to PDF through the Excel instance DocuLink is already
    /// running inside.
    ///
    /// Why this is not part of <see cref="OfficeInteropConverter"/> — Excel registers
    /// its class factory in its own process, so calling
    /// <c>CoCreateInstance("Excel.Application")</c> from a VSTO add-in does NOT start a
    /// second Excel: COM resolves it from the current process's class table and hands
    /// back the very instance the user is working in. Everything the automation path
    /// then does lands on the live session — <c>Visible = false</c> hides the user's
    /// window, <c>EnableEvents = false</c> silently disables the add-in's own event
    /// wiring, and <c>Quit()</c> tears the host down mid-conversion, which is what
    /// raised the 'DisconnectedContext' MDA and the 0x80010114 COMExceptions.
    ///
    /// So spreadsheets are converted deliberately in-host instead. That means:
    ///   • every call runs on the UI thread — no STA marshalling, no extra process;
    ///   • the application is never quit, hidden or reconfigured beyond a
    ///     save-and-restore window around the export;
    ///   • a workbook the user already has open is exported in place and left open.
    /// </summary>
    internal static class HostExcelConverter
    {
        private const int XlTypePdf = 0;
        private const int XlQualityStandard = 0;

        /// <summary>
        /// Exports <paramref name="sourcePath"/> to <paramref name="outputPdfPath"/>.
        /// UI thread only — the host's COM objects belong to Excel's main apartment.
        /// </summary>
        public static void Convert(string sourcePath, string outputPdfPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("A source path is required.", nameof(sourcePath));

            Excel.Application app = Globals.ThisAddIn?.Application;
            if (app == null)
                throw new InvalidOperationException("Excel is not available to convert this workbook.");

            using (var state = new HostApplicationState(app))
            {
                Excel.Workbook workbook = FindOpenWorkbook(app, sourcePath);

                // Only close what we opened; a workbook the user already had open
                // stays open, and closing it would lose their unsaved work.
                bool weOpenedIt = workbook == null;

                if (weOpenedIt)
                    workbook = OpenForExport(app, sourcePath);

                try
                {
                    Export(workbook, outputPdfPath);
                }
                finally
                {
                    if (weOpenedIt)
                        CloseWithoutSaving(workbook);
                }
            }
        }

        private static Excel.Workbook OpenForExport(Excel.Application app, string sourcePath)
        {
            Excel.Workbook workbook;

            try
            {
                workbook = app.Workbooks.Open(
                    Filename: sourcePath,
                    UpdateLinks: 0,
                    ReadOnly: true,
                    AddToMru: false,
                    Notify: false);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "Excel could not open this workbook: " + ex.Message, ex);
            }

            if (workbook == null)
                throw new InvalidOperationException("Excel could not open this workbook.");

            return workbook;
        }

        private static void Export(Excel.Workbook workbook, string outputPdfPath)
        {
            try
            {
                workbook.ExportAsFixedFormat(
                    Type: (Excel.XlFixedFormatType)XlTypePdf,
                    Filename: outputPdfPath,
                    Quality: (Excel.XlFixedFormatQuality)XlQualityStandard,
                    IncludeDocProperties: false,
                    IgnorePrintAreas: false,
                    OpenAfterPublish: false);
            }
            catch (COMException ex)
            {
                // Excel reports "Document not saved" for workbooks with nothing
                // printable (every sheet empty or hidden), which is worth saying
                // plainly rather than surfacing an HRESULT.
                throw new InvalidOperationException(
                    File.Exists(outputPdfPath)
                        ? "Excel could not finish exporting this workbook: " + ex.Message
                        : "Excel produced no PDF for this workbook — it may have no printable content.",
                    ex);
            }
        }

        /// <summary>
        /// Returns the already-open workbook for a path, or null when it is not open.
        /// <c>Workbooks.Open</c> on an open file returns the existing workbook rather
        /// than a fresh copy, so this has to be checked before deciding to close.
        /// </summary>
        private static Excel.Workbook FindOpenWorkbook(Excel.Application app, string sourcePath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(sourcePath);
            }
            catch (Exception)
            {
                return null;
            }

            foreach (Excel.Workbook candidate in app.Workbooks)
            {
                string candidatePath;
                try
                {
                    candidatePath = candidate.FullName;
                }
                catch (COMException)
                {
                    continue;
                }

                if (string.Equals(candidatePath, fullPath, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        private static void CloseWithoutSaving(Excel.Workbook workbook)
        {
            if (workbook == null) return;

            try { workbook.Close(SaveChanges: false); }
            catch (Exception ex) { DocuLinkLog.Trace($"Could not close the exported workbook: {ex.Message}"); }
        }

        /// <summary>
        /// Captures the host application settings the export disturbs and puts them
        /// back on dispose, including the window the user was looking at — opening a
        /// workbook activates it.
        ///
        /// Every property is read and written defensively: Excel throws from these
        /// accessors while a modal dialog or cell edit is in progress, and a failure
        /// to restore one setting must not prevent the others being restored.
        /// </summary>
        private sealed class HostApplicationState : IDisposable
        {
            private readonly Excel.Application _app;
            private readonly bool? _screenUpdating;
            private readonly bool? _displayAlerts;
            private readonly bool? _enableEvents;
            private readonly bool? _askToUpdateLinks;
            private readonly Excel.Window _activeWindow;

            public HostApplicationState(Excel.Application app)
            {
                _app = app;

                _screenUpdating = Read(() => app.ScreenUpdating);
                _displayAlerts = Read(() => app.DisplayAlerts);
                _enableEvents = Read(() => app.EnableEvents);
                _askToUpdateLinks = Read(() => app.AskToUpdateLinks);

                try { _activeWindow = app.ActiveWindow; }
                catch (Exception) { _activeWindow = null; }

                // EnableEvents is the important one: without it the add-in's own
                // WorkbookOpen/WorkbookActivate handlers fire for a workbook that is
                // only being opened to be exported, and would try to build a task
                // pane and a storage session for it.
                Write(() => app.EnableEvents = false);
                Write(() => app.ScreenUpdating = false);
                Write(() => app.DisplayAlerts = false);
                Write(() => app.AskToUpdateLinks = false);
            }

            public void Dispose()
            {
                if (_activeWindow != null)
                    Write(() => _activeWindow.Activate());

                if (_askToUpdateLinks.HasValue)
                    Write(() => _app.AskToUpdateLinks = _askToUpdateLinks.Value);

                if (_displayAlerts.HasValue)
                    Write(() => _app.DisplayAlerts = _displayAlerts.Value);

                if (_screenUpdating.HasValue)
                    Write(() => _app.ScreenUpdating = _screenUpdating.Value);

                // Restored last so the handlers stay quiet until everything else is
                // back the way the user left it.
                if (_enableEvents.HasValue)
                    Write(() => _app.EnableEvents = _enableEvents.Value);
            }

            private static bool? Read(Func<bool> get)
            {
                try { return get(); }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not read an Excel application setting: {ex.Message}");
                    return null;
                }
            }

            private static void Write(Action set)
            {
                try { set(); }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"Could not restore an Excel application setting: {ex.Message}");
                }
            }
        }
    }
}
