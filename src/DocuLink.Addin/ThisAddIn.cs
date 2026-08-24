using System;

using System.Collections.Generic;

using System.Runtime.InteropServices;

using System.Threading.Tasks;

using System.Windows.Forms;

using Excel = Microsoft.Office.Interop.Excel;

using Office = Microsoft.Office.Core;

using DocuLink.Addin.Ribbon;

using DocuLink.Addin.Modules.CustomXml;

using DocuLink.Addin.Modules.CustomXml.Models;

using DocuLink.Addin.Modules.Services;

using DocuLink.Addin.Modules.UI;

using DocuLink.Addin.Modules.WebView;

using DocuLink.Addin.Properties;



namespace DocuLink.Addin

{

    public partial class ThisAddIn

    {

        // One entry per open workbook; created on demand when the user opens the task pane.

        private readonly List<WorkbookPaneEntry> _workbookPanes = new List<WorkbookPaneEntry>();

        private FileManagerHost _fileManagerWindow;

        private DocumentMatcherHost _matcherWindow;

        private ViewerWindowHost _viewerWindow;

        private readonly Dictionary<string, WorkbookStorageSession> _storageSessions =
            new Dictionary<string, WorkbookStorageSession>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<string, string>> _transientPdfGeometry =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Per-workbook link-creation undo history. Keyed and released exactly like
        /// <see cref="_storageSessions"/>, so undo dies with the workbook and is never persisted.
        /// </summary>
        private readonly Dictionary<string, Modules.Services.LinkCreationUndoStack> _linkUndoStacks =
            new Dictionary<string, Modules.Services.LinkCreationUndoStack>(StringComparer.OrdinalIgnoreCase);

        private Modules.Infrastructure.ExcelUndoKeyHook _excelUndoKeyHook;

        /// <summary>
        /// Excel-grid navigation state for keystrokes forwarded from the viewer. Single
        /// instance because Excel itself tracks only one entry anchor at a time.
        /// </summary>
        private readonly Modules.Services.ExcelCellNavigationService _cellNavigation =
            new Modules.Services.ExcelCellNavigationService();

        internal Modules.Services.ExcelCellNavigationService CellNavigation => _cellNavigation;



        /// <summary>

        /// Set to <c>true</c> before making a programmatic cell selection (e.g.

        /// when navigating from a clicked PDF rectangle to its linked cell) so the

        /// resulting <see cref="Application_SheetSelectionChange"/> event is skipped

        /// and does not bounce a redundant navigate-to-rectangle message back.

        /// The handler resets this flag after reading it.

        /// </summary>

        internal bool SuppressNextSelectionNav { get; set; }

        private int _suppressSelectionNavDepth;

        /// <summary>
        /// When &gt; 0, <see cref="Application_SheetSelectionChange"/> skips viewer
        /// navigation. Used during bulk link deletion so unbind/repaint events do
        /// not post redundant navigate-to-rectangle messages.
        /// </summary>
        internal bool IsSelectionNavSuppressed => _suppressSelectionNavDepth > 0;

        internal SelectionNavSuppressScope EnterSelectionNavSuppress() =>
            new SelectionNavSuppressScope(this);

        internal sealed class SelectionNavSuppressScope : IDisposable
        {
            private readonly ThisAddIn _addIn;

            internal SelectionNavSuppressScope(ThisAddIn addIn)
            {
                _addIn = addIn;
                _addIn._suppressSelectionNavDepth++;
            }

            public void Dispose()
            {
                _addIn._suppressSelectionNavDepth--;
            }
        }

        internal bool IsViewerPoppedOut =>

            _viewerWindow != null && !_viewerWindow.IsDisposed && _viewerWindow.Visible;

        /// <summary>
        /// Controls whether linked-cell selection opens the task-pane viewer. This is
        /// intentionally session-only and resets to enabled each time the add-in starts.
        /// </summary>
        internal bool AutoOpenViewerOnCellClick { get; set; } = true;



        internal bool IsTaskPaneViewerVisible()

        {

            if (IsViewerPoppedOut) return false;

            var entry = FindEntryForActiveWorkbook();

            return entry != null && entry.Pane.Visible;

        }



        internal WorkbookStorageSession GetStorageSession(Excel.Workbook workbook)

        {

            if (workbook == null) throw new ArgumentNullException(nameof(workbook));

            string key = GetWorkbookSessionKey(workbook);

            if (!_storageSessions.TryGetValue(key, out WorkbookStorageSession session))

            {

                session = new WorkbookStorageSession(workbook);

                _storageSessions[key] = session;

            }

            return session;

        }



        internal void ReleaseStorageSession(Excel.Workbook workbook)

        {

            if (workbook == null) return;

            string key = GetWorkbookSessionKey(workbook);
            _storageSessions.Remove(key);
            _transientPdfGeometry.Remove(key);
            _linkUndoStacks.Remove(key);
            RefreshExcelUndoArmedState();

        }

