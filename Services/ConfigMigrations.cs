using RadioDeck.Models;

namespace RadioDeck.Services;

/// <summary>
/// Brings any stored configuration (including early files with the fixed
/// Localizer/Glidepath pair) to the current shape. Deterministic and idempotent.
/// </summary>
public static class ConfigMigrations
{
    /// <returns>true when something was changed and the file should be rewritten.</returns>
    public static bool Normalize(AppConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var changed = false;

        if (cfg.Ptt is null) { cfg.Ptt = new PttConfig(); changed = true; }
        if (cfg.Audio is null) { cfg.Audio = new AudioConfig(); changed = true; }
        if (cfg.Ui is null) { cfg.Ui = new UiConfig(); changed = true; }
        if (cfg.Connection is null) { cfg.Connection = new ConnectionConfig(); changed = true; }
        if (cfg.Security is null) { cfg.Security = new SecurityConfig(); changed = true; }
        if (cfg.Radios is null) { cfg.Radios = new List<SiteConfig>(); changed = true; }

        // Fixed pair -> radio list: the two fixed sites become the first two radios.
        if (cfg.Localizer is not null || cfg.Glidepath is not null)
        {
            if (cfg.Radios.Count == 0)
            {
                if (cfg.Localizer is not null) cfg.Radios.Add(FromLegacy(cfg.Localizer, "LOC", "LOCALIZER"));
                if (cfg.Glidepath is not null) cfg.Radios.Add(FromLegacy(cfg.Glidepath, "GP", "GLIDEPATH"));
            }
            cfg.Localizer = null;
            cfg.Glidepath = null;
            changed = true;
        }

        cfg.Radios.RemoveAll(static r => r is null);
        if (cfg.Radios.Count == 0)
        {
            cfg.Radios.AddRange(AppConfig.CreateDefault().Radios);
            changed = true;
        }
        if (cfg.Radios.Count > AppConfig.MaxRadios)
        {
            cfg.Radios.RemoveRange(AppConfig.MaxRadios, cfg.Radios.Count - AppConfig.MaxRadios);
            changed = true;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cfg.Radios.Count; i++)
            changed |= NormalizeRadio(cfg.Radios[i], i, ids);

        changed |= NormalizePtt(cfg.Ptt);
        changed |= NormalizeAudio(cfg.Audio);

        if (cfg.Ui.Mode != UiConfig.Classic && cfg.Ui.Mode != UiConfig.Grid)
        {
            cfg.Ui.Mode = string.Equals(cfg.Ui.Mode, UiConfig.Grid, StringComparison.OrdinalIgnoreCase)
                ? UiConfig.Grid
                : UiConfig.Classic;
            changed = true;
        }
        if (cfg.Ui.Organization is null) { cfg.Ui.Organization = ""; changed = true; }
        if (cfg.Ui.Language is null) { cfg.Ui.Language = ""; changed = true; }
        if (cfg.Security.PinSalt is null) { cfg.Security.PinSalt = ""; changed = true; }
        if (cfg.Security.PinHash is null) { cfg.Security.PinHash = ""; changed = true; }

        if (cfg.ConfigVersion != AppConfig.CurrentVersion)
        {
            cfg.ConfigVersion = AppConfig.CurrentVersion;
            changed = true;
        }

        return changed;
    }

    private static SiteConfig FromLegacy(SiteConfig legacy, string id, string fallbackName)
    {
        var radio = legacy.Clone();
        radio.Id = id;
        // The fixed pair was always shown as LOCALIZER / GLIDEPATH regardless of the stored name.
        radio.Name = fallbackName;
        radio.Profile = RadioProfile.BasicId;
        radio.LinkMode = nameof(RadioLinkMode.Separate);
        // Early files stored 123.000 as a placeholder; it is not a real reading.
        if (radio.Frequency?.Trim() == "123.000") radio.Frequency = "";

        var defaults = AppConfig.CreateDefault().Radios.First(r => r.Id == id);
        if (string.IsNullOrWhiteSpace(radio.RxUser)) radio.RxUser = defaults.RxUser;
        if (string.IsNullOrWhiteSpace(radio.TxUser)) radio.TxUser = defaults.TxUser;
        if (string.IsNullOrWhiteSpace(radio.RxHost) && string.IsNullOrWhiteSpace(radio.SipHost)) radio.RxHost = defaults.RxHost;
        if (string.IsNullOrWhiteSpace(radio.TxHost) && string.IsNullOrWhiteSpace(radio.SipHost)) radio.TxHost = defaults.TxHost;
        return radio;
    }

