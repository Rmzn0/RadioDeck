using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.Services.Audio;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using static T;

/// <summary>
/// Drives the real SIP/RTP engine against a simulated radio on 127.0.0.1 and checks
/// what actually goes over the wire. No packet leaves this PC.
/// </summary>
internal static class EngineRegression
{
    private const uint BasicAudio = 0x20400000;      // normal PTT ON, PTT-ID 1
    private const uint BasicKeyedKeepAlive = 0x00400000;
    private const uint SquelchOpen = 0x10000000;

    public static async Task Run()
    {
        await Check("Basic profile pair on loopback: INVITE, keepalives, PTT audio, speech tail, hang-up", () => WithLog(BasicPair), 40);
        await Check("ED-137B transceiver on loopback: WG67 headers, R2S offer, assigned PTT-ID", () => WithLog(Ed137Transceiver), 30);
        await Check("ED-137C receiver on loopback: receive-only offer and WG67-Version override", () => WithLog(Ed137Receiver), 30);
        await Check("A radio answering 404 to the user part is called again without it", () => WithLog(NotFoundRetry), 30);
        await Check("Refused, unanswered and cancelled calls end with a clear reason", () => WithLog(RefusedUnansweredCancelled), 40);
    }

    private sealed class Seen
    {
        public volatile bool Squelch, Keyed;
        public volatile int RxPeak, TxPeak, RxEnded, TxEnded, Errors;
    }

    // Prints the engine log when a check fails; it shows the SIP/SDP exchange.
    private static async Task WithLog(Func<ConcurrentQueue<string>, Task> body)
    {
        var log = new ConcurrentQueue<string>();
        try
        {
            await body(log);
        }
        catch
        {
            foreach (var line in log.TakeLast(80)) Console.WriteLine("    | " + line);
            throw;
        }
    }

    private static SipsorceryRadioEngine NewEngine(TestMicrophone? microphone, ConcurrentQueue<string> log, Seen seen)
    {
        var engine = new SipsorceryRadioEngine(microphone, () => new AudioConfig());
        engine.SetRxMuted(true); // keep the test PC silent
        engine.SetRxVolume(0, 0);
        engine.Log += log.Enqueue;
        engine.Error += _ => seen.Errors++;
        engine.RxSquelchChanged += open => seen.Squelch = open;
        engine.TxConfirmedChanged += keyed => seen.Keyed = keyed;
        engine.RxLevelChanged += level => { if (level > seen.RxPeak) seen.RxPeak = level; };
        engine.TxLevelChanged += level => { if (level > seen.TxPeak) seen.TxPeak = level; };
        engine.RxCallEnded += () => seen.RxEnded++;
        engine.TxCallEnded += () => seen.TxEnded++;
        return engine;
    }

    private static byte[] Frame(byte value) => Enumerable.Repeat(value, 160).ToArray();

    private static readonly byte Loud = G711.Encode((short)20000);

    // Numbered frames for the noise gate: loud speech, and background far below the gate level.
    private static byte[] LoudFrame(int number)
    {
        var frame = Frame(Loud);
        frame[0] = (byte)(0x60 + number);
        return frame;
    }

    private static byte QuietMarker(int number) => (byte)(G711.Silence ^ (number + 1));

    private static byte[] QuietFrame(int number)
    {
        var frame = G711.SilenceFrame(160);
        frame[0] = QuietMarker(number);
        return frame;
    }

