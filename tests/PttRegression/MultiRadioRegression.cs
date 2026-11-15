using System.ComponentModel;
using System.Diagnostics;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.ViewModels;
using static T;

/// <summary>Version 2: up to eight freely named radios, the grid view and the connection supervision.</summary>
internal static class MultiRadioRegression
{
    public static async Task Run()
    {
        await Check("Eight radios: the PTT key keys only the selected radios", SelectedRadiosOnly);
        foreach (var mode in new[] { PttConfig.FirstWins, PttConfig.LastWins, PttConfig.PriorityFirst })
            await Check($"Eight radios, single TX, {mode}: exactly one transmitter at a time", () => SingleTxAmongEight(mode));
        await Check("PTT controller rejects more than eight radios and ignores unknown slots", SlotLimits);
        await Check("Transmit time limit stops a stuck PTT until every input is released", TransmitLimit, 15);
        await Check("Radio names are free text and renaming does not drop the call", Naming);
        await Check("Connect, radio hang-up, automatic reconnect and manual disconnect", Reconnect, 20);
        await Check("Failed and cancelled connection attempts are reported and can be stopped", FailedAndCancelled);
        await Check("Transceiver and receive-only radios use only the session they need", LinkModes);
        await Check("A changed address re-establishes the call", AddressChange);
        await Check("Frequency entry: VHF/UHF formats, range check and label-only radios", Frequency);
        await Check("Main view: 1-8 radios in configured order, four tiles per row", MainView, 20);
    }

    private static async Task SelectedRadiosOnly()
    {
        var radios = Enumerable.Range(0, 8).Select(_ => new FakeRadio()).ToArray();
        var cfg = new PttConfig { SingleTxAtATime = false, MaxTxSeconds = 0 };
        var selected = PttController.Slot(1) | PttController.Slot(4) | PttController.Slot(7);
        await using var ptt = new PttController(8, (slot, down) => radios[slot].Apply(down), () => cfg,
            keyboardTargets: () => selected);

        bool Only(params int[] slots) => radios.Select((r, i) => r.IsSendingToTx == slots.Contains(i)).All(x => x);

        ptt.SetKeyboard(true);
        await Until(() => Only(1, 4, 7), what: "radios 2, 5 and 8 to transmit");
        await Task.Delay(60);
        Require(Only(1, 4, 7), "An unselected radio was keyed by the PTT key");

        // Changing the selection while the key is held takes effect at once.
        selected = PttController.Slot(0);
        ptt.Refresh();
        await Until(() => Only(0), what: "the new selection");

        ptt.SetMouse(PttController.Slot(6)); // an on-screen button keys its own radio regardless of the selection
        await Until(() => Only(0, 6));
        ptt.SetKeyboard(false);
        await Until(() => Only(6));
        ptt.SetMouse(PttTargets.None);
        await Until(() => Only());
    }

    private static async Task SingleTxAmongEight(string mode)
    {
        var radios = Enumerable.Range(0, 8).Select(_ => new FakeRadio()).ToArray();
        var cfg = new PttConfig { SingleTxAtATime = true, Arbitration = mode, MaxTxSeconds = 0 };
        var overlap = false;
        Task Apply(int slot, bool down)
        {
            if (down && radios.Where((_, i) => i != slot).Any(r => r.IsSendingToTx)) overlap = true;
            return radios[slot].Apply(down);
        }
        var selected = PttController.Slot(2) | PttController.Slot(5);
        await using var ptt = new PttController(8, Apply, () => cfg, keyboardTargets: () => selected);

        async Task Expect(int slot, string step)
        {
            await Until(() => radios[slot].IsSendingToTx, what: $"{mode}: {step}");
            await Task.Delay(50);
            Require(radios.Select((r, i) => r.IsSendingToTx == (i == slot)).All(x => x), $"{mode}: {step}: wrong transmitter");
        }

        // Two radios pressed at the same instant: the one higher in the list wins.
        ptt.SetKeyboard(true);
        await Expect(2, "key pressed");

        ptt.SetMouse(PttController.Slot(7));
        await Expect(mode == PttConfig.LastWins ? 7 : 2, "button of radio 8 pressed");

        ptt.SetMouse(PttController.Slot(0));
        await Expect(mode == PttConfig.FirstWins ? 2 : 0, "button of radio 1 pressed");

        ptt.SetKeyboard(false);
        await Expect(0, "key released");

        ptt.SetMouse(PttTargets.None);
        await Until(() => radios.All(r => !r.IsSendingToTx));
        Require(!overlap, $"{mode}: two transmitters were keyed at the same time");
    }

