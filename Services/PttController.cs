using System.Numerics;
using System.Threading.Channels;
using RadioDeck.Models;

namespace RadioDeck.Services;

/// <summary>One bit per radio slot (list order). Localizer/Glidepath are the first two slots.</summary>
[Flags]
public enum PttTargets
{
    None = 0,
    Localizer = 1,
    Glidepath = 2,
    Both = Localizer | Glidepath,
    All = 0xFF
}

/// <summary>
/// Combines held inputs (global key, mouse) for up to eight radios, applies Single-TX
/// arbitration and the transmit time limit, and serializes the radio work on its own
/// worker independently of the WPF dispatcher.
/// </summary>
public sealed class PttController : IAsyncDisposable
{
    public const int MaxSlots = AppConfig.MaxRadios;

    private readonly object _sync = new();
    private readonly Func<int, bool, Task> _apply;
    private readonly int _slots;
    private readonly PttTargets _allSlots;
    private readonly Func<PttConfig>? _getConfig;
    private readonly Func<PttTargets>? _connected;
    private readonly Func<PttTargets>? _transmitting;
    private readonly Func<PttTargets>? _keyboardTargets;
    private readonly long[] _press = new long[MaxSlots];
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false
    });
    private readonly Task _worker;
    private readonly Timer _limitTimer;
    private PttTargets _applied;
    private long _revision;
    private bool _keyDown;
    private PttTargets _mouse;
    private bool _suspended;
    private bool _waitForKeyUp;
    private bool _stopped;
    private bool _timedOut;
    private bool _limitArmed;

    public event Action<Exception>? Error;

    /// <summary>Raised once when the transmit time limit ends a transmission.</summary>
    public event Action? TimedOut;

    /// <summary>Two-radio form: slot 0 = localizer, slot 1 = glidepath.</summary>
    public PttController(Func<bool, Task> localizer, Func<bool, Task> glidepath,
        Func<PttConfig>? getConfig = null, Func<PttTargets>? connected = null,
        Func<PttTargets>? transmitting = null)
        : this(2, (slot, down) => slot == 0 ? localizer(down) : glidepath(down), getConfig, connected, transmitting)
    {
    }

    /// <param name="slots">Number of radio slots (1-8).</param>
    /// <param name="apply">Starts (true) or stops (false) transmission on a slot.</param>
    /// <param name="connected">Slots that can transmit right now; default all.</param>
    /// <param name="transmitting">Slots really transmitting; default what was applied.</param>
    /// <param name="keyboardTargets">Slots keyed by the keyboard; default all.</param>
    public PttController(int slots, Func<int, bool, Task> apply,
        Func<PttConfig>? getConfig = null, Func<PttTargets>? connected = null,
        Func<PttTargets>? transmitting = null, Func<PttTargets>? keyboardTargets = null)
    {
        if (slots is < 1 or > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slots));
        _slots = slots;
        _allSlots = (PttTargets)((1 << slots) - 1);
        _apply = apply;
        _getConfig = getConfig;
        _connected = connected;
        _transmitting = transmitting;
        _keyboardTargets = keyboardTargets;
        _limitTimer = new Timer(_ => OnLimitReached(), null, Timeout.Infinite, Timeout.Infinite);
        _worker = Task.Run(ProcessAsync);
    }

    public static PttTargets Slot(int index) => (PttTargets)(1 << index);

    public void SetKeyboard(bool down)
    {
        lock (_sync)
        {
            if (_stopped) return;
            if (!down) _waitForKeyUp = false;
            if (down && _suspended) _waitForKeyUp = true;
            bool desired = down && !_suspended && !_waitForKeyUp;
            if (desired == _keyDown) return;
            _keyDown = desired;
            if (desired) Array.Fill(_press, _revision + 1, 0, _slots);
            ClearTimeoutWhenReleased();
            Signal();
        }
    }

    public void SetMouse(PttTargets target)
    {
        lock (_sync)
        {
            if (_stopped) return;
            target = _suspended ? PttTargets.None : target & _allSlots;
            if (_mouse == target) return;
            var pressed = target & ~_mouse;
            for (var i = 0; i < _slots; i++)
                if ((pressed & Slot(i)) != 0) _press[i] = _revision + 1;
            _mouse = target;
            ClearTimeoutWhenReleased();
            Signal();
        }
    }

    public void SetSuspended(bool suspended)
    {
        lock (_sync)
        {
            if (_stopped) return;
            _suspended = suspended;
            if (suspended)
            {
                _waitForKeyUp = _keyDown || _waitForKeyUp;
                _keyDown = false;
                _mouse = PttTargets.None;
                _timedOut = false;
            }
            Signal();
        }
    }

    /// <summary>Re-evaluates the targets, e.g. after the TX selection or a connection changed.</summary>
    public void Refresh()
    {
        lock (_sync)
        {
            if (_stopped) return;
            Signal();
        }
    }

    // A transmission ended by the time limit stays off until every input was released.
    private void ClearTimeoutWhenReleased()
    {
        if (!_keyDown && _mouse == PttTargets.None) _timedOut = false;
    }

    private void Signal()
    {
        _revision++;
        _changes.Writer.TryWrite(true);
    }

    private (PttTargets Targets, long Revision) Desired()
    {
        lock (_sync)
        {
            if (_stopped || _suspended || _timedOut) return (PttTargets.None, _revision);

            var keyboard = _keyDown ? _keyboardTargets?.Invoke() ?? _allSlots : PttTargets.None;
            var requested = (keyboard | _mouse) & (_connected?.Invoke() ?? _allSlots) & _allSlots;
            var config = _getConfig?.Invoke();
            if (BitOperations.PopCount((uint)requested) <= 1 || config?.SingleTxAtATime != true)
                return (requested, _revision);

            var mode = PttConfig.NormalizeArbitration(config.Arbitration);
            if (mode == PttConfig.PriorityFirst)
                return (Lowest(requested), _revision);
            if (mode == PttConfig.LastWins)
                return (Pick(requested, latest: true), _revision);

            // FIRST_WINS: whoever already transmits keeps the channel.
            var active = (_transmitting?.Invoke() ?? _applied) & requested;
            if (BitOperations.PopCount((uint)active) == 1)
                return (active, _revision);
            return (Pick(requested, latest: false), _revision);
        }
    }

    private static PttTargets Lowest(PttTargets targets) => (PttTargets)((int)targets & -(int)targets);

    // Earliest or latest press among the requested slots; ties go to the lowest slot.
    private PttTargets Pick(PttTargets requested, bool latest)
    {
        var best = -1;
        for (var i = 0; i < _slots; i++)
        {
            if ((requested & Slot(i)) == 0) continue;
            if (best < 0 || (latest ? _press[i] > _press[best] : _press[i] < _press[best]))
                best = i;
        }
        return best < 0 ? PttTargets.None : Slot(best);
    }

    private async Task ProcessAsync()
    {
        await foreach (var _ in _changes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var desired = Desired();
            ArmLimit(desired.Targets != PttTargets.None);
            var stopped = true;
            // Always finish OFF before changing owners. Never replay a press superseded while awaiting.
            for (var i = 0; i < _slots; i++)
                if ((desired.Targets & Slot(i)) == 0)
                    stopped &= await ApplyAsync(i, false).ConfigureAwait(false);
            if (!stopped) continue;
            for (var i = 0; i < _slots; i++)
            {
                if (Desired().Revision != desired.Revision) break;
                if ((desired.Targets & Slot(i)) != 0)
                    await ApplyAsync(i, true).ConfigureAwait(false);
            }
        }
        _limitTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // The limit covers one continuous keying, regardless of which radio it lands on.
    private void ArmLimit(bool keyed)
    {
        lock (_sync)
        {
            if (!keyed)
            {
                if (_limitArmed) _limitTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _limitArmed = false;
                return;
            }
            if (_limitArmed) return;
            var seconds = _getConfig?.Invoke()?.MaxTxSeconds ?? 0;
            if (seconds <= 0) return;
            _limitArmed = true;
            _limitTimer.Change(TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnLimitReached()
    {
        lock (_sync)
        {
            if (_stopped || !_limitArmed) return;
            _limitArmed = false;
            if (!_keyDown && _mouse == PttTargets.None) return;
            _timedOut = true;
            Signal();
        }
        try { TimedOut?.Invoke(); } catch { }
    }

    private async Task<bool> ApplyAsync(int slot, bool down)
    {
        try
        {
            await _apply(slot, down).ConfigureAwait(false);
            _applied = down ? _applied | Slot(slot) : _applied & ~Slot(slot);
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (!_stopped)
            {
                _stopped = true;
                Signal();
                _changes.Writer.TryComplete();
            }
        }
        await _worker.ConfigureAwait(false);
        _limitTimer.Dispose();
    }
}
