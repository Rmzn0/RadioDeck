using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace RadioDeck.Services;

/// <summary>What the SDP offer advertises beyond plain PCMA; depends on the radio profile.</summary>
internal sealed class Ed137SdpOptions
{
    /// <summary>List payload type 123 "R2S/8000" in the media line, as ED-137B/C offers do.</summary>
    public bool AdvertiseR2s { get; init; }

    /// <summary>Additional media attributes without the "a=" prefix, e.g. "type:Radio-TxRx".</summary>
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();
}

internal sealed class Ed137MediaSession : VoIPMediaSession
{
    public int KeepAlivePeriodMs { get; }
    public int KeepAliveMultiplier { get; }
    public Ed137SdpOptions SdpOptions { get; set; } = new();
    public event Action<IPEndPoint>? R2sReceived;

    /// <summary>An RTP packet that came from the radio's media address.</summary>
    public event Action<IPEndPoint, byte[]>? RadioRtpReceived;

    /// <summary>A packet from another address was dropped; the second value counts them on this call.</summary>
    public event Action<IPEndPoint, long>? ForeignPacketDropped;

    private readonly IPAddress? _forcedConnectionAddress;
    private long _foreignPackets;

    public Ed137MediaSession(
        MediaEndPoints mediaEndPoints,
        int keepAlivePeriodMs,
        int keepAliveMultiplier,
        IPAddress? forcedConnectionAddress = null)
        : base(mediaEndPoints, bindAddress: forcedConnectionAddress ?? ResolveLocalIPv4() ?? IPAddress.Any)
    {
        KeepAlivePeriodMs = keepAlivePeriodMs;
        KeepAliveMultiplier = keepAliveMultiplier;
        _forcedConnectionAddress = forcedConnectionAddress;

        // The media library takes RTP from any address and may even move the send
        // destination to it. Put a filter in front of it: only packets from the media
        // address the radio signalled get through.
        var channel = AudioStream.GetRTPChannel();
        channel.OnRTPDataReceived -= OnReceive;
        channel.OnControlDataReceived -= OnReceive;
        channel.OnRTPDataReceived += OnRtpDatagram;
        channel.OnControlDataReceived += OnControlDatagram;
    }

    private void OnRtpDatagram(int localPort, IPEndPoint remote, byte[] data)
    {
        var radio = AudioStream.DestinationEndPoint;
        if (radio == null || !SameAddress(radio.Address, remote.Address))
        {
            ForeignPacketDropped?.Invoke(remote, Interlocked.Increment(ref _foreignPackets));
            return;
        }

        // Some radios send from another port than the one they listen on. Handing the
        // packet on with the signalled end point keeps the send destination where the
        // radio said it is.
        OnReceive(localPort, radio, data);
        RecordR2sActivity(radio, data);
        RadioRtpReceived?.Invoke(remote, data);
    }

    private void OnControlDatagram(int localPort, IPEndPoint remote, byte[] data)
    {
        var radio = AudioStream.DestinationEndPoint;
        if (radio == null || !SameAddress(radio.Address, remote.Address))
        {
            ForeignPacketDropped?.Invoke(remote, Interlocked.Increment(ref _foreignPackets));
            return;
        }

        OnReceive(localPort, AudioStream.ControlDestinationEndPoint ?? remote, data);
    }

    private static bool SameAddress(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return a.Equals(b);
    }

