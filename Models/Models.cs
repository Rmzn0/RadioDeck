namespace RadioDeck.Models;

public enum RadioState
{
    Disconnected,
    Connecting,
    Connected,
    Partial,
    Error
}

/// <summary>How the program talks to one radio.</summary>
public enum RadioLinkMode
{
    /// <summary>A receiver and a transmitter with their own SIP sessions.</summary>
    Separate,

    /// <summary>One combined TxRx session; receive audio arrives on the same session.</summary>
    Transceiver,

    /// <summary>Receive only; PTT is not available.</summary>
    RxOnly
}
