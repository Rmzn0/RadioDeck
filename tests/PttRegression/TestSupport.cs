using System.Diagnostics;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.Services.Audio;

/// <summary>Helpers shared by the regression files added with version 2.</summary>
internal static class T
{
    public static async Task Check(string name, Func<Task> test, int seconds = 12)
    {
        await test().WaitAsync(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"PASS {name}");
    }

    public static Task Check(string name, Action test) => Check(name, () =>
    {
        test();
        return Task.CompletedTask;
    });

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task Until(Func<bool> predicate, int milliseconds = 3000, string what = "condition")
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > milliseconds) throw new Exception($"Timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>A radio entry whose addresses are documentation addresses (RFC 5737) and never contacted.</summary>
    public static SiteConfig Radio(string id, string name, RadioLinkMode mode = RadioLinkMode.Separate,
        string profile = RadioProfile.BasicId) => new()
    {
        Id = id,
        Name = name,
        Profile = profile,
        LinkMode = mode.ToString(),
        RxHost = mode == RadioLinkMode.Transceiver ? "" : "192.0.2.10",
        TxHost = mode == RadioLinkMode.RxOnly ? "" : "192.0.2.11",
        RxUser = "RX_01",
        TxUser = "TX_01",
        SnmpEnabled = false // no SNMP request leaves the test process
    };

    public static AppConfig Config(params SiteConfig[] radios)
    {
        var config = new AppConfig { ConfigVersion = AppConfig.CurrentVersion };
        config.Radios.AddRange(radios);
        config.Audio.NoiseGateConfigured = true;
        return config;
    }
}

/// <summary>A radio engine whose calls succeed, fail or hang exactly as the test scripts it.</summary>
internal sealed class ScriptedRadio : ISipRadioEngine
{
    private volatile bool _rx, _tx, _sending;
    public int RxCalls, TxCalls, RxDisconnects, TxDisconnects;
    public string? RxFailure, TxFailure;
    public string? LastRxUri, LastTxUri;
    public TaskCompletionSource? Hold;
    public bool Disposed;

    public bool IsRxConnected => _rx;
    public bool IsTxConnected => _tx;
    public bool IsSendingToTx => _sending;
    public bool CanTransmit => _tx;

    public event Action<int>? RxLevelChanged { add { } remove { } }
    public event Action<int>? TxLevelChanged { add { } remove { } }
    public event Action<string>? Log { add { } remove { } }
    public event Action<string>? Error { add { } remove { } }
    public event Action? RxCallEnded;
    public event Action? TxCallEnded;
    public event Action<bool>? RxSquelchChanged;
    public event Action<bool>? TxConfirmedChanged;

    public async Task ConnectRxAsync(string targetUri, SiteConfig cfg, CancellationToken ct)
    {
        Interlocked.Increment(ref RxCalls);
        LastRxUri = targetUri;
        if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        if (RxFailure is { } failure) throw new RadioCallException(failure);
        _rx = true;
    }

    public async Task ConnectTxAsync(string targetUri, SiteConfig cfg, CancellationToken ct)
    {
        Interlocked.Increment(ref TxCalls);
        LastTxUri = targetUri;
        if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        if (TxFailure is { } failure) throw new RadioCallException(failure);
        _tx = true;
    }

    public Task DisconnectRxAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref RxDisconnects);
        _rx = false;
        return Task.CompletedTask;
    }

    public Task DisconnectTxAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref TxDisconnects);
        _tx = false;
        _sending = false;
        return Task.CompletedTask;
    }

    public Task StartTxAudioAsync(CancellationToken ct, AudioConfig? audio = null)
    {
        if (_tx) _sending = true;
        return Task.CompletedTask;
    }

    public Task StopTxAudioAsync(CancellationToken ct)
    {
        _sending = false;
        return Task.CompletedTask;
    }

    /// <summary>The radio (or the network) ends the receive call.</summary>
    public void DropRx()
    {
        _rx = false;
        RxCallEnded?.Invoke();
    }

    public void DropTx()
    {
        _tx = false;
        _sending = false;
        TxCallEnded?.Invoke();
    }

    public void ReportSquelch(bool open) => RxSquelchChanged?.Invoke(open);
    public void ReportKeyed(bool keyed) => TxConfirmedChanged?.Invoke(keyed);

    public void SetRxMuted(bool muted) { }
    public void SetRxVolume(int volumePercent, int pan) { }

    public void Dispose()
    {
        Disposed = true;
        _rx = _tx = _sending = false;
    }
}

/// <summary>Stands in for the sound card: the test decides which frames the "microphone" delivers.</summary>
internal sealed class TestMicrophone : IMicrophoneSource
{
    private volatile Action<byte[]>? _handler;
    public int Subscriptions;

    public bool IsSubscribed => _handler != null;

    public IDisposable Subscribe(Action<byte[]> onFrame)
    {
        Interlocked.Increment(ref Subscriptions);
        _handler = onFrame;
        return new Subscription(this);
    }

    public void Push(byte[] frame) => _handler?.Invoke(frame);

    private sealed class Subscription(TestMicrophone owner) : IDisposable
    {
        public void Dispose() => owner._handler = null;
    }
}
