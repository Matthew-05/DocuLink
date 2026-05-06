using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Hosts the document-viewer web UI inside a WebView2 control.</summary>
    public sealed class TaskPaneHost : UserControl
    {
        private readonly WebView2 _webView = new WebView2();

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

                // Minimal parse: look for "viewer-ready" type without pulling in a JSON library.
                if (raw.Contains("\"viewer-ready\""))
                    SendPdfsToWebView();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] OnWebMessageReceived failed: {ex.Message}");
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

                string json = BuildPdfsLoadedJson(storage.Pdfs);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                // Non-fatal — the pane will just show an empty selector.
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendPdfsToWebView failed: {ex.Message}");
            }
        }

        private static string BuildPdfsLoadedJson(System.Collections.Generic.IList<PdfDocument> pdfs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"pdfs-loaded\",\"pdfs\":[");

            for (int i = 0; i < pdfs.Count; i++)
            {
                PdfDocument pdf = pdfs[i];
                if (i > 0) sb.Append(',');

                sb.Append('{');
                sb.Append("\"id\":"); AppendJsonString(sb, pdf.Id);
                sb.Append(",\"name\":"); AppendJsonString(sb, pdf.Name ?? string.Empty);
                sb.Append(",\"base64\":"); AppendJsonString(sb, pdf.Base64 ?? string.Empty);
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:x4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
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
