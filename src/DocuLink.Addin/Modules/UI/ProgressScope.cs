namespace DocuLink.Addin.Modules.UI
{
    /// <summary>
    /// Shows a <see cref="ProgressDialog"/> on construction and closes it on
    /// <see cref="Dispose"/>. Intended for use in a <c>using</c> block around
    /// any operation that may take a noticeable amount of time.
    ///
    /// <para>
    /// For synchronous operations the marquee will not animate while the UI
    /// thread is blocked, but the window appears before the work starts and
    /// disappears once it finishes, giving clear visual feedback.
    /// </para>
    /// </summary>
    internal sealed class ProgressScope : System.IDisposable
    {
        private ProgressDialog _dialog;
        private bool _disposed;

        public ProgressScope(string message)
        {
            // TODO: re-enable loading popup
            return;

            _dialog = new ProgressDialog(message);
            _dialog.Show();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_dialog == null) return;
            _dialog.ForceClose();
        }
    }
}
