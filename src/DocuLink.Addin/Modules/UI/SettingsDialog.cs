using System.Drawing;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.UI
{
    internal sealed class SettingsDialog : Form
    {
        internal SettingsDialog()
        {
            Text = "DocuLink Settings";
            ClientSize = new Size(320, 130);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            Controls.Add(new Label
            {
                Text = $"Version: {AppVersion.Current}",
                AutoSize = true,
                Location = new Point(20, 30),
                Font = new Font("Segoe UI", 10f)
            });

            var closeBtn = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.Cancel,
                Size = new Size(80, 28),
                Location = new Point(220, 82)
            };
            Controls.Add(closeBtn);
            CancelButton = closeBtn;
        }
    }
}
