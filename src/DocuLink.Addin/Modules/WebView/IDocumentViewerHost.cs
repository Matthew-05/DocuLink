namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Common surface for document-viewer hosts (task pane or pop-out window).</summary>
    internal interface IDocumentViewerHost
    {
        void RefreshDataIfReady();

        void SendPdfUpdated(string pdfId);

        void SendNavigateToRectangle(string id, string pdfId, int page);

        void SendClearRectangleHighlight();

        void SendLinkRectanglesRemoved(System.Collections.Generic.IList<string> ids);
    }
}