        /// <summary>
        /// Returns the workbook's in-memory link-creation undo stack, creating it on first use.
        /// </summary>
        internal Modules.Services.LinkCreationUndoStack GetLinkUndoStack(Excel.Workbook workbook)
        {
            if (workbook == null) return null;

            string key = GetWorkbookSessionKey(workbook);

            if (!_linkUndoStacks.TryGetValue(key, out Modules.Services.LinkCreationUndoStack stack))
            {
                stack = new Modules.Services.LinkCreationUndoStack();
                _linkUndoStacks[key] = stack;
            }

            return stack;
        }

        /// <summary>
        /// Looks up a workbook's undo stack without creating one, so callers that only want to
        /// inspect state do not populate the dictionary for every workbook Excel touches.
        /// </summary>
        private Modules.Services.LinkCreationUndoStack TryGetLinkUndoStack(Excel.Workbook workbook)
        {
            if (workbook == null) return null;

            return _linkUndoStacks.TryGetValue(
                GetWorkbookSessionKey(workbook), out Modules.Services.LinkCreationUndoStack stack)
                ? stack
                : null;
        }

        /// <summary>
        /// Marks a link-rectangle creation as the most recent action, so the next Ctrl+Z on the
        /// worksheet grid reaches DocuLink instead of Excel's own undo.
        /// </summary>
        internal void ArmExcelUndoForLinkCreation() => RefreshExcelUndoArmedState();

        /// <summary>
        /// Points the grid's Ctrl+Z at DocuLink only when the workbook the user is actually
        /// looking at has undoable history that is still the most recent thing to happen in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Recomputed from the active workbook rather than latched, because arming is per workbook
        /// while the hook is one global switch. Creating a rectangle in one workbook must not leave
        /// the hook armed over another workbook's empty stack, and returning to the first workbook
        /// must restore its pending undo.
        /// </para>
        /// <para>
        /// Resolving the active workbook here, on the UI thread, is also what keeps COM calls out
        /// of the keyboard hook procedure, where Excel may refuse them mid-message.
        /// </para>
        /// </remarks>
        private void RefreshExcelUndoArmedState()
        {
            if (_excelUndoKeyHook == null) return;

            bool armed = false;

            try
            {
                Modules.Services.LinkCreationUndoStack stack =
                    TryGetLinkUndoStack(Application?.ActiveWorkbook);

                armed = stack != null && stack.IsArmed && !stack.IsEmpty;
            }
            catch (Exception ex)
            {
                Modules.DocuLinkLog.Trace($"RefreshExcelUndoArmedState failed: {ex.Message}");
            }

            if (armed)
                _excelUndoKeyHook.Arm();
            else
                _excelUndoKeyHook.Disarm();
        }

        /// <summary>
        /// Runs one step of link-creation undo and re-arms the grid keystroke when more history
        /// remains, so repeated Ctrl+Z in Excel walks back through the stack the way it does in
        /// the viewer.
        /// </summary>
        internal string UndoLastLinkCreation()
        {
            Excel.Workbook wb = Application?.ActiveWorkbook;
            if (wb == null) return null;

            string removedId = null;

            try
            {
                removedId = new Modules.Services.UndoLinkCreationService().UndoLast(wb);
            }
            catch (Exception ex)
            {
                Modules.DocuLinkLog.Trace($"UndoLastLinkCreation failed: {ex.Message}");
            }

            // Undoing is itself a DocuLink action, so re-arm explicitly rather than relying on the
            // flag having survived: the reversal's own cell writes, and the pop that consumed the
            // entry, both leave it stale. Without this the chain stops after one Ctrl+Z.
            Modules.Services.LinkCreationUndoStack stack = TryGetLinkUndoStack(wb);
            if (removedId != null)
                stack?.Arm();
            else
                stack?.Disarm();

            RefreshExcelUndoArmedState();

            if (removedId != null)
            {
                _cellNavigation.ResetAnchor();
                GetActiveViewerHost()?.SendLinkRectanglesRemoved(new List<string> { removedId });
                NotifyFileManagerLinksChanged();
            }

            return removedId;
        }

        internal bool TryGetTransientPdfGeometry(Excel.Workbook workbook, string pdfId, out string geometryBase64)
        {
            geometryBase64 = null;
            if (workbook == null || string.IsNullOrWhiteSpace(pdfId)) return false;

            string key = GetWorkbookSessionKey(workbook);
            return _transientPdfGeometry.TryGetValue(key, out var byPdf)
                && byPdf.TryGetValue(pdfId, out geometryBase64)
                && !string.IsNullOrWhiteSpace(geometryBase64);
        }

        internal void StoreTransientPdfGeometry(Excel.Workbook workbook, string pdfId, string geometryBase64)
        {
            if (workbook == null || string.IsNullOrWhiteSpace(pdfId) || string.IsNullOrWhiteSpace(geometryBase64))
                return;

            string key = GetWorkbookSessionKey(workbook);
            if (!_transientPdfGeometry.TryGetValue(key, out var byPdf))
            {
                byPdf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _transientPdfGeometry[key] = byPdf;
            }

            byPdf[pdfId] = geometryBase64;
        }



