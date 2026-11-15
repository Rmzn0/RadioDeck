using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.ViewModels;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

internal static class RemainingRegression
{
    public static async Task Run()
    {
        foreach (var mode in new[] { "FIRST_WINS", "LAST_WINS", "PRIORITY_FIRST", "PRIORITY_LOC", "None" })
            await Check($"Arbitration {mode}: ordered OFF, selection and release", () => Arbitration(mode));
        await Check("Arbitration skips disconnected LOC and honors multi-TX mode", async () =>
        {
            var cfg = new PttConfig();
            var loc = new FakeRadio();
            var gp = new FakeRadio();
            var connected = PttTargets.Glidepath;
            await using var ptt = new PttController(loc.Apply, gp.Apply, () => cfg, () => connected);
            ptt.SetKeyboard(true);
            await Until(() => gp.IsSendingToTx);
            Require(!loc.IsSendingToTx, "Disconnected LOC won arbitration");
            ptt.SetKeyboard(false);
            await Until(() => !gp.IsSendingToTx);
            connected = PttTargets.Both;
            cfg.SingleTxAtATime = false;
            ptt.SetKeyboard(true);
            await Until(() => loc.IsSendingToTx && gp.IsSendingToTx);
        });
        await Check("Failed OFF prevents switching to a different transmitter", async () =>
        {
            var gp = new FakeRadio();
            var cfg = new PttConfig();
            var fail = false;
            var errors = 0;
            await using var ptt = new PttController(down => !down && fail
                ? throw new InvalidOperationException("stop") : Task.CompletedTask,
                gp.Apply, () => cfg);
            ptt.Error += _ => Interlocked.Increment(ref errors);
            ptt.SetMouse(PttTargets.Localizer);
            await Task.Delay(50);
            fail = true;
            ptt.SetMouse(PttTargets.Glidepath);
            await Until(() => errors > 0);
            Require(gp.Starts == 0, "Switched TX despite failed OFF");
            fail = false;
        });
        await Check("Connected but inactive LOC cannot suppress or preempt usable GP", async () =>
        {
            foreach (var mode in new[] { "FIRST_WINS", "LAST_WINS", "PRIORITY_FIRST" })
            {
                var cfg = AppConfig.CreateDefault();
                cfg.Ptt.Arbitration = mode;
                var txLock = new TxLock();
                var locRadio = new FakeRadio { AllowsTx = false };
                var gpRadio = new FakeRadio();
                using var loc = Program.Site("LOC", locRadio, txLock, cfg);
                using var gp = Program.Site("GP", gpRadio, txLock, cfg);
                await using var ptt = new PttController(loc.SetPttAsync, gp.SetPttAsync, () => cfg.Ptt,
                    () => (loc.CanPtt ? PttTargets.Localizer : 0) | (gp.CanPtt ? PttTargets.Glidepath : 0),
                    () => (loc.IsTransmitting ? PttTargets.Localizer : 0) | (gp.IsTransmitting ? PttTargets.Glidepath : 0));
                ptt.SetKeyboard(true);
                await Until(() => gpRadio.IsSendingToTx);
                Require(locRadio.Starts == 0, "Ineligible LOC was started");
                ptt.SetKeyboard(false);
                await Until(() => !gpRadio.IsSendingToTx);
                ptt.SetMouse(PttTargets.Glidepath);
                await Until(() => gpRadio.IsSendingToTx);
                var starts = gpRadio.Starts;
                ptt.SetKeyboard(true);
                await Task.Delay(80);
                Require(gpRadio.IsSendingToTx && gpRadio.Starts == starts && locRadio.Starts == 0,
                    "Ineligible LOC preempted transmitting GP");
            }
        });
        await Check("Settings preserve each arbitration choice and mute switch", () =>
        {
            // Names written by version 1 are mapped onto the three supported modes.
            foreach (var (stored, expected) in new[] { ("FIRST_WINS", "FIRST_WINS"), ("LAST_WINS", "LAST_WINS"),
                         ("PRIORITY_FIRST", "PRIORITY_FIRST"), ("PRIORITY_LOC", "PRIORITY_FIRST"), ("None", "FIRST_WINS") })
            {
                var cfg = AppConfig.CreateDefault();
                cfg.Ptt.Arbitration = stored;
                var settings = new SettingsViewModel(cfg) { MuteRxWhileTx = false };
                Require(settings.ArbitrationOptions.Contains(settings.Arbitration) && settings.Arbitration == expected,
                    "Saved arbitration is not one of the offered choices");
                var saved = settings.ToConfig();
                Require(saved.Ptt.Arbitration == expected && !saved.Ptt.MuteRxWhileTx, "Settings lost value");
            }
            return Task.CompletedTask;
        });
        await Check("RX gate silences both sink APIs without mutating RTP payloads", TestPlaybackGate);
        await Check("Upgraded ED137 RX/TX SDP and malformed UDP recovery (loopback only)", TestMedia);
        await Check("SDP preserves hold/direction and rejects unusable or unsupported media", TestSdpValidation);
        await Check("Malformed SCTP SACK counts are rejected at parser boundary", TestSctp);
    }

