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
            // Task pane: create invisibly and force HWND so InitAsync starts now
            addIn.EnsureTaskPaneCreated();
            _ = addIn.TaskPaneHost.Handle;

            // File manager: create and force HWND so InitAsync starts now
            addIn.PreloadFileManagerWindow();
        }
    }
}
