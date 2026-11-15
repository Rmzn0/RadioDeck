using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;

namespace RadioDeck.Services;

/// <summary>
/// Normalizes inbound SDP answers of ED-137/R2S radios so SIPSorcery can accept them.
/// In particular, some radios list the R2S payload type (123), which is not an audio codec
/// and can cause SetRemoteDescription to fail on some SIPSorcery versions.
/// </summary>
internal sealed class SdpSanitizingMediaSession : IMediaSession
{
    private readonly IMediaSession _inner;
    private readonly Action<string>? _log;
    private readonly string _leg;

    private static readonly System.Reflection.FieldInfo? AudioNegotiatedFormatField =
        typeof(AudioStream).GetField("<NegotiatedFormat>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public SdpSanitizingMediaSession(IMediaSession inner, Action<string>? log, string leg)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log;
        _leg = string.IsNullOrWhiteSpace(leg) ? "-" : leg.Trim();
    }

    public bool HasText => _inner.HasText;
    public bool HasAudio => _inner.HasAudio;
    public bool HasVideo => _inner.HasVideo;
    public bool IsClosed => _inner.IsClosed;
    public SDP? RemoteDescription => _inner.RemoteDescription;

    /// <summary>PTT-ID an ED-137B/C radio assigned in its answer (a=ptt-id), if any.</summary>
    public int? RemotePttId { get; private set; }
    public IPAddress RtpBindAddress => _inner.RtpBindAddress;

    public event Action<string> OnRtpClosed
    {
        add => _inner.OnRtpClosed += value;
        remove => _inner.OnRtpClosed -= value;
    }

    public event Action<IPEndPoint, RTPEvent, RTPHeader> OnRtpEvent
    {
        add => _inner.OnRtpEvent += value;
        remove => _inner.OnRtpEvent -= value;
    }

    public event Action<SDPMediaTypesEnum> OnTimeout
    {
        add => _inner.OnTimeout += value;
        remove => _inner.OnTimeout -= value;
    }

    public SDP CreateOffer(IPAddress connectionAddress) => _inner.CreateOffer(connectionAddress);

    public SDP CreateAnswer(IPAddress connectionAddress) => _inner.CreateAnswer(connectionAddress);