        private static string GetWorkbookDebugName(Excel.Workbook workbook)

        {

            if (workbook == null) return "(null)";

            try

            {

                if (!string.IsNullOrEmpty(workbook.FullName))

                    return workbook.FullName;

            }

            catch (COMException ex)

            {

                Modules.DocuLinkLog.Trace($"FullName unavailable: {ex.Message}");

            }

            try

            {

                return workbook.Name ?? "(unnamed)";

            }

            catch (COMException ex)

            {

                Modules.DocuLinkLog.Trace($"Name unavailable: {ex.Message}");

                return "(workbook COM unavailable)";

            }

        }



        private static string GetWorkbookSessionKey(Excel.Workbook workbook)

        {

            try

            {

                if (!string.IsNullOrEmpty(workbook.FullName))

                    return workbook.FullName;

            }

            catch (COMException) { }

            IntPtr unknown = Marshal.GetIUnknownForObject(workbook);
            try
            {
                return "unsaved:" + ((IntPtr)Marshal.GetUniqueObjectForIUnknown(unknown)).ToInt64().ToString();
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }



        internal void ShowTaskPane()

        {

            if (IsViewerPoppedOut)
                _viewerWindow.Hide();



            var entry = EnsureTaskPaneForActiveWorkbook();

            if (entry == null) return;

            entry.Pane.Visible = true;

            entry.Host.NotifyViewerShown();

        }



        internal void ShowViewerWindow()

        {

            if (_viewerWindow == null || _viewerWindow.IsDisposed)

                _viewerWindow = new ViewerWindowHost();



            HideAllTaskPanes();

            _viewerWindow.Show();

            _viewerWindow.BringToFront();

            _viewerWindow.NotifyViewerShown();

        }



        /// <summary>

        /// Pushes the current workbook's PDFs and linked rectangles to the active

        /// viewer surface. No-ops if no viewer is open yet.

        /// </summary>

        internal void RefreshTaskPanePdfs()

        {

            GetActiveViewerHost()?.RefreshDataIfReady();

        }



        /// <summary>

        /// Pushes updated bytes for a single PDF to the active viewer surface.

        /// Used after OCR so an already-loaded document is refreshed immediately.

        /// </summary>

        internal void RefreshTaskPanePdf(string pdfId)

        {

            GetActiveViewerHost()?.SendPdfUpdated(pdfId);

        }

        internal void NotifyViewerPdfAdded(string pdfId)

        {

            GetActiveViewerHost()?.SendPdfAdded(pdfId);

        }

        internal void NotifyViewerPdfRenamed(string id, string name)

        {

            GetActiveViewerHost()?.SendPdfNameUpdated(id, name);

        }

        internal void NotifyViewerPdfRemoved(string id)

        {

            GetActiveViewerHost()?.SendPdfRemoved(id);

        }

        /// <summary>

        /// Switches an open viewer surface to a specific PDF. Raised when the user

        /// selects a document in the file manager. No-ops when no viewer is open —

        /// the file manager is usable on its own and must not force one open.

        /// </summary>

        internal void NotifyViewerShowPdf(string pdfId)

        {

            GetActiveViewerHost()?.SendShowPdf(pdfId);

        }



        /// <summary>

        /// Pushes the current folder catalogue and PDF folder assignments to the active viewer.

        /// Called after file-manager folder or move operations so the viewer's folder filter stays current.

        /// </summary>

        internal void NotifyViewerFoldersChanged()

        {

            GetActiveViewerHost()?.SendFoldersToWebView();

        }



        internal void ShowManageFilesWindow()

        {

            if (_fileManagerWindow == null || _fileManagerWindow.IsDisposed)

                _fileManagerWindow = new FileManagerHost();



            _fileManagerWindow.Show();

            _fileManagerWindow.BringToFront();

            _fileManagerWindow.RefreshDataIfReady();

        }



        internal void ShowDocumentMatcherWindow()

        {

            if (_matcherWindow == null || _matcherWindow.IsDisposed)

                _matcherWindow = new DocumentMatcherHost();

            else

                _matcherWindow.Reset();

            _matcherWindow.Show();

            _matcherWindow.BringToFront();

        }



        internal void NotifyFileManagerLinksChanged()
        {
            _fileManagerWindow?.RefreshDataIfReady();
        }



        internal TaskPaneHost TaskPaneHost => FindEntryForActiveWorkbook()?.Host;



        internal IDocumentViewerHost GetActiveViewerHost()

        {

            if (IsViewerPoppedOut)

                return _viewerWindow;

            return FindEntryForActiveWorkbook()?.Host;

        }



        /// <summary>

        /// Ensures a task pane exists for the active workbook (eager-load / pre-warm).

        /// </summary>

        internal void EnsureTaskPaneCreated()

        {

            EnsureTaskPaneForActiveWorkbook();

        }



        internal void PreloadFileManagerWindow()

        {

            _fileManagerWindow = new FileManagerHost();

            _ = _fileManagerWindow.Handle;

        }



        internal void PreloadViewerWindow()

        {

            _viewerWindow = new ViewerWindowHost();

            _ = _viewerWindow.Handle;

        }



        internal void PreloadMatcherWindow()

        {

            _matcherWindow = new DocumentMatcherHost();

            _ = _matcherWindow.Handle;

        }

        internal void CloseAllApplicationWindows()
        {
            if (_fileManagerWindow != null && !_fileManagerWindow.IsDisposed)
                _fileManagerWindow.Close();
            if (_viewerWindow != null && !_viewerWindow.IsDisposed)
                _viewerWindow.Close();
            if (_matcherWindow != null && !_matcherWindow.IsDisposed)
                _matcherWindow.Close();
        }



        // ── Per-workbook task pane management ────────────────────────────────



        private WorkbookPaneEntry EnsureTaskPaneForActiveWorkbook()

        {

            // Clear out anything left by a workbook that has since closed, so a stale entry
            // cannot cause a second pane to be built for the active workbook.
            ReconcileClosedWorkbooks();

            Excel.Workbook wb = Application?.ActiveWorkbook;

            if (wb == null) return null;



            var entry = FindEntryFor(wb);

            if (entry != null) return entry;



            var host = new TaskPaneHost();

            // Passing the active window scopes the pane to this workbook's window,

            // so Excel shows/hides it automatically when the user switches workbooks.

            var pane = CustomTaskPanes.Add(host, "DocuLink", Application.ActiveWindow);

            pane.DockPosition = Office.MsoCTPDockPosition.msoCTPDockPositionRight;

            pane.Width = 640;



            pane.VisibleChanged += (_, __) =>

            {

                if (IsViewerPoppedOut && pane.Visible)

                    pane.Visible = false;

            };



            entry = new WorkbookPaneEntry(wb, pane, host);

            _workbookPanes.Add(entry);

            return entry;

        }



        private void HideAllTaskPanes()

        {

            foreach (var entry in _workbookPanes)

            {

                try

                {

                    if (entry.Pane.Visible)

                        entry.Pane.Visible = false;

                }

                catch (Exception ex)

                {

                    System.Diagnostics.Debug.WriteLine(

                        $"[DocuLink] HideAllTaskPanes failed: {ex.Message}");

                }

            }

        }



        private WorkbookPaneEntry FindEntryForActiveWorkbook()

        {

            Excel.Workbook wb = Application?.ActiveWorkbook;

            return wb == null ? null : FindEntryFor(wb);

        }



        /// <summary>

        /// Finds the pane entry for a specific workbook using COM identity comparison

        /// so that multiple RCW wrappers for the same COM object resolve correctly.

        /// </summary>

        private WorkbookPaneEntry FindEntryFor(Excel.Workbook wb)

        {

            if (wb == null) return null;



            IntPtr target = IntPtr.Zero;

            try

            {

                target = Marshal.GetIUnknownForObject(wb);

                foreach (var entry in _workbookPanes)

                {

                    IntPtr candidate = IntPtr.Zero;

                    try

                    {

                        candidate = Marshal.GetIUnknownForObject(entry.Workbook);

                        if (candidate == target) return entry;

                    }

                    catch (Exception ex)

                    {

                        // A workbook that closed leaves an unusable wrapper behind, and entries
                        // now survive until ReconcileClosedWorkbooks sweeps them. Skip rather
                        // than let a dead entry break the lookup for a live workbook.
                        Modules.DocuLinkLog.Trace($"FindEntryFor skipping unusable entry: {ex.Message}");

                    }

                    finally

                    {

                        if (candidate != IntPtr.Zero) Marshal.Release(candidate);

                    }

                }

            }

            finally

            {

                if (target != IntPtr.Zero) Marshal.Release(target);

            }



            return null;

        }



        // ── Event handlers ────────────────────────────────────────────────────



        private void ThisAddIn_Startup(object sender, System.EventArgs e)

        {

            Modules.DocuLinkLog.Clear();

            Modules.DocuLinkLog.Trace("addin startup");

            WebViewEagerLoader.Initialize(this);

            Application.SheetSelectionChange += Application_SheetSelectionChange;

            Application.SheetChange += Application_SheetChange;

            Application.WorkbookBeforeClose += Application_WorkbookBeforeClose;

            Application.WorkbookBeforeSave += Application_WorkbookBeforeSave;

            Application.WorkbookActivate += Application_WorkbookActivate;

            Application.WorkbookOpen += Application_WorkbookOpen;

            ((Excel.AppEvents_Event)Application).NewWorkbook += Application_NewWorkbook;

            EnsureLinkTracking(Application.ActiveWorkbook);

            _excelUndoKeyHook = new Modules.Infrastructure.ExcelUndoKeyHook(
                () => UndoLastLinkCreation());

            _ = CheckForUpdateOnOpenAsync();

        }



        private void ThisAddIn_Shutdown(object sender, System.EventArgs e)

        {

            Modules.DocuLinkLog.Trace("ENTER");

            DisposeApplicationSurfacesForShutdown();

            _excelUndoKeyHook?.Dispose();

            _excelUndoKeyHook = null;

            Application.SheetSelectionChange -= Application_SheetSelectionChange;

            Application.SheetChange -= Application_SheetChange;

            Application.WorkbookBeforeClose -= Application_WorkbookBeforeClose;

            Application.WorkbookBeforeSave -= Application_WorkbookBeforeSave;

            Application.WorkbookActivate -= Application_WorkbookActivate;

            Application.WorkbookOpen -= Application_WorkbookOpen;

            ((Excel.AppEvents_Event)Application).NewWorkbook -= Application_NewWorkbook;

            Modules.DocuLinkLog.Trace("EXIT");

        }



        private void DisposeApplicationSurfacesForShutdown()

        {

            using (Modules.DocuLinkLog.Time("DisposeApplicationSurfacesForShutdown total"))

            {

                Modules.DocuLinkLog.Trace($"ENTER panes={_workbookPanes.Count} sessions={_storageSessions.Count}");

                foreach (WorkbookPaneEntry entry in _workbookPanes.ToArray())

                {

                    try

                    {

                        Modules.DocuLinkLog.Trace("disposing task pane host");

                        entry.Host?.Dispose();

                    }

                    catch (Exception ex)

                    {

                        Modules.DocuLinkLog.Trace($"task pane host dispose failed: {ex.GetType().FullName}: {ex.Message}");

                    }

                }

                _workbookPanes.Clear();

                try

                {

                    if (_fileManagerWindow != null && !_fileManagerWindow.IsDisposed)

                    {

                        Modules.DocuLinkLog.Trace("disposing file manager window");

                        _fileManagerWindow.Dispose();

                    }

                }

                catch (Exception ex)

                {

                    Modules.DocuLinkLog.Trace($"file manager dispose failed: {ex.GetType().FullName}: {ex.Message}");

                }

                _fileManagerWindow = null;

                try

                {

                    if (_viewerWindow != null && !_viewerWindow.IsDisposed)

                    {

                        Modules.DocuLinkLog.Trace("disposing viewer window");

                        _viewerWindow.Dispose();

                    }

                }

                catch (Exception ex)

                {

                    Modules.DocuLinkLog.Trace($"viewer window dispose failed: {ex.GetType().FullName}: {ex.Message}");

                }

                _viewerWindow = null;

                try

                {

                    if (_matcherWindow != null && !_matcherWindow.IsDisposed)

                    {

                        Modules.DocuLinkLog.Trace("disposing document matcher window");

                        _matcherWindow.Dispose();

                    }

                }

                catch (Exception ex)

                {

                    Modules.DocuLinkLog.Trace($"document matcher window dispose failed: {ex.GetType().FullName}: {ex.Message}");

                }

                _matcherWindow = null;

                _storageSessions.Clear();

                Modules.DocuLinkLog.Trace("EXIT");

            }

        }



        private void Application_WorkbookBeforeClose(Excel.Workbook wb, ref bool cancel)
        {
            Modules.DocuLinkLog.Trace(
                $"ENTER workbook={GetWorkbookDebugName(wb)} cancel={cancel} " +
                $"panes={_workbookPanes.Count} sessions={_storageSessions.Count}");

            // Safe to drop even if the close is cancelled: the session is only a cache over
            // the workbook's Custom XML, and GetStorageSession rebuilds it on next use.
            ReleaseStorageSession(wb);

            // The pane entry is deliberately left alone.
            //
            // This event fires *before* Excel asks about unsaved changes, so the close can
            // still be cancelled — and if the user cancels, the workbook stays open. Removing
            // the entry here left exactly that case broken: the CustomTaskPane was still on
            // screen but no longer in _workbookPanes, so the next Show Task Pane built a
            // second pane for the same workbook and the original host was never disposed.
            //
            // ReconcileClosedWorkbooks handles it instead, by checking which workbooks Excel
            // actually still has open rather than guessing from this event.
            Modules.DocuLinkLog.Trace("EXIT (pane cleanup deferred to reconcile)");
        }

        /// <summary>
        /// Drops pane entries and storage sessions belonging to workbooks Excel no longer has
        /// open, disposing each orphaned host.
        /// </summary>
        /// <remarks>
        /// Driven off the live workbook collection rather than the close event, because
        /// WorkbookBeforeClose cannot tell a real close from one the user is about to cancel.
        /// Called from the workbook lifecycle points that matter — activate, open, and pane
        /// creation — but deliberately not from the selection-change path, which is hot.
        /// </remarks>
        private void ReconcileClosedWorkbooks()
        {
            if (_workbookPanes.Count == 0 && _storageSessions.Count == 0)
                return;

            var liveWorkbooks = new HashSet<IntPtr>();
            var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (Excel.Workbook open in Application.Workbooks)
                {
                    IntPtr unknown = IntPtr.Zero;
                    try
                    {
                        unknown = Marshal.GetIUnknownForObject(open);
                        liveWorkbooks.Add(unknown);
                        liveKeys.Add(GetWorkbookSessionKey(open));
                    }
                    finally
                    {
                        if (unknown != IntPtr.Zero) Marshal.Release(unknown);
                    }
                }
            }
            catch (Exception ex)
            {
                // Excel is busy or mid-teardown. Retry on the next lifecycle event rather than
                // risk disposing a pane whose workbook is in fact still open.
                Modules.DocuLinkLog.Trace($"ReconcileClosedWorkbooks enumeration failed: {ex.Message}");
                return;
            }

            foreach (WorkbookPaneEntry entry in _workbookPanes.ToArray())
            {
                if (IsWorkbookStillOpen(entry.Workbook, liveWorkbooks))
                    continue;

                Modules.DocuLinkLog.Trace("reconcile: removing pane entry for closed workbook");
                _workbookPanes.Remove(entry);

                try
                {
                    entry.Host?.Dispose();
                }
                catch (Exception ex)
                {
                    Modules.DocuLinkLog.Trace(
                        $"reconcile: host dispose failed: {ex.GetType().FullName}: {ex.Message}");
                }
            }

            // Backstop for sessions WorkbookBeforeClose did not catch — a workbook closed
            // without that event, or one whose key changed via Save As while it was open.
            foreach (string key in new List<string>(_storageSessions.Keys))
            {
                if (liveKeys.Contains(key)) continue;
                _storageSessions.Remove(key);
                _transientPdfGeometry.Remove(key);
                _linkUndoStacks.Remove(key);
                Modules.DocuLinkLog.Trace("reconcile: released storage session for closed workbook");
            }
        }

