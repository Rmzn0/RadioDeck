using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.Services.Localization;
using RadioDeck.ViewModels;
using static T;

/// <summary>Frequency control over SNMP (against an agent on the loopback interface) and the user interface languages.</summary>
internal static class SnmpLanguageRegression
{
    private const string Oid = "1.3.6.1.4.1.99999.1.2.0";
    private const string TxOid = "1.3.6.1.4.1.99999.7.300.0";

    public static async Task Run()
    {
        await Check("SNMP: frequency as kHz, Hz or text is read and written (loopback agent)", Units);
        await Check("SNMP: version 1 or 2c and the community are sent as configured", VersionAndCommunity);
        await Check("SNMP: transmitter with its own object and its own SNMP address", SeparateTargets);
        await Check("SNMP: missing object, unusable answer and silence are reported, never shown as a frequency", Failures);
        await Check("SNMP: the radio card shows the frequency read from the radio and writes a new one", CardFrequency);
        await Check("Settings: SNMP options are validated, stored and tried with 'Read now'", SnmpSettings);
        await Check("Config: SNMP options and the language get defaults and canonical spellings", NormalizeNewFields);
        await Check("Languages: every file has all texts, the same placeholders and fits its button", LanguageFiles);
        await Check("Languages: switching re-words the program, unknown texts fall back to English", SwitchLanguage);
    }

    private static SiteConfig SnmpRadio(SnmpAgent agent, string unit = SnmpTarget.UnitKhz) => new()
    {
        Id = "S1",
        Name = "TOWER",
        Profile = RadioProfile.Ed137BId,
        LinkMode = nameof(RadioLinkMode.Transceiver),
        TxHost = agent.Host,
        TxUser = "TRX",
        SnmpEnabled = true,
        SnmpPort = agent.Port,
        SnmpFrequencyOid = Oid,
        SnmpFrequencyUnit = unit
    };

    private static async Task Units()
    {
        var client = new SnmpFrequencyClient();
        var cases = new (string Unit, byte Tag, byte[] Stored, byte WrittenTag, string Written)[]
        {
            (SnmpTarget.UnitKhz, 0x02, Ber.IntegerBytes(118100), 0x02, "121500"),
            (SnmpTarget.UnitKhz, 0x42, Ber.IntegerBytes(118100), 0x02, "121500"), // Gauge32 / Unsigned32
            (SnmpTarget.UnitHz, 0x02, Ber.IntegerBytes(118100000), 0x02, "121500000"),
            (SnmpTarget.UnitMhzText, 0x04, Encoding.ASCII.GetBytes("118.100"), 0x04, "121.500"),
            (SnmpTarget.UnitMhzText, 0x04, Encoding.ASCII.GetBytes(" 118,1 \0"), 0x04, "121.500")
        };

        foreach (var (unit, tag, stored, writtenTag, written) in cases)
        {
            using var agent = new SnmpAgent();
            agent.Values[Oid] = (tag, stored);
            var target = SnmpTarget.For(SnmpRadio(agent, unit));

            var read = await client.GetFrequencyAsync(target, CancellationToken.None);
            Require(read.Success && read.FrequencyKhz == 118100, $"{unit}/0x{tag:X2}: read {read.FrequencyKhz} ({read.Message})");

            var set = await client.SetFrequencyAsync(target, 121500, CancellationToken.None);
            Require(set.Count == 1 && set[0].Success, $"{unit}: write failed ({set[0].Message})");
            var (newTag, newValue) = agent.Values[Oid];
            var text = newTag == 0x04 ? Encoding.ASCII.GetString(newValue) : Ber.Integer(newValue).ToString();
            Require(newTag == writtenTag && text == written, $"{unit}: the radio received 0x{newTag:X2} '{text}'");

            read = await client.GetFrequencyAsync(target, CancellationToken.None);
            Require(read.Success && read.FrequencyKhz == 121500, $"{unit}: read back {read.FrequencyKhz}");
        }
    }

