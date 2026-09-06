"""Render spreadsheet documents as self-contained HTML for the host to print to PDF.

Every supported format is read into one small intermediate model (:class:`SheetModel`
of :class:`CellBox`) and a single emitter turns that into HTML. Adding a format means
writing a reader, not another HTML generator.

Formats and what each reader can actually recover:

  ============  ==========  =======  ======  =============  =========
  Format        Library     Values   Merges  Column widths  Styling
  ============  ==========  =======  ======  =============  =========
  .xlsx .xlsm   openpyxl    yes      yes     yes            full
  .xls          xlrd        yes      yes*    yes*           partial*
  .xlsb         pyxlsb      yes      no      no             none
  .ods          stdlib      yes      yes     yes            alignment
  .csv .tsv     stdlib      yes      no      no             none
  ============  ==========  =======  ======  =============  =========

  * .xls needs ``formatting_info``, which xlrd only offers for some files; the
    reader degrades to values-only rather than failing.

.ods is read with zipfile + ElementTree rather than odfpy. odfpy publishes no
wheel, and the worker is built on the Python embeddable distribution, which has
no setuptools — so pip cannot build its sdist and the whole worker build fails.
An OpenDocument spreadsheet is a zip with an XML part in it, and the subset that
matters here (values, spans, repeats, column widths) is a short read.

Charts, shapes, embedded images and conditional formatting are out of scope for
all of them — nothing here lays out drawing objects.

Why HTML and not PDF bytes: Talliark already renders HTML through an offscreen
WebView2 (Modules/Services/Conversion/HtmlToPdfConverter.cs), and Chromium's table
layout is far better than anything that could be assembled here. The worker returns
``ConversionOutput.as_html`` and the host finishes the job. See
contracts/python-worker-v1.json.
"""
from __future__ import annotations

import csv
import datetime as _datetime
import html as html_module
import io
import re
from dataclasses import dataclass, field
from typing import Any, Iterable, Sequence

# ── Format routing ────────────────────────────────────────────────────────────
# Keep in sync with ConversionFormatCatalog.cs on the C# side.

EXCEL_OOXML_EXTENSIONS = frozenset({".xlsx", ".xlsm"})
EXCEL_LEGACY_EXTENSIONS = frozenset({".xls"})
EXCEL_BINARY_EXTENSIONS = frozenset({".xlsb"})
OPENDOCUMENT_EXTENSIONS = frozenset({".ods"})
DELIMITED_EXTENSIONS = frozenset({".csv", ".tsv"})

SPREADSHEET_EXTENSIONS = (
    EXCEL_OOXML_EXTENSIONS
    | EXCEL_LEGACY_EXTENSIONS
    | EXCEL_BINARY_EXTENSIONS
    | OPENDOCUMENT_EXTENSIONS
    | DELIMITED_EXTENSIONS
)

# Guard rails. A spreadsheet with a million used rows would produce HTML that
# takes longer to render than anyone will wait for, and the result would be
# unreadable anyway; truncate and say so in the output.
MAX_ROWS_PER_SHEET = 5000
MAX_COLUMNS_PER_SHEET = 256
MAX_SHEETS = 50

# Printable width of A4 landscape at 96dpi less the page margins below, in px.
# Sheets wider than this are zoomed down to fit rather than clipped.
_PRINTABLE_WIDTH_PX = 1045.0
_MIN_ZOOM = 0.4

# openpyxl column widths are in "characters"; this is the usual approximation.
_PX_PER_CHARACTER = 7.0
_COLUMN_PADDING_PX = 5.0
_DEFAULT_COLUMN_PX = 64.0


class SpreadsheetError(Exception):
    """Raised when a spreadsheet cannot be read."""


# ── Intermediate model ────────────────────────────────────────────────────────


@dataclass
class CellBox:
    """One rendered cell. Readers fill in as much as their format supports."""

    text: str = ""
    colspan: int = 1
    rowspan: int = 1
    align: str = ""          # "", "left", "right", "center"
    bold: bool = False
    italic: bool = False
    underline: bool = False
    background: str = ""     # "#rrggbb"
    colour: str = ""         # "#rrggbb"
    wrap: bool = False


@dataclass
class SheetModel:
    """One worksheet: a grid of cells, with None marking a position covered by a merge."""

    name: str
    rows: list[list[CellBox | None]] = field(default_factory=list)
    column_widths: list[float] = field(default_factory=list)
    truncated_rows: bool = False
    truncated_columns: bool = False

    @property
    def column_count(self) -> int:
        return max((len(row) for row in self.rows), default=0)


# ── Entry point ───────────────────────────────────────────────────────────────


