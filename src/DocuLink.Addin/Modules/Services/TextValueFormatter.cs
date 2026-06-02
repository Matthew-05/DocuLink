using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocuLink.Addin.Modules.Services
{
    /// <summary>
    /// Converts extracted PDF text to cell values according to the selected link type.
    /// </summary>
    internal static class TextValueFormatter
    {
        // Matches an entire string that is a parenthetical number: (1,234.56)
        private static readonly Regex _parentheticalNumber =
            new Regex(@"^\(([\d,]+(?:\.\d+)?)\)$", RegexOptions.Compiled);

        // Matches an entire string that is a plain positive number: 1,234.56
        private static readonly Regex _plainNumber =
            new Regex(@"^([\d,]+(?:\.\d+)?)$", RegexOptions.Compiled);

        // Finds all numbers inside a string (parenthetical or plain) for Sum parsing
        private static readonly Regex _anyParenthetical =
            new Regex(@"\(([\d,]+(?:\.\d+)?)\)", RegexOptions.Compiled);

        private static readonly Regex _anyPlain =
            new Regex(@"\b([\d,]+(?:\.\d+)?)\b", RegexOptions.Compiled);

        /// <summary>
        /// Auto format: converts parenthetical numbers to negatives, strips numeric thousand-separator
        /// commas. Returns a <see cref="double"/> when the entire trimmed text is a number;
        /// otherwise returns the original string.
        /// </summary>
        public static object FormatAuto(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            string trimmed = text.Trim();

            Match parenMatch = _parentheticalNumber.Match(trimmed);
            if (parenMatch.Success)
            {
                string digits = parenMatch.Groups[1].Value.Replace(",", "");
                if (double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out double negVal))
                    return -negVal;
            }

            Match plainMatch = _plainNumber.Match(trimmed);
            if (plainMatch.Success)
            {
                string digits = plainMatch.Groups[1].Value.Replace(",", "");
                if (double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out double posVal))
                    return posVal;
            }

            return text;
        }

        /// <summary>
        /// Builds an Excel formula string that sums all numbers found in <paramref name="text"/>.
        /// Returns <c>null</c> if no numbers are found.
        /// Always produces a formula (e.g. <c>=1234</c> for a single number).
        /// </summary>
        public static string BuildSumFormula(string text)
        {
            var numbers = ExtractNumbers(text);
            if (numbers.Count == 0) return null;
            return BuildFormulaFromNumbers(numbers);
        }

        /// <summary>
        /// Rebuilds a sum formula from multiple source texts (one per contributing rectangle).
        /// Numbers from each text are concatenated in order.
        /// Returns <c>null</c> if no numbers are found across all texts.
        /// </summary>
        public static string RebuildSumFormula(IEnumerable<string> sourceTexts)
        {
            var all = new List<double>();
            foreach (string src in sourceTexts)
            {
                if (!string.IsNullOrEmpty(src))
                    all.AddRange(ExtractNumbers(src));
            }
            if (all.Count == 0) return null;
            return BuildFormulaFromNumbers(all);
        }

        private static List<double> ExtractNumbers(string text)
        {
            var result = new List<double>();
            if (string.IsNullOrEmpty(text)) return result;

            // Work on a copy; mark positions consumed by parenthetical matches to avoid double-counting
            var consumed = new bool[text.Length];

            foreach (Match m in _anyParenthetical.Matches(text))
            {
                string digits = m.Groups[1].Value.Replace(",", "");
                if (double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                {
                    result.Add(-val);
                    for (int i = m.Index; i < m.Index + m.Length; i++)
                        consumed[i] = true;
                }
            }

            // Find plain numbers not inside already-consumed ranges
            foreach (Match m in _anyPlain.Matches(text))
            {
                if (consumed[m.Index]) continue;

                string digits = m.Groups[1].Value.Replace(",", "");
                if (double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                    result.Add(val);
            }

            return result;
        }

        private static string BuildFormulaFromNumbers(List<double> numbers)
        {
            var sb = new StringBuilder("=");
            for (int i = 0; i < numbers.Count; i++)
            {
                if (i > 0) sb.Append("+");
                // Format without trailing zeros but preserves decimals
                sb.Append(numbers[i].ToString("G", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
