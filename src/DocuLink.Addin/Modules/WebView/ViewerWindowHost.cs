using System;
using System.Windows.Forms;
using DocuLink.Addin.Modules;

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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            DocuLinkLog.Trace($"ENTER reason={e.CloseReason} cancel={e.Cancel}");

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                DocuLinkLog.Trace("EXIT user close hidden");
                return;
            }

            base.OnFormClosing(e);
            DocuLinkLog.Trace($"EXIT cancel={e.Cancel}");
        }

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