def is_spreadsheet(extension: str) -> bool:
    return _normalise_extension(extension) in SPREADSHEET_EXTENSIONS


def spreadsheet_to_html(
    source_bytes: bytes,
    extension: str,
    source_name: str = "",
) -> str:
    """Reads a spreadsheet and returns a self-contained HTML document."""
    ext = _normalise_extension(extension)

    if not source_bytes:
        raise SpreadsheetError("Spreadsheet is empty.")

    if ext in EXCEL_OOXML_EXTENSIONS:
        sheets = _read_openpyxl(source_bytes)
    elif ext in EXCEL_LEGACY_EXTENSIONS:
        sheets = _read_xlrd(source_bytes)
    elif ext in EXCEL_BINARY_EXTENSIONS:
        sheets = _read_pyxlsb(source_bytes)
    elif ext in OPENDOCUMENT_EXTENSIONS:
        sheets = _read_ods(source_bytes)
    elif ext in DELIMITED_EXTENSIONS:
        sheets = _read_delimited(source_bytes, ext, source_name)
    else:
        raise SpreadsheetError(f"Unsupported spreadsheet type '{ext}'.")

    sheets = [sheet for sheet in sheets if sheet.rows]
    if not sheets:
        raise SpreadsheetError("The spreadsheet has no readable content.")

    return _render_document(sheets, source_name)


def _normalise_extension(extension: str) -> str:
    ext = (extension or "").strip().lower()
    if ext and not ext.startswith("."):
        ext = "." + ext
    return ext


# ── .xlsx / .xlsm via openpyxl ────────────────────────────────────────────────


def _read_openpyxl(source_bytes: bytes) -> list[SheetModel]:
    try:
        import openpyxl
        from openpyxl.utils import get_column_letter
    except ImportError as exc:  # pragma: no cover - packaging error
        raise SpreadsheetError("The .xlsx reader is not available in this build.") from exc

    try:
        # data_only so cached formula results come through as values; a formula
        # string in a printed sheet is never what the user meant to see.
        workbook = openpyxl.load_workbook(
            io.BytesIO(source_bytes), data_only=True, read_only=False
        )
    except Exception as exc:  # noqa: BLE001
        raise SpreadsheetError(f"Could not open the workbook: {exc}") from exc

    sheets: list[SheetModel] = []

    try:
        for worksheet in workbook.worksheets[:MAX_SHEETS]:
            if worksheet.sheet_state != "visible":
                continue

            sheets.append(_read_openpyxl_sheet(worksheet, get_column_letter))
    finally:
        try:
            workbook.close()
        except Exception:  # noqa: BLE001
            pass

    return sheets


def _read_openpyxl_sheet(worksheet: Any, get_column_letter: Any) -> SheetModel:
    row_limit = min(worksheet.max_row or 0, MAX_ROWS_PER_SHEET)
    column_limit = min(worksheet.max_column or 0, MAX_COLUMNS_PER_SHEET)

    sheet = SheetModel(
        name=str(worksheet.title),
        truncated_rows=(worksheet.max_row or 0) > row_limit,
        truncated_columns=(worksheet.max_column or 0) > column_limit,
    )

    # Merges are recorded on the sheet, not the cell, so build a lookup first:
    # the anchor gets the span, every other position it covers is dropped.
    spans: dict[tuple[int, int], tuple[int, int]] = {}
    covered: set[tuple[int, int]] = set()

    for merged in worksheet.merged_cells.ranges:
        anchor = (merged.min_row, merged.min_col)
        spans[anchor] = (
            merged.max_row - merged.min_row + 1,
            merged.max_col - merged.min_col + 1,
        )
        for row_index in range(merged.min_row, merged.max_row + 1):
            for column_index in range(merged.min_col, merged.max_col + 1):
                if (row_index, column_index) != anchor:
                    covered.add((row_index, column_index))

    for column_index in range(1, column_limit + 1):
        dimension = worksheet.column_dimensions.get(get_column_letter(column_index))
        if dimension is not None and dimension.hidden:
            sheet.column_widths.append(0.0)
        elif dimension is not None and dimension.width:
            sheet.column_widths.append(dimension.width * _PX_PER_CHARACTER + _COLUMN_PADDING_PX)
        else:
            sheet.column_widths.append(_DEFAULT_COLUMN_PX)

    for row_index in range(1, row_limit + 1):
        if worksheet.row_dimensions.get(row_index) is not None \
                and worksheet.row_dimensions[row_index].hidden:
            continue

        row: list[CellBox | None] = []

        for column_index in range(1, column_limit + 1):
            if (row_index, column_index) in covered:
                row.append(None)
                continue

            cell = worksheet.cell(row=row_index, column=column_index)
            box = _openpyxl_cell_box(cell)

            span = spans.get((row_index, column_index))
            if span is not None:
                box.rowspan, box.colspan = span

            row.append(box)

        sheet.rows.append(row)

    _trim_trailing_blank_rows(sheet)
    return sheet