        /// <summary>
        /// COM-identity test against the set of open workbooks. A workbook that has closed
        /// leaves behind an RCW that throws when touched, so failure here means closed too.
        /// </summary>
        private static bool IsWorkbookStillOpen(Excel.Workbook workbook, HashSet<IntPtr> liveWorkbooks)
        {
            if (workbook == null) return false;

            IntPtr unknown = IntPtr.Zero;
            try
            {
                unknown = Marshal.GetIUnknownForObject(workbook);
                return liveWorkbooks.Contains(unknown);
            }
            catch (Exception ex)
            {
                Modules.DocuLinkLog.Trace($"reconcile: workbook handle unusable, treating as closed: {ex.Message}");
                return false;
            }
            finally
            {
                if (unknown != IntPtr.Zero) Marshal.Release(unknown);
            }
        }



        private void Application_WorkbookActivate(Excel.Workbook wb)

        {

            ReconcileClosedWorkbooks();

            EnsureLinkTracking(wb);

            // Pending undo belongs to whichever workbook is in front, so re-evaluate before
            // anything else — including the pop-out early return further down.
            RefreshExcelUndoArmedState();

            try

            {

                if (IsViewerPoppedOut)

                {

                    _viewerWindow.InvalidateData();

                    _viewerWindow.RefreshDataIfReady();

                    return;

                }



                var entry = FindEntryFor(wb);

                if (entry == null) return;



                if (entry.Pane.Visible)

                    entry.Host.RefreshDataIfReady();

            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine(

                    $"[DocuLink] Application_WorkbookActivate refresh failed: {ex.Message}");

            }

        }