    private static async Task VersionAndCommunity()
    {
        using var agent = new SnmpAgent();
        agent.Values[Oid] = (0x02, Ber.IntegerBytes(118100));
        var client = new SnmpFrequencyClient();

        var site = SnmpRadio(agent);
        var result = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(result.Success && agent.LastVersion == 1 && agent.LastCommunity == "public", "Default must be v2c with community public");

        site.SnmpVersion = SnmpTarget.VersionOne;
        site.SnmpCommunity = " radio-rw ";
        result = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(result.Success && agent.LastVersion == 0 && agent.LastCommunity == "radio-rw",
            $"Version 1 / community not sent: version {agent.LastVersion}, '{agent.LastCommunity}'");

        // A version 1 agent reports an unknown object with noSuchName instead of an exception value.
        agent.Values.Clear();
        result = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(!result.Success && result.Message.Contains("noSuchName"), $"noSuchName not reported: {result.Message}");
    }

    private static async Task SeparateTargets()
    {
        // Two units on two loopback addresses, the transmitter with its own object.
        using var rx = new SnmpAgent();
        using var tx = new SnmpAgent("127.0.0.2", rx.Port);
        rx.Values[Oid] = (0x02, Ber.IntegerBytes(118100));
        tx.Values[TxOid] = (0x02, Ber.IntegerBytes(118100));

        var site = new SiteConfig
        {
            Id = "S2", Name = "APPROACH", Profile = RadioProfile.Ed137BId, LinkMode = nameof(RadioLinkMode.Separate),
            // SIP runs on documentation addresses; SNMP has its own (management) addresses.
            RxHost = "192.0.2.10", TxHost = "192.0.2.11",
            SnmpRxHost = rx.Host, SnmpTxHost = tx.Host,
            SnmpEnabled = true, SnmpPort = rx.Port,
            SnmpFrequencyOid = "." + Oid, SnmpTxFrequencyOid = TxOid
        };

        var target = SnmpTarget.For(site);
        Require(target.Endpoints.Count == 2 &&
                target.Endpoints[0] == new SnmpEndpoint(tx.Host, TxOid) &&
                target.Endpoints[1] == new SnmpEndpoint(rx.Host, Oid),
            "The transmitter must be asked first, each unit at its SNMP address for its own object: " +
            string.Join(" | ", target.Endpoints));

        var set = await new SnmpFrequencyClient().SetFrequencyAsync(target, 119700, CancellationToken.None);
        Require(set.Count == 2 && set.All(r => r.Success), "Write to both units failed: " + string.Join(" | ", set));
        Require(Ber.Integer(tx.Values[TxOid].Value) == 119700 && Ber.Integer(rx.Values[Oid].Value) == 119700 &&
                !tx.Values.ContainsKey(Oid) && !rx.Values.ContainsKey(TxOid),
            "The units did not each receive their own object");

        // Without SNMP addresses the SIP addresses are used.
        site.SnmpRxHost = site.SnmpTxHost = "";
        target = SnmpTarget.For(site);
        Require(target.Endpoints.Select(e => e.Host).SequenceEqual(new[] { "192.0.2.11", "192.0.2.10" }), "SIP addresses not used");

        // Only a transmitter object: the receiver is not asked for an object it does not have.
        site.SnmpFrequencyOid = "";
        Require(site.HasFrequencyControl() && SnmpTarget.For(site).Endpoints.Count == 1 &&
                SnmpTarget.For(site).Endpoints[0].Oid == TxOid, "Transmitter-only object not handled");

        // One address, one object: asked once.
        var transceiver = new SiteConfig
        {
            Id = "S3", Name = "GROUND", Profile = RadioProfile.Ed137BId, LinkMode = nameof(RadioLinkMode.Transceiver),
            TxHost = "192.0.2.30", SnmpEnabled = true, SnmpFrequencyOid = Oid, SnmpTxFrequencyOid = Oid
        };
        Require(SnmpTarget.For(transceiver).Endpoints.Count == 1, "A transceiver has one unit");
        var receiver = new SiteConfig
        {
            Id = "S4", Name = "ATIS", Profile = RadioProfile.Ed137BId, LinkMode = nameof(RadioLinkMode.RxOnly),
            RxHost = "192.0.2.40", SnmpEnabled = true, SnmpFrequencyOid = Oid, SnmpTxFrequencyOid = TxOid, SnmpTxHost = "192.0.2.41"
        };
        Require(SnmpTarget.For(receiver).Endpoints is [{ Host: "192.0.2.40", Oid: Oid }], "A receiver-only radio has no transmitter to ask");
    }

