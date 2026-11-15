using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RadioDeck.Models;
using RadioDeck.Services;
using RadioDeck.Services.Audio;
using static T;

/// <summary>
/// Packets that do not come from the radio. Everything runs on the loopback interface:
/// the radios are 127.0.0.1, the intruder is 127.0.0.2.
/// </summary>
internal static class SecurityRegression
{
    private static readonly IPAddress Intruder = IPAddress.Parse("127.0.0.2");

    public static async Task Run()
    {
        await Check("RTP from another address is not played, moves no lamp and cannot take over the transmit audio", ForeignRtp, 60);
        await Check("RTP from the radio's address but another port is accepted without moving the send destination", OtherPort, 40);
        await Check("SIP from another address cannot end a call; the radio itself still can", ForeignSip, 40);
        await Check("Malformed SIP, RTP and RTCP from the radio's address do not disturb the calls", Malformed, 60);
        await Check("SNMP: an answer from another address is not taken for the radio's", ForeignSnmpAnswer, 30);
    }

    private sealed class Console1 : IDisposable
    {
        public readonly ConcurrentQueue<string> Log = new();
        public readonly TestMicrophone Microphone = new();
        public readonly RadioSimulator Receiver = new();
        public readonly RadioSimulator Transmitter = new();
        public readonly SipsorceryRadioEngine Engine;
        public volatile bool Squelch, Keyed;
        public volatile int RxPeak, TxPeak, RxEnded, TxEnded;
        public int RxPort, TxPort, SipPort;
        public string TxCallId = "";

        public Console1()
        {
            Engine = new SipsorceryRadioEngine(Microphone, () => new AudioConfig());
            Engine.SetRxMuted(true); // keep the test PC silent
            Engine.SetRxVolume(0, 0);
            Engine.Log += Log.Enqueue;
            Engine.RxSquelchChanged += open => { if (open) Squelch = true; };
            Engine.TxConfirmedChanged += keyed => { if (keyed) Keyed = true; };
            Engine.RxLevelChanged += level => { if (level > RxPeak) RxPeak = level; };
            Engine.TxLevelChanged += level => { if (level > TxPeak) TxPeak = level; };
            Engine.RxCallEnded += () => RxEnded++;
            Engine.TxCallEnded += () => TxEnded++;
        }

        public async Task Connect()
        {
            var site = new SiteConfig { Id = "S1", Name = "TOWER", RxUser = "rx1", TxUser = "tx1", Frequency = "118.100", SnmpEnabled = false };
            await Task.WhenAll(
                Engine.ConnectRxAsync($"sip:{Receiver.Address}", site, default),
                Engine.ConnectTxAsync($"sip:{Transmitter.Address}", site, default));
            var rxInvite = Receiver.Invites.First();
            var txInvite = Transmitter.Invites.First();
            RxPort = int.Parse(Regex.Match(rxInvite, @"\r\nm=audio (\d+) ").Groups[1].Value);
            TxPort = int.Parse(Regex.Match(txInvite, @"\r\nm=audio (\d+) ").Groups[1].Value);
            SipPort = int.Parse(Regex.Match(txInvite, @"\r\nVia: SIP/2\.0/UDP [^:\r]+:(\d+)").Groups[1].Value);
            TxCallId = Regex.Match(txInvite, @"\r\nCall-ID: ([^\r]+)").Groups[1].Value;
        }

        /// <summary>Keys the transmitter for half a second; returns the audio packets the radio received.</summary>
        public async Task<int> Transmit()
        {
            Transmitter.Clear();
            await Engine.StartTxAudioAsync(default);
            for (var i = 0; i < 25; i++)
            {
                Microphone.Push(Enumerable.Repeat((byte)0x33, 160).ToArray());
                await Task.Delay(20);
            }
            await Engine.StopTxAudioAsync(default);
            await Task.Delay(500);
            return Transmitter.Snapshot().Count(p => p.PayloadType == 8);
        }