    private static async Task Arbitration(string mode)
    {
        var cfg = new PttConfig { Arbitration = mode };
        var loc = new FakeRadio();
        var gp = new FakeRadio();
        var overlap = false;
        Task Loc(bool down) { if (down && gp.IsSendingToTx) overlap = true; return loc.Apply(down); }
        Task Gp(bool down) { if (down && loc.IsSendingToTx) overlap = true; return gp.Apply(down); }
        await using var ptt = new PttController(Loc, Gp, () => cfg);
        ptt.SetMouse(PttTargets.Glidepath);
        await Until(() => gp.IsSendingToTx);
        ptt.SetKeyboard(true);
        if (mode is "LAST_WINS" or "PRIORITY_FIRST" or "PRIORITY_LOC") await Until(() => loc.IsSendingToTx && !gp.IsSendingToTx);
        else { await Task.Delay(60); Require(gp.IsSendingToTx && !loc.IsSendingToTx, "First owner lost priority"); }
        ptt.SetKeyboard(false);
        await Until(() => gp.IsSendingToTx && !loc.IsSendingToTx);
        ptt.SetMouse(PttTargets.None);
        await Until(() => !gp.IsSendingToTx);

        ptt.SetKeyboard(true);
        await Until(() => loc.IsSendingToTx);
        ptt.SetMouse(PttTargets.Glidepath);
        if (mode == "LAST_WINS") await Until(() => gp.IsSendingToTx && !loc.IsSendingToTx);
        else { await Task.Delay(60); Require(loc.IsSendingToTx && !gp.IsSendingToTx, "Wrong priority owner"); }
        Require(!overlap, "Both transmitters were on during ownership transfer");
    }

    private static async Task TestPlaybackGate()
    {
        var sink = new TestAudio();
        var muted = false;
        var gate = new RxPlaybackGate(sink, () => muted);
        var payload = Enumerable.Repeat((byte)0xAA, 160).ToArray();
        var remote = new IPEndPoint(IPAddress.Loopback, 12345);
        await gate.StartAudioSink();
        gate.GotAudioRtp(remote, 1, 2, 160, 8, false, payload);
        Require(ReferenceEquals(sink.LastAudio, payload), "Unmuted audio changed");
        muted = true;
        gate.GotAudioRtp(remote, 1, 3, 320, 8, false, payload);
        Require(sink.LastAudio!.All(x => x == 0xD5) && payload.All(x => x == 0xAA), "Invalid silence/payload modified");
        gate.GotEncodedMediaFrame(new EncodedAudioFrame(0, TestAudio.Pcma, 20, payload));
        Require(sink.LastAudio!.All(x => x == 0xD5), "New sink API bypassed mute");
        muted = false;
        gate.GotEncodedMediaFrame(new EncodedAudioFrame(0, TestAudio.Pcma, 20, payload));
        Require(ReferenceEquals(sink.LastAudio, payload), "Audio did not resume");
        await gate.CloseAudioSink();
        Require(sink.Started && sink.Closed, "Sink lifecycle not forwarded");
    }

