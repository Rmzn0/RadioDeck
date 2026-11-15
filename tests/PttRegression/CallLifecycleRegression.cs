using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RadioDeck.Services;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

internal static class CallLifecycleRegression
{
    public static async Task Run()
    {
        await R2sReception();
        Console.WriteLine("PASS Received R2S counts as activity without reaching audio playback");
        await PlaybackStopsBeforeSource();
        Console.WriteLine("PASS Playback stops before delayed source shutdown; late frames cannot reopen it");
        await DualRadioCalls().WaitAsync(TimeSpan.FromSeconds(65));
        Console.WriteLine("PASS Four SIP calls idle 40s, simultaneous LOC/GP audio, isolated hangup and reconnect");
    }

    private static VoIPMediaSession Media(TestAudio audio)
    {
        var type = typeof(SipsorceryRadioEngine).Assembly.GetTypes().Single(t => t.BaseType == typeof(VoIPMediaSession));
        return (VoIPMediaSession)Activator.CreateInstance(type,
            new MediaEndPoints { AudioSource = audio, AudioSink = new RxPlaybackGate(audio, () => false) }, 200, 10, IPAddress.Loopback)!;
    }

    private static IMediaSession Wrapper(VoIPMediaSession media, string leg)
    {
        var type = typeof(SipsorceryRadioEngine).Assembly.GetTypes().Single(t => !t.IsAbstract &&
            !t.IsSubclassOf(typeof(RTPSession)) && typeof(IMediaSession).IsAssignableFrom(t));
        return (IMediaSession)Activator.CreateInstance(type, media, null, leg)!;
    }

    private static async Task R2sReception()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var remote = (IPEndPoint)peer.Client.LocalEndPoint!;
        var audio = new TestAudio();
        using var media = Media(audio);
        media.CreateOffer(IPAddress.Loopback);
        var wrapper = Wrapper(media, "RX");
        Require(wrapper.SetRemoteDescription(SdpType.answer, Answer(remote.Port)) == SetDescriptionResultEnum.OK, "Negotiation failed");
        await media.Start();
        var local = media.AudioStream.GetRTPChannel().RTPLocalEndPoint;
        await peer.SendAsync(KeepAlive(1), local);
        await Task.Delay(150);
        Require(media.AudioRtcpSession.LastActivityAt != DateTime.MinValue,
            "ED137 keepalive arrived but was discarded by the RTP inactivity watchdog");
        Require(media.AudioRtcpSession.PacketsReceivedCount == 1, "R2S counted more than once");
        Require(audio.LastAudio == null, "Keepalive was decoded as audio");
        var count = media.AudioRtcpSession.PacketsReceivedCount;
        // A well-formed keepalive from another address is not the radio's. (Another port of
        // the radio's own address is accepted: some radios send from a second port.)
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        await stranger.SendAsync(KeepAlive(2), local);
        var wrongProfile = KeepAlive(3); wrongProfile[13] = 0x68;
        var truncated = KeepAlive(4)[..18];
        var withAudio = KeepAlive(5).Concat(new byte[] { 0xD5 }).ToArray();
        foreach (var invalid in new[] { wrongProfile, truncated, withAudio })
            await peer.SendAsync(invalid, local);
        await Task.Delay(150);
        Require(media.AudioRtcpSession.PacketsReceivedCount == count, "Invalid/unrelated packet kept session alive");
        wrapper.Close("test complete");
    }

    private static async Task PlaybackStopsBeforeSource()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = new TestAudio { SourceCloseDelay = closed.Task };
        using var media = Media(audio);
        await media.Start();
        media.Close("remote hangup");
        Require(audio.Closed, "Sink was left playing while source close was blocked");
        var sink = media.Media.AudioSink;
        sink.GotEncodedMediaFrame(new EncodedAudioFrame(0, TestAudio.Pcma, 20, new byte[160]));
#pragma warning disable CS0618 // Exercise the legacy callback after close as well.
        sink.GotAudioRtp(new IPEndPoint(IPAddress.Loopback, 12345), 1, 2, 160, 8, false, new byte[160]);
