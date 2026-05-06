import * as pdfjsLib from "pdfjs-dist";

pdfjsLib.GlobalWorkerOptions.workerSrc = "pdf.worker.min.mjs";

/**
 * Loads a PDF document from the given URL and returns the pdf.js proxy.
 * Destroys any previously held proxy before loading so callers don't need to
 * track it themselves.
 */
export async function loadPdfDocument(
  url: string,
  previous: pdfjsLib.PDFDocumentProxy | null
): Promise<pdfjsLib.PDFDocumentProxy> {
  previous?.destroy();
  const task = pdfjsLib.getDocument(url);
  return task.promise;
}
