using System.Drawing;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.UI
{
    /// <summary>
    /// Modal dialog that collects manual link text when PDF text extraction
    /// returns nothing for a drawn rectangle.
    /// </summary>
    internal sealed class LinkTextPromptDialog : Form
    {
        private readonly TextBox _textBox;
        private readonly Button _okButton;

        private LinkTextPromptDialog()
        {
            Text            = "DocuLink";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = false;
            ClientSize      = new Size(360, 120);
            Padding         = new Padding(12);

            var label = new Label
            {
                Text     = "Enter text content",
                AutoSize = true,
                Dock     = DockStyle.Top,
                Padding  = new Padding(0, 0, 0, 8),
            };

            _textBox = new TextBox
            {
                Dock = DockStyle.Top,
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize      = true,
                Padding       = new Padding(0, 12, 0, 0),
            };

            var cancelButton = new Button
            {
                Text   = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
            };

            _okButton = new Button
            {
                Text         = "OK",
                DialogResult = DialogResult.OK,
                AutoSize     = true,
                Enabled      = false,
            };

            buttonPanel.Controls.Add(cancelButton);
            buttonPanel.Controls.Add(_okButton);

            Controls.Add(buttonPanel);
            Controls.Add(_textBox);
            Controls.Add(label);

            AcceptButton = _okButton;
            CancelButton = cancelButton;

            _textBox.TextChanged += (_, __) => UpdateOkEnabled();
            Shown += (_, __) =>
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };
        }

        /// <summary>
        /// Shows the prompt modally. Returns <c>false</c> when the user cancels.
        /// </summary>
        internal static bool TryPrompt(IWin32Window owner, out string text)
        {
            using (var dialog = new LinkTextPromptDialog())
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK)
                {
                    text = null;
                    return false;
                }

                text = dialog._textBox.Text.Trim();
                return true;
            }
        }

        private void UpdateOkEnabled()
        {
            _okButton.Enabled = !string.IsNullOrWhiteSpace(_textBox.Text);
        }
    }
}
