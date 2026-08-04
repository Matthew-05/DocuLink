using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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
    /// Threading — this is the important part. Every COM call runs on one dedicated
    /// STA thread owned by this instance, and the Office applications are created,
    /// used, quit and released on that same thread. Anything else raises the
    /// 'DisconnectedContext' MDA: an RCW created on a thread-pool thread is bound to
    /// that thread's COM context, and the pool reassigns threads between awaits, so
    /// the second file in a batch (or Dispose on the UI thread) finds the original
    /// context gone. A long-lived STA thread keeps one context alive for the whole
    /// batch, which is also what Office automation expects.
    ///
    /// That thread runs a real Windows message loop. An STA that blocks without
    /// pumping cannot service incoming COM calls or complete cross-apartment
    /// marshalling, which shows up as intermittent RPC failures and hangs when Office
    /// calls back into the caller; work is posted to the loop through a hidden control
    /// rather than a blocking queue.
    ///
    /// Ownership — every Office application here is single-instance, so
    /// CoCreateInstance returns the copy already running rather than starting a new
    /// one. Each is therefore tagged as owned (we started it) or adopted (it was
    /// already there); an adopted application is never hidden and never quit, and
    /// every property Configure() changes on it is put back on shutdown. Without
    /// that, converting one .docx would hide the user's Word and then close it with
    /// DisplayAlerts suppressed, discarding their unsaved work.
    ///
    /// Excel is the extreme case of adoption: DocuLink runs inside Excel, and Excel
    /// registers its class factory in its own process, so activation always returns
    /// the host. That is safe here precisely because it is adopted — but it is why
    /// the Excel branch of ConfigureApplication also suppresses application events,
    /// and why nothing in this class may ever quit an adopted application.
    ///
    /// Applications are launched at most once per batch and reused across files, so
    /// the converter must always be disposed or a hidden WINWORD.EXE is left behind.
    /// </summary>
    internal sealed class OfficeInteropConverter : IDisposable
    {
        // WdExportFormat.wdExportFormatPDF and PpFixedFormatType.ppFixedFormatTypePDF
        // use these documented constants; late binding means they cannot come from
        // the enums themselves.
        private const int WdExportFormatPdf = 17;
        private const int WdExportOptimizeForPrint = 0;
        private const int WdDoNotSaveChanges = 0;
        private const int WdAlertsNone = 0;

        private const int XlTypePdf = 0;
        private const int XlQualityStandard = 0;

        private const int PpFixedFormatTypePdf = 2;
        private const int PpFixedFormatIntentPrint = 2;

        private const int OlSaveAsTypeHtml = 5;
        private const int OlDiscard = 1;

        private const int MsoTrue = -1;
        private const int MsoFalse = 0;

        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

        private readonly Thread _staThread;

        /// <summary>Signalled once <see cref="_marshal"/> has a window handle.</summary>
        private readonly ManualResetEventSlim _pumpReady = new ManualResetEventSlim(false);

        /// <summary>
        /// Hidden window living on the STA thread; posting to it is how work reaches
        /// the message loop. Created on that thread, so its handle belongs to it.
        /// </summary>
        private Control _marshal;

        /// <summary>
        /// One launched or adopted Office application, and whether this converter is
        /// the thing that started it. Only an owned application may be hidden or quit.
        /// </summary>
        private sealed class ApplicationHandle
        {
            public ApplicationHandle(object app, bool owned)
            {
                App = app;
                Owned = owned;
            }

            public object App { get; }

            /// <summary>False when we attached to a copy the user already had open.</summary>
            public bool Owned { get; }

            /// <summary>
            /// Property name to the value it held before we changed it. Only populated
            /// for adopted applications — an owned one is quit, so there is nothing to
            /// put back.
            /// </summary>
            public Dictionary<string, object> SavedSettings { get; } =
                new Dictionary<string, object>(StringComparer.Ordinal);
        }

        /// <summary>Touched only on the STA thread.</summary>
        private readonly Dictionary<ConversionEngine, ApplicationHandle> _applications =
            new Dictionary<ConversionEngine, ApplicationHandle>();

        private volatile bool _disposed;

        public OfficeInteropConverter()
        {
            _staThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "DocuLink Office Automation",
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

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

        /// <summary>
        /// True when the ProgID an engine needs is registered on this machine.
        /// Registry-only, so safe to call from any thread.
        /// </summary>
        public static bool IsAvailable(ConversionEngine engine)
        {
            string progId = GetProgId(engine);
            return progId != null && Type.GetTypeFromProgID(progId) != null;
        }

        /// <summary>
        /// Converts a source document to PDF at <paramref name="outputPdfPath"/>,
        /// marshalling the work onto this converter's STA thread.
        ///
        /// Outlook is the exception: it has no PDF export, so a .msg is saved as HTML
        /// at <paramref name="htmlPathForMessages"/> and that path is returned for the
        /// caller to render. All other engines return null.
        /// </summary>
        public Task<string> ConvertAsync(
            ConversionEngine engine,
            string sourcePath,
            string outputPdfPath,
            string htmlPathForMessages)
        {
            return RunOnStaThread(() =>
            {
                switch (engine)
                {
                    case ConversionEngine.Word:
                        ConvertWithWord(sourcePath, outputPdfPath);
                        return null;

                    case ConversionEngine.Excel:
                        ConvertWithExcel(sourcePath, outputPdfPath);
                        return null;

                    case ConversionEngine.PowerPoint:
                        ConvertWithPowerPoint(sourcePath, outputPdfPath);
                        return null;

                    case ConversionEngine.Outlook:
                        ConvertMessageToHtml(sourcePath, htmlPathForMessages);
                        return htmlPathForMessages;

                    default:
                        throw new NotSupportedException($"'{engine}' is not an Office conversion engine.");
                }
            });
        }

        // ── STA thread plumbing ───────────────────────────────────────────────

        /// <summary>
        /// Body of the STA thread: create the marshalling window, then pump messages
        /// until <see cref="StopThread"/> asks the loop to exit.
        /// </summary>
        private void RunMessageLoop()
        {
            try
            {
                _marshal = new Control();

                // Forces handle creation on this thread — BeginInvoke needs a handle,
                // and the handle's owning thread is the one that pumps for it.
                IntPtr unused = _marshal.Handle;
                GC.KeepAlive(unused);

                _pumpReady.Set();

                System.Windows.Forms.Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Office automation thread stopped: {ex}");
            }
            finally
            {
                // Released so a caller blocked in RunOnStaThread fails fast rather
                // than waiting out StartupTimeout on a thread that already died.
                // Guarded: StopThread disposes the event once it has joined, and a
                // Join that timed out leaves this racing against that dispose.
                try { _pumpReady.Set(); } catch (ObjectDisposedException) { }

                try { _marshal?.Dispose(); }
                catch (Exception ex) { DocuLinkLog.Trace($"Office automation window dispose failed: {ex.Message}"); }
            }
        }

        private Task<T> RunOnStaThread<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>();

            if (_disposed)
            {
                completion.SetException(new ObjectDisposedException(nameof(OfficeInteropConverter)));
                return completion.Task;
            }

            Post(
                () =>
                {
                    try { completion.TrySetResult(work()); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                },
                onCannotPost: () => completion.TrySetException(
                    new ObjectDisposedException(nameof(OfficeInteropConverter))));

            return completion.Task;
        }

        /// <summary>
        /// Queues an action onto the STA thread's message loop. Returns false, and
        /// runs <paramref name="onCannotPost"/>, when the loop is gone.
        /// </summary>
        private bool Post(Action work, Action onCannotPost)
        {
            try
            {
                if (!_pumpReady.Wait(StartupTimeout))
                {
                    DocuLinkLog.Trace("Office automation thread did not start within the timeout.");
                    onCannotPost?.Invoke();
                    return false;
                }
            }
            catch (ObjectDisposedException)
            {
                onCannotPost?.Invoke();
                return false;
            }

            Control marshal = _marshal;
            if (marshal == null || marshal.IsDisposed || !marshal.IsHandleCreated)
            {
                onCannotPost?.Invoke();
                return false;
            }

            try
            {
                marshal.BeginInvoke(work);
                return true;
            }
            catch (Exception ex)
            {
                // Handle destroyed between the check above and the post.
                DocuLinkLog.Trace($"Could not post to the Office automation thread: {ex.Message}");
                onCannotPost?.Invoke();
                return false;
            }
        }

        // ── Conversions (STA thread only) ─────────────────────────────────────

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
                if ((object)document != null)
                {
                    try { document.Close(WdDoNotSaveChanges); }
                    catch (Exception ex) { DocuLinkLog.Trace($"Could not close the converted document: {ex.Message}"); }
                }

                Release((object)document);
                Release((object)documents);
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
                if ((object)workbook != null)
                {
                    try { workbook.Close(false); }
                    catch (Exception ex) { DocuLinkLog.Trace($"Could not close the converted workbook: {ex.Message}"); }
                }

                Release((object)workbook);
                Release((object)workbooks);
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
                if ((object)presentation != null)
                {
                    try { presentation.Close(); }
                    catch (Exception ex) { DocuLinkLog.Trace($"Could not close the converted presentation: {ex.Message}"); }
                }

                Release((object)presentation);
                Release((object)presentations);
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
                if ((object)item != null)
                {
                    try { item.Close(OlDiscard); }
                    catch (Exception ex) { DocuLinkLog.Trace($"Could not close the converted message: {ex.Message}"); }
                }

                Release((object)item);
                Release((object)session);
            }

            if (!File.Exists(outputHtmlPath))
                throw new IOException("Outlook did not produce an HTML file for the message.");
        }

        /// <summary>
        /// Returns the cached application for an engine, attaching to a running copy
        /// or launching one on first use.
        /// STA thread only — the returned RCW is bound to that thread's COM context.
        /// </summary>
        private dynamic GetApplication(ConversionEngine engine)
        {
            if (_applications.TryGetValue(engine, out ApplicationHandle cached))
                return cached.App;

            string progId = GetProgId(engine);
            Type type = progId == null ? null : Type.GetTypeFromProgID(progId);

            if (type == null)
                throw new InvalidOperationException(
                    $"{GetApplicationName(engine)} is not installed, so this file type cannot be converted.");

            // These are single-instance applications: CoCreateInstance returns the copy
            // already running rather than starting a new one. Anything we do to an
            // adopted instance lands on the user's session, so ownership has to be
            // established before it is configured — an adopted app is never hidden and
            // never quit, and its settings are restored on shutdown.
            ApplicationHandle handle = AttachToRunningApplication(progId)
                ?? LaunchApplication(engine, type);

            // Excel can never be ours. DocuLink is loaded into it, so activation
            // returns the host no matter what the running object table said — and
            // Excel registers there lazily, so a probe miss would otherwise mark the
            // user's own Excel as ours to hide, reconfigure and quit.
            if (engine == ConversionEngine.Excel && handle.Owned)
                handle = new ApplicationHandle(handle.App, owned: false);

            ConfigureApplication(engine, handle);
            _applications[engine] = handle;
            return handle.App;
        }

        /// <summary>
        /// Returns a handle to an already-running application, or null when none is
        /// registered in the running object table.
        /// </summary>
        private static ApplicationHandle AttachToRunningApplication(string progId)
        {
            try
            {
                object running = Marshal.GetActiveObject(progId);
                if (running == null)
                    return null;

                DocuLinkLog.Trace($"Attached to a running '{progId}'; it will not be quit on shutdown.");
                return new ApplicationHandle(running, owned: false);
            }
            catch (COMException)
            {
                // MK_E_UNAVAILABLE — nothing registered, so we get to start our own.
                return null;
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Could not check for a running '{progId}': {ex.Message}");
                return null;
            }
        }

        private static ApplicationHandle LaunchApplication(ConversionEngine engine, Type type)
        {
            try
            {
                return new ApplicationHandle(Activator.CreateInstance(type), owned: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not start {GetApplicationName(engine)}: {ex.Message}", ex);
            }
        }

        private static void ConfigureApplication(ConversionEngine engine, ApplicationHandle handle)
        {
            switch (engine)
            {
                case ConversionEngine.Word:
                    // Only an instance we started may be hidden. Hiding one the user
                    // is working in makes their Word vanish with no way back.
                    if (handle.Owned)
                        Configure(handle, "Visible", false);

                    Configure(handle, "DisplayAlerts", WdAlertsNone);
                    break;

                case ConversionEngine.Excel:
                    if (handle.Owned)
                        Configure(handle, "Visible", false);

                    Configure(handle, "DisplayAlerts", false);
                    Configure(handle, "AskToUpdateLinks", false);
                    Configure(handle, "ScreenUpdating", false);

                    // Excel is adopted in practice — DocuLink runs inside it — so this
                    // suppresses the add-in's own WorkbookOpen/Activate handlers while
                    // a workbook is opened purely to be exported. Saved and restored
                    // like everything else, or the host would be left deaf to events.
                    Configure(handle, "EnableEvents", false);
                    break;

                case ConversionEngine.PowerPoint:
                    // PowerPoint rejects Visible = false on several builds and throws;
                    // presentations are opened WithWindow:=msoFalse instead.
                    break;
            }
        }

        /// <summary>
        /// Sets one application property, remembering the previous value first when the
        /// application is one we adopted rather than started.
        /// </summary>
        private static void Configure(ApplicationHandle handle, string property, object value)
        {
            try
            {
                if (!handle.Owned && !handle.SavedSettings.ContainsKey(property))
                    handle.SavedSettings[property] = GetProperty(handle.App, property);

                SetProperty(handle.App, property, value);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Could not set '{property}': {ex.Message}");
            }
        }

        /// <summary>Puts back everything <see cref="Configure"/> changed on an adopted application.</summary>
        private static void RestoreSettings(ConversionEngine engine, ApplicationHandle handle)
        {
            foreach (var setting in handle.SavedSettings)
            {
                try
                {
                    SetProperty(handle.App, setting.Key, setting.Value);
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace(
                        $"Could not restore '{setting.Key}' on {GetApplicationName(engine)}: {ex.Message}");
                }
            }

            handle.SavedSettings.Clear();
        }

        // Reflection rather than 'dynamic' on purpose: InvokeMember goes straight to
        // IDispatch without the DLR building a BindingRestriction over the RCW. See
        // the remarks on Release for why that distinction matters here.
        private static object GetProperty(object app, string name)
        {
            return app.GetType().InvokeMember(
                name, BindingFlags.GetProperty, null, app, null);
        }

        private static void SetProperty(object app, string name, object value)
        {
            app.GetType().InvokeMember(
                name, BindingFlags.SetProperty, null, app, new[] { value });
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

        /// <summary>
        /// Drops one RCW. Always call this as <c>Release((object)x)</c>, never
        /// <c>Release(x)</c>, when x is declared <c>dynamic</c>.
        ///
        /// C# dispatches any call with a <c>dynamic</c> argument through the DLR —
        /// even a static method in this same class. Binding that call site builds a
        /// <c>BindingRestrictions.InstanceRestriction</c>, which takes a
        /// <c>WeakReference</c> to the instance, and that touches the RCW. In a
        /// cleanup block the underlying COM object has just been closed, so the touch
        /// throws COMException 0x80010114 ("The requested object does not exist")
        /// straight out of the <c>finally</c> — failing a conversion whose PDF was
        /// already written, and raising the 'DisconnectedContext' MDA on the way past.
        ///
        /// The cast makes the call statically bound: no binder, no WeakReference, no
        /// context transition. The same rule applies to null checks — use
        /// <c>(object)x != null</c>, since <c>x != null</c> on a dynamic operand is
        /// also a DLR operation.
        /// </summary>
        private static void Release(object comObject)
        {
            if (comObject == null) return;

            try
            {
                if (Marshal.IsComObject(comObject))
                    Marshal.ReleaseComObject(comObject);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Could not release a COM object: {ex.Message}");
            }
        }

        /// <summary>
        /// Quits the applications this converter started, hands back the ones it
        /// borrowed, and releases every RCW. STA thread only.
        /// </summary>
        private void ShutdownApplications()
        {
            foreach (var pair in _applications)
            {
                ApplicationHandle handle = pair.Value;
                dynamic app = handle.App;

                // Put back anything we changed on an application the user owns,
                // before we let go of it.
                RestoreSettings(pair.Key, handle);

                // Reasons to leave an application running: we attached to one that was
                // already open, or it is Outlook (commonly running for the user, and
                // Quit() would close their mail client) or Excel (we are running inside
                // it, so activation can only ever have returned the host — the ownership
                // flag is not trusted here because Excel registers in the running object
                // table lazily, and a missed probe would mark the host as ours to quit).
                // Either way it is released without quitting.
                bool mayQuit = handle.Owned
                    && pair.Key != ConversionEngine.Outlook
                    && pair.Key != ConversionEngine.Excel;

                if (mayQuit)
                {
                    try
                    {
                        if (pair.Key == ConversionEngine.Word)
                            app.Quit(WdDoNotSaveChanges);
                        else
                            app.Quit();
                    }
                    catch (Exception ex)
                    {
                        DocuLinkLog.Trace($"Could not quit {GetApplicationName(pair.Key)}: {ex.Message}");
                    }
                }

                Release(handle.App);
            }

            _applications.Clear();
        }

        /// <summary>
        /// Quits every launched application and stops the STA thread.
        ///
        /// Prefer this over <see cref="Dispose"/> when a caller is already on the UI
        /// thread inside an async method: quitting Office takes seconds, and awaiting
        /// keeps the message pump alive instead of blocking it.
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await RunOnStaThreadIgnoringDisposal(() =>
                {
                    ShutdownApplications();
                    return true;
                });
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Office automation shutdown failed: {ex}");
            }

            StopThread();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Shutdown must run on the STA thread that created the RCWs, for the
            // same reason the conversions do.
            using (var shutdownComplete = new ManualResetEventSlim(false))
            {
                bool posted = Post(
                    () =>
                    {
                        try { ShutdownApplications(); }
                        catch (Exception ex) { DocuLinkLog.Trace($"OfficeInteropConverter dispose failed: {ex}"); }
                        finally
                        {
                            // Guarded: if the Wait below times out, the using block
                            // disposes the event while this action is still running.
                            try { shutdownComplete.Set(); } catch (ObjectDisposedException) { }
                        }
                    },
                    onCannotPost: null);

                // Blocks the calling thread — that is why ShutdownAsync exists and is
                // what DocumentConversionService uses from the UI thread.
                if (posted)
                    shutdownComplete.Wait(ShutdownTimeout);
            }

            StopThread();
        }

        /// <summary>Queues shutdown work after <c>_disposed</c> is already set.</summary>
        private Task<bool> RunOnStaThreadIgnoringDisposal(Func<bool> work)
        {
            var completion = new TaskCompletionSource<bool>();

            Post(
                () =>
                {
                    try { completion.TrySetResult(work()); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                },
                onCannotPost: () => completion.TrySetResult(false));

            return completion.Task;
        }

        /// <summary>Ends the message loop and waits for the STA thread to unwind.</summary>
        private void StopThread()
        {
            try
            {
                // ExitThread must be called on the thread whose loop is ending.
                Post(() => System.Windows.Forms.Application.ExitThread(), onCannotPost: null);

                _staThread.Join(ShutdownTimeout);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Office automation thread did not stop cleanly: {ex}");
            }
            finally
            {
                try { _pumpReady.Dispose(); } catch { }
            }
        }
    }
}