    private static async Task Failures()
    {
        var client = new SnmpFrequencyClient();
        using var agent = new SnmpAgent();
        var site = SnmpRadio(agent);

        // Object not provided (version 2c: exception value in the answer).
        var result = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(!result.Success && result.FrequencyKhz == 0 && result.Message == Loc.T("Snmp_NoSuchObject"),
            $"Missing object: {result.Message}");

        // Text that is not a number.
        site.SnmpFrequencyUnit = SnmpTarget.UnitMhzText;
        agent.Values[Oid] = (0x04, Encoding.ASCII.GetBytes("standby"));
        result = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(!result.Success && result.Message.Contains("standby"), $"Text answer: {result.Message}");

        // A number that cannot be a frequency in the chosen unit.
        site.SnmpFrequencyUnit = SnmpTarget.UnitKhz;
        agent.Values[Oid] = (0x02, Ber.IntegerBytes(-5));
        result = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(!result.Success, "A negative number was accepted as a frequency");

        // The radio refuses the write.
        agent.Values[Oid] = (0x02, Ber.IntegerBytes(118100));
        agent.RefuseWrites = true;
        var set = await client.SetFrequencyAsync(SnmpTarget.For(site), 121500, CancellationToken.None);
        Require(!set[0].Success && set[0].Message.Contains("notWritable") && Ber.Integer(agent.Values[Oid].Value) == 118100,
            $"Refused write: {set[0].Message}");

        // Nobody answers.
        agent.Silent = true;
        result = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(!result.Success && result.Message == Loc.T("Snmp_NoAnswer"), $"Silence: {result.Message}");
    }

    private static async Task CardFrequency()
    {
        using var agent = new SnmpAgent();
        agent.Values[Oid] = (0x02, Ber.IntegerBytes(118100000));
        var site = SnmpRadio(agent, SnmpTarget.UnitHz);
        var config = Config(site);
        var saves = 0;
        using var vm = new SiteViewModel(site, new ScriptedRadio(), new TxLock(), () => config, () => { }, () => saves++);

        Require(vm.HasFrequencyControl, "Frequency control not recognised");
        await Until(() => vm.FrequencyDisplay == "118.100" && vm.FrequencyVerified, what: "the frequency read from the radio");
        Require(site.Frequency == "118.100" && saves == 1, "The frequency read from the radio was not stored");

        vm.BeginFrequencyEdit();
        vm.FrequencyInput = "121,5";
        await vm.SetFrequencyAsync();
        Require(Ber.Integer(agent.Values[Oid].Value) == 121500000, $"The radio received {Ber.Integer(agent.Values[Oid].Value)}");
        await Until(() => vm.FrequencyDisplay == "121.500" && !vm.IsFrequencyEditing, what: "the new frequency on the card");

        // A refused write leaves the old value on the card and says why.
        agent.RefuseWrites = true;
        vm.BeginFrequencyEdit();
        vm.FrequencyInput = "119.000";
        await vm.SetFrequencyAsync();
        Require(vm.StatusText.StartsWith("Frequency not changed") && site.Frequency == "121.500",
            $"A refused write was shown as done: {vm.StatusText} / {site.Frequency}");
    }

