# Table structure goldens

Generate candidate `PageTables` JSON with:

```powershell
py scripts/score_tables.py test-imports.local/19-scanned-table-layouts.pdf `
  --write-candidates output/table-candidates
```

The scorer automatically uses direct single-block OCR on pages whose source text
geometry is empty or extremely sparse. Visually inspect each candidate against the rendered PDF before copying approved
`<pdf-stem>.page-<n>.json` files here. The scorer intentionally does not bless detector output
automatically, because doing so would make the detector its own oracle.

The current seed corpus covers a local ruled form plus ruled and whitespace-only
scanned table layouts. Header labels in goldens are visually transcribed when OCR
does not recover the body text; the scorer currently measures row and column bands.
