using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.ViewModels;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Contains("--only-security"))
        {
            await SecurityRegression.Run();
            return;
        }
        if (args.Contains("--only-new"))
        {
            await SnmpLanguageRegression.Run();
            return;
        }
        if (args.Contains("--native-audio"))
        {
            // Device check only: opens the Windows default microphone for two seconds.
            await Check("Default microphone delivers cleaned 20 ms frames", AudioRegression.NativeMicrophone);
            return;
        }

        await Check("Background press/release without a UI message pump", async () =>
        {
            _ = Dispatcher.CurrentDispatcher;
            var loc = new FakeRadio();
            var gp = new FakeRadio();
            await using var ptt = new PttController(loc.Apply, gp.Apply);
            ptt.SetKeyboard(true);
            await Until(() => loc.IsSendingToTx && gp.IsSendingToTx);
            ptt.SetKeyboard(false);
            await Until(() => !loc.IsSendingToTx && !gp.IsSendingToTx);
        });
        await Check("Auto-repeat is ignored", async () =>
        {
            var loc = new FakeRadio();
            await using var ptt = new PttController(loc.Apply, _ => Task.CompletedTask);
            ptt.SetKeyboard(true);
            await Until(() => loc.IsSendingToTx);
            for (int i = 0; i < 100; i++) ptt.SetKeyboard(true);
            await Task.Delay(80);
            Require(loc.Starts == 1, "Repeated key down re-started TX");
        });
        await Check("Delayed press cannot start GP after release; shutdown waits for OFF", async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loc = new FakeRadio();
            var gp = new FakeRadio();
            var ptt = new PttController(async down =>
            {
                if (down) { started.TrySetResult(); await finish.Task; }
                await loc.Apply(down);
            }, gp.Apply);
            ptt.SetKeyboard(true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            ptt.SetKeyboard(false);
            var stopping = ptt.DisposeAsync().AsTask();
            finish.SetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!loc.IsSendingToTx && !gp.IsSendingToTx && gp.Starts == 0, "Stale ON replayed after release");
        });
        await Check("Mouse release preserves held keyboard; keyboard release preserves held mouse", async () =>
        {
            var loc = new FakeRadio();
            var gp = new FakeRadio();
            await using var ptt = new PttController(loc.Apply, gp.Apply);
            ptt.SetMouse(PttTargets.Glidepath);
            await Until(() => gp.IsSendingToTx);
            ptt.SetKeyboard(true);
            await Until(() => loc.IsSendingToTx);
            ptt.SetMouse(PttTargets.None);
            await Task.Delay(80);
            Require(loc.IsSendingToTx && gp.IsSendingToTx, "Lost mouse capture stopped held keyboard");
            ptt.SetMouse(PttTargets.Glidepath);
            ptt.SetKeyboard(false);
            await Until(() => !loc.IsSendingToTx && gp.IsSendingToTx);
            ptt.SetMouse(PttTargets.None);
            await Until(() => !gp.IsSendingToTx);
        });
        await Check("Settings capture requires a fresh press after close", async () =>
        {
            var loc = new FakeRadio();
            await using var ptt = new PttController(loc.Apply, _ => Task.CompletedTask);
            ptt.SetKeyboard(true);
            await Until(() => loc.IsSendingToTx);
            ptt.SetSuspended(true);
            await Until(() => !loc.IsSendingToTx);
            ptt.SetSuspended(false);
            ptt.SetKeyboard(true);
            await Task.Delay(80);
            Require(!loc.IsSendingToTx, "Held key resumed automatically");
            ptt.SetKeyboard(false);
            ptt.SetKeyboard(true);
            await Until(() => loc.IsSendingToTx);
            ptt.SetSuspended(true);
            ptt.SetKeyboard(false);
            ptt.SetKeyboard(true); // A new press while capturing the key must also be blocked.
            ptt.SetSuspended(false);
            await Until(() => !loc.IsSendingToTx);
        });
        await Check("Single-TX lock and independent target release", async () =>
        {
            var cfg = AppConfig.CreateDefault();
            var txLock = new TxLock();
            var locRadio = new FakeRadio();
            var gpRadio = new FakeRadio();
            using var loc = Site("LOC", locRadio, txLock, cfg);
            using var gp = Site("GP", gpRadio, txLock, cfg);
            await using var ptt = new PttController(loc.SetPttAsync, gp.SetPttAsync);
            ptt.SetKeyboard(true);
            await Until(() => loc.PttOn);
            Require(!gp.PttOn && txLock.Owner == "LOC", "Both sites acquired the TX lock");
            ptt.SetKeyboard(false);
            await Until(() => !loc.PttOn && txLock.Owner == null);
            ptt.SetMouse(PttTargets.Glidepath);
            await Until(() => gp.PttOn && txLock.Owner == "GP");
        });
        await Check("Failed start rolls back transmitter and releases TX lock", async () =>
        {
            var txLock = new TxLock();
            var radio = new FakeRadio { FailStart = true };
            using var site = Site("LOC", radio, txLock, AppConfig.CreateDefault());
            await MustThrow(() => site.SetPttAsync(true));
            Require(!radio.IsSendingToTx && !site.PttOn && txLock.Owner == null, "Failed start left TX locked");
            radio.FailStart = false;
            await site.SetPttAsync(true);
            Require(site.PttOn, "Next press could not recover");
        });
        await Check("Failed stop releases TX lock and UI state", async () =>
        {
            var txLock = new TxLock();
            var radio = new FakeRadio();
            using var site = Site("LOC", radio, txLock, AppConfig.CreateDefault());
            await site.SetPttAsync(true);
            radio.FailStop = true;
            await MustThrow(() => site.SetPttAsync(false));
            Require(!site.PttOn && txLock.Owner == null, "Failed stop left lock owned");
        });
        await Check("Disconnected TX does not report PTT ON", async () =>
        {
            using var site = Site("LOC", new FakeRadio { Connected = false }, new TxLock(), AppConfig.CreateDefault());
            await site.SetPttAsync(true);
            Require(!site.PttOn, "PTT indicated ON without a TX connection");
        });
        await Check("Start returning without transmission releases lock", async () =>
        {
            var txLock = new TxLock();
            using var site = Site("LOC", new FakeRadio { NoStart = true }, txLock, AppConfig.CreateDefault());
            await site.SetPttAsync(true);
            Require(!site.PttOn && txLock.Owner == null, "No-op start left lock owned");
        });
        await Check("Error in one target does not prevent releasing the other", async () =>
        {
            var gp = new FakeRadio();
            await using var ptt = new PttController(down => down ? throw new InvalidOperationException("test") : Task.CompletedTask, gp.Apply);
            ptt.SetKeyboard(true);
            await Until(() => gp.IsSendingToTx);
            ptt.SetKeyboard(false);
            await Until(() => !gp.IsSendingToTx);
        });
        await Check("Disposed site cannot re-key", async () =>
        {
            var radio = new FakeRadio();
            var txLock = new TxLock();
            var site = Site("LOC", radio, txLock, AppConfig.CreateDefault());
            await site.SetPttAsync(true);
            site.Dispose();
            await site.SetPttAsync(true);
            Require(radio.Disposed && !site.PttOn && txLock.Owner == null, "Dispose did not stop TX");
        });

        await RemainingRegression.Run();
        await MultiRadioRegression.Run();
        await ConfigRegression.Run();
        await AudioRegression.Run();
        await EngineRegression.Run();
        await CallLifecycleRegression.Run();
        await SnmpLanguageRegression.Run();
        await SecurityRegression.Run();
        if (args.Contains("--native-keyboard"))
            await Check("Native Windows hook while caller/UI thread is blocked (F24)", TestNativeKeyboard);
        Console.WriteLine("All PTT regression checks passed. No radio endpoints were contacted.");
    }

    internal static SiteViewModel Site(string id, ISipRadioEngine radio, TxLock txLock, AppConfig cfg)
    {
        // SiteViewModel reads the frequency over SNMP when an address is configured. Keep tests off the radio network.
        foreach (var site in cfg.Radios) site.RxHost = site.TxHost = site.SipHost = "";
        return new SiteViewModel(cfg.Radios.First(r => r.Id == id), radio, txLock, () => cfg, () => { });
    }

    private static async Task Check(string name, Func<Task> test)
    {
        await test().WaitAsync(TimeSpan.FromSeconds(8));
        Console.WriteLine($"PASS {name}");
    }

    private static async Task Until(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timeout.ElapsedMilliseconds > 2000) throw new Exception("Timed out waiting for PTT state");
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task MustThrow(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected operation to fail");
    }

    private static Task TestNativeKeyboard()
    {
        var events = new ConcurrentQueue<(bool Down, long Ms)>();
        var clock = Stopwatch.StartNew();
        using var hook = new GlobalKeyboardHook();
        hook.Log += Console.WriteLine;
        hook.KeyChanged += down => events.Enqueue((down, clock.ElapsedMilliseconds));
        hook.Start(Key.F24);
        var start = clock.ElapsedMilliseconds;
        try
        {
            SendKey(0x87, false);
            // Deliberately do not pump any dispatcher on the caller's thread.
            Thread.Sleep(350);
            SendKey(0x87, true);
            Thread.Sleep(150);
            var captured = events.ToArray();
            Require(captured.Length == 2 && captured[0].Down && !captured[1].Down,
                "Missing first press or release, or spurious repeated transitions");
            Require(captured[0].Ms - start < 200, "PTT waited for the caller/UI thread");
            Console.WriteLine($"Native hook press latency: {captured[0].Ms - start} ms");
        }
        finally { SendKey(0x87, true); }
        return Task.CompletedTask;
    }

    private static void SendKey(ushort key, bool up)
    {
        var input = new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = key, Flags = up ? 2u : 0u } } };
        Require(SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) == 1, "SendInput failed");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}