    private static async Task SnmpSettings()
    {
        using var agent = new SnmpAgent();
        agent.Values[Oid] = (0x04, Encoding.ASCII.GetBytes("118.100"));

        var radio = Radio("A", "TOWER");
        var settings = new SettingsViewModel(Config(radio));
        var edit = settings.Radios[0];
        Require(edit.Snmp == "Off" && edit.SnmpVersion == SnmpTarget.Version2c && edit.SnmpUnit == SnmpTarget.UnitKhz &&
                edit.SnmpUnitChoices.Count == 3 && edit.SnmpVersionChoices.Count == 2 && edit.ShowTxOid, "Defaults");

        await edit.TestSnmpAsync();
        Require(edit.SnmpTestResult == Loc.T("Snmp_TestNoTarget"), $"Test with SNMP switched off: {edit.SnmpTestResult}");

        edit.Snmp = "On";
        edit.SnmpFrequencyOid = Oid;
        edit.SnmpTxFrequencyOid = "1.3.x";
        Require(settings.Validate()?.Contains("SNMP OID") == true, "A malformed transmitter OID was accepted");
        edit.SnmpTxFrequencyOid = "";
        edit.SnmpRxHost = "not a host!";
        Require(settings.Validate()?.Contains("SNMP address") == true, "A malformed SNMP address was accepted");
        await edit.TestSnmpAsync();
        Require(edit.SnmpTestResult.Contains("SNMP address"), "The test must not run with invalid entries");

        edit.SnmpRxHost = agent.Host;
        edit.SnmpTxHost = agent.Host;
        edit.SnmpPort = agent.Port.ToString();
        edit.SnmpUnit = SnmpTarget.UnitMhzText;
        edit.SnmpVersion = SnmpTarget.VersionOne;
        edit.SnmpCommunity = "ops";
        Require(settings.Validate() == null, $"Valid SNMP entries rejected: {settings.Validate()}");

        await edit.TestSnmpAsync();
        Require(edit.SnmpTestResult == $"{agent.Host}: 118.100 MHz" && !edit.IsSnmpTesting && agent.LastVersion == 0 &&
                agent.LastCommunity == "ops", $"Read now: '{edit.SnmpTestResult}'");

        var stored = settings.ToConfig().Radios[0];
        Require(stored.SnmpEnabled == true && stored.SnmpFrequencyOid == Oid && stored.SnmpTxFrequencyOid == "" &&
                stored.SnmpRxHost == agent.Host && stored.SnmpTxHost == agent.Host && stored.SnmpPort == agent.Port &&
                stored.SnmpFrequencyUnit == SnmpTarget.UnitMhzText && stored.SnmpVersion == SnmpTarget.VersionOne &&
                stored.SnmpCommunity == "ops" && stored.RxHost == "192.0.2.10" && stored.TxHost == "192.0.2.11",
            "SNMP entries were not stored as entered");

        // A transceiver has one unit: no separate transmitter object, one SNMP address.
        edit.LinkMode = nameof(RadioLinkMode.Transceiver);
        Require(!edit.ShowTxOid && !edit.ShowRx && edit.SnmpTxHostLabel == Loc.T("Set_SnmpRadioHost"), "Transceiver layout");
    }

    private static void NormalizeNewFields()
    {
        var cfg = Config(Radio("A", "ONE"), Radio("B", "TWO"));
        cfg.Radios[0].SnmpFrequencyUnit = "hz";
        cfg.Radios[0].SnmpVersion = " 1 ";
        cfg.Radios[0].SnmpTxFrequencyOid = null!;
        cfg.Radios[0].SnmpRxHost = null!;
        cfg.Radios[0].SnmpTxHost = null!;
        cfg.Radios[1].SnmpFrequencyUnit = "furlongs";
        cfg.Radios[1].SnmpVersion = "3";
        cfg.Ui.Language = null!;

        Require(ConfigMigrations.Normalize(cfg), "Nothing was corrected");
        Require(cfg.Radios[0].SnmpFrequencyUnit == SnmpTarget.UnitHz && cfg.Radios[0].SnmpVersion == SnmpTarget.VersionOne &&
                cfg.Radios[0].SnmpTxFrequencyOid == "" && cfg.Radios[0].SnmpRxHost == "" && cfg.Radios[0].SnmpTxHost == "",
            "Stored spellings were not made canonical");
        Require(cfg.Radios[1].SnmpFrequencyUnit == SnmpTarget.UnitKhz && cfg.Radios[1].SnmpVersion == SnmpTarget.Version2c,
            "Unknown values must fall back to kHz and v2c");
        Require(cfg.Ui.Language == "", "Language");
        Require(!ConfigMigrations.Normalize(cfg), "Normalising twice changed something");

        // A file written before these options existed keeps working with the defaults.
        var old = System.Text.Json.JsonSerializer.Deserialize<SiteConfig>(
            """{ "Id": "X", "Name": "OLD", "RxHost": "192.0.2.50", "TxHost": "192.0.2.51", "SnmpCommunity": "public", "SnmpPort": 161 }""")!;
        var target = SnmpTarget.For(old);
        Require(old.HasFrequencyControl() && !target.Version1 && target.Unit == SnmpFrequencyUnit.Khz && target.Endpoints.Count == 2 &&
                target.Endpoints.All(e => e.Oid == old.GetSnmpFrequencyOid()), "An older radio entry changed its behaviour");
    }

