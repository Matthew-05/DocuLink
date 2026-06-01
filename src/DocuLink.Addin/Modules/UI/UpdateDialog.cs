using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DocuLink.Addin.Modules.Services;

namespace DocuLink.Addin.Modules.UI
{
    internal sealed class UpdateDialog : Form
    {
        private enum State { Checking, Found, UpToDate, Dev, Downloading, Complete, Error }

        private readonly UpdateCheckResult _preChecked;
        private CancellationTokenSource _downloadCts;
        private string _localMsiPath;

        private readonly Label _statusLabel;
        private readonly Label _versionLabel;
        private readonly ProgressBar _progressBar;
        private readonly Label _percentLabel;
        private readonly Button _actionButton;
        private readonly Button _closeButton;

        internal UpdateDialog(UpdateCheckResult preChecked = null)
        {
            _preChecked = preChecked;

            Text = "DocuLink Updates";
            ClientSize = new Size(400, 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            _statusLabel = new Label
            {
                AutoSize = false,
                Size = new Size(360, 22),
                Location = new Point(20, 20),
                Font = new Font("Segoe UI", 10f),
                Text = "Checking for updates…"
            };

            _versionLabel = new Label
            {
                AutoSize = false,
                Size = new Size(360, 18),
                Location = new Point(20, 46),
                Font = new Font("Segoe UI", 9f),
                ForeColor = SystemColors.GrayText,
                Visible = false
            };

            _progressBar = new ProgressBar
            {
                Size = new Size(300, 18),
                Location = new Point(20, 72),
                Minimum = 0,
                Maximum = 100,
                Visible = false
            };

            _percentLabel = new Label
            {
                AutoSize = true,
                Location = new Point(328, 72),
                Font = new Font("Segoe UI", 9f),
                Text = "0%",
                Visible = false
            };

            _actionButton = new Button
            {
                Size = new Size(110, 28),
                Location = new Point(160, 122),
                Text = "Download",
                Visible = false
            };
            _actionButton.Click += ActionButton_Click;

            _closeButton = new Button
            {
                Size = new Size(80, 28),
                Location = new Point(300, 122),
                Text = "Close",
                DialogResult = DialogResult.Cancel
            };
            CancelButton = _closeButton;

            Controls.AddRange(new Control[] { _statusLabel, _versionLabel, _progressBar, _percentLabel, _actionButton, _closeButton });
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (_preChecked != null)
            {
                ApplyResult(_preChecked);
                return;
            }

            SetState(State.Checking);
            UpdateCheckResult result;
            try
            {
                result = await UpdateCheckService.CheckAsync().ConfigureAwait(true);
            }
            catch
            {
                result = null;
            }

            ApplyResult(result);
        }

        private void ApplyResult(UpdateCheckResult result)
        {
            if (result == null)
            {
                SetState(State.Error);
                return;
            }
            if (result.IsDevBuild)
            {
                SetState(State.Dev, result);
                return;
            }
            SetState(result.UpdateAvailable ? State.Found : State.UpToDate, result);
        }

        private void SetState(State state, UpdateCheckResult result = null)
        {
            switch (state)
            {
                case State.Checking:
                    _statusLabel.Text = "Checking for updates…";
                    _versionLabel.Visible = false;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Visible = false;
                    _closeButton.Text = "Cancel";
                    break;

                case State.Found:
                    _statusLabel.Text = "A new version of DocuLink is available.";
                    _versionLabel.Text = $"v{result.LatestVersion}  (you have {AppVersion.Current})";
                    _versionLabel.Visible = true;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Text = string.IsNullOrEmpty(result.DownloadUrl) ? "Release Page" : "Download";
                    _actionButton.Tag = result;
                    _actionButton.Visible = true;
                    _closeButton.Text = "Later";
                    break;

                case State.UpToDate:
                    _statusLabel.Text = "DocuLink is up to date.";
                    _versionLabel.Text = result != null ? $"(v{result.LatestVersion})" : $"(v{AppVersion.Current})";
                    _versionLabel.Visible = true;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Visible = false;
                    _closeButton.Text = "Close";
                    break;

                case State.Dev:
                    _statusLabel.Text = "Current build is Dev.";
                    _versionLabel.Text = result != null ? $"Most recent version available: v{result.LatestVersion}" : "";
                    _versionLabel.Visible = true;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Text = "Download";
                    _actionButton.Tag = result;
                    _actionButton.Visible = true;
                    _closeButton.Text = "Close";
                    break;

                case State.Downloading:
                    _statusLabel.Text = "Downloading update…";
                    _progressBar.Value = 0;
                    _progressBar.Visible = true;
                    _percentLabel.Text = "0%";
                    _percentLabel.Visible = true;
                    _actionButton.Visible = false;
                    _closeButton.Text = "Cancel";
                    break;

                case State.Complete:
                    _statusLabel.Text = "Download complete. Ready to install.";
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Text = "Install Now";
                    _actionButton.Tag = null;
                    _actionButton.Visible = true;
                    _closeButton.Text = "Later";
                    break;

                case State.Error:
                    _statusLabel.Text = "Could not check for updates.";
                    _versionLabel.Visible = false;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Visible = false;
                    _closeButton.Text = "Close";
                    break;
            }
        }

        private async void ActionButton_Click(object sender, EventArgs e)
        {
            // "Install Now" path — _actionButton.Tag is null
            if (_actionButton.Tag == null)
            {
                if (_localMsiPath != null)
                {
                    Process.Start("msiexec.exe", $"/i \"{_localMsiPath}\"");
                    Globals.ThisAddIn.CloseAllApplicationWindows();
                    var owner = Owner;
                    Close();
                    owner?.Close();
                }
                return;
            }

            var result = _actionButton.Tag as UpdateCheckResult;
            if (result == null) return;

            // "Release Page" fallback when no MSI asset exists
            if (string.IsNullOrEmpty(result.DownloadUrl))
            {
                Process.Start(result.ReleaseUrl);
                Close();
                return;
            }

            // "Download" path
            SetState(State.Downloading, result);

            _downloadCts = new CancellationTokenSource();
            var progress = new Progress<int>(pct =>
            {
                _progressBar.Value = Math.Min(pct, 100);
                _percentLabel.Text = $"{pct}%";
            });

            try
            {
                _localMsiPath = await UpdateCheckService.DownloadAsync(result.DownloadUrl, result.LatestVersion, progress, _downloadCts.Token).ConfigureAwait(true);
                SetState(State.Complete);
            }
            catch (OperationCanceledException)
            {
                SetState(State.Found, result);
            }
            catch
            {
                _statusLabel.Text = "Download failed. Please try again.";
                _actionButton.Text = "Download";
                _actionButton.Tag = result;
                _actionButton.Visible = true;
                _closeButton.Text = "Close";
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _downloadCts?.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _downloadCts?.Dispose();
            base.Dispose(disposing);
        }
    }
}
