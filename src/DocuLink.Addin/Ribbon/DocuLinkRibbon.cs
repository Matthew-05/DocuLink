using System;
using System.Collections.Generic;
using System.IO;
using DocuLink.Addin;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DocuLink.Addin.Modules.Services;
using Microsoft.Office.Core;

namespace DocuLink.Addin.Ribbon
{
    [ComVisible(true)]
    public class DocuLinkRibbon : IRibbonExtensibility
    {
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

        public void OnManageFiles(IRibbonControl control)
        {
            Globals.ThisAddIn.ShowManageFilesWindow();
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

            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Add PDFs to workbook";
                dialog.Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;

                if (dialog.ShowDialog() != DialogResult.OK || dialog.FileNames == null || dialog.FileNames.Length == 0)
                    return;

                var service = new AddPdfDocumentService();
                int added = 0;
                var errors = new List<string>();

                foreach (string path in dialog.FileNames)
                {
                    try
                    {
                        service.AddEmbeddedPdf(app.ActiveWorkbook, path);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }

                if (added > 0)
                    Globals.ThisAddIn.RefreshTaskPanePdfs();

                if (errors.Count > 0)
                {
                    var message = new StringBuilder();
                    if (added > 0)
                        message.AppendLine($"Added {added} PDF(s).").AppendLine();
                    message.AppendLine("Some files could not be added:");
                    message.AppendLine(string.Join(Environment.NewLine, errors));
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
        }
    }
}
