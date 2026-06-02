using System.Windows.Forms;
using DocuLink.Addin.Modules;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Hosts the document-viewer web UI inside a task pane WebView2 control.</summary>
    public sealed class TaskPaneHost : UserControl, IDocumentViewerHost
    {
        private readonly DocumentViewerController _controller;

        public TaskPaneHost()
        {
            Dock = DockStyle.Fill;
            _controller = new DocumentViewerController(this, "task pane");
            Controls.Add(_controller.Surface);
            _controller.Start();
        }

        public void SendClearRectangleHighlight() => _controller.SendClearRectangleHighlight();

        public void SendLinkRectanglesRemoved(System.Collections.Generic.IList<string> ids) =>
            _controller.SendLinkRectanglesRemoved(ids);

        public void NotifyViewerShown() => _controller.NotifyViewerShown();

        public void SendNavigateToRectangle(string id, string pdfId, int page) =>
            _controller.SendNavigateToRectangle(id, pdfId, page);

        public void RefreshDataIfReady() => _controller.RefreshDataIfReady();

        public void InvalidateData() => _controller.InvalidateData();

        public void SendPdfUpdated(string pdfId) => _controller.SendPdfUpdated(pdfId);

        public void SendPdfAdded(string pdfId) => _controller.SendPdfAdded(pdfId);

        public void SendPdfNameUpdated(string id, string name) => _controller.SendPdfNameUpdated(id, name);

        public void SendPdfRemoved(string id) => _controller.SendPdfRemoved(id);

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
