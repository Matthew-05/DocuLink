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
    /// <summary>Shared WebView2 + messaging logic for the document-viewer web app.</summary>
    internal sealed class DocumentViewerController
    {
        // SetFocus (not SetForegroundWindow) is required: it directly reassigns which
        // child HWND receives WM_KEYDOWN, sending WM_KILLFOCUS to the Chromium window
        // inside WebView2 and WM_SETFOCUS to the Excel workbook grid window.
        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private readonly Control _invokeTarget;
        private readonly string _loadFailureSurfaceName;
        private readonly WebView2 _webView = new WebView2();
        private ProgressScope _cacheProgressScope;
        private bool _webViewReady;

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

            SendLinkedRectanglesToWebView();

            if (linkedRect != null)
                SendNavigateToRectangle(linkedRect.Id, linkedRect.PdfId, linkedRect.Rectangle.PageIndex);

            _invokeTarget.BeginInvoke(new Action(() =>
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

            Globals.ThisAddIn.SuppressNextSelectionNav = true;

            new LinkNavigationService().NavigateToLinkedCell(rectId, wb);
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

        internal void RefreshDataIfReady()
        {
            if (!_webViewReady) return;
            SendPdfsToWebView();
            SendLinkedRectanglesToWebView();
        }

        internal void SendLinkedRectanglesToWebView()
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

        internal void SendPdfsToWebView()
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
            string codeBase = Assembly.GetExecutingAssembly().CodeBase;
            string addinDir = Path.GetDirectoryName(new Uri(codeBase).LocalPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(addinDir, "webui");
        }
    }
}