    // ------------------------------------------------------------------ languages

    private static readonly (string Prefix, int Limit)[] Limits =
    {
        ("Card_Connect", 12), ("Card_Disconnect", 12), ("Card_Cancel", 12), ("Card_Set", 8), ("Card_Transmitting", 14),
        ("Set_Add", 12), ("Set_Remove", 12), ("Set_Up", 12), ("Set_Down", 12), ("Btn_", 12), ("Set_Tab", 12),
        ("Status_Disconnected", 22), ("Status_Connecting", 22), ("Status_Connected", 22), ("Status_NotConnected", 22),
        ("Status_RxOnly", 22), ("Status_TxOnly", 22)
    };

    private static void LanguageFiles()
    {
        var english = Loc.Table(Loc.English);
        Require(english.Count > 150, $"The English texts were not found in the program ({english.Count})");
        Require(Loc.Languages.Count == 34 && Loc.Languages.Select(l => l.Code).Distinct().Count() == 34 &&
                Loc.Languages[0].Code == Loc.English, "Language list");
        Require(Loc.Languages.Where(l => l.RightToLeft).Select(l => l.Code).SequenceEqual(new[] { "ar", "fa", "ur" }),
            "Right-to-left languages");

        var problems = new List<string>();
        foreach (var language in Loc.Languages)
        {
            var table = Loc.Table(language.Code);
            if (table.Count == 0)
            {
                problems.Add($"{language.Code}: no texts");
                continue;
            }

            foreach (var (key, reference) in english)
            {
                if (!table.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{language.Code}: {key} is missing");
                    continue;
                }
                if (!Placeholders(text).SequenceEqual(Placeholders(reference)))
                    problems.Add($"{language.Code}: {key} has other placeholders than the English text");
                if (reference.Contains('\n') != text.Contains('\n'))
                    problems.Add($"{language.Code}: {key} lost its line breaks");
                foreach (var (prefix, limit) in Limits)
                    if (key.StartsWith(prefix, StringComparison.Ordinal) && Graphemes(text) > limit)
                        problems.Add($"{language.Code}: {key} '{text}' is longer than {limit} characters");
                try { _ = string.Format(text, "a", "b", "c"); }
                catch (FormatException) { problems.Add($"{language.Code}: {key} is not a valid format text"); }
            }

            foreach (var key in table.Keys.Where(k => !english.ContainsKey(k)))
                problems.Add($"{language.Code}: unknown text {key}");
        }

        Require(problems.Count == 0, $"{problems.Count} problem(s):\n  " + string.Join("\n  ", problems.Take(40)));
    }

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToArray();

    private static int Graphemes(string text) => new System.Globalization.StringInfo(text).LengthInTextElements;