        private async void Application_WorkbookOpen(Excel.Workbook wb)

        {

            ReconcileClosedWorkbooks();

            EnsureLinkTracking(wb);

            WarmUpTaskPaneFor(wb);

            await CheckForUpdateOnOpenAsync();

        }

        private async Task CheckForUpdateOnOpenAsync()

        {

            if (AppVersion.Current == "dev") return;

            if ((DateTime.UtcNow - Settings.Default.LastUpdateCheck).TotalHours < 24) return;

            UpdateCheckResult result;

            try { result = await UpdateCheckService.CheckAsync().ConfigureAwait(true); }

            catch { return; }

            if (result?.UpdateAvailable != true) return;

            new UpdateDialog(result).ShowDialog();

        }



        private void Application_NewWorkbook(Excel.Workbook wb)

        {

            EnsureLinkTracking(wb);

            WarmUpTaskPaneFor(wb);

        }



        /// <summary>

        /// Pre-creates the task pane for a workbook invisibly and forces HWND creation

        /// so that WebView2 initialisation starts in the background. By the time the user

        /// clicks "Show Task Pane" the WebView2 environment is already warm.

        /// </summary>

        private void WarmUpTaskPaneFor(Excel.Workbook wb)

        {

            if (wb == null) return;

            try

            {

                var entry = EnsureTaskPaneForActiveWorkbook();

                if (entry != null)

                    _ = entry.Host.Handle;

            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine(

                    $"[DocuLink] WarmUpTaskPaneFor failed: {ex.Message}");

            }

        }