    private static async Task BasicPair(ConcurrentQueue<string> log)
    {
        var microphone = new TestMicrophone();
        var seen = new Seen();
        using var receiver = new RadioSimulator();
        using var transmitter = new RadioSimulator();
        using var engine = NewEngine(microphone, log, seen);
        var site = new SiteConfig
        {
            Id = "T1", Name = "LOCALIZER", RxUser = "rx1", TxUser = "tx1", Frequency = "110.300", SnmpEnabled = false
        };

        // Both sessions are set up at the same time, as the main window does.
        await Task.WhenAll(
            engine.ConnectRxAsync($"sip:{receiver.Address}", site, default),
            engine.ConnectTxAsync($"sip:{transmitter.Address}", site, default));
        Require(engine.IsRxConnected && engine.IsTxConnected && engine.CanTransmit && !engine.IsSendingToTx,
            "Both legs must be connected with the PTT gate closed");
        Require(microphone.IsSubscribed, "The microphone must run while the transmitter is connected");

        var rxInvite = receiver.Invites.First();
        Require(rxInvite.StartsWith($"INVITE sip:rx1@{receiver.Address} SIP/2.0"), $"RX request line: {FirstLine(rxInvite)}");
        Require(rxInvite.Contains("\r\nSubject: 110.300 Rx Radio-Rxonly\r\n"), "Basic profile RX Subject header changed");
        Require(Regex.IsMatch(rxInvite, @"\r\nFrom: [^\r]*sip:rx1@127\.0\.0\.1[:>;]"), "RX From header must carry the RX user and the local address");
        Require(Regex.IsMatch(rxInvite, @"\r\nm=audio \d+ RTP/AVP 8\r\n") && rxInvite.Contains("a=rtpmap:8 PCMA/8000") &&
                !rxInvite.Contains("R2S/8000") && !rxInvite.Contains("telephone-event"), "The basic offer must list PCMA only");
        Require(rxInvite.Contains("\r\na=recvonly\r\n") && rxInvite.Contains("c=IN IP4 127.0.0.1\r\n"), "RX offer direction or address wrong");
        Require(rxInvite.Contains("a=R2S-KeepAlivePeriod:200") && rxInvite.Contains("a=R2S-KeepAliveMultiplier:10") &&
                rxInvite.Contains("a=ptime:20"), "R2S attributes missing from the offer");
        Require(!rxInvite.Contains("WG67-Version", StringComparison.OrdinalIgnoreCase) && !rxInvite.Contains("\r\nPriority:") &&
                !rxInvite.Contains("a=type:"), "Basic profile calls must not carry the generic ED-137 additions");

        var txInvite = transmitter.Invites.First();
        Require(txInvite.StartsWith($"INVITE sip:tx1@{transmitter.Address} SIP/2.0"), $"TX request line: {FirstLine(txInvite)}");
        Require(txInvite.Contains("\r\nSubject: 110.300 TxRx Radio-TxRx\r\n") && txInvite.Contains("\r\na=sendrecv\r\n") &&
                Regex.IsMatch(txInvite, @"\r\nm=audio \d+ RTP/AVP 8\r\n"), "Basic profile TX INVITE changed");

        // Idle: R2S keepalives every 200 ms on both legs, PTT OFF.
        await Until(() => receiver.Rtp.Count >= 4 && transmitter.Rtp.Count >= 4, what: "idle keepalives");
        foreach (var packets in new[] { receiver.Snapshot(), transmitter.Snapshot() })
        {
            Require(packets.All(p => p.PayloadType == 123 && p.Word == 0 && p.Payload.Length == 0), "Idle packets must be empty R2S keepalives with PTT OFF");
            Require(packets.Zip(packets.Skip(1), (a, b) => b.Timestamp - a.Timestamp).All(step => step == 1600), "Keepalive timestamps must advance by 200 ms");
        }

        // Transmission: 30 numbered "speech" frames. The first ones are captured while the
        // radio is still keying up and the last ones are still queued when PTT is released.
        transmitter.Clear();
        await engine.StartTxAudioAsync(default);
        Require(engine.IsSendingToTx, "PTT did not open the gate");
        for (var i = 0; i < 30; i++)
        {
            microphone.Push(Frame((byte)(0x20 + i)));
            await Task.Delay(20);
        }
        await engine.StopTxAudioAsync(default);
        Require(!engine.IsSendingToTx, "PTT release did not close the gate");
        await Task.Delay(500);

        var sent = transmitter.Snapshot();
        var audio = sent.Where(p => p.PayloadType == 8).ToArray();
        Require(audio.Length >= 35 && audio.All(p => p.Word == BasicAudio && p.Payload.Length == 160),
            $"Every audio packet must be 20 ms PCMA with PTT ON/PTT-ID 1 ({audio.Length} packets)");
        var speech = audio.Where(p => p.Payload[0] != G711.Silence).Select(p => p.Payload[0]).ToArray();
        Require(speech.SequenceEqual(Enumerable.Range(0, 30).Select(i => (byte)(0x20 + i))),
            $"Speech was lost or re-ordered (first syllable / last word): {Convert.ToHexString(speech)}");
        Require(Array.FindIndex(audio, p => p.Payload[0] != G711.Silence) >= 5, "The transmitter was not keyed with silence before the speech");
        Require(audio.Zip(audio.Skip(1), (a, b) => b.Timestamp - a.Timestamp).All(step => step == 160), "Audio timestamps must advance by 20 ms");

        var firstAudio = Array.FindIndex(sent, p => p.PayloadType == 8);
        var lastAudio = Array.FindLastIndex(sent, p => p.PayloadType == 8);
        Require(sent.Take(firstAudio).Count(p => p.PayloadType == 123 && p.Word == BasicKeyedKeepAlive) >= 4,
            "The wake-up keepalive burst (PTT-ID only) is missing");
        Require(sent.Where(p => p.PayloadType == 123).All(p => p.Payload.Length == 0 && p.Word is 0 or BasicKeyedKeepAlive),
            "Unexpected keepalive contents");
        var tail = sent.Skip(lastAudio + 1).ToArray();
        Require(tail.Length >= 3 && tail.All(p => p.PayloadType == 123 && p.Word == 0), "PTT OFF keepalives after the transmission are missing");
        Require(sent.Zip(sent.Skip(1), (a, b) => (ushort)(b.Sequence - a.Sequence)).All(step => step == 1), "RTP sequence numbers are not continuous");

        transmitter.Clear();
        await Task.Delay(300);
        Require(transmitter.Snapshot().All(p => p.PayloadType == 123 && p.Word == 0), "Audio or PTT ON was sent after the release");
        Require(receiver.Snapshot().All(p => p.PayloadType == 123 && p.Word == 0), "The receive session must never carry audio or PTT");

        // No microphone frames at all (device trouble): silence keeps the transmitter keyed.
        transmitter.Clear();
        await engine.StartTxAudioAsync(default);
        await Task.Delay(400);
        await engine.StopTxAudioAsync(default);
        var silent = transmitter.Snapshot().Where(p => p.PayloadType == 8).ToArray();
        Require(silent.Length >= 8 && silent.All(p => p.Word == BasicAudio && p.Payload.All(b => b == G711.Silence)),
            $"Without microphone frames the transmitter must be fed silence ({silent.Length} packets)");

        // The key is released in the middle of a word. What was still inside the capture device
        // arrives a moment later and must go out, and the transmitter stays keyed afterwards so
        // the radio can play out its own buffer.
        transmitter.Clear();
        await engine.StartTxAudioAsync(default, new AudioConfig { Vad = false, TxLeadMs = 60, TxTailMs = 120 });
        for (var i = 0; i < 12; i++)
        {
            microphone.Push(Frame((byte)(0x40 + i)));
            await Task.Delay(20);
        }
        var release = engine.StopTxAudioAsync(default);
        microphone.Push(Frame(0x4C));
        microphone.Push(Frame(0x4D));
        await release;
        Require(!engine.IsSendingToTx, "The release did not complete");
        await Task.Delay(300);
        var ended = transmitter.Snapshot().Where(p => p.PayloadType == 8).ToArray();
        var endedSpeech = ended.Where(p => p.Payload[0] != G711.Silence).Select(p => p.Payload[0]).ToArray();
        Require(endedSpeech.SequenceEqual(Enumerable.Range(0, 14).Select(i => (byte)(0x40 + i))),
            $"The end of the transmission was cut: {Convert.ToHexString(endedSpeech)}");
        var held = ended.Length - 1 - Array.FindLastIndex(ended, p => p.Payload[0] != G711.Silence);
        Require(held >= 3 && ended.All(p => p.Word == BasicAudio), $"The transmitter was not held keyed after the last word ({held} packets)");

        // Noise gate: background between words is replaced by silence, but when speech starts,
        // the frames leading up to it are sent first, so the beginning of the word is complete.
        transmitter.Clear();
        await engine.StartTxAudioAsync(default, new AudioConfig { Vad = true, TxLeadMs = 60, TxTailMs = 60 });
        for (var i = 0; i < 10; i++)
        {
            microphone.Push(QuietFrame(i));
            await Task.Delay(20);
        }
        for (var i = 0; i < 10; i++)
        {
            microphone.Push(LoudFrame(i));
            await Task.Delay(20);
        }
        await engine.StopTxAudioAsync(default);
        await Task.Delay(300);
        var gated = transmitter.Snapshot().Where(p => p.PayloadType == 8).Select(p => p.Payload).ToArray();
        var words = gated.Where(p => p[1] == Loud).Select(p => p[0]).ToArray();
        Require(words.SequenceEqual(Enumerable.Range(0, 10).Select(i => (byte)(0x60 + i))),
            $"The noise gate cut the beginning of the word: {Convert.ToHexString(words)}");
        var firstWord = Array.FindIndex(gated, p => p[1] == Loud);
        Require(firstWord >= 3 && gated[firstWord - 1][0] == QuietMarker(9) && gated[firstWord - 2][0] == QuietMarker(8) &&
                gated[firstWord - 3][0] == QuietMarker(7), "The frames leading up to the word were not sent");
        Require(gated.All(p => p[0] != QuietMarker(0) && p[0] != QuietMarker(3)), "Background noise passed the closed gate");

        // Radio -> console: squelch bit, received audio level, keyed confirmation.
        receiver.KeepAliveWord = SquelchOpen;
        await Until(() => seen.Squelch, what: "the squelch indication");
        for (var i = 0; i < 12; i++)
        {
            await receiver.SendAudio(Loud, SquelchOpen);
            await Task.Delay(20);
        }
        await Until(() => seen.RxPeak > 30, what: "the receive level");
        receiver.KeepAliveWord = 0;
        await Until(() => !seen.Squelch, what: "the squelch to close");
        transmitter.KeepAliveWord = BasicAudio;
        await Until(() => seen.Keyed, what: "the keyed confirmation");
        transmitter.KeepAliveWord = 0;
        await Until(() => !seen.Keyed, what: "the keyed confirmation to clear");

        // The receiver hangs up: reported, the transmitter stays connected, and it can be called again.
        await receiver.Hangup();
        await Until(() => seen.RxEnded == 1 && !engine.IsRxConnected, what: "the remote hang-up");
        Require(engine.IsTxConnected && seen.TxEnded == 0, "A receiver hang-up took the transmitter down");
        await engine.ConnectRxAsync($"sip:{receiver.Address}", site, default);
        Require(engine.IsRxConnected && receiver.Invites.Count >= 2, "The receiver could not be called again");

        // A local disconnect sends BYE, is not reported as a dropped call and releases the microphone.
        await engine.DisconnectTxAsync(default);
        await Until(() => transmitter.Byes == 1, what: "BYE to the transmitter");
        Require(!engine.IsTxConnected && seen.TxEnded == 0 && !microphone.IsSubscribed, "Local disconnect was not clean");
        await engine.StartTxAudioAsync(default);
        Require(!engine.IsSendingToTx, "PTT without a transmitter session must be ignored");

        engine.Dispose();
        await Until(() => receiver.Byes == 1, what: "BYE to the receiver on shutdown");
        Require(seen.RxEnded == 1 && seen.Errors == 0, "Shutdown was reported as a dropped call or an error");
        Require(receiver.Fault == null && transmitter.Fault == null, $"Simulator problem: {receiver.Fault ?? transmitter.Fault}");
    }

