using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocuLink.Addin;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DocuLink.Addin.Modules.Services;
using DocuLink.Addin.Modules.UI;
using DocuLink.Addin.Properties;
using Excel = Microsoft.Office.Interop.Excel;
using Microsoft.Office.Core;

namespace DocuLink.Addin.Ribbon
{
    [ComVisible(true)]
    public class DocuLinkRibbon : IRibbonExtensibility
    {
        private IRibbonUI _ribbonUi;
        public string GetCustomUI(string ribbonID)
        {
            return LoadRibbonXmlFromResources();
        }

        public void OnShowTaskPane(IRibbonControl control)
        {
            Globals.ThisAddIn.ShowTaskPane();
        }

        public void OnShowViewerWindow(IRibbonControl control)
        {
            Globals.ThisAddIn.ShowViewerWindow();
        }

        public bool GetAutoOpenViewerOnCellClick(IRibbonControl control)
        {
            return Settings.Default.AutoOpenViewerOnCellClick;
        }

        public void OnToggleAutoOpenViewerOnCellClick(IRibbonControl control, bool pressed)
        {
            Settings.Default.AutoOpenViewerOnCellClick = pressed;
            Settings.Default.Save();
            _ribbonUi?.InvalidateControl(control.Id);
        }

        public void OnManageFiles(IRibbonControl control)
        {
            Globals.ThisAddIn.ShowManageFilesWindow();
        }

        public void OnDeleteLinksInSelection(IRibbonControl control)
        {
            var app = Globals.ThisAddIn.Application;
            if (app?.ActiveWorkbook == null)
            {
                MessageBox.Show(
                    text: "Open a workbook before deleting links.",
                    caption: "DocuLink",
                    buttons: MessageBoxButtons.OK,
                    icon: MessageBoxIcon.Information);
                return;
            }

            if (!WorkbookProtectionGuard.TryRequireWritable(app.ActiveWorkbook))
                return;

            var selection = app.Selection as Excel.Range;
            if (selection == null)
            {
                MessageBox.Show(
                    text: "Select one or more cells first.",
                    caption: "DocuLink",
                    buttons: MessageBoxButtons.OK,
                    icon: MessageBoxIcon.Information);
                return;
            }

            IList<string> deletedIds;
            using (Globals.ThisAddIn.EnterSelectionNavSuppress())
            {
                bool prevEnableEvents = app.EnableEvents;
                try
                {
                    app.EnableEvents = false;

                    deletedIds = new DeleteLinkService().DeleteLinksInSelection(
                        selection, app.ActiveWorkbook);

                    if (deletedIds.Count > 0)
                        Globals.ThisAddIn.GetActiveViewerHost()?.SendLinkRectanglesRemoved(deletedIds);
                }
                finally
                {
                    app.EnableEvents = prevEnableEvents;
                }
            }

            if (deletedIds.Count == 0)
            {
                MessageBox.Show(
                    text: "No linked cells in the selected range.",
                    caption: "DocuLink",
                    buttons: MessageBoxButtons.OK,
                    icon: MessageBoxIcon.Information);
            }
        }

        public void OnAddPdfDocuments(IRibbonControl control)
        {
            var app = Globals.ThisAddIn.Application;
            if (app.ActiveWorkbook == null)
            {
                MessageBox.Show(
                    text: "Open or create a workbook before adding PDFs.",
                    caption: "DocuLink",
                    buttons: MessageBoxButtons.OK,
                    icon: MessageBoxIcon.Information);
                return;
            }

            if (!WorkbookProtectionGuard.TryRequireWritable(app.ActiveWorkbook))
                return;

            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Add PDFs to workbook";
                dialog.Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;

                if (dialog.ShowDialog() != DialogResult.OK || dialog.FileNames == null || dialog.FileNames.Length == 0)
                    return;

                var requests = dialog.FileNames
                    .Select(path => new PdfPathImportRequest(path))
                    .ToList();

                PdfImportResult result;
                using (var progress = ThreadedProgressController.Show("Importing documents..."))
                {
                    result = new PdfImportService().ImportFilePaths(app.ActiveWorkbook, requests, progress);

                    if (result.AddedIds.Count > 0)
                    {
                        progress.Report(
                            "Refreshing DocuLink",
                            "Updating viewer data...",
                            result.AddedIds.Count,
                            result.AddedIds.Count);

                        foreach (string id in result.AddedIds)
                            Globals.ThisAddIn.NotifyViewerPdfAdded(id);
                    }
                }

                if (result.Errors.Count > 0)
                {
                    var message = new StringBuilder();
                    if (result.AddedIds.Count > 0)
                        message.AppendLine($"Added {result.AddedIds.Count} PDF(s).").AppendLine();
                    message.AppendLine("Some files could not be added:");
                    message.AppendLine(string.Join(Environment.NewLine, result.Errors));
                    MessageBox.Show(message.ToString(), "DocuLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private static string LoadRibbonXmlFromResources()
        {
            Assembly assembly = typeof(DocuLinkRibbon).Assembly;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith("DocuLinkRibbon.xml", StringComparison.Ordinal))
                {
                    using (var stream = assembly.GetManifestResourceStream(name))
                    {
                        if (stream == null)
                            break;
                        using (var reader = new System.IO.StreamReader(stream))
                            return reader.ReadToEnd();
                    }
                }
            }

            throw new InvalidOperationException("Embedded resource DocuLinkRibbon.xml was not found.");
        }

        /// <summary>Called when the Ribbon extensibility loads; retained for optional IRibbonUI caching.</summary>
        public void Ribbon_Load(IRibbonUI ribbonUi)
        {
            _ribbonUi = ribbonUi;
        }

        public System.Drawing.Bitmap GetViewerMenuImage(IRibbonControl control)
        {
            return LoadEmbeddedSvgAsIcon("icon-viewer.svg");
        }

        public System.Drawing.Bitmap GetAddPdfImage(IRibbonControl control)
        {
            return LoadEmbeddedSvgAsIcon("icon-add-document.svg");
        }

        public System.Drawing.Bitmap GetDeleteLinksImage(IRibbonControl control)
        {
            return LoadEmbeddedSvgAsIcon("icon-delete-links.svg");
        }

        public System.Drawing.Bitmap GetManageFilesImage(IRibbonControl control)
        {
            return LoadEmbeddedSvgAsIcon("icon-manage-files.svg");
        }

        private static System.Drawing.Bitmap LoadEmbeddedSvgAsIcon(string iconName)
        {
            Assembly assembly = typeof(DocuLinkRibbon).Assembly;

            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(iconName, StringComparison.OrdinalIgnoreCase));

            if (resourceName != null)
            {
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            string svgText = reader.ReadToEnd();
                            return RenderSvgAsIcon(svgText);
                        }
                    }
                }
            }

            return CreatePlaceholderIcon();
        }

        private static System.Drawing.Bitmap RenderSvgAsIcon(string svgText)
        {
            var doc = Svg.SvgDocument.FromSvg<Svg.SvgDocument>(svgText);
            return doc.Draw(32, 32);
        }

        private static System.Drawing.Bitmap CreatePlaceholderIcon()
        {
            var bitmap = new System.Drawing.Bitmap(32, 32);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.LightGray, 1))
                    graphics.DrawRectangle(pen, 2, 2, 28, 28);
            }
            return bitmap;
        }
    }
}
