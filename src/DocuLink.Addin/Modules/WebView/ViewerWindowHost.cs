using System;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Hosts the document-viewer web UI in a standalone non-modal window.</summary>
    public sealed class ViewerWindowHost : Form, IDocumentViewerHost
    {
        private readonly DocumentViewerController _controller;

        public ViewerWindowHost()
        {
            Text = "DocuLink \u2013 Document Viewer";
            Width = 900;
            Height = 700;
            MinimumSize = new System.Drawing.Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;

            _controller = new DocumentViewerController(this, "document viewer");
            Controls.Add(_controller.WebView);
        }

        public void SendClearRectangleHighlight() => _controller.SendClearRectangleHighlight();

        public void SendNavigateToRectangle(string id, string pdfId, int page) =>
            _controller.SendNavigateToRectangle(id, pdfId, page);

        public void RefreshDataIfReady() => _controller.RefreshDataIfReady();

        public void SendPdfUpdated(string pdfId) => _controller.SendPdfUpdated(pdfId);

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
    }
}