def _openpyxl_cell_box(cell: Any) -> CellBox:
    box = CellBox(text=_format_value(cell.value, getattr(cell, "number_format", "")))

    font = getattr(cell, "font", None)
    if font is not None:
        box.bold = bool(font.bold)
        box.italic = bool(font.italic)
        box.underline = bool(font.underline)
        box.colour = _openpyxl_colour(getattr(font, "color", None))

    fill = getattr(cell, "fill", None)
    if fill is not None and getattr(fill, "patternType", None) in ("solid", "lightGray"):
        box.background = _openpyxl_colour(getattr(fill, "fgColor", None))

    alignment = getattr(cell, "alignment", None)
    if alignment is not None:
        if alignment.horizontal in ("left", "right", "center"):
            box.align = alignment.horizontal
        box.wrap = bool(alignment.wrap_text)

    # Numbers read far better right-aligned, which is what Excel does by default
    # and what "General" leaves unset.
    if not box.align and isinstance(cell.value, (int, float)) \
            and not isinstance(cell.value, bool):
        box.align = "right"

    return box


def _openpyxl_colour(colour: Any) -> str:
    """Converts an openpyxl colour to #rrggbb, ignoring theme and indexed colours.

    Theme colours resolve through the workbook's theme XML, and indexed ones
    through a palette that varies by file. Neither is worth carrying for a
    printed sheet — an unresolved colour simply renders as the default.
    """
    if colour is None:
        return ""

    if getattr(colour, "type", None) != "rgb":
        return ""

    value = getattr(colour, "rgb", None)
    if not isinstance(value, str) or len(value) not in (6, 8):
        return ""

    rgb = value[-6:]
    if not re.fullmatch(r"[0-9A-Fa-f]{6}", rgb):
        return ""

    # Excel writes white fills on cells that simply have no fill; painting them
    # would draw a grid of white boxes over the table's own borders.
    if rgb.upper() in ("FFFFFF", "000000") and getattr(colour, "tint", 0):
        return ""

    return "#" + rgb.lower()


# ── .xls via xlrd ─────────────────────────────────────────────────────────────


def _read_xlrd(source_bytes: bytes) -> list[SheetModel]:
    try:
        import xlrd
    except ImportError as exc:  # pragma: no cover - packaging error
        raise SpreadsheetError("The .xls reader is not available in this build.") from exc

    workbook = None
    has_formatting = True

    # formatting_info is what carries merges and column widths, but xlrd raises
    # for files whose records it cannot fully parse. Values matter more than
    # styling, so fall back rather than fail the conversion.
    try:
        workbook = xlrd.open_workbook(file_contents=source_bytes, formatting_info=True)
    except Exception:  # noqa: BLE001
        has_formatting = False
        try:
            workbook = xlrd.open_workbook(file_contents=source_bytes)
        except Exception as exc:  # noqa: BLE001
            raise SpreadsheetError(f"Could not open the workbook: {exc}") from exc

    sheets: list[SheetModel] = []

    for worksheet in workbook.sheets()[:MAX_SHEETS]:
        if getattr(worksheet, "visibility", 0) != 0:
            continue

        sheets.append(_read_xlrd_sheet(workbook, worksheet, has_formatting, xlrd))

    return sheets


def _read_xlrd_sheet(
    workbook: Any,
    worksheet: Any,
    has_formatting: bool,
    xlrd: Any,
) -> SheetModel:
    row_limit = min(worksheet.nrows, MAX_ROWS_PER_SHEET)
    column_limit = min(worksheet.ncols, MAX_COLUMNS_PER_SHEET)

    sheet = SheetModel(
        name=str(worksheet.name),
        truncated_rows=worksheet.nrows > row_limit,
        truncated_columns=worksheet.ncols > column_limit,
    )

    spans: dict[tuple[int, int], tuple[int, int]] = {}
    covered: set[tuple[int, int]] = set()

    if has_formatting:
        # xlrd merge ranges are half-open: [row_lo, row_hi) x [col_lo, col_hi).
        for row_lo, row_hi, column_lo, column_hi in getattr(worksheet, "merged_cells", []):
            spans[(row_lo, column_lo)] = (row_hi - row_lo, column_hi - column_lo)
            for row_index in range(row_lo, row_hi):
                for column_index in range(column_lo, column_hi):
                    if (row_index, column_index) != (row_lo, column_lo):
                        covered.add((row_index, column_index))

    for column_index in range(column_limit):
        width = None
        if has_formatting:
            info = worksheet.colinfo_map.get(column_index)
            if info is not None:
                # width is in 1/256ths of a character.
                width = info.width / 256.0 * _PX_PER_CHARACTER + _COLUMN_PADDING_PX
        sheet.column_widths.append(width or _DEFAULT_COLUMN_PX)

    for row_index in range(row_limit):
        row: list[CellBox | None] = []

        for column_index in range(column_limit):
            if (row_index, column_index) in covered:
                row.append(None)
                continue

            box = _xlrd_cell_box(
                workbook, worksheet, row_index, column_index, has_formatting, xlrd
            )

            span = spans.get((row_index, column_index))
            if span is not None:
                box.rowspan, box.colspan = span

            row.append(box)

        sheet.rows.append(row)

    _trim_trailing_blank_rows(sheet)
    return sheet