        /// <summary>
        /// Creates filter-safe formula trackers for persisted links and removes legacy
        /// per-cell XML maps. The operation is idempotent and normally becomes a quick
        /// read-only check on subsequent workbook activations.
        /// </summary>
        private void EnsureLinkTracking(Excel.Workbook wb)
        {
            if (wb == null || WorkbookProtectionGuard.IsStructureProtected(wb))
                return;

            try
            {
                WorkbookStorageSession session = GetStorageSession(wb);
                LinkCellTracker.EnsureBindings(wb, session.GetLinks());
            }
            catch (Exception ex)
            {
                Modules.DocuLinkLog.Trace(
                    $"EnsureLinkTracking failed: {ex.GetType().FullName}: {ex.Message}");
            }
        }



        private void Application_WorkbookBeforeSave(Excel.Workbook wb, bool saveAsUi, ref bool cancel)

        {

            string workbookName = GetWorkbookDebugName(wb);

            Modules.DocuLinkLog.Trace($"ENTER workbook={workbookName} saveAsUi={saveAsUi} cancel={cancel}");

            using (Modules.DocuLinkLog.Time("WorkbookBeforeSave total"))

            {

            try

            {

                if (WorkbookProtectionGuard.IsStructureProtected(wb))

                {

                    Modules.DocuLinkLog.Trace("structure protected - skipping SyncAllPositions");

                    return;

                }

                Modules.DocuLinkLog.Trace("calling LinkCellTracker.SyncAllPositions");

                LinkCellTracker.SyncAllPositions(wb);

                Modules.DocuLinkLog.Trace("LinkCellTracker.SyncAllPositions done");

            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine(

                    $"[DocuLink] Application_WorkbookBeforeSave sync failed: {ex.Message}");

                Modules.DocuLinkLog.Trace($"EXCEPTION {ex.GetType().FullName}: {ex.Message}");

            }

            }

            Modules.DocuLinkLog.Trace($"EXIT cancel={cancel}");

        }