        public void Dispose()
        {
            Engine.Dispose();
            Receiver.Dispose();
            Transmitter.Dispose();
        }
    }

    /// <summary>A socket that records what it is sent.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        public readonly UdpClient Socket;
        public readonly ConcurrentQueue<byte[]> Received = new();

        public Listener(IPAddress address)
        {
            Socket = new UdpClient(new IPEndPoint(address, 0));
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try { Received.Enqueue((await Socket.ReceiveAsync(_stop.Token)).Buffer); }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    catch (SocketException) { }
                }
            });
        }

        public int Port => ((IPEndPoint)Socket.Client.LocalEndPoint!).Port;
        public Task Send(byte[] data, int port) => Socket.SendAsync(data, new IPEndPoint(IPAddress.Loopback, port)).AsTask();

        public void Dispose()
        {
            _stop.Cancel();
            Socket.Dispose();
        }
    }

    private static readonly byte[] Loud = Enumerable.Repeat(G711.Encode((short)20000), 160).ToArray();

    private static byte[] Rtp(int payloadType, ushort sequence, uint word, byte[] payload, byte ssrc)
    {
        var packet = new byte[20 + payload.Length];
        packet[0] = 0x90;
        packet[1] = (byte)payloadType;
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        var timestamp = (uint)sequence * 160;
        packet[4] = (byte)(timestamp >> 24); packet[5] = (byte)(timestamp >> 16); packet[6] = (byte)(timestamp >> 8); packet[7] = (byte)timestamp;
        packet[11] = ssrc;
        packet[12] = 0x01; packet[13] = 0x67; packet[15] = 1;
        packet[16] = (byte)(word >> 24); packet[17] = (byte)(word >> 16); packet[18] = (byte)(word >> 8); packet[19] = (byte)word;
        payload.CopyTo(packet, 20);
        return packet;
    }

    private static async Task ForeignRtp()
    {
        // Once with the radios already talking, once with the intruder's packet as the very
        // first one of the call (the moment at which the media library would follow it).
        foreach (var intruderFirst in new[] { false, true })
        {
            using var console = new Console1();
            using var intruder = new Listener(Intruder);
            console.Receiver.RtpQuiet = console.Transmitter.RtpQuiet = intruderFirst;
            await console.Connect();
            if (!intruderFirst) await Task.Delay(600);

            var playedBefore = console.Engine.PlaybackFrames;
            for (ushort i = 1; i <= 25; i++)
            {
                await intruder.Send(Rtp(8, i, 0x10000000, Loud, 0x55), console.RxPort);   // "squelch open" + loud audio
                await intruder.Send(Rtp(8, i, 0x20000000, Loud, 0x66), console.TxPort);   // "transmitter keyed"
                await intruder.Send(Rtp(123, i, 0x30000000, Array.Empty<byte>(), 0x66), console.TxPort);
                await Task.Delay(15);
            }
            await Task.Delay(300);

            var what = intruderFirst ? "intruder first" : "radio first";
            Require(console.Engine.PlaybackFrames == playedBefore, $"{what}: {console.Engine.PlaybackFrames - playedBefore} foreign audio frames reached the headset");
            Require(!console.Squelch && !console.Keyed && console.RxPeak == 0 && console.TxPeak == 0,
                $"{what}: foreign packets moved the indicators (SQ {console.Squelch}, ON AIR {console.Keyed}, levels {console.RxPeak}/{console.TxPeak})");

            console.Receiver.RtpQuiet = console.Transmitter.RtpQuiet = false;
            await Task.Delay(400);
            while (intruder.Received.TryDequeue(out _)) { }
            var audio = await console.Transmit();
            Require(intruder.Received.IsEmpty, $"{what}: {intruder.Received.Count} packets of the transmission went to the intruder");
            Require(audio >= 15, $"{what}: only {audio} audio packets reached the transmitter");
            Require(console.Engine.IsRxConnected && console.Engine.IsTxConnected && console.RxEnded == 0 && console.TxEnded == 0,
                $"{what}: a call was lost");

            var notes = console.Log.Count(line => line.Contains("Ignored a packet from 127.0.0.2"));
            Require(notes is >= 1 and <= 6, $"{what}: dropped packets must be logged, but briefly ({notes} lines for 75 packets)");
        }
    }

    private static async Task OtherPort()
    {
        using var console = new Console1();
        using var otherPort = new Listener(IPAddress.Loopback); // the radio's own address, not its signalled port
        await console.Connect();
        await Task.Delay(500);

        var playedBefore = console.Engine.PlaybackFrames;
        for (ushort i = 1; i <= 20; i++)
        {
            await otherPort.Send(Rtp(8, i, 0x10000000, Loud, 0x77), console.RxPort);
            await Task.Delay(20);
        }
        await Until(() => console.Squelch && console.RxPeak > 0 && console.Engine.PlaybackFrames > playedBefore,
            what: "audio from the radio's address to be accepted");

        // The answer of the radio said where to send; a packet from another port must not change that.
        var audio = await console.Transmit();
        Require(audio >= 15 && otherPort.Received.IsEmpty,
            $"The send destination moved: transmitter got {audio}, the other port got {otherPort.Received.Count}");
        console.Receiver.Clear();
        await Until(() => console.Receiver.Rtp.Count >= 3, what: "keepalives still reaching the receiver");
    }

    private static string SipRequest(string method, string callId, int sipPort, int fromPort, string toTag = "") =>
        $"{method} sip:console@127.0.0.1:{sipPort} SIP/2.0\r\n" +
        $"Via: SIP/2.0/UDP {Intruder}:{fromPort};branch=z9hG4bK{Guid.NewGuid():N}\r\n" +
        $"From: <sip:radio@{Intruder}>;tag=abc\r\nTo: <sip:console@127.0.0.1>{toTag}\r\nCall-ID: {callId}\r\n" +
        $"CSeq: 7 {method}\r\nMax-Forwards: 70\r\nContact: <sip:radio@{Intruder}:{fromPort}>\r\nContent-Length: 0\r\n\r\n";

    private static async Task ForeignSip()
    {
        using var console = new Console1();
        using var intruder = new Listener(Intruder);
        await console.Connect();
        await Task.Delay(400);

        // The intruder has seen the call on the wire and quotes its Call-ID.
        foreach (var request in new[]
                 {
                     SipRequest("BYE", console.TxCallId, console.SipPort, intruder.Port, ";tag=zzz"),
                     SipRequest("BYE", "another-call", console.SipPort, intruder.Port, ";tag=zzz"),
                     SipRequest("INVITE", "new-call", console.SipPort, intruder.Port),
                     SipRequest("OPTIONS", "probe", console.SipPort, intruder.Port)
                 })
            await intruder.Send(Encoding.UTF8.GetBytes(request), console.SipPort);
        await Task.Delay(800);

        Require(console.Engine.IsTxConnected && console.Engine.IsRxConnected && console.TxEnded == 0 && console.RxEnded == 0,
            "A SIP request from another address ended a call");
        Require(intruder.Received.IsEmpty,
            "The console answered an unknown address: " +
            (intruder.Received.TryPeek(out var reply) ? Encoding.UTF8.GetString(reply).Split("\r\n")[0] : ""));
        Require(await console.Transmit() >= 15, "Transmitting no longer works after the foreign requests");
        Require(console.Log.Count(line => line.Contains("[SIP] Ignored a message from 127.0.0.2")) == 1,
            "Dropped SIP messages must be logged once, not one by one");

        // The radio itself still ends its call.
        await console.Transmitter.Hangup();
        await Until(() => console.TxEnded == 1 && !console.Engine.IsTxConnected, what: "the radio's own BYE");
        Require(console.Engine.IsRxConnected, "The receiver call must not be affected");
    }

    private static async Task Malformed()
    {
        using var console = new Console1();
        using var radioSide = new Listener(IPAddress.Loopback); // passes the address filter: exercises the parsers
        await console.Connect();
        await Task.Delay(400);

        var random = new Random(20261114);
        for (var i = 0; i < 500; i++)
        {
            var junk = new byte[random.Next(0, 700)];
            random.NextBytes(junk);
            await radioSide.Send(junk, console.SipPort);

            var rtp = new byte[random.Next(0, 240)];
            random.NextBytes(rtp);
            if (rtp.Length > 1)
            {
                // Looks like RTP version 2; often with the extension flag, a full CSRC list or a huge extension length.
                rtp[0] = (byte)(0x80 | (random.Next(4) == 0 ? 0x1F : 0x10));
                rtp[1] = (byte)(random.Next(2) == 0 ? 8 : 123);
            }
            var port = i % 2 == 0 ? console.RxPort : console.TxPort;
            await radioSide.Send(rtp, port);
            await radioSide.Send(rtp, port + 1); // RTCP socket
            if (i % 50 == 0) await Task.Delay(10);
        }
        foreach (var text in new[]
                 {
                     "INVITE sip:a SIP/2.0\r\nVia: \r\nContent-Length: 99999999\r\n\r\n",
                     "SIP/2.0 200 OK\r\nCSeq: x INVITE\r\n\r\n",
                     "BYE sip:console SIP/2.0\r\n\r\n",
                     new string('A', 60000)
                 })
            await radioSide.Send(Encoding.UTF8.GetBytes(text), console.SipPort);
        await Task.Delay(1200);

        Require(console.Engine.IsRxConnected && console.Engine.IsTxConnected && console.RxEnded == 0 && console.TxEnded == 0,
            "Malformed packets ended a call");
        Require(await console.Transmit() >= 15, "Transmitting no longer works after the malformed packets");
        console.Receiver.Clear();
        await Until(() => console.Receiver.Rtp.Count >= 3, what: "keepalives after the malformed packets");
    }

    private static async Task ForeignSnmpAnswer()
    {
        const string oid = "1.3.6.1.4.1.99999.1.2.0";
        using var radio = new SnmpAgent { Silent = true };
        radio.Values[oid] = (0x02, Ber.IntegerBytes(118100));
        using var intruder = new UdpClient(new IPEndPoint(Intruder, 0));

        // The radio does not answer; the intruder sends a well-formed answer (right request id,
        // right object) to the very port the question came from.
        var forged = 0;
        radio.Unanswered = (asker, answer) =>
        {
            Interlocked.Increment(ref forged);
            intruder.Send(answer, answer.Length, asker);
        };

        var site = new SiteConfig
        {
            Id = "S1", Name = "TOWER", Profile = RadioProfile.Ed137BId, LinkMode = nameof(RadioLinkMode.Transceiver),
            TxHost = radio.Host, SnmpEnabled = true, SnmpPort = radio.Port, SnmpFrequencyOid = oid
        };
        var client = new SnmpFrequencyClient();
        var read = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(forged == 1, "The forged answer was not sent; the check proves nothing");
        Require(!read.Success && read.FrequencyKhz == 0, $"A forged answer was shown as the frequency: {read.FrequencyKhz}");

        // The radio's own answer is still accepted.
        radio.Silent = false;
        read = await client.GetFrequencyAsync(SnmpTarget.For(site), CancellationToken.None);
        Require(read.Success && read.FrequencyKhz == 118100 && forged == 1, $"The radio's own answer was rejected: {read.Message}");

        radio.Silent = true;
        var set = await client.SetFrequencyAsync(SnmpTarget.For(site), 121500, CancellationToken.None);
        Require(forged == 2 && !set[0].Success, "A forged answer made a write look confirmed");
    }
}
