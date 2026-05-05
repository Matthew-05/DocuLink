using System.Windows.Forms;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>Host surface for the DocuLink task pane (WebView2 will attach here later).</summary>
    public sealed class TaskPaneHost : UserControl
    {
        public TaskPaneHost()
        {
            Dock = DockStyle.Fill;
        }
    }
}
