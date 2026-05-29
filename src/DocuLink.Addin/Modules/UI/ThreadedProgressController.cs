using System;
using System.Threading;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.UI
{
    internal sealed class ThreadedProgressController : IProgressReporter, IDisposable
    {
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private Thread _thread;
        private ProgressDialog _dialog;
        private bool _disposed;

        private ThreadedProgressController(string message)
        {
            _thread = new Thread(() => RunDialog(message));
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "DocuLink Progress";
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(2));
        }

        public static ThreadedProgressController Show(string message)
        {
            return new ThreadedProgressController(message);
        }

        public void Report(string message, string detail = null, int current = 0, int total = 0)
        {
            if (_disposed || _dialog == null)
                return;

            try
            {
                _dialog.UpdateProgress(message, detail, current, total);
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _dialog?.ForceClose();
            }
            catch (InvalidOperationException)
            {
            }

            if (_thread != null && _thread.IsAlive)
                _thread.Join(TimeSpan.FromSeconds(2));

            _ready.Dispose();
            _thread = null;
            _dialog = null;
        }

        private void RunDialog(string message)
        {
            using (var dialog = new ProgressDialog(message))
            {
                _dialog = dialog;
                dialog.Shown += (s, e) => dialog.BringForward();
                dialog.Paint += (s, e) => _ready.Set();
                Application.Run(dialog);
            }

            _ready.Set();
        }
    }
}
