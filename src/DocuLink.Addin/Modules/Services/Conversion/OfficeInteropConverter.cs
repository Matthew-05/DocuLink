using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DocuLink.Addin.Modules.Services.Conversion
{
    /// <summary>
    /// Converts Office documents to PDF by driving the installed Office applications
    /// through late-bound COM automation.
    ///
    /// Late binding (Type.GetTypeFromProgID + dynamic) rather than typed interop is
    /// deliberate: DocuLink only references the Excel PIA, and adding Word/PowerPoint/
    /// Outlook PIAs would pin the add-in to specific Office versions and grow the
    /// install. Late binding also lets a missing application degrade into a clear
    /// per-file error instead of a load-time failure.
    ///
    /// One instance covers a whole import batch: each Office application is launched
    /// at most once and reused across files, then quit and released on dispose.
    /// Instances are separate from the user's own Office session, so a failed
    /// conversion cannot disturb open documents — but for the same reason the
    /// converter must always be disposed, or a hidden WINWORD.EXE is left behind.
    ///
    /// Not thread-safe; use from the thread that created it.
    /// </summary>
    internal sealed class OfficeInteropConverter : IDisposable
    {
        // WdExportFormat.wdExportFormatPDF / XlFixedFormatType.xlTypePDF /
        // PpFixedFormatType.ppFixedFormatTypePDF use these documented constants.
        private const int WdExportFormatPdf = 17;
        private const int WdExportOptimizeForPrint = 0;
        private const int WdDoNotSaveChanges = 0;
        private const int WdAlertsNone = 0;

        private const int XlTypePdf = 0;
        private const int XlQualityStandard = 0;

        private const int PpFixedFormatTypePdf = 2;
        private const int PpFixedFormatIntentPrint = 2;

        private const int OlSaveAsTypeHtml = 5;

        private const int MsoTrue = -1;
        private const int MsoFalse = 0;

        private readonly Dictionary<ConversionEngine, object> _applications =
            new Dictionary<ConversionEngine, object>();

        private bool _disposed;

        /// <summary>Friendly name of the Office application an engine needs.</summary>
        public static string GetApplicationName(ConversionEngine engine)
        {
            switch (engine)
            {
                case ConversionEngine.Word: return "Microsoft Word";
                case ConversionEngine.Excel: return "Microsoft Excel";
                case ConversionEngine.PowerPoint: return "Microsoft PowerPoint";
                case ConversionEngine.Outlook: return "Microsoft Outlook";
                default: return "Microsoft Office";
            }
        }

        /// <summary>True when the ProgID an engine needs is registered on this machine.</summary>
        public static bool IsAvailable(ConversionEngine engine)
        {
            string progId = GetProgId(engine);
            return progId != null && Type.GetTypeFromProgID(progId) != null;
        }

        /// <summary>
        /// Converts a source document to PDF at <paramref name="outputPdfPath"/>.
        ///
        /// Outlook is the exception: it has no PDF export, so a .msg is saved as
        /// HTML at <paramref name="htmlPathForMessages"/> and the path is returned
        /// via <paramref name="producedHtmlPath"/> for the caller to render.
        /// </summary>
        public void Convert(
            ConversionEngine engine,
            string sourcePath,
            string outputPdfPath,
            string htmlPathForMessages,
            out string producedHtmlPath)
        {
            ThrowIfDisposed();
            producedHtmlPath = null;

            switch (engine)
            {
                case ConversionEngine.Word:
                    ConvertWithWord(sourcePath, outputPdfPath);
                    break;

                case ConversionEngine.Excel:
                    ConvertWithExcel(sourcePath, outputPdfPath);
                    break;

                case ConversionEngine.PowerPoint:
                    ConvertWithPowerPoint(sourcePath, outputPdfPath);
                    break;

                case ConversionEngine.Outlook:
                    ConvertMessageToHtml(sourcePath, htmlPathForMessages);
                    producedHtmlPath = htmlPathForMessages;
                    break;

                default:
                    throw new NotSupportedException($"'{engine}' is not an Office conversion engine.");
            }
        }

        private void ConvertWithWord(string sourcePath, string outputPdfPath)
        {
            dynamic app = GetApplication(ConversionEngine.Word);
            dynamic documents = null;
            dynamic document = null;

            try
            {
                documents = app.Documents;
                document = documents.Open(
                    FileName: sourcePath,
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false);

                document.ExportAsFixedFormat(
                    OutputFileName: outputPdfPath,
                    ExportFormat: WdExportFormatPdf,
                    OpenAfterExport: false,
                    OptimizeFor: WdExportOptimizeForPrint);
            }
            finally
            {
                if (document != null)
                {
                    try { document.Close(WdDoNotSaveChanges); } catch { }
                }
                Release(document);
                Release(documents);
            }
        }

        private void ConvertWithExcel(string sourcePath, string outputPdfPath)
        {
            dynamic app = GetApplication(ConversionEngine.Excel);
            dynamic workbooks = null;
            dynamic workbook = null;

            try
            {
                workbooks = app.Workbooks;
                workbook = workbooks.Open(
                    Filename: sourcePath,
                    UpdateLinks: 0,
                    ReadOnly: true,
                    AddToMru: false);

                workbook.ExportAsFixedFormat(
                    Type: XlTypePdf,
                    Filename: outputPdfPath,
                    Quality: XlQualityStandard,
                    IncludeDocProperties: false,
                    IgnorePrintAreas: false,
                    OpenAfterPublish: false);
            }
            finally
            {
                if (workbook != null)
                {
                    try { workbook.Close(false); } catch { }
                }
                Release(workbook);
                Release(workbooks);
            }
        }

        private void ConvertWithPowerPoint(string sourcePath, string outputPdfPath)
        {
            dynamic app = GetApplication(ConversionEngine.PowerPoint);
            dynamic presentations = null;
            dynamic presentation = null;

            try
            {
                presentations = app.Presentations;
                presentation = presentations.Open(
                    FileName: sourcePath,
                    ReadOnly: MsoTrue,
                    Untitled: MsoTrue,   // Opens a copy, leaving the original unlocked
                    WithWindow: MsoFalse);

                presentation.ExportAsFixedFormat(
                    Path: outputPdfPath,
                    FixedFormatType: PpFixedFormatTypePdf,
                    Intent: PpFixedFormatIntentPrint);
            }
            finally
            {
                if (presentation != null)
                {
                    try { presentation.Close(); } catch { }
                }
                Release(presentation);
                Release(presentations);
            }
        }

        /// <summary>
        /// Saves an Outlook .msg as HTML. Outlook has no PDF export, so the caller
        /// renders the HTML through <see cref="HtmlToPdfConverter"/>.
        /// </summary>
        private void ConvertMessageToHtml(string sourcePath, string outputHtmlPath)
        {
            if (string.IsNullOrWhiteSpace(outputHtmlPath))
                throw new ArgumentException("An HTML output path is required for messages.", nameof(outputHtmlPath));

            dynamic app = GetApplication(ConversionEngine.Outlook);
            dynamic session = null;
            dynamic item = null;

            try
            {
                session = app.Session;                     // Forces logon to the default profile
                item = session.OpenSharedItem(sourcePath);
                item.SaveAs(outputHtmlPath, OlSaveAsTypeHtml);
            }
            finally
            {
                if (item != null)
                {
                    try { item.Close(1); } catch { }       // olDiscard
                }
                Release(item);
                Release(session);
            }

            if (!File.Exists(outputHtmlPath))
                throw new IOException("Outlook did not produce an HTML file for the message.");
        }

        /// <summary>Returns the cached application for an engine, launching it on first use.</summary>
        private dynamic GetApplication(ConversionEngine engine)
        {
            if (_applications.TryGetValue(engine, out object cached))
                return cached;

            string progId = GetProgId(engine);
            Type type = progId == null ? null : Type.GetTypeFromProgID(progId);

            if (type == null)
                throw new InvalidOperationException(
                    $"{GetApplicationName(engine)} is not installed, so this file type cannot be converted.");

            dynamic app;
            try
            {
                app = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not start {GetApplicationName(engine)}: {ex.Message}", ex);
            }

            ConfigureApplication(engine, app);
            _applications[engine] = (object)app;
            return app;
        }

        private static void ConfigureApplication(ConversionEngine engine, dynamic app)
        {
            try
            {
                switch (engine)
                {
                    case ConversionEngine.Word:
                        app.Visible = false;
                        app.DisplayAlerts = WdAlertsNone;
                        break;

                    case ConversionEngine.Excel:
                        app.Visible = false;
                        app.DisplayAlerts = false;
                        app.AskToUpdateLinks = false;
                        app.ScreenUpdating = false;
                        break;

                    case ConversionEngine.PowerPoint:
                        // PowerPoint rejects Visible = false on several builds and
                        // throws; presentations are opened WithWindow:=msoFalse instead.
                        break;
                }
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Could not configure {GetApplicationName(engine)}: {ex.Message}");
            }
        }

        private static string GetProgId(ConversionEngine engine)
        {
            switch (engine)
            {
                case ConversionEngine.Word: return "Word.Application";
                case ConversionEngine.Excel: return "Excel.Application";
                case ConversionEngine.PowerPoint: return "PowerPoint.Application";
                case ConversionEngine.Outlook: return "Outlook.Application";
                default: return null;
            }
        }

        private static void Release(object comObject)
        {
            if (comObject == null) return;

            try
            {
                if (Marshal.IsComObject(comObject))
                    Marshal.ReleaseComObject(comObject);
            }
            catch { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OfficeInteropConverter));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var pair in _applications)
            {
                dynamic app = pair.Value;

                // Outlook is commonly already running for the user and Quit() would
                // close their mail client; it is released without quitting and shuts
                // itself down once the last automation reference drops.
                if (pair.Key != ConversionEngine.Outlook)
                {
                    try
                    {
                        if (pair.Key == ConversionEngine.Word)
                            app.Quit(WdDoNotSaveChanges);
                        else
                            app.Quit();
                    }
                    catch { }
                }

                Release(pair.Value);
            }

            _applications.Clear();
        }
    }
}
