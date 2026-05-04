using System;
using System.Reflection;
using System.Runtime.InteropServices;
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

        public void OnHelloWorld(IRibbonControl control)
        {
            System.Windows.Forms.MessageBox.Show(
                text: "Hello, world!",
                caption: "DocuLink",
                buttons: System.Windows.Forms.MessageBoxButtons.OK,
                icon: System.Windows.Forms.MessageBoxIcon.Information);
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