def _xlrd_cell_box(
    workbook: Any,
    worksheet: Any,
    row_index: int,
    column_index: int,
    has_formatting: bool,
    xlrd: Any,
) -> CellBox:
    cell = worksheet.cell(row_index, column_index)
    value: Any = cell.value

    if cell.ctype == xlrd.XL_CELL_DATE:
        try:
            value = _datetime.datetime(*xlrd.xldate_as_tuple(cell.value, workbook.datemode))
        except Exception:  # noqa: BLE001
            pass
    elif cell.ctype == xlrd.XL_CELL_BOOLEAN:
        value = bool(cell.value)
    elif cell.ctype == xlrd.XL_CELL_EMPTY:
        value = None
    elif cell.ctype == xlrd.XL_CELL_ERROR:
        value = "#ERR"

    number_format = ""
    if has_formatting:
        try:
            xf = workbook.xf_list[worksheet.cell_xf_index(row_index, column_index)]
            number_format = workbook.format_map[xf.format_key].format_str or ""
        except Exception:  # noqa: BLE001
            number_format = ""

    box = CellBox(text=_format_value(value, number_format))

    if has_formatting:
        try:
            xf = workbook.xf_list[worksheet.cell_xf_index(row_index, column_index)]
            font = workbook.font_list[xf.font_index]
            box.bold = bool(getattr(font, "bold", 0)) or getattr(font, "weight", 400) >= 700
            box.italic = bool(getattr(font, "italic", 0))
            box.underline = bool(getattr(font, "underline_type", 0))
        except Exception:  # noqa: BLE001
            pass

    if isinstance(value, (int, float)) and not isinstance(value, bool):
        box.align = "right"

    return box


# ── .xlsb via pyxlsb ──────────────────────────────────────────────────────────


def _read_pyxlsb(source_bytes: bytes) -> list[SheetModel]:
    try:
        import pyxlsb
    except ImportError as exc:  # pragma: no cover - packaging error
        raise SpreadsheetError("The .xlsb reader is not available in this build.") from exc

    sheets: list[SheetModel] = []

    try:
        with pyxlsb.open_workbook(io.BytesIO(source_bytes)) as workbook:
            for name in workbook.sheets[:MAX_SHEETS]:
                with workbook.get_sheet(name) as worksheet:
                    sheets.append(_read_pyxlsb_sheet(name, worksheet))
    except SpreadsheetError:
        raise
    except Exception as exc:  # noqa: BLE001
        raise SpreadsheetError(f"Could not open the workbook: {exc}") from exc

    return sheets


def _read_pyxlsb_sheet(name: str, worksheet: Any) -> SheetModel:
    """pyxlsb yields sparse rows of (row, col, value) — no styles, merges or widths."""
    sheet = SheetModel(name=str(name))
    grid: dict[int, dict[int, Any]] = {}
    max_column = 0

    for row_index, row in enumerate(worksheet.rows()):
        if row_index >= MAX_ROWS_PER_SHEET:
            sheet.truncated_rows = True
            break

        for cell in row:
            if cell.c >= MAX_COLUMNS_PER_SHEET:
                sheet.truncated_columns = True
                continue

            if cell.v is None or cell.v == "":
                continue

            grid.setdefault(cell.r, {})[cell.c] = cell.v
            max_column = max(max_column, cell.c)

    if not grid:
        return sheet

    sheet.column_widths = [_DEFAULT_COLUMN_PX] * (max_column + 1)

    for row_index in range(max(grid) + 1):
        source_row = grid.get(row_index, {})
        row: list[CellBox | None] = []

        for column_index in range(max_column + 1):
            value = source_row.get(column_index)
            box = CellBox(text=_format_value(value, ""))
            if isinstance(value, (int, float)) and not isinstance(value, bool):
                box.align = "right"
            row.append(box)

        sheet.rows.append(row)

    _trim_trailing_blank_rows(sheet)
    return sheet


