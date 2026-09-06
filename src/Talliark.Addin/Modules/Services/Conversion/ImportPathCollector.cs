using System;
using System.Collections.Generic;
using System.IO;

namespace Talliark.Addin.Modules.Services.Conversion
{
    /// <summary>Expands a directory into the document candidates Talliark can import.</summary>
    internal static class ImportPathCollector
    {
        public static IList<ImportCandidate> CollectDirectory(string directoryPath, string folderId = null)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
                throw new ArgumentException("Directory path must be non-empty.", nameof(directoryPath));

            var candidates = new List<ImportCandidate>();
            foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(filePath);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    continue;
                }

                if (!ConversionFormatCatalog.IsPdf(fullPath)
                    && !ConversionFormatCatalog.TryGetFormat(fullPath, out _))
                    continue;

                candidates.Add(new ImportCandidate
                {
                    Path = fullPath,
                    Name = Path.GetFileName(fullPath),
                    FolderId = folderId,
                });
            }

            return candidates;
        }
    }
}
