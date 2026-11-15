using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace RadioDeck.Services;

public sealed class GlobalKeyboardHook : IDisposable
{
    // Subscribers must only queue work; never run radio/audio operations in the hook.
    public event Action<bool>? KeyChanged;
    public event Action<string>? Log;

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private IntPtr _hookId;
    private HookProc? _proc;
    private int _virtualKey;
    private bool _suppress;
    private bool _pressed;
    private bool _waitForRelease;
    private long _lastTransitionMs;
    private long _unexpectedDownSinceMs;
    private int _disposed;

    /// <param name="suppress">Hide the key from every other application while the hook is installed.</param>
    public void Start(Key key, bool suppress = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_thread != null) return;
        _thread = new Thread(() => Run(key, suppress)) { IsBackground = true, Name = "Radio PTT keyboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Task.GetAwaiter().GetResult();
    }

    public void Configure(Key key, bool suppress = false)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _dispatcher?.BeginInvoke(() => ConfigureCore(key, suppress), DispatcherPriority.Send);
    }

    private void ConfigureCore(Key key, bool suppress)
    {
        Publish(false);
        _virtualKey = KeyInterop.VirtualKeyFromKey(key);
        _suppress = suppress;
        _unexpectedDownSinceMs = 0;
        // Changing the binding while a key is held must not start a transmission.
        _waitForRelease = IsPhysicalKeyDown();
    }

    private void Run(Key key, bool suppress)
    {
        DispatcherTimer? guard = null;
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            ConfigureCore(key, suppress);
            _proc = HookCallback;
            InstallHook();
            if (_hookId == IntPtr.Zero)
                Log?.Invoke($"[PTT][KEY] Hook unavailable: {new Win32Exception(Marshal.GetLastWin32Error()).Message}; using 20ms key polling.");
            else
                Log?.Invoke("[PTT][KEY] Global keyboard hook ready on dedicated thread; 20ms fallback polling.");

            guard = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            guard.Tick += (_, _) => ReconcilePhysicalState();
            guard.Start();
            _started.TrySetResult();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            Log?.Invoke($"[PTT][KEY][ERR] {ex.Message}");
        }
        finally
        {
            guard?.Stop();
            if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            Publish(false);
        }
    }

    private void InstallHook()
    {
        if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Marshal.ReadInt32(lParam) == _virtualKey)
        {
            int message = wParam.ToInt32();
            bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = message is WM_KEYUP or WM_SYSKEYUP;
            if (down || up)
            {
                // WH_KEYBOARD_LL runs BEFORE GetAsyncKeyState is updated. Trust the event.
                if (_waitForRelease)
                {
                    if (up) _waitForRelease = false;
                }
                else
                {
                    Publish(down);
                }
                // A non-zero result drops the keystroke before any application sees it.
                if (_suppress) return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool IsPhysicalKeyDown() => _virtualKey != 0 && (GetAsyncKeyState(_virtualKey) & 0x8000) != 0;

    private void ReconcilePhysicalState()
    {
        if (_suppress && _hookId != IntPtr.Zero)
        {
            ReconcileSuppressed();
            return;
        }

        // Allow Windows to finish updating its state after the hook returns.
        if (Environment.TickCount64 - _lastTransitionMs < 60) return;
        bool down = IsPhysicalKeyDown();
        if (_waitForRelease)
        {
            if (!down) _waitForRelease = false;
            return;
        }
        Publish(down);
    }

    // Swallowed keystrokes never reach the Windows key state table, so polling cannot
    // confirm a held key. It can still reveal a hook Windows removed silently: the key
    // then shows as down although the hook never reported it.
    private void ReconcileSuppressed()
    {
        if (_pressed || _waitForRelease || !IsPhysicalKeyDown())
        {
            _unexpectedDownSinceMs = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (_unexpectedDownSinceMs == 0) { _unexpectedDownSinceMs = now; return; }
        if (now - _unexpectedDownSinceMs < 100) return;

        _unexpectedDownSinceMs = 0;
        Log?.Invoke("[PTT][KEY][WARN] Keyboard hook stopped receiving events; re-installing it.");
        InstallHook();
        // The key is physically down but its release will now be seen by the fresh hook
        // (or by plain polling if the hook could not be re-installed).
        Publish(true);
    }

    private void Publish(bool down)
    {
        if (_pressed == down) return;
        _pressed = down;
        _lastTransitionMs = Environment.TickCount64;
        KeyChanged?.Invoke(down);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        if (_thread != Thread.CurrentThread) _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