    private static void SwitchLanguage()
    {
        var radio = Radio("A", "TOWER");
        var config = Config(radio);
        using var card = new SiteViewModel(radio, new ScriptedRadio(), new TxLock(), () => config, () => { });
        var changes = 0;
        void Count() => changes++;
        Loc.Changed += Count;
        try
        {
            Require(Loc.Language.Code == Loc.English && !Loc.IsRightToLeft, "The checks must start in English");
            Require(Loc.T("Main_PttKey", "F9") == "PTT key: F9" && card.ConnectButtonText == "Connect" &&
                    card.StatusText == "Disconnected", "English texts");
            Require(Loc.T("No_Such_Text") == "No_Such_Text", "An unknown key must be shown as it is, not crash");

            Loc.Use("DE");
            var german = Loc.Table("de");
            Require(Loc.Language.Code == "de" && changes == 1, "Language not switched");
            Require(Loc.T("Btn_Cancel") == german["Btn_Cancel"] && german["Btn_Cancel"] != "Cancel" &&
                    LocSource.Instance["Btn_Save"] == german["Btn_Save"], "German texts are not used");
            card.RefreshTexts();
            Require(card.ConnectButtonText == german["Card_Connect"] && card.ConnectAction == "Connect" &&
                    card.StatusText == german["Status_Disconnected"],
                $"The radio card kept the old language: {card.ConnectButtonText} / {card.StatusText}");
            Require(Loc.T("Main_PttKey", "F9") == string.Format(german["Main_PttKey"], "F9"), "Placeholders");

            var settings = new SettingsViewModel(config);
            Require(settings.LanguageChoices.Count == Loc.Languages.Count + 1 && settings.LanguageChoices[0].Value == "" &&
                    settings.Language == "" && settings.Radios[0].LinkModeChoices[0].Label == german["Link_Separate"],
                "Settings lists are not in the chosen language");
            settings.Language = "ja";
            Require(settings.ToConfig().Ui.Language == "ja", "The chosen language is not stored");
            Require(settings.RadioCountText == "1 of 8", "Language-independent count changed");

            Loc.Use("de");
            Require(changes == 1, "Choosing the same language again must not rebuild the windows");

            Loc.Use("ar");
            Require(Loc.IsRightToLeft && LocSource.Instance.FlowDirection == System.Windows.FlowDirection.RightToLeft && changes == 2,
                "Arabic must switch the windows to right-to-left");

            // A stored code that is not a language of the program: Windows language or English, never a crash.
            Require(Loc.Languages.Contains(Loc.Resolve("xx")) && Loc.Resolve(" TR ").Code == "tr" && Loc.Resolve("en").Code == "en",
                "Unknown language code");
        }
        finally
        {
            Loc.Changed -= Count;
            Loc.Use(Loc.English);
        }

        card.RefreshTexts();
        Require(card.ConnectButtonText == "Connect" && !Loc.IsRightToLeft, "Back to English");

        // A text placed in a window with {loc:T Key} follows the language while the window is open.
        string? shown = null, shownGerman = null, shownAgain = null;
        Exception? failure = null;
        var ui = new Thread(() =>
        {
            try
            {
                var block = new System.Windows.Controls.TextBlock();
                var binding = (System.Windows.Data.BindingBase)new TExtension("Btn_Save").ProvideValue(null!);
                System.Windows.Data.BindingOperations.SetBinding(block, System.Windows.Controls.TextBlock.TextProperty, binding);
                shown = block.Text;
                Loc.Use("de");
                shownGerman = block.Text;
                Loc.Use(Loc.English);
                shownAgain = block.Text;
            }
            catch (Exception ex) { failure = ex; }
            finally { Loc.Use(Loc.English); }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        Require(failure == null, $"Window text binding failed: {failure}");
        Require(shown == "Save" && shownGerman == Loc.Table("de")["Btn_Save"] && shownAgain == "Save",
            $"An open window did not follow the language: {shown} / {shownGerman} / {shownAgain}");
    }
}

/// <summary>Minimal BER helpers for the test agent.</summary>
internal static class Ber
{
    public static (byte Tag, byte[] Value) Read(byte[] data, ref int offset)
    {
        var tag = data[offset++];
        int length = data[offset++];
        if ((length & 0x80) != 0)
        {
            var count = length & 0x7F;
            length = 0;
            for (var i = 0; i < count; i++) length = (length << 8) | data[offset++];
        }
        var value = data.AsSpan(offset, length).ToArray();
        offset += length;
        return (tag, value);
    }

    public static byte[] Tlv(byte tag, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var head = body.Length < 0x80
            ? new[] { tag, (byte)body.Length }
            : new[] { tag, (byte)0x82, (byte)(body.Length >> 8), (byte)body.Length };
        return head.Concat(body).ToArray();
    }

    /// <summary>Two's complement, shortest form.</summary>
    public static byte[] IntegerBytes(long value)
    {
        var bytes = BitConverter.GetBytes(value).Reverse().ToArray();
        var start = 0;
        while (start < bytes.Length - 1 &&
               ((bytes[start] == 0x00 && (bytes[start + 1] & 0x80) == 0) || (bytes[start] == 0xFF && (bytes[start + 1] & 0x80) != 0)))
            start++;
        return bytes[start..];
    }

    public static long Integer(byte[] value)
    {
        long number = value.Length > 0 && (value[0] & 0x80) != 0 ? -1 : 0;
        foreach (var b in value) number = (number << 8) | b;
        return number;
    }

    public static string Oid(byte[] value)
    {
        var parts = new List<long> { value[0] / 40, value[0] % 40 };
        long current = 0;
        for (var i = 1; i < value.Length; i++)
        {
            current = (current << 7) | (uint)(value[i] & 0x7F);
            if ((value[i] & 0x80) != 0) continue;
            parts.Add(current);
            current = 0;
        }
        return string.Join('.', parts);
    }
}

/// <summary>
/// An SNMP agent on the loopback interface that knows a handful of objects. It stands in
/// for a radio; nothing leaves the PC.
/// </summary>
internal sealed class SnmpAgent : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();

    public SnmpAgent(string host = "127.0.0.1", int port = 0)
    {
        Host = host;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Parse(host), port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(Serve);
    }

    public string Host { get; }
    public int Port { get; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, (byte Tag, byte[] Value)> Values { get; } = new();
    public volatile int LastVersion = -1;
    public volatile string LastCommunity = "";
    public volatile bool RefuseWrites, Silent;

    /// <summary>Called instead of answering while <see cref="Silent"/>: who asked, and the answer that was withheld.</summary>
    public volatile Action<IPEndPoint, byte[]>? Unanswered;

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try { request = await _udp.ReceiveAsync(_stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }

            byte[] answer;
            try { answer = Answer(request.Buffer); }
            catch (Exception) { continue; } // not SNMP
            if (Silent)
            {
                Unanswered?.Invoke(request.RemoteEndPoint, answer);
                continue;
            }
            try { await _udp.SendAsync(answer, answer.Length, request.RemoteEndPoint); }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException) { return; }
        }
    }

