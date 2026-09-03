using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DocuLink.Addin.Modules.Infrastructure;
using DocuLink.Addin.Modules.Services;

namespace DocuLink.Addin.Modules.UI
{
    internal sealed class SettingsDialog : Form
    {
        /// <summary>Gap between the window edge and the cards.</summary>
        private const int ContentMargin = 20;

        /// <summary>Inner padding used by every card.</summary>
        private const int CardPadding = 16;

        /// <summary>Vertical gap between stacked cards.</summary>
        private const int CardGap = 16;

        /// <summary>Height of the Development card, which has a fixed set of controls.</summary>
        private const int DevelopmentCardHeight = 232;

        /// <summary>Height of the action bar holding the Close button.</summary>
        private const int FooterHeight = 56;

        private readonly ReleaseNotesControl _updateHistory;
        private bool _updateHistoryRequested;

        internal SettingsDialog()
        {
            bool showDevelopmentSection = AppVersion.IsDevelopment || AppVersion.IsBeta;

            Text = "DocuLink Settings";
            // Border style first: changing it after ClientSize would resize the client area.
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(620, 600);
            ClientSize = new Size(660, 720);
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = DialogTheme.Canvas;
            Font = DialogTheme.BodyFont;
            ForeColor = DialogTheme.Text;

            int cardWidth = ClientSize.Width - (ContentMargin * 2);

            Controls.Add(DialogTheme.CreateTitle("Settings", new Point(ContentMargin + 2, 20)));
            Controls.Add(DialogTheme.CreateCaption(
                $"Version {AppVersion.Current}", new Point(ContentMargin + 4, 51)));

            int nextCardTop = 82;

            if (showDevelopmentSection)
            {
                Controls.Add(BuildDevelopmentCard(
                    new Point(ContentMargin, nextCardTop), cardWidth));
                nextCardTop += DevelopmentCardHeight + CardGap;
            }

            // The updates card takes the remaining height so the release history
            // grows with the window instead of leaving dead space below it.
            int updatesHeight =
                ClientSize.Height - FooterHeight - ContentMargin - nextCardTop;

            Controls.Add(BuildUpdatesCard(
                new Point(ContentMargin, nextCardTop),
                new Size(cardWidth, updatesHeight),
                out _updateHistory));

            Button closeBtn;
            Panel footer = BuildFooter(out closeBtn);
            Controls.Add(footer);

            // Docking gives the footer its real width only once it has a parent, so the
            // Close button is placed afterwards for its right anchor to hold on resize.
            closeBtn.Location = new Point(
                footer.ClientSize.Width - ContentMargin - closeBtn.Width, 13);

            CancelButton = closeBtn;
        }

        /// <summary>Developer-only card; shown in development and beta builds.</summary>
        private CardPanel BuildDevelopmentCard(Point location, int width)
        {
            var card = new CardPanel
            {
                Location = location,
                Size = new Size(width, DevelopmentCardHeight),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            card.Controls.Add(DialogTheme.CreateSectionTitle(
                "Development", new Point(CardPadding, 14)));

            card.Controls.Add(DialogTheme.CreateSeparator(
                new Point(CardPadding, 46), width - (CardPadding * 2)));

            card.Controls.Add(DialogTheme.CreateSubHeading(
                "Debugging tools", new Point(CardPadding + 2, 60)));

            var bboxToggle = new CheckBox
            {
                Text = "Show character bounding boxes",
                AutoSize = true,
                Location = new Point(CardPadding, 82),
                Font = DialogTheme.BodyFont,
                ForeColor = DialogTheme.Text,
                Cursor = Cursors.Hand,
                Checked = DevSettings.ShowCharBoundingBoxes
            };
            bboxToggle.CheckedChanged += (s, e) =>
                DevSettings.ShowCharBoundingBoxes = bboxToggle.Checked;
            card.Controls.Add(bboxToggle);

            card.Controls.Add(DialogTheme.CreateCaption(
                "Draws the per-character text boxes over every page in the document viewer.",
                new Point(CardPadding + 18, 104)));

            var tableToggle = new CheckBox
            {
                Text = "Show table suggestions",
                AutoSize = true,
                Location = new Point(CardPadding, 130),
                Font = DialogTheme.BodyFont,
                ForeColor = DialogTheme.Text,
                Cursor = Cursors.Hand,
                Checked = DevSettings.ShowTableSuggestions
            };
            tableToggle.CheckedChanged += (s, e) =>
                DevSettings.ShowTableSuggestions = tableToggle.Checked;
            card.Controls.Add(tableToggle);

            card.Controls.Add(DialogTheme.CreateCaption(
                "Draws detected table regions and their row, column, and header bands.",
                new Point(CardPadding + 18, 152)));

            var openLogsBtn = new Button
            {
                Text = "Open Log Folder",
                Size = new Size(140, 30),
                Location = new Point(CardPadding, 184)
            };
            DialogTheme.StyleSecondaryButton(openLogsBtn);
            openLogsBtn.Click += OpenLogFolder;
            card.Controls.Add(openLogsBtn);

            return card;
        }

        /// <summary>Version actions plus the scrollable release history.</summary>
        private CardPanel BuildUpdatesCard(
            Point location, Size size, out ReleaseNotesControl history)
        {
            var card = new CardPanel
            {
                Location = location,
                Size = size,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                       | AnchorStyles.Left | AnchorStyles.Right
            };

            card.Controls.Add(DialogTheme.CreateSectionTitle(
                "Updates", new Point(CardPadding, 14)));

            var checkBtn = new Button
            {
                Text = "Check for Updates",
                Size = new Size(150, 30),
                Location = new Point(size.Width - CardPadding - 150, 12),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            DialogTheme.StylePrimaryButton(checkBtn);
            checkBtn.Click += (s, e) => UpdateDialog.ShowSingle(owner: this);
            card.Controls.Add(checkBtn);

            card.Controls.Add(DialogTheme.CreateSeparator(
                new Point(CardPadding, 54), size.Width - (CardPadding * 2)));

            card.Controls.Add(DialogTheme.CreateSubHeading(
                "Release history", new Point(CardPadding + 2, 68)));

            history = new ReleaseNotesControl
            {
                BorderStyle = BorderStyle.None,
                Location = new Point(CardPadding, 90),
                Size = new Size(
                    size.Width - (CardPadding * 2),
                    size.Height - 90 - CardPadding),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                       | AnchorStyles.Left | AnchorStyles.Right
            };
            card.Controls.Add(history);

            return card;
        }

        /// <summary>Action bar pinned to the bottom of the dialog.</summary>
        private Panel BuildFooter(out Button closeBtn)
        {
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = DialogTheme.Surface
            };

            footer.Controls.Add(new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = DialogTheme.Border
            });

            closeBtn = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.Cancel,
                Size = new Size(96, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            DialogTheme.StyleSecondaryButton(closeBtn);
            footer.Controls.Add(closeBtn);

            return footer;
        }

        private void OpenLogFolder(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(DocuLinkLog.DirectoryPath);
                Process.Start("explorer.exe", $"\"{DocuLinkLog.DirectoryPath}\"");
            }
            catch (Exception ex)
            {
                DocuLinkLog.Trace($"Could not open log folder: {ex}");
                MessageBox.Show(
                    this,
                    "The DocuLink log folder could not be opened.",
                    "DocuLink",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
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
