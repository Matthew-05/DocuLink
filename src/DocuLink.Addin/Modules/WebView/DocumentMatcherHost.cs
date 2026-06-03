using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using DocuLink.Addin.Modules.CustomXml;
using DocuLink.Addin.Modules.CustomXml.Models;
using DocuLink.Addin.Modules.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Hosts the document-matcher wizard web UI in a standalone non-modal window.</summary>
    public sealed class DocumentMatcherHost : Form
    {
        private const string AllFoldersId = "__all__";

        private readonly WebView2 _webView = new WebView2();
        private bool _webViewReady;
        private bool _disposed;
        private bool _selectionChangeSubscribed;
        private bool _selectionLocked;

        /// <summary>
        /// The range selected when the user opened the wizard.
        /// Each column in each selected area corresponds to one key column.
        /// </summary>
        private Excel.Range _selectedRange;

        /// <summary>
        /// The first 1-based Excel row number in the selected areas' worksheet,
        /// ordered to match the key columns sent in matcher-ready.
        /// Stored so HandleCreateLinks can resolve rows correctly.
        /// </summary>
        private int _firstSelectedRow;

        public DocumentMatcherHost()
        {
            Text = "DocuLink – Match Documents";
            Width = 900;
            Height = 640;
            MinimumSize = new System.Drawing.Size(700, 480);
            StartPosition = FormStartPosition.CenterScreen;

            _webView.Dock = DockStyle.Fill;
            Controls.Add(_webView);

            _ = InitAsync();
        }

        private async Task InitAsync()
        {
            DocuLinkLog.Trace("ENTER document matcher init");
            try
            {
                if (_disposed) return;

                string userDataFolder = System.IO.Path.Combine(
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
                _webView.CoreWebView2.Navigate("https://doculink.local/document-matcher/index.html");

                _webViewReady = true;
                DocuLinkLog.Trace("EXIT document matcher initialized");
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"EXCEPTION document matcher init {ex.GetType().FullName}: {ex.Message}");
                MessageBox.Show(
                    $"DocuLink document matcher failed to load:\n\n{ex.Message}",
                    "DocuLink",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_disposed) return;

            string raw = e.TryGetWebMessageAsString();
            string messageType = DocumentMatcherMessageParser.GetMessageType(raw);

            switch (messageType)
            {
                case "matcher-app-ready":
                    HandleAppReady();
                    break;

                case "matcher-selection-locked":
                    HandleSelectionLocked();
                    break;

                case "matcher-selection-unlocked":
                    HandleSelectionUnlocked();
                    break;

                case "matcher-log":
                    HandleMatcherLog(raw);
                    break;

                case "matcher-geometry-prepared":
                    HandleMatcherGeometryPrepared(raw);
                    break;

                case "start-matching":
                    HandleStartMatching(raw);
                    break;

                case "create-links":
                    HandleCreateLinks(raw);
                    break;
            }
        }

        private void HandleAppReady()
        {
            DocuLinkLog.Trace("matcher-app-ready received");
            try
            {
                _selectionLocked = false;

                var app = Globals.ThisAddIn.Application;
                var workbook = app?.ActiveWorkbook;
                if (workbook == null) return;

                _selectedRange = app.Selection as Excel.Range;
                if (_selectedRange == null) return;

                if (!TryAnalyzeSelection(_selectedRange, out int rowCount, out var keyColumns, out var outputColumns))
                    return;

                var firstArea = (Excel.Range)_selectedRange.Areas[1]; // 1-based
                _firstSelectedRow = firstArea.Row;

                var session = Globals.ThisAddIn.GetStorageSession(workbook);
                var content = session.Store.LoadContent();
                var folders = content.Folders ?? new List<PdfFolder>();

                string json = DocumentMatcherMessageSerializer.BuildMatcherReady(
                    rowCount, keyColumns, outputColumns, folders);
                Post(json);
                SubscribeSelectionChanged();
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"HandleAppReady error {ex.GetType().FullName}: {ex.Message}");
            }
        }

        private void Application_SheetSelectionChange(object sh, Excel.Range target)
        {
            if (_disposed || !_webViewReady || _selectionLocked) return;

            try
            {
                if (TryAnalyzeSelection(target, out int rowCount, out var keyColumns, out var outputColumns))
                {
                    _selectedRange = target;
                    _firstSelectedRow = ((Excel.Range)target.Areas[1]).Row;
                    Post(DocumentMatcherMessageSerializer.BuildMatcherSelectionChanged(
                        rowCount, keyColumns, outputColumns));
                }
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Application_SheetSelectionChange error {ex.GetType().FullName}: {ex.Message}");
            }
        }

        private void HandleSelectionLocked()
        {
            DocuLinkLog.Trace("matcher-selection-locked received");
            _selectionLocked = true;
            UnsubscribeSelectionChanged();
        }

        private void HandleSelectionUnlocked()
        {
            DocuLinkLog.Trace("matcher-selection-unlocked received");
            _selectionLocked = false;
            SubscribeSelectionChanged();
        }

        private void HandleMatcherLog(string raw)
        {
            try
            {
                string message = DocumentMatcherMessageParser.ParseMatcherLog(raw);
                DocuLinkLog.Trace($"web matcher: {message}");
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"matcher-log parse failed {ex.GetType().FullName}: {ex.Message}");
            }
        }

        private void HandleMatcherGeometryPrepared(string raw)
        {
            try
            {
                var payload = DocumentMatcherMessageParser.ParseMatcherGeometryPrepared(raw);
                var workbook = GetSelectedWorkbook() ?? Globals.ThisAddIn.Application?.ActiveWorkbook;
                Globals.ThisAddIn.StoreTransientPdfGeometry(
                    workbook, payload.PdfId, payload.GeometryBase64);
                DocuLinkLog.Trace($"matcher geometry cached pdfId={payload.PdfId}");
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"matcher-geometry-prepared failed {ex.GetType().FullName}: {ex.Message}");
            }
        }

        private Excel.Workbook GetSelectedWorkbook()
        {
            if (_selectedRange == null) return null;

            var firstArea = (Excel.Range)_selectedRange.Areas[1];
            var worksheet = (Excel.Worksheet)firstArea.Worksheet;
            return worksheet.Parent as Excel.Workbook;
        }

        private bool TryAnalyzeSelection(
            Excel.Range range,
            out int rowCount,
            out List<KeyColumnEntry> keyColumns,
            out List<OutputColumnEntry> outputColumns)
        {
            rowCount = 0;
            keyColumns = new List<KeyColumnEntry>();
            outputColumns = new List<OutputColumnEntry>();

            if (range == null) return false;

            var areas = range.Areas;
            if (areas.Count == 0) return false;

            var firstArea = (Excel.Range)areas[1]; // 1-based
            var worksheet = (Excel.Worksheet)firstArea.Worksheet;
            int maxKeyColNumber = 0;

            for (int a = 1; a <= areas.Count; a++)
            {
                var area = (Excel.Range)areas[a];
                int areaColumnCount = area.Columns.Count;
                int areaRowCount = area.Rows.Count;

                for (int offset = 0; offset < areaColumnCount; offset++)
                {
                    int colNumber = area.Column + offset;
                    string header = ColNumberToLetter(colNumber);

                    var startCell = (Excel.Range)worksheet.Cells[area.Row, colNumber];
                    var endCell = (Excel.Range)worksheet.Cells[area.Row + areaRowCount - 1, colNumber];
                    var columnRange = worksheet.get_Range(startCell, endCell);
                    string rangeAddress = columnRange.get_Address(
                        RowAbsolute: true,
                        ColumnAbsolute: true,
                        ReferenceStyle: Excel.XlReferenceStyle.xlA1,
                        External: false);

                    keyColumns.Add(new KeyColumnEntry
                    {
                        ColNumber    = colNumber,
                        Header       = header,
                        RangeAddress = rangeAddress,
                    });

                    if (colNumber > maxKeyColNumber)
                        maxKeyColNumber = colNumber;
                }
            }

            if (maxKeyColNumber > 0)
            {
                var usedRange = worksheet.UsedRange;
                int lastUsedCol = usedRange.Column + usedRange.Columns.Count - 1;
                int outputEnd = Math.Max(lastUsedCol, maxKeyColNumber + 10);

                for (int c = maxKeyColNumber + 1; c <= outputEnd; c++)
                {
                    outputColumns.Add(new OutputColumnEntry { ColNumber = c, Header = ColNumberToLetter(c) });
                }
            }

            rowCount = firstArea.Rows.Count;
            return true;
        }

        private void SubscribeSelectionChanged()
        {
            if (_selectionChangeSubscribed) return;
            Globals.ThisAddIn.Application.SheetSelectionChange += Application_SheetSelectionChange;
            _selectionChangeSubscribed = true;
        }

        private void UnsubscribeSelectionChanged()
        {
            if (!_selectionChangeSubscribed) return;
            Globals.ThisAddIn.Application.SheetSelectionChange -= Application_SheetSelectionChange;
            _selectionChangeSubscribed = false;
        }

        private void HandleStartMatching(string raw)
        {
            DocuLinkLog.Trace("start-matching received");
            try
            {
                _selectionLocked = true;
                UnsubscribeSelectionChanged();

                var payload = DocumentMatcherMessageParser.ParseStartMatching(raw);
                var app = Globals.ThisAddIn.Application;
                var workbook = GetSelectedWorkbook() ?? app?.ActiveWorkbook;
                if (workbook == null || _selectedRange == null) return;

                var session = Globals.ThisAddIn.GetStorageSession(workbook);
                var content = session.Store.LoadContent();

                var selectedFolderIds = new HashSet<string>(
                    payload.FolderIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                bool includeAllFolders =
                    selectedFolderIds.Count == 0 || selectedFolderIds.Contains(AllFoldersId);
                DocuLinkLog.Trace($"start-matching outputCols={payload.OutputColNumbers.Count} folderIds={selectedFolderIds.Count} includeAll={includeAllFolders}");

                var matchingPdfs = new List<PdfMetadata>();
                foreach (var pdf in content.Pdfs)
                {
                    string fid = pdf.FolderId ?? string.Empty;
                    if (includeAllFolders || selectedFolderIds.Contains(fid))
                        matchingPdfs.Add(pdf);
                }

                var pdfEntries = new List<MatcherPdfEntry>();
                foreach (var meta in matchingPdfs)
                {
                    session.Store.TryLoadPdfBinary(meta.Id, out string base64, out string geometryBase64);
                    bool hasGeometry = !string.IsNullOrEmpty(geometryBase64);
                    if (!hasGeometry
                        && Globals.ThisAddIn.TryGetTransientPdfGeometry(
                            workbook, meta.Id, out string cachedGeometryBase64))
                    {
                        geometryBase64 = cachedGeometryBase64;
                        hasGeometry = true;
                    }

                    pdfEntries.Add(new MatcherPdfEntry
                    {
                        Id             = meta.Id,
                        Name           = meta.Name,
                        FolderId       = meta.FolderId ?? string.Empty,
                        GeometryBase64 = hasGeometry ? geometryBase64 : null,
                        Base64         = hasGeometry ? null : (string.IsNullOrEmpty(base64) ? null : base64),
                    });
                }
                DocuLinkLog.Trace($"start-matching pdfEntries={pdfEntries.Count}");

                if (!TryAnalyzeSelection(_selectedRange, out int totalRows, out var keyColumns, out _))
                    return;
                DocuLinkLog.Trace($"start-matching selection rows={totalRows} keyColumns={keyColumns.Count}");

                var firstArea = (Excel.Range)_selectedRange.Areas[1];
                var worksheet = (Excel.Worksheet)firstArea.Worksheet;

                var rows = new List<MatcherRowEntry>();

                for (int r = 1; r <= totalRows; r++)
                {
                    int excelRow = _firstSelectedRow + (r - 1);
                    var keyValues = new List<string>();

                    foreach (var keyColumn in keyColumns)
                    {
                        var cell = (Excel.Range)worksheet.Cells[excelRow, keyColumn.ColNumber];
                        keyValues.Add(cell.Value2?.ToString() ?? string.Empty);
                    }

                    rows.Add(new MatcherRowEntry { RowIndex = r - 1, KeyValues = keyValues });
                }

                string json = DocumentMatcherMessageSerializer.BuildMatcherDataLoaded(rows, pdfEntries);
                DocuLinkLog.Trace($"posting matcher-data-loaded rows={rows.Count} pdfs={pdfEntries.Count}");
                Post(json);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"HandleStartMatching error {ex.GetType().FullName}: {ex.Message}");
            }
        }

        private void HandleCreateLinks(string raw)
        {
            DocuLinkLog.Trace("create-links received");
            var results = new List<LinkResultEntry>();
            try
            {
                var payload = DocumentMatcherMessageParser.ParseCreateLinks(raw);
                var app = Globals.ThisAddIn.Application;
                var workbook = app?.ActiveWorkbook;
                if (workbook == null || _selectedRange == null)
                {
                    Post(DocumentMatcherMessageSerializer.BuildLinksCreated(results));
                    return;
                }

                var svc = new CreateLinkService();
                var firstArea = (Excel.Range)_selectedRange.Areas[1];
                var worksheet = (Excel.Worksheet)firstArea.Worksheet;

                foreach (var link in payload.Links)
                {
                    bool success = false;
                    try
                    {
                        int excelRow = _firstSelectedRow + link.RowIndex;
                        // outputColNumber is directly the 1-based Excel column number
                        int excelCol = link.OutputColNumber;

                        var targetCell = (Excel.Range)worksheet.Cells[excelRow, excelCol];

                        svc.CreateLinkAtCell(
                            pdfId:      link.PdfId,
                            page:       link.PageIndex,
                            x:          link.RectX,
                            y:          link.RectY,
                            width:      link.RectWidth,
                            height:     link.RectHeight,
                            text:       link.Text,
                            linkType:   LinkType.Auto,
                            targetCell: targetCell,
                            workbook:   workbook);

                        success = true;
                    }
                    catch (Exception ex)
                    {
                        DocuLinkLog.Trace($"CreateLinkAtCell failed row={link.RowIndex} col={link.OutputColNumber}: {ex.Message}");
                    }

                    results.Add(new LinkResultEntry
                    {
                        RowIndex        = link.RowIndex,
                        OutputColNumber = link.OutputColNumber,
                        Success         = success,
                    });
                }

                // Notify the active viewer that links have changed so overlays refresh
                var viewerHost = Globals.ThisAddIn.GetActiveViewerHost();
                if (viewerHost != null)
                {
                    viewerHost.InvalidateData();
                    viewerHost.RefreshDataIfReady();
                }
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"HandleCreateLinks error: {ex.Message}");
            }
            finally
            {
                Post(DocumentMatcherMessageSerializer.BuildLinksCreated(results));
            }
        }

        private void Post(string json)
        {
            if (_disposed || !_webViewReady) return;
            try
            {
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Post failed: {ex.Message}");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                UnsubscribeSelectionChanged();
                _webView.Dispose();
            }
            base.Dispose(disposing);
        }

        private static string GetWebUiPath()
        {
            string codeBase = Assembly.GetExecutingAssembly().CodeBase;
            string addinDir = Path.GetDirectoryName(new Uri(codeBase).LocalPath)
                ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(addinDir, "webui");
        }

        private static string ColNumberToLetter(int colNumber)
        {
            string result = string.Empty;
            while (colNumber > 0)
            {
                int rem = (colNumber - 1) % 26;
                result = (char)('A' + rem) + result;
                colNumber = (colNumber - 1) / 26;
            }
            return result;
        }
    }
}
