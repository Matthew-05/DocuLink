using System;
using Excel = Microsoft.Office.Interop.Excel;
using Office = Microsoft.Office.Core;
using DocuLink.Addin.Ribbon;
using DocuLink.Addin.Modules.WebView;
using Microsoft.Office.Tools.Excel;

namespace DocuLink.Addin
{
    public partial class ThisAddIn
    {
        private Microsoft.Office.Tools.CustomTaskPane _taskPane;
        private TaskPaneHost _taskPaneHost;

        internal void ShowTaskPane()
        {
            EnsureTaskPaneCreated();
            _taskPane.Visible = true;
        }

        /// <summary>
        /// Pushes the current workbook's PDF list to the task pane viewer.
        /// No-ops if the task pane has not been created yet.
        /// </summary>
        internal void RefreshTaskPanePdfs()
        {
            _taskPaneHost?.SendPdfsToWebView();
        }

        private void EnsureTaskPaneCreated()
        {
            if (_taskPane != null)
                return;

            _taskPaneHost = new TaskPaneHost();
            _taskPane = CustomTaskPanes.Add(_taskPaneHost, "DocuLink");
            _taskPane.DockPosition = Office.MsoCTPDockPosition.msoCTPDockPositionRight;
            _taskPane.Width = 420;
        }

        private void ThisAddIn_Startup(object sender, System.EventArgs e)
        {
        }

        private void ThisAddIn_Shutdown(object sender, System.EventArgs e)
        {
        }

        protected override Office.IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            return new DocuLinkRibbon();
        }

        #region VSTO generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InternalStartup()
        {
            this.Startup += new System.EventHandler(ThisAddIn_Startup);
            this.Shutdown += new System.EventHandler(ThisAddIn_Shutdown);
        }
        
        #endregion
    }
}
