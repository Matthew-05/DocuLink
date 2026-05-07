using System.Drawing;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.UI
{
    /// <summary>
    /// A non-closable modal-style dialog with an indeterminate marquee progress bar.
    /// The user cannot dismiss it — call <see cref="ForceClose"/> from code when
    /// the operation completes.
    /// </summary>
    internal sealed class ProgressDialog : Form
    {
        private bool _allowClose;

        public ProgressDialog(string message)
        {
            Text             = "DocuLink";
            FormBorderStyle  = FormBorderStyle.FixedDialog;
            ControlBox       = false;
            ShowInTaskbar    = false;
            StartPosition    = FormStartPosition.CenterScreen;
            MaximizeBox      = false;
            MinimizeBox      = false;
            ClientSize       = new Size(320, 72);

            var label = new Label
            {
                Text      = message,
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 32,
                TextAlign = ContentAlignment.BottomCenter,
                Padding   = new Padding(0, 6, 0, 0),
            };

            var bar = new ProgressBar
            {
                Style                 = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Dock                  = DockStyle.Bottom,
                Height                = 24,
            };

            Controls.Add(label);
            Controls.Add(bar);
        }

        /// <summary>Closes the dialog from code, bypassing the close guard.</summary>
        internal void ForceClose()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose)
                e.Cancel = true;

            base.OnFormClosing(e);
        }
    }
}
