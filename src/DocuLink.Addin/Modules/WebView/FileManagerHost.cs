using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    /// <summary>
    /// Layout constants for native dropzone panel positioning and sizing.
    /// MUST match corresponding CSS values in src/web/apps/file-manager/src/styles/:
    /// - SidebarWidth (260) ↔ grid-template-columns: 260px 1fr (layout.css:5)
    /// - DropZoneHeight (122) ↔ .native-dropzone-spacer { height: 122px; } (folder-panel.css:5)
    /// - Gap (12) ↔ margin-top/bottom: 12px (folder-panel.css:6-7)
    /// </summary>
    internal static class DropzoneLayout
    {
        public const int SidebarWidth = 260;
        public const int Gap = 12;
        public const int DropZoneHeight = 122;
        public const int MinWidth = 160;
    }

    /// <summary>Hosts the file-manager web UI in a standalone non-modal window.</summary>
    /// <remarks>
    /// Explorer → WebView2 file drops often do not surface HTML5 drop events inside Office/WinForms;
    /// Chromium may navigate to file:/// URLs instead. This host disables WebView2 external
    /// drops so WinForms can receive real <see cref="DataFormats.FileDrop"/> paths and imports
    /// them via <see cref="PdfImportService"/>. The navigation-cancellation handler (CoreWebView2_NavigationStarting)
    /// remains active to catch any file:/// URLs that slip through as an extra safeguard.
    /// </remarks>
    public sealed class FileManagerHost : Form
    {
        private readonly WebView2 _webView = new WebView2();
        private readonly NativeDropZonePanel _nativeDropZone = new NativeDropZonePanel();
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
            AllowDrop = true;

            _webView.Dock = DockStyle.Fill;
            _webView.DragEnter += NativeFileDrop_DragEnter;
            _webView.DragOver += NativeFileDrop_DragEnter;
            _webView.DragDrop += NativeFileDrop_DragDrop;
            Controls.Add(_webView);

            _nativeDropZone.AllowDrop = true;
            _nativeDropZone.Click += (sender, args) => ShowPdfFilePicker();
            _nativeDropZone.DragEnter += NativeFileDrop_DragEnter;
            _nativeDropZone.DragOver += NativeFileDrop_DragEnter;
            _nativeDropZone.DragLeave += NativeFileDrop_DragLeave;
            _nativeDropZone.DragDrop += NativeFileDrop_DragDrop;
            Controls.Add(_nativeDropZone);

            DragEnter += NativeFileDrop_DragEnter;
            DragOver += NativeFileDrop_DragEnter;
            DragLeave += NativeFileDrop_DragLeave;
            DragDrop += NativeFileDrop_DragDrop;

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
                _webView.AllowExternalDrop = false;

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
                _webView.CoreWebView2.NavigationCompleted += (sender, args) => PositionNativeDropZone();

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

        private void NativeFileDrop_DragEnter(object sender, DragEventArgs e)
        {
            if (GetDroppedPaths(e.Data).Length == 0)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            e.Effect = DragDropEffects.Copy;
            _nativeDropZone.SetDragOver(true);
        }

        private void NativeFileDrop_DragLeave(object sender, EventArgs e)
        {
            _nativeDropZone.SetDragOver(false);
        }

        private void NativeFileDrop_DragDrop(object sender, DragEventArgs e)
        {
            _nativeDropZone.SetDragOver(false);

            string[] paths = GetDroppedPaths(e.Data);
            if (paths.Length == 0)
                return;

            BeginInvoke(new Action(() => ProcessOsPaths(paths)));
        }

        private static string[] GetDroppedPaths(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
                return Array.Empty<string>();

            return data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        }

        private void PositionNativeDropZone()
        {
            int width = Math.Max(DropzoneLayout.MinWidth, Math.Min(DropzoneLayout.SidebarWidth - DropzoneLayout.Gap * 2, ClientSize.Width - DropzoneLayout.Gap * 2));
            width = Math.Min(width, Math.Max(0, ClientSize.Width - DropzoneLayout.Gap * 2));

            _nativeDropZone.SetBounds(
                DropzoneLayout.Gap,
                ClientSize.Height - DropzoneLayout.Gap - DropzoneLayout.DropZoneHeight,
                width,
                DropzoneLayout.DropZoneHeight);
            _nativeDropZone.BringToFront();
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
            if (!RequireWritable(wb))
                return;

            var folderIdCache = new Dictionary<string, string>(StringComparer.Ordinal);
            var requests = new List<PdfPathImportRequest>();
            PdfImportResult importResult;

            using (var progress = ThreadedProgressController.Show("Importing documents..."))
            {
                progress.Report("Collecting PDFs", null, 0, 0);

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
                            AddDirectoryPdfRequests(wb, path, folderIdCache, requests);
                        else if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            requests.Add(new PdfPathImportRequest(path, _selectedFolderId));
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DocuLink] OS import failed for '{path}': {ex.Message}");
                    }
                }

                importResult = new PdfImportService().ImportFilePaths(wb, requests, progress);

                if (importResult.AddedIds.Count > 0)
                {
                    progress.Report(
                        "Refreshing DocuLink",
                        "Updating file list and viewer data...",
                        importResult.AddedIds.Count,
                        importResult.AddedIds.Count);

                    SendFilesToWebView();
                    foreach (string id in importResult.AddedIds)
                        Globals.ThisAddIn.NotifyViewerPdfAdded(id);
                }
            }
        }

        private void AddDirectoryPdfRequests(
            Excel.Workbook wb,
            string dirPath,
            Dictionary<string, string> folderIdCache,
            List<PdfPathImportRequest> requests)
        {
            string folderName;
            try
            {
                folderName = new DirectoryInfo(dirPath).Name;
            }
            catch
            {
                return;
            }

            string sentinel = "__new__:" + folderName;
            string folderId = ResolveFolderId(wb, sentinel, folderIdCache);

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

                requests.Add(new PdfPathImportRequest(full, folderId));
            }
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

                    case "browse-pdf-files":
                        ShowPdfFilePicker();
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
            if (!RequireWritable(wb)) return;

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
            }
        }

        private void HandleAddFiles(AddFilesRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            if (!RequireWritable(wb)) return;

            // Cache resolved folder ids so that a dropped directory only creates one folder
            // even if it contains many files.
            var resolvedFolderIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var requests = new List<PdfBase64ImportRequest>();
            PdfImportResult importResult;

            using (var progress = ThreadedProgressController.Show("Importing documents..."))
            {
                foreach (var file in req.Files)
                {
                    try
                    {
                        string folderId = ResolveFolderId(wb, file.FolderId, resolvedFolderIds);
                        requests.Add(new PdfBase64ImportRequest(file.Name, file.Base64, folderId));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DocuLink] AddPdf failed for '{file.Name}': {ex.Message}");
                    }
                }

                importResult = new PdfImportService().ImportBase64(wb, requests, progress);

                progress.Report(
                    "Refreshing DocuLink",
                    "Updating file list and viewer data...",
                    importResult.AddedIds.Count,
                    importResult.AddedIds.Count);

                SendFilesToWebView();
                foreach (string id in importResult.AddedIds)
                    Globals.ThisAddIn.NotifyViewerPdfAdded(id);
            }
        }

        private void ShowPdfFilePicker()
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            if (!RequireWritable(wb)) return;

            string[] selectedPaths;
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Add PDFs to workbook";
                dialog.Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;

                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.FileNames == null || dialog.FileNames.Length == 0)
                    return;

                selectedPaths = dialog.FileNames.ToArray();
            }

            BeginInvoke(new Action(() => ProcessOsPaths(selectedPaths)));
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
            if (!RequireWritable(wb)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            DocuLinkContent content = _service.RenamePdf(wb, req.Id, req.NewName);
            System.Diagnostics.Debug.WriteLine($"[DocuLink] RenamePdf: {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            SendFilesToWebView(content);
            System.Diagnostics.Debug.WriteLine($"[DocuLink] SendFilesToWebView: {sw.ElapsedMilliseconds}ms");

            sw.Restart();
            Globals.ThisAddIn.NotifyViewerPdfRenamed(req.Id, req.NewName);
            System.Diagnostics.Debug.WriteLine($"[DocuLink] NotifyViewerPdfRenamed: {sw.ElapsedMilliseconds}ms");
        }

        private void HandleRemoveFile(RemoveFileRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            if (!RequireWritable(wb)) return;
            _service.RemovePdf(wb, req.Id);
            SendFilesToWebView();
            Globals.ThisAddIn.NotifyViewerPdfRemoved(req.Id);
        }

        private void HandleMoveFile(MoveFileRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            if (!RequireWritable(wb)) return;
            DocuLinkContent content = _service.MoveFile(wb, req.Id, req.FolderId);
            SendFilesToWebView(content);
        }

        private void HandleAddFolder(AddFolderRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            if (!RequireWritable(wb)) return;
            _service.AddFolder(wb, req.Name);
            SendFilesToWebView();
        }

        private void HandleRenameFolder(RenameFolderRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            if (!RequireWritable(wb)) return;
            _service.RenameFolder(wb, req.Id, req.NewName);
            SendFilesToWebView();
        }

        private void HandleRemoveFolder(RemoveFolderRequest req)
        {
            Excel.Workbook wb = GetActiveWorkbook();
            if (wb == null) return;
            if (!RequireWritable(wb)) return;
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
        public void SendFilesToWebView(DocuLinkContent preloaded = null)
        {
            try
            {
                DocuLinkContent content = preloaded;
                if (content == null)
                {
                    Excel.Workbook wb = GetActiveWorkbook();
                    if (wb == null) return;
                    var store = new DocuLinkCustomXmlPartStore(wb);
                    content = store.LoadContent();
                    if (content == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[DocuLink] Failed to load workbook content");
                        return;
                    }
                }

                IReadOnlyDictionary<string, int> linkCounts = null;
                try
                {
                    Excel.Workbook wb = GetActiveWorkbook();
                    if (wb != null)
                    {
                        var links = new WorkbookStorageSession(wb).GetLinks();
                        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var link in links)
                            counts[link.PdfId] = counts.TryGetValue(link.PdfId, out int n) ? n + 1 : 1;
                        linkCounts = counts;
                    }
                }
                catch { /* non-fatal; link counts default to 0 */ }

                string json = FileManagerMessageSerializer.BuildFilesLoaded(content.Folders, content.Pdfs, linkCounts);
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

        private bool RequireWritable(Excel.Workbook workbook)
        {
            return WorkbookProtectionGuard.TryRequireWritable(workbook, this);
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

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PositionNativeDropZone();
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

        /// <summary>
        /// Native Windows Forms panel for drag-drop file interaction.
        /// Color tokens MUST match src/web/shared/base.css design system:
        ///   - ForeColor (31,41,55) = --color-text-primary
        ///   - BackColor (255,255,255) = --color-surface
        ///   - fillColor drag-over (238,242,255) = --color-surface-light
        ///   - borderColor drag-over (124,106,247) = --color-accent
        ///   - borderColor rest (212,212,224) = --color-border
        ///   - mutedBrush (92,92,112) = --color-text-muted
        /// If design tokens change, both CSS and these hardcoded values must be updated together.
        /// </summary>
        private sealed class NativeDropZonePanel : Panel
        {
            private bool _dragOver;

            public NativeDropZonePanel()
            {
                DoubleBuffered = true;
                BackColor = Color.White;
                ForeColor = Color.FromArgb(31, 41, 55);
                Cursor = Cursors.Hand;
            }

            public void SetDragOver(bool dragOver)
            {
                if (_dragOver == dragOver)
                    return;

                _dragOver = dragOver;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var bounds = ClientRectangle;
                bounds.Inflate(-1, -1);

                Color fillColor = _dragOver ? Color.FromArgb(238, 242, 255) : Color.White;
                Color borderColor = _dragOver ? Color.FromArgb(124, 106, 247) : Color.FromArgb(212, 212, 224);

                using (var fill = new SolidBrush(fillColor))
                using (var border = new Pen(borderColor, 2f))
                using (var textBrush = new SolidBrush(ForeColor))
                using (var mutedBrush = new SolidBrush(Color.FromArgb(92, 92, 112)))
                using (var iconFont = new Font(Font.FontFamily, 20f, FontStyle.Regular))
                using (var titleFont = new Font(Font.FontFamily, 12f, FontStyle.Bold))
                using (var bodyFont = new Font(Font.FontFamily, 11f, FontStyle.Regular))
                {
                    border.DashStyle = DashStyle.Dash;
                    e.Graphics.FillRectangle(fill, bounds);
                    e.Graphics.DrawRectangle(border, bounds);

                    var icon = "PDF";
                    var title = "Drop PDFs or folders here";
                    var body = "or click to browse";
                    var iconSize = e.Graphics.MeasureString(icon, iconFont);
                    var titleSize = e.Graphics.MeasureString(title, titleFont);
                    var bodySize = e.Graphics.MeasureString(body, bodyFont);
                    float centerY = bounds.Top + bounds.Height / 2f;

                    e.Graphics.DrawString(
                        icon,
                        iconFont,
                        mutedBrush,
                        bounds.Left + (bounds.Width - iconSize.Width) / 2f,
                        centerY - iconSize.Height - titleSize.Height / 2f - 4f);
                    e.Graphics.DrawString(
                        title,
                        titleFont,
                        textBrush,
                        bounds.Left + (bounds.Width - titleSize.Width) / 2f,
                        centerY - titleSize.Height / 2f + 1f);
                    e.Graphics.DrawString(
                        body,
                        bodyFont,
                        mutedBrush,
                        bounds.Left + (bounds.Width - bodySize.Width) / 2f,
                        centerY + titleSize.Height / 2f + 7f);
                }
            }
        }
    }
}
