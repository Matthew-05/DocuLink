using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace DocuLink.Addin.Modules.WebView
{
    /// <summary>
    /// Parses inbound file-manager→host JSON messages that conform to
    /// contracts/webview-messages-v1.json.
    /// </summary>
    internal static class FileManagerMessageParser
    {
        private static readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        /// <inheritdoc cref="WebMessageParser.GetMessageType"/>
        public static string GetMessageType(string json) =>
            WebMessageParser.GetMessageType(json);

        public static AddFilesRequest ParseAddFiles(string json)
        {
            var dict = Deserialize(json);
            var filesRaw = dict["files"] as System.Collections.ArrayList;
            if (filesRaw == null)
                throw new FormatException("add-files message missing 'files' array.");

            var files = new List<AddFileEntry>();
            foreach (var item in filesRaw)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null) continue;
                files.Add(new AddFileEntry
                {
                    Name     = GetString(entry, "name"),
                    Base64   = GetString(entry, "base64"),
                    FolderId = GetStringOrNull(entry, "folderId"),
                });
            }

            return new AddFilesRequest { Files = files };
        }

        public static RenameFileRequest ParseRenameFile(string json)
        {
            var dict = Deserialize(json);
            return new RenameFileRequest
            {
                Id      = GetString(dict, "id"),
                NewName = GetString(dict, "newName"),
            };
        }

        public static RemoveFileRequest ParseRemoveFile(string json)
        {
            var dict = Deserialize(json);
            return new RemoveFileRequest { Id = GetString(dict, "id") };
        }

        public static MoveFileRequest ParseMoveFile(string json)
        {
            var dict = Deserialize(json);
            return new MoveFileRequest
            {
                Id       = GetString(dict, "id"),
                FolderId = GetStringOrNull(dict, "folderId"),
            };
        }

        public static AddFolderRequest ParseAddFolder(string json)
        {
            var dict = Deserialize(json);
            return new AddFolderRequest { Name = GetString(dict, "name") };
        }

        public static RenameFolderRequest ParseRenameFolder(string json)
        {
            var dict = Deserialize(json);
            return new RenameFolderRequest
            {
                Id      = GetString(dict, "id"),
                NewName = GetString(dict, "newName"),
            };
        }

        public static RemoveFolderRequest ParseRemoveFolder(string json)
        {
            var dict = Deserialize(json);
            return new RemoveFolderRequest { Id = GetString(dict, "id") };
        }

        /// <returns>Folder GUID, or null when omitted (All Files / uncategorised).</returns>
        public static string ParseSetSelectedFolder(string json)
        {
            var dict = Deserialize(json);
            return GetStringOrNull(dict, "folderId");
        }

        private static Dictionary<string, object> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON must be non-empty.", nameof(json));

            var dict = _serializer.Deserialize<Dictionary<string, object>>(json);
            if (dict == null)
                throw new FormatException("Could not parse JSON object.");
            return dict;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out object val) && val is string s)
                return s;
            throw new FormatException($"Missing or non-string field '{key}'.");
        }

        private static string GetStringOrNull(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out object val) && val is string s && !string.IsNullOrEmpty(s))
                return s;
            return null;
        }
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    internal sealed class AddFileEntry
    {
        public string Name     { get; set; }
        public string Base64   { get; set; }
        public string FolderId { get; set; }
    }

    internal sealed class AddFilesRequest
    {
        public List<AddFileEntry> Files { get; set; }
    }

    internal sealed class RenameFileRequest
    {
        public string Id      { get; set; }
        public string NewName { get; set; }
    }

    internal sealed class RemoveFileRequest
    {
        public string Id { get; set; }
    }

    internal sealed class MoveFileRequest
    {
        public string Id       { get; set; }
        public string FolderId { get; set; }
    }

    internal sealed class AddFolderRequest
    {
        public string Name { get; set; }
    }

    internal sealed class RenameFolderRequest
    {
        public string Id      { get; set; }
        public string NewName { get; set; }
    }

    internal sealed class RemoveFolderRequest
    {
        public string Id { get; set; }
    }
}