#pragma warning restore CS0618
        await sink.StartAudioSink();
        await sink.ResumeAudioSink();
        Require(audio.LastAudio == null && audio.SinkStarts == 1, "Late callback revived closed audio");
        closed.SetResult();
        await Task.Delay(30);
    }

    private static async Task DualRadioCalls()
    {
        using var locTransport = new SIPTransport();
        using var gpTransport = new SIPTransport();
        locTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0)));
        gpTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Loopback, 0)));
        var peers = Enumerable.Range(0, 4).Select(_ => new RadioPeer()).ToArray();
        var endpoints = Enumerable.Range(0, 4).Select(_ => new TestAudio()).ToArray();
        var media = endpoints.Select(Media).ToArray();
        var uas = Enumerable.Range(0, 4).Select(i => new SIPUserAgent(i < 2 ? locTransport : gpTransport, null)).ToArray();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                var leg = i % 2 == 0 ? "RX" : "TX";
                media[i].SetMediaStreamStatus(SDPMediaTypesEnum.audio,
                    leg == "RX" ? MediaStreamStatusEnum.RecvOnly : MediaStreamStatusEnum.SendRecv);
                Require(await uas[i].Call(peers[i].Uri, null, null, Wrapper(media[i], leg)).WaitAsync(TimeSpan.FromSeconds(4)),
                    $"Loopback call {i} failed");
            }
            Console.WriteLine("CHECK Four loopback calls: waiting beyond the real 30-second RTP timeout...");
            await Task.Delay(TimeSpan.FromSeconds(40));
            Require(uas.All(x => x.IsCallActive), "Idle R2S call was hung up by RTP timeout");
            Require(endpoints.All(x => x.LastAudio == null), "R2S reached an audio sink");
            await Task.WhenAll(peers[0].SendAudio(0xAA), peers[2].SendAudio(0xBB));
            await Until(() => endpoints[0].LastAudio?.FirstOrDefault() == 0xAA && endpoints[2].LastAudio?.FirstOrDefault() == 0xBB);
            await peers[0].Hangup();
            await Until(() => !uas[0].IsCallActive && endpoints[0].Closed);
            Require(uas.Skip(1).All(x => x.IsCallActive), "One RX hangup closed another leg/site");
            var oldPayload = endpoints[0].LastAudio;
            await Task.WhenAll(peers[0].SendAudio(0xCC), peers[2].SendAudio(0xDD));
            await Until(() => endpoints[2].LastAudio?.FirstOrDefault() == 0xDD);
            Require(ReferenceEquals(endpoints[0].LastAudio, oldPayload), "Disconnected LOC still received audio");

            // Replace LOC RX while LOC TX and both GP legs remain connected.
            using var nextPeer = new RadioPeer();
            var nextAudio = new TestAudio();
            using var nextMedia = Media(nextAudio);
            using var nextUa = new SIPUserAgent(locTransport, null);
            Require(await nextUa.Call(nextPeer.Uri, null, null, Wrapper(nextMedia, "RX")), "Reconnect failed");
            await nextPeer.SendAudio(0xAB);
            await Until(() => nextAudio.LastAudio?.FirstOrDefault() == 0xAB);
            uas[1].Hangup();
            Require(nextUa.IsCallActive && uas[2].IsCallActive && uas[3].IsCallActive, "Local TX hangup affected other calls");

            // A real loss of all media must still terminate a call, not be hidden by the fix.
            nextPeer.StopR2s();
            nextMedia.AudioRtcpSession.NoActivityTimeoutMilliseconds = 100;
            await Until(() => !nextUa.IsCallActive, 9000);
            Require(nextAudio.Closed, "Timed-out media left sink open");
            Console.WriteLine("PASS Lost peer still times out (watchdog remains enabled)");
        }
        finally
        {
            foreach (var ua in uas) ua.Dispose();
            foreach (var session in media) session.Dispose();
            foreach (var peer in peers) peer.Dispose();
        }
    }

    private sealed class RadioPeer : IDisposable
    {
        private readonly UdpClient _sip = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _rtp;
        private readonly UdpClient _rtcp;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _listen;
        private readonly Task _keepAlives;
        private IPEndPoint? _clientSip, _clientRtp;
        private SIPRequest? _invite;
        private SIPResponse? _answer;
        private int _sequence;
        private volatile bool _sendR2s = true;
        public string Uri => $"sip:test@127.0.0.1:{((IPEndPoint)_sip.Client.LocalEndPoint!).Port}";

        public RadioPeer()
        {
            // Reserve both advertised media ports; otherwise Windows can allocate
            // RTP+1 to the next fake peer's SIP socket and misroute RTCP into SIP.
            while (true)
            {
                var rtp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)rtp.Client.LocalEndPoint!).Port;
                if (port == 65535) { rtp.Dispose(); continue; }
                try
                {
                    _rtcp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));
                    _rtp = rtp;
                    break;
                }
                catch (SocketException) { rtp.Dispose(); }
            }
            _listen = Task.Run(Listen);
            _keepAlives = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        if (_sendR2s && _clientRtp is { } remote)
                            await _rtp.SendAsync(KeepAlive((ushort)Interlocked.Increment(ref _sequence)), remote);
                        await Task.Delay(100, _stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private async Task Listen()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var received = await _sip.ReceiveAsync(_stop.Token);
                    if (received.Buffer.All(b => b is 0 or 10 or 13)) continue; // SIP NAT probes.
                    var text = System.Text.Encoding.UTF8.GetString(received.Buffer);
                    if (string.IsNullOrWhiteSpace(text) || text.StartsWith("SIP/2.0")) continue;
                    SIPRequest request;
                    try { request = SIPRequest.ParseSIPRequest(text); }
                    catch (SIPValidationException ex)
                    {
                        throw new Exception($"Test peer received unparseable SIP: {Convert.ToHexString(received.Buffer.AsSpan(0, Math.Min(48, received.Buffer.Length)))}", ex);
                    }
                    if (request.Method == SIPMethodsEnum.ACK) continue;
                    var response = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                    if (request.Method == SIPMethodsEnum.INVITE)
                    {
                        var offer = SDP.ParseSDPDescription(request.Body);
                        _clientRtp = new IPEndPoint(IPAddress.Loopback, offer.Media[0].Port);
                        _clientSip = received.RemoteEndPoint;
                        _invite = request;
                        response.Header.To.ToTag = "test-radio";
                        response.Header.Contact = new List<SIPContactHeader> { new(null, SIPURI.ParseSIPURI(Uri)) };
                        response.Header.ContentType = SDP.SDP_MIME_CONTENTTYPE;
                        response.Body = Answer(((IPEndPoint)_rtp.Client.LocalEndPoint!).Port).ToString();
                        response.Header.ContentLength = System.Text.Encoding.UTF8.GetByteCount(response.Body);
                        _answer = response;
                    }
                    await _sip.SendAsync(System.Text.Encoding.UTF8.GetBytes(response.ToString()), received.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task SendAudio(byte value)
        {
            var packet = KeepAlive((ushort)Interlocked.Increment(ref _sequence)).Concat(Enumerable.Repeat(value, 160)).ToArray();
            packet[1] = 8;
            await _rtp.SendAsync(packet, _clientRtp!);
        }

        public async Task Hangup()
        {
            var request = $"BYE {_invite!.Header.Contact[0].ContactURI} SIP/2.0\r\n" +
                $"Via: SIP/2.0/UDP 127.0.0.1:{((IPEndPoint)_sip.Client.LocalEndPoint!).Port};branch=z9hG4bK{Guid.NewGuid():N}\r\n" +
                $"From: {_answer!.Header.To}\r\nTo: {_invite.Header.From}\r\nCall-ID: {_invite.Header.CallId}\r\n" +
                "CSeq: 2 BYE\r\nMax-Forwards: 70\r\nContent-Length: 0\r\n\r\n";
            await _sip.SendAsync(System.Text.Encoding.UTF8.GetBytes(request), _clientSip!);
        }

        public void StopR2s() => _sendR2s = false;
        public void Dispose()
        {
            _stop.Cancel();
            Task.WhenAll(_listen, _keepAlives).GetAwaiter().GetResult();
            _sip.Dispose(); _rtp.Dispose(); _rtcp.Dispose(); _stop.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition, int timeout = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeout) throw new Exception("Lifecycle condition timed out");
            await Task.Delay(10);
        }
    }

    private static SDP Answer(int port) => SDP.ParseSDPDescription(
        $"v=0\r\no=- 42 1 IN IP4 127.0.0.1\r\ns=radio\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=audio {port} RTP/AVP 8 123\r\na=rtpmap:8 PCMA/8000\r\na=rtpmap:123 R2S/8000\r\na=sendrecv\r\n");

    private static byte[] KeepAlive(ushort sequence)
    {
        var packet = new byte[20];
        packet[0] = 0x90; packet[1] = 123;
        packet[2] = (byte)(sequence >> 8); packet[3] = (byte)sequence;
        packet[6] = (byte)(sequence >> 8); packet[7] = (byte)sequence;
        packet[11] = 1; packet[12] = 1; packet[13] = 0x67; packet[15] = 1;
        return packet;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
