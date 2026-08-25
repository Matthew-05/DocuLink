import type { CharacterEntry } from "./char-entries.js";
import type { NormalizedRect, SearchMatch } from "./types.js";

export interface SearchPageIndex {
  pageText: string;
  normalizedPageText: string;
  indexMap: number[];
  dateSpans: NormalizedDateSpan[];
}

export interface SearchPageOptions {
  limit?: number;
  normalizeDates?: boolean;
}

interface NormalizedDateSpan {
  normalizedStart: number;
  normalizedEnd: number;
  originalStart: number;
  originalEnd: number;
}

interface DateToken {
  end: number;
  normalized: string;
}

const MonthNumbers: Readonly<Record<string, number>> = {
  jan: 1,
  january: 1,
  feb: 2,
  february: 2,
  mar: 3,
  march: 3,
  apr: 4,
  april: 4,
  may: 5,
  jun: 6,
  june: 6,
  jul: 7,
  july: 7,
  aug: 8,
  august: 8,
  sep: 9,
  sept: 9,
  september: 9,
  oct: 10,
  october: 10,
  nov: 11,
  november: 11,
  dec: 12,
  december: 12,
};

const MonthNamePattern =
  "(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\\.?";

const YearMonthDayPattern = /^(\d{4})[-/](\d{1,2})[-/](\d{1,2})/;
const CompactYearMonthDayPattern = /^(\d{4})(\d{2})(\d{2})/;
const UsNumericDatePattern = /^(\d{1,2})[/-](\d{1,2})[/-](\d{4}|\d{2})/;
const MonthDayYearPattern = new RegExp(
  `^(${MonthNamePattern})\\s+(\\d{1,2})(?:st|nd|rd|th)?,?\\s+(\\d{4})`,
);
const DayMonthYearPattern = new RegExp(
  `^(\\d{1,2})(?:st|nd|rd|th)?\\s+(${MonthNamePattern}),?\\s+(\\d{4})`,
);

function hasGeometryBoundary(
  entries: CharacterEntry[] | undefined,
  leftIndex: number,
  rightIndex: number,
): boolean {
  if (!entries) return false;

  const left = entries[leftIndex];
  const right = entries[rightIndex];
  if (!left || !right || left.lineIndex !== right.lineIndex) return true;

  const leftWidth = left.normRight - left.normLeft;
  return right.normLeft - left.normRight > leftWidth;
}

function hasDateTokenBoundary(
  text: string,
  start: number,
  end: number,
  entries?: CharacterEntry[],
): boolean {
  const hasLeftBoundary = !/[a-z0-9]/.test(text[start - 1] ?? "")
    || hasGeometryBoundary(entries, start - 1, start);
  const hasRightBoundary = !/[a-z0-9]/.test(text[end] ?? "")
    || hasGeometryBoundary(entries, end - 1, end);
  return hasLeftBoundary && hasRightBoundary;
}

function expandTwoDigitYear(year: number): number {
  // Matches CultureInfo.InvariantCulture.Calendar.ToFourDigitYear in the C# Auto-link parser.
  return year <= 49 ? 2000 + year : 1900 + year;
}

function isValidDate(year: number, month: number, day: number): boolean {
  if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1) return false;

  const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const daysInMonth = [31, leapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return day <= daysInMonth[month - 1]!;
}

function createDateToken(
  text: string,
  start: number,
  matchedText: string,
  year: number,
  month: number,
  day: number,
  entries?: CharacterEntry[],
): DateToken | null {
  const end = start + matchedText.length;
  if (!hasDateTokenBoundary(text, start, end, entries) || !isValidDate(year, month, day)) return null;
  return { end, normalized: `${month}/${day}/${year}` };
}

function findDateToken(text: string, start: number, entries?: CharacterEntry[]): DateToken | null {
  const remaining = text.slice(start);
  const startsWithDigit = /\d/.test(text[start] ?? "");

  let match: RegExpExecArray | null;
  if (startsWithDigit) {
    match = YearMonthDayPattern.exec(remaining) ?? CompactYearMonthDayPattern.exec(remaining);
    if (match) {
      return createDateToken(
        text,
        start,
        match[0],
        Number(match[1]),
        Number(match[2]),
        Number(match[3]),
        entries,
      );
    }

    match = UsNumericDatePattern.exec(remaining);
    if (match) {
      const rawYear = match[3]!;
      const year = rawYear.length === 2 ? expandTwoDigitYear(Number(rawYear)) : Number(rawYear);
      return createDateToken(
        text,
        start,
        match[0],
        year,
        Number(match[1]),
        Number(match[2]),
        entries,
      );
    }

    match = DayMonthYearPattern.exec(remaining);
    if (match) {
      const month = MonthNumbers[match[2]!.replace(/\.$/, "")];
      if (month !== undefined) {
        return createDateToken(
          text,
          start,
          match[0],
          Number(match[3]),
          month,
          Number(match[1]),
          entries,
        );
      }
    }

    return null;
  }

  match = MonthDayYearPattern.exec(remaining);
  if (match) {
    const month = MonthNumbers[match[1]!.replace(/\.$/, "")];
    if (month !== undefined) {
      return createDateToken(
        text,
        start,
        match[0],
        Number(match[3]),
        month,
        Number(match[2]),
        entries,
      );
    }
  }

  return null;
}