    private static async Task SlotLimits()
    {
        foreach (var slots in new[] { 0, 9 })
        {
            try
            {
                await using var invalid = new PttController(slots, (_, _) => Task.CompletedTask);
                throw new Exception($"A controller for {slots} radios was accepted");
            }
            catch (ArgumentOutOfRangeException) { }
        }

        var radios = Enumerable.Range(0, 3).Select(_ => new FakeRadio()).ToArray();
        await using var ptt = new PttController(3, (slot, down) => radios[slot].Apply(down));
        ptt.SetMouse(PttController.Slot(5));
        await Task.Delay(80);
        Require(radios.All(r => r.Starts == 0), "A slot beyond the configured radios was keyed");
        ptt.SetMouse(PttController.Slot(2));
        await Until(() => radios[2].IsSendingToTx);
    }

    private static async Task TransmitLimit()
    {
        var radios = new[] { new FakeRadio(), new FakeRadio() };
        var cfg = new PttConfig { SingleTxAtATime = false, MaxTxSeconds = 1 };
        var timedOut = 0;
        await using var ptt = new PttController(2, (slot, down) => radios[slot].Apply(down), () => cfg);
        ptt.TimedOut += () => Interlocked.Increment(ref timedOut);

        var held = Stopwatch.StartNew();
        ptt.SetMouse(PttController.Slot(0));
        await Until(() => radios[0].IsSendingToTx);
        await Until(() => !radios[0].IsSendingToTx, 4000, "the transmit time limit");
        Require(held.ElapsedMilliseconds >= 900, $"Transmission was cut after only {held.ElapsedMilliseconds} ms");
        Require(Volatile.Read(ref timedOut) == 1, "The time limit was not reported exactly once");

        // The button is still held and the key is pressed as well: nothing may transmit.
        ptt.SetKeyboard(true);
        await Task.Delay(200);
        Require(radios.All(r => !r.IsSendingToTx), "A held input re-keyed the transmitter after the time limit");

        ptt.SetMouse(PttTargets.None);
        await Task.Delay(100);
        Require(radios.All(r => !r.IsSendingToTx), "Releasing one of two inputs cleared the time limit");
        ptt.SetKeyboard(false);

        // A fresh press works again, and 0 switches the limit off.
        cfg.MaxTxSeconds = 0;
        ptt.SetMouse(PttController.Slot(1));
        await Until(() => radios[1].IsSendingToTx);
        await Task.Delay(1400);
        Require(radios[1].IsSendingToTx && Volatile.Read(ref timedOut) == 1, "A disabled time limit ended the transmission");
        ptt.SetMouse(PttTargets.None);
        await Until(() => !radios[1].IsSendingToTx);
    }

    private static async Task Naming()
    {
        var site = Radio("R1", "  İstanbul Tower 118.1  ");
        var cfg = Config(site);
        var radio = new ScriptedRadio();
        using var vm = new SiteViewModel(site, radio, new TxLock(), () => cfg, () => { });
        Require(vm.Name == "İstanbul Tower 118.1", "The configured name is not shown as entered");
        await vm.ConnectAsync();
        Require(vm.State == RadioState.Connected, "Scripted radio did not connect");

        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var renamed = site.Clone();
        renamed.Name = "APP 120.5";
        cfg.Radios[0] = renamed;
        vm.UpdateConfig(renamed);
        await Task.Delay(100);
        Require(vm.Name == "APP 120.5" && changed.Contains(nameof(SiteViewModel.Name)), "Renaming did not reach the card");
        Require(radio.RxCalls == 1 && radio.TxCalls == 1 && radio.RxDisconnects == 0 && vm.State == RadioState.Connected,
            "Renaming a radio dropped or re-established its calls");

        var unnamed = site.Clone();
        unnamed.Name = "";
        vm.UpdateConfig(unnamed);
        Require(vm.Name == "R1", "A radio without a name must fall back to its id");
    }

