using System.Windows.Forms;
using DocuLink.Addin.Modules;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Hosts the document-viewer web UI inside a task pane WebView2 control.</summary>
    public sealed class TaskPaneHost : UserControl, IDocumentViewerHost
    {
        private readonly DocumentViewerController _controller;

        public TaskPaneHost(Excel.Workbook workbook)
        {
            Dock = DockStyle.Fill;
            _controller = new DocumentViewerController(this, "task pane", workbook);
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

        public void SendFsValuesVisible(bool visible) =>
            _controller.SendFsValuesVisible(visible);

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
