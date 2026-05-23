using System;
using System.Collections.Generic;
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
    /// <summary>Hosts the file-manager web UI in a standalone non-modal window.</summary>
    /// <remarks>
    /// Explorer → WebView2 file drops often do not surface HTML5 drop events inside Office/WinForms;
    /// Chromium may navigate to file:/// URLs instead. This host cancels navigation for those
    /// file URIs and imports paths via <see cref="ManageFilesService"/> (WinForms
    /// <see cref="Control.AllowDrop"/> on <see cref="WebView2"/> is unavailable in SDK versions
    /// where that property is read-only). Multi-file Explorer drops rely on sequential
    /// file-navigation cancels where the runtime emits them.
    /// </remarks>
    public sealed class FileManagerHost : Form
    {
        private readonly WebView2 _webView = new WebView2();
        private readonly ManageFilesService _service = new ManageFilesService();
        private OcrService _ocrService;

        /// <summary>The folder GUID currently selected in the web UI (<c>null</c> for All Files).</summary>
        private string _selectedFolderId;

        private bool _webViewReady;

        private readonly object _osImportLock = new object();
        private readonly Dictionary<string, long> _recentOsImportTicks =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public FileManagerHost()
        {
            Text = "DocuLink – Manage Files";
            // OcrService needs a Control reference for UI-thread marshalling;
            // created here after the Form's handle is available.
            _ocrService = new OcrService(this);
            Width = 900;
            Height = 620;
            MinimumSize = new System.Drawing.Size(700, 480);
            StartPosition = FormStartPosition.CenterScreen;

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

                // Reuse the same virtual host as TaskPaneHost; the mapping is per-process and
                // idempotent — setting it again with the same folder is harmless.
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "doculink.local",
                    uiPath,
                    CoreWebView2HostResourceAccessKind.Allow);

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;

                _webView.CoreWebView2.Navigate("https://doculink.local/file-manager/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"DocuLink file manager failed to load:\n\n{ex.Message}",
                    "DocuLink",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void CoreWebView2_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Uri))
                return;
            if (!e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return;

            e.Cancel = true;

            string localPath;
            try
            {
                localPath = new Uri(e.Uri).LocalPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] file: navigation parse failed: {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(localPath))
                return;

            ProcessOsPaths(new[] { localPath });
        }

        /// <summary>Imports PDFs (or folders of PDFs) dropped from the OS. De-duplicates rapid double delivery (NavigationStarting + DragDrop).</summary>
        private void ProcessOsPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return;

            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null)
            {
                System.Diagnostics.Debug.WriteLine("[DocuLink] OS drop ignored: no active workbook.");
                return;
            }

            var folderIdCache = new Dictionary<string, string>(StringComparer.Ordinal);
            int added = 0;

            using (new ProgressScope("Importing documents\u2026"))
            {
                foreach (string raw in paths)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    string path;
                    try
                    {
                        path = Path.GetFullPath(raw.Trim());
                    }
                    catch
                    {
                        continue;
                    }

                    if (ShouldSkipDuplicateOsImport(path))
                        continue;

                    try
                    {
                        FileAttributes attr = File.GetAttributes(path);
                        if ((attr & FileAttributes.Directory) == FileAttributes.Directory)
                            added += ImportDirectoryOfPdfs(wb, path, folderIdCache);
                        else if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            _service.AddPdfFromFilePath(wb, path, _selectedFolderId);
                            added++;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DocuLink] OS import failed for '{path}': {ex.Message}");
                    }
                }
            }

            if (added > 0)
            {
                SendFilesToWebView();
                Globals.ThisAddIn.RefreshTaskPanePdfs();
            }
        }

        private int ImportDirectoryOfPdfs(Excel.Workbook wb, string dirPath, Dictionary<string, string> folderIdCache)
        {
            string folderName;
            try
            {
                folderName = new DirectoryInfo(dirPath).Name;
            }
            catch
            {
                return 0;
            }

            string sentinel = "__new__:" + folderName;
            string folderId = ResolveFolderId(wb, sentinel, folderIdCache);
            int count = 0;

            foreach (string pdfPath in Directory.EnumerateFiles(dirPath, "*.pdf", SearchOption.AllDirectories))
            {
                string full;
                try
                {
                    full = Path.GetFullPath(pdfPath);
                }
                catch
                {
                    continue;
                }

                if (ShouldSkipDuplicateOsImport(full))
                    continue;

                try
                {
                    _service.AddPdfFromFilePath(wb, full, folderId);
                    count++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DocuLink] OS import failed for '{full}': {ex.Message}");
                }
            }

            return count;
        }

        private bool ShouldSkipDuplicateOsImport(string fullPath)
        {
            lock (_osImportLock)
            {
                long now = DateTime.UtcNow.Ticks;
                const long window = 750 * TimeSpan.TicksPerMillisecond;

                if (_recentOsImportTicks.TryGetValue(fullPath, out long prev) && (now - prev) < window)
                    return true;

                _recentOsImportTicks[fullPath] = now;

                if (_recentOsImportTicks.Count > 96)
                {
                    long cutoff = now - 2 * TimeSpan.TicksPerSecond;
                    foreach (string key in _recentOsImportTicks.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                        _recentOsImportTicks.Remove(key);
                }

                return false;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                string type = FileManagerMessageParser.GetMessageType(raw);
                switch (type)
                {
                    case "manager-ready":
                        _webViewReady = true;
                        SendFilesToWebView();
                        break;

                    case "set-selected-folder":
                        _selectedFolderId = FileManagerMessageParser.ParseSetSelectedFolder(raw);
                        break;

                    case "add-files":
                        HandleAddFiles(FileManagerMessageParser.ParseAddFiles(raw));
                        break;

                    case "rename-file":
                        HandleRenameFile(FileManagerMessageParser.ParseRenameFile(raw));
                        break;

                    case "remove-file":
                        HandleRemoveFile(FileManagerMessageParser.ParseRemoveFile(raw));
                        break;

                    case "move-file":
                        HandleMoveFile(FileManagerMessageParser.ParseMoveFile(raw));
                        break;

                    case "add-folder":
                        HandleAddFolder(FileManagerMessageParser.ParseAddFolder(raw));
                        break;

                    case "rename-folder":
                        HandleRenameFolder(FileManagerMessageParser.ParseRenameFolder(raw));
                        break;

                    case "remove-folder":
                        HandleRemoveFolder(FileManagerMessageParser.ParseRemoveFolder(raw));
                        break;

                    case "ocr-pdfs":
                        _ = HandleOcrPdfsAsync(FileManagerMessageParser.ParseOcrPdfs(raw));
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] FileManagerHost.OnWebMessageReceived failed: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task HandleOcrPdfsAsync(OcrPdfsRequest req)
        {
            if (req?.PdfIds == null || req.PdfIds.Count == 0) return;

            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;

            bool anyComplete = false;

            await _ocrService.RunOcrAsync(
                req.PdfIds,
                wb,
                onStatusUpdate: (pdfId, status, message) =>
                {
                    string json = FileManagerMessageSerializer.BuildOcrStatus(pdfId, status, message);
                    _webView.CoreWebView2?.PostWebMessageAsString(json);

                    if (status == PdfStatus.Ocr)
                    {
                        anyComplete = true;
                        Globals.ThisAddIn.RefreshTaskPanePdf(pdfId);
                    }
                });

            if (anyComplete)
            {
                SendFilesToWebView();
                Globals.ThisAddIn.RefreshTaskPanePdfs();
            }
        }

        private void HandleAddFiles(AddFilesRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;

            // Cache resolved folder ids so that a dropped directory only creates one folder
            // even if it contains many files.
            var resolvedFolderIds = new Dictionary<string, string>(StringComparer.Ordinal);

            using (new ProgressScope("Importing documents\u2026"))
            {
                foreach (var file in req.Files)
                {
                    try
                    {
                        string folderId = ResolveFolderId(wb, file.FolderId, resolvedFolderIds);
                        _service.AddPdf(wb, file.Name, file.Base64, folderId);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DocuLink] AddPdf failed for '{file.Name}': {ex.Message}");
                    }
                }
            }

            SendFilesToWebView();
            Globals.ThisAddIn.RefreshTaskPanePdfs();
        }

        /// <summary>
        /// Resolves the folderId for a file. Handles the "__new__:FolderName" sentinel that
        /// the dropzone sends when a directory is dropped — the folder is created on first
        /// encounter and its new GUID is cached for subsequent files in the same batch.
        /// </summary>
        private string ResolveFolderId(
            Excel.Workbook wb,
            string folderId,
            Dictionary<string, string> cache)
        {
            if (string.IsNullOrEmpty(folderId))
                return null;

            const string newPrefix = "__new__:";
            if (!folderId.StartsWith(newPrefix, StringComparison.Ordinal))
                return folderId;

            if (cache.TryGetValue(folderId, out string cached))
                return cached;

            string folderName = folderId.Substring(newPrefix.Length);
            string newId = _service.AddFolder(wb, folderName);
            cache[folderId] = newId;
            return newId;
        }

        private void HandleRenameFile(RenameFileRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            _service.RenamePdf(wb, req.Id, req.NewName);
            SendFilesToWebView();
            Globals.ThisAddIn.RefreshTaskPanePdfs();
        }

        private void HandleRemoveFile(RemoveFileRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            _service.RemovePdf(wb, req.Id);
            SendFilesToWebView();
            Globals.ThisAddIn.RefreshTaskPanePdfs();
        }

        private void HandleMoveFile(MoveFileRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            _service.MoveFile(wb, req.Id, req.FolderId);
            SendFilesToWebView();
        }

        private void HandleAddFolder(AddFolderRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            _service.AddFolder(wb, req.Name);
            SendFilesToWebView();
        }

        private void HandleRenameFolder(RenameFolderRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            _service.RenameFolder(wb, req.Id, req.NewName);
            SendFilesToWebView();
        }

        private void HandleRemoveFolder(RemoveFolderRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            _service.RemoveFolder(wb, req.Id);
            SendFilesToWebView();
        }

        /// <summary>
        /// Pushes the current workbook's file list to the web UI, but only if the web app
        /// has already signalled <c>manager-ready</c>. Call this whenever the window is
        /// shown after a warm-load so the web UI receives data unavailable at pre-init time.
        /// </summary>
        public void RefreshDataIfReady()
        {
            if (!_webViewReady) return;
            SendFilesToWebView();
        }

        /// <summary>Reads the active workbook's file list and pushes it to the web UI.</summary>
        public void SendFilesToWebView()
        {
            try
            {
                Excel.Workbook wb = GetActiveWorkbook();
                if (wb == null) return;

                var store = new DocuLinkCustomXmlPartStore(wb);
                DocuLinkContent content = store.LoadContent();

                string json = FileManagerMessageSerializer.BuildFilesLoaded(content.Folders, content.Pdfs);
                _webView.CoreWebView2?.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] SendFilesToWebView failed: {ex.Message}");
            }
        }

        private static Excel.Workbook GetActiveWorkbook()
        {
            return Globals.ThisAddIn.Application?.ActiveWorkbook;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SendResetUiToWebView();
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void SendResetUiToWebView()
        {
            if (!_webViewReady) return;

            try
            {
                _webView.CoreWebView2?.PostWebMessageAsString(
                    HostMessageSerializer.BuildResetUi());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DocuLink] SendResetUiToWebView failed: {ex.Message}");
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