    private static async Task Reconnect()
    {
        var site = Radio("R1", "TWR");
        var cfg = Config(site);
        var radio = new ScriptedRadio();
        using var vm = new SiteViewModel(site, radio, new TxLock(), () => cfg, () => { });

        Require(vm.State == RadioState.Disconnected && vm.ConnectButtonText == "Connect", "Wrong initial state");
        await vm.ConnectAsync();
        Require(vm.State == RadioState.Connected && vm.StatusText == "Connected" && vm.ConnectButtonText == "Disconnect",
            $"Not connected: {vm.State} / {vm.StatusText}");
        Require(radio.LastRxUri == "sip:RX_01@192.0.2.10:5060" && radio.LastTxUri == "sip:TX_01@192.0.2.11:5060",
            $"Wrong call targets: {radio.LastRxUri} / {radio.LastTxUri}");

        // The receiver hangs up: shown at once, retried without the operator.
        radio.DropRx();
        Require(vm.State == RadioState.Partial && vm.StatusText.Contains("TX only") && vm.StatusText.Contains("retrying"),
            $"A dropped call is not visible: {vm.State} / {vm.StatusText}");
        await Until(() => radio.RxCalls == 2 && vm.State == RadioState.Connected, 5000, "the automatic reconnect");
        Require(radio.TxCalls == 1, "The healthy transmit call was re-established as well");

        // The transmitter drops while transmitting: PTT must end and the lock must be free.
        await vm.SetPttAsync(true);
        Require(vm.PttOn, "PTT did not start");
        radio.DropTx();
        await Until(() => !vm.PttOn, what: "PTT to end with the call");

        await vm.DisconnectAsync();
        Require(vm.State == RadioState.Disconnected && !vm.IsConnectWanted && vm.ConnectButtonText == "Connect",
            $"Disconnect did not settle: {vm.State} / {vm.StatusText}");
        var calls = radio.RxCalls + radio.TxCalls;
        await Task.Delay(2600);
        Require(radio.RxCalls + radio.TxCalls == calls && vm.State == RadioState.Disconnected,
            "A radio the operator disconnected was called again");
    }