    public SetDescriptionResultEnum SetRemoteDescription(SdpType sdpType, SDP sessionDescription)
    {
        var preSummary = Summarize(sessionDescription);
        _log?.Invoke($"[SDP][{_leg}] Remote SDP (pre-sanitize): {preSummary}");

        RemotePttId = ReadPttId(sessionDescription);

        var sdpToApply = sessionDescription;
        try
        {
            sdpToApply = BuildSanitizedSdp(sessionDescription, _leg.Equals("RX", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[SDP][{_leg}][WARN] sanitize failed: {ex.GetType().Name}: {ex.Message}");
            return SetDescriptionResultEnum.Error;
        }

        if (!ReferenceEquals(sdpToApply, sessionDescription))
            _log?.Invoke($"[SDP][{_leg}] Remote SDP (post-sanitize): {Summarize(sdpToApply)}");

        SetDescriptionResultEnum res;
        try
        {
            res = _inner.SetRemoteDescription(sdpType, sdpToApply);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[SDP][{_leg}][ERR] SetRemoteDescription threw: {ex.GetType().Name}: {ex.Message}");
            _log?.Invoke(ex.ToString());
            throw;
        }
        if (res != SetDescriptionResultEnum.OK)
        {
            _log?.Invoke($"[SDP][{_leg}] SetRemoteDescription result: {res}");
            try
            {
                DumpNegotiationState(sdpType, sdpToApply);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[SDP][{_leg}][WARN] negotiation dump failed: {ex.GetType().Name}: {ex.Message}");
            }

        }

        return res;
    }

    public Task Start() => _inner.Start();

    public void SetMediaStreamStatus(SDPMediaTypesEnum kind, MediaStreamStatusEnum status) =>
        _inner.SetMediaStreamStatus(kind, status);

    public Task SendDtmf(byte tone, CancellationToken ct) => _inner.SendDtmf(tone, ct);

    public void Close(string reason) => _inner.Close(reason);

    private static SDP BuildSanitizedSdp(SDP sdp, bool isRx)
    {
        // Some radios answer with PT=123 (R2S) added as if it was an audio codec, which SDP offer/answer does not allow.
        // Some SIPSorcery builds reject this in SetRemoteDescription. The most robust fix is to rebuild
        // a minimal, standards-ish SDP answer with only PCMA (PT=8) and the remote IP/port.
        var audio = sdp.Media?.FirstOrDefault(m => m?.Media == SDPMediaTypesEnum.audio);
        if (audio == null || audio.Port is <= 0 or >= 65535)
            throw new ArgumentException("A usable audio RTP port and separate RTCP port are required.");
        if (!string.Equals(audio.Transport, "RTP/AVP", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unsupported media transport (expected RTP/AVP).");
        if (audio.MediaFormats == null || !audio.MediaFormats.TryGetValue(8, out var pcma) ||
            (!string.IsNullOrEmpty(pcma.Rtpmap) &&
             !pcma.Rtpmap.Equals("PCMA/8000", StringComparison.OrdinalIgnoreCase) &&
             !pcma.Rtpmap.Equals("PCMA/8000/1", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Remote SDP did not offer PCMA/8000 on payload 8.");

        var ipStr = audio.Connection?.ConnectionAddress
            ?? sdp.Connection?.ConnectionAddress
            ?? sdp.AddressOrHost;
        if (!IPAddress.TryParse(ipStr, out var remoteIp) || remoteIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("A valid IPv4 media connection address is required.");

        var attrs = new List<string>();
        // Directions are parsed into MediaStreamStatus, not retained in ExtraMediaAttributes.
        // Normalize only a sendrecv answer to our recvonly offer; preserve hold/inactive.
        var direction = audio.MediaStreamStatus ?? MediaStreamStatusEnum.SendRecv;
        if (isRx && direction == MediaStreamStatusEnum.SendRecv) direction = MediaStreamStatusEnum.SendOnly;
        AddUnique(attrs, direction.ToString().ToLowerInvariant());
        if (audio.ExtraMediaAttributes != null)
        {
            // ptime + R2S hints (harmless even if ignored).
            foreach (var raw in audio.ExtraMediaAttributes)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // Parsed attributes are complete lines ("a=ptime:20").
                var a = raw.Trim();
                if (a.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) a = a[2..];
                if (a.StartsWith("ptime", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("R2S-", StringComparison.OrdinalIgnoreCase))
                {
                    AddUnique(attrs, a);
                }
            }
        }

        // Some radios echo our offer's o= line back in the 200 OK (including our local IP).
        // Some SIPSorcery versions appear to reject an answer whose origin address is "local".
        // Keep the session id/version but force the origin address to the remote IP.
        var ownerLine = BuildOwnerLineWithRemoteIp(sdp.Owner, remoteIp);

        var sdpText =
            "v=0\r\n" +
            $"o={ownerLine}\r\n" +
            "s=-\r\n" +
            $"c=IN IP4 {remoteIp}\r\n" +
            "t=0 0\r\n" +
            $"m=audio {audio.Port} RTP/AVP 8\r\n" +
            $"c=IN IP4 {remoteIp}\r\n" +
            "a=rtpmap:8 PCMA/8000\r\n" +
            string.Join("", attrs.Select(a => $"a={a}\r\n"));

        return SDP.ParseSDPDescription(sdpText) ?? sdp;
    }

    private static int? ReadPttId(SDP? sdp)
    {
        var audio = sdp?.Media?.FirstOrDefault(m => m?.Media == SDPMediaTypesEnum.audio);
        var attributes = (audio?.ExtraMediaAttributes ?? Enumerable.Empty<string>())
            .Concat(sdp?.ExtraSessionAttributes ?? Enumerable.Empty<string>());
        foreach (var raw in attributes)
        {
            var attribute = raw?.Trim() ?? "";
            if (attribute.StartsWith("a=", StringComparison.OrdinalIgnoreCase)) attribute = attribute[2..];
            if (!attribute.StartsWith("ptt-id:", StringComparison.OrdinalIgnoreCase) &&
                !attribute.StartsWith("ptt_id:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(attribute[7..].Trim(), out var id) && id is >= 0 and <= 63)
                return id;
        }
        return null;
    }

    private static string BuildOwnerLineWithRemoteIp(string? owner, IPAddress remoteIp)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return $"- 0 0 IN IP4 {remoteIp}";

        try
        {
            // Expected: "<username> <sess-id> <sess-ver> IN IP4 <addr>"
            var parts = owner.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6)
            {
                parts[5] = remoteIp.ToString();
                return string.Join(' ', parts);
            }
        }
        catch
        {
            // ignore
        }

        return $"- 0 0 IN IP4 {remoteIp}";
    }

    private static string Summarize(SDP? sdp)
    {
        if (sdp == null) return "-";
        try
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(sdp.Owner))
                parts.Add($"o={sdp.Owner}");
            if (!string.IsNullOrWhiteSpace(sdp.Connection?.ConnectionAddress))
                parts.Add($"c={sdp.Connection.ConnectionAddress}");

            if (sdp.Media != null)
            {
                foreach (var m in sdp.Media)
                {
                    if (m == null) continue;
                    if (m.Media != SDPMediaTypesEnum.audio) continue;
                    var fmt = m.MediaFormats == null ? "-" : string.Join(",", m.MediaFormats.Keys.OrderBy(x => x));
                    parts.Add($"m=audio {m.Port} {m.Transport} {fmt}");
                    if (m.ExtraMediaAttributes != null)
                    {
                        foreach (var a in m.ExtraMediaAttributes)
                        {
                            if (a == null) continue;
                            if (a.StartsWith("a=rtpmap", StringComparison.OrdinalIgnoreCase) ||
                                a.StartsWith("rtpmap", StringComparison.OrdinalIgnoreCase) ||
                                a.StartsWith("sendrecv", StringComparison.OrdinalIgnoreCase) ||
                                a.StartsWith("recvonly", StringComparison.OrdinalIgnoreCase) ||
                                a.StartsWith("sendonly", StringComparison.OrdinalIgnoreCase) ||
                                a.StartsWith("inactive", StringComparison.OrdinalIgnoreCase) ||
                                a.StartsWith("R2S-", StringComparison.OrdinalIgnoreCase) ||
                                a.StartsWith("ptime", StringComparison.OrdinalIgnoreCase))
                            {
                                parts.Add($"a={a.TrimStart('a', '=')}");
                            }
                        }
                    }
                    break;
                }
            }

            return string.Join(" | ", parts);
        }
        catch
        {
            return "(summary failed)";
        }
    }

    private static void AddUnique(List<string> list, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!list.Contains(value))
            list.Add(value);
    }

    private void DumpNegotiationState(SdpType sdpType, SDP remote)
    {
        if (_log == null) return;

        var voip = _inner as VoIPMediaSession;
        if (voip == null) return;

        var localTrack = voip.AudioStream?.LocalTrack;
        var negotiated = voip.AudioStream?.NegotiatedFormat;
        var localCaps = localTrack?.Capabilities == null
            ? "-"
            : string.Join(", ", localTrack.Capabilities.Select(c => c.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)));

        var remoteAudio = remote?.Media?.FirstOrDefault(m => m?.Media == SDPMediaTypesEnum.audio);
        var remoteFmt = remoteAudio?.MediaFormats == null
            ? "-"
            : string.Join(",", remoteAudio.MediaFormats.Keys.OrderBy(x => x));

        string? remoteCodec8 = null;
        try
        {
            if (remoteAudio?.MediaFormats != null && remoteAudio.MediaFormats.TryGetValue(8, out var fmt8))
                remoteCodec8 = fmt8.Rtpmap;
        }
        catch
        {
            // ignore
        }

        var remoteConn = remoteAudio?.Connection?.ConnectionAddress
            ?? remote?.Connection?.ConnectionAddress
            ?? remote?.AddressOrHost
            ?? "-";

        _log($"[SDP][{_leg}] Debug: sdpType={sdpType} rtpBind={voip.RtpBindAddress} localCaps=[{localCaps}] localStatus={localTrack?.StreamStatus} negotiated={negotiated?.Rtpmap} remoteConn={remoteConn} remotePort={remoteAudio?.Port} remoteStatus={remoteAudio?.MediaStreamStatus} remoteFmts=[{remoteFmt}] remote8={remoteCodec8}");
    }

}
