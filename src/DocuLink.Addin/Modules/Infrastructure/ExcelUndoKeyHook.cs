using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DocuLink.Addin.Modules.Infrastructure
{
    /// <summary>
    /// Routes Ctrl+Z pressed on the Excel worksheet grid to DocuLink's own undo, but only
    /// while the most recent action was a link-rectangle creation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thread-local keyboard hook is used rather than <c>Application.OnUndo</c> because
    /// OnUndo resolves its argument as a VBA macro name. A VSTO add-in has no VBA project to
    /// register one in, so the call binds to nothing and the handler never fires. The hook
    /// reaches the same outcome by the only route available in-process, and mirrors the
    /// mouse hook <c>ExcelGridFocusRestoreService</c> already installs on this thread.
    /// </para>
    /// <para>
    /// Two conditions gate every keystroke, so Excel's native undo is never quietly stolen:
    /// the hook must be armed — set when a rectangle is created, cleared by any subsequent
    /// worksheet edit — and the focused window must be the grid itself, which keeps Ctrl+Z
    /// working normally in the formula bar, dialogs and the WebView.
    /// </para>
    /// </remarks>
    internal sealed class ExcelUndoKeyHook : IDisposable
    {
        private const int WH_KEYBOARD = 2;
        private const int HC_ACTION = 0;
        private const int VK_Z = 0x5A;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;
        private const int KeyPressedMask = 0x8000;

        /// <summary>lParam bit 31 is set when the key is being released.</summary>
        private const uint TransitionStateMask = 0x80000000;

        private const string ExcelGridClassName = "EXCEL7";

        private readonly KeyboardHookProc _hookProc;
        private readonly Action _onUndoRequested;
        private readonly Control _invokeTarget;

        private IntPtr _hookHandle;
        private bool _armed;
        private bool _disposed;

        /// <param name="onUndoRequested">
        /// Raised on the UI thread when an armed Ctrl+Z is consumed. Never called from inside
        /// the hook itself — COM work in a hook procedure risks re-entering Excel mid-message.
        /// </param>
        internal ExcelUndoKeyHook(Action onUndoRequested)
        {
            _onUndoRequested = onUndoRequested ?? throw new ArgumentNullException(nameof(onUndoRequested));

            // Touching Handle forces creation on the Excel UI thread, which is what makes
            // BeginInvoke a valid way back out of the hook. CreateControl() would not: it
            // no-ops on a control that has no parent and is not visible.
            _invokeTarget = new Control();
            _ = _invokeTarget.Handle;

            _hookProc = HandleKeyMessage;
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD, _hookProc, IntPtr.Zero, GetCurrentThreadId());
        }

        /// <summary>
        /// Whether the next Ctrl+Z on the grid belongs to DocuLink. Set after a rectangle is
        /// created or undone; cleared by any worksheet edit the user makes in between.
        /// </summary>
        internal bool IsArmed => _armed;

        internal void Arm() => _armed = true;

        internal void Disarm() => _armed = false;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            _invokeTarget.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc lpfn, IntPtr hMod, int dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        private delegate IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        private IntPtr HandleKeyMessage(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (!_disposed && nCode == HC_ACTION && ShouldConsume(wParam, lParam))
            {
                RequestUndo();

                // A non-zero return swallows the keystroke, so Excel never sees the Ctrl+Z
                // and cannot run its own undo on top of ours.
                return new IntPtr(1);
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private bool ShouldConsume(IntPtr wParam, IntPtr lParam)
        {
            if (!_armed) return false;
            if (wParam.ToInt32() != VK_Z) return false;

            // Key-up and auto-repeat both arrive here; only act on the initial press.
            if (((uint)lParam.ToInt64() & TransitionStateMask) != 0) return false;

            if (!IsKeyDown(VK_CONTROL)) return false;

            // Ctrl+Shift+Z is redo and Alt+Ctrl+Z is not ours; both pass through untouched.
            if (IsKeyDown(VK_SHIFT) || IsKeyDown(VK_MENU)) return false;

            return IsExcelGridFocused();
        }

        private void RequestUndo()
        {
            try
            {
                if (_invokeTarget.IsDisposed || !_invokeTarget.IsHandleCreated) return;
                _invokeTarget.BeginInvoke(_onUndoRequested);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DocuLink] ExcelUndoKeyHook dispatch failed: {ex.Message}");
            }
        }

        private static bool IsKeyDown(int virtualKey) => (GetKeyState(virtualKey) & KeyPressedMask) != 0;

        /// <summary>
        /// True only when the worksheet grid itself holds focus. The formula bar, name box,
        /// task pane and any dialog all report a different class, so their Ctrl+Z is left alone.
        /// </summary>
        private static bool IsExcelGridFocused()
        {
            IntPtr focused = GetFocus();
            if (focused == IntPtr.Zero) return false;

            var className = new StringBuilder(256);
            if (GetClassName(focused, className, className.Capacity) <= 0) return false;

            return string.Equals(className.ToString(), ExcelGridClassName, StringComparison.Ordinal);
        }
    }
}