# ── .ods via zipfile + ElementTree ────────────────────────────────────────────

_ODF_TABLE_NS = "urn:oasis:names:tc:opendocument:xmlns:table:1.0"
_ODF_OFFICE_NS = "urn:oasis:names:tc:opendocument:xmlns:office:1.0"
_ODF_STYLE_NS = "urn:oasis:names:tc:opendocument:xmlns:style:1.0"

_ODF_TABLE = f"{{{_ODF_TABLE_NS}}}"
_ODF_OFFICE = f"{{{_ODF_OFFICE_NS}}}"
_ODF_STYLE = f"{{{_ODF_STYLE_NS}}}"

# Lengths in ODF carry their unit; everything is normalised to px at 96dpi.
_ODF_UNIT_TO_PX = {
    "cm": 96.0 / 2.54,
    "mm": 96.0 / 25.4,
    "in": 96.0,
    "pt": 96.0 / 72.0,
    "pc": 16.0,
    "px": 1.0,
}


def _read_ods(source_bytes: bytes) -> list[SheetModel]:
    import xml.etree.ElementTree as ElementTree
    import zipfile

    try:
        with zipfile.ZipFile(io.BytesIO(source_bytes)) as archive:
            content = archive.read("content.xml")
    except KeyError as exc:
        raise SpreadsheetError("The file is not an OpenDocument spreadsheet.") from exc
    except Exception as exc:  # noqa: BLE001
        raise SpreadsheetError(f"Could not open the spreadsheet: {exc}") from exc

    try:
        root = ElementTree.fromstring(content)
    except ElementTree.ParseError as exc:
        raise SpreadsheetError(f"The spreadsheet's content is malformed: {exc}") from exc

    column_widths = _read_ods_column_styles(root)

    return [
        _read_ods_sheet(table, column_widths)
        for table in root.iter(_ODF_TABLE + "table")
    ][:MAX_SHEETS]


def _read_ods_column_styles(root: Any) -> dict[str, float]:
    """Maps automatic column style names to a width in px.

    Columns reference a style by name rather than carrying their own width, and
    the styles live in a separate part of the same document.
    """
    widths: dict[str, float] = {}

    for style in root.iter(_ODF_STYLE + "style"):
        if style.get(_ODF_STYLE + "family") != "table-column":
            continue

        name = style.get(_ODF_STYLE + "name")
        if not name:
            continue

        for properties in style.iter(_ODF_STYLE + "table-column-properties"):
            width = _odf_length_to_px(properties.get(_ODF_STYLE + "column-width"))
            if width:
                widths[name] = width

    return widths


def _odf_length_to_px(length: str | None) -> float:
    if not length:
        return 0.0

    match = re.fullmatch(r"\s*(-?[0-9.]+)\s*([a-z]{2})\s*", length)
    if not match:
        return 0.0

    try:
        return float(match.group(1)) * _ODF_UNIT_TO_PX.get(match.group(2), 0.0)
    except ValueError:
        return 0.0


def _read_ods_sheet(table: Any, column_styles: dict[str, float]) -> SheetModel:
    """Expands ODF's run-length encoding into a dense grid.

    ODF repeats rows, columns and cells with number-*-repeated attributes rather
    than writing them out, and a trailing empty row routinely claims to repeat a
    million times, so every repeat is clamped to the sheet limits.
    """
    sheet = SheetModel(name=str(table.get(_ODF_TABLE + "name") or "Sheet"))

    sheet.column_widths = _read_ods_column_widths(table, column_styles)

    row_index = 0

    for row_element in table.iter(_ODF_TABLE + "table-row"):
        if row_index >= MAX_ROWS_PER_SHEET:
            sheet.truncated_rows = True
            break

        repeat = _odf_int(row_element, _ODF_TABLE + "number-rows-repeated", 1)
        repeat = max(1, min(repeat, MAX_ROWS_PER_SHEET - row_index))

        cells = _read_ods_row(row_element, sheet)

        for _ in range(repeat):
            sheet.rows.append([_copy_box(box) for box in cells])
            row_index += 1

    _normalise_row_lengths(sheet)
    _trim_trailing_blank_rows(sheet)

    while len(sheet.column_widths) < sheet.column_count:
        sheet.column_widths.append(_DEFAULT_COLUMN_PX)

    return sheet