function isNumericComma(text: string, i: number): boolean {
  return text[i] === "," && /\d/.test(text[i - 1] ?? "") && /\d/.test(text[i + 1] ?? "");
}

function findAccountingNumberEnd(text: string, start: number): number {
  if (text[start] !== "(") return -1;

  let hasDigit = false;
  let i = start + 1;
  while (i < text.length && /[\d,.]/.test(text[i] ?? "")) {
    if (/\d/.test(text[i] ?? "")) hasDigit = true;
    i++;
  }

  return hasDigit && text[i] === ")" ? i : -1;
}

function isAccountingNumberClose(text: string, i: number): boolean {
  if (text[i] !== ")") return false;

  const openIndex = text.lastIndexOf("(", i);
  return openIndex !== -1 && findAccountingNumberEnd(text, openIndex) === i;
}

function normalizeQuery(raw: string, normalizeDates: boolean): string {
  const lower = raw.trim().toLowerCase();
  let result = "";
  for (let i = 0; i < lower.length; i++) {
    if (normalizeDates) {
      const date = findDateToken(lower, i);
      if (date) {
        result += date.normalized;
        i = date.end - 1;
        continue;
      }
    }

    const accountingEnd = findAccountingNumberEnd(lower, i);
    if (accountingEnd !== -1) {
      result += "-";
      continue;
    }

    if (isAccountingNumberClose(lower, i)) continue;
    if (!isNumericComma(lower, i)) result += lower[i];
  }
  return result;
}

export function normalizeSearchQuery(raw: string): string {
  return normalizeQuery(raw, false);
}

export function normalizeMatcherQuery(raw: string): string {
  return normalizeQuery(raw, true);
}

export function pageTextMatchesQuery(pageText: string, normalizedQuery: string): boolean {
  return buildSearchPageIndex(pageText).normalizedPageText.includes(normalizedQuery);
}

function buildNormalizedPageText(
  text: string,
  normalizeDates: boolean,
  entries?: CharacterEntry[],
): {
  normalizedPageText: string;
  indexMap: number[];
  dateSpans: NormalizedDateSpan[];
} {
  const indexMap: number[] = [];
  const dateSpans: NormalizedDateSpan[] = [];
  let normalizedPageText = "";
  for (let i = 0; i < text.length; i++) {
    if (normalizeDates) {
      const date = findDateToken(text, i, entries);
      if (date) {
        const normalizedStart = normalizedPageText.length;
        for (let j = 0; j < date.normalized.length; j++) {
          const sourceOffset = date.normalized.length === 1
            ? 0
            : Math.round((j * (date.end - i - 1)) / (date.normalized.length - 1));
          indexMap.push(i + sourceOffset);
          normalizedPageText += date.normalized[j];
        }
        dateSpans.push({
          normalizedStart,
          normalizedEnd: normalizedPageText.length,
          originalStart: i,
          originalEnd: date.end,
        });
        i = date.end - 1;
        continue;
      }
    }

    const accountingEnd = findAccountingNumberEnd(text, i);
    if (accountingEnd !== -1) {
      indexMap.push(i);
      normalizedPageText += "-";
      continue;
    }

    if (isAccountingNumberClose(text, i)) continue;

    if (!isNumericComma(text, i)) {
      indexMap.push(i);
      normalizedPageText += text[i];
    }
  }
  return { normalizedPageText, indexMap, dateSpans };
}

export function buildSearchPageIndex(pageText: string): SearchPageIndex {
  const { normalizedPageText, indexMap, dateSpans } = buildNormalizedPageText(pageText.toLowerCase(), false);
  return { pageText, normalizedPageText, indexMap, dateSpans };
}

export function buildSearchPageIndexFromEntries(
  entries: CharacterEntry[],
  options: Pick<SearchPageOptions, "normalizeDates"> = {},
): SearchPageIndex {
  const pageText = entries.map((e) => e.char).join("");
  const { normalizedPageText, indexMap, dateSpans } = buildNormalizedPageText(
    pageText.toLowerCase(),
    options.normalizeDates ?? false,
    entries,
  );
  return { pageText, normalizedPageText, indexMap, dateSpans };
}

function matchSpansSingleLine(entries: CharacterEntry[], start: number, end: number): boolean {
  for (let i = start; i < end - 1; i++) {
    const curr = entries[i];
    const next = entries[i + 1];
    if (curr === undefined || next === undefined || curr.lineIndex !== next.lineIndex) {
      return false;
    }
  }
  return true;
}

