using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
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

        internal void ShowTaskPane()
        {
            EnsureTaskPaneCreated();
            _taskPane.Visible = true;
        }

        private void EnsureTaskPaneCreated()
        {
            if (_taskPane != null)
                return;

            var host = new TaskPaneHost();
            _taskPane = CustomTaskPanes.Add(host, "DocuLink");
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