    private static async Task Ed137Transceiver(ConcurrentQueue<string> log)
    {
        var seen = new Seen();
        using var radio = new RadioSimulator { AnswerAttributes = "a=ptt-id:5\r\n" };
        using var engine = NewEngine(new TestMicrophone(), log, seen);
        var site = new SiteConfig
        {
            Id = "T2", Name = "GROUND", Profile = RadioProfile.Ed137BId, LinkMode = nameof(RadioLinkMode.Transceiver),
            TxUser = "GND_TRX", Frequency = "121.900", SnmpEnabled = false
        };

        await engine.ConnectTxAsync($"sip:{radio.Address}", site, default);
        Require(engine.IsTxConnected && engine.CanTransmit && !engine.IsRxConnected, "Transceiver session not established");

        var invite = radio.Invites.First();
        Require(invite.StartsWith($"INVITE sip:GND_TRX@{radio.Address} SIP/2.0"), $"Request line: {FirstLine(invite)}");
        Require(invite.Contains("\r\nWG67-Version: radio.01\r\n") && invite.Contains("\r\nSubject: radio\r\n") &&
                invite.Contains("\r\nPriority: normal\r\n"), "ED-137B SIP headers missing");
        Require(Regex.IsMatch(invite, @"\r\nm=audio \d+ RTP/AVP 8 123\r\n") && invite.Contains("a=rtpmap:8 PCMA/8000") &&
                invite.Contains("a=rtpmap:123 R2S/8000"), "ED-137B offer must list PCMA and R2S");
        foreach (var attribute in new[] { "a=type:Radio-TxRx", "a=txrxmode:TxRx", "a=sigtime:1", "a=ptt_rep:0", "a=rtphe:1",
                     "a=fid:121.900", "a=sendrecv", "a=R2S-KeepAlivePeriod:200", "a=R2S-KeepAliveMultiplier:10" })
            Require(invite.Contains($"\r\n{attribute}\r\n"), $"ED-137B offer lacks {attribute}");

        await Until(() => radio.Rtp.Count >= 3, what: "idle keepalives");
        Require(radio.Snapshot().All(p => p.PayloadType == 123 && p.Word == 0), "Idle keepalives must carry PTT OFF");

        // The radio assigned PTT-ID 5; every packet of the transmission carries PTT ON with it.
        const uint keyed = 0x21400000;
        radio.Clear();
        await engine.StartTxAudioAsync(default);
        await Task.Delay(450);
        await engine.StopTxAudioAsync(default);
        await Task.Delay(400);
        var sent = radio.Snapshot();
        var audio = sent.Where(p => p.PayloadType == 8).ToArray();
        Require(audio.Length >= 8 && audio.All(p => p.Word == keyed && p.Payload.Length == 160), "Audio must carry PTT ON with the assigned PTT-ID 5");
        var first = Array.FindIndex(sent, p => p.PayloadType == 8);
        var last = Array.FindLastIndex(sent, p => p.PayloadType == 8);
        Require(sent.Take(last).Where(p => p.PayloadType == 123).All(p => p.Word is 0 or keyed) &&
                sent.Skip(first).Take(last - first).Where(p => p.PayloadType == 123).All(p => p.Word == keyed),
            "Keepalives during a transmission must carry PTT ON for a standard radio");
        Require(sent.Skip(last + 1).Count(p => p.PayloadType == 123 && p.Word == 0) >= 3 && sent.Skip(last + 1).All(p => p.Word == 0),
            "PTT OFF keepalives after the transmission are missing");

        // What the transceiver sends back is the radio's receive audio.
        radio.KeepAliveWord = SquelchOpen;
        for (var i = 0; i < 12; i++)
        {
            await radio.SendAudio(Loud, SquelchOpen);
            await Task.Delay(20);
        }
        await Until(() => seen.RxPeak > 30 && seen.Squelch, what: "receive audio on the transceiver session");
        Require(seen.TxPeak == 0, "Received transceiver audio was shown on the transmit meter");
        radio.KeepAliveWord = keyed;
        await Until(() => seen.Keyed && !seen.Squelch, what: "the keyed confirmation");

        await radio.Hangup();
        await Until(() => seen.TxEnded == 1 && !engine.IsTxConnected && !seen.Keyed, what: "the remote hang-up");
        Require(radio.Fault == null, $"Simulator problem: {radio.Fault}");
    }

