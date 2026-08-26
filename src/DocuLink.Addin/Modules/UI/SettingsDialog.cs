using System;
using System.Drawing;
using System.Windows.Forms;
using DocuLink.Addin.Modules.Services;

namespace DocuLink.Addin.Modules.UI
{
    internal sealed class SettingsDialog : Form
    {
        private readonly ReleaseNotesControl _updateHistory;
        private bool _updateHistoryRequested;

        internal SettingsDialog()
        {
            Text = "DocuLink Settings";
            ClientSize = new Size(640, 590);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            Controls.Add(new Label
            {
                Text = "Updates",
                AutoSize = true,
                Location = new Point(20, 18),
                Font = new Font("Segoe UI", 12f, FontStyle.Bold)
            });

            Controls.Add(new Label
            {
                Text = $"Version: {AppVersion.Current}",
                AutoSize = true,
                Location = new Point(20, 50),
                Font = new Font("Segoe UI", 10f)
            });

            var checkBtn = new Button
            {
                Text = "Check for Updates",
                Size = new Size(130, 28),
                Location = new Point(490, 44),
                Font = new Font("Segoe UI", 9f)
            };
            checkBtn.Click += (s, e) => UpdateDialog.ShowSingle(owner: this);
            Controls.Add(checkBtn);

            Controls.Add(new Label
            {
                Text = "Update history",
                AutoSize = false,
                Size = new Size(600, 20),
                Location = new Point(20, 88),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            });

            _updateHistory = new ReleaseNotesControl
            {
                Size = new Size(600, 400),
                Location = new Point(20, 110)
            };
            Controls.Add(_updateHistory);

            var closeBtn = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.Cancel,
                Size = new Size(80, 28),
                Location = new Point(540, 548)
            };
            Controls.Add(closeBtn);
            CancelButton = closeBtn;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_updateHistoryRequested) return;

            _updateHistoryRequested = true;

            UpdateCheckResult result;
            try
            {
                result = await UpdateCheckService.CheckAsync().ConfigureAwait(true);
            }
            catch
            {
                result = null;
            }

            if (IsDisposed || Disposing) return;

            if (result == null)
            {
                _updateHistory.ShowStatusMessage("Update history could not be loaded.");
                return;
            }

            _updateHistory.SetReleases(result.ReleaseNotes);
        }
    }
}
