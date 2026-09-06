using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;

namespace Talliark.Addin.Modules.Services
{
    /// <summary>
    /// One reversible link-rectangle creation.
    /// </summary>
    /// <remarks>
    /// <see cref="ExpectedFormula"/> is the cell's formula captured immediately after the
    /// creation wrote to it, and is how undo detects that the workbook has moved on. A
    /// mismatch means the user (or another tool) changed the cell since, so the entry is
    /// discarded rather than applied over their edit. Because the stack is last-in-first-out,
    /// a later rectangle appended to the same Sum cell is always undone first and restores
    /// the formula this entry expects — so the check stays accurate for Sum chains.
    /// </remarks>
    internal sealed class LinkCreationUndoEntry
    {
        public string RectId { get; set; }

        public string SheetName { get; set; }

        public string CellAddress { get; set; }

        public int TrackIndex { get; set; }

        /// <summary>Cell formula immediately after the creation, used as a staleness fingerprint.</summary>
        public string ExpectedFormula { get; set; }

        /// <summary>
        /// The cell's number format before the creation overwrote it, so undo restores what
        /// the user had rather than flattening the cell to General.
        /// </summary>
        public string PreviousNumberFormat { get; set; }
    }

    /// <summary>
    /// In-memory, per-workbook history of link-rectangle creations, newest first.
    /// </summary>
    /// <remarks>
    /// Deliberately not persisted to Custom XML: undo is a within-session convenience, and
    /// writing it to the workbook would version a shape that carries no value once the file
    /// is reopened. The stack lives beside the workbook's storage session and is dropped
    /// with it when the workbook closes.
    /// </remarks>
    internal sealed class LinkCreationUndoStack
    {
        /// <summary>
        /// Bounds memory on a long editing session. Undoing forty-odd rectangles by keystroke
        /// is already well past the point where the user would reach for the rectangle's own
        /// delete instead.
        /// </summary>
        private const int MaxDepth = 50;

        private readonly LinkedList<LinkCreationUndoEntry> _entries =
            new LinkedList<LinkCreationUndoEntry>();

        public int Count => _entries.Count;

        public bool IsEmpty => _entries.Count == 0;

        /// <summary>
        /// Whether a Talliark action is still this workbook's most recent, and so whether Ctrl+Z
        /// on its grid belongs to Talliark rather than Excel.
        /// </summary>
        /// <remarks>
        /// Tracked per workbook, not per Excel instance. A single global flag has two failure
        /// modes: a creation in one workbook arms the grid keystroke in another, where it is
        /// swallowed against an empty stack and eats the user's first Ctrl+Z; and an edit in one
        /// workbook cancels pending undo history in another.
        /// </remarks>
        public bool IsArmed { get; private set; }

        /// <summary>
        /// Claims the grid's Ctrl+Z for this workbook. Called for an undo as well as a creation —
        /// undoing is itself a Talliark action, and that is what lets repeated Ctrl+Z chain
        /// down the stack rather than stopping after the first.
        /// </summary>
        public void Arm() => IsArmed = !IsEmpty;

        /// <summary>Gives the grid's Ctrl+Z back to Excel, without discarding the history.</summary>
        public void Disarm() => IsArmed = false;

        public void Push(LinkCreationUndoEntry entry)
        {
            if (entry == null) return;

            _entries.AddFirst(entry);
            IsArmed = true;

            while (_entries.Count > MaxDepth)
                _entries.RemoveLast();
        }

        public bool TryPop(out LinkCreationUndoEntry entry)
        {
            entry = null;
            if (_entries.Count == 0) return false;

            entry = _entries.First.Value;
            _entries.RemoveFirst();
            return true;
        }

        public void Clear()
        {
            _entries.Clear();
            IsArmed = false;
        }

        /// <summary>
        /// Drops every entry for a rectangle that no longer exists, so callers can ask
        /// whether an undo is available without performing one.
        /// </summary>
        public void DropEntriesFor(IEnumerable<string> rectIds)
        {
            if (rectIds == null) return;

            var removed = new HashSet<string>(rectIds, StringComparer.Ordinal);
            if (removed.Count == 0) return;

            LinkedListNode<LinkCreationUndoEntry> node = _entries.First;
            while (node != null)
            {
                LinkedListNode<LinkCreationUndoEntry> next = node.Next;
                if (removed.Contains(node.Value.RectId))
                    _entries.Remove(node);
                node = next;
            }
        }

        /// <summary>
        /// Builds an entry from a cell that has just been written to. Returns <c>null</c>
        /// when Excel cannot be read, so a failed snapshot costs an undo rather than the
        /// creation itself.
        /// </summary>
        public static LinkCreationUndoEntry TryCapture(
            string rectId, int trackIndex, Excel.Range cell, string previousNumberFormat)
        {
            if (string.IsNullOrEmpty(rectId) || cell == null) return null;

            try
            {
                return new LinkCreationUndoEntry
                {
                    RectId               = rectId,
                    TrackIndex           = trackIndex,
                    SheetName            = ((Excel.Worksheet)cell.Worksheet).Name,
                    CellAddress          = cell.Address,
                    ExpectedFormula      = cell.Formula as string,
                    PreviousNumberFormat = previousNumberFormat,
                };
            }
            catch (COMException ex)
            {
                TalliarkLog.Trace($"LinkCreationUndoStack.TryCapture failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Reads a cell's number format, or <c>null</c> when Excel refuses.</summary>
        public static string TryReadNumberFormat(Excel.Range cell)
        {
            try { return cell?.NumberFormat as string; }
            catch (COMException) { return null; }
        }
    }
}
