using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DocuLink.Addin.Modules.Services
{
    internal enum AutoDateKind
    {
        FullDate,
        MonthYear,
        QuarterYear,
    }

    internal sealed class AutoDateParseResult
    {
        public AutoDateParseResult(DateTime value, AutoDateKind kind)
        {
            Value = value;
            Kind = kind;
        }

        public DateTime Value { get; }
        public AutoDateKind Kind { get; }
    }

    internal interface IAutoDateFormatPolicy
    {
        string GetNumberFormat(AutoDateParseResult result);
    }

    internal sealed class DefaultAutoDateFormatPolicy : IAutoDateFormatPolicy
    {
        public string GetNumberFormat(AutoDateParseResult result)
        {
            if (result == null) return null;

            switch (result.Kind)
            {
                case AutoDateKind.MonthYear:
                    return "mmmm yyyy";
                case AutoDateKind.QuarterYear:
                    return "\"Q\"q yyyy";
                default:
                    return "m/d/yyyy";
            }
        }
    }

    internal static class AutoDateParser
    {
        private static readonly IFormatProvider Invariant = CultureInfo.InvariantCulture;

        private static readonly Regex QuarterPattern =
            new Regex(@"^(?:Q([1-4])\s*[-/]?\s*(\d{4})|(\d{4})\s*[-/]?\s*Q([1-4]))$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex YearMonthDayPattern =
            new Regex(@"^(\d{4})[-/](\d{1,2})[-/](\d{1,2})$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex CompactYearMonthDayPattern =
            new Regex(@"^(\d{4})(\d{2})(\d{2})$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UsNumericDatePattern =
            new Regex(@"^(\d{1,2})[/-](\d{1,2})[/-](\d{2}|\d{4})$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex MonthYearPattern =
            new Regex(@"^([A-Za-z]+\.?)\s+(\d{4})$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex MonthDayYearPattern =
            new Regex(@"^([A-Za-z]+\.?)\s+(\d{1,2}),?\s+(\d{4})$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DayMonthYearPattern =
            new Regex(@"^(\d{1,2})\s+([A-Za-z]+\.?),?\s+(\d{4})$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, int> Months =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "jan", 1 }, { "january", 1 },
                { "feb", 2 }, { "february", 2 },
                { "mar", 3 }, { "march", 3 },
                { "apr", 4 }, { "april", 4 },
                { "may", 5 },
                { "jun", 6 }, { "june", 6 },
                { "jul", 7 }, { "july", 7 },
                { "aug", 8 }, { "august", 8 },
                { "sep", 9 }, { "sept", 9 }, { "september", 9 },
                { "oct", 10 }, { "october", 10 },
                { "nov", 11 }, { "november", 11 },
                { "dec", 12 }, { "december", 12 },
            };

        public static bool TryParse(string text, out AutoDateParseResult result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string normalized = NormalizeDateText(text);

            if (TryParseQuarter(normalized, out result)) return true;
            if (TryParseYearMonthDay(normalized, out result)) return true;
            if (TryParseUsNumericDate(normalized, out result)) return true;
            if (TryParseTextDate(normalized, out result)) return true;
            if (TryParseMonthYear(normalized, out result)) return true;

            return false;
        }

        private static bool TryParseQuarter(string text, out AutoDateParseResult result)
        {
            result = null;
            Match match = QuarterPattern.Match(text);
            if (!match.Success) return false;

            int quarter = match.Groups[1].Success
                ? int.Parse(match.Groups[1].Value, Invariant)
                : int.Parse(match.Groups[4].Value, Invariant);
            int year = match.Groups[2].Success
                ? int.Parse(match.Groups[2].Value, Invariant)
                : int.Parse(match.Groups[3].Value, Invariant);

            int month = ((quarter - 1) * 3) + 1;
            return TryCreateDate(year, month, 1, AutoDateKind.QuarterYear, out result);
        }

        private static bool TryParseYearMonthDay(string text, out AutoDateParseResult result)
        {
            result = null;

            Match match = YearMonthDayPattern.Match(text);
            if (!match.Success)
                match = CompactYearMonthDayPattern.Match(text);

            if (!match.Success) return false;

            int year = int.Parse(match.Groups[1].Value, Invariant);
            int month = int.Parse(match.Groups[2].Value, Invariant);
            int day = int.Parse(match.Groups[3].Value, Invariant);
            return TryCreateDate(year, month, day, AutoDateKind.FullDate, out result);
        }

        private static bool TryParseUsNumericDate(string text, out AutoDateParseResult result)
        {
            result = null;
            Match match = UsNumericDatePattern.Match(text);
            if (!match.Success) return false;

            int month = int.Parse(match.Groups[1].Value, Invariant);
            int day = int.Parse(match.Groups[2].Value, Invariant);
            int year = ParseYear(match.Groups[3].Value);
            return TryCreateDate(year, month, day, AutoDateKind.FullDate, out result);
        }

        private static bool TryParseTextDate(string text, out AutoDateParseResult result)
        {
            result = null;

            Match match = MonthDayYearPattern.Match(text);
            if (match.Success
                && TryParseMonthName(match.Groups[1].Value, out int month))
            {
                int day = int.Parse(match.Groups[2].Value, Invariant);
                int year = int.Parse(match.Groups[3].Value, Invariant);
                return TryCreateDate(year, month, day, AutoDateKind.FullDate, out result);
            }

            match = DayMonthYearPattern.Match(text);
            if (match.Success
                && TryParseMonthName(match.Groups[2].Value, out month))
            {
                int day = int.Parse(match.Groups[1].Value, Invariant);
                int year = int.Parse(match.Groups[3].Value, Invariant);
                return TryCreateDate(year, month, day, AutoDateKind.FullDate, out result);
            }

            return false;
        }

        private static bool TryParseMonthYear(string text, out AutoDateParseResult result)
        {
            result = null;
            Match match = MonthYearPattern.Match(text);
            if (!match.Success
                || !TryParseMonthName(match.Groups[1].Value, out int month))
            {
                return false;
            }

            int year = int.Parse(match.Groups[2].Value, Invariant);
            return TryCreateDate(year, month, 1, AutoDateKind.MonthYear, out result);
        }

        private static bool TryCreateDate(
            int year,
            int month,
            int day,
            AutoDateKind kind,
            out AutoDateParseResult result)
        {
            result = null;
            try
            {
                result = new AutoDateParseResult(new DateTime(year, month, day), kind);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static bool TryParseMonthName(string value, out int month)
        {
            string key = value.Trim().TrimEnd('.');
            return Months.TryGetValue(key, out month);
        }

        private static int ParseYear(string value)
        {
            int year = int.Parse(value, Invariant);
            return value.Length == 2
                ? CultureInfo.InvariantCulture.Calendar.ToFourDigitYear(year)
                : year;
        }

        private static string NormalizeDateText(string text)
        {
            string normalized = Regex.Replace(text.Trim(), @"\r\n|\r|\n", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return Regex.Replace(normalized, @"(?<=\d)(st|nd|rd|th)\b", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