    private static async Task FailedAndCancelled()
    {
        // Without automatic reconnect a failed attempt ends with the reason on the card.
        var site = Radio("R1", "TWR");
        var cfg = Config(site);
        cfg.Connection.AutoReconnect = false;
        var radio = new ScriptedRadio
        {
            RxFailure = "RX 192.0.2.10:5060: no answer within 8 s",
            TxFailure = "TX 192.0.2.11:5060: the radio answered 486 Busy Here"
        };
        using (var vm = new SiteViewModel(site, radio, new TxLock(), () => cfg, () => { }))
        {
            await vm.ConnectAsync();
            Require(vm.State == RadioState.Error && vm.StatusText.Contains("no answer within 8 s") &&
                    vm.StatusText.Contains("486 Busy Here") && !vm.StatusText.Contains("retrying"),
                $"The reason for the failure is not shown: {vm.State} / {vm.StatusText}");
            Require(!vm.IsConnectWanted && vm.ConnectButtonText == "Connect", "A failed attempt without retry stayed pending");

            // One leg up: the half-connected state can be disconnected.
            radio.RxFailure = null;
            await vm.ConnectAsync();
            Require(vm.State == RadioState.Partial && vm.StatusText.StartsWith("RX only") && vm.ConnectButtonText == "Disconnect",
                $"Half-connected state is wrong: {vm.State} / {vm.StatusText}");
            await vm.DisconnectAsync();
            Require(vm.State == RadioState.Disconnected && !radio.IsRxConnected, "Half-connected radio could not be disconnected");
        }

        // With automatic reconnect the attempt is repeated until the operator cancels it.
        var cfg2 = Config(Radio("R2", "GND"));
        var failing = new ScriptedRadio { RxFailure = "RX: unreachable", TxFailure = "TX: unreachable" };
        using (var vm = new SiteViewModel(cfg2.Radios[0], failing, new TxLock(), () => cfg2, () => { }))
        {
            await vm.ConnectAsync();
            Require(vm.State == RadioState.Error && vm.StatusText.Contains("retrying") && vm.ConnectButtonText == "Cancel",
                $"Retry is not announced: {vm.State} / {vm.StatusText} / {vm.ConnectButtonText}");
            await vm.DisconnectAsync();
            Require(vm.State == RadioState.Disconnected, "Cancel did not stop the retries");
        }

        // An attempt that hangs (no answer yet) is cancelled by the same button.
        var cfg3 = Config(Radio("R3", "APP"));
        var hanging = new ScriptedRadio { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using (var vm = new SiteViewModel(cfg3.Radios[0], hanging, new TxLock(), () => cfg3, () => { }))
        {
            var attempt = vm.ConnectAsync();
            await Until(() => hanging.RxCalls == 1 && hanging.TxCalls == 1);
            Require(vm.State == RadioState.Connecting && vm.ConnectButtonText == "Cancel", "A running attempt cannot be cancelled");
            await vm.DisconnectAsync();
            await attempt.WaitAsync(TimeSpan.FromSeconds(2));
            Require(vm.State == RadioState.Disconnected && !vm.AnyConnected, "Cancelled attempt left the radio connected");
        }

        // A radio without an address explains itself instead of calling 127.0.0.1.
        var empty = Radio("R4", "NEW");
        empty.RxHost = "";
        var cfg4 = Config(empty);
        cfg4.Connection.AutoReconnect = false;
        var untouched = new ScriptedRadio();
        using (var vm = new SiteViewModel(empty, untouched, new TxLock(), () => cfg4, () => { }))
        {
            await vm.ConnectAsync();
            Require(untouched.RxCalls == 0 && vm.StatusText.Contains("RX: no address configured"),
                $"Missing address not reported: {vm.StatusText}");
            await vm.DisconnectAsync();
        }
    }

    private static async Task LinkModes()
    {
        var transceiver = Radio("R1", "TRX", RadioLinkMode.Transceiver, RadioProfile.Ed137BId);
        var cfg = Config(transceiver);
        var radio = new ScriptedRadio();
        using (var vm = new SiteViewModel(transceiver, radio, new TxLock(), () => cfg, () => { }))
        {
            await vm.ConnectAsync();
            Require(radio.RxCalls == 0 && radio.TxCalls == 1 && vm.State == RadioState.Connected && vm.HasTx,
                $"Transceiver: {vm.State} / {vm.StatusText}");
            Require(vm.EndpointSummary.Contains("TRX 192.0.2.11:5060") && !vm.EndpointSummary.Contains("RX 192.0.2.10"),
                "Transceiver summary lists a separate receiver");
        }

        var monitor = Radio("R2", "ATIS", RadioLinkMode.RxOnly);
        var cfg2 = Config(monitor);
        var receiver = new ScriptedRadio();
        using (var vm = new SiteViewModel(monitor, receiver, new TxLock(), () => cfg2, () => { }))
        {
            await vm.ConnectAsync();
            Require(receiver.RxCalls == 1 && receiver.TxCalls == 0 && vm.State == RadioState.Connected && !vm.HasTx,
                $"Receive-only: {vm.State} / {vm.StatusText}");
            await vm.SetPttAsync(true);
            Require(!vm.PttOn, "A receive-only radio transmitted");
        }
    }

    private static async Task AddressChange()
    {
        var site = Radio("R1", "TWR");
        var cfg = Config(site);
        var radio = new ScriptedRadio();
        using var vm = new SiteViewModel(site, radio, new TxLock(), () => cfg, () => { });
        await vm.ConnectAsync();

        var moved = site.Clone();
        moved.RxHost = "192.0.2.50";
        moved.RxUser = "RX_NEW";
        cfg.Radios[0] = moved;
        vm.UpdateConfig(moved);
        await Until(() => radio.RxCalls == 2 && vm.State == RadioState.Connected, what: "the call to the new address");
        Require(radio.LastRxUri == "sip:RX_NEW@192.0.2.50:5060" && radio.RxDisconnects >= 1,
            $"The new address was not called: {radio.LastRxUri}");
    }

    private static async Task Frequency()
    {
        foreach (var (text, khz, normalized) in new[]
                 {
                     ("123.450", 123450, "123.450"), ("123,45", 123450, "123.450"), (" 118.1 ", 118100, "118.100"),
                     ("123450", 123450, "123.450"), ("123", 123000, "123.000"), ("108", 108000, "108.000"),
                     ("156.000", 156000, "156.000"), ("335.000", 335000, "335.000"), ("243", 243000, "243.000"),
                     ("399975", 399975, "399.975"), ("121.505", 121505, "121.505")
                 })
        {
            Require(SiteViewModel.TryParseFrequencyKhz(text, out var parsed, out var shown, out var error) &&
                    parsed == khz && shown == normalized, $"'{text}' was not accepted as {normalized}: {error}");
        }
        foreach (var text in new[] { "", "  ", "abc", "107.999", "156.001", "200", "224.999", "400.001", "-123.4", "1e3", "123.4.5" })
            Require(!SiteViewModel.TryParseFrequencyKhz(text, out var parsed, out _, out var error) && parsed == 0 && error.Length > 0,
                $"'{text}' was accepted as a frequency");

        // No frequency control (SNMP off): the value is a label the operator types.
        var site = Radio("R1", "TWR");
        var cfg = Config(site);
        var saves = 0;
        using var vm = new SiteViewModel(site, new ScriptedRadio(), new TxLock(), () => cfg, () => { }, () => saves++);
        Require(!vm.HasFrequencyControl && vm.FrequencyDisplay == SiteViewModel.NoFrequency && !vm.FrequencyStale,
            "An unknown frequency must not be shown as a number");

        vm.BeginFrequencyEdit();
        vm.FrequencyInput = "118,1";
        await vm.SetFrequencyAsync();
        Require(vm.FrequencyDisplay == "118.100" && site.Frequency == "118.100" && !vm.IsFrequencyEditing && saves == 1,
            $"Label not stored: {vm.FrequencyDisplay}");

        vm.BeginFrequencyEdit();
        vm.FrequencyInput = "999";
        await vm.SetFrequencyAsync();
        Require(vm.FrequencyDisplay == "118.100" && vm.StatusText.StartsWith("Frequency not changed") && saves == 1,
            "An out-of-range frequency was stored");
        vm.CancelFrequencyEdit();

        vm.BeginFrequencyEdit();
        vm.FrequencyInput = "";
        await vm.SetFrequencyAsync();
        Require(vm.FrequencyDisplay == SiteViewModel.NoFrequency && site.Frequency == "", "The label could not be cleared");

        // The basic profile has frequency control unless it is switched off.
        var basic = Radio("R2", "LOC");
        basic.SnmpEnabled = null;
        Require(basic.HasFrequencyControl(), "The basic profile lost its frequency control");
        var generic = Radio("R3", "GEN", profile: RadioProfile.Ed137CId);
        generic.SnmpEnabled = null;
        Require(!generic.HasFrequencyControl(), "A generic ED-137 radio must not be sent SNMP requests by default");
        generic.SnmpEnabled = true;
        Require(!generic.HasFrequencyControl(), "Frequency control without an OID");
        generic.SnmpFrequencyOid = "1.3.6.1.4.1.99999.1.0";
        Require(generic.HasFrequencyControl() && generic.GetSnmpFrequencyOid() == "1.3.6.1.4.1.99999.1.0",
            "A user-supplied frequency OID is not used");
    }

    private static async Task MainView()
    {
        var names = new[] { "ALFA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "FOXTROT", "GOLF", "HOTEL" };
        foreach (var count in new[] { 1, 2, 3, 4, 5, 8 })
        {
            var config = Config(names.Take(count).Select((name, i) => Radio($"R{i}", name)).ToArray());
            config.Ui.Mode = UiConfig.Grid;
            using var view = new MainViewModel(config);
            Require(view.IsDemo && view.Radios.Count == count, "Wrong number of radio cards");
            Require(view.Radios.Select(r => r.Name).SequenceEqual(names.Take(count)), "Cards are not in the configured order");
            Require(view.GridColumns == Math.Min(count, 4), $"{count} radios must use {Math.Min(count, 4)} tiles per row, not {view.GridColumns}");
            Require(view.IsGridMode, "Grid view not selected");
        }

        var radios = names.Select((name, i) => Radio($"R{i}", name)).ToArray();
        radios[5] = Radio("R5", "FOXTROT", RadioLinkMode.RxOnly);
        radios[6] = Radio("R6", "GOLF", RadioLinkMode.Transceiver, RadioProfile.Ed137BId);
        var cfg = Config(radios);
        cfg.Ptt.MaxTxSeconds = 0;
        using var main = new MainViewModel(cfg);
        Require(!main.IsGridMode, "Classic view is the default");
        main.ToggleViewCommand.Execute(null);
        Require(main.IsGridMode && cfg.Ui.Mode == UiConfig.Grid, "The view switch did not select the grid");

        main.ToggleConnectAllCommand.Execute(null);
        await Until(() => main.Radios.All(r => r.State == RadioState.Connected), 5000, "all eight demo radios to connect");
        Require(main.AnyConnectWanted, "Connect-all state not tracked");

        // On-screen PTT keys exactly the radio whose button is held.
        main.SetMousePtt(main.Radios[2]);
        await Until(() => main.Radios[2].PttOn, what: "PTT on the third radio");
        Require(main.Radios.Count(r => r.PttOn) == 1, "PTT reached another radio");
        main.SetMousePtt(main.Radios[7]);
        await Until(() => main.Radios[7].PttOn && !main.Radios[2].PttOn, what: "PTT to move to the eighth radio");
        main.SetMousePtt(main.Radios[5]); // receive-only
        await Until(() => main.Radios.All(r => !r.PttOn), what: "PTT to stop");
        await Task.Delay(100);
        Require(main.Radios.All(r => !r.PttOn), "A receive-only radio transmitted");
        main.SetMousePtt(null);

        main.ToggleConnectAllCommand.Execute(null);
        await Until(() => main.Radios.All(r => r.State == RadioState.Disconnected), what: "all radios to disconnect");
    }
}
