using System;
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
    /// <summary>Hosts the document-viewer web UI inside a WebView2 control.</summary>
    public sealed class TaskPaneHost : UserControl
    {
        // SetFocus (not SetForegroundWindow) is required: it directly reassigns which
        // child HWND receives WM_KEYDOWN, sending WM_KILLFOCUS to the Chromium window
        // inside WebView2 and WM_SETFOCUS to the Excel workbook grid window.
        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private readonly WebView2 _webView = new WebView2();
        private ProgressScope _cacheProgressScope;
        private bool _webViewReady;

        public TaskPaneHost()
        {
            Dock = DockStyle.Fill;
            _webView.Dock = DockStyle.Fill;
            Controls.Add(_webView);

            _ = InitAsync();
        }

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
                    $"DocuLink task pane failed to load:\n\n{ex.Message}",
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
                        SendPdfsToWebView();
                        SendLinkedRectanglesToWebView();
                        break;

                    case "link-rectangle-created":
                        HandleLinkRectangleCreated(raw);
                        break;

                    case "link-rectangle-clicked":
                        HandleLinkRectangleClicked(raw);
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

            var linkedRect = new CreateLinkService().CreateLink(
                payload.PdfId,
                payload.Page,
                payload.X, payload.Y, payload.Width, payload.Height,
                payload.Text,
                wb);

            // Push updated rectangles first so the div exists when navigate arrives.
            SendLinkedRectanglesToWebView();

            // Highlight the newly created rectangle.
            if (linkedRect != null)
                SendNavigateToRectangle(linkedRect.Id, linkedRect.PdfId, linkedRect.Rectangle.PageIndex);

            // WebView2's Chromium child window holds Win32 keyboard focus after the
            // drag ends. COM calls (e.g. ActiveCell.Select) only update Excel's data
            // model and do not move the Win32 focus HWND, so keyboard events keep
            // going to WebView2. SetFocus on the workbook window (Application.ActiveWindow.Hwnd,
            // the EXCEL7 class window that hosts the cell grid) sends WM_KILLFOCUS to
            // Chromium and WM_SETFOCUS to the grid — the same path as clicking the
            // formula bar. BeginInvoke defers this until after WebView2 finishes its
            // own post-mouseup event processing, which would otherwise re-assert focus.
            BeginInvoke(new Action(() =>
            {
                try
                {
                    var window = Globals.ThisAddIn.Application?.ActiveWindow;
                    if (window != null)
                        SetFocus(new IntPtr(window.Hwnd));
                }
                catch { }
            }));
        }

        private void HandleLinkRectangleClicked(string json)
        {
            string rectId = HostMessageParser.ParseLinkRectangleClicked(json);
            if (string.IsNullOrWhiteSpace(rectId)) return;

            Excel.Workbook wb = Globals.ThisAddIn.Application?.ActiveWorkbook;
            if (wb == null) return;

            // Suppress the SheetSelectionChange handler so the programmatic
            // cell selection we're about to make doesn't bounce a navigate-to-
            // rectangle message back to the viewer.
            Globals.ThisAddIn.SuppressNextSelectionNav = true;

            new LinkNavigationService().NavigateToLinkedCell(rectId, wb);
        }

        /// <summary>
        /// Posts a <c>clear-rectangle-highlight</c> message to the viewer to remove
        /// any active rectangle highlight. No-ops if the WebView2 control is not yet ready.
        /// </summary>
        public void SendClearRectangleHighlight()
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

        /// <summary>
        /// Posts a <c>navigate-to-rectangle</c> message to the viewer so it can
        /// jump to and highlight the rectangle associated with a selected cell.
        /// No-ops if the WebView2 control is not yet ready.
        /// </summary>
        public void SendNavigateToRectangle(string id, string pdfId, int page)
        {
            if (!_webViewReady) return;

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

        /// <summary>
        /// Pushes both the PDF list and linked rectangles to the web UI, but only
        /// if the web app has already signalled <c>viewer-ready</c>. Call this
        /// whenever the task pane becomes visible after a warm-load so the web UI
        /// receives data that was unavailable at pre-init time.
        /// </summary>
        public void RefreshDataIfReady()
        {
            if (!_webViewReady) return;
            SendPdfsToWebView();
            SendLinkedRectanglesToWebView();
        }

        /// <summary>
        /// Reads all persisted linked rectangles from the active workbook and
        /// pushes them to the web UI via a <c>linked-rectangles-loaded</c> message.
        /// </summary>
        public void SendLinkedRectanglesToWebView()
        {
            try
            {
                Excel.Application app = Globals.ThisAddIn.Application;
                Excel.Workbook workbook = app?.ActiveWorkbook;
                if (workbook == null)
                    return;

                var store = new DocuLinkCustomXmlPartStore(workbook);
                DocuLinkStorage storage = store.Load();

                string json = HostMessageSerializer.BuildLinkedRectanglesLoaded(storage.LinkedRectangles);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendLinkedRectanglesToWebView failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the active workbook's embedded PDFs and pushes them to the web UI.
        /// Call this after adding or removing a PDF so the selector stays in sync.
        /// </summary>
        public void SendPdfsToWebView()
        {
            try
            {
                Excel.Application app = Globals.ThisAddIn.Application;
                Excel.Workbook workbook = app?.ActiveWorkbook;
                if (workbook == null)
                    return;

                var store = new DocuLinkCustomXmlPartStore(workbook);
                DocuLinkStorage storage = store.Load();

                string json = HostMessageSerializer.BuildPdfsLoaded(storage.Pdfs);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                // Non-fatal — the pane will just show an empty selector.
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfsToWebView failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pushes updated bytes for a single PDF to the web UI. Used after OCR so
        /// an already-loaded document is refreshed without re-sending the full list.
        /// </summary>
        public void SendPdfUpdated(string pdfId)
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
                DocuLinkStorage storage = store.Load();

                PdfDocument pdf = storage.Pdfs.FirstOrDefault(
                    p => string.Equals(p.Id, pdfId, StringComparison.Ordinal));
                if (pdf == null)
                    return;

                string json = HostMessageSerializer.BuildPdfUpdated(pdf);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfUpdated failed: {ex.Message}");
            }
        }

        private static string GetWebUiPath()
        {
            // Use CodeBase (not Location) so we get the true on-disk path even when
            // the CLR host shadow-copies the assembly to a temp/AppData directory.
            string codeBase = Assembly.GetExecutingAssembly().CodeBase;
            string addinDir = Path.GetDirectoryName(new Uri(codeBase).LocalPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;

            // MSBuild copies dist/ to webui/ next to the DLL for all configurations.
            return Path.Combine(addinDir, "webui");
        }
    }
}
