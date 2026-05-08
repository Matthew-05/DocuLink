namespace DocuLink.Addin
{
    /// <summary>
    /// Pre-initializes WebView2 hosts at add-in startup so the first
    /// user interaction is instant rather than paying the cold-start cost.
    /// </summary>
    internal static class WebViewEagerLoader
    {
        internal static void Initialize(ThisAddIn addIn)
        {
            // Task pane: create invisibly for the initial workbook and force HWND so InitAsync starts now.
            // EnsureTaskPaneCreated is a no-op when no workbook is open yet, so guard against null.
            addIn.EnsureTaskPaneCreated();
            var host = addIn.TaskPaneHost;
            if (host != null)
                _ = host.Handle;

            // File manager: create and force HWND so InitAsync starts now
            addIn.PreloadFileManagerWindow();
        }
    }
}