function expandToWord(
  text: string,
  entries: CharacterEntry[],
  matchStart: number,
  matchEnd: number,
): { contextText: string; matchInContext: { start: number; end: number } } {
  let wordStart = matchStart;
  while (
    wordStart > 0
    && !/\s/.test(text[wordStart - 1] ?? "")
    && entries[wordStart]?.lineIndex === entries[wordStart - 1]?.lineIndex
  ) {
    wordStart--;
  }

  let wordEnd = matchEnd;
  while (
    wordEnd < text.length
    && !/\s/.test(text[wordEnd] ?? "")
    && entries[wordEnd - 1]?.lineIndex === entries[wordEnd]?.lineIndex
  ) {
    wordEnd++;
  }

  return {
    contextText: text.slice(wordStart, wordEnd),
    matchInContext: { start: matchStart - wordStart, end: matchEnd - wordStart },
  };
}

function rectFromEntries(
  entries: CharacterEntry[],
  startIndex: number,
  endIndex: number,
): NormalizedRect {
  if (startIndex > endIndex) return { x: 0, y: 0, width: 0, height: 0 };

  let normLeft = Number.POSITIVE_INFINITY;
  let normTop = Number.POSITIVE_INFINITY;
  let normRight = Number.NEGATIVE_INFINITY;
  let normBottom = Number.NEGATIVE_INFINITY;

  for (let i = startIndex; i <= endIndex; i++) {
    const entry = entries[i];
    if (!entry) continue;

    normLeft = Math.min(normLeft, entry.normLeft);
    normTop = Math.min(normTop, entry.normTop);
    normRight = Math.max(normRight, entry.normRight);
    normBottom = Math.max(normBottom, entry.normBottom);
  }

  if (!Number.isFinite(normLeft)) return { x: 0, y: 0, width: 0, height: 0 };

  return { x: normLeft, y: normTop, width: normRight - normLeft, height: normBottom - normTop };
}

function resolveOriginalMatchSpan(
  pageText: string,
  dateSpans: NormalizedDateSpan[],
  normalizedStart: number,
  normalizedEnd: number,
  originalStart: number,
  originalEnd: number,
): { originalStart: number; originalEnd: number } {
  const exactDate = dateSpans.find(
    (span) => span.normalizedStart === normalizedStart && span.normalizedEnd === normalizedEnd,
  );
  if (exactDate) {
    return { originalStart: exactDate.originalStart, originalEnd: exactDate.originalEnd };
  }

  const accountingEnd = findAccountingNumberEnd(pageText.toLowerCase(), originalStart);
  if (accountingEnd !== -1 && accountingEnd >= originalEnd) {
    return { originalStart, originalEnd: accountingEnd + 1 };
  }

  return { originalStart, originalEnd };
}

export function searchPage(
  pdfId: string,
  pdfName: string,
  pageIndex: number,
  entries: CharacterEntry[],
  normalizedQuery: string,
  options: SearchPageOptions = {},
): SearchMatch[] {
  if (!normalizedQuery || entries.length === 0) return [];

  const searchIndex = buildSearchPageIndexFromEntries(entries, options);
  return searchPageWithIndex(pdfId, pdfName, pageIndex, entries, searchIndex, normalizedQuery, options);
}

export function searchPageWithIndex(
  pdfId: string,
  pdfName: string,
  pageIndex: number,
  entries: CharacterEntry[],
  searchIndex: SearchPageIndex,
  normalizedQuery: string,
  options: SearchPageOptions = {},
): SearchMatch[] {
  if (!normalizedQuery || entries.length === 0) return [];
  if (!searchIndex.normalizedPageText.includes(normalizedQuery)) return [];

  const { pageText, normalizedPageText, indexMap, dateSpans } = searchIndex;
  const queryLen = normalizedQuery.length;
  const matches: SearchMatch[] = [];
  const limit = options.limit ?? Number.POSITIVE_INFINITY;

  let offset = 0;
  while (matches.length < limit && offset <= normalizedPageText.length - queryLen) {
    const hitIndex = normalizedPageText.indexOf(normalizedQuery, offset);
    if (hitIndex === -1) break;

    const span = resolveOriginalMatchSpan(
      pageText,
      dateSpans,
      hitIndex,
      hitIndex + queryLen,
      indexMap[hitIndex]!,
      indexMap[hitIndex + queryLen - 1]! + 1,
    );
    const { originalStart, originalEnd } = span;

    if (!matchSpansSingleLine(entries, originalStart, originalEnd)) {
      offset = hitIndex + 1;
      continue;
    }

    const { contextText, matchInContext } = expandToWord(pageText, entries, originalStart, originalEnd);
    const highlightRect = rectFromEntries(entries, originalStart, originalEnd - 1);

    matches.push({
      id: `${pdfId}:${pageIndex}:${originalStart}`,
      pdfId,
      pdfName,
      pageIndex,
      contextText,
      matchInContext,
      highlightRect,
    });

    offset = hitIndex + 1;
  }

  return matches;
}