def _read_ods_column_widths(table: Any, column_styles: dict[str, float]) -> list[float]:
    widths: list[float] = []

    for column in table.iter(_ODF_TABLE + "table-column"):
        repeat = _odf_int(column, _ODF_TABLE + "number-columns-repeated", 1)
        repeat = max(1, min(repeat, MAX_COLUMNS_PER_SHEET - len(widths)))

        width = column_styles.get(column.get(_ODF_TABLE + "style-name") or "", 0.0)

        for _ in range(repeat):
            widths.append(width or _DEFAULT_COLUMN_PX)

    return widths


def _read_ods_row(row_element: Any, sheet: SheetModel) -> list[CellBox | None]:
    """Reads one row's cells.

    ODF writes an explicit <covered-table-cell> for every position a merge hides,
    so the covered set only has to guard against producers that do not — both
    routes emit None, and each occurrence advances the column exactly once, so
    they cannot disagree about alignment.
    """
    cells: list[CellBox | None] = []
    covered_columns: set[int] = set()
    column_index = 0

    for cell_element in row_element:
        tag = cell_element.tag
        is_covered = tag == _ODF_TABLE + "covered-table-cell"

        if not is_covered and tag != _ODF_TABLE + "table-cell":
            continue

        repeat = _odf_int(cell_element, _ODF_TABLE + "number-columns-repeated", 1)
        repeat = max(1, min(repeat, MAX_COLUMNS_PER_SHEET - column_index))

        colspan = _odf_int(cell_element, _ODF_TABLE + "number-columns-spanned", 1)
        rowspan = _odf_int(cell_element, _ODF_TABLE + "number-rows-spanned", 1)

        text = _odf_cell_text(cell_element)
        value_type = cell_element.get(_ODF_OFFICE + "value-type") or ""

        for _ in range(repeat):
            if column_index >= MAX_COLUMNS_PER_SHEET:
                sheet.truncated_columns = True
                break

            if is_covered or column_index in covered_columns:
                cells.append(None)
                column_index += 1
                continue

            box = CellBox(text=text)
            if value_type in ("float", "currency", "percentage"):
                box.align = "right"

            if colspan > 1 or rowspan > 1:
                box.colspan = colspan
                box.rowspan = rowspan
                covered_columns.update(
                    range(column_index + 1, column_index + colspan)
                )

            cells.append(box)
            column_index += 1

    return cells


def _odf_int(element: Any, attribute: str, default: int) -> int:
    raw = element.get(attribute)
    if not raw:
        return default
    try:
        return int(raw)
    except (TypeError, ValueError):
        return default


def _odf_cell_text(cell_element: Any) -> str:
    """Joins the cell's paragraphs, preferring displayed text over the raw value."""
    paragraphs = [
        "".join(paragraph.itertext()).strip()
        for paragraph in cell_element
        if paragraph.tag.endswith("}p")
    ]
    text = "\n".join(part for part in paragraphs if part).strip()

    if text:
        return html_module.escape(text).replace("\n", "<br>")

    for attribute in ("value", "date-value", "time-value", "boolean-value"):
        raw = cell_element.get(_ODF_OFFICE + attribute)
        if raw:
            return html_module.escape(str(raw))

    return ""


# ── .csv / .tsv via the standard library ──────────────────────────────────────


def _read_delimited(source_bytes: bytes, ext: str, source_name: str) -> list[SheetModel]:
    text = _decode_text(source_bytes)

    if ext == ".tsv":
        dialect: Any = csv.excel_tab
    else:
        # Sniffing catches semicolon-separated exports from non-English locales,
        # which are common enough to be worth handling and cheap to get wrong
        # safely — the fallback is plain comma.
        try:
            dialect = csv.Sniffer().sniff(text[:8192], delimiters=",;\t|")
        except csv.Error:
            dialect = csv.excel

    sheet = SheetModel(name=source_name or "Data")

    for row_index, values in enumerate(csv.reader(io.StringIO(text), dialect)):
        if row_index >= MAX_ROWS_PER_SHEET:
            sheet.truncated_rows = True
            break

        if len(values) > MAX_COLUMNS_PER_SHEET:
            values = values[:MAX_COLUMNS_PER_SHEET]
            sheet.truncated_columns = True

        row: list[CellBox | None] = []
        for value in values:
            box = CellBox(text=html_module.escape(value))
            if _looks_numeric(value):
                box.align = "right"
            row.append(box)

        sheet.rows.append(row)

    _normalise_row_lengths(sheet)
    sheet.column_widths = [_DEFAULT_COLUMN_PX] * sheet.column_count

    # A header row is the overwhelmingly common shape for delimited data and
    # costs nothing to mark up.
    if sheet.rows:
        for box in sheet.rows[0]:
            if box is not None:
                box.bold = True

    return [sheet]