    private static async Task TestMedia()
    {
        var assembly = typeof(SipsorceryRadioEngine).Assembly;
        // Locate by contract so the same tests can run against the renamed protected assembly.
        var edType = assembly.GetTypes().Single(t => t.BaseType == typeof(VoIPMediaSession));
        var wrapperType = assembly.GetTypes().Single(t => !t.IsAbstract && !t.IsSubclassOf(typeof(RTPSession))
            && typeof(IMediaSession).IsAssignableFrom(t));
        foreach (var type in new[] { edType, wrapperType, typeof(SipsorceryRadioEngine) })
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic).Where(f => f.FieldType == typeof(FieldInfo)))
                Require(field.GetValue(null) is FieldInfo, $"Required media compatibility field missing on {type.Name}");
        foreach (var leg in new[] { "RX", "TX" })
        {
            var endpoint = new TestAudio();
            using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
            using var media = (VoIPMediaSession)Activator.CreateInstance(edType,
                new MediaEndPoints { AudioSource = endpoint, AudioSink = endpoint }, 200, 10, IPAddress.Loopback)!;
            media.AcceptRtpFromAny = true;
            media.SetMediaStreamStatus(SDPMediaTypesEnum.audio,
                leg == "RX" ? MediaStreamStatusEnum.RecvOnly : MediaStreamStatusEnum.SendRecv);
            var offer = media.CreateOffer(null!);
            Require(offer.Connection.ConnectionAddress == "127.0.0.1", "Wrong local SDP address");
            Require(offer.Media.Single().MediaFormats.Keys.SequenceEqual(new[] { 8 }), "PCMA offer changed");
            Require(offer.ToString().Contains("R2S-KeepAlivePeriod:200"), "R2S hints missing");
            var remote = SDP.ParseSDPDescription($"v=0\r\no=- 42 1 IN IP4 127.0.0.1\r\ns=radio\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {peerPort} RTP/AVP 8 123\r\na=rtpmap:8 pcma/8000\r\na=rtpmap:123 R2S/8000\r\na=sendrecv\r\na=ptime:20\r\n");
            var logs = new List<string>();
            var wrapper = (IMediaSession)Activator.CreateInstance(wrapperType, media, (Action<string>)logs.Add, leg)!;
            Require(wrapper.SetRemoteDescription(SdpType.answer, remote) == SetDescriptionResultEnum.OK, "SDP negotiation failed");
            Require(!logs.Any(x => x.Contains("Forcing")), "Negotiation only passed via forced-error fallback");
            Require(media.AudioStream.GetSendingFormat().ID == 8, "PCMA not negotiated");
            await media.Start();
            var rtp = media.AudioStream.GetRTPChannel();
            Require(rtp.RTPLocalEndPoint.Address.Equals(IPAddress.Loopback) &&
                rtp.ControlLocalEndPoint.Address.Equals(IPAddress.Loopback) && rtp.ControlPort != rtp.RTPPort,
                "RTP/RTCP sockets not bound to the specified interface");
            var local = new IPEndPoint(IPAddress.Loopback, rtp.RTPPort);
            var received = new ConcurrentQueue<RTPPacket>();
            media.OnRtpPacketReceived += (_, _, packet) => received.Enqueue(packet);
            var valid = Packet(1);
            await peer.SendAsync(valid, local);
            await Until(() => received.Count >= 1);
            // The advisory's one-byte packet plus truncated extension/CSRC declarations.
            foreach (var malformed in new[] { new byte[] { 0x80 }, new byte[] { 0x90, 8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 1 },
                new byte[] { 0x8F, 8, 0, 3, 0, 0, 0, 0, 0, 0, 0, 1 },
                new byte[] { 0x90, 8, 0, 4, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0x67, 0xFF, 0xFF } })
                await peer.SendAsync(malformed, local);
            await peer.SendAsync(Packet(5), local);
            await Until(() => received.Count >= 2);
            Require(received.Last().Payload.Length == 160, "PCMA audio corrupted after malformed packets");
            Require(endpoint.LastAudio?.Length == 160, "Audio sink not fed");
            wrapper.Close("test complete");
        }
    }

    private static byte[] Packet(byte sequence)
    {
        var packet = new byte[180];
        packet[0] = 0x90; packet[1] = 8; packet[3] = sequence;
        packet[6] = sequence; packet[11] = 1;
        packet[12] = 1; packet[13] = 0x67; packet[15] = 1;
        packet[17] = 0x40; // ED137 PTT ON extension, one 32-bit word.
        Array.Fill(packet, (byte)0xD5, 20, 160);
        return packet;
    }

    private static Task TestSdpValidation()
    {
        var assembly = typeof(SipsorceryRadioEngine).Assembly;
        var edType = assembly.GetTypes().Single(t => t.BaseType == typeof(VoIPMediaSession));
        var wrapperType = assembly.GetTypes().Single(t => !t.IsAbstract && !t.IsSubclassOf(typeof(RTPSession))
            && typeof(IMediaSession).IsAssignableFrom(t));
        var valid = "v=0\r\no=- 42 1 IN IP4 127.0.0.1\r\ns=radio\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio 3003 RTP/AVP 8 123\r\na=rtpmap:8 pcma/8000\r\na=rtpmap:123 R2S/8000\r\na=sendrecv\r\n";
        foreach (var leg in new[] { "RX", "TX" })
        foreach (var direction in new[] { "sendrecv", "recvonly", "inactive" })
        {
            var endpoint = new TestAudio();
            using var media = (VoIPMediaSession)Activator.CreateInstance(edType,
                new MediaEndPoints { AudioSource = endpoint, AudioSink = endpoint }, 200, 10, IPAddress.Loopback)!;
            media.CreateOffer(IPAddress.Loopback);
            var wrapper = (IMediaSession)Activator.CreateInstance(wrapperType, media, null, leg)!;
            var result = wrapper.SetRemoteDescription(SdpType.answer, SDP.ParseSDPDescription(valid.Replace("a=sendrecv", "a=" + direction)));
            Require(result == SetDescriptionResultEnum.OK, "Valid direction rejected");
            var expected = leg == "RX" && direction == "sendrecv" ? "sendonly" : direction;
            Require(media.RemoteDescription.Media.Single().MediaStreamStatus.ToString()!.Equals(expected, StringComparison.OrdinalIgnoreCase), "Remote direction overwritten");
        }
        foreach (var invalid in new[] {
            valid.Replace("RTP/AVP 8 123", "RTP/AVP 123").Replace("a=rtpmap:8 pcma/8000\r\n", ""),
            valid.Replace("pcma/8000", "PCMU/8000"),
            valid.Replace("pcma/8000", "PCMA/16000"),
            valid.Replace("3003 RTP", "0 RTP"), valid.Replace("3003 RTP", "65535 RTP"),
            valid.Replace("c=IN IP4 127.0.0.1", "c=IN IP4 invalid"), valid.Replace("RTP/AVP", "RTP/SAVP") })
        {
            var endpoint = new TestAudio();
            using var media = (VoIPMediaSession)Activator.CreateInstance(edType,
                new MediaEndPoints { AudioSource = endpoint, AudioSink = endpoint }, 200, 10, IPAddress.Loopback)!;
            media.CreateOffer(IPAddress.Loopback);
            var wrapper = (IMediaSession)Activator.CreateInstance(wrapperType, media, null, "TX")!;
            Require(wrapper.SetRemoteDescription(SdpType.answer, SDP.ParseSDPDescription(invalid)) != SetDescriptionResultEnum.OK,
                "Invalid SDP falsely accepted as connected");
        }
        return Task.CompletedTask;
    }

    private static Task TestSctp()
    {
        var type = typeof(RTPSession).Assembly.GetType("SIPSorcery.Net.SctpSackChunk", throwOnError: true)!;
        var parse = type.GetMethod("ParseChunk", BindingFlags.Public | BindingFlags.Static)!;
        foreach (var counts in new[] { (65535, 0), (0, 65535), (65535, 65535) })
        {
            var chunk = new byte[16];
            chunk[0] = 3; chunk[3] = 16;
            chunk[12] = (byte)(counts.Item1 >> 8); chunk[13] = (byte)counts.Item1;
            chunk[14] = (byte)(counts.Item2 >> 8); chunk[15] = (byte)counts.Item2;
            try { parse.Invoke(null, new object[] { chunk, 0 }); throw new Exception("Invalid SACK accepted"); }
            catch (TargetInvocationException ex) when (ex.InnerException is ApplicationException) { }
        }
        var good = new byte[16]; good[0] = 3; good[3] = 16;
        Require(parse.Invoke(null, new object[] { good, 0 }) != null, "Valid SACK rejected");
        return Task.CompletedTask;
    }

    private static async Task Check(string name, Func<Task> test)
    {
        await test().WaitAsync(TimeSpan.FromSeconds(12));
        Console.WriteLine($"PASS {name}");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.ElapsedMilliseconds > 3000) throw new Exception("Timed out"); await Task.Delay(5); }
    }
}

