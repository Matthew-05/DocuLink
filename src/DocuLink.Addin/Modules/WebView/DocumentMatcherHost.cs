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
        private readonly WebView2 _webView = new WebView2();
        private bool _webViewReady;
        private bool _disposed;

        /// <summary>
        /// The areas selected when the user opened the wizard.
        /// Each area corresponds to one key column. All areas are assumed to share the same row span.
        /// </summary>
        private Excel.Range _selectedRange;

        /// <summary>
        /// The 1-based Excel column numbers for the header row of the selected areas' worksheet,
        /// ordered to match the key columns sent in matcher-ready.
        /// Stored so HandleCreateLinks can resolve rows correctly.
        /// </summary>
        private int _headerRow;

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
                var app = Globals.ThisAddIn.Application;
                var workbook = app?.ActiveWorkbook;
                if (workbook == null) return;

                _selectedRange = app.Selection as Excel.Range;
                if (_selectedRange == null) return;

                var areas = _selectedRange.Areas;
                if (areas.Count == 0) return;

                var firstArea = (Excel.Range)areas[1]; // 1-based
                var worksheet = (Excel.Worksheet)firstArea.Worksheet;
                _headerRow = firstArea.Row;

                // Build key column entries from each selected area
                var keyColumns = new List<KeyColumnEntry>();
                int maxKeyColNumber = 0;

                for (int a = 1; a <= areas.Count; a++)
                {
                    var area = (Excel.Range)areas[a];
                    int colNumber = area.Column;
                    // Header is the first cell of this area
                    var headerCell = (Excel.Range)area.Cells[1, 1];
                    string header = headerCell.Value2?.ToString();
                    if (string.IsNullOrWhiteSpace(header))
                        header = ColNumberToLetter(colNumber);

                    string rangeAddress = area.get_Address(
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

                // Output columns: columns immediately after the rightmost key column
                var outputColumns = new List<OutputColumnEntry>();
                if (maxKeyColNumber > 0)
                {
                    var usedRange = worksheet.UsedRange;
                    int lastUsedCol = usedRange.Column + usedRange.Columns.Count - 1;
                    // Offer at least 10 output options beyond the used range boundary
                    int outputEnd = Math.Max(lastUsedCol, maxKeyColNumber + 10);

                    for (int c = maxKeyColNumber + 1; c <= outputEnd; c++)
                    {
                        var headerCell = (Excel.Range)worksheet.Cells[_headerRow, c];
                        string header = headerCell.Value2?.ToString();
                        if (string.IsNullOrWhiteSpace(header))
                            header = ColNumberToLetter(c);

                        outputColumns.Add(new OutputColumnEntry { ColNumber = c, Header = header });
                    }
                }

                var session = Globals.ThisAddIn.GetStorageSession(workbook);
                var content = session.Store.LoadContent();
                var folders = content.Folders ?? new List<PdfFolder>();

                int rowCount = firstArea.Rows.Count;

                string json = DocumentMatcherMessageSerializer.BuildMatcherReady(
                    rowCount, keyColumns, outputColumns, folders);
                Post(json);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"HandleAppReady error: {ex.Message}");
            }
        }

        private void HandleStartMatching(string raw)
        {
            DocuLinkLog.Trace("start-matching received");
            try
            {
                var payload = DocumentMatcherMessageParser.ParseStartMatching(raw);
                var app = Globals.ThisAddIn.Application;
                var workbook = app?.ActiveWorkbook;
                if (workbook == null || _selectedRange == null) return;

                var session = Globals.ThisAddIn.GetStorageSession(workbook);
                var content = session.Store.LoadContent();

                var selectedFolderIds = new HashSet<string>(
                    payload.FolderIds, StringComparer.OrdinalIgnoreCase);

                var matchingPdfs = new List<PdfMetadata>();
                foreach (var pdf in content.Pdfs)
                {
                    string fid = pdf.FolderId ?? string.Empty;
                    if (selectedFolderIds.Contains(fid))
                        matchingPdfs.Add(pdf);
                }

                var pdfEntries = new List<MatcherPdfEntry>();
                foreach (var meta in matchingPdfs)
                {
                    session.Store.TryLoadPdfBinary(meta.Id, out _, out string geometryBase64);
                    pdfEntries.Add(new MatcherPdfEntry
                    {
                        Id             = meta.Id,
                        Name           = meta.Name,
                        FolderId       = meta.FolderId ?? string.Empty,
                        GeometryBase64 = string.IsNullOrEmpty(geometryBase64) ? null : geometryBase64,
                    });
                }

                // Read key column values from each selected area for every data row
                var areas = _selectedRange.Areas;
                var firstArea = (Excel.Range)areas[1];
                var worksheet = (Excel.Worksheet)firstArea.Worksheet;

                int totalRows = firstArea.Rows.Count;
                var rows = new List<MatcherRowEntry>();

                for (int r = 2; r <= totalRows; r++) // row 1 = header, skip it
                {
                    int excelRow = _headerRow + (r - 1);
                    var keyValues = new List<string>();

                    for (int a = 1; a <= areas.Count; a++)
                    {
                        var area = (Excel.Range)areas[a];
                        var cell = (Excel.Range)worksheet.Cells[excelRow, area.Column];
                        keyValues.Add(cell.Value2?.ToString() ?? string.Empty);
                    }

                    rows.Add(new MatcherRowEntry { RowIndex = r - 2, KeyValues = keyValues });
                }

                string json = DocumentMatcherMessageSerializer.BuildMatcherDataLoaded(rows, pdfEntries);
                Post(json);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"HandleStartMatching error: {ex.Message}");
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
                        // rowIndex is 0-based from first data row; +1 skips header row
                        int excelRow = _headerRow + 1 + link.RowIndex;
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
