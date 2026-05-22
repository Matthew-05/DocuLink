using System.Windows.Forms;

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
            Controls.Add(_controller.WebView);
        }

        public void SendClearRectangleHighlight() => _controller.SendClearRectangleHighlight();

        public void SendLinkRectanglesRemoved(System.Collections.Generic.IList<string> ids) =>
            _controller.SendLinkRectanglesRemoved(ids);

        public void SendNavigateToRectangle(string id, string pdfId, int page) =>
            _controller.SendNavigateToRectangle(id, pdfId, page);

        public void RefreshDataIfReady() => _controller.RefreshDataIfReady();

        public void SendPdfUpdated(string pdfId) => _controller.SendPdfUpdated(pdfId);
    }
}
