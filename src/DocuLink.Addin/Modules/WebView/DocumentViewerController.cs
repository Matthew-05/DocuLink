using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
    internal sealed class DocumentViewerController
    {
        // Transfers Win32 keyboard focus to the target HWND, sending WM_KILLFOCUS to
        // the previously focused window (the WebView2 Chromium child) and WM_SETFOCUS
        // to the target. Must target Application.Hwnd (XLMAIN), not Window.Hwnd (the
        // workbook frame), to avoid blanking the formula bar.
        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private readonly Control _invokeTarget;
        private readonly string _loadFailureSurfaceName;
        private readonly WebView2 _webView = new WebView2();
        private ProgressScope _cacheProgressScope;
        private bool _webViewReady;
        private bool _dataSentToViewer;
        private bool _viewerShown;
        private string _pendingNavigateId;
        private string _pendingNavigatePdfId;
        private int? _pendingNavigatePage;

        internal DocumentViewerController(Control invokeTarget, string loadFailureSurfaceName)
        {
            _invokeTarget = invokeTarget ?? throw new ArgumentNullException(nameof(invokeTarget));
            _loadFailureSurfaceName = loadFailureSurfaceName ?? "viewer";

            _webView.Dock = DockStyle.Fill;
            _ = InitAsync();
        }

        internal WebView2 WebView => _webView;

        private async Task InitAsync()
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DocuLink", "WebView2");

                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);

                await _webView.EnsureCoreWebView2Async(environment);

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
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"DocuLink {_loadFailureSurfaceName} failed to load:\n\n{ex.Message}",
                    "DocuLink",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                switch (HostMessageParser.GetMessageType(raw))
                {
                    case "viewer-ready":
                        _webViewReady = true;
                        bool hasPendingNavigate = _pendingNavigatePage != null;
                        if ((hasPendingNavigate || _viewerShown) && !_dataSentToViewer)
                        {
                            SendPdfsToWebView();
                            _dataSentToViewer = true;
                        }
                        SendLinkedRectanglesToWebView();
                        FlushPendingNavigateToRectangle();
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

                    case "cache-build-started":
                        _cacheProgressScope?.Dispose();
                        _cacheProgressScope = new ProgressScope("Building document index\u2026");
                        break;

                    case "cache-build-complete":
                        _cacheProgressScope?.Dispose();
                        _cacheProgressScope = null;
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] OnWebMessageReceived failed: {ex.Message}");
            }
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
                    IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
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
                try
                {
                    int appHwnd = Globals.ThisAddIn.Application?.Hwnd ?? 0;
                    if (appHwnd != 0)
                    {
                        IntPtr prev = SetFocus(new IntPtr(appHwnd));
                        DocuLinkLog.Trace($"SetFocus(Application.Hwnd) success, prev=0x{prev.ToInt64():X}");
                    }
                    else
                    {
                        DocuLinkLog.Trace("SetFocus skipped: Application.Hwnd is 0");
                    }
                }
                catch (Exception ex)
                {
                    DocuLinkLog.Trace($"SetFocus failed: {ex.Message}");
                }

                DocuLinkLog.Trace("EXIT");
            }
        }

        private void HandleLinkRectangleUpdated(string json)
        {
            var payload = HostMessageParser.ParseLinkRectangleUpdated(json);
            if (payload == null) return;

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

            string text = payload.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                IWin32Window owner = _invokeTarget.FindForm() ?? _invokeTarget;
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

            try
            {
                int appHwnd = Globals.ThisAddIn.Application?.Hwnd ?? 0;
                if (appHwnd != 0)
                {
                    SetFocus(new IntPtr(appHwnd));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] HandleLinkRectangleUpdated focus restore failed: {ex.Message}");
            }
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
                int appHwnd = Globals.ThisAddIn.Application?.Hwnd ?? 0;
                if (appHwnd != 0)
                    SetFocus(new IntPtr(appHwnd));
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

            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                if (!new DeleteLinkService().DeleteLink(rectId, wb))
                    return;

                SendLinkRectanglesRemoved(new[] { rectId });
            }

            Globals.ThisAddIn.NotifyFileManagerLinksChanged();
        }

        private void HandleRotatePage(string json)
        {
            var payload = HostMessageParser.ParseRotatePage(json);
            if (payload == null) return;

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

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
            if (!_webViewReady || _dataSentToViewer) return;
            SendPdfsToWebView();
            SendLinkedRectanglesToWebView();
        }

        internal void InvalidateData()
        {
            _dataSentToViewer = false;
        }

        internal void NotifyViewerShown()
        {
            _viewerShown = true;
            RefreshDataIfReady();
        }

        internal void SendLinkedRectanglesToWebView()
        {
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
            }
        }

        /// <summary>
        /// Sends a pre-loaded linked-rectangles list to the viewer without reloading
        /// from storage. Use this when the caller already has the current list in memory
        /// (e.g. immediately after CreateLink returns) to avoid a redundant XML load.
        /// </summary>
        internal void SendLinkedRectanglesToWebView(IList<LinkedRectangle> linkedRectangles)
        {
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
            if (!_webViewReady) return;
            string json = HostMessageSerializer.BuildLinkedRectanglesLoaded(linkedRectangles);
            _webView.CoreWebView2.PostWebMessageAsString(json);
        }

        internal void SendPdfsToWebView()
        {
            try
            {
                Excel.Application app = Globals.ThisAddIn.Application;
                Excel.Workbook workbook = app?.ActiveWorkbook;
                if (workbook == null)
                    return;

                var store = new DocuLinkCustomXmlPartStore(workbook);
                string json = HostMessageSerializer.BuildPdfsLoaded(store.LoadAllPdfsWithBinary());
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfsToWebView failed: {ex.Message}");
            }
        }

        internal void SendPdfUpdated(string pdfId)
        {
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
    }
}
