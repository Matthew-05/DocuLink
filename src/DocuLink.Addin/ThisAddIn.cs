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

        // Standalone viewers are also workbook-scoped. A workbook may own at most one,
        // while viewers belonging to other open workbooks remain independent and visible.
        private readonly List<WorkbookViewerEntry> _workbookViewers = new List<WorkbookViewerEntry>();

        // File managers follow the same ownership model as standalone viewers: each open
        // workbook may have one independent window, permanently bound to that workbook.
        private readonly List<WorkbookFileManagerEntry> _workbookFileManagers =
            new List<WorkbookFileManagerEntry>();

        private DocumentMatcherHost _matcherWindow;

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

        private readonly object _automaticUpdateCheckSync = new object();
        private Task _automaticUpdateCheckTask;

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
            IsViewerPoppedOutFor(Application?.ActiveWorkbook);

        /// <summary>
        /// Controls whether linked-cell selection opens the task-pane viewer. This is
        /// intentionally session-only and resets to enabled each time the add-in starts;
        /// do not load it from or save it to application settings.
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
        /// Invalidates link-creation undo after a persisted DocuLink mutation. Creation and
        /// successful undo call this indirectly while writing storage, then explicitly re-arm
        /// only after their complete operation has succeeded.
        /// </summary>
        internal void DisarmLinkCreationUndo(Excel.Workbook workbook)
        {
            try
            {
                TryGetLinkUndoStack(workbook)?.Disarm();
            }
            catch (Exception ex)
            {
                Modules.DocuLinkLog.Trace(
                    $"DisarmLinkCreationUndo failed: {ex.Message}");
            }

            RefreshExcelUndoArmedState();
        }

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
        /// Routes Ctrl+Z to the actual most recent undo target. Excel's native stack has
        /// priority because it contains any worksheet action performed after DocuLink's
        /// programmatic link write, including actions such as row/column sizing that raise no
        /// SheetChange event. Link creation is reversed only when Excel has nothing newer.
        /// </summary>
        internal void UndoMostRecentAction()
        {
            if (!TryGetNativeExcelUndoState(
                out Office.CommandBars commandBars,
                out bool nativeUndoAvailable))
            {
                // Uncertainty must never cost the user a rectangle. Leave both histories
                // untouched so a later Ctrl+Z can retry when Excel is responsive.
                Modules.DocuLinkLog.Trace(
                    "UndoMostRecentAction: native Excel undo state unavailable – doing nothing");
                return;
            }

            if (nativeUndoAvailable)
            {
                try
                {
                    // Execute the built-in control rather than Application.Undo(), whose
                    // contract requires it to be the first operation in a macro. We already
                    // queried the command state to decide which undo history owns Ctrl+Z.
                    commandBars.ExecuteMso("Undo");
                }
                catch (Exception ex)
                {
                    // Never fall through to rectangle undo after Excel said it owned the
                    // keystroke. A failed native undo is safer than undoing the wrong action.
                    Modules.DocuLinkLog.Trace(
                        $"UndoMostRecentAction: Excel undo failed: {ex.Message}");
                }

                RefreshExcelUndoArmedState();
                return;
            }

            UndoLastLinkCreation();
        }

        /// <summary>
        /// Reads the live state of Excel's Undo command. Unlike worksheet events, the command
        /// covers every native undoable action, including formatting and dimension changes.
        /// </summary>
        private bool TryGetNativeExcelUndoState(
            out Office.CommandBars commandBars,
            out bool available)
        {
            commandBars = null;
            available = false;

            try
            {
                commandBars = Application?.CommandBars as Office.CommandBars;
                if (commandBars == null) return false;

                available = commandBars.GetEnabledMso("Undo");
                return true;
            }
            catch (Exception ex)
            {
                Modules.DocuLinkLog.Trace(
                    $"TryGetNativeExcelUndoState failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Runs one eligible step of link-creation undo and re-arms the grid keystroke when
        /// more history remains, so repeated Ctrl+Z can walk back through consecutive creates.
        /// </summary>
        internal string UndoLastLinkCreation()
        {
            Excel.Workbook wb = Application?.ActiveWorkbook;
            if (wb == null) return null;

            Modules.Services.LinkCreationUndoStack stack = TryGetLinkUndoStack(wb);
            if (stack == null || !stack.IsArmed || stack.IsEmpty)
            {
                RefreshExcelUndoArmedState();
                return null;
            }

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
            if (removedId != null)
                stack?.Arm();
            else
                stack?.Disarm();

            RefreshExcelUndoArmedState();

            if (removedId != null)
            {
                _cellNavigation.ResetAnchor();
                GetViewerHostFor(wb)?.SendLinkRectanglesRemoved(new List<string> { removedId });
                NotifyFileManagerLinksChanged(wb);
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

            Excel.Workbook wb = Application?.ActiveWorkbook;
            WorkbookViewerEntry viewerEntry = FindViewerEntryFor(wb);
            if (viewerEntry != null && !viewerEntry.Window.IsDisposed)
                viewerEntry.Window.Close();



            var entry = EnsureTaskPaneForActiveWorkbook();

            if (entry == null) return;

            entry.Pane.Visible = true;

            entry.Host.NotifyViewerShown();

            entry.Host.SendSearchQuery(
                GetActiveCellDisplayText(Application?.Selection as Excel.Range));

        }



        internal void ShowViewerWindow()

        {
            ReconcileClosedWorkbooks();

            Excel.Workbook wb = Application?.ActiveWorkbook;
            if (wb == null) return;

            WorkbookViewerEntry entry = EnsureViewerWindowFor(wb);
            entry.Window.InvalidateData();

            HideTaskPaneFor(wb);

            entry.Window.Show();

            entry.Window.BringToFront();

            entry.Window.NotifyViewerShown();

            entry.Window.SendSearchQuery(
                GetActiveCellDisplayText(Application?.Selection as Excel.Range));

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

        internal void RefreshTaskPanePdf(Excel.Workbook workbook, string pdfId)

        {

            GetViewerHostFor(workbook)?.SendPdfUpdated(pdfId);

        }

        internal void NotifyViewerPdfAdded(Excel.Workbook workbook, string pdfId)

        {

            GetViewerHostFor(workbook)?.SendPdfAdded(pdfId);

        }

        internal void NotifyViewerPdfRenamed(Excel.Workbook workbook, string id, string name)

        {

            GetViewerHostFor(workbook)?.SendPdfNameUpdated(id, name);

        }

        internal void NotifyViewerPdfRemoved(Excel.Workbook workbook, string id)

        {

            GetViewerHostFor(workbook)?.SendPdfRemoved(id);

        }

        /// <summary>

        /// Switches an open viewer surface to a specific PDF. Raised when the user

        /// selects a document in the file manager. No-ops when no viewer is open —

        /// the file manager is usable on its own and must not force one open.

        /// </summary>

        internal void NotifyViewerShowPdf(Excel.Workbook workbook, string pdfId)

        {

            GetViewerHostFor(workbook)?.SendShowPdf(pdfId);

        }



        /// <summary>

        /// Pushes the current folder catalogue and PDF folder assignments to the active viewer.

        /// Called after file-manager folder or move operations so the viewer's folder filter stays current.

        /// </summary>

        internal void NotifyViewerFoldersChanged(Excel.Workbook workbook)

        {

            GetViewerHostFor(workbook)?.SendFoldersToWebView();

        }



        internal void ShowManageFilesWindow()

        {
            ShowManageFilesWindow(Application?.ActiveWorkbook);
        }

        internal void ShowManageFilesWindow(Excel.Workbook workbook)
        {
            ReconcileClosedWorkbooks();
            if (workbook == null) return;

            WorkbookFileManagerEntry entry = EnsureFileManagerFor(workbook);
            entry.Window.Show();
            entry.Window.BringToFront();
            entry.Window.RefreshDataIfReady();

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



        internal void NotifyFileManagerLinksChanged(Excel.Workbook workbook)
        {
            WorkbookFileManagerEntry entry = FindFileManagerEntryFor(workbook);
            if (entry != null && !entry.Window.IsDisposed)
                entry.Window.RefreshDataIfReady();
        }



        internal TaskPaneHost TaskPaneHost => FindEntryForActiveWorkbook()?.Host;



        internal IDocumentViewerHost GetActiveViewerHost()

        {
            return GetViewerHostFor(Application?.ActiveWorkbook);
        }

        private IDocumentViewerHost GetViewerHostFor(Excel.Workbook workbook)
        {
            WorkbookViewerEntry viewerEntry = FindViewerEntryFor(workbook);
            if (viewerEntry != null
                && !viewerEntry.Window.IsDisposed
                && viewerEntry.Window.Visible)
                return viewerEntry.Window;

            return FindEntryFor(workbook)?.Host;

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
            WarmUpFileManagerFor(Application?.ActiveWorkbook);

        }



        internal void PreloadViewerWindow()

        {
            Excel.Workbook wb = Application?.ActiveWorkbook;
            if (wb == null) return;

            WorkbookViewerEntry entry = EnsureViewerWindowFor(wb);
            _ = entry.Window.Handle;

        }



        internal void PreloadMatcherWindow()

        {

            _matcherWindow = new DocumentMatcherHost();

            _ = _matcherWindow.Handle;

        }

        internal void CloseAllApplicationWindows()
        {
            foreach (WorkbookFileManagerEntry entry in _workbookFileManagers.ToArray())
            {
                if (!entry.Window.IsDisposed)
                    entry.Window.Close();
            }
            foreach (WorkbookViewerEntry entry in _workbookViewers.ToArray())
            {
                if (!entry.Window.IsDisposed)
                    entry.Window.Close();
            }
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



            var host = new TaskPaneHost(wb);

            // Passing the active window scopes the pane to this workbook's window,

            // so Excel shows/hides it automatically when the user switches workbooks.

            var pane = CustomTaskPanes.Add(host, "DocuLink", Application.ActiveWindow);

            pane.DockPosition = Office.MsoCTPDockPosition.msoCTPDockPositionRight;

            pane.Width = 640;



            pane.VisibleChanged += (_, __) =>

            {

                if (IsViewerPoppedOutFor(wb) && pane.Visible)

                    pane.Visible = false;

            };



            entry = new WorkbookPaneEntry(wb, pane, host);

            _workbookPanes.Add(entry);

            return entry;

        }



        private WorkbookViewerEntry EnsureViewerWindowFor(Excel.Workbook workbook)
        {
            WorkbookViewerEntry entry = FindViewerEntryFor(workbook);
            if (entry != null && !entry.Window.IsDisposed)
                return entry;

            if (entry != null)
                _workbookViewers.Remove(entry);

            var window = new ViewerWindowHost(workbook);
            entry = new WorkbookViewerEntry(workbook, window);
            _workbookViewers.Add(entry);
            return entry;
        }

        private WorkbookFileManagerEntry EnsureFileManagerFor(Excel.Workbook workbook)
        {
            WorkbookFileManagerEntry entry = FindFileManagerEntryFor(workbook);
            if (entry != null && !entry.Window.IsDisposed)
                return entry;

            if (entry != null)
                _workbookFileManagers.Remove(entry);

            var window = new FileManagerHost(workbook);
            entry = new WorkbookFileManagerEntry(workbook, window);
            _workbookFileManagers.Add(entry);
            return entry;
        }

        private void HideTaskPaneFor(Excel.Workbook workbook)
        {
            WorkbookPaneEntry entry = FindEntryFor(workbook);
            if (entry == null) return;

            try
            {
                if (entry.Pane.Visible)
                    entry.Pane.Visible = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] HideTaskPaneFor failed: {ex.Message}");
            }
        }



        private WorkbookPaneEntry FindEntryForActiveWorkbook()

        {

            Excel.Workbook wb = Application?.ActiveWorkbook;

            return wb == null ? null : FindEntryFor(wb);

        }

        private bool IsViewerPoppedOutFor(Excel.Workbook workbook)
        {
            WorkbookViewerEntry entry = FindViewerEntryFor(workbook);
            return entry != null && !entry.Window.IsDisposed && entry.Window.Visible;
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



        /// <summary>
        /// Applies the Development toggle to every open viewer surface so the overlay
        /// appears or clears without reopening the workbook. Viewers created later pick
        /// the state up from their controller when their web context reports ready.
        /// </summary>
        private void OnCharBoundingBoxesChanged(object sender, bool visible)
        {
            foreach (WorkbookPaneEntry entry in _workbookPanes.ToArray())
            {
                try
                {
                    entry.Host?.SendCharBboxesVisible(visible);
                }
                catch (Exception ex)
                {
                    Modules.DocuLinkLog.Trace(
                        $"OnCharBoundingBoxesChanged skipping pane: {ex.Message}");
                }
            }

            foreach (WorkbookViewerEntry entry in _workbookViewers.ToArray())
            {
                try
                {
                    if (entry.Window != null && !entry.Window.IsDisposed)
                        entry.Window.SendCharBboxesVisible(visible);
                }
                catch (Exception ex)
                {
                    Modules.DocuLinkLog.Trace(
                        $"OnCharBoundingBoxesChanged skipping viewer window: {ex.Message}");
                }
            }
        }

        /// <summary>Finds the standalone viewer owned by a workbook using COM identity.</summary>
        private WorkbookViewerEntry FindViewerEntryFor(Excel.Workbook wb)
        {
            if (wb == null) return null;

            IntPtr target = IntPtr.Zero;
            try
            {
                target = Marshal.GetIUnknownForObject(wb);
                foreach (WorkbookViewerEntry entry in _workbookViewers)
                {
                    IntPtr candidate = IntPtr.Zero;
                    try
                    {
                        candidate = Marshal.GetIUnknownForObject(entry.Workbook);
                        if (candidate == target) return entry;
                    }
                    catch (Exception ex)
                    {
                        Modules.DocuLinkLog.Trace(
                            $"FindViewerEntryFor skipping unusable entry: {ex.Message}");
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

        /// <summary>Finds the file manager owned by a workbook using COM identity.</summary>
        private WorkbookFileManagerEntry FindFileManagerEntryFor(Excel.Workbook wb)
        {
            if (wb == null) return null;

            IntPtr target = IntPtr.Zero;
            try
            {
                target = Marshal.GetIUnknownForObject(wb);
                foreach (WorkbookFileManagerEntry entry in _workbookFileManagers)
                {
                    IntPtr candidate = IntPtr.Zero;
                    try
                    {
                        candidate = Marshal.GetIUnknownForObject(entry.Workbook);
                        if (candidate == target) return entry;
                    }
                    catch (Exception ex)
                    {
                        Modules.DocuLinkLog.Trace(
                            $"FindFileManagerEntryFor skipping unusable entry: {ex.Message}");
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

            Modules.DocuLinkLog.StartSession();

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
                () => UndoMostRecentAction());

            Modules.Infrastructure.DevSettings.CharBoundingBoxesChanged +=
                OnCharBoundingBoxesChanged;

            _ = CheckForUpdateOnOpenAsync();

        }



        private void ThisAddIn_Shutdown(object sender, System.EventArgs e)

        {

            Modules.DocuLinkLog.Trace("ENTER");

            DisposeApplicationSurfacesForShutdown();

            _excelUndoKeyHook?.Dispose();

            _excelUndoKeyHook = null;

            Modules.Infrastructure.DevSettings.CharBoundingBoxesChanged -=
                OnCharBoundingBoxesChanged;

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

                foreach (WorkbookFileManagerEntry entry in _workbookFileManagers.ToArray())
                {
                    try
                    {
                        if (!entry.Window.IsDisposed)
                        {
                            Modules.DocuLinkLog.Trace("disposing workbook file manager window");
                            entry.Window.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Modules.DocuLinkLog.Trace(
                            $"file manager dispose failed: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
                _workbookFileManagers.Clear();

                foreach (WorkbookViewerEntry entry in _workbookViewers.ToArray())
                {
                    try
                    {
                        if (!entry.Window.IsDisposed)
                        {
                            Modules.DocuLinkLog.Trace("disposing workbook viewer window");
                            entry.Window.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Modules.DocuLinkLog.Trace(
                            $"viewer window dispose failed: {ex.GetType().FullName}: {ex.Message}");
                    }
                }
                _workbookViewers.Clear();

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
                $"panes={_workbookPanes.Count} viewers={_workbookViewers.Count} " +
                $"fileManagers={_workbookFileManagers.Count} " +
                $"sessions={_storageSessions.Count}");

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
            if (_workbookPanes.Count == 0
                && _workbookViewers.Count == 0
                && _workbookFileManagers.Count == 0
                && _storageSessions.Count == 0)
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

            foreach (WorkbookViewerEntry entry in _workbookViewers.ToArray())
            {
                if (!entry.Window.IsDisposed
                    && IsWorkbookStillOpen(entry.Workbook, liveWorkbooks))
                    continue;

                Modules.DocuLinkLog.Trace("reconcile: removing viewer for closed workbook");
                _workbookViewers.Remove(entry);

                try
                {
                    if (!entry.Window.IsDisposed)
                        entry.Window.Dispose();
                }
                catch (Exception ex)
                {
                    Modules.DocuLinkLog.Trace(
                        $"reconcile: viewer dispose failed: {ex.GetType().FullName}: {ex.Message}");
                }
            }

            foreach (WorkbookFileManagerEntry entry in _workbookFileManagers.ToArray())
            {
                if (!entry.Window.IsDisposed
                    && IsWorkbookStillOpen(entry.Workbook, liveWorkbooks))
                    continue;

                Modules.DocuLinkLog.Trace("reconcile: removing file manager for closed workbook");
                _workbookFileManagers.Remove(entry);

                try
                {
                    if (!entry.Window.IsDisposed)
                        entry.Window.Dispose();
                }
                catch (Exception ex)
                {
                    Modules.DocuLinkLog.Trace(
                        $"reconcile: file manager dispose failed: {ex.GetType().FullName}: {ex.Message}");
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

            // The startup eager-loader can run before Excel exposes ActiveWorkbook.
            // Warm the manager for the workbook that actually became active so its
            // workbook-bound WebView is ready before the user opens it.
            WarmUpFileManagerFor(wb);

            EnsureLinkTracking(wb);

            // Pending undo belongs to whichever workbook is in front, so re-evaluate before
            // anything else — including the pop-out early return further down.
            RefreshExcelUndoArmedState();

            try

            {

                WorkbookViewerEntry viewerEntry = FindViewerEntryFor(wb);
                if (viewerEntry != null
                    && !viewerEntry.Window.IsDisposed
                    && viewerEntry.Window.Visible)

                {

                    viewerEntry.Window.InvalidateData();

                    viewerEntry.Window.RefreshDataIfReady();

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

            WarmUpFileManagerFor(wb);

            EnsureLinkTracking(wb);

            WarmUpTaskPaneFor(wb);

            await CheckForUpdateOnOpenAsync();

        }

        private Task CheckForUpdateOnOpenAsync()

        {

            if (AppVersion.IsDevelopment)
                return Task.CompletedTask;

            if ((DateTime.UtcNow - Settings.Default.LastUpdateCheck).TotalHours < 24)
                return Task.CompletedTask;

            lock (_automaticUpdateCheckSync)
            {
                if (_automaticUpdateCheckTask == null || _automaticUpdateCheckTask.IsCompleted)
                    _automaticUpdateCheckTask = CheckForUpdateOnOpenCoreAsync();

                return _automaticUpdateCheckTask;
            }

        }

        private async Task CheckForUpdateOnOpenCoreAsync()

        {

            UpdateCheckResult result;

            try { result = await UpdateCheckService.CheckAsync().ConfigureAwait(true); }

            catch { return; }

            if (result?.UpdateAvailable != true) return;

            UpdateDialog.ShowSingle(result);

        }



        private void Application_NewWorkbook(Excel.Workbook wb)

        {

            WarmUpFileManagerFor(wb);

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
        /// Pre-creates the workbook-bound file manager invisibly and forces HWND creation
        /// so its WebView2 shell loads before the user asks to show the window.
        /// </summary>
        private void WarmUpFileManagerFor(Excel.Workbook wb)
        {
            if (wb == null) return;

            try
            {
                WorkbookFileManagerEntry entry = EnsureFileManagerFor(wb);
                _ = entry.Window.Handle;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] WarmUpFileManagerFor failed: {ex.Message}");
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

                IList<string> prunedIds = LinkCellTracker.SyncAllPositions(wb);

                Modules.DocuLinkLog.Trace("LinkCellTracker.SyncAllPositions done");

                if (prunedIds.Count > 0)
                {
                    Modules.DocuLinkLog.Trace(
                        $"pruned stale linked rectangles count={prunedIds.Count}");
                    GetViewerHostFor(wb)?.SendLinkRectanglesRemoved(prunedIds);
                    NotifyFileManagerLinksChanged(wb);
                }

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

                string searchQuery = GetActiveCellDisplayText(target);
                IDocumentViewerHost visibleViewer = GetVisibleViewerHost();
                visibleViewer?.SendSearchQuery(searchQuery);

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

                IDocumentViewerHost viewer = GetVisibleViewerHost();
                viewer?.SendSearchQuery(GetActiveCellDisplayText(target));
                viewer?.SendLinkSelectionChanged(entries);

            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine(

                    $"[DocuLink] PublishLinkSelection failed: {ex.Message}");

            }

        }



        private IDocumentViewerHost GetVisibleViewerHost()

        {

            return IsViewerPoppedOut || IsTaskPaneViewerVisible()

                ? GetActiveViewerHost()

                : null;

        }



        private string GetActiveCellDisplayText(Excel.Range selection)

        {

            try

            {

                Excel.Range activeCell = Application?.ActiveCell as Excel.Range;

                if (activeCell != null)

                    return activeCell.Text?.ToString() ?? string.Empty;

                return selection?.Text?.ToString() ?? string.Empty;

            }

            catch (COMException)

            {

                return string.Empty;

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

    /// <summary>Associates a workbook's COM identity with its standalone viewer.</summary>
    internal sealed class WorkbookViewerEntry
    {
        internal Excel.Workbook Workbook { get; }
        internal ViewerWindowHost Window { get; }

        internal WorkbookViewerEntry(Excel.Workbook workbook, ViewerWindowHost window)
        {
            Workbook = workbook;
            Window = window;
        }
    }

    /// <summary>Associates a workbook's COM identity with its file-manager window.</summary>
    internal sealed class WorkbookFileManagerEntry
    {
        internal Excel.Workbook Workbook { get; }
        internal FileManagerHost Window { get; }

        internal WorkbookFileManagerEntry(Excel.Workbook workbook, FileManagerHost window)
        {
            Workbook = workbook;
            Window = window;
        }
    }

}


