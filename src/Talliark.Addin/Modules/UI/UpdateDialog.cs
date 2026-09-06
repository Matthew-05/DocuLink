using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Talliark.Addin.Modules.Services;
using Talliark.Addin.Properties;

namespace Talliark.Addin.Modules.UI
{
    internal sealed class UpdateDialog : Form
    {
        private enum State { Checking, Found, UpToDate, Dev, Downloading, Complete, Error }

        private static readonly object InstanceSync = new object();
        private static UpdateDialog _activeDialog;
        private static bool _installationStarted;

        private readonly UpdateCheckResult _preChecked;
        private CancellationTokenSource _downloadCts;
        private string _localMsiPath;

        private const int ContentMargin = 20;
        private const int CardPadding = 16;
        private const int FooterHeight = 64;
        private const int CompactWidth = 460;
        private const int CompactHeight = 180;
        private const int NotesWidth = 600;
        private const int NotesHeight = 560;

        private readonly Label _statusLabel;
        private readonly Label _versionLabel;
        private readonly ProgressBar _progressBar;
        private readonly Label _percentLabel;
        private readonly Button _actionButton;
        private readonly Button _closeButton;
        private readonly CheckBox _snoozeCheckBox;
        private readonly Label _releaseNotesLabel;
        private readonly Panel _notesSeparator;
        private readonly CardPanel _notesCard;
        private readonly ReleaseNotesControl _releaseNotes;
        private readonly Panel _footer;

        private UpdateDialog(UpdateCheckResult preChecked = null)
        {
            _preChecked = preChecked;

            Text = "Talliark Updates";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = DialogTheme.Canvas;
            Font = DialogTheme.BodyFont;
            ForeColor = DialogTheme.Text;
            ClientSize = new Size(CompactWidth, CompactHeight);

            _statusLabel = new Label
            {
                AutoSize = false,
                Font = DialogTheme.SectionTitleFont,
                ForeColor = DialogTheme.Text,
                Text = "Checking for updates…"
            };

            _versionLabel = new Label
            {
                AutoSize = false,
                Font = DialogTheme.CaptionFont,
                ForeColor = DialogTheme.MutedText,
                Visible = false
            };

            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };

            _percentLabel = new Label
            {
                AutoSize = true,
                Font = DialogTheme.CaptionFont,
                ForeColor = DialogTheme.MutedText,
                Text = "0%",
                Visible = false
            };

            _releaseNotesLabel = DialogTheme.CreateSectionTitle(
                "What’s new", new Point(CardPadding, 14));

            _notesSeparator = new Panel { BackColor = DialogTheme.Border };

            _releaseNotes = new ReleaseNotesControl { BorderStyle = BorderStyle.None };

            _notesCard = new CardPanel { Visible = false };
            _notesCard.Controls.Add(_releaseNotesLabel);
            _notesCard.Controls.Add(_notesSeparator);
            _notesCard.Controls.Add(_releaseNotes);

            _actionButton = new Button
            {
                Size = new Size(130, 30),
                Text = "Download",
                Visible = false
            };
            DialogTheme.StylePrimaryButton(_actionButton);
            _actionButton.Click += ActionButton_Click;

            _closeButton = new Button
            {
                Size = new Size(96, 30),
                Text = "Close",
                DialogResult = DialogResult.Cancel
            };
            DialogTheme.StyleSecondaryButton(_closeButton);
            CancelButton = _closeButton;

            _snoozeCheckBox = new CheckBox
            {
                AutoSize = true,
                Font = DialogTheme.BodyFont,
                ForeColor = DialogTheme.MutedText,
                Cursor = Cursors.Hand,
                Text = "Don’t check again for 24 hours",
                Visible = false
            };

