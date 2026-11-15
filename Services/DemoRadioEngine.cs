using RadioDeck.Models;

namespace RadioDeck.Services;

/// <summary>
/// Stand-in for a radio, used by the --demo mode: no network and no audio devices.
/// Calls "connect" after a moment and the receiver shows staggered bursts of activity.
/// </summary>
public sealed class DemoRadioEngine : ISipRadioEngine
{
    private readonly int _index;
    private readonly Timer _timer;
    private volatile bool _rx;
    private volatile bool _tx;
    private volatile bool _sending;
    private bool _receiving;
    private int _tick;

    public DemoRadioEngine(int index)
    {
        _index = index;
        _timer = new Timer(_ => Tick(), null, 100, 100);
    }

    public bool IsRxConnected => _rx;
    public bool IsTxConnected => _tx;
    public bool IsSendingToTx => _sending;
    public bool CanTransmit => _tx;

    public event Action<int>? RxLevelChanged;
    public event Action<int>? TxLevelChanged;
    public event Action<string>? Log;
    public event Action<string>? Error { add { } remove { } }
    public event Action? RxCallEnded { add { } remove { } }
    public event Action? TxCallEnded { add { } remove { } }
    public event Action<bool>? RxSquelchChanged;
    public event Action<bool>? TxConfirmedChanged;

    private void Tick()
    {
        var tick = Interlocked.Increment(ref _tick);
        var wave = (int)(18 * Math.Sin(tick * 0.9 + _index));

        // Two seconds of reception every eight seconds, staggered per radio.
        var receiving = (_rx || _tx) && !_sending && (tick / 10 + _index * 3) % 8 < 2;
        if (receiving != _receiving)
        {
            _receiving = receiving;
            RxSquelchChanged?.Invoke(receiving);
            if (!receiving) RxLevelChanged?.Invoke(0);
        }
        if (receiving) RxLevelChanged?.Invoke(45 + wave);
        if (_sending) TxLevelChanged?.Invoke(50 + wave);
    }

    public async Task ConnectRxAsync(string targetUri, SiteConfig cfg, CancellationToken ct)
    {
        await Task.Delay(350, ct).ConfigureAwait(false);
        _rx = true;
        Log?.Invoke($"[DEMO][RX] Connected to {targetUri}");
    }

    public async Task ConnectTxAsync(string targetUri, SiteConfig cfg, CancellationToken ct)
    {
        await Task.Delay(450, ct).ConfigureAwait(false);
        _tx = true;
        Log?.Invoke($"[DEMO][TX] Connected to {targetUri}");
    }

    public Task DisconnectRxAsync(CancellationToken ct)
    {
        _rx = false;
        return Task.CompletedTask;
    }

    public Task DisconnectTxAsync(CancellationToken ct)
    {
        _tx = false;
        return StopTxAudioAsync(ct);
    }

    public Task StartTxAudioAsync(CancellationToken ct, AudioConfig? audio = null)
    {
        if (!_tx || _sending) return Task.CompletedTask;
        _sending = true;
        TxConfirmedChanged?.Invoke(true);
        return Task.CompletedTask;
    }

    public Task StopTxAudioAsync(CancellationToken ct)
    {
        if (!_sending) return Task.CompletedTask;
        _sending = false;
        TxConfirmedChanged?.Invoke(false);
        TxLevelChanged?.Invoke(0);
        return Task.CompletedTask;
    }

    public void SetRxMuted(bool muted) { }
    public void SetRxVolume(int volumePercent, int pan) { }

    public void Dispose()
    {
        _timer.Dispose();
        _rx = _tx = _sending = false;
    }
}