internal sealed class TestAudio : IAudioSource, IAudioSink
{
    public static readonly AudioFormat Pcma = new(SDPWellKnownMediaFormatsEnum.PCMA);
    public byte[]? LastAudio;
    public bool Started, Closed;
    public int SinkStarts;
    public Task SourceCloseDelay = Task.CompletedTask;
    public event SourceErrorDelegate OnAudioSinkError { add { } remove { } }
    public event SourceErrorDelegate OnAudioSourceError { add { } remove { } }
    public event EncodedSampleDelegate OnAudioSourceEncodedSample { add { } remove { } }
    public event Action<EncodedAudioFrame> OnAudioSourceEncodedFrameReady { add { } remove { } }
    public event RawAudioSampleDelegate OnAudioSourceRawSample { add { } remove { } }
    public List<AudioFormat> GetAudioSinkFormats() => new() { Pcma };
    public List<AudioFormat> GetAudioSourceFormats() => new() { Pcma };
    public void SetAudioSinkFormat(AudioFormat format) { }
    public void SetAudioSourceFormat(AudioFormat format) { }
    public void RestrictFormats(Func<AudioFormat, bool> filter) { }
    public void GotAudioRtp(IPEndPoint remote, uint ssrc, uint seq, uint timestamp, int pt, bool marker, byte[] data) => LastAudio = data;
    public void GotEncodedMediaFrame(EncodedAudioFrame frame) => LastAudio = frame.EncodedAudio;
    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum rate, uint duration, short[] data) { }
    public bool HasEncodedAudioSubscribers() => false;
    public bool IsAudioSourcePaused() => false;
    public Task PauseAudio() => Task.CompletedTask;
    public Task ResumeAudio() => Task.CompletedTask;
    public Task StartAudio() => Task.CompletedTask;
    public Task CloseAudio() => SourceCloseDelay;
    public Task PauseAudioSink() => Task.CompletedTask;
    public Task ResumeAudioSink() { SinkStarts++; return Task.CompletedTask; }
    public Task StartAudioSink() { Started = true; SinkStarts++; return Task.CompletedTask; }
    public Task CloseAudioSink() { Closed = true; return Task.CompletedTask; }
}
