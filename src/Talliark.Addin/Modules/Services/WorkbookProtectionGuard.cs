using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Excel = Microsoft.Office.Interop.Excel;

namespace Talliark.Addin.Modules.Services
{
    internal static class WorkbookProtectionGuard
    {
        internal const string ProtectedStructureMessage =
            "This workbook's structure is protected. Unprotect workbook structure before changing Talliark data.";

        internal static bool IsStructureProtected(Excel.Workbook workbook)
        {
            if (workbook == null) return false;

            try
            {
                return workbook.ProtectStructure;
            }
            catch (COMException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Talliark] Failed to read Workbook.ProtectStructure: {ex.Message}");
                return false;
            }
        }

        internal static void ThrowIfStructureProtected(Excel.Workbook workbook)
        {
            if (IsStructureProtected(workbook))
                throw new InvalidOperationException(ProtectedStructureMessage);
        }

        internal static bool TryRequireWritable(Excel.Workbook workbook, IWin32Window owner = null)
        {
            if (!IsStructureProtected(workbook))
                return true;

            ShowProtectedStructureMessage(owner);
            return false;
        }

        internal static void ShowProtectedStructureMessage(IWin32Window owner = null)
        {
            if (owner == null)
            {
                MessageBox.Show(
                    ProtectedStructureMessage,
                    "Talliark",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            MessageBox.Show(
                owner,
                ProtectedStructureMessage,
                "Talliark",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }
}
