using System;
using System.IO;
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
    /// <summary>Hosts the document-viewer web UI inside a WebView2 control.</summary>
    public sealed class TaskPaneHost : UserControl
    {
        private readonly WebView2 _webView = new WebView2();
        private ProgressScope _cacheProgressScope;

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
                        SendPdfsToWebView();
                        SendLinkedRectanglesToWebView();
                        break;

                    case "link-rectangle-created":
                        HandleLinkRectangleCreated(raw);
                        break;

                    case "cache-build-started":
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

            new CreateLinkService().CreateLink(
                payload.PdfId,
                payload.Page,
                payload.X, payload.Y, payload.Width, payload.Height,
                payload.Text,
                wb);

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