    private static async Task Ed137Receiver(ConcurrentQueue<string> log)
    {
        var seen = new Seen();
        using var radio = new RadioSimulator();
        using var engine = NewEngine(null, log, seen);
        var site = new SiteConfig
        {
            Id = "T3", Name = "ATIS", Profile = RadioProfile.Ed137CId, LinkMode = nameof(RadioLinkMode.RxOnly),
            RxUser = "ATIS_RX", SnmpEnabled = false
        };

        await engine.ConnectRxAsync($"sip:ATIS_RX@{radio.Address}", site, default);
        var invite = radio.Invites.Last();
        Require(invite.Contains("\r\nWG67-Version: radio.02\r\n") && invite.Contains("\r\nSubject: radio\r\n"), "ED-137C headers missing");
        Require(Regex.IsMatch(invite, @"\r\nm=audio \d+ RTP/AVP 8 123\r\n") && invite.Contains("\r\na=type:Radio-Rxonly\r\n") &&
                invite.Contains("\r\na=txrxmode:Rx\r\n") && invite.Contains("\r\na=recvonly\r\n") && !invite.Contains("a=fid:"),
            "ED-137C receive-only offer is wrong");

        radio.KeepAliveWord = SquelchOpen;
        await radio.SendAudio(Loud, SquelchOpen);
        await Until(() => seen.Squelch, what: "the squelch indication");

        // Some radios insist on a particular WG67-Version; it can be overridden per radio.
        var custom = site.Clone();
        custom.Wg67Version = "radio.01";
        var invites = radio.Invites.Count;
        await engine.ConnectRxAsync($"sip:ATIS_RX@{radio.Address}", custom, default);
        Require(radio.Invites.Count > invites && radio.Invites.Last().Contains("\r\nWG67-Version: radio.01\r\n"),
            "The WG67-Version override did not reach the INVITE");
        Require(engine.IsRxConnected && seen.RxEnded == 0, "Calling again was reported as a dropped call");
        Require(radio.Fault == null, $"Simulator problem: {radio.Fault}");
    }

