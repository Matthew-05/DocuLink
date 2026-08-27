using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using DocuLink.Addin.Modules.Services;
using DocuLink.Addin.Modules.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Shared WebView2 + messaging logic for the document-viewer web app.</summary>
    internal sealed class DocumentViewerController : IDisposable
    {
        private readonly Control _invokeTarget;
        private readonly string _loadFailureSurfaceName;
        private readonly Excel.Workbook _workbook;
        private readonly Panel _surface = new Panel();
        private readonly Label _startupPlaceholder = new Label();
        private readonly WebView2 _webView = new WebView2();
        private readonly ExcelGridFocusRestoreService _focusRestoreService;
        private ThreadedProgressController _cacheProgress;
        private Task _initTask;
        private bool _webShellReady;
        private bool _webViewReady;
        private bool _dataSentToViewer;
        private bool _viewerShown;
        private bool _contentReady;
        private string _pendingNavigateId;
        private string _pendingNavigatePdfId;
        private int? _pendingNavigatePage;
        private string _pendingSearchQuery;
        private bool _disposed;

        internal DocumentViewerController(
            Control invokeTarget,
            string loadFailureSurfaceName,
            Excel.Workbook workbook)
        {
            _invokeTarget = invokeTarget ?? throw new ArgumentNullException(nameof(invokeTarget));
            _loadFailureSurfaceName = loadFailureSurfaceName ?? "viewer";
            _workbook = workbook ?? throw new ArgumentNullException(nameof(workbook));

            Color background = Color.FromArgb(244, 244, 249);

            _surface.Dock = DockStyle.Fill;
            _surface.BackColor = background;

            _webView.Dock = DockStyle.Fill;
            _webView.DefaultBackgroundColor = background;
            _webView.Leave += OnWebViewLeave;
            _focusRestoreService = new ExcelGridFocusRestoreService(_surface);

            _startupPlaceholder.Dock = DockStyle.Fill;
            _startupPlaceholder.BackColor = background;
            _startupPlaceholder.ForeColor = Color.FromArgb(92, 92, 112);
            _startupPlaceholder.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            _startupPlaceholder.Text = "DocuLink Initializing...";
            _startupPlaceholder.TextAlign = ContentAlignment.MiddleCenter;

            _surface.Controls.Add(_webView);
            _surface.Controls.Add(_startupPlaceholder);
            _startupPlaceholder.BringToFront();
        }

        internal Control Surface => _surface;

        internal WebView2 WebView => _webView;

        internal void Start()
        {
            if (_disposed) return;
            if (_initTask != null) return;
            DocuLinkLog.Trace($"START surface={_loadFailureSurfaceName}");
            _initTask = InitAsync();
        }

        private async Task InitAsync()
        {
            DocuLinkLog.Trace($"ENTER surface={_loadFailureSurfaceName}");
            try
            {
                if (_disposed) return;

                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DocuLink", "WebView2");

                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);

                await _webView.EnsureCoreWebView2Async(environment);
                if (_disposed) return;

                string uiPath = GetWebUiPath();
                if (!Directory.Exists(uiPath))
                    throw new DirectoryNotFoundException(
                        $"Web UI folder not found: {uiPath}\n\nRun 'npm run build' in src/web to generate it.");

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "doculink.local",
                    uiPath,
                    CoreWebView2HostResourceAccessKind.Allow);

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                _webView.CoreWebView2.Navigate("https://doculink.local/index.html");
                DocuLinkLog.Trace($"EXIT initialized surface={_loadFailureSurfaceName}");
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"EXCEPTION surface={_loadFailureSurfaceName} {ex.GetType().FullName}: {ex.Message}");
                MessageBox.Show(
                    $"DocuLink {_loadFailureSurfaceName} failed to load:\n\n{ex.Message}",
                    "DocuLink",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                ShowStartupFailure(ex.Message);
            }
        }

        private void RevealWebView()
        {
            if (_disposed) return;

            if (_surface.InvokeRequired)
            {
                _surface.BeginInvoke(new Action(RevealWebView));
                return;
            }

            _startupPlaceholder.Visible = false;
            _webView.BringToFront();
        }

        private void ShowStartupFailure(string message)
        {
            if (_disposed) return;

            if (_surface.InvokeRequired)
            {
                _surface.BeginInvoke(new Action(() => ShowStartupFailure(message)));
                return;
            }

            _startupPlaceholder.Text = $"DocuLink failed to load.\n\n{message}";
            _startupPlaceholder.Visible = true;
            _startupPlaceholder.BringToFront();
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_disposed) return;

            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                string messageType = HostMessageParser.GetMessageType(raw);
                DocuLinkLog.Trace($"message type={messageType ?? "(unknown)"} surface={_loadFailureSurfaceName}");

                switch (messageType)
                {
                    case "viewer-shell-ready":
                        // A shell-ready message identifies a newly mounted web document. Any
                        // state retained by this controller belongs to the previous JavaScript
                        // context and must not suppress the new context's bootstrap payload.
                        _webShellReady = true;
                        _webViewReady = false;
                        _dataSentToViewer = false;
                        _contentReady = false;
                        break;

                    case "viewer-ready":
                        if (!_webShellReady)
                        {
                            _webShellReady = true;
                        }
                        _webViewReady = true;
                        // viewer-ready is emitted once per initialized JavaScript context. A
                        // renderer/page restart therefore needs a complete authoritative sync,
                        // even if the previous context had already received the workbook data.
                        _dataSentToViewer = false;
                        if (_viewerShown)
                        {
                            RefreshDataIfReady();
                        }
                        break;

                    case "viewer-content-ready":
                        _contentReady = true;
                        // Also dismiss the cache-build progress here. It was opened in
                        // NotifyViewerShown() and is normally closed by cache-build-complete,
                        // but viewer-content-ready is always sent (from the finally block in
                        // viewer-bridge.ts) and arrives after onDocumentChanged has run —
                        // so it guarantees the loader is dismissed even when the cache was
                        // already populated (the fast path that skips sendCacheBuildComplete).
                        _cacheProgress?.Dispose();
                        _cacheProgress = null;
                        if (_viewerShown)
                            RevealWebView();
                        break;

                    case "open-file-manager":
                        HandleOpenFileManager();
                        break;

                    case "link-rectangle-created":
                        HandleLinkRectangleCreated(raw);
                        break;

                    case "link-rectangle-updated":
                        HandleLinkRectangleUpdated(raw);
                        break;

                    case "link-rectangle-clicked":
                        HandleLinkRectangleClicked(raw);
                        break;

                    case "link-rectangle-deleted":
                        HandleLinkRectangleDeleted(raw);
                        break;

                    case "copy-table-selection":
                        HandleCopyTableSelection(raw);
                        break;

                    case "excel-navigate":
                        HandleExcelNavigate(raw);
                        break;

                    case "undo-link-creation":
                        HandleUndoLinkCreation();
                        break;

                    case "rotate-page":
                        HandleRotatePage(raw);
                        break;

                    case "cache-build-complete":
                        _cacheProgress?.Dispose();
                        _cacheProgress = null;
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] OnWebMessageReceived failed: {ex.Message}");
            }
        }

        private void OnWebViewLeave(object sender, EventArgs e)
        {
            if (_disposed) return;
            RestoreExcelFocus();
        }

        private void HandleOpenFileManager()
        {
            _invokeTarget.BeginInvoke(new Action(() =>
            {
                if (!TryActivateWorkbook()) return;
                Globals.ThisAddIn.ShowManageFilesWindow();
            }));
        }

        private void RestoreExcelFocus()
        {
            ExcelGridFocusRestoreService.RestoreExcelFocus();
        }

        /// <summary>
        /// Moves the Excel cursor for a Tab/Enter keystroke the viewer forwarded, so the grid
        /// keeps responding while the user works inside the PDF.
        /// </summary>
        private void HandleExcelNavigate(string json)
        {
            var payload = HostMessageParser.ParseExcelNavigate(json);
            if (payload == null) return;

            _invokeTarget.BeginInvoke(new Action(() =>
            {
                if (!TryActivateWorkbook()) return;
                Globals.ThisAddIn.CellNavigation.Navigate(payload.Motion, payload.Reverse);
            }));
        }

        /// <summary>
        /// Routes undo through the add-in so Excel's native undo stack keeps priority over
        /// DocuLink's link-creation history.
        /// </summary>
        private void HandleUndoLinkCreation()
        {
            _invokeTarget.BeginInvoke(new Action(() =>
            {
                if (!TryActivateWorkbook()) return;
                Globals.ThisAddIn.UndoMostRecentAction();
            }));
        }

        private void HandleLinkRectangleCreated(string json)
        {
            var payload = HostMessageParser.ParseLinkRectangleCreated(json);
            if (payload == null) return;

            _invokeTarget.BeginInvoke(new Action(() =>
            {
                if (!TryActivateWorkbook()) return;
                ExecuteLinkRectangleCreated(payload, _workbook);
            }));
        }

        private void ExecuteLinkRectangleCreated(LinkRectangleCreatedPayload payload, Excel.Workbook wb)
        {
            using (DocuLinkLog.Time("ExecuteLinkRectangleCreated total"))
            {
                DocuLinkLog.Trace("ENTER");
                IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
                if (!WorkbookProtectionGuard.TryRequireWritable(wb, owner))
                    return;

                try
                {
                    var sel = Globals.ThisAddIn.Application?.Selection as Excel.Range;
                    DocuLinkLog.Trace($"selection before CreateLink: {sel?.Address ?? "null"}");
                    DocuLinkLog.Trace($"active cell before CreateLink: {(Globals.ThisAddIn.Application?.ActiveCell as Excel.Range)?.Address ?? "null"}, value={((Globals.ThisAddIn.Application?.ActiveCell as Excel.Range)?.Value2 ?? "(null)")}");
                }
                catch (Exception ex) { DocuLinkLog.Trace($"pre-create cell read failed: {ex.Message}"); }

                string text = payload.Text;
                if (payload.LinkType != LinkType.Table && string.IsNullOrWhiteSpace(text))
                {
                    if (!LinkTextPromptDialog.TryPrompt(owner, out text))
                    {
                        DocuLinkLog.Trace("text prompt cancelled");
                        SendLinkedRectanglesToWebView();
                        return;
                    }
                }

                DocuLinkLog.Trace($"text='{text}' – calling CreateLink");
                LinkedRectangle linkedRect;
                IList<LinkedRectangle> allRects;
                using (Globals.ThisAddIn.EnterSelectionNavSuppress())
                {
                    (linkedRect, allRects) = new CreateLinkService().CreateLink(
                        payload.PdfId,
                        payload.Page,
                        payload.X, payload.Y, payload.Width, payload.Height,
                        text,
                        payload.LinkType,
                        payload.AppendToActiveSum,
                        payload.TableGrid,
                        payload.TableCells,
                        owner,
                        wb);

                    if (linkedRect != null)
                    {
                        Excel.Range linkedCell = LinkCellResolver.TryResolveCell(wb, linkedRect);
                        ExcelCellNavigationService.BringIntoView(linkedCell);
                    }
                }
                DocuLinkLog.Trace($"CreateLink returned id={linkedRect?.Id ?? "null"}");

                try
                {
                    var ac = Globals.ThisAddIn.Application?.ActiveCell as Excel.Range;
                    DocuLinkLog.Trace($"active cell after CreateLink: {ac?.Address ?? "null"}, value={ac?.Value2 ?? "(null)"}");
                }
                catch (Exception ex) { DocuLinkLog.Trace($"post-create cell read failed: {ex.Message}"); }

                DocuLinkLog.Trace("calling SendLinkedRectanglesToWebView (pre-loaded)");
                if (allRects != null)
                    SendLinkedRectanglesToWebView(allRects);
                else
                    SendLinkedRectanglesToWebView();
                DocuLinkLog.Trace("SendLinkedRectanglesToWebView done");

                if (linkedRect != null)
                {
                    DocuLinkLog.Trace($"calling SendHighlightRectangle id={linkedRect.Id}");
                    SendHighlightRectangle(linkedRect.Id);
                    DocuLinkLog.Trace("SendHighlightRectangle done");
                }

                Globals.ThisAddIn.NotifyFileManagerLinksChanged();

                DocuLinkLog.Trace("restoring focus to Excel");
                RestoreExcelFocus();

                DocuLinkLog.Trace("EXIT");
            }
        }

        private void HandleLinkRectangleUpdated(string json)
        {
            var payload = HostMessageParser.ParseLinkRectangleUpdated(json);
            if (payload == null) return;

            if (!TryActivateWorkbook()) return;
            Excel.Workbook wb = _workbook;

            IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
            if (!WorkbookProtectionGuard.TryRequireWritable(wb, owner))
                return;

            string text = payload.Text;
            if (payload.TableGrid == null && string.IsNullOrWhiteSpace(text))
            {
                if (!LinkTextPromptDialog.TryPrompt(owner, out text))
                {
                    SendLinkedRectanglesToWebView();
                    return;
                }
            }

            new UpdateLinkService().UpdateLink(
                payload.Id,
                payload.Page,
                payload.X, payload.Y, payload.Width, payload.Height,
                text,
                payload.TableGrid,
                payload.TableCells,
                owner,
                wb);

            SendLinkedRectanglesToWebView();

            RestoreExcelFocus();
        }

        private void HandleLinkRectangleClicked(string json)
        {
            string rectId = HostMessageParser.ParseLinkRectangleClicked(json);
            if (string.IsNullOrWhiteSpace(rectId)) return;

            if (!TryActivateWorkbook()) return;
            Excel.Workbook wb = _workbook;

            Globals.ThisAddIn.SuppressNextSelectionNav = true;

            var session = Globals.ThisAddIn.GetStorageSession(wb);
            var rect = session.GetLinks().FirstOrDefault(r => string.Equals(r.Id, rectId, StringComparison.Ordinal));
            if (rect == null)
            {
                Globals.ThisAddIn.SuppressNextSelectionNav = false;
                return;
            }

            Excel.Range cell = LinkCellResolver.TryResolveCell(wb, rect);
            if (cell == null)
            {
                Globals.ThisAddIn.SuppressNextSelectionNav = false;
                return;
            }

            try
            {
                ((Excel.Worksheet)cell.Worksheet).Activate();
                cell.Select();

                // Selection nav is suppressed for this round-trip, so publish the selection
                // explicitly: a Sum cell still has several rectangles to list.
                Globals.ThisAddIn.PublishLinkSelection(cell);

                RestoreExcelFocus();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] HandleLinkRectangleClicked navigate failed: {ex.Message}");
            }
            finally
            {
                Globals.ThisAddIn.SuppressNextSelectionNav = false;
            }
        }

        private void HandleLinkRectangleDeleted(string json)
        {
            LinkRectangleDeletedPayload payload =
                HostMessageParser.ParseLinkRectangleDeletion(json);
            if (payload == null) return;

            if (!TryActivateWorkbook()) return;
            Excel.Workbook wb = _workbook;

            IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
            if (!WorkbookProtectionGuard.TryRequireWritable(wb, owner))
                return;

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                if (!new DeleteLinkService().DeleteLink(
                    payload.Id, wb, payload.DeleteCellData))
                    return;

                SendLinkRectanglesRemoved(new[] { payload.Id });
            }

            Globals.ThisAddIn.NotifyFileManagerLinksChanged();

            RestoreExcelFocus();
        }

        private void HandleCopyTableSelection(string json)
        {
            CopyTableSelectionPayload payload = HostMessageParser.ParseCopyTableSelection(json);
            if (payload == null) return;

            if (!TryActivateWorkbook()) return;
            Excel.Workbook wb = _workbook;

            IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
            if (!WorkbookProtectionGuard.TryRequireWritable(wb, owner))
                return;

            var targets = payload.Targets
                .Select(target => (target.Page, target.TableGrid, target.TableCells))
                .ToList();
            IList<LinkedRectangle> allRects;
            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                allRects = new CopyTableSelectionService().Copy(
                    payload.Id, targets, owner, wb, SendLinkedRectangleAdded);
            }
            if (allRects == null) return;

            Globals.ThisAddIn.NotifyFileManagerLinksChanged();
            RestoreExcelFocus();
        }

        private void HandleRotatePage(string json)
        {
            var payload = HostMessageParser.ParseRotatePage(json);
            if (payload == null) return;

            if (!TryActivateWorkbook()) return;
            Excel.Workbook wb = _workbook;

            IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
            if (!WorkbookProtectionGuard.TryRequireWritable(wb, owner))
                return;

            _invokeTarget.BeginInvoke(new Action(() => ExecuteRotatePage(payload, wb)));
        }

        private void ExecuteRotatePage(RotatePagePayload payload, Excel.Workbook wb)
        {
            try
            {
                var (newRotations, allRects) = new RotatePageService().RotatePage(
                    payload.PdfId, payload.Page, payload.Direction, wb);

                SendPageRotationsUpdated(payload.PdfId, newRotations);
                SendLinkedRectanglesToWebView(allRects);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] ExecuteRotatePage failed: {ex.Message}");
            }
        }

        internal void SendPageRotationsUpdated(string pdfId, Dictionary<int, int> rotations)
        {
            if (!_webViewReady || string.IsNullOrWhiteSpace(pdfId))
                return;

            try
            {
                string json = HostMessageSerializer.BuildPageRotationsUpdated(pdfId, rotations);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPageRotationsUpdated failed: {ex.Message}");
            }
        }

        internal void SendLinkRectanglesRemoved(IList<string> ids)
        {
            if (!_webViewReady || ids == null || ids.Count == 0)
                return;

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(
                    HostMessageSerializer.BuildLinkRectanglesRemoved(ids));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendLinkRectanglesRemoved failed: {ex.Message}");
            }
        }

        private void SendLinkedRectangleAdded(LinkedRectangle rectangle)
        {
            if (_disposed || !_webViewReady || rectangle == null) return;
            try
            {
                string json = HostMessageSerializer.BuildLinkedRectangleAdded(rectangle);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendLinkedRectangleAdded failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Commands originating from a pop-out must act on that pop-out's workbook, even
        /// when another workbook was focused in Excel. Activating the owner also makes the
        /// workbook's current selection authoritative for link creation and grid navigation.
        /// </summary>
        private bool TryActivateWorkbook()
        {
            if (_disposed) return false;

            try
            {
                _workbook.Activate();
                return true;
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace(
                    $"Activate owning workbook failed surface={_loadFailureSurfaceName}: {ex.Message}");
                return false;
            }
        }

        internal void SendClearRectangleHighlight()
        {
            if (!_webViewReady) return;

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(
                    HostMessageSerializer.BuildClearRectangleHighlight());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendClearRectangleHighlight failed: {ex.Message}");
            }
        }

        internal void SendLinkSelectionChanged(IList<LinkSelectionEntry> entries)
        {
            if (!_webViewReady) return;

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(
                    HostMessageSerializer.BuildLinkSelectionChanged(entries));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendLinkSelectionChanged failed: {ex.Message}");
            }
        }

        internal void SendSearchQuery(string query)
        {
            if (!_webViewReady)
            {
                _pendingSearchQuery = query ?? string.Empty;
                return;
            }

            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(
                    HostMessageSerializer.BuildSetSearchQuery(query));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendSearchQuery failed: {ex.Message}");
            }
        }

        private void FlushPendingSearchQuery()
        {
            if (_pendingSearchQuery == null) return;

            string query = _pendingSearchQuery;
            _pendingSearchQuery = null;
            SendSearchQuery(query);
        }

        internal void SendNavigateToRectangle(string id, string pdfId, int page)
        {
            if (!_webViewReady)
            {
                _pendingNavigateId = id;
                _pendingNavigatePdfId = pdfId;
                _pendingNavigatePage = page;
                return;
            }

            PostNavigateToRectangle(id, pdfId, page);
        }

        private void FlushPendingNavigateToRectangle()
        {
            if (_pendingNavigatePage == null)
                return;

            string id = _pendingNavigateId;
            string pdfId = _pendingNavigatePdfId;
            int page = _pendingNavigatePage.Value;

            _pendingNavigateId = null;
            _pendingNavigatePdfId = null;
            _pendingNavigatePage = null;

            PostNavigateToRectangle(id, pdfId, page);
        }

        private void PostNavigateToRectangle(string id, string pdfId, int page)
        {
            try
            {
                string json = HostMessageSerializer.BuildNavigateToRectangle(id, pdfId, page);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendNavigateToRectangle failed: {ex.Message}");
            }
        }

        private void SendHighlightRectangle(string id)
        {
            try
            {
                string json = HostMessageSerializer.BuildHighlightRectangle(id);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendHighlightRectangle failed: {ex.Message}");
            }
        }

        internal void RefreshDataIfReady()
        {
            DocuLinkLog.Trace($"ENTER surface={_loadFailureSurfaceName} ready={_webViewReady} dataSent={_dataSentToViewer}");
            if (_disposed || !_webViewReady || _dataSentToViewer) return;

            // Do not mark this viewer as synchronized when loading or posting the
            // authoritative catalogue failed. A later show/activation/ready event can retry.
            if (!SendPdfsToWebView()) return;

            _dataSentToViewer = true;
            SendLinkedRectanglesToWebView();
            FlushPendingSearchQuery();
            FlushPendingNavigateToRectangle();
            DocuLinkLog.Trace($"EXIT surface={_loadFailureSurfaceName}");
        }

        internal void InvalidateData()
        {
            _dataSentToViewer = false;
            _contentReady = false;
        }

        internal void NotifyViewerShown()
        {
            if (!_viewerShown && !_contentReady)
            {
                _cacheProgress?.Dispose();
                _cacheProgress = ThreadedProgressController.Show("Preparing document viewer...");
                _cacheProgress.Report("Preparing document viewer", "Building document index...", 0, 0);
            }
            _viewerShown = true;
            RefreshDataIfReady();
            if (_contentReady)
                RevealWebView();
        }

        internal void SendLinkedRectanglesToWebView()
        {
            using (DocuLinkLog.Time($"SendLinkedRectanglesToWebView surface={_loadFailureSurfaceName}"))
            {
            if (_disposed) return;
            try
            {
                PostLinkedRectangles(Globals.ThisAddIn.GetStorageSession(_workbook).GetLinks());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendLinkedRectanglesToWebView failed: {ex.Message}");
                DocuLinkLog.Trace($"EXCEPTION {ex.GetType().FullName}: {ex.Message}");
            }
            }
        }

        /// <summary>
        /// Sends a pre-loaded linked-rectangles list to the viewer without reloading
        /// from storage. Use this when the caller already has the current list in memory
        /// (e.g. immediately after CreateLink returns) to avoid a redundant XML load.
        /// </summary>
        internal void SendLinkedRectanglesToWebView(IList<LinkedRectangle> linkedRectangles)
        {
            if (_disposed) return;
            if (linkedRectangles == null) return;
            try
            {
                PostLinkedRectangles(linkedRectangles);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendLinkedRectanglesToWebView(list) failed: {ex.Message}");
            }
        }

        private void PostLinkedRectangles(IList<LinkedRectangle> linkedRectangles)
        {
            if (_disposed) return;
            if (!_webViewReady) return;
            string json = HostMessageSerializer.BuildLinkedRectanglesLoaded(linkedRectangles);
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        private bool SendPdfsToWebView()
        {
            using (DocuLinkLog.Time($"SendPdfsToWebView surface={_loadFailureSurfaceName}"))
            {
            if (_disposed || !_webViewReady) return false;
            try
            {
                var store = new DocuLinkCustomXmlPartStore(_workbook);
                IList<PdfDocument> pdfs = store.LoadAllPdfsWithBinary();
                IList<PdfFolder> folders = store.LoadContent().Folders;
                string json = HostMessageSerializer.BuildPdfsLoaded(pdfs, folders);
                _webView.CoreWebView2.PostWebMessageAsString(json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfsToWebView failed: {ex.Message}");
                DocuLinkLog.Trace($"EXCEPTION {ex.GetType().FullName}: {ex.Message}");
                return false;
            }
            }
        }

        /// <summary>
        /// Pushes the current folder catalogue and every PDF's folder assignment to the viewer.
        /// Sent after file-manager folder mutations so the viewer's folder filter stays in sync
        /// without reloading PDF bytes.
        /// </summary>
        internal void SendFoldersToWebView()
        {
            if (_disposed) return;
            if (!_webViewReady) return;

            try
            {
                DocuLinkContent content = new DocuLinkCustomXmlPartStore(_workbook).LoadContent();
                string json = HostMessageSerializer.BuildViewerFoldersUpdated(
                    content.Folders, content.Pdfs);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendFoldersToWebView failed: {ex.Message}");
            }
        }

        internal void SendPdfUpdated(string pdfId)
        {
            if (_disposed) return;
            if (!_webViewReady || string.IsNullOrWhiteSpace(pdfId))
                return;

            try
            {
                var store = new DocuLinkCustomXmlPartStore(_workbook);
                if (!store.TryGetPdf(pdfId, out PdfDocument pdf))
                    return;

                string json = HostMessageSerializer.BuildPdfUpdated(pdf);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfUpdated failed: {ex.Message}");
            }
        }

        internal void SendPdfAdded(string pdfId)
        {
            if (_disposed) return;
            if (!_webViewReady || string.IsNullOrWhiteSpace(pdfId))
                return;

            try
            {
                var store = new DocuLinkCustomXmlPartStore(_workbook);
                if (!store.TryGetPdf(pdfId, out PdfDocument pdf))
                    return;

                string json = HostMessageSerializer.BuildPdfAdded(pdf);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfAdded failed: {ex.Message}");
            }
        }

        internal void SendPdfNameUpdated(string id, string name)
        {
            if (_disposed) return;
            if (!_webViewReady || string.IsNullOrWhiteSpace(id))
                return;

            try
            {
                string json = HostMessageSerializer.BuildPdfNameUpdated(id, name);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfNameUpdated failed: {ex.Message}");
            }
        }

        internal void SendPdfRemoved(string id)
        {
            if (_disposed) return;
            if (!_webViewReady || string.IsNullOrWhiteSpace(id))
                return;

            try
            {
                string json = HostMessageSerializer.BuildPdfRemoved(id);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfRemoved failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Switches the viewer to <paramref name="pdfId"/>. Unlike navigate-to-rectangle
        /// this is never queued: it reflects a transient file-manager selection, and
        /// replaying it once a viewer finally opens would override the document the
        /// viewer picks for itself.
        /// </summary>
        internal void SendShowPdf(string pdfId)
        {
            if (_disposed) return;
            if (!_webViewReady || string.IsNullOrWhiteSpace(pdfId))
                return;

            try
            {
                string json = HostMessageSerializer.BuildShowPdf(pdfId);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendShowPdf failed: {ex.Message}");
            }
        }

        private static string GetWebUiPath()
        {
            string codeBase = Assembly.GetExecutingAssembly().CodeBase;
            string addinDir = Path.GetDirectoryName(new Uri(codeBase).LocalPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(addinDir, "webui");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DocuLinkLog.Trace($"ENTER surface={_loadFailureSurfaceName}");

            try
            {
                _cacheProgress?.Dispose();
                _cacheProgress = null;
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"cache progress dispose failed: {ex.Message}");
            }

            try
            {
                _focusRestoreService.Dispose();
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"focus restore dispose failed: {ex.Message}");
            }

            try
            {
                _webView.Leave -= OnWebViewLeave;
                if (_webView.CoreWebView2 != null)
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"webview event detach failed: {ex.Message}");
            }

            try
            {
                _webView.Dispose();
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"webview dispose failed: {ex.Message}");
            }

            try
            {
                _startupPlaceholder.Dispose();
                _surface.Dispose();
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"surface dispose failed: {ex.Message}");
            }

            DocuLinkLog.Trace($"EXIT surface={_loadFailureSurfaceName}");
        }
    }
}