internal sealed class FakeRadio : ISipRadioEngine
{
    private volatile bool _sending;
    public bool Connected = true, FailStart, FailStop, NoStart, Disposed;
    public int Starts;
    public bool IsRxConnected => false;
    public bool IsTxConnected => Connected;
    public bool IsSendingToTx => _sending;
    public bool AllowsTx = true;
    public bool CanTransmit => Connected && AllowsTx;
    public bool RxMuted;
    public void SetRxMuted(bool muted) => RxMuted = muted;
    public int Volume = 100, Pan;
    public void SetRxVolume(int volumePercent, int pan) { Volume = volumePercent; Pan = pan; }
    public Task Apply(bool down) => down ? StartTxAudioAsync(default) : StopTxAudioAsync(default);
    public Task StartTxAudioAsync(CancellationToken ct, AudioConfig? audio = null)
    {
        if (NoStart) return Task.CompletedTask;
        Interlocked.Increment(ref Starts);
        _sending = true;
        if (FailStart) throw new InvalidOperationException("start failed");
        return Task.CompletedTask;
    }
    public Task StopTxAudioAsync(CancellationToken ct)
    {
        _sending = false;
        if (FailStop) throw new InvalidOperationException("stop failed");
        return Task.CompletedTask;
    }
    public void Dispose() { Disposed = true; _sending = false; }
    public Task ConnectRxAsync(string uri, SiteConfig cfg, CancellationToken ct) => Task.CompletedTask;
    public Task ConnectTxAsync(string uri, SiteConfig cfg, CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectRxAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectTxAsync(CancellationToken ct) => StopTxAudioAsync(ct);
    public event Action<int>? RxLevelChanged { add { } remove { } }
    public event Action<int>? TxLevelChanged { add { } remove { } }
    public event Action<string>? Log { add { } remove { } }
    public event Action<string>? Error { add { } remove { } }
    public event Action? RxCallEnded { add { } remove { } }
    public event Action? TxCallEnded { add { } remove { } }
    public event Action<bool>? RxSquelchChanged { add { } remove { } }
    public event Action<bool>? TxConfirmedChanged { add { } remove { } }
}
