using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.UI
{
    /// <summary>One extension in the import selection, with how many files carry it.</summary>
    internal sealed class ImportFormatSummary
    {
        public ImportFormatSummary(string extension, string description, int count)
        {
            Extension = extension;
            Description = description;
            Count = count;
        }

        /// <summary>Lower-case extension including the dot, e.g. ".docx".</summary>
        public string Extension { get; }

        /// <summary>Human-readable format name, e.g. "Word document".</summary>
        public string Description { get; }

        public int Count { get; }
    }

    /// <summary>
    /// Shown before anything is converted or imported, whenever a selection contains
    /// files that are not already PDFs.
    ///
    /// It answers three questions at a glance: that non-PDF files were selected, which
    /// of them DocuLink can convert (checkable, one row per extension with a count),
    /// and which it cannot (listed but deliberately not selectable, so the user cannot
    /// ask for something that will fail).
    /// </summary>
    internal sealed class NonPdfImportDialog : Form
    {
        private readonly ListView _convertibleList;
        private readonly Button _importButton;
        private readonly int _pdfCount;

        private NonPdfImportDialog(
            int pdfCount,
            IList<ImportFormatSummary> convertible,
            IList<ImportFormatSummary> unsupported)
        {
            _pdfCount = pdfCount;

            Text = "DocuLink — Convert documents";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 440);
            MinimumSize = new Size(460, 380);
            Padding = new Padding(12);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                 // intro
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                 // convertible header
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));             // convertible list
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));             // unsupported group
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));                 // buttons

            layout.Controls.Add(BuildIntroLabel(pdfCount, convertible, unsupported), 0, 0);
            layout.Controls.Add(BuildConvertibleHeader(convertible.Count > 0), 0, 1);

            _convertibleList = BuildConvertibleList(convertible);
            layout.Controls.Add(_convertibleList, 0, 2);

            layout.Controls.Add(BuildUnsupportedGroup(unsupported), 0, 3);

            _importButton = new Button
            {
                Text = "Import",
                DialogResult = DialogResult.OK,
                AutoSize = true,
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(0, 10, 0, 0),
            };
            buttonPanel.Controls.Add(_importButton);
            buttonPanel.Controls.Add(cancelButton);
            layout.Controls.Add(buttonPanel, 0, 4);

            Controls.Add(layout);

            AcceptButton = _importButton;
            CancelButton = cancelButton;

            _convertibleList.ItemChecked += (_, __) => UpdateImportEnabled();
            UpdateImportEnabled();
        }

        /// <summary>
        /// Shows the dialog modally.
        /// </summary>
        /// <param name="owner">Dialog owner; may be null.</param>
        /// <param name="pdfCount">How many already-PDF files are in the selection.</param>
        /// <param name="convertible">Convertible extensions with counts, in display order.</param>
        /// <param name="unsupported">Unsupported extensions with counts, shown read-only.</param>
        /// <param name="selectedExtensions">
        /// Extensions the user chose to convert. Empty when they chose none — the PDFs
        /// in the selection are still imported.
        /// </param>
        /// <returns>False when the user cancelled; nothing should be imported.</returns>
        internal static bool TryConfirm(
            IWin32Window owner,
            int pdfCount,
            IList<ImportFormatSummary> convertible,
            IList<ImportFormatSummary> unsupported,
            out ISet<string> selectedExtensions)
        {
            using (var dialog = new NonPdfImportDialog(
                pdfCount,
                convertible ?? new List<ImportFormatSummary>(),
                unsupported ?? new List<ImportFormatSummary>()))
            {
                DialogResult result = owner == null
                    ? dialog.ShowDialog()
                    : dialog.ShowDialog(owner);

                if (result != DialogResult.OK)
                {
                    selectedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    return false;
                }

                selectedExtensions = dialog.GetSelectedExtensions();
                return true;
            }
        }

        private ISet<string> GetSelectedExtensions()
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ListViewItem item in _convertibleList.CheckedItems)
            {
                if (item.Tag is ImportFormatSummary summary)
                    selected.Add(summary.Extension);
            }

            return selected;
        }

        private static Label BuildIntroLabel(
            int pdfCount,
            IList<ImportFormatSummary> convertible,
            IList<ImportFormatSummary> unsupported)
        {
            int convertibleCount = convertible.Sum(f => f.Count);
            int unsupportedCount = unsupported.Sum(f => f.Count);
            int nonPdfCount = convertibleCount + unsupportedCount;

            string text =
                Pluralise(
                    nonPdfCount,
                    "selected document is not a PDF.",
                    "selected documents are not PDFs.") +
                " DocuLink converts supported documents to PDF for workbook storage.";

            if (pdfCount > 0)
                text += $" {Pluralise(pdfCount, "PDF was", "PDFs were")} also selected and will be imported as-is.";

            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                AutoSize = true,
                MaximumSize = new Size(480, 0),
                Padding = new Padding(0, 0, 0, 10),
            };
        }

        private static Label BuildConvertibleHeader(bool anyConvertible)
        {
            return new Label
            {
                Text = anyConvertible
                    ? "Choose which types to convert and import:"
                    : "None of the selected file types can be converted.",
                Dock = DockStyle.Fill,
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                Padding = new Padding(0, 0, 0, 4),
            };
        }

        private static ListView BuildConvertibleList(IList<ImportFormatSummary> convertible)
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
                HideSelection = true,
            };

            list.Columns.Add("Type", 90);
            list.Columns.Add("Format", 250);
            list.Columns.Add("Files", 60, HorizontalAlignment.Right);

            foreach (ImportFormatSummary summary in convertible)
            {
                var item = new ListViewItem(summary.Extension)
                {
                    Checked = true,   // Convertible types are opted in by default
                    Tag = summary,
                };
                item.SubItems.Add(summary.Description);
                item.SubItems.Add(summary.Count.ToString());
                list.Items.Add(item);
            }

            return list;
        }

        private static Control BuildUnsupportedGroup(IList<ImportFormatSummary> unsupported)
        {
            var group = new GroupBox
            {
                Text = unsupported.Count > 0
                    ? $"Cannot be converted — {Pluralise(unsupported.Sum(f => f.Count), "file", "files")} will be skipped"
                    : "Cannot be converted",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                Margin = new Padding(0, 10, 0, 0),
            };

            if (unsupported.Count == 0)
            {
                group.Controls.Add(new Label
                {
                    Text = "Every selected file type can be converted.",
                    Dock = DockStyle.Fill,
                    ForeColor = SystemColors.GrayText,
                });
                return group;
            }

            // Read-only by design: unsupported types have no conversion path, so
            // offering a checkbox would only let the user opt into a guaranteed error.
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = false,
                FullRowSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
                HideSelection = true,
                ForeColor = SystemColors.GrayText,
            };

            list.Columns.Add("Type", 90);
            list.Columns.Add("Files", 60, HorizontalAlignment.Right);
            list.Columns.Add("", 250);

            foreach (ImportFormatSummary summary in unsupported)
            {
                var item = new ListViewItem(string.IsNullOrEmpty(summary.Extension)
                    ? "(no extension)"
                    : summary.Extension);
                item.SubItems.Add(summary.Count.ToString());
                item.SubItems.Add(summary.Description ?? string.Empty);
                list.Items.Add(item);
            }

            group.Controls.Add(list);
            return group;
        }

        private void UpdateImportEnabled()
        {
            // Importing with nothing checked is still valid when the selection also
            // contains PDFs — those go in untouched.
            _importButton.Enabled = _pdfCount > 0 || _convertibleList.CheckedItems.Count > 0;
        }

        private static string Pluralise(int count, string singular, string plural)
        {
            return count == 1 ? $"{count} {singular}" : $"{count} {plural}";
        }
    }
}