        /// <summary>
        /// Any worksheet edit the user makes means a link creation is no longer the most recent
        /// action, so the grid's Ctrl+Z goes back to Excel's own undo.
        /// </summary>
        /// <remarks>
        /// DocuLink's own writes run inside <see cref="EnterSelectionNavSuppress"/>, which is
        /// what distinguishes them from a user edit here — without that test, creating a link
        /// would immediately disarm the undo it just recorded. Only the edited workbook is
        /// disarmed, so typing in one workbook cannot cancel pending undo history in another.
        /// </remarks>
        private void Application_SheetChange(object sh, Excel.Range target)
        {
            if (IsSelectionNavSuppressed) return;

            try
            {
                TryGetLinkUndoStack((sh as Excel.Worksheet)?.Parent as Excel.Workbook)?.Disarm();
            }
            catch (Exception ex)
            {
                Modules.DocuLinkLog.Trace($"Application_SheetChange disarm failed: {ex.Message}");
            }

            RefreshExcelUndoArmedState();
        }

        private void Application_SheetSelectionChange(object sh, Excel.Range target)

        {

            Modules.DocuLinkLog.Trace($"ENTER addr={target?.Address ?? "null"} SuppressNext={SuppressNextSelectionNav} SuppressDepth={_suppressSelectionNavDepth}");

            if (SuppressNextSelectionNav)

            {

                SuppressNextSelectionNav = false;

                Modules.DocuLinkLog.Trace("suppressed (SuppressNext) – return");

                return;

            }

            if (IsSelectionNavSuppressed)

            {

                Modules.DocuLinkLog.Trace("suppressed (depth) – return");

                return;

            }



            try

            {

                Excel.Workbook wb = Application?.ActiveWorkbook;

                if (wb == null) return;



                WorkbookStorageSession session = GetStorageSession(wb);

                IList<LinkSelectionEntry> selectedLinks = BuildLinkSelection(session, target, out LinkedRectangle rect);



                // Always publish the selection so the viewer can show or hide its panel.

                GetActiveViewerHost()?.SendLinkSelectionChanged(selectedLinks);



                if (rect == null)

                {

                    GetActiveViewerHost()?.SendClearRectangleHighlight();

                    return;

                }



                if (AutoOpenViewerOnCellClick

                    && !IsViewerPoppedOut

                    && !IsTaskPaneViewerVisible())

                {

                    ShowTaskPane();

                }



                var viewer = GetActiveViewerHost();

                if (viewer == null) return;



                viewer.SendNavigateToRectangle(rect.Id, rect.PdfId, rect.Rectangle.PageIndex);

            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine(

                    $"[DocuLink] Application_SheetSelectionChange failed: {ex.Message}");

            }

        }



