using System;
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
        private readonly Label _messageLabel;
        private readonly Label _detailLabel;
        private readonly ProgressBar _bar;

        public ProgressDialog(string message)
        {
            Text             = "DocuLink";
            FormBorderStyle  = FormBorderStyle.FixedDialog;
            ControlBox       = false;
            ShowInTaskbar    = false;
            StartPosition    = FormStartPosition.CenterScreen;
            MaximizeBox      = false;
            MinimizeBox      = false;
            TopMost          = true;
            ClientSize       = new Size(420, 116);

            _messageLabel = new Label
            {
                Text      = message,
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 38,
                TextAlign = ContentAlignment.BottomLeft,
                Padding   = new Padding(16, 12, 16, 0),
            };

            _detailLabel = new Label
            {
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 34,
                TextAlign = ContentAlignment.TopLeft,
                Padding   = new Padding(16, 2, 16, 0),
                ForeColor = SystemColors.GrayText,
            };

            _bar = new ProgressBar
            {
                Style                 = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Dock                  = DockStyle.Bottom,
                Height                = 22,
            };

            Controls.Add(_detailLabel);
            Controls.Add(_messageLabel);
            Controls.Add(_bar);
        }

        internal void BringForward()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(BringForward));
                return;
            }

            Show();
            WindowState = FormWindowState.Normal;
            TopMost = true;
            BringToFront();
            Activate();
            Refresh();
        }

        public void UpdateProgress(string message, string detail, int current, int total)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateProgress(message, detail, current, total)));
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
                _messageLabel.Text = message;

            _detailLabel.Text = detail ?? string.Empty;

            if (total > 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Minimum = 0;
                _bar.Maximum = total;
                _bar.Value = Math.Max(0, Math.Min(current, total));
            }
            else
            {
                _bar.Style = ProgressBarStyle.Marquee;
                _bar.MarqueeAnimationSpeed = 30;
            }
        }

        /// <summary>Closes the dialog from code, bypassing the close guard.</summary>
        internal void ForceClose()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ForceClose));
                return;
            }

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