    private static async Task NotFoundRetry(ConcurrentQueue<string> log)
    {
        using var radio = new RadioSimulator { Behaviour = RadioSimulator.Answer.NotFoundForUser };
        using var engine = NewEngine(null, log, new Seen());
        var site = new SiteConfig { Id = "T4", Name = "TWR", RxUser = "rx9", SnmpEnabled = false };

        await engine.ConnectRxAsync($"sip:rx9@{radio.Address}", site, default);
        Require(engine.IsRxConnected, "The call without the user part did not connect");
        var requests = radio.Invites.Select(FirstLine).Distinct().ToArray();
        Require(requests.SequenceEqual(new[]
        {
            $"INVITE sip:rx9@{radio.Address} SIP/2.0",
            $"INVITE sip:{radio.Address} SIP/2.0"
        }), $"Unexpected INVITE sequence: {string.Join(" | ", requests)}");
        Require(radio.Fault == null, $"Simulator problem: {radio.Fault}");
    }

    private static async Task RefusedUnansweredCancelled(ConcurrentQueue<string> log)
    {
        var microphone = new TestMicrophone();
        var seen = new Seen();
        var site = new SiteConfig { Id = "T5", Name = "TWR", RxUser = "RX", TxUser = "TX", SnmpEnabled = false };
        using var engine = NewEngine(microphone, log, seen);

        using (var busy = new RadioSimulator { Behaviour = RadioSimulator.Answer.Busy })
        {
            var clock = Stopwatch.StartNew();
            var error = await Fails<RadioCallException>(() => engine.ConnectTxAsync($"sip:{busy.Address}", site, default));
            Require(error.Message.StartsWith($"TX {busy.Address}: ") && error.Message.Contains("486"),
                $"The refusal is not explained: {error.Message}");
            Require(clock.ElapsedMilliseconds < 3000, "A refused call must fail at once");
            Require(!engine.IsTxConnected && !engine.CanTransmit && !microphone.IsSubscribed && seen.Errors == 1,
                "A refused call left state behind");
        }

        using (var silent = new RadioSimulator { Behaviour = RadioSimulator.Answer.Silent })
        {
            // The operator presses Cancel while nothing answers.
            using var cancel = new CancellationTokenSource(400);
            var clock = Stopwatch.StartNew();
            await Fails<OperationCanceledException>(() => engine.ConnectRxAsync($"sip:{silent.Address}", site, cancel.Token));
            Require(clock.ElapsedMilliseconds < 3000 && !engine.IsRxConnected, "Cancel did not end the attempt");

            // Nobody answers: give up after the connect timeout, not after the 32 s SIP timeout.
            clock.Restart();
            var error = await Fails<RadioCallException>(() => engine.ConnectRxAsync($"sip:{silent.Address}", site, default));
            var expected = $"no answer within {SipsorceryRadioEngine.ConnectTimeoutMs / 1000} s";
            Require(error.Message == $"RX {silent.Address}: {expected}", $"Unanswered call: {error.Message}");
            Require(clock.ElapsedMilliseconds is > 7000 and < 12000, $"Gave up after {clock.ElapsedMilliseconds} ms");
            Require(!engine.IsRxConnected && seen.RxEnded == 0, "An unanswered call left state behind");
        }

        // The same engine still works afterwards.
        using var radio = new RadioSimulator();
        await engine.ConnectRxAsync($"sip:{radio.Address}", site, default);
        Require(engine.IsRxConnected, "The engine could not call after failed attempts");
    }

