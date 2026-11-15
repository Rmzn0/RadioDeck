using RadioDeck.Services.Localization;

namespace RadioDeck.Models;

/// <summary>
/// Signalling differences between radio families. All of them speak ED-137 style
/// SIP/RTP with G.711 A-law and R2S keepalives; the profile selects the SIP headers,
/// the SDP attributes and the PTT bits of the RTP header extension.
/// </summary>
public sealed class RadioProfile
{
    public const string BasicId = "basic";
    public const string Ed137BId = "ed137b";
    public const string Ed137CId = "ed137c";

    /// <summary>SNMP object of the operating frequency (integer, kHz) used by the basic profile.</summary>
    public const string DefaultFrequencyOid = "1.3.6.1.4.1.22154.3.1.2.3.4.0";

    public string Id { get; private init; } = "";

    /// <summary>Name shown in lists, in the language of the user interface.</summary>
    public string DisplayName => Loc.T("Profile_" + Id);

    /// <summary>Verified against real hardware by the author.</summary>
    public bool Tested { get; private init; }

    /// <summary>WG67-Version header value; null = header not sent.</summary>
    public string? Wg67Version { get; private init; }

    /// <summary>Standard ED-137 offer: PT 123 "R2S" in the media line plus type/txrxmode/... attributes.</summary>
    public bool StandardSdp { get; private init; }

    /// <summary>"Frequency + mode" subject instead of the standard "radio".</summary>
    public bool FrequencySubject { get; private init; }

    /// <summary>
    /// False: keepalives keep the PTT type at 0 and only carry the PTT-ID while transmitting.
    /// True: every packet sent during a transmission carries PTT ON, as the standard describes.
    /// </summary>
    public bool KeepAliveCarriesPttType { get; private init; }

    /// <summary>Use the PTT-ID the radio assigns in its SDP answer (a=ptt-id).</summary>
    public bool UseAssignedPttId { get; private init; }

    /// <summary>Default SNMP frequency OID; null = no frequency control by default.</summary>
    public string? SnmpFrequencyOid { get; private init; }

    public static RadioProfile Basic { get; } = new()
    {
        Id = BasicId,
        Tested = true,
        Wg67Version = null,
        StandardSdp = false,
        FrequencySubject = true,
        KeepAliveCarriesPttType = false,
        UseAssignedPttId = false,
        SnmpFrequencyOid = DefaultFrequencyOid
    };

    public static RadioProfile Ed137B { get; } = new()
    {
        Id = Ed137BId,
        Wg67Version = "radio.01",
        StandardSdp = true,
        KeepAliveCarriesPttType = true,
        UseAssignedPttId = true
    };

    public static RadioProfile Ed137C { get; } = new()
    {
        Id = Ed137CId,
        Wg67Version = "radio.02",
        StandardSdp = true,
        KeepAliveCarriesPttType = true,
        UseAssignedPttId = true
    };

    public static IReadOnlyList<RadioProfile> All { get; } = new[] { Basic, Ed137B, Ed137C };

    /// <summary>Unknown ids fall back to the basic profile, which is also what files without a profile get.</summary>
    public static RadioProfile Get(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Basic;

    public static RadioLinkMode ParseLinkMode(string? value) =>
        Enum.TryParse<RadioLinkMode>(value?.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : RadioLinkMode.Separate;

    public override string ToString() => DisplayName;
}

/// <summary>Per-radio values resolved from the profile and the user's overrides.</summary>
public static class SiteConfigExtensions
{
    public static RadioProfile GetProfile(this SiteConfig site) => RadioProfile.Get(site.Profile);

    public static RadioLinkMode GetLinkMode(this SiteConfig site) => RadioProfile.ParseLinkMode(site.LinkMode);

    public static string? GetWg67Version(this SiteConfig site) =>
        string.IsNullOrWhiteSpace(site.Wg67Version) ? site.GetProfile().Wg67Version : site.Wg67Version.Trim();

    public static string? GetSnmpFrequencyOid(this SiteConfig site) =>
        string.IsNullOrWhiteSpace(site.SnmpFrequencyOid) ? site.GetProfile().SnmpFrequencyOid : site.SnmpFrequencyOid.Trim();

    /// <summary>Frequency read/write over SNMP is available for this radio.</summary>
    public static bool HasFrequencyControl(this SiteConfig site) =>
        (site.SnmpEnabled ?? site.GetProfile().SnmpFrequencyOid != null) &&
        (!string.IsNullOrWhiteSpace(site.GetSnmpFrequencyOid()) ||
         (site.UsesTxLeg() && !string.IsNullOrWhiteSpace(site.SnmpTxFrequencyOid)));

    public static bool UsesRxLeg(this SiteConfig site) => site.GetLinkMode() != RadioLinkMode.Transceiver;

    public static bool UsesTxLeg(this SiteConfig site) => site.GetLinkMode() != RadioLinkMode.RxOnly;
}
