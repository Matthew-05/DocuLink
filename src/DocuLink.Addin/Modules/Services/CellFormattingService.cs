using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>Applies visual and number formats to Excel cells that host linked rectangles.</summary>
    internal static class CellFormattingService
    {
        private static readonly int AutoFill = ColorTranslator.ToOle(Color.FromArgb(221, 235, 255));
        private static readonly int RawFill  = ColorTranslator.ToOle(Color.FromArgb(220, 252, 231));
        private static readonly int SumFill  = ColorTranslator.ToOle(Color.FromArgb(254, 243, 199));

        private static readonly Regex _parentheticalNumber =
            new Regex(@"^\(([\d,]+(?:\.\d+)?)\)$", RegexOptions.Compiled);

        private static readonly Regex _plainNumber =
            new Regex(@"^([\d,]+(?:\.\d+)?)$", RegexOptions.Compiled);

        private static readonly Regex _anyParenthetical =
            new Regex(@"\(([\d,]+(?:\.\d+)?)\)", RegexOptions.Compiled);

        private static readonly Regex _anyPlain =
            new Regex(@"\b([\d,]+(?:\.\d+)?)\b", RegexOptions.Compiled);

        /// <summary>Applies the standard link-rectangle cell style.</summary>
        public static void ApplyLinkStyle(Excel.Range cell, LinkType linkType)
        {
            cell.Interior.Color = GetFillColor(linkType);
        }

        /// <summary>Applies Auto-link number formatting when <paramref name="sourceText"/> is numeric.</summary>
        public static void ApplyAutoNumberFormat(Excel.Range cell, string sourceText)
        {
            string numberFormat = BuildAutoNumberFormat(sourceText);
            if (numberFormat != null)
                cell.NumberFormat = numberFormat;
        }

        /// <summary>Applies Sum-link number formatting inferred from all contributing source texts.</summary>
        public static void ApplySumNumberFormat(Excel.Range cell, IEnumerable<string> sourceTexts)
        {
            string numberFormat = BuildSumNumberFormat(sourceTexts);
            if (numberFormat != null)
                cell.NumberFormat = numberFormat;
        }

        /// <summary>Applies Sum-link number formatting inferred from one source text.</summary>
        public static void ApplySumNumberFormat(Excel.Range cell, string sourceText)
        {
            ApplySumNumberFormat(cell, new[] { sourceText });
        }

        /// <summary>Removes the link-rectangle cell background fill.</summary>
        public static void ClearLinkStyle(Excel.Range cell)
        {
            cell.Interior.Pattern = Excel.XlPattern.xlPatternNone;
        }

        /// <summary>
        /// Clears link background fill on all <paramref name="cells"/> in one pass
        /// per worksheet by unioning ranges, so Excel repaints once per sheet.
        /// </summary>
        public static void ClearLinkStyles(IEnumerable<Excel.Range> cells, Excel.Application app)
        {
            if (app == null) return;

            var bySheet = new Dictionary<Excel.Worksheet, List<Excel.Range>>();

            foreach (Excel.Range cell in cells)
            {
                if (cell == null) continue;

                var ws = (Excel.Worksheet)cell.Worksheet;
                if (!bySheet.TryGetValue(ws, out List<Excel.Range> list))
                {
                    list = new List<Excel.Range>();
                    bySheet[ws] = list;
                }

                list.Add(cell);
            }

            foreach (List<Excel.Range> sheetCells in bySheet.Values)
            {
                if (sheetCells.Count == 0) continue;

                Excel.Range batch = sheetCells[0];
                for (int i = 1; i < sheetCells.Count; i++)
                    batch = app.Union(batch, sheetCells[i]);

                batch.Interior.Pattern = Excel.XlPattern.xlPatternNone;
            }
        }

        private static string BuildAutoNumberFormat(string text)
        {
            if (!TryReadAutoNumberFormat(text, out AutoNumberFormatInfo info))
                return null;

            return BuildNumberFormat(info);
        }

        private static int GetFillColor(LinkType linkType)
        {
            switch (linkType)
            {
                case LinkType.Raw: return RawFill;
                case LinkType.Sum: return SumFill;
                default:           return AutoFill;
            }
        }

        private static string BuildSumNumberFormat(IEnumerable<string> sourceTexts)
        {
            if (!TryReadSumNumberFormat(sourceTexts, out AutoNumberFormatInfo info))
                return null;

            return BuildNumberFormat(info);
        }

        private static string BuildNumberFormat(AutoNumberFormatInfo info)
        {
            var sb = new StringBuilder(info.HasThousandsSeparator ? "#,##0" : "0");
            if (info.DecimalPlaces > 0)
            {
                sb.Append(".");
                sb.Append('0', info.DecimalPlaces);
            }

            string positiveFormat = sb.ToString();
            return info.UsesParenthesesForNegative
                ? positiveFormat + ";(" + positiveFormat + ")"
                : positiveFormat;
        }

        private static bool TryReadAutoNumberFormat(string text, out AutoNumberFormatInfo formatInfo)
        {
            formatInfo = null;
            if (string.IsNullOrEmpty(text)) return false;

            string trimmed = NormalizeAutoNumberText(text);
            bool isParenthetical = false;

            Match match = _parentheticalNumber.Match(trimmed);
            if (match.Success)
            {
                isParenthetical = true;
            }
            else
            {
                match = _plainNumber.Match(trimmed);
            }

            if (!match.Success) return false;

            string sourceNumber = match.Groups[1].Value;
            string digits = sourceNumber.Replace(",", "");
            if (!double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return false;

            int decimalIndex = sourceNumber.IndexOf('.');
            formatInfo = new AutoNumberFormatInfo
            {
                HasThousandsSeparator = sourceNumber.IndexOf(',') >= 0,
                DecimalPlaces = decimalIndex >= 0 ? sourceNumber.Length - decimalIndex - 1 : 0,
                UsesParenthesesForNegative = isParenthetical,
            };
            return true;
        }

        private static bool TryReadSumNumberFormat(
            IEnumerable<string> sourceTexts,
            out AutoNumberFormatInfo formatInfo)
        {
            formatInfo = null;
            if (sourceTexts == null) return false;

            var info = new AutoNumberFormatInfo();
            bool found = false;

            foreach (string text in sourceTexts)
            {
                if (string.IsNullOrEmpty(text)) continue;

                foreach (AutoNumberFormatInfo numberInfo in ReadNumberFormats(text))
                {
                    found = true;
                    info.HasThousandsSeparator |= numberInfo.HasThousandsSeparator;
                    info.DecimalPlaces = System.Math.Max(info.DecimalPlaces, numberInfo.DecimalPlaces);
                    info.UsesParenthesesForNegative |= numberInfo.UsesParenthesesForNegative;
                }
            }

            formatInfo = found ? info : null;
            return found;
        }

        private static IEnumerable<AutoNumberFormatInfo> ReadNumberFormats(string text)
        {
            var consumed = new bool[text.Length];

            foreach (Match match in _anyParenthetical.Matches(text))
            {
                if (TryReadNumberFormat(match.Groups[1].Value, true, out AutoNumberFormatInfo info))
                {
                    yield return info;
                    for (int i = match.Index; i < match.Index + match.Length; i++)
                        consumed[i] = true;
                }
            }

            foreach (Match match in _anyPlain.Matches(text))
            {
                if (consumed[match.Index]) continue;

                if (TryReadNumberFormat(match.Groups[1].Value, false, out AutoNumberFormatInfo info))
                    yield return info;
            }
        }

        private static bool TryReadNumberFormat(
            string sourceNumber,
            bool isParenthetical,
            out AutoNumberFormatInfo formatInfo)
        {
            formatInfo = null;

            string digits = sourceNumber.Replace(",", "");
            if (!double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return false;

            int decimalIndex = sourceNumber.IndexOf('.');
            formatInfo = new AutoNumberFormatInfo
            {
                HasThousandsSeparator = sourceNumber.IndexOf(',') >= 0,
                DecimalPlaces = decimalIndex >= 0 ? sourceNumber.Length - decimalIndex - 1 : 0,
                UsesParenthesesForNegative = isParenthetical,
            };
            return true;
        }

        private static string NormalizeAutoNumberText(string text)
        {
            string normalized = Regex.Replace(text.Trim(), @"\s+", "");
            return normalized.StartsWith("$", System.StringComparison.Ordinal)
                ? normalized.Substring(1)
                : normalized;
        }

        private sealed class AutoNumberFormatInfo
        {
            public bool HasThousandsSeparator { get; set; }
            public int DecimalPlaces { get; set; }
            public bool UsesParenthesesForNegative { get; set; }
        }
    }
}