    private byte[] Answer(byte[] packet)
    {
        var offset = 0;
        var message = Ber.Read(packet, ref offset).Value;
        offset = 0;
        var version = Ber.Read(message, ref offset);
        var community = Ber.Read(message, ref offset);
        var pdu = Ber.Read(message, ref offset);
        LastVersion = (int)Ber.Integer(version.Value);
        LastCommunity = Encoding.ASCII.GetString(community.Value);

        offset = 0;
        var requestId = Ber.Read(pdu.Value, ref offset);
        _ = Ber.Read(pdu.Value, ref offset);
        _ = Ber.Read(pdu.Value, ref offset);
        var bindings = Ber.Read(pdu.Value, ref offset).Value;
        offset = 0;
        var binding = Ber.Read(bindings, ref offset).Value;
        offset = 0;
        var oid = Ber.Read(binding, ref offset);
        var value = Ber.Read(binding, ref offset);
        var name = Ber.Oid(oid.Value);

        var error = 0;
        (byte Tag, byte[] Value) result = (0x05, Array.Empty<byte>());
        if (pdu.Tag == 0xA3)
        {
            if (RefuseWrites) error = 17; // notWritable
            else
            {
                Values[name] = value;
                result = value;
            }
        }
        else if (Values.TryGetValue(name, out var stored)) result = stored;
        else if (LastVersion == 0) error = 2; // noSuchName
        else result = (0x80, Array.Empty<byte>()); // noSuchObject

        return Ber.Tlv(0x30,
            Ber.Tlv(0x02, version.Value),
            Ber.Tlv(0x04, community.Value),
            Ber.Tlv(0xA2,
                Ber.Tlv(0x02, requestId.Value),
                Ber.Tlv(0x02, new[] { (byte)error }),
                Ber.Tlv(0x02, new[] { (byte)(error == 0 ? 0 : 1) }),
                Ber.Tlv(0x30, Ber.Tlv(0x30, Ber.Tlv(0x06, oid.Value), Ber.Tlv(result.Tag, result.Value)))));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}