    private static async Task<TException> Fails<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException expected)
        {
            return expected;
        }
        throw new Exception($"Expected {typeof(TException).Name}, but the call succeeded");
    }

    private static string FirstLine(string text) => text.Split("\r\n", 2)[0];
}

/// <summary>A minimal ED-137 radio on 127.0.0.1: answers INVITE, sends R2S keepalives, records what it receives.</summary>
internal sealed class RadioSimulator : IDisposable
{
    public enum Answer { Accept, NotFoundForUser, Busy, Silent }

    public readonly record struct Packet(int PayloadType, ushort Sequence, uint Timestamp, uint Word, byte[] Payload);

    private readonly UdpClient _sip = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _rtp;
    private readonly UdpClient _rtcp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _tasks;
    private volatile IPEndPoint? _clientSip;
    private volatile IPEndPoint? _clientRtp;
    private volatile SIPRequest? _invite;
    private volatile SIPResponse? _answer;
    private int _sequence;
    private uint _keepAliveWord;

    public Answer Behaviour = Answer.Accept;

    /// <summary>Extra SDP lines for the answer, e.g. "a=ptt-id:5\r\n".</summary>
    public string AnswerAttributes = "";

    /// <summary>Raw INVITE requests as received.</summary>
    public readonly ConcurrentQueue<string> Invites = new();

