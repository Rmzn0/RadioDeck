using System.IO;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.Services.Audio;
using RadioDeck.ViewModels;
using static T;

/// <summary>Configuration file, settings window logic, PIN and the small pure helpers.</summary>
internal static class ConfigRegression
{
    public static async Task Run()
    {
        await Check("Version 1 config.json becomes two named radios without losing addresses", MigrateVersion1);
        await Check("Unreadable config.json is kept aside, never overwritten silently", UnreadableConfig);
        await Check("Stored values are brought into range (radio count, ids, profile, volume)", NormalizeLimits);
        await Check("Settings: add up to eight radios, remove, re-order", EditRadioList);
        await Check("Settings: names, addresses, ports and limits are validated before saving", ValidateSettings);
        await Check("Settings PIN is stored as a salted hash and can be removed", Pin);
        await Check("Radio profiles: SIP/SDP flavour and PTT bits per radio family", Profiles);
        await Check("ED-137 header extension is read only from well-formed packets", ExtensionParsing);
        await Check("G.711 A-law level meter and silence", G711Checks);
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "RadioDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MigrateVersion1()
    {
        var dir = TempDirectory();
        try
        {
            // The shape written by version 1.x (two fixed sites, placeholder frequency).
            var version1Path = Path.Combine(dir, "config.json");
            File.WriteAllText(version1Path, """
                {
                  "Localizer": { "Name": "Localizer", "SipHost": "", "SipPort": 5060, "RxHost": "10.20.30.14", "TxHost": "10.20.30.15",
                                 "Frequency": "123.000", "RxUser": "RX_A", "TxUser": "TX_A", "User": null, "Pass": null },
                  "Glidepath": { "Name": "Glidepath", "SipHost": "10.20.30.99", "SipPort": 5070, "RxHost": "", "TxHost": "",
                                 "Frequency": "334.700", "RxUser": "", "TxUser": "" },
                  "Ptt": { "PttKey": "Space", "SingleTxAtATime": true, "Arbitration": "PRIORITY_LOC", "MuteRxWhileTx": false },
                  "Audio": { "Codec": "PCMA", "Vad": false }
                }
                """);
            var version1Text = File.ReadAllText(version1Path);

            var store = new ConfigStore("RadioDeck", dir);
            var cfg = store.Load();
            Require(store.LoadWarning == null, $"Migration reported a problem: {store.LoadWarning}");
            var backupPath = Path.Combine(dir, "config.v1-backup.json");
            Require(File.Exists(backupPath) && File.ReadAllText(backupPath) == version1Text,
                "The version 1 file was not kept before the conversion");
            Require(cfg.ConfigVersion == AppConfig.CurrentVersion && cfg.Localizer == null && cfg.Glidepath == null, "Not migrated");
            Require(cfg.Radios.Count == 2 && cfg.Radios[0].Id == "LOC" && cfg.Radios[1].Id == "GP", "Radios were not created in order");
            Require(cfg.Radios[0].Name == "LOCALIZER" && cfg.Radios[1].Name == "GLIDEPATH", "Titles changed for an existing installation");

            var loc = cfg.Radios[0];
            Require(loc.EffectiveRxHost == "10.20.30.14" && loc.EffectiveTxHost == "10.20.30.15" && loc.RxUser == "RX_A" && loc.TxUser == "TX_A",
                "Localizer addresses were lost");
            Require(loc.Frequency == "", "The 123.000 placeholder must not be presented as a real frequency");

            var gp = cfg.Radios[1];
            Require(gp.EffectiveRxHost == "10.20.30.99" && gp.EffectiveTxHost == "10.20.30.99" && gp.SipPort == 5070,
                "The single-host form of version 1 was not kept");
            Require(gp.Frequency == "334.700" && gp.RxUser == "rx2" && gp.TxUser == "tx2", "Glidepath values were lost");
            Require(cfg.Radios.All(r => r.Profile == RadioProfile.BasicId && r.LinkMode == nameof(RadioLinkMode.Separate) &&
                                        r.TxSelected && r.RxMonitor && r.RxVolume == 100), "New per-radio values have wrong defaults");

            Require(cfg.Ptt.PttKey == "Space" && cfg.Ptt.Arbitration == PttConfig.PriorityFirst && !cfg.Ptt.MuteRxWhileTx,
                "PTT settings were not carried over");
            Require(cfg.Ptt.MaxTxSeconds == 180 && cfg.Connection.AutoReconnect && !cfg.Connection.ConnectOnStartup &&
                    cfg.Ui.Mode == UiConfig.Classic && !cfg.Security.HasPin, "New options have wrong defaults");

            var written = File.ReadAllText(store.PathOnDisk);
            Require(written.Contains("\"Radios\"") && !written.Contains("\"Localizer\"") && !written.Contains("\"Glidepath\""),
                "The migrated file still contains the old layout");

            // Loading again changes nothing.
            var again = new ConfigStore("RadioDeck", dir).Load();
            Require(again.Radios.Count == 2 && again.Radios[1].EffectiveTxHost == "10.20.30.99" &&
                    File.ReadAllText(store.PathOnDisk) == written, "Migration is not stable");

            // A fresh installation starts with the two example radios.
            File.Delete(store.PathOnDisk);
            var fresh = new ConfigStore("RadioDeck", dir).Load();
            Require(fresh.Radios.Count == 2 && File.Exists(store.PathOnDisk) && fresh.Ptt.PttKey == "F9", "No default configuration was created");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void UnreadableConfig()
    {
        var dir = TempDirectory();
        try
        {
            const string broken = "{ \"Radios\": [ { \"Name\": \"TWR\", \"RxHost\": \"10.0.0.1\" ";
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, broken);

            var store = new ConfigStore("RadioDeck", dir);
            var cfg = store.Load();
            Require(store.LoadWarning is { Length: > 0 } && cfg.Radios.Count == 2, "A damaged file was not reported");
            Require(File.ReadAllText(path) == broken, "The damaged file was overwritten");
            var aside = Directory.GetFiles(dir, "config.unreadable-*.json");
            Require(aside.Length == 1 && File.ReadAllText(aside[0]) == broken, "No copy of the damaged file was kept");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void NormalizeLimits()
    {
        var cfg = new AppConfig();
        for (var i = 0; i < 11; i++)
            cfg.Radios.Add(new SiteConfig { Id = i < 3 ? "SAME" : "", Name = i == 4 ? " " : $"N{i}" });
        cfg.Radios[0].Profile = "does-not-exist";
        cfg.Radios[0].LinkMode = "transceiver";
        cfg.Radios[1].RxVolume = 900;
        cfg.Radios[1].RxPan = -500;
        cfg.Radios[1].SipPort = 0;
        cfg.Radios[2].SnmpPort = 70000;
        cfg.Radios[2].SnmpCommunity = "";
        cfg.Ptt.Arbitration = "whatever";
        cfg.Ptt.MaxTxSeconds = -5;
        cfg.Ptt.PttKey = "";
        cfg.Ui.Mode = "grid";

        Require(ConfigMigrations.Normalize(cfg), "Changes were not reported");
        Require(cfg.Radios.Count == AppConfig.MaxRadios, "More than eight radios were kept");
        Require(cfg.Radios.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8 &&
                cfg.Radios.All(r => r.Id.Length > 0) && cfg.Radios[0].Id == "SAME", "Radio ids are not unique");
        Require(cfg.Radios[4].Name == "Radio 5", "An empty name was not replaced");
        Require(cfg.Radios[0].Profile == RadioProfile.BasicId && cfg.Radios[0].LinkMode == nameof(RadioLinkMode.Transceiver),
            "Profile or link mode not normalized");
        Require(cfg.Radios[1].RxVolume == 200 && cfg.Radios[1].RxPan == -100 && cfg.Radios[1].SipPort == 5060, "Radio values out of range");
        Require(cfg.Radios[2].SnmpPort == 161 && cfg.Radios[2].SnmpCommunity == "public", "SNMP values out of range");
        Require(cfg.Ptt.Arbitration == PttConfig.FirstWins && cfg.Ptt.MaxTxSeconds == 0 && cfg.Ptt.PttKey == "F9", "PTT values out of range");
        Require(cfg.Ui.Mode == UiConfig.Grid && cfg.ConfigVersion == AppConfig.CurrentVersion, "View mode or version not normalized");
        Require(!ConfigMigrations.Normalize(cfg), "Normalizing twice changed the configuration again");

        var empty = new AppConfig();
        ConfigMigrations.Normalize(empty);
        Require(empty.Radios.Count == 2, "A configuration without radios must fall back to the examples");
    }

    private static void EditRadioList()
    {
        var settings = new SettingsViewModel(AppConfig.CreateDefault());
        Require(settings.Radios.Count == 2 && settings.SelectedRadio == settings.Radios[0] && settings.CanAddRadio, "Wrong start");

        while (settings.CanAddRadio) settings.AddRadioCommand.Execute(null);
        settings.AddRadioCommand.Execute(null); // a ninth press does nothing
        Require(settings.Radios.Count == 8 && !settings.AddRadioCommand.CanExecute(null) && settings.RadioCountText == "8 of 8",
            "The limit of eight radios is not enforced");
        Require(settings.Radios.Select(r => r.Id).Distinct().Count() == 8, "New radios share an id");
        Require(settings.Radios.Skip(2).Select(r => r.Name).SequenceEqual(Enumerable.Range(3, 6).Select(n => $"RADIO {n}")),
            "New radios are not named RADIO 3..8");
        Require(settings.SelectedRadio == settings.Radios[7] && settings.Radios[7].RxHost == "" && settings.Radios[7].TxHost == "",
            "A new radio must be selected and start without addresses");

        // New radios need addresses before the list can be saved.
        Require(settings.Validate() is { } missing && missing.Contains("RADIO 3") && settings.SelectedRadio == settings.Radios[2],
            "A radio without an address was accepted");
        for (var i = 2; i < 8; i++)
        {
            settings.Radios[i].RxHost = $"10.0.{i}.1";
            settings.Radios[i].TxHost = $"10.0.{i}.2";
        }
        Require(settings.Validate() == null, $"Valid list rejected: {settings.Validate()}");

        // Re-order: the last radio moves to the front; the order is what the main window shows.
        settings.SelectedRadio = settings.Radios[7];
        while (settings.MoveUpCommand.CanExecute(null)) settings.MoveUpCommand.Execute(null);
        Require(settings.Radios[0].Name == "RADIO 8" && settings.Radios[1].Name == "LOCALIZER", "Move up failed");
        settings.MoveDownCommand.Execute(null);
        Require(settings.Radios[0].Name == "LOCALIZER" && settings.Radios[1].Name == "RADIO 8", "Move down failed");

        settings.Radios[1].Name = "  Kule 118.1 ";
        settings.Radios[1].LinkMode = nameof(RadioLinkMode.Transceiver);
        settings.Radios[1].Profile = RadioProfile.Ed137BId;
        settings.UiMode = UiConfig.Grid;
        var saved = settings.ToConfig();
        Require(saved.Radios.Count == 8 && saved.Radios[0].Id == "LOC" && saved.Radios[2].Id == "GP", "Ids did not follow the order");
        Require(saved.Radios[1].Name == "Kule 118.1" && saved.Radios[1].RxHost == "" && saved.Radios[1].TxHost == "10.0.7.2" &&
                saved.Radios[1].Profile == RadioProfile.Ed137BId && saved.Radios[1].LinkMode == nameof(RadioLinkMode.Transceiver),
            "Edited radio not saved as entered");
        Require(saved.Ui.Mode == UiConfig.Grid && saved.ConfigVersion == AppConfig.CurrentVersion, "View mode not saved");

        // Remove down to one radio; the last one cannot be removed.
        while (settings.CanRemoveRadio) settings.RemoveRadioCommand.Execute(null);
        settings.RemoveRadioCommand.Execute(null);
        Require(settings.Radios.Count == 1 && settings.SelectedRadio == settings.Radios[0] && !settings.RemoveRadioCommand.CanExecute(null),
            "The last radio could be removed");
        Require(settings.ToConfig().Radios.Count == 1, "One radio must be a valid configuration");
    }

    private static void ValidateSettings()
    {
        SettingsViewModel New() => new(AppConfig.CreateDefault());
        void Rejects(Action<SettingsViewModel> change, string expected)
        {
            var settings = New();
            change(settings);
            var error = settings.Validate();
            Require(error != null && error.Contains(expected, StringComparison.OrdinalIgnoreCase),
                $"Expected an error containing '{expected}', got '{error ?? "(accepted)"}'");
        }

        Require(New().Validate() == null, "The default configuration is not valid");
        Rejects(s => s.Radios[0].Name = "  ", "needs a name");
        Rejects(s => s.Radios[1].Name = "localizer", "Two radios are named");
        Rejects(s => s.Radios[0].RxHost = "", "receiver's IP address");
        Rejects(s => s.Radios[0].TxHost = "not an address", "transmitter's IP address");
        Rejects(s => s.Radios[0].Port = "70000", "SIP port");
        Rejects(s => s.Radios[0].Port = "abc", "SIP port");
        Rejects(s => s.Radios[0].Frequency = "999", "frequency");
        Rejects(s => s.Radios[0].SnmpPort = "0", "SNMP port");
        Rejects(s => s.Radios[0].SnmpFrequencyOid = "1.3.x", "SNMP OID");
        Rejects(s => s.PttKey = "Banana", "PTT key");
        Rejects(s => s.PttKey = "None", "PTT key");
        Rejects(s => s.MaxTxSeconds = "-1", "transmit time limit");
        Rejects(s => s.MaxTxSeconds = "4000", "transmit time limit");
        Rejects(s => { s.NewPin = "12"; s.NewPinRepeat = "12"; }, "at least 4");
        Rejects(s => { s.NewPin = "1234"; s.NewPinRepeat = "1235"; }, "do not match");

        // A transceiver needs one address only; a receiver needs no transmitter.
        var settings = New();
        settings.Radios[0].LinkMode = nameof(RadioLinkMode.Transceiver);
        settings.Radios[0].RxHost = "";
        settings.Radios[1].LinkMode = nameof(RadioLinkMode.RxOnly);
        settings.Radios[1].TxHost = "";
        settings.Radios[0].Port = " 5062 ";
        settings.Radios[0].Frequency = "118,1";
        settings.Radios[0].RxHost = "ignored.example";
        settings.Radios[0].TxHost = " sip:TX@radio-1.example.org:5060 ";
        settings.MaxTxSeconds = "0";
        settings.PttKey = "f12";
        settings.SuppressKey = true;
        settings.AutoReconnect = false;
        settings.ConnectOnStartup = true;
        settings.AlwaysOnTop = true;
        settings.Organization = "  Flight Inspection  ";
        Require(settings.Validate() == null, $"Valid entries rejected: {settings.Validate()}");

        var saved = settings.ToConfig();
        Require(saved.Radios[0].RxHost == "" && saved.Radios[0].TxHost == "radio-1.example.org" && saved.Radios[0].SipPort == 5062 &&
                saved.Radios[0].Frequency == "118.100", "Transceiver entry not cleaned up");
        Require(saved.Radios[1].TxHost == "" && saved.Radios[1].RxHost == "192.0.2.21" && !saved.Radios[1].UsesTxLeg(), "Receiver entry wrong");
        Require(saved.Ptt.MaxTxSeconds == 0 && saved.Ptt.PttKey == "f12" && saved.Ptt.SuppressKey, "PTT options not saved");
        Require(!saved.Connection.AutoReconnect && saved.Connection.ConnectOnStartup && saved.Ui.AlwaysOnTop &&
                saved.Ui.Organization == "Flight Inspection", "General options not saved");

        Require(SettingsViewModel.TryParseKey("F9", out _) && SettingsViewModel.TryParseKey("space", out _) &&
                !SettingsViewModel.TryParseKey("", out _) && !SettingsViewModel.TryParseKey("123456", out _),
            "PTT key names are not parsed correctly");
    }

    private static void Pin()
    {
        var security = new SecurityConfig();
        Require(!security.HasPin && PinHasher.Verify(security, null) && PinHasher.Verify(security, "anything"),
            "Without a PIN the settings must open");

        PinHasher.SetPin(security, "24-68!");
        Require(security.HasPin && !security.PinHash.Contains("24-68!") && !security.PinSalt.Contains("24-68!"), "PIN stored in clear text");
        Require(PinHasher.Verify(security, "24-68!") && !PinHasher.Verify(security, "24-69!") && !PinHasher.Verify(security, "") &&
                !PinHasher.Verify(security, null), "PIN check is wrong");

        var other = new SecurityConfig();
        PinHasher.SetPin(other, "24-68!");
        Require(other.PinSalt != security.PinSalt && other.PinHash != security.PinHash, "The same PIN gives the same hash (no salt)");

        var damaged = new SecurityConfig { PinSalt = "***", PinHash = "***" };
        Require(!PinHasher.Verify(damaged, "24-68!"), "A damaged PIN entry must not open the settings");

        // Through the settings window: set, keep, remove.
        var cfg = AppConfig.CreateDefault();
        var set = new SettingsViewModel(cfg) { NewPin = "47-11!", NewPinRepeat = "47-11!" };
        Require(!set.HasPin && set.Validate() == null, "A valid new PIN was rejected");
        var withPin = set.ToConfig();
        Require(withPin.Security.HasPin && PinHasher.Verify(withPin.Security, "47-11!") && !PinHasher.Verify(withPin.Security, "12-34!"),
            "The PIN from the settings window does not work");

        var keep = new SettingsViewModel(withPin);
        Require(keep.HasPin && PinHasher.Verify(keep.ToConfig().Security, "47-11!"), "Saving other settings lost the PIN");

        var remove = new SettingsViewModel(withPin) { RemovePin = true };
        Require(!remove.ToConfig().Security.HasPin, "The PIN could not be removed");

        var json = System.Text.Json.JsonSerializer.Serialize(withPin);
        Require(!json.Contains("47-11!") && !json.Contains("\"HasPin\""), "The PIN leaks into config.json");
    }

    private static void Profiles()
    {
        Require(RadioProfile.All.Count == 3 && RadioProfile.All.Count(p => p.Tested) == 1 && RadioProfile.Basic.Tested,
            "Only the basic profile is verified on hardware");
        Require(RadioProfile.Get(null) == RadioProfile.Basic && RadioProfile.Get("unknown") == RadioProfile.Basic &&
                RadioProfile.Get(" ED137B ") == RadioProfile.Ed137B && RadioProfile.Get("ed137c") == RadioProfile.Ed137C,
            "Profile lookup is wrong");
        Require(RadioProfile.ParseLinkMode("transceiver") == RadioLinkMode.Transceiver &&
                RadioProfile.ParseLinkMode("RxOnly") == RadioLinkMode.RxOnly &&
                RadioProfile.ParseLinkMode("7") == RadioLinkMode.Separate && RadioProfile.ParseLinkMode(null) == RadioLinkMode.Separate,
            "Link mode parsing is wrong");

        var site = new SiteConfig();
        Require(site.GetProfile() == RadioProfile.Basic && site.GetWg67Version() == null && site.UsesRxLeg() && site.UsesTxLeg(),
            "A new radio must behave like version 1 (basic profile, separate RX/TX)");
        site.Profile = RadioProfile.Ed137BId;
        Require(site.GetWg67Version() == "radio.01", "ED-137B announces radio.01");
        site.Profile = RadioProfile.Ed137CId;
        Require(site.GetWg67Version() == "radio.02", "ED-137C announces radio.02");
        site.Wg67Version = " radio.01 ";
        Require(site.GetWg67Version() == "radio.01", "The WG67-Version override is ignored");

        // PTT bits. Basic profile: audio 0x20400000,
        // keepalive while transmitting 0x00400000, idle 0.
        Require(SipsorceryRadioEngine.BuildExtensionWord(RadioProfile.Basic, 1, pttOn: true, keepAlive: false) == 0x20400000u,
            "Basic profile audio packets must carry normal PTT ON with PTT-ID 1");
        Require(SipsorceryRadioEngine.BuildExtensionWord(RadioProfile.Basic, 1, pttOn: true, keepAlive: true) == 0x00400000u,
            "Basic profile keepalives during a transmission changed");
        Require(SipsorceryRadioEngine.BuildExtensionWord(RadioProfile.Basic, 1, pttOn: false, keepAlive: true) == 0 &&
                SipsorceryRadioEngine.BuildExtensionWord(RadioProfile.Ed137C, 9, pttOn: false, keepAlive: false) == 0,
            "PTT OFF must be an all-zero word");
        Require(SipsorceryRadioEngine.BuildExtensionWord(RadioProfile.Ed137B, 5, pttOn: true, keepAlive: true) == 0x21400000u &&
                SipsorceryRadioEngine.BuildExtensionWord(RadioProfile.Ed137B, 5, pttOn: true, keepAlive: false) == 0x21400000u,
            "Standard radios expect PTT ON and the assigned PTT-ID in every packet of a transmission");
        Require(SipsorceryRadioEngine.BuildExtensionWord(RadioProfile.Ed137C, 0x45, pttOn: true, keepAlive: false) == 0x21400000u,
            "The PTT-ID must be limited to six bits");
    }

    private static void ExtensionParsing()
    {
        static byte[] Packet(uint word, int csrcCount = 0, ushort profile = 0x0167, ushort length = 1, int payload = 0)
        {
            var packet = new byte[12 + csrcCount * 4 + 4 + length * 4 + payload];
            packet[0] = (byte)(0x90 | csrcCount);
            packet[1] = 123;
            var offset = 12 + csrcCount * 4;
            packet[offset] = (byte)(profile >> 8);
            packet[offset + 1] = (byte)profile;
            packet[offset + 2] = (byte)(length >> 8);
            packet[offset + 3] = (byte)length;
            if (length >= 1)
            {
                packet[offset + 4] = (byte)(word >> 24);
                packet[offset + 5] = (byte)(word >> 16);
                packet[offset + 6] = (byte)(word >> 8);
                packet[offset + 7] = (byte)word;
            }
            return packet;
        }

        Require(SipsorceryRadioEngine.TryReadExtensionWord(Packet(0x30400000), out var word) && word == 0x30400000u, "Keepalive not read");
        Require(SipsorceryRadioEngine.TryReadExtensionWord(Packet(0x10000000, payload: 160), out word) && word == 0x10000000u, "Audio packet not read");
        Require(SipsorceryRadioEngine.TryReadExtensionWord(Packet(0x20400000, csrcCount: 2), out word) && word == 0x20400000u, "CSRC list not skipped");
        Require(SipsorceryRadioEngine.TryReadExtensionWord(Packet(0x20400000, length: 2), out word) && word == 0x20400000u, "Longer extension not read");

        var noExtension = Packet(0x20400000);
        noExtension[0] = 0x80;
        var wrongVersion = Packet(0x20400000);
        wrongVersion[0] = 0x50;
        foreach (var (name, packet) in new[]
                 {
                     ("empty", Array.Empty<byte>()), ("one byte", new byte[] { 0x90 }), ("header only", new byte[12]),
                     ("no extension flag", noExtension), ("wrong RTP version", wrongVersion),
                     ("other profile", Packet(0x20400000, profile: 0x0168)), ("zero length", Packet(0, length: 0, payload: 8)),
                     ("truncated", Packet(0x20400000)[..18]), ("truncated CSRC", Packet(0x20400000, csrcCount: 3)[..22])
                 })
        {
            Require(!SipsorceryRadioEngine.TryReadExtensionWord(packet, out word) && word == 0, $"Malformed packet accepted: {name}");
        }
    }

    private static void G711Checks()
    {
        var silence = G711.SilenceFrame(160);
        Require(silence.Length == 160 && silence.All(b => b == G711.Silence) && G711.Level(silence) == 0, "Silence is not silent");
        Require(Math.Abs(G711.Decode(G711.Silence)) <= 8, "0xD5 must decode to (almost) zero");

        var loud = Enumerable.Range(0, 160).Select(i => G711.Encode((short)(i % 2 == 0 ? 20000 : -20000))).ToArray();
        var quiet = Enumerable.Range(0, 160).Select(i => G711.Encode((short)(i % 2 == 0 ? 300 : -300))).ToArray();
        Require(G711.Level(loud) > G711.Level(quiet) && G711.Level(loud) is > 30 and <= 100 && G711.Level(quiet) < 10,
            $"Level meter is implausible: loud={G711.Level(loud)} quiet={G711.Level(quiet)}");
        foreach (var sample in new short[] { 0, 100, -100, 1000, -1000, 12000, -12000, 32000, -32000 })
        {
            var decoded = G711.Decode(G711.Encode(sample));
            Require(Math.Abs(decoded - sample) <= Math.Max(16, Math.Abs(sample) / 16), $"A-law round trip of {sample} gave {decoded}");
        }
        Require(G711.Level(ReadOnlySpan<byte>.Empty) == 0, "Empty frame level");
    }
}
