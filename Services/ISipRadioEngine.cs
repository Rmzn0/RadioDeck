using RadioDeck.Models;

namespace RadioDeck.Services;

/// <summary>The SIP/RTP sessions of one radio: an optional receive leg and an optional transmit leg.</summary>
public interface ISipRadioEngine : IDisposable
{
    bool IsRxConnected { get; }
    bool IsTxConnected { get; }
    bool IsSendingToTx { get; }
    bool CanTransmit { get; }

    event Action<int>? RxLevelChanged;
    event Action<int>? TxLevelChanged;
    event Action<string>? Log;
    event Action<string>? Error;

    /// <summary>The call ended without a local disconnect (remote BYE, timeout, network loss).</summary>
    event Action? RxCallEnded;
    event Action? TxCallEnded;

    /// <summary>The receiver reports its squelch open/closed (ED-137 SQU bit).</summary>
    event Action<bool>? RxSquelchChanged;

    /// <summary>The transmitter reports that it is keyed (ED-137 PTT type echoed by the radio).</summary>
    event Action<bool>? TxConfirmedChanged;

    /// <exception cref="RadioCallException">The radio did not answer or rejected the call.</exception>
    Task ConnectRxAsync(string targetUri, SiteConfig cfg, CancellationToken ct);

    /// <exception cref="RadioCallException">The radio did not answer or rejected the call.</exception>
    Task ConnectTxAsync(string targetUri, SiteConfig cfg, CancellationToken ct);
    Task DisconnectRxAsync(CancellationToken ct);
    Task DisconnectTxAsync(CancellationToken ct);

    Task StartTxAudioAsync(CancellationToken ct, AudioConfig? audio = null);
    Task StopTxAudioAsync(CancellationToken ct);
    void SetRxMuted(bool muted);

    /// <param name="volumePercent">0-200.</param>
    /// <param name="pan">-100 (left) to 100 (right).</param>
    void SetRxVolume(int volumePercent, int pan);
}

/// <summary>A call attempt failed for a reason worth showing to the operator as is.</summary>
public sealed class RadioCallException(string message) : Exception(message);