        /// <summary>

        /// Publishes the link selection for <paramref name="target"/> without navigating.

        /// Used when the viewer itself drives the Excel selection (a rectangle click), where

        /// navigation is suppressed but the panel still has to reflect the new cell — a Sum

        /// cell backed by several rectangles opens the panel just as a multi-cell drag does.

        /// </summary>

        internal void PublishLinkSelection(Excel.Range target)

        {

            if (target == null) return;



            try

            {

                Excel.Workbook wb = Application?.ActiveWorkbook;

                if (wb == null) return;



                IList<LinkSelectionEntry> entries =

                    BuildLinkSelection(GetStorageSession(wb), target, out _);

                GetActiveViewerHost()?.SendLinkSelectionChanged(entries);

            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine(

                    $"[DocuLink] PublishLinkSelection failed: {ex.Message}");

            }

        }



        /// <summary>

        /// Maps the linked rectangles inside <paramref name="target"/> to viewer payload entries

        /// and reports the first one via <paramref name="firstRect"/> for navigation.

        /// Entries are only populated once the selection covers at least two rectangles, which

        /// covers both several linked cells and a single Sum cell built from several rectangles;

        /// a lone rectangle is handled by navigation alone and the panel stays hidden.

        /// </summary>

        private IList<LinkSelectionEntry> BuildLinkSelection(

            WorkbookStorageSession session,

            Excel.Range target,

            out LinkedRectangle firstRect)

        {

            firstRect = null;

            var entries = new List<LinkSelectionEntry>();



            IList<LinkCellResolver.SelectedLink> selected =

                LinkCellResolver.ResolveLinksInSelection(session.GetLinks(), target);

            if (selected.Count == 0) return entries;



            firstRect = selected[0].Rectangle;

            if (selected.Count < 2) return entries;



            var pdfNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (PdfMetadata pdf in session.Store.LoadContent().Pdfs)

            {

                if (!string.IsNullOrEmpty(pdf?.Id))

                    pdfNames[pdf.Id] = pdf.Name ?? string.Empty;

            }



            foreach (LinkCellResolver.SelectedLink link in selected)

            {

                string cellValue = string.Empty;

                string cellAddress = string.Empty;

                try

                {

                    cellValue = link.Cell.Text?.ToString() ?? string.Empty;

                    cellAddress = ((Excel.Worksheet)link.Cell.Worksheet).Name

                        + "!" + (link.Cell.Address ?? string.Empty).Replace("$", string.Empty);

                }

                catch (COMException) { }



                // Sum cells hold several rectangles behind one total, so each row carries what

                // its own rectangle contributes; the cell total stays on the cell. A rectangle

                // holding several numbers reports their subtotal plus how many it summed,

                // formatted like the cell so the figures line up with the sheet.

                bool isSum = link.Rectangle.LinkType == LinkType.Sum;

                string value = cellValue;

                int valueCount = 1;



                if (isSum)

                {

                    string sourceText = link.Rectangle.SourceText ?? string.Empty;

                    valueCount = TextValueFormatter.CountValues(sourceText);

                    double? subtotal = TextValueFormatter.SumValues(sourceText);

                    value = subtotal.HasValue

                        ? CellFormattingService.FormatLikeCell(link.Cell, subtotal.Value)

                        : sourceText;

                }



                pdfNames.TryGetValue(link.Rectangle.PdfId ?? string.Empty, out string pdfName);



                entries.Add(new LinkSelectionEntry(

                    link.Rectangle.Id,

                    link.Rectangle.PdfId,

                    pdfName ?? string.Empty,

                    link.Rectangle.Rectangle.PageIndex,

                    value,

                    valueCount,

                    cellAddress,

                    cellValue));

            }



            return entries;

        }



        protected override Office.IRibbonExtensibility CreateRibbonExtensibilityObject()

        {

            return new DocuLinkRibbon();

        }



        #region VSTO generated code



        /// <summary>

        /// Required method for Designer support - do not modify

        /// the contents of this method with the code editor.

        /// </summary>

        private void InternalStartup()

        {

            this.Startup += new System.EventHandler(ThisAddIn_Startup);

            this.Shutdown += new System.EventHandler(ThisAddIn_Shutdown);

        }



        #endregion

    }



    /// <summary>Associates a workbook's COM identity with its task pane and host control.</summary>

    internal sealed class WorkbookPaneEntry

    {

        internal Excel.Workbook Workbook { get; }

        internal Microsoft.Office.Tools.CustomTaskPane Pane { get; }

        internal TaskPaneHost Host { get; }



        internal WorkbookPaneEntry(

            Excel.Workbook workbook,

            Microsoft.Office.Tools.CustomTaskPane pane,

            TaskPaneHost host)

        {

            Workbook = workbook;

            Pane = pane;

            Host = host;

        }

    }

}


