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

        /// <summary>
        /// The selection to put back when output-range hover preview ends, captured on the
        /// first preview of a hover session. Null when no preview is active.
        /// </summary>
        private Excel.Range _previewRestoreRange;

        /// <summary>Column currently shown by hover preview, or 0 when none.</summary>
        private int _previewColNumber;

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

                case "matcher-preview-output-range":
                    HandlePreviewOutputRange(raw);
                    break;

                case "matcher-clear-output-range-preview":
                    ClearOutputRangePreview();
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

                case "check-output-content":
                    HandleCheckOutputContent(raw);
                    break;

                case "matcher-close":
                    ClearOutputRangePreview();
                    Hide();
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
                if (app == null) return;

                // The captured range is what pins the wizard to a workbook for the rest of
                // its life; every later handler derives the workbook from it via
                // GetSelectedWorkbook rather than re-reading ActiveWorkbook.
                _selectedRange = app.Selection as Excel.Range;
                if (_selectedRange == null) return;

                var workbook = GetSelectedWorkbook();
                if (workbook == null) return;

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

            // Step 1 reads the live selection, so any preview must be undone before the
            // change handler is re-armed — otherwise a previewed output column would be
            // adopted as the user's key selection.
            ClearOutputRangePreview();

            _selectionLocked = false;
            SubscribeSelectionChanged();
        }

        /// <summary>
        /// Selects the range that would receive links for the hovered output column: the key
        /// selection's row span in that column.
        /// </summary>
        private void HandlePreviewOutputRange(string raw)
        {
            try
            {
                int colNumber = DocumentMatcherMessageParser.ParsePreviewOutputRange(raw);
                if (_selectedRange == null || colNumber <= 0) return;
                if (colNumber == _previewColNumber) return; // already showing this column

                var firstArea = (Excel.Range)_selectedRange.Areas[1];
                var worksheet = (Excel.Worksheet)firstArea.Worksheet;
                int firstRow  = firstArea.Row;
                int rowCount  = firstArea.Rows.Count;
                if (rowCount <= 0) return;

                var topCell = (Excel.Range)worksheet.Cells[firstRow, colNumber];
                var botCell = (Excel.Range)worksheet.Cells[firstRow + rowCount - 1, colNumber];
                var target  = worksheet.get_Range(topCell, botCell);

                // Captured once per hover session: previewing a second column must still
                // restore the user's original selection, not the first previewed column.
                if (_previewRestoreRange == null)
                    _previewRestoreRange = _selectedRange;

                SelectWithoutFeedback(target);
                _previewColNumber = colNumber;
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"HandlePreviewOutputRange error {ex.GetType().FullName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores the selection captured before hover preview began. No-op when no preview
        /// is active, so it is safe to call from teardown and step transitions.
        /// </summary>
        private void ClearOutputRangePreview()
        {
            if (_previewRestoreRange == null)
            {
                _previewColNumber = 0;
                return;
            }

            try
            {
                SelectWithoutFeedback(_previewRestoreRange);
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"ClearOutputRangePreview error {ex.GetType().FullName}: {ex.Message}");
            }
            finally
            {
                _previewRestoreRange = null;
                _previewColNumber = 0;
            }
        }

        /// <summary>
        /// Selects <paramref name="range"/> with SheetSelectionChange detached.
        /// </summary>
        /// <remarks>
        /// Preview only runs on Step 2, where the handler is already unsubscribed, but the
        /// guard is unconditional: re-entering that handler from a preview would feed a
        /// previewed output column back in as the user's key selection.
        /// </remarks>
        private void SelectWithoutFeedback(Excel.Range range)
        {
            if (range == null) return;

            bool wasSubscribed = _selectionChangeSubscribed;
            if (wasSubscribed) UnsubscribeSelectionChanged();

            try
            {
                // Range.Select throws unless the owning workbook and sheet are active.
                // Activating an already-active book or sheet is a no-op, so this is
                // unconditional rather than guarded by unreliable RCW identity checks.
                var worksheet = (Excel.Worksheet)range.Worksheet;
                (worksheet.Parent as Excel.Workbook)?.Activate();
                worksheet.Activate();
                range.Select();
            }
            finally
            {
                if (wasSubscribed) SubscribeSelectionChanged();
            }
        }

        private void HandleCheckOutputContent(string raw)
        {
            try
            {
                var colNumbers = DocumentMatcherMessageParser.ParseCheckOutputContent(raw);

                if (_selectedRange == null || colNumbers.Count == 0)
                {
                    Post(DocumentMatcherMessageSerializer.BuildConfirmOverwriteResult(true));
                    return;
                }

                var firstArea  = (Excel.Range)_selectedRange.Areas[1];
                var worksheet  = (Excel.Worksheet)firstArea.Worksheet;
                int firstRow   = firstArea.Row;
                int rowCount   = firstArea.Rows.Count;

                var conflictingHeaders = new List<string>();
                foreach (int col in colNumbers)
                {
                    if (ColumnHasContent(worksheet, firstRow, rowCount, col))
                        conflictingHeaders.Add(ColNumberToLetter(col));
                }

                if (conflictingHeaders.Count == 0)
                {
                    Post(DocumentMatcherMessageSerializer.BuildConfirmOverwriteResult(true));
                    return;
                }

                string colNames  = string.Join(", ", conflictingHeaders);
                bool   isPlural  = conflictingHeaders.Count > 1;
                string message   = $"{(isPlural ? "Columns" : "Column")} {colNames} already contain data.\n\n" +
                                   "Continuing will overwrite existing cell content. Do you want to proceed?";

                var result = MessageBox.Show(
                    message,
                    "DocuLink – Overwrite Warning",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                Post(DocumentMatcherMessageSerializer.BuildConfirmOverwriteResult(result == DialogResult.Yes));
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"HandleCheckOutputContent error {ex.GetType().FullName}: {ex.Message}");
                Post(DocumentMatcherMessageSerializer.BuildConfirmOverwriteResult(false));
            }
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

                // Cached per workbook, so falling back to ActiveWorkbook would file this
                // under a workbook that has no such PDF. StoreTransientPdfGeometry ignores
                // a null workbook, which is the right outcome — a missed cache, not a
                // misplaced one.
                var workbook = GetSelectedWorkbook();
                Globals.ThisAddIn.StoreTransientPdfGeometry(
                    workbook, payload.PdfId, payload.GeometryBase64);
                DocuLinkLog.Trace($"matcher geometry cached pdfId={payload.PdfId}");
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"matcher-geometry-prepared failed {ex.GetType().FullName}: {ex.Message}");
            }
        }

        /// <summary>
        /// The workbook the wizard is operating on: the one owning the range captured when it
        /// opened, never whatever happens to be active now.
        /// </summary>
        /// <remarks>
        /// A matching run lasts long enough for the user to click into another workbook, so
        /// every handler that reads or writes workbook state has to resolve it this way.
        /// Returns <c>null</c> if the workbook can no longer be reached — callers must abort
        /// rather than fall back to the active workbook, which is how links ended up being
        /// recorded against the wrong file.
        /// </remarks>
        private Excel.Workbook GetSelectedWorkbook()
        {
            if (_selectedRange == null) return null;

            try
            {
                var firstArea = (Excel.Range)_selectedRange.Areas[1];
                var worksheet = (Excel.Worksheet)firstArea.Worksheet;
                return worksheet.Parent as Excel.Workbook;
            }
            catch (Exception ex)
            {
                // Typically the captured range's workbook has been closed mid-run.
                DocuLinkLog.Trace($"GetSelectedWorkbook unavailable: {ex.GetType().FullName}: {ex.Message}");
                return null;
            }
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

            // A whole-column or Ctrl+A selection spans 1,048,576 rows and 16,384 columns.
            // Analysing that literally costs one COM round trip per row and per column, which
            // freezes Excel for minutes. Nothing outside UsedRange can contribute key data, so
            // every span below is clamped to it before any per-row or per-column work happens.
            var usedRange = worksheet.UsedRange;
            int usedFirstRow = usedRange.Row;
            int usedLastRow  = usedFirstRow + usedRange.Rows.Count - 1;
            int usedFirstCol = usedRange.Column;
            int usedLastCol  = usedFirstCol + usedRange.Columns.Count - 1;

            // Row span of the first area, clamped; every key column is read over this span.
            ClampSpan(firstArea.Row, firstArea.Rows.Count, usedFirstRow, usedLastRow,
                      out int keyFirstRow, out int keyRowCount);

            for (int a = 1; a <= areas.Count; a++)
            {
                var area = (Excel.Range)areas[a];
                int areaFirstCol = area.Column;

                ClampSpan(areaFirstCol, area.Columns.Count, usedFirstCol, usedLastCol,
                          out int colStart, out int colCount);

                // An area entirely outside UsedRange still shows its first column so the
                // wizard reflects what the user picked — it will simply report zero rows.
                if (colCount <= 0)
                {
                    colStart = areaFirstCol;
                    colCount = 1;
                }

                ClampSpan(area.Row, area.Rows.Count, usedFirstRow, usedLastRow,
                          out int areaFirstRow, out int areaRowCount);
                if (areaRowCount <= 0)
                {
                    areaFirstRow = area.Row;
                    areaRowCount = 1;
                }

                for (int offset = 0; offset < colCount; offset++)
                {
                    int colNumber = colStart + offset;
                    string header = ColNumberToLetter(colNumber);

                    var startCell = (Excel.Range)worksheet.Cells[areaFirstRow, colNumber];
                    var endCell = (Excel.Range)worksheet.Cells[areaFirstRow + areaRowCount - 1, colNumber];
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
                int outputEnd = Math.Max(usedLastCol, maxKeyColNumber + 10);
                for (int c = maxKeyColNumber + 1; c <= outputEnd; c++)
                {
                    outputColumns.Add(new OutputColumnEntry { ColNumber = c, Header = ColNumberToLetter(c) });
                }
            }

            rowCount = CountRowsWithKeyData(worksheet, keyFirstRow, keyRowCount, keyColumns);
            return true;
        }

        /// <summary>
        /// Intersects the span starting at <paramref name="start"/> of length
        /// <paramref name="count"/> with the inclusive bounds
        /// <paramref name="boundFirst"/>..<paramref name="boundLast"/>.
        /// Yields <paramref name="clampedCount"/> of 0 when the spans do not overlap.
        /// </summary>
        private static void ClampSpan(
            int start,
            int count,
            int boundFirst,
            int boundLast,
            out int clampedStart,
            out int clampedCount)
        {
            int last = start + Math.Max(count, 0) - 1;
            clampedStart = Math.Max(start, boundFirst);
            int clampedLast = Math.Min(last, boundLast);
            clampedCount = clampedLast - clampedStart + 1;
            if (clampedCount < 0) clampedCount = 0;
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
                ClearOutputRangePreview();
                _selectionLocked = true;
                UnsubscribeSelectionChanged();

                var payload = DocumentMatcherMessageParser.ParseStartMatching(raw);

                // No ActiveWorkbook fallback: guessing here would match against another
                // workbook's PDFs, and the pdfIds that come back would then be written as
                // links in this one — references to documents it does not contain.
                var workbook = GetSelectedWorkbook();
                if (workbook == null || _selectedRange == null)
                {
                    DocuLinkLog.Trace("start-matching aborted: selection workbook unavailable");
                    return;
                }

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
                    session.Store.TryLoadPdfBinary(meta.Id, out PdfBinaryParts parts);
                    string base64 = parts.Base64;
                    string geometryBase64 = parts.GeometryBase64;
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

                if (!TryAnalyzeSelection(_selectedRange, out int dataRows, out var keyColumns, out _))
                    return;
                DocuLinkLog.Trace($"start-matching selection dataRows={dataRows} keyColumns={keyColumns.Count}");

                var firstArea = (Excel.Range)_selectedRange.Areas[1];
                var worksheet = (Excel.Worksheet)firstArea.Worksheet;
                int selectedRows = firstArea.Rows.Count;

                var rows = new List<MatcherRowEntry>();

                for (int r = 1; r <= selectedRows; r++)
                {
                    int excelRow = _firstSelectedRow + (r - 1);
                    var keyValues = new List<string>();
                    bool hasKeyData = false;

                    foreach (var keyColumn in keyColumns)
                    {
                        var cell = (Excel.Range)worksheet.Cells[excelRow, keyColumn.ColNumber];
                        string value = ReadMatcherKeyValue(cell);
                        if (!string.IsNullOrWhiteSpace(value))
                            hasKeyData = true;
                        keyValues.Add(value);
                    }

                    if (!hasKeyData)
                        continue;

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

                // Must be the selection's workbook. This used to read ActiveWorkbook, so if
                // focus moved during the run — matching is slow enough to invite that — the
                // link records and formula tracking bindings went to the newly active
                // workbook while the target cells stayed in this one. The bindings then
                // failed and the other workbook was left holding entries for cells it does not own.
                var workbook = GetSelectedWorkbook();
                if (workbook == null || _selectedRange == null)
                {
                    DocuLinkLog.Trace("create-links aborted: selection workbook unavailable");
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

        /// <summary>
        /// Resets the wizard to step 1 by re-sending matcher-ready with the current
        /// Excel selection. Instant — no page reload required.
        /// </summary>
        internal void Reset()
        {
            if (!_webViewReady) return;
            ClearOutputRangePreview();
            UnsubscribeSelectionChanged();
            _selectionLocked = false;
            HandleAppReady();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                ClearOutputRangePreview();
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                ClearOutputRangePreview();
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

        private static bool ColumnHasContent(Excel.Worksheet worksheet, int firstRow, int rowCount, int col)
        {
            if (rowCount <= 0) return false;
            var topCell = (Excel.Range)worksheet.Cells[firstRow, col];
            var botCell = (Excel.Range)worksheet.Cells[firstRow + rowCount - 1, col];
            var range   = worksheet.get_Range(topCell, botCell);
            object v    = range.Value2;
            if (v == null) return false;
            if (v is object[,] arr)
            {
                foreach (object item in arr)
                    if (item != null && !(item is string s && s.Length == 0))
                        return true;
                return false;
            }
            return !(v is string str && str.Length == 0);
        }

        private static int CountRowsWithKeyData(
            Excel.Worksheet worksheet,
            int firstRow,
            int rowCount,
            IList<KeyColumnEntry> keyColumns)
        {
            if (rowCount <= 0 || keyColumns.Count == 0) return 0;

            // One bulk Value2 read per key column instead of one COM round trip per cell:
            // a 50,000-row selection over 3 key columns costs 3 calls, not 150,000.
            var rowHasData = new bool[rowCount];
            int remaining = rowCount;

            foreach (var keyColumn in keyColumns)
            {
                if (remaining == 0) break; // every row already accounted for

                var topCell = (Excel.Range)worksheet.Cells[firstRow, keyColumn.ColNumber];
                var botCell = (Excel.Range)worksheet.Cells[firstRow + rowCount - 1, keyColumn.ColNumber];
                object values = worksheet.get_Range(topCell, botCell).Value2;

                if (values is object[,] block)
                {
                    // Excel returns a 1-based [rows, cols] array.
                    int lower = block.GetLowerBound(0);
                    for (int i = 0; i < rowCount; i++)
                    {
                        if (rowHasData[i]) continue;
                        if (!CellHasContent(block[lower + i, block.GetLowerBound(1)])) continue;
                        rowHasData[i] = true;
                        remaining--;
                    }
                }
                else if (!rowHasData[0] && CellHasContent(values))
                {
                    // Single-cell span: Value2 is a scalar rather than an array.
                    rowHasData[0] = true;
                    remaining--;
                }
            }

            return rowCount - remaining;
        }

        private static bool CellHasContent(object value)
        {
            if (value == null) return false;
            return !(value is string text && string.IsNullOrWhiteSpace(text));
        }

        /// <summary>
        /// Returns the semantic text the matcher should look for. Excel stores dates as OA
        /// serial numbers in Value2, so date-formatted cells must be rendered with their Excel
        /// number format before crossing into the web matcher. Other values retain the existing
        /// Value2 representation; number/accounting normalization is handled by the shared index.
        /// </summary>
        private static string ReadMatcherKeyValue(Excel.Range cell)
        {
            object value2 = cell?.Value2;
            if (value2 == null) return string.Empty;

            try
            {
                if (cell.Value is DateTime)
                    return CellFormattingService.FormatLikeCell(cell, Convert.ToDouble(value2));
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace(
                    $"matcher date formatting fallback {ex.GetType().FullName}: {ex.Message}");
            }

            return value2.ToString() ?? string.Empty;
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
