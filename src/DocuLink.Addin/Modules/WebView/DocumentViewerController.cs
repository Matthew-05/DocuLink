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
        private bool _disposed;

        internal DocumentViewerController(Control invokeTarget, string loadFailureSurfaceName)
        {
            _invokeTarget = invokeTarget ?? throw new ArgumentNullException(nameof(invokeTarget));
            _loadFailureSurfaceName = loadFailureSurfaceName ?? "viewer";

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
                        _webShellReady = true;
                        break;

                    case "viewer-ready":
                        if (!_webShellReady)
                        {
                            _webShellReady = true;
                        }
                        _webViewReady = true;
                        if (_viewerShown)
                        {
                            if (!_dataSentToViewer)
                            {
                                SendPdfsToWebView();
                                _dataSentToViewer = true;
                            }
                            SendLinkedRectanglesToWebView();
                            FlushPendingNavigateToRectangle();
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

        private void RestoreExcelFocus()
        {
            ExcelGridFocusRestoreService.RestoreExcelFocus();
        }

        private void HandleLinkRectangleCreated(string json)
        {
            var payload = HostMessageParser.ParseLinkRectangleCreated(json);
            if (payload == null) return;

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

            _invokeTarget.BeginInvoke(new Action(() => ExecuteLinkRectangleCreated(payload, wb)));
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
                    DocuLinkLog.Trace($"selection before CreateLink: {sel?.get_Address() ?? "null"}");
                    DocuLinkLog.Trace($"active cell before CreateLink: {(Globals.ThisAddIn.Application?.ActiveCell as Excel.Range)?.get_Address() ?? "null"}, value={((Globals.ThisAddIn.Application?.ActiveCell as Excel.Range)?.Value2 ?? "(null)")}");
                }
                catch (Exception ex) { DocuLinkLog.Trace($"pre-create cell read failed: {ex.Message}"); }

                string text = payload.Text;
                if (string.IsNullOrWhiteSpace(text))
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
                        wb);
                }
                DocuLinkLog.Trace($"CreateLink returned id={linkedRect?.Id ?? "null"}");

                try
                {
                    var ac = Globals.ThisAddIn.Application?.ActiveCell as Excel.Range;
                    DocuLinkLog.Trace($"active cell after CreateLink: {ac?.get_Address() ?? "null"}, value={ac?.Value2 ?? "(null)"}");
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

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

            IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
            if (!WorkbookProtectionGuard.TryRequireWritable(wb, owner))
                return;

            string text = payload.Text;
            if (string.IsNullOrWhiteSpace(text))
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
                wb);

            SendLinkedRectanglesToWebView();

            RestoreExcelFocus();
        }

        private void HandleLinkRectangleClicked(string json)
        {
            string rectId = HostMessageParser.ParseLinkRectangleClicked(json);
            if (string.IsNullOrWhiteSpace(rectId)) return;

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

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
            string rectId = HostMessageParser.ParseLinkRectangleDeleted(json);
            if (string.IsNullOrWhiteSpace(rectId)) return;

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

            IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
            if (!WorkbookProtectionGuard.TryRequireWritable(wb, owner))
                return;

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                if (!new DeleteLinkService().DeleteLink(rectId, wb))
                    return;

                SendLinkRectanglesRemoved(new[] { rectId });
            }

            Globals.ThisAddIn.NotifyFileManagerLinksChanged();

            RestoreExcelFocus();
        }

        private void HandleRotatePage(string json)
        {
            var payload = HostMessageParser.ParseRotatePage(json);
            if (payload == null) return;

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

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
            SendPdfsToWebView();
            _dataSentToViewer = true;
            SendLinkedRectanglesToWebView();
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
            if (!_viewerShown)
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
                Excel.Application app = Globals.ThisAddIn.Application;
                Excel.Workbook workbook = app?.ActiveWorkbook;
                if (workbook == null)
                    return;

                PostLinkedRectangles(Globals.ThisAddIn.GetStorageSession(workbook).GetLinks());
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

        internal void SendPdfsToWebView()
        {
            using (DocuLinkLog.Time($"SendPdfsToWebView surface={_loadFailureSurfaceName}"))
            {
            if (_disposed) return;
            try
            {
                Excel.Application app = Globals.ThisAddIn.Application;
                Excel.Workbook workbook = app?.ActiveWorkbook;
                IList<PdfDocument> pdfs = workbook == null
                    ? new List<PdfDocument>()
                    : new DocuLinkCustomXmlPartStore(workbook).LoadAllPdfsWithBinary();
                string json = HostMessageSerializer.BuildPdfsLoaded(pdfs);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfsToWebView failed: {ex.Message}");
                DocuLinkLog.Trace($"EXCEPTION {ex.GetType().FullName}: {ex.Message}");
            }
            }
        }

        internal void SendPdfUpdated(string pdfId)
        {
            if (_disposed) return;
            if (!_webViewReady || string.IsNullOrWhiteSpace(pdfId))
                return;

            try
            {
                Excel.Application app = Globals.ThisAddIn.Application;
                Excel.Workbook workbook = app?.ActiveWorkbook;
                if (workbook == null)
                    return;

                var store = new DocuLinkCustomXmlPartStore(workbook);
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
                Excel.Application app = Globals.ThisAddIn.Application;
                Excel.Workbook workbook = app?.ActiveWorkbook;
                if (workbook == null)
                    return;

                var store = new DocuLinkCustomXmlPartStore(workbook);
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