    private static bool NormalizeRadio(SiteConfig radio, int index, HashSet<string> ids)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(radio.Id) || !ids.Add(radio.Id))
        {
            do { radio.Id = "R" + Guid.NewGuid().ToString("N")[..8]; }
            while (!ids.Add(radio.Id));
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(radio.Name)) { radio.Name = $"Radio {index + 1}"; changed = true; }
        if (radio.SipPort is < 1 or > 65535) { radio.SipPort = 5060; changed = true; }
        if (radio.SnmpPort is < 1 or > 65535) { radio.SnmpPort = 161; changed = true; }

        var profile = RadioProfile.Get(radio.Profile).Id;
        if (radio.Profile != profile) { radio.Profile = profile; changed = true; }

        var mode = RadioProfile.ParseLinkMode(radio.LinkMode).ToString();
        if (radio.LinkMode != mode) { radio.LinkMode = mode; changed = true; }

        var volume = Math.Clamp(radio.RxVolume, 0, 200);
        if (radio.RxVolume != volume) { radio.RxVolume = volume; changed = true; }
        var pan = Math.Clamp(radio.RxPan, -100, 100);
        if (radio.RxPan != pan) { radio.RxPan = pan; changed = true; }

        if (radio.SipHost is null) { radio.SipHost = ""; changed = true; }
        if (radio.RxHost is null) { radio.RxHost = ""; changed = true; }
        if (radio.TxHost is null) { radio.TxHost = ""; changed = true; }
        if (radio.RxUser is null) { radio.RxUser = ""; changed = true; }
        if (radio.TxUser is null) { radio.TxUser = ""; changed = true; }
        if (radio.Frequency is null) { radio.Frequency = ""; changed = true; }
        if (string.IsNullOrWhiteSpace(radio.SnmpCommunity)) { radio.SnmpCommunity = "public"; changed = true; }
        if (radio.SnmpFrequencyOid is null) { radio.SnmpFrequencyOid = ""; changed = true; }
        if (radio.SnmpTxFrequencyOid is null) { radio.SnmpTxFrequencyOid = ""; changed = true; }
        if (radio.SnmpRxHost is null) { radio.SnmpRxHost = ""; changed = true; }
        if (radio.SnmpTxHost is null) { radio.SnmpTxHost = ""; changed = true; }
        var unit = SnmpTarget.UnitName(SnmpTarget.ParseUnit(radio.SnmpFrequencyUnit));
        if (radio.SnmpFrequencyUnit != unit) { radio.SnmpFrequencyUnit = unit; changed = true; }
        var snmpVersion = SnmpTarget.NormalizeVersion(radio.SnmpVersion);
        if (radio.SnmpVersion != snmpVersion) { radio.SnmpVersion = snmpVersion; changed = true; }
        if (radio.Wg67Version is null) { radio.Wg67Version = ""; changed = true; }

        return changed;
    }

    private static bool NormalizePtt(PttConfig ptt)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(ptt.PttKey)) { ptt.PttKey = new PttConfig().PttKey; changed = true; }

        var arbitration = PttConfig.NormalizeArbitration(ptt.Arbitration);
        if (ptt.Arbitration != arbitration) { ptt.Arbitration = arbitration; changed = true; }

        var maxTx = Math.Clamp(ptt.MaxTxSeconds, 0, 3600);
        if (ptt.MaxTxSeconds != maxTx) { ptt.MaxTxSeconds = maxTx; changed = true; }

        return changed;
    }

    private static bool NormalizeAudio(AudioConfig audio)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(audio.Codec)) { audio.Codec = "PCMA"; changed = true; }

        // Early files had the noise gate hidden and effectively off. Enable it
        // once; a later explicit choice in Settings is preserved.
        if (!audio.NoiseGateConfigured)
        {
            audio.Vad = true;
            audio.NoiseGateConfigured = true;
            changed = true;
        }

        var lead = Math.Clamp(audio.TxLeadMs, 0, AudioConfig.MaxTxTimingMs);
        if (audio.TxLeadMs != lead) { audio.TxLeadMs = lead; changed = true; }
        var tail = Math.Clamp(audio.TxTailMs, 0, AudioConfig.MaxTxTimingMs);
        if (audio.TxTailMs != tail) { audio.TxTailMs = tail; changed = true; }
        var reduction = Math.Clamp(audio.NoiseReduction, 0, 2);
        if (audio.NoiseReduction != reduction) { audio.NoiseReduction = reduction; changed = true; }

        return changed;
    }
}