def _looks_numeric(value: str) -> bool:
    stripped = value.strip().replace(",", "").replace("%", "")
    if not stripped:
        return False
    try:
        float(stripped)
        return True
    except ValueError:
        return False


def _decode_text(source_bytes: bytes) -> str:
    for bom, encoding in (
        (b"\xef\xbb\xbf", "utf-8-sig"),
        (b"\xff\xfe", "utf-16"),
        (b"\xfe\xff", "utf-16"),
    ):
        if source_bytes.startswith(bom):
            try:
                return source_bytes.decode(encoding)
            except UnicodeDecodeError:
                break
    try:
        return source_bytes.decode("utf-8")
    except UnicodeDecodeError:
        return source_bytes.decode("latin-1", errors="replace")


# ── Value formatting ──────────────────────────────────────────────────────────


def _format_value(value: Any, number_format: str) -> str:
    """Renders a cell value as escaped display text, honouring the number format.

    This is not a full Excel format engine — those are a small language of their
    own. It reads the shape of the format string (does it contain a currency
    symbol, a percent sign, a thousands separator, how many decimal places) and
    applies that, which covers the formats people actually apply to a sheet they
    intend to print.
    """
    if value is None:
        return ""

    if isinstance(value, bool):
        return "TRUE" if value else "FALSE"

    if isinstance(value, (_datetime.datetime, _datetime.date, _datetime.time)):
        return html_module.escape(_format_temporal(value, number_format))

    if isinstance(value, (int, float)):
        return html_module.escape(_format_number(value, number_format))

    return html_module.escape(str(value)).replace("\n", "<br>")


def _format_temporal(value: Any, number_format: str) -> str:
    fmt = (number_format or "").lower()

    if isinstance(value, _datetime.time):
        return value.strftime("%H:%M:%S" if "s" in fmt else "%H:%M")

    has_date = any(token in fmt for token in ("y", "d")) or "mmm" in fmt
    has_time = "h" in fmt or "s" in fmt

    if isinstance(value, _datetime.datetime):
        if has_time and not has_date:
            return value.strftime("%H:%M:%S" if "s" in fmt else "%H:%M")
        if has_time:
            return value.strftime("%Y-%m-%d %H:%M")
        # Midnight with a date-only format is a plain date, not a timestamp.
        return value.strftime("%Y-%m-%d")

    return value.strftime("%Y-%m-%d")


_CURRENCY_PATTERN = re.compile(r"[$£€¥₹]|\[\$[^\]]*\]")


def _format_number(value: float, number_format: str) -> str:
    fmt = number_format or ""

    if fmt in ("", "General", "@"):
        return _trim_float(value)

    is_percentage = "%" in fmt
    if is_percentage:
        value *= 100.0

    decimals = _decimal_places(fmt)
    grouped = "#,#" in fmt or "#,0" in fmt or "0,0" in fmt

    try:
        if grouped:
            text = f"{value:,.{decimals}f}"
        else:
            text = f"{value:.{decimals}f}"
    except (ValueError, OverflowError):
        return _trim_float(value)

    if is_percentage:
        return text + "%"

    currency = _CURRENCY_PATTERN.search(fmt)
    if currency:
        symbol = currency.group(0)
        if symbol.startswith("[$"):
            # [$€-407] and friends: the symbol is everything before the locale id.
            symbol = symbol[2:].split("-", 1)[0]
        if symbol:
            negative = text.startswith("-")
            return ("-" if negative else "") + symbol + (text[1:] if negative else text)

    return text


def _decimal_places(number_format: str) -> int:
    """Counts the decimal placeholders in the positive section of a format string."""
    positive = number_format.split(";", 1)[0]
    match = re.search(r"\.([0#]+)", positive)
    return len(match.group(1)) if match else 0


def _trim_float(value: float) -> str:
    if isinstance(value, int) or float(value).is_integer():
        return str(int(value))

    # repr-like precision without the exponent noise for ordinary magnitudes.
    return f"{value:.10g}"


# ── Grid helpers ──────────────────────────────────────────────────────────────


def _copy_box(box: CellBox | None) -> CellBox | None:
    if box is None:
        return None
    return CellBox(**vars(box))


def _normalise_row_lengths(sheet: SheetModel) -> None:
    """Pads short rows so every row has the same cell count.

    HTML tolerates ragged rows, but borders and column widths only line up when
    the grid is rectangular, and CSV in particular is routinely ragged.
    """
    width = sheet.column_count
    for row in sheet.rows:
        while len(row) < width:
            row.append(CellBox())