    /// <summary>Raw RTP packets as received.</summary>
    public readonly ConcurrentQueue<byte[]> Rtp = new();

    public int Byes;

    /// <summary>The radio sends nothing on RTP yet (its first packet has not arrived).</summary>
    public volatile bool RtpQuiet;
    public volatile string? Fault;

    public RadioSimulator()
    {
        // Reserve RTP and RTCP ports as a pair so RTCP cannot land on another simulator's SIP socket.
        while (true)
        {
            var rtp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
            if (port == 65535) { rtp.Dispose(); continue; }
            try
            {
                _rtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));
                _rtp = rtp;
                break;
            }
            catch (SocketException) { rtp.Dispose(); }
        }
        _tasks = new[] { Task.Run(ListenSip), Task.Run(ListenRtp), Task.Run(SendKeepAlives) };
    }

    public string Address => $"127.0.0.1:{((IPEndPoint)_sip.Client.LocalEndPoint!).Port}";

    /// <summary>First word of the header extension in the keepalives this radio sends (SQU / PTT echo).</summary>
    public uint KeepAliveWord
    {
        get => Volatile.Read(ref _keepAliveWord);
        set => Volatile.Write(ref _keepAliveWord, value);
    }

    public Packet[] Snapshot() => Rtp.ToArray().Select(Parse).ToArray();

    public void Clear()
    {
        while (Rtp.TryDequeue(out _)) { }
    }

    private static Packet Parse(byte[] p)
    {
        // Everything the console sends has the fixed ED-137 layout: no CSRC, one extension word.
        if (p.Length < 20 || p[0] != 0x90 || p[12] != 0x01 || p[13] != 0x67 || p[14] != 0 || p[15] != 1)
            throw new Exception($"Packet without the ED-137 header extension (0x0167, one word): {Convert.ToHexString(p.AsSpan(0, Math.Min(p.Length, 20)))}");
        return new Packet(p[1] & 0x7F, (ushort)((p[2] << 8) | p[3]),
            (uint)((p[4] << 24) | (p[5] << 16) | (p[6] << 8) | p[7]),
            (uint)((p[16] << 24) | (p[17] << 16) | (p[18] << 8) | p[19]), p[20..]);
    }

    private byte[] BuildPacket(int payloadType, uint word, byte[] payload)
    {
        var sequence = (ushort)Interlocked.Increment(ref _sequence);
        var packet = new byte[20 + payload.Length];
        packet[0] = 0x90;
        packet[1] = (byte)payloadType;
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        var timestamp = (uint)sequence * 160;
        packet[4] = (byte)(timestamp >> 24); packet[5] = (byte)(timestamp >> 16); packet[6] = (byte)(timestamp >> 8); packet[7] = (byte)timestamp;
        packet[11] = 0x77; // SSRC
        packet[12] = 0x01; packet[13] = 0x67; packet[15] = 1;
        packet[16] = (byte)(word >> 24); packet[17] = (byte)(word >> 16); packet[18] = (byte)(word >> 8); packet[19] = (byte)word;
        payload.CopyTo(packet, 20);
        return packet;
    }

    public async Task SendAudio(byte value, uint word)
    {
        if (_clientRtp is { } client)
            await _rtp.SendAsync(BuildPacket(8, word, Enumerable.Repeat(value, 160).ToArray()), client);
    }

    /// <summary>The radio ends the call.</summary>
    public async Task Hangup()
    {
        var invite = _invite ?? throw new InvalidOperationException("No call to hang up");
        var port = ((IPEndPoint)_sip.Client.LocalEndPoint!).Port;
        _clientRtp = null;
        var request = $"BYE {invite.Header.Contact[0].ContactURI} SIP/2.0\r\n" +
            $"Via: SIP/2.0/UDP 127.0.0.1:{port};branch=z9hG4bK{Guid.NewGuid():N}\r\n" +
            $"From: {_answer!.Header.To}\r\nTo: {invite.Header.From}\r\nCall-ID: {invite.Header.CallId}\r\n" +
            "CSeq: 2 BYE\r\nMax-Forwards: 70\r\nContent-Length: 0\r\n\r\n";
        await _sip.SendAsync(Encoding.UTF8.GetBytes(request), _clientSip!);
    }

    private async Task ListenSip()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var received = await _sip.ReceiveAsync(_stop.Token);
                if (received.Buffer.All(b => b is 0 or 10 or 13)) continue; // keep-alive probes
                var text = Encoding.UTF8.GetString(received.Buffer);
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("SIP/2.0")) continue; // responses to our BYE

                var request = SIPRequest.ParseSIPRequest(text);
                if (request.Method == SIPMethodsEnum.ACK) continue;

                SIPResponse response;
                if (request.Method == SIPMethodsEnum.INVITE)
                {
                    Invites.Enqueue(text);
                    if (Behaviour == Answer.Silent) continue;
                    if (Behaviour == Answer.Busy)
                        response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.BusyHere, null);
                    else if (Behaviour == Answer.NotFoundForUser && !string.IsNullOrEmpty(request.URI.User))
                        response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.NotFound, null);
                    else
                        response = Accept(request, received.RemoteEndPoint);
                }
                else
                {
                    if (request.Method == SIPMethodsEnum.BYE)
                    {
                        _clientRtp = null;
                        Interlocked.Increment(ref Byes);
                    }
                    response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                }

                await _sip.SendAsync(Encoding.UTF8.GetBytes(response.ToString()), received.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { } // ICMP "port unreachable" from a console socket that is already closed
            catch (Exception ex) { Fault = ex.ToString(); }
        }
    }

    private SIPResponse Accept(SIPRequest request, IPEndPoint remote)
    {
        var offer = SDP.ParseSDPDescription(request.Body);
        var response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
        response.Header.To.ToTag = "sim" + Guid.NewGuid().ToString("N")[..8];
        response.Header.Contact = new List<SIPContactHeader> { new(null, SIPURI.ParseSIPURI($"sip:radio@{Address}")) };
        response.Header.ContentType = SDP.SDP_MIME_CONTENTTYPE;
        // Like real radios, the answer lists the R2S payload type next to PCMA.
        response.Body =
            "v=0\r\no=- 42 1 IN IP4 127.0.0.1\r\ns=radio\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n" +
            $"m=audio {((IPEndPoint)_rtp.Client.LocalEndPoint!).Port} RTP/AVP 8 123\r\n" +
            "a=rtpmap:8 PCMA/8000\r\na=rtpmap:123 R2S/8000\r\na=sendrecv\r\n" + AnswerAttributes;
        response.Header.ContentLength = Encoding.UTF8.GetByteCount(response.Body);

        _invite = request;
        _answer = response;
        _clientSip = remote;
        _clientRtp = new IPEndPoint(IPAddress.Loopback, offer.Media[0].Port);
        return response;
    }

    private async Task ListenRtp()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var received = await _rtp.ReceiveAsync(_stop.Token);
                Rtp.Enqueue(received.Buffer);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    private async Task SendKeepAlives()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!RtpQuiet && _clientRtp is { } client)
                    await _rtp.SendAsync(BuildPacket(123, KeepAliveWord, Array.Empty<byte>()), client);
                await Task.Delay(100, _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { Task.WhenAll(_tasks).Wait(TimeSpan.FromSeconds(2)); } catch { }
        _sip.Dispose();
        _rtp.Dispose();
        _rtcp.Dispose();
        _stop.Dispose();
    }
}
