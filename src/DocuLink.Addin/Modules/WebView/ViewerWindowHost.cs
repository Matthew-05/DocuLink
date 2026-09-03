using System;
using System.Windows.Forms;
using DocuLink.Addin.Modules;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Hosts the document-viewer web UI in a standalone non-modal window.</summary>
    public sealed class ViewerWindowHost : Form, IDocumentViewerHost
    {
        private readonly DocumentViewerController _controller;

        public ViewerWindowHost(Excel.Workbook workbook)
        {
            string workbookName;
            try
            {
                workbookName = workbook?.Name;
            }
            catch
            {
                workbookName = null;
            }

            Text = string.IsNullOrWhiteSpace(workbookName)
                ? "DocuLink \u2013 Document Viewer"
                : $"DocuLink \u2013 Document Viewer \u2013 {workbookName}";
            Width = 900;
            Height = 700;
            MinimumSize = new System.Drawing.Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;

            _controller = new DocumentViewerController(this, "document viewer", workbook);
            Controls.Add(_controller.Surface);
            _controller.Start();
        }

        public void SendClearRectangleHighlight() => _controller.SendClearRectangleHighlight();

        public void SendLinkSelectionChanged(System.Collections.Generic.IList<LinkSelectionEntry> entries) =>
            _controller.SendLinkSelectionChanged(entries);

        public void SendSearchQuery(string query) => _controller.SendSearchQuery(query);

        public void SendLinkRectanglesRemoved(System.Collections.Generic.IList<string> ids) =>
            _controller.SendLinkRectanglesRemoved(ids);

        public void SendCharBboxesVisible(bool visible) =>
            _controller.SendCharBboxesVisible(visible);

        public void NotifyViewerShown() => _controller.NotifyViewerShown();

        public void SendNavigateToRectangle(string id, string pdfId, int page) =>
            _controller.SendNavigateToRectangle(id, pdfId, page);

        public void RefreshDataIfReady() => _controller.RefreshDataIfReady();

        public void InvalidateData() => _controller.InvalidateData();

        public void SendPdfUpdated(string pdfId) => _controller.SendPdfUpdated(pdfId);

        public void SendPdfAdded(string pdfId) => _controller.SendPdfAdded(pdfId);

        public void SendPdfNameUpdated(string id, string name) => _controller.SendPdfNameUpdated(id, name);

        public void SendPdfRemoved(string id) => _controller.SendPdfRemoved(id);

        public void SendShowPdf(string pdfId) => _controller.SendShowPdf(pdfId);

        public void SendFoldersToWebView() => _controller.SendFoldersToWebView();

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DocuLinkLog.Trace($"ENTER reason={e.CloseReason}");
            base.OnFormClosed(e);
            DocuLinkLog.Trace("EXIT");
        }

        protected override void Dispose(bool disposing)
        {
            DocuLinkLog.Trace($"ENTER disposing={disposing}");
            if (disposing)
                _controller.Dispose();
            base.Dispose(disposing);
            DocuLinkLog.Trace("EXIT");
        }
    }
}