    private void RecordR2sActivity(IPEndPoint remote, byte[] data)
    {
        // PT123 is signalling, not an audio codec. SIPSorcery 10.0.17 discards it
        // before updating RTCP activity, which otherwise hangs up idle radio calls.
        if (IsClosed || data.Length < 20 ||
            (data[0] >> 6) != 2 || (data[0] & 0x10) == 0 || (data[1] & 0x7F) != 123)
            return;

        int extensionOffset = 12 + (data[0] & 0x0F) * 4;
        if (data.Length < extensionOffset + 8 ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(extensionOffset)) != 0x0167 ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(extensionOffset + 2)) != 1 ||
            !RTPPacket.TryParse(data, out var packet, out _) || packet == null || packet.Payload.Length != 0)
            return;

        AudioRtcpSession?.RecordRtpPacketReceived(packet);
        R2sReceived?.Invoke(remote);
    }

    public override void Close(string reason)
    {
        // VoIPMediaSession closes the source before the sink. Stop playback first
        // so a slow microphone shutdown cannot leave old received audio playing.
        if (Media.AudioSink is RxPlaybackGate gate)
            _ = gate.CloseAudioSink();
        base.Close(reason);
    }

    public override SDP CreateOffer(IPAddress connectionAddress)
    {
        // SIPSorcery may pass a null connectionAddress depending on SIPCallDescriptor/transport setup.
        // Ensure we never pass null down into the base implementation (it will throw).
        var offerAddress = connectionAddress ?? _forcedConnectionAddress ?? ResolveLocalIPv4() ?? IPAddress.Loopback;
        var sdp = base.CreateOffer(offerAddress);
        NormalizeAddresses(sdp, offerAddress, _forcedConnectionAddress, SdpOptions.AdvertiseR2s);
        AddR2sAttributes(sdp);
        return sdp;
    }

    public override SDP CreateAnswer(IPAddress connectionAddress)
    {
        var answerAddress = connectionAddress ?? _forcedConnectionAddress ?? ResolveLocalIPv4() ?? IPAddress.Loopback;
        var sdp = base.CreateAnswer(answerAddress);
        NormalizeAddresses(sdp, answerAddress, _forcedConnectionAddress, SdpOptions.AdvertiseR2s);
        AddR2sAttributes(sdp);
        return sdp;
    }

    private static void NormalizeAddresses(SDP sdp, IPAddress? connectionAddress, IPAddress? forcedConnectionAddress, bool advertiseR2s)
    {
        var ipAddress = ResolveAddress(connectionAddress, sdp, forcedConnectionAddress);
        var ip = ipAddress.ToString();
        sdp.AddressOrHost = ip;

        sdp.Connection ??= new SDPConnectionInformation(ipAddress);
        sdp.Connection.ConnectionAddress = ip;

        if (sdp.Media == null) return;

        foreach (var media in sdp.Media)
        {
            if (media.Media == SDPMediaTypesEnum.audio)
            {
                // Keep the SDP minimal; simple SDP parsers in radios accept it more readily.
                media.MediaID = null;
                media.SsrcAttributes?.Clear();
                media.MediaFormats ??= new Dictionary<int, SDPAudioVideoMediaFormat>();
                media.MediaFormats.Clear();
                media.MediaFormats[8] = new SDPAudioVideoMediaFormat(
                    SDPMediaTypesEnum.audio, 8, "PCMA", 8000, 1, null);
                if (advertiseR2s)
                    media.MediaFormats[123] = new SDPAudioVideoMediaFormat(
                        SDPMediaTypesEnum.audio, 123, "R2S", 8000, 1, null);
            }

            media.Connection ??= new SDPConnectionInformation(ipAddress);
            media.Connection.ConnectionAddress = ip;
        }
    }

    private void AddR2sAttributes(SDP sdp)
    {
        // SIPSorcery writes the "extra attribute" lists verbatim, one SDP line per entry,
        // so every entry must be a complete line including the "a=" prefix. (Version 1
        // omitted the prefix and sent lines a strict SDP parser rejects.)
        var periodAttr = $"a=R2S-KeepAlivePeriod:{KeepAlivePeriodMs}";
        var multiplierAttr = $"a=R2S-KeepAliveMultiplier:{KeepAliveMultiplier}";
        const string ptimeAttr = "a=ptime:20";

        if (sdp.Media == null || sdp.Media.Count == 0)
        {
            sdp.ExtraSessionAttributes ??= new List<string>();
            AddUnique(sdp.ExtraSessionAttributes, periodAttr);
            AddUnique(sdp.ExtraSessionAttributes, multiplierAttr);
            AddUnique(sdp.ExtraSessionAttributes, ptimeAttr);
            return;
        }

        foreach (var media in sdp.Media)
        {
            if (media.Media != SDPMediaTypesEnum.audio) continue;
            media.ExtraMediaAttributes ??= new List<string>();
            media.ExtraMediaAttributes.RemoveAll(x =>
                x != null && (
                    WithoutPrefix(x).StartsWith("mid:", StringComparison.OrdinalIgnoreCase) ||
                    WithoutPrefix(x).StartsWith("ssrc:", StringComparison.OrdinalIgnoreCase)));
            foreach (var attribute in SdpOptions.Attributes)
                if (!string.IsNullOrWhiteSpace(attribute))
                    AddUnique(media.ExtraMediaAttributes, "a=" + WithoutPrefix(attribute.Trim()));
            AddUnique(media.ExtraMediaAttributes, periodAttr);
            AddUnique(media.ExtraMediaAttributes, multiplierAttr);
            AddUnique(media.ExtraMediaAttributes, ptimeAttr);
        }
    }

    private static string WithoutPrefix(string attribute) =>
        attribute.StartsWith("a=", StringComparison.OrdinalIgnoreCase) ? attribute[2..] : attribute;

    private static void AddUnique(List<string> list, string value)
    {
        if (!list.Contains(value))
            list.Add(value);
    }

    private static IPAddress ResolveAddress(IPAddress? connectionAddress, SDP sdp, IPAddress? forcedConnectionAddress)
    {
        if (forcedConnectionAddress != null)
            return forcedConnectionAddress;

        if (connectionAddress != null)
            return connectionAddress;

        if (IPAddress.TryParse(sdp.Connection?.ConnectionAddress, out var fromConn))
            return fromConn;

        if (IPAddress.TryParse(sdp.AddressOrHost, out var fromOwner))
            return fromOwner;

        if (sdp.Media != null)
        {
            foreach (var media in sdp.Media)
            {
                if (IPAddress.TryParse(media.Connection?.ConnectionAddress, out var fromMedia))
                    return fromMedia;
            }
        }

        try
        {
            var local = Dns.GetHostAddresses(Dns.GetHostName());
            var ipv4 = local.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
                return ipv4;
        }
        catch
        {
            // ignore
        }

        return IPAddress.Loopback;
    }

    private static IPAddress? ResolveLocalIPv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }

}
