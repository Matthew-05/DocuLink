import type { CharacterEntry } from "./char-entries.js";
import type { FinancialValue, PageFsValues } from "./fs-values-decoder.js";

const MONTH = "Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?";
const DATE_PATTERNS = [
  new RegExp(`\\b(?:${MONTH})\\.?\\s+\\d{1,2}(?:st|nd|rd|th)?\\s*,?\\s+(?:19|20)\\d{2}\\b`, "gi"),
  new RegExp(`\\b\\d{1,2}(?:st|nd|rd|th)?\\s+(?:${MONTH})\\.?\\s*,?\\s+(?:19|20)\\d{2}\\b`, "gi"),
  /\b(?:19|20)\d{2}[-/.](?:0?[1-9]|1[0-2])[-/.](?:0?[1-9]|[12]\d|3[01])\b/g,
  /(?<![\d.])(?:0?[1-9]|[12]\d|3[01])[/.-](?:0?[1-9]|[12]\d|3[01])[/.-](?:(?:19|20)?\d{2})(?![\d.])/g,
  new RegExp(`\\b(?:${MONTH})\\.?\\s+(?:19|20)\\d{2}\\b`, "gi"),
  /\b(?:Q[1-4]\s*(?:FY\s*)?(?:19|20)\d{2}|(?:FY\s*)?(?:19|20)\d{2}\s*Q[1-4])\b/gi,
  /\b(?:FY\s*)?(?:19|20)\d{2}\b/gi,
];
const MAGNITUDE_PATTERN = "thousands?|millions?|billions?|trillions?|thou|tril|bln|mln|trn|ths|bil|mil|bn|mn|mm|tn|th|k|m|b|t";
const MAGNITUDES: Readonly<Record<string, FinancialValue["magnitude"]>> = {
  k: 1000, th: 1000, ths: 1000, thou: 1000, thousand: 1000, thousands: 1000,
  m: 1000000, mm: 1000000, mn: 1000000, mil: 1000000, mln: 1000000, million: 1000000, millions: 1000000,
  b: 1000000000, bn: 1000000000, bln: 1000000000, bil: 1000000000, billion: 1000000000, billions: 1000000000,
  t: 1000000000000, tn: 1000000000000, trn: 1000000000000, tril: 1000000000000, trillion: 1000000000000, trillions: 1000000000000,
};
const NUMBER = new RegExp(
  `(?<![\\w\\d])(?:\\(\\s*)?(?:(?:USD|EUR|GBP|JPY|CAD|AUD|CHF|CNY|INR|KRW|[$€£¥₹₩])\\s*)?[+-]?\\s*`+
  `(?:(?:\\d{1,3}(?:,\\d{3})+|\\d+)(?:\\.\\d+)?|\\.\\d+)\\s*(?:%|percent\\b)?\\s*`+
  `(?:${MAGNITUDE_PATTERN})?\\s*\\)?(?![\\w\\d])`,
  "gi",
);
const MAGNITUDE_AT_END = new RegExp(`\\b(${MAGNITUDE_PATTERN})\\s*\\)?\\s*$`, "i");
const MAGNITUDE_AT_START = new RegExp(`^\\s*(${MAGNITUDE_PATTERN})\\b`, "i");
const BASE_NUMBER = /(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|\.\d+/;

interface LineText { text: string; source: Array<CharacterEntry | null> }
interface Span {
  start: number;
  end: number;
  kind: FinancialValue["kind"];
  text: string;
  normalizedValue?: string;
  magnitude?: FinancialValue["magnitude"];
}

function buildLine(entries: CharacterEntry[]): LineText {
  const ordered = [...entries].sort((a, b) => a.normLeft - b.normLeft || a.itemIndex - b.itemIndex);
  const widths = ordered.map((entry) => entry.normRight - entry.normLeft).filter((width) => width > 0).sort((a, b) => a - b);
  const typical = widths[Math.floor(widths.length / 2)] ?? 0.005;
  let text = "";
  const source: Array<CharacterEntry | null> = [];
  let prior: CharacterEntry | undefined;
  for (const entry of ordered) {
    if (prior && entry.normLeft - prior.normRight > typical * 1.25 && !text.endsWith(" ")) {
      text += " "; source.push(null);
    }
    for (const char of entry.char) { text += char; source.push(entry); }
    prior = entry;
  }
  return { text, source };
}

function recognize(text: string): Span[] {
  const spans: Span[] = [];
  const overlaps = (start: number, end: number) => spans.some((span) => start < span.end && end > span.start);
  for (const pattern of DATE_PATTERNS) {
    pattern.lastIndex = 0;
    for (const match of text.matchAll(pattern)) {
      const start = match.index;
      if (start === undefined || overlaps(start, start + match[0].length)) continue;
      spans.push({ start, end: start + match[0].length, kind: "date", text: match[0] });
    }
  }
  NUMBER.lastIndex = 0;
  for (const match of text.matchAll(NUMBER)) {
    const start = match.index;
    if (start === undefined || overlaps(start, start + match[0].length)) continue;
    const matchedText = match[0].trim();
    const isPercent = /%|percent\b/i.test(matchedText);
    const magnitudeToken = isPercent ? undefined : MAGNITUDE_AT_END.exec(matchedText)?.[1]?.toLowerCase();
    const magnitude = magnitudeToken ? MAGNITUDES[magnitudeToken] : undefined;
    const base = BASE_NUMBER.exec(matchedText)?.[0];
    const negative = /-\s*(?:\d|\.)/.test(matchedText) || /^\s*\(/.test(matchedText);
    const numeric = base === undefined ? Number.NaN : Number(base.replaceAll(",", ""));
    const scaled = numeric * (magnitude ?? 1) * (negative ? -1 : 1);
    spans.push({
      start,
      end: start + match[0].length,
      kind: isPercent ? "percent" : "number",
      text: matchedText,
      ...(Number.isFinite(scaled) ? { normalizedValue: String(scaled) } : {}),
      ...(magnitude ? { magnitude } : {}),
    });
  }
  return spans.sort((a, b) => a.start - b.start);
}

function financialValue(span: Span, source: Array<CharacterEntry | null>, pageIndex: number, index: number): FinancialValue | null {
  const entries = source.slice(span.start, span.end).filter((entry): entry is CharacterEntry => entry !== null && entry.char.trim() !== "");
  if (entries.length === 0) return null;
  const left = Math.min(...entries.map((entry) => entry.normLeft));
  const top = Math.min(...entries.map((entry) => entry.normTop));
  const right = Math.max(...entries.map((entry) => entry.normRight));
  const bottom = Math.max(...entries.map((entry) => entry.normBottom));
  return {
    id: `fsv-fallback-p${pageIndex}-v${index}`,
    kind: span.kind,
    text: span.text,
    bounds: { x: left, y: top, width: right - left, height: bottom - top },
    confidence: 0.7,
    ...(span.normalizedValue ? { normalizedValue: span.normalizedValue } : {}),
    ...(span.magnitude ? { magnitude: span.magnitude } : {}),
  };
}

/** Browser fallback for PDFs whose workbook cache predates fs-values-v1. */
export function detectFsValuesFromEntries(pageIndex: number, entries: CharacterEntry[]): PageFsValues {
  const byLine = new Map<number, CharacterEntry[]>();
  for (const entry of entries) {
    const line = byLine.get(entry.lineIndex);
    if (line) line.push(entry); else byLine.set(entry.lineIndex, [entry]);
  }
  const values: FinancialValue[] = [];
  const lines = [...byLine.entries()].sort((a, b) => a[0] - b[0]).map((entry) => buildLine(entry[1]));
  for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
    const line = lines[lineIndex]!;
    for (const span of recognize(line.text)) {
      const value = financialValue(span, line.source, pageIndex, values.length);
      if (!value) continue;
      const nextLine = lines[lineIndex + 1];
      if (span.kind === "number" && !span.magnitude && !line.text.slice(span.end).trim() && nextLine) {
        const modifier = MAGNITUDE_AT_START.exec(nextLine.text);
        const token = modifier?.[1];
        const magnitude = token ? MAGNITUDES[token.toLowerCase()] : undefined;
        if (modifier && token && magnitude && value.normalizedValue) {
          const modifierSpan: Span = { start: modifier.index, end: modifier.index + modifier[0].length, kind: "number", text: token };
          const modifierValue = financialValue(modifierSpan, nextLine.source, pageIndex, values.length);
          if (modifierValue) {
            value.text = `${span.text} ${token}`;
            value.normalizedValue = String(Number(value.normalizedValue) * magnitude);
            value.magnitude = magnitude;
            value.segments = [
              { pageIndex, text: span.text, bounds: value.bounds },
              { pageIndex, text: token, bounds: modifierValue.bounds },
            ];
          }
        }
      }
      values.push(value);
    }
  }
  return { pageIndex, context: {}, values };
}
