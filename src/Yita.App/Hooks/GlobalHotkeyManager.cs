using System.ComponentModel;
using System.Runtime.InteropServices;
using Yita.Interop;

namespace Yita.Hooks;

/// <summary>
/// Provides a keyboard-only, non-injecting fallback for applications whose
/// custom text renderer cannot expose a selection to accessibility APIs.
/// </summary>
internal sealed class GlobalHotkeyManager : IDisposable
{
    private const int TranslateClipboardHotkeyId = 0x4954;
    private const int TranslateSelectionHotkeyId = 0x4955;
    private const uint VirtualKeyT = 0x54;
    private const uint VirtualKeyD = 0x44;
    private readonly object _lifecycleSync = new();
    private readonly TaskCompletionSource<Exception?> _startup = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _messageThread;
    private uint _messageThreadId;
    private int _isSelectionHotkeyRegistered;
    private bool _disposed;

    public event Action? TranslateClipboardRequested;

    public event Action? TranslateSelectionRequested;

    public event Action? HotkeyStoppedUnexpectedly;

    public bool IsRunning => Volatile.Read(ref _messageThreadId) != 0;

    public bool IsSelectionHotkeyRegistered => Volatile.Read(ref _isSelectionHotkeyRegistered) != 0;

    public void Start()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_messageThread is not null)
            {
                return;
            }

            _messageThread = new Thread(MessageThreadMain)
            {
                IsBackground = true,
                Name = "Yita.GlobalHotkey",
            };
            _messageThread.Start();
        }

        if (!_startup.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            throw new TimeoutException("全局快捷键启动超时。");
        }

        if (_startup.Task.Result is { } startupException)
        {
            throw new InvalidOperationException(
                "无法注册 Ctrl+Shift+T，可能已被其他程序占用。",
                startupException);
        }
    }

    private void MessageThreadMain()
    {
        var isClipboardRegistered = false;
        var isSelectionRegistered = false;
        try
        {
            var threadId = NativeMethods.GetCurrentThreadId();
            // Force creation of the thread message queue before publishing its
            // id. A concurrent Dispose can then reliably enqueue WM_QUIT even
            // if registration has not started yet.
            _ = NativeMethods.PeekMessage(
                out _,
                IntPtr.Zero,
                0,
                0,
                NativeMethods.PmNoRemove);
            lock (_lifecycleSync)
            {
                _messageThreadId = threadId;
                if (_disposed)
                {
                    _startup.TrySetResult(null);
                    return;
                }
            }

            isClipboardRegistered = NativeMethods.RegisterHotKey(
                IntPtr.Zero,
                TranslateClipboardHotkeyId,
                NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
                VirtualKeyT);
            if (!isClipboardRegistered)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            isSelectionRegistered = NativeMethods.RegisterHotKey(
                IntPtr.Zero,
                TranslateSelectionHotkeyId,
                NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
                VirtualKeyD);
            // The selection hotkey is best-effort. If another application
            // already owns Ctrl+Shift+D, keep the clipboard hotkey alive and
            // expose the partial state instead of failing the whole manager.
            Volatile.Write(ref _isSelectionHotkeyRegistered, isSelectionRegistered ? 1 : 0);
        }
        catch (Exception exception)
        {
            _startup.TrySetResult(exception);
            return;
        }

        _startup.TrySetResult(null);
        try
        {
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Value != NativeMethods.WmHotKey)
                {
                    continue;
                }

                var hotkeyId = unchecked((int)message.WParam.ToUInt64());
                if (hotkeyId == TranslateClipboardHotkeyId)
                {
                    try
                    {
                        TranslateClipboardRequested?.Invoke();
                    }
                    catch (Exception)
                    {
                        // A subscriber failure must not terminate the native loop.
                    }
                }
                else if (hotkeyId == TranslateSelectionHotkeyId)
                {
                    try
                    {
                        TranslateSelectionRequested?.Invoke();
                    }
                    catch (Exception)
                    {
                        // A subscriber failure must not terminate the native loop.
                    }
                }
            }
        }
        finally
        {
            if (isClipboardRegistered)
            {
                NativeMethods.UnregisterHotKey(IntPtr.Zero, TranslateClipboardHotkeyId);
            }

            if (isSelectionRegistered)
            {
                NativeMethods.UnregisterHotKey(IntPtr.Zero, TranslateSelectionHotkeyId);
            }

            var notifyUnexpectedStop = false;
            lock (_lifecycleSync)
            {
                _messageThreadId = 0;
                _messageThread = null;
                notifyUnexpectedStop = !_disposed;
            }

            if (notifyUnexpectedStop)
            {
                try
                {
                    HotkeyStoppedUnexpectedly?.Invoke();
                }
                catch
                {
                    // A recovery subscriber must not terminate the native loop.
                }
            }
        }
    }

    public void Dispose()
    {
        Thread? messageThread;
        uint messageThreadId;
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            messageThread = _messageThread;
            messageThreadId = _messageThreadId;
        }

        if (messageThreadId != 0)
        {
            NativeMethods.PostThreadMessage(messageThreadId, NativeMethods.WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        messageThread?.Join(TimeSpan.FromSeconds(2));
    }
}
