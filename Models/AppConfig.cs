using System.Text.Json.Serialization;

namespace RadioDeck.Models;

/// <summary>Everything persisted to %AppData%\RadioDeck\config.json.</summary>
public sealed class AppConfig
{
    public const int MaxRadios = 8;
    public const int CurrentVersion = 2;

    public int ConfigVersion { get; set; }

    /// <summary>1 to 8 radios, shown in this order.</summary>
    public List<SiteConfig> Radios { get; set; } = new();

    public PttConfig Ptt { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public ConnectionConfig Connection { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();

    // Early configuration files stored exactly two fixed sites. They are read once, converted
    // into Radios by ConfigMigrations and never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SiteConfig? Localizer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SiteConfig? Glidepath { get; set; }

    /// <summary>A ready-to-use configuration with the two example radios.</summary>
    public static AppConfig CreateDefault()
    {
        var config = new AppConfig { ConfigVersion = CurrentVersion };
        config.Radios.Add(new SiteConfig
        {
            Id = "LOC", Name = "LOCALIZER",
            // Placeholder addresses from the documentation range; replace them in Settings.
            RxHost = "192.0.2.11", TxHost = "192.0.2.12",
            RxUser = "rx1", TxUser = "tx1"
        });
        config.Radios.Add(new SiteConfig
        {
            Id = "GP", Name = "GLIDEPATH",
            RxHost = "192.0.2.21", TxHost = "192.0.2.22",
            RxUser = "rx2", TxUser = "tx2"
        });
        config.Audio.NoiseGateConfigured = true;
        return config;
    }
}

/// <summary>One radio (a receiver/transmitter pair or a transceiver).</summary>
public sealed class SiteConfig
{
    /// <summary>Stable identifier; survives renaming and re-ordering.</summary>
    public string Id { get; set; } = "";

    /// <summary>Name shown on the radio card. Free text.</summary>
    public string Name { get; set; } = "";

    /// <summary>Radio profile id, see <see cref="RadioProfile"/>.</summary>
    public string Profile { get; set; } = RadioProfile.BasicId;

    /// <summary>Separate | Transceiver | RxOnly, see <see cref="RadioLinkMode"/>.</summary>
    public string LinkMode { get; set; } = nameof(RadioLinkMode.Separate);

    // Kept for early configuration files: used when RxHost/TxHost are empty.
    public string SipHost { get; set; } = "";
    public int SipPort { get; set; } = 5060;

    public string RxHost { get; set; } = "";
    public string TxHost { get; set; } = "";

    /// <summary>Last known frequency in MHz (e.g. "123.450"). Empty when unknown.</summary>
    public string Frequency { get; set; } = "";

    public string RxUser { get; set; } = "";
    public string TxUser { get; set; } = "";

    public string? User { get; set; } // SIP digest user, only if the radio asks for it
    public string? Pass { get; set; }

    /// <summary>The global PTT key keys this radio.</summary>
    public bool TxSelected { get; set; } = true;

    /// <summary>Received audio is played on the PC.</summary>
    public bool RxMonitor { get; set; } = true;

    /// <summary>Playback volume in percent (0-200).</summary>
    public int RxVolume { get; set; } = 100;

    /// <summary>-100 = left ear only, 0 = both, 100 = right ear only.</summary>
    public int RxPan { get; set; }

    /// <summary>null = follow the profile default.</summary>
    public bool? SnmpEnabled { get; set; }
    public string SnmpCommunity { get; set; } = "public";
    public int SnmpPort { get; set; } = 161;

    /// <summary>Empty = profile default.</summary>
    public string SnmpFrequencyOid { get; set; } = "";

    /// <summary>Frequency object of the transmitter when it differs; empty = SnmpFrequencyOid.</summary>
    public string SnmpTxFrequencyOid { get; set; } = "";

    /// <summary>Addresses SNMP is sent to; empty = the SIP address of the receiver / transmitter.</summary>
    public string SnmpRxHost { get; set; } = "";
    public string SnmpTxHost { get; set; } = "";

    /// <summary>How the radio reports the frequency: kHz | Hz | MHzText.</summary>
    public string SnmpFrequencyUnit { get; set; } = "kHz";

    /// <summary>"2c" or "1".</summary>
    public string SnmpVersion { get; set; } = "2c";

    /// <summary>Empty = profile default (e.g. "radio.01").</summary>
    public string Wg67Version { get; set; } = "";

    [JsonIgnore]
    public string EffectiveRxHost => string.IsNullOrWhiteSpace(RxHost) ? SipHost : RxHost;

    [JsonIgnore]
    public string EffectiveTxHost => string.IsNullOrWhiteSpace(TxHost) ? SipHost : TxHost;

    public SiteConfig Clone() => (SiteConfig)MemberwiseClone();

    /// <summary>True when a change between the two requires the calls to be re-established.</summary>
    public static bool ConnectionDiffers(SiteConfig a, SiteConfig b) =>
        !string.Equals(a.Profile, b.Profile, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(a.LinkMode, b.LinkMode, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(a.EffectiveRxHost?.Trim(), b.EffectiveRxHost?.Trim(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(a.EffectiveTxHost?.Trim(), b.EffectiveTxHost?.Trim(), StringComparison.OrdinalIgnoreCase) ||
        a.SipPort != b.SipPort ||
        !string.Equals(a.RxUser?.Trim(), b.RxUser?.Trim(), StringComparison.Ordinal) ||
        !string.Equals(a.TxUser?.Trim(), b.TxUser?.Trim(), StringComparison.Ordinal) ||
        !string.Equals(a.User, b.User, StringComparison.Ordinal) ||
        !string.Equals(a.Pass, b.Pass, StringComparison.Ordinal) ||
        !string.Equals(a.Wg67Version?.Trim(), b.Wg67Version?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class PttConfig
{
    public const string FirstWins = "FIRST_WINS";
    public const string LastWins = "LAST_WINS";
    public const string PriorityFirst = "PRIORITY_FIRST";

    /// <summary>System.Windows.Input.Key name, e.g. "F9". Works while other applications have focus.</summary>
    public string PttKey { get; set; } = "F9";

    /// <summary>Hide the PTT key from other applications while this program runs.</summary>
    public bool SuppressKey { get; set; }

    public bool SingleTxAtATime { get; set; } = true;

    /// <summary>FIRST_WINS | LAST_WINS | PRIORITY_FIRST. Applied when Single TX is enabled.</summary>
    public string Arbitration { get; set; } = FirstWins;

    public bool MuteRxWhileTx { get; set; } = true;

    /// <summary>Longest continuous transmission in seconds; 0 disables the limit.</summary>
    public int MaxTxSeconds { get; set; } = 180;

    /// <summary>Maps legacy and unknown names onto the three supported modes.</summary>
    public static string NormalizeArbitration(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        LastWins => LastWins,
        PriorityFirst or "PRIORITY_LOC" => PriorityFirst,
        _ => FirstWins
    };
}

public sealed class AudioConfig
{
    public string Codec { get; set; } = "PCMA"; // G.711 A-law, the only codec ED-137 radios use here

    // TX noise gate: while PTT is ON but nobody speaks, send clean silence
    // instead of microphone hiss.
    public bool Vad { get; set; } = true;
    public bool NoiseGateConfigured { get; set; }

    public const int DefaultTxLeadMs = 200;
    public const int DefaultTxTailMs = 150;
    public const int MaxTxTimingMs = 1000;

    /// <summary>
    /// Time the transmitter is keyed before speech is passed on. Speech spoken during
    /// that time is delayed by the same amount, not lost.
    /// </summary>
    public int TxLeadMs { get; set; } = DefaultTxLeadMs;

    /// <summary>Time the transmitter stays keyed after the last speech was sent.</summary>
    public int TxTailMs { get; set; } = DefaultTxTailMs;

    /// <summary>Reduction of steady background noise in the microphone signal: 0 = off, 1 = normal, 2 = strong.</summary>
    public int NoiseReduction { get; set; }

    /// <summary>Device name as listed by Windows; null/empty = Windows default device.</summary>
    public string? InputDevice { get; set; }
    public string? OutputDevice { get; set; }
}

public sealed class UiConfig
{
    public const string Classic = "Classic";
    public const string Grid = "Grid";

    /// <summary>Classic (two tall cards) or Grid (square tiles, 4 per row, up to 8).</summary>
    public string Mode { get; set; } = Classic;

    public bool AlwaysOnTop { get; set; }

    /// <summary>Language code of the user interface (e.g. "tr"); empty = the language of Windows.</summary>
    public string Language { get; set; } = "";

    /// <summary>Optional text shown at the right of the footer.</summary>
    public string Organization { get; set; } = "";
}

public sealed class ConnectionConfig
{
    /// <summary>Re-establish a call that dropped while the operator wanted it connected.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Connect every radio when the program starts.</summary>
    public bool ConnectOnStartup { get; set; }
}

public sealed class SecurityConfig
{
    /// <summary>PBKDF2 salt/hash of the settings PIN (Base64). Empty = no PIN.</summary>
    public string PinSalt { get; set; } = "";
    public string PinHash { get; set; } = "";

    [JsonIgnore]
    public bool HasPin => !string.IsNullOrEmpty(PinSalt) && !string.IsNullOrEmpty(PinHash);
}
