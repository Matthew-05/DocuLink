using System;
using System.Collections.Generic;
using System.Linq;
using DocuLink.Addin.Modules.CustomXml.Models;
using Excel = Microsoft.Office.Interop.Excel;

namespace DocuLink.Addin.Modules.CustomXml
{
    /// <summary>
    /// Per-workbook in-memory cache for linked rectangles. Avoids reloading the
    /// links Custom XML part on hot paths such as link creation and cell selection.
    /// </summary>
    internal sealed class WorkbookStorageSession
    {
        private readonly Excel.Workbook _workbook;
        private readonly DocuLinkCustomXmlPartStore _store;
        private List<LinkedRectangle> _linksCache;
        private bool _linksLoaded;

        internal WorkbookStorageSession(Excel.Workbook workbook)
        {
            _workbook = workbook ?? throw new ArgumentNullException(nameof(workbook));
            _store = new DocuLinkCustomXmlPartStore(workbook);
        }

        internal Excel.Workbook Workbook => _workbook;

        internal IList<LinkedRectangle> GetLinks()
        {
            EnsureLinksLoaded();
            return _linksCache;
        }

        internal bool TryGetLink(string id, out LinkedRectangle linkedRectangle)
        {
            EnsureLinksLoaded();
            linkedRectangle = _linksCache.FirstOrDefault(
                r => string.Equals(r.Id, id, StringComparison.Ordinal));
            return linkedRectangle != null;
        }

        internal void SetLinks(IList<LinkedRectangle> links)
        {
            _linksCache = links != null
                ? new List<LinkedRectangle>(links)
                : new List<LinkedRectangle>();
            _linksLoaded = true;
            _store.SaveLinks(_linksCache);
        }

        internal void AddLink(LinkedRectangle linkedRectangle)
        {
            if (linkedRectangle == null) throw new ArgumentNullException(nameof(linkedRectangle));

            EnsureLinksLoaded();
            _linksCache.Add(linkedRectangle);
            _store.SaveLinks(_linksCache);
        }

        internal bool UpdateLink(LinkedRectangle linkedRectangle)
        {
            if (linkedRectangle == null) throw new ArgumentNullException(nameof(linkedRectangle));

            EnsureLinksLoaded();
            int index = _linksCache.FindIndex(
                r => string.Equals(r.Id, linkedRectangle.Id, StringComparison.Ordinal));
            if (index < 0)
                return false;

            _linksCache[index] = linkedRectangle;
            _store.SaveLinks(_linksCache);
            return true;
        }

        internal bool RemoveLink(string id)
        {
            EnsureLinksLoaded();
            int before = _linksCache.Count;
            _linksCache = _linksCache
                .Where(r => !string.Equals(r.Id, id, StringComparison.Ordinal))
                .ToList();
            if (_linksCache.Count == before)
                return false;

            _store.SaveLinks(_linksCache);
            return true;
        }

        internal void InvalidateLinks()
        {
            _linksLoaded = false;
            _linksCache = null;
        }

        internal DocuLinkCustomXmlPartStore Store => _store;

        private void EnsureLinksLoaded()
        {
            if (_linksLoaded)
                return;

            _linksCache = _store.LoadLinks().ToList();
            _linksLoaded = true;
        }
    }
}
