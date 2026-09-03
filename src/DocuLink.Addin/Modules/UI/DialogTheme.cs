using System.Drawing;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.UI
{
    /// <summary>
    /// Shared palette, fonts and control styling for DocuLink's WinForms dialogs.
    /// The values mirror the release-notes stylesheet so native chrome and the
    /// rendered HTML read as one surface.
    /// </summary>
    internal static class DialogTheme
    {
        internal static readonly Color Canvas      = Color.FromArgb(0xF6, 0xF8, 0xFA);
        internal static readonly Color Surface     = Color.White;
        internal static readonly Color Border      = Color.FromArgb(0xD8, 0xDE, 0xE4);
        internal static readonly Color Text        = Color.FromArgb(0x1F, 0x23, 0x28);
        internal static readonly Color MutedText   = Color.FromArgb(0x65, 0x6D, 0x76);
        internal static readonly Color Accent      = Color.FromArgb(0x09, 0x69, 0xDA);
        internal static readonly Color AccentHover = Color.FromArgb(0x08, 0x5A, 0xBE);
        internal static readonly Color AccentDown  = Color.FromArgb(0x06, 0x4D, 0xA3);
        internal static readonly Color HoverFill   = Color.FromArgb(0xEE, 0xF2, 0xF6);
        internal static readonly Color PressedFill = Color.FromArgb(0xE3, 0xE9, 0xEF);

        /// <summary>Corner radius of the section cards, matching the HTML surfaces.</summary>
        internal const int CornerRadius = 6;

        internal static readonly Font TitleFont        = new Font("Segoe UI", 14f, FontStyle.Bold);
        internal static readonly Font SectionTitleFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        internal static readonly Font SubHeadingFont   = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        internal static readonly Font BodyFont         = new Font("Segoe UI", 9f);
        internal static readonly Font CaptionFont      = new Font("Segoe UI", 8.25f);

        /// <summary>Dialog title shown inside the window, above the first section.</summary>
        internal static Label CreateTitle(string text, Point location)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Location = location,
                Font = TitleFont,
                ForeColor = Text
            };
        }

        /// <summary>Secondary line under a title, or a hint under a control.</summary>
        internal static Label CreateCaption(string text, Point location)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Location = location,
                Font = CaptionFont,
                ForeColor = MutedText
            };
        }

        /// <summary>Heading for a card, e.g. "Updates".</summary>
        internal static Label CreateSectionTitle(string text, Point location)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Location = location,
                Font = SectionTitleFont,
                ForeColor = Text
            };
        }

        /// <summary>Small muted label that groups related controls inside a card.</summary>
        internal static Label CreateSubHeading(string text, Point location)
        {
            return new Label
            {
                Text = (text ?? string.Empty).ToUpperInvariant(),
                AutoSize = true,
                Location = location,
                Font = SubHeadingFont,
                ForeColor = MutedText
            };
        }

        /// <summary>Hairline used to separate regions inside a card.</summary>
        internal static Panel CreateSeparator(Point location, int width)
        {
            return new Panel
            {
                Location = location,
                Size = new Size(width, 1),
                BackColor = Border,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
        }

        /// <summary>Filled accent button for a section's main action.</summary>
        internal static void StylePrimaryButton(Button button)
        {
            button.Font = BodyFont;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentDown;
        }

        /// <summary>Outlined button for secondary actions such as Close.</summary>
        internal static void StyleSecondaryButton(Button button)
        {
            button.Font = BodyFont;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Surface;
            button.ForeColor = Text;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = HoverFill;
            button.FlatAppearance.MouseDownBackColor = PressedFill;
        }
    }
}
