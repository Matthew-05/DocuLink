using System;
using System.Runtime.InteropServices;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Resolves the Excel cell bound to a persisted <see cref="LinkedRectangle"/>.
    /// </summary>
    internal static class LinkCellResolver
    {
        internal static Excel.Range TryResolveCell(Excel.Workbook workbook, LinkedRectangle rect)
        {
            // PRIMARY: Query XmlMap binding (always current through cell moves and worksheet renames)
            Excel.Range cell = TryResolveCellViaXmlMap(workbook, rect.LinkedCell.TrackIndex);
            if (cell != null)
                return cell;

            // FALLBACK: Use stored sheet name + address (for backward compatibility with old workbooks)
            try
            {
                Excel.Worksheet ws = FindWorksheet(workbook, rect.LinkedCell.SheetName);
                if (ws != null)
                {
                    cell = ws.Range[rect.LinkedCell.Address] as Excel.Range;
                    if (cell != null)
                        return cell;
                }
            }
            catch (COMException) { }

            return null;
        }

        internal static Excel.Range TryResolveCellViaXmlMap(Excel.Workbook workbook, int trackIndex)
        {
            if (trackIndex <= 0)
                return null;

            string mapName = "DocuLink_" + trackIndex;
            Excel.XmlMap map = null;

            foreach (Excel.XmlMap candidate in workbook.XmlMaps)
            {
                try
                {
                    if (string.Equals(candidate.Name, mapName, StringComparison.Ordinal))
                    {
                        map = candidate;
                        break;
                    }
                }
                catch (COMException) { }
            }

            if (map == null)
                return null;

            const string linkXPath = "/DocuLinkLink";
            foreach (Excel.Worksheet ws in workbook.Worksheets)
            {
                try
                {
                    object result = ws.XmlDataQuery(linkXPath, Type.Missing, map);
                    if (result is Excel.Range range)
                        return range;
                }
                catch (COMException) { }
            }

            return null;
        }

        private static Excel.Worksheet FindWorksheet(Excel.Workbook workbook, string sheetName)
        {
            foreach (Excel.Worksheet ws in workbook.Worksheets)
            {
                if (string.Equals(ws.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                    return ws;
            }
            return null;
        }
    }
}