            _footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = DialogTheme.Surface
            };
            _footer.Controls.Add(new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = DialogTheme.Border
            });
            _footer.Controls.Add(_snoozeCheckBox);
            _footer.Controls.Add(_actionButton);
            _footer.Controls.Add(_closeButton);

            Controls.AddRange(new Control[]
            {
                _statusLabel,
                _versionLabel,
                _progressBar,
                _percentLabel,
                _notesCard,
                _footer
            });

            ApplyLayout(showNotes: false);
        }

        /// <summary>
        /// Shows the single update workflow shared by automatic and manual checks.
        /// The dialog remains registered while an update is downloading, and a
        /// successful installer launch suppresses further dialogs for this process.
        /// </summary>
        internal static bool ShowSingle(
            UpdateCheckResult preChecked = null,
            IWin32Window owner = null)
        {
            UpdateDialog dialog;
            UpdateDialog existing;

            lock (InstanceSync)
            {
                if (_installationStarted)
                    return false;

                existing = _activeDialog;
                if (existing == null || existing.IsDisposed)
                {
                    dialog = new UpdateDialog(preChecked);
                    _activeDialog = dialog;
                    existing = null;
                }
                else
                {
                    dialog = null;
                }
            }

            if (existing != null)
            {
                FocusExistingDialog(existing);
                return false;
            }

            try
            {
                if (owner == null)
                    dialog.ShowDialog();
                else
                    dialog.ShowDialog(owner);

                return true;
            }
            finally
            {
                lock (InstanceSync)
                {
                    if (ReferenceEquals(_activeDialog, dialog))
                        _activeDialog = null;
                }

                dialog.Dispose();
            }
        }

        private static void FocusExistingDialog(UpdateDialog dialog)
        {
            if (dialog.IsDisposed || !dialog.IsHandleCreated)
                return;

            Action focus = () =>
            {
                if (dialog.IsDisposed || !dialog.Visible)
                    return;

                dialog.BringToFront();
                dialog.Activate();
            };

            if (dialog.InvokeRequired)
                dialog.BeginInvoke(focus);
            else
                focus();
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
            SetReleaseNotesLayout(state == State.Found || state == State.Dev, result);

            switch (state)
            {
                case State.Checking:
                    _statusLabel.Text = "Checking for updates…";
                    _versionLabel.Visible = false;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Visible = false;
                    _snoozeCheckBox.Visible = false;
                    _closeButton.Text = "Cancel";
                    break;

                case State.Found:
                    _statusLabel.Text = "Update available";
                    _versionLabel.Text =
                        $"Version {result.LatestVersion} · you have {AppVersion.Current}";
                    _versionLabel.Visible = true;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Text = string.IsNullOrEmpty(result.DownloadUrl) ? "Release Page" : "Download";
                    _actionButton.Tag = result;
                    _actionButton.Visible = true;
                    _snoozeCheckBox.Checked = false;
                    _snoozeCheckBox.Visible = true;
                    _closeButton.Text = "Later";
                    break;

                case State.UpToDate:
                    _statusLabel.Text = "Talliark is up to date";
                    _versionLabel.Text = result != null
                        ? $"Version {result.LatestVersion} is the latest release."
                        : $"Version {AppVersion.Current}";
                    _versionLabel.Visible = true;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Visible = false;
                    _snoozeCheckBox.Visible = false;
                    _closeButton.Text = "Close";
                    break;

                case State.Dev:
                    _statusLabel.Text = "You’re on a development build";
                    _versionLabel.Text = result != null
                        ? $"Latest published version: {result.LatestVersion}"
                        : string.Empty;
                    _versionLabel.Visible = true;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Text = "Download";
                    _actionButton.Tag = result;
                    _actionButton.Visible = true;
                    _snoozeCheckBox.Checked = false;
                    _snoozeCheckBox.Visible = true;
                    _closeButton.Text = "Later";
                    break;

                case State.Downloading:
                    _statusLabel.Text = "Downloading update…";
                    _progressBar.Value = 0;
                    _progressBar.Visible = true;
                    _percentLabel.Text = "0%";
                    _percentLabel.Visible = true;
                    _actionButton.Visible = false;
                    _snoozeCheckBox.Visible = false;
                    _closeButton.Text = "Cancel";
                    break;

                case State.Complete:
                    _statusLabel.Text = "Download complete — ready to install";
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Text = "Install Now";
                    _actionButton.Tag = null;
                    _actionButton.Visible = true;
                    _snoozeCheckBox.Visible = false;
                    _closeButton.Text = "Later";
                    break;

                case State.Error:
                    _statusLabel.Text = "Could not check for updates";
                    _versionLabel.Visible = false;
                    _progressBar.Visible = false;
                    _percentLabel.Visible = false;
                    _actionButton.Visible = false;
                    _snoozeCheckBox.Visible = false;
                    _closeButton.Text = "Close";
                    break;
            }
        }

        /// <summary>
        /// Shows the notes for the update being offered — the newest release the
        /// user does not have — rather than the whole published history.
        /// </summary>
        private void SetReleaseNotesLayout(bool visible, UpdateCheckResult result)
        {
            ReleaseNote latest = visible ? SelectOfferedRelease(result) : null;

            _releaseNotesLabel.Text =
                latest != null && !string.IsNullOrWhiteSpace(latest.Version)
                    ? $"What’s new in v{latest.Version}"
                    : "What’s new";

            ApplyLayout(visible);

            if (visible)
                _releaseNotes.SetRelease(latest);
        }

        /// <summary>
        /// The release matching the version on offer, falling back to the most
        /// recently published one when no tag matches.
        /// </summary>
        private static ReleaseNote SelectOfferedRelease(UpdateCheckResult result)
        {
            var notes = result?.ReleaseNotes;
            if (notes == null || notes.Count == 0) return null;

            for (int i = 0; i < notes.Count; i++)
            {
                if (string.Equals(
                        notes[i].Version,
                        result.LatestVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return notes[i];
                }
            }

            // The service sorts newest first, so entry zero is the latest release.
            return notes[0];
        }

        /// <summary>
        /// Sizes the dialog for its two shapes: a compact status window, and the
        /// taller form that carries the release-notes card.
        /// </summary>
        private void ApplyLayout(bool showNotes)
        {
            ClientSize = showNotes
                ? new Size(NotesWidth, NotesHeight)
                : new Size(CompactWidth, CompactHeight);

            int width = ClientSize.Width;
            int textWidth = width - (ContentMargin * 2) - 8;

            _statusLabel.SetBounds(ContentMargin + 2, 22, textWidth, 24);
            _versionLabel.SetBounds(ContentMargin + 4, 50, textWidth, 18);
            _progressBar.SetBounds(ContentMargin + 4, 84, textWidth - 52, 10);
            _percentLabel.Location = new Point(width - ContentMargin - 40, 80);

            _notesCard.Visible = showNotes;
            if (showNotes)
            {
                const int CardTop = 82;
                _notesCard.SetBounds(
                    ContentMargin,
                    CardTop,
                    width - (ContentMargin * 2),
                    ClientSize.Height - FooterHeight - ContentMargin - CardTop);

                _notesSeparator.SetBounds(
                    CardPadding, 46, _notesCard.Width - (CardPadding * 2), 1);

                _releaseNotes.SetBounds(
                    CardPadding,
                    60,
                    _notesCard.Width - (CardPadding * 2),
                    _notesCard.Height - 60 - CardPadding);
            }

            int buttonTop = (FooterHeight - _closeButton.Height) / 2 + 1;
            _closeButton.Location =
                new Point(width - ContentMargin - _closeButton.Width, buttonTop);
            _actionButton.Location =
                new Point(_closeButton.Left - 8 - _actionButton.Width, buttonTop);
            _snoozeCheckBox.Location = new Point(ContentMargin, buttonTop + 6);

            RecenterIfVisible();
        }

        private void RecenterIfVisible()
        {
            if (!Visible) return;

            if (Owner == null)
                CenterToScreen();
            else
                CenterToParent();
        }

        private async void ActionButton_Click(object sender, EventArgs e)
        {
            // Installation path — Windows Installer handles the major upgrade and
            // coordinates any loaded Excel files through its standard UI.
            if (_actionButton.Tag == null)
            {
                if (_localMsiPath != null)
                {
                    lock (InstanceSync)
                    {
                        if (_installationStarted)
                            return;

                        _installationStarted = true;
                    }

                    try
                    {
                        UpdateInstallerService.Start(_localMsiPath);
                    }
                    catch
                    {
                        lock (InstanceSync)
                            _installationStarted = false;

                        _statusLabel.Text = "Could not start the installer.";
                        _actionButton.Visible = true;
                        _closeButton.Text = "Close";
                        return;
                    }

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

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_snoozeCheckBox.Checked)
            {
                Settings.Default.LastUpdateCheck = DateTime.UtcNow;
                Settings.Default.Save();
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