def _trim_trailing_blank_rows(sheet: SheetModel) -> None:
    """Drops empty rows from the end.

    max_row counts formatted-but-empty cells, so a sheet someone once styled to
    row 900 would otherwise print 800 blank rows.
    """
    while sheet.rows and all(
        box is None or not box.text for box in sheet.rows[-1]
    ):
        sheet.rows.pop()


# ── HTML rendering ────────────────────────────────────────────────────────────

_DOCUMENT_CSS = """
@page { size: A4 landscape; margin: 0.4in; }
* { box-sizing: border-box; }
body {
  margin: 0;
  font-family: Calibri, "Segoe UI", sans-serif;
  font-size: 10pt;
  color: #000;
  -webkit-print-color-adjust: exact;
  print-color-adjust: exact;
}
.sheet { page-break-after: always; }
.sheet:last-child { page-break-after: auto; }
.sheet > h2 {
  font-size: 11pt;
  font-weight: 600;
  margin: 0 0 6px;
  padding-bottom: 3px;
  border-bottom: 1px solid #999;
}
table { border-collapse: collapse; table-layout: fixed; }
td {
  border: 1px solid #d0d0d0;
  padding: 1px 4px;
  height: 18px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: bottom;
}
td.wrap { white-space: normal; word-break: break-word; }
.note { margin-top: 8px; font-size: 8pt; color: #666; font-style: italic; }
thead { display: table-header-group; }
tr { page-break-inside: avoid; }
"""


def _render_document(sheets: Sequence[SheetModel], source_name: str) -> str:
    title = html_module.escape(source_name or "Spreadsheet")
    body = "".join(_render_sheet(sheet, len(sheets) > 1) for sheet in sheets)

    return (
        "<!DOCTYPE html>"
        '<html><head><meta charset="utf-8">'
        f"<title>{title}</title>"
        f"<style>{_DOCUMENT_CSS}</style>"
        f"</head><body>{body}</body></html>"
    )


def _render_sheet(sheet: SheetModel, show_name: bool) -> str:
    widths = _effective_widths(sheet)
    total = sum(widths) or _PRINTABLE_WIDTH_PX

    # Chromium applies zoom during print layout, so scaling here is what makes a
    # wide sheet fit the page instead of being cut off at the right margin.
    zoom = min(1.0, _PRINTABLE_WIDTH_PX / total)
    zoom = max(zoom, _MIN_ZOOM)

    parts: list[str] = [f'<section class="sheet" style="zoom:{zoom:.4f}">']

    if show_name:
        parts.append(f"<h2>{html_module.escape(sheet.name)}</h2>")

    parts.append(f'<table style="width:{total:.0f}px">')
    parts.append("<colgroup>")
    for width in widths:
        parts.append(f'<col style="width:{width:.0f}px">')
    parts.append("</colgroup><tbody>")

    for row in sheet.rows:
        parts.append("<tr>")
        for box in row:
            if box is not None:
                parts.append(_render_cell(box))
        parts.append("</tr>")

    parts.append("</tbody></table>")

    note = _truncation_note(sheet)
    if note:
        parts.append(f'<p class="note">{note}</p>')

    parts.append("</section>")
    return "".join(parts)


def _effective_widths(sheet: SheetModel) -> list[float]:
    widths = list(sheet.column_widths)
    count = sheet.column_count

    while len(widths) < count:
        widths.append(_DEFAULT_COLUMN_PX)

    return [max(width, 4.0) for width in widths[:count]]


def _render_cell(box: CellBox) -> str:
    attributes: list[str] = []
    styles: list[str] = []

    if box.colspan > 1:
        attributes.append(f'colspan="{box.colspan}"')
    if box.rowspan > 1:
        attributes.append(f'rowspan="{box.rowspan}"')
    if box.wrap:
        attributes.append('class="wrap"')

    if box.align:
        styles.append(f"text-align:{box.align}")
    if box.bold:
        styles.append("font-weight:600")
    if box.italic:
        styles.append("font-style:italic")
    if box.underline:
        styles.append("text-decoration:underline")
    if box.background:
        styles.append(f"background:{box.background}")
    if box.colour:
        styles.append(f"color:{box.colour}")

    if styles:
        attributes.append('style="' + ";".join(styles) + '"')

    opening = "<td" + ("" if not attributes else " " + " ".join(attributes)) + ">"
    return opening + box.text + "</td>"


def _truncation_note(sheet: SheetModel) -> str:
    reasons: list[str] = []
    if sheet.truncated_rows:
        reasons.append(f"the first {MAX_ROWS_PER_SHEET:,} rows")
    if sheet.truncated_columns:
        reasons.append(f"the first {MAX_COLUMNS_PER_SHEET} columns")

    if not reasons:
        return ""

    return "This sheet was too large to render in full; only " + " and ".join(reasons) + " are shown."
