using System;

using System.Collections.Generic;

using System.Runtime.InteropServices;

using Excel = Microsoft.Office.Interop.Excel;

using Office = Microsoft.Office.Core;

using DocuLink.Addin.Ribbon;

using DocuLink.Addin.Modules.CustomXml;

using DocuLink.Addin.Modules.CustomXml.Models;

using DocuLink.Addin.Modules.Services;

using DocuLink.Addin.Modules.WebView;

using DocuLink.Addin.Properties;



namespace DocuLink.Addin

{

    public partial class ThisAddIn

    {

        // One entry per open workbook; created on demand when the user opens the task pane.

        private readonly List<WorkbookPaneEntry> _workbookPanes = new List<WorkbookPaneEntry>();

        private FileManagerHost _fileManagerWindow;

        private ViewerWindowHost _viewerWindow;

        private readonly Dictionary<string, WorkbookStorageSession> _storageSessions =
            new Dictionary<string, WorkbookStorageSession>(StringComparer.OrdinalIgnoreCase);



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

            _storageSessions.Remove(GetWorkbookSessionKey(workbook));

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

            entry.Host.RefreshDataIfReady();

        }



        internal void ShowViewerWindow()

        {

            if (_viewerWindow == null || _viewerWindow.IsDisposed)

                _viewerWindow = new ViewerWindowHost();



            HideAllTaskPanes();

            _viewerWindow.Show();

            _viewerWindow.BringToFront();

            _viewerWindow.RefreshDataIfReady();

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



        internal void ShowManageFilesWindow()

        {

            if (_fileManagerWindow == null || _fileManagerWindow.IsDisposed)

                _fileManagerWindow = new FileManagerHost();



            _fileManagerWindow.Show();

            _fileManagerWindow.BringToFront();

            _fileManagerWindow.RefreshDataIfReady();

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



        // ── Per-workbook task pane management ────────────────────────────────



        private WorkbookPaneEntry EnsureTaskPaneForActiveWorkbook()

        {

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

            Application.WorkbookBeforeClose += Application_WorkbookBeforeClose;

            Application.WorkbookBeforeSave += Application_WorkbookBeforeSave;

            Application.WorkbookActivate += Application_WorkbookActivate;

            Application.WorkbookOpen += Application_WorkbookOpen;

            ((Excel.AppEvents_Event)Application).NewWorkbook += Application_NewWorkbook;

        }



        private void ThisAddIn_Shutdown(object sender, System.EventArgs e)

        {

            Application.SheetSelectionChange -= Application_SheetSelectionChange;

            Application.WorkbookBeforeClose -= Application_WorkbookBeforeClose;

            Application.WorkbookBeforeSave -= Application_WorkbookBeforeSave;

            Application.WorkbookActivate -= Application_WorkbookActivate;

            Application.WorkbookOpen -= Application_WorkbookOpen;

            ((Excel.AppEvents_Event)Application).NewWorkbook -= Application_NewWorkbook;

        }



        private void Application_WorkbookBeforeClose(Excel.Workbook wb, ref bool cancel)

        {

            ReleaseStorageSession(wb);

            var entry = FindEntryFor(wb);

            if (entry == null) return;



            // Remove the entry before the window is destroyed so no subsequent code

            // touches the soon-to-be-invalid CustomTaskPane COM object.

            _workbookPanes.Remove(entry);

        }



        private void Application_WorkbookActivate(Excel.Workbook wb)

        {

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



        private void Application_WorkbookOpen(Excel.Workbook wb)

        {

            WarmUpTaskPaneFor(wb);

        }



        private void Application_NewWorkbook(Excel.Workbook wb)

        {

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



        private void Application_WorkbookBeforeSave(Excel.Workbook wb, bool saveAsUi, ref bool cancel)

        {

            try

            {

                LinkCellTracker.SyncAllPositions(wb);

            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine(

                    $"[DocuLink] Application_WorkbookBeforeSave sync failed: {ex.Message}");

            }

        }



        private void Application_SheetSelectionChange(object sh, Excel.Range target)

        {

            Modules.DocuLinkLog.Trace($"ENTER addr={target?.get_Address() ?? "null"} SuppressNext={SuppressNextSelectionNav} SuppressDepth={_suppressSelectionNavDepth}");

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

                Excel.Range firstCell = (Excel.Range)target.Cells[1, 1];

                int trackIndex = LinkCellTracker.FindTrackIndexForCell(firstCell);



                if (trackIndex <= 0)

                {

                    GetActiveViewerHost()?.SendClearRectangleHighlight();

                    return;

                }



                Excel.Workbook wb = Application?.ActiveWorkbook;

                if (wb == null) return;



                IList<LinkedRectangle> links = GetStorageSession(wb).GetLinks();

                LinkedRectangle rect = null;

                foreach (LinkedRectangle r in links)

                {

                    if (r.LinkedCell.TrackIndex == trackIndex)

                    {

                        rect = r;

                        break;

                    }

                }



                if (rect == null)

                {

                    GetActiveViewerHost()?.SendClearRectangleHighlight();

                    return;

                }



                if (Settings.Default.AutoOpenViewerOnCellClick

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


