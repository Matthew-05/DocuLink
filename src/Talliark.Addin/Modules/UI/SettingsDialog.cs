using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Talliark.Addin.Modules.Infrastructure;
using Talliark.Addin.Modules.Services;

namespace Talliark.Addin.Modules.UI
{
    internal sealed class SettingsDialog : Form
    {
        /// <summary>Gap between the window edge and the cards.</summary>
        private const int ContentMargin = 20;

        /// <summary>Inner padding used by every card.</summary>
        private const int CardPadding = 16;

        /// <summary>Vertical gap between stacked cards.</summary>
        private const int CardGap = 16;

        /// <summary>Where the Development card's flowed rows start, below its heading.</summary>
        private const int DevelopmentRowsTop = 82;

        /// <summary>How far a caption is indented under the switch it explains.</summary>
        private const int CaptionIndent = 18;

        /// <summary>Gap between a switch and its caption.</summary>
        private const int CaptionGap = 2;

        /// <summary>Gap between one switch-and-caption row and the next.</summary>
        private const int RowGap = 12;

        /// <summary>Height of the action bar holding the Close button.</summary>
        private const int FooterHeight = 56;

        /// <summary>Least room the release history is worth showing in.</summary>
        private const int MinimumUpdatesHeight = 140;

        private readonly ReleaseNotesControl _updateHistory;
        private bool _updateHistoryRequested;

        internal SettingsDialog()
        {
            bool showDevelopmentSection = AppVersion.IsDevelopment || AppVersion.IsBeta;

            Text = "Talliark Settings";
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

            CardPanel developmentCard = null;
            if (showDevelopmentSection)
            {
                developmentCard = BuildDevelopmentCard(
                    new Point(ContentMargin, nextCardTop), cardWidth);
                Controls.Add(developmentCard);
                nextCardTop += developmentCard.Height + CardGap;
            }

            // The development card is as tall as its wrapped captions need, so the
            // window's floor is computed from it rather than assumed. Without this a
            // narrow window would wrap the captions into more lines than the fixed
            // minimum height was ever sized for, and the updates card would slide
            // under the footer.
            int requiredClientHeight =
                nextCardTop + MinimumUpdatesHeight + FooterHeight + ContentMargin;
            if (requiredClientHeight > ClientSize.Height)
            {
                ClientSize = new Size(ClientSize.Width, requiredClientHeight);
            }
            int chromeHeight = Size.Height - ClientSize.Height;
            MinimumSize = new Size(
                MinimumSize.Width,
                Math.Max(MinimumSize.Height, requiredClientHeight + chromeHeight));

            // The updates card takes the remaining height so the release history
            // grows with the window instead of leaving dead space below it.
            int updatesHeight = Math.Max(
                MinimumUpdatesHeight,
                ClientSize.Height - FooterHeight - ContentMargin - nextCardTop);

            CardPanel updatesCard = BuildUpdatesCard(
                new Point(ContentMargin, nextCardTop),
                new Size(cardWidth, updatesHeight),
                out _updateHistory);
            Controls.Add(updatesCard);

            if (developmentCard != null)
            {
                // Wrapping makes the development card's height depend on the dialog's
                // width, so the updates card below it cannot keep a fixed top.
                developmentCard.SizeChanged += (sender, args) =>
                {
                    int top = developmentCard.Bottom + CardGap;
                    updatesCard.Location = new Point(ContentMargin, top);
                    updatesCard.Height = Math.Max(
                        MinimumUpdatesHeight,
                        ClientSize.Height - FooterHeight - ContentMargin - top);
                };
            }

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
        /// <remarks>
        /// The rows are laid out by flowing rather than by fixed coordinates. Every
        /// caption wraps at the card's width, so its height depends on how wide the
        /// dialog is, and a hard-coded Y for the row beneath it would overlap as soon
        /// as one line became two. <see cref="LayoutDevelopmentRows"/> re-runs on
        /// every width change and gives the card the height its content needs.
        /// </remarks>
        private CardPanel BuildDevelopmentCard(Point location, int width)
        {
            var card = new CardPanel
            {
                Location = location,
                Size = new Size(width, DevelopmentRowsTop),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            card.Controls.Add(DialogTheme.CreateSectionTitle(
                "Development", new Point(CardPadding, 14)));

            card.Controls.Add(DialogTheme.CreateSeparator(
                new Point(CardPadding, 46), width - (CardPadding * 2)));

            card.Controls.Add(DialogTheme.CreateSubHeading(
                "Debugging tools", new Point(CardPadding + 2, 60)));

            AddDebugToggle(
                card,
                "Show character bounding boxes",
                "Draws the per-character text boxes over every page in the document viewer.",
                DevSettings.ShowCharBoundingBoxes,
                value => DevSettings.ShowCharBoundingBoxes = value);

            AddDebugToggle(
                card,
                "Show detected-value debug boxes",
                "Values measure something. Click targets are always active; this draws "
                + "every detected target box.",
                DevSettings.ShowValues,
                value => DevSettings.ShowValues = value);

            AddDebugToggle(
                card,
                "Show reference boxes",
                "References identify something outside the document — an invoice number, "
                + "a phone number, a note citation. They are click targets either way; "
                + "this draws their boxes.",
                DevSettings.ShowReferences,
                value => DevSettings.ShowReferences = value);

            AddDebugToggle(
                card,
                "Show structure boxes",
                "Structure is the document indexing itself — a note heading's number, a "
                + "contents-row page number, the ordinal opening a footnote. Nothing in "
                + "it is a click target today.",
                DevSettings.ShowStructure,
                value => DevSettings.ShowStructure = value);

            AddDebugToggle(
                card,
                "Show refused spans",
                "Draws what is left once values, references and structure are taken: "
                + "damaged tokens, running headers, statute years.",
                DevSettings.ShowValueNoise,
                value => DevSettings.ShowValueNoise = value);

            var openLogsBtn = new Button
            {
                Text = "Open Log Folder",
                Size = new Size(140, 30)
            };
            DialogTheme.StyleSecondaryButton(openLogsBtn);
            openLogsBtn.Click += OpenLogFolder;
            openLogsBtn.Tag = DevelopmentRowTag.Footer;
            card.Controls.Add(openLogsBtn);

            LayoutDevelopmentRows(card);
            int laidOutAt = card.ClientSize.Width;
            card.SizeChanged += (sender, args) =>
            {
                // Height is this method's own output, so only a width change means
                // the text has to be measured again.
                if (card.ClientSize.Width == laidOutAt) return;
                laidOutAt = card.ClientSize.Width;
                LayoutDevelopmentRows(card);
            };

            return card;
        }

        /// <summary>What part a control plays when the development card is flowed.</summary>
        private enum DevelopmentRowTag
        {
            Toggle,
            Caption,
            Footer
        }

        /// <summary>One debugging switch and the sentence explaining it.</summary>
        private void AddDebugToggle(
            CardPanel card, string text, string caption, bool initial, Action<bool> apply)
        {
            var toggle = new CheckBox
            {
                Text = text,
                AutoSize = true,
                Font = DialogTheme.BodyFont,
                ForeColor = DialogTheme.Text,
                Cursor = Cursors.Hand,
                Checked = initial,
                Tag = DevelopmentRowTag.Toggle
            };
            toggle.CheckedChanged += (sender, args) => apply(toggle.Checked);
            card.Controls.Add(toggle);

            var hint = DialogTheme.CreateCaption(caption, Point.Empty);
            hint.Tag = DevelopmentRowTag.Caption;
            card.Controls.Add(hint);
        }

        /// <summary>
        /// Stack the card's rows top to bottom at the width it currently has, then
        /// size the card to whatever that came to.
        /// </summary>
        private void LayoutDevelopmentRows(CardPanel card)
        {
            int available = card.ClientSize.Width - (CardPadding * 2);
            if (available <= 0) return;

            int y = DevelopmentRowsTop;
            Control footer = null;

            foreach (Control control in card.Controls)
            {
                if (!(control.Tag is DevelopmentRowTag tag)) continue;
                if (tag == DevelopmentRowTag.Footer) { footer = control; continue; }

                bool isCaption = tag == DevelopmentRowTag.Caption;
                int indent = isCaption ? CaptionIndent : 0;
                DialogTheme.WrapAt(control, available - indent);
                control.Location = new Point(CardPadding + indent, y);
                y = control.Bottom + (isCaption ? RowGap : CaptionGap);
            }

            if (footer != null)
            {
                footer.Location = new Point(CardPadding, y + 6);
                y = footer.Bottom;
            }

            card.Height = y + CardPadding;
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
                Directory.CreateDirectory(TalliarkLog.DirectoryPath);
                Process.Start("explorer.exe", $"\"{TalliarkLog.DirectoryPath}\"");
            }
            catch (Exception ex)
            {
                TalliarkLog.Trace($"Could not open log folder: {ex}");
                MessageBox.Show(
                    this,
                    "The Talliark log folder could not be opened.",
                    "Talliark",
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
