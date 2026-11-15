using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RadioDeck.Models;
using RadioDeck.Services.Audio;
using RadioDeck.Services.Localization;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;

namespace RadioDeck.Services;

/// <summary>
/// SIP/RTP engine for ED-137 style radio endpoints (no registration, direct call).
///
/// - RX leg: a receive-only session; audio is played while it is connected.
/// - TX leg: stays established with the software packet gate CLOSED. PTT opens the
///   gate: encoded microphone frames are sent or dropped, the capture device itself
///   is never paused so it is already running when PTT is pressed.
/// - Transceivers use only the TX leg; its received audio is the radio's RX audio.
/// - Both legs send R2S keepalives (RTP payload type 123) while idle.
/// </summary>
public sealed class SipsorceryRadioEngine : ISipRadioEngine
{
    private const int R2sPayloadType = 123;
    private const ushort Ed137ExtensionProfile = 0x0167;
    private const int KeepAlivePeriodMs = 200;
    private const int KeepAliveMultiplier = 10;

    /// <summary>A radio on the LAN answers within milliseconds; do not wait for the 32 s SIP timeout.</summary>
    public const int ConnectTimeoutMs = 8000;

    private const int LevelDecayMs = 500;
    private const int LevelUpdateMinIntervalMs = 40;

    // The transmitter and the receivers listening to it need a moment after PTT.
    // Microphone audio captured during that lead-in (AudioConfig.TxLeadMs) is
    // delayed, not dropped, so the first syllable is not lost. After a longer
    // pause the first transmission gets at least the cold value.
    private const int TxColdPreKeyMs = 260;
    private const int TxColdIdleThresholdMs = 5000;
    private const int TxQueueMaxFrames = 80;
    private const int TxMicAliveMs = 120;

    // After the key is released: speech still inside the capture device is collected
    // for a moment, the delayed speech is sent, and the transmitter stays keyed for
    // AudioConfig.TxTailMs so the radio can play out what it has buffered.
    private const int TxCaptureTailMs = 60;

    // The noise gate opens only after a few loud frames. The frames before that are
    // kept and sent first when it opens, so the beginning of a word is not cut.
    private const int TxNoiseGateOpenLevel = 5;
    private const int TxNoiseGateCloseLevel = 3;
    private const int TxNoiseGateAttackFrames = 3;
    private const int TxNoiseGateHangoverMs = 400;
    private const int TxNoiseGateLookBackFrames = 6;

    private static readonly byte[] PcmaSilence160 = G711.SilenceFrame(160);

    // SIPSorcery 10.0.17 exposes no setter for the RTP timestamp of a locally built packet.
    private static readonly FieldInfo MediaStreamTrackTimestampField =
        typeof(MediaStreamTrack).GetField("<Timestamp>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException("Incompatible media library: RTP timestamp field not found.");

    private sealed class Leg(string name, bool isTx)
    {
        public readonly string Name = name;
        public readonly bool IsTx = isTx;
        public readonly object PacketLock = new();
        public readonly object KeepAliveLock = new();
        public SIPUserAgent? Ua;
        public Ed137MediaSession? Media;
        public RadioAudioSink? Sink;
        public RxPlaybackGate? Gate;
        /// <summary>Addresses of the radio this leg calls; SIP is taken only from them.</summary>
        public volatile IPAddress[] Peers = Array.Empty<IPAddress>();
        public volatile bool Connected;
        public Timer? KeepAliveTimer;
        public uint KeepAliveTimestamp;
        public uint KeepAliveTimestampStep;
        public int KeepAliveSent;
        public int? FailStatus;
        public string? FailReason;
        public IPAddress Local = IPAddress.Loopback;
        public RadioProfile Profile = RadioProfile.Basic;
        public int PttId = 1;
        public bool CarriesRxAudio;
    }

    private readonly SIPTransport _transport;
    private readonly SIPUDPChannel _udp;
    private readonly IMicrophoneSource? _microphone;
    private readonly Func<AudioConfig?>? _audioConfig;
    private readonly Leg _rx = new("RX", isTx: false);
    private readonly Leg _tx = new("TX", isTx: true);
    private readonly Timer _levelTimer;
    private IDisposable? _micSubscription;
    private volatile bool _isSendingToTx;
    private volatile bool _rxMuted;
    private volatile int _volume = 100;
    private volatile int _pan;
    private int _disposed;

    private int _rxLevel;
    private int _txLevel;
    private long _lastRxLevelMs;
    private long _lastTxLevelMs;
    private int _rxSquelch;
    private int _txConfirmed;

    // If TX audio is not flowing (microphone failure) keepalives must continue even while
    // the gate is open, otherwise the radio hangs up with "missing R2S KeepAlive".
    private long _lastTxAudioPacketSentMs;
    private long _foreignSipMessages;

    // While PTT is ON and the microphone delivers nothing, periodic silence frames keep
    // the transmitter keyed and the session alive. The same timer sends the pre-key
    // silence and flushes delayed speech after PTT is released.
    private Timer? _txSilenceTimer;
    private readonly object _txSilenceLock = new();
    private readonly Queue<byte[]> _txPreKeyBuffer = new();
    private readonly Queue<byte[]> _txGateLookBack = new();
    private TaskCompletionSource? _txDrained;
    private bool _txDraining;      // the key was released; the rest of the speech is going out
    private long _txDrainAtMs;     // until then microphone frames are still accepted
    private long _txHoldUntilMs;   // the transmitter stays keyed until then (0 = not started)
    private int _txLeadMs = AudioConfig.DefaultTxLeadMs;
    private int _txTailMs = AudioConfig.DefaultTxTailMs;
    private bool _txLastFrameGated;
    private long _txAudioReleaseAtMs;
    private long _lastTxStopMs;
    private long _lastTxMicFrameMs;
    private long _lastTxMicLogMs;
    private long _lastTxSilenceLogMs;
    private long _lastTxNoiseGateLogMs;
    private bool _txNoiseGateEnabled;
    private bool _txNoiseGateOpen;
    private int _txNoiseGateAttackFrames;
    private long _txNoiseGateOpenUntilMs;

    public bool IsRxConnected => _rx.Connected;
    public bool IsTxConnected => _tx.Connected;
    public bool IsSendingToTx { get => _isSendingToTx; private set => _isSendingToTx = value; }
    public bool CanTransmit => IsTxConnected && _tx.Media?.AudioStream?.RemoteTrack?.StreamStatus is
        MediaStreamStatusEnum.SendRecv or MediaStreamStatusEnum.RecvOnly;

    public void SetRxMuted(bool muted) => _rxMuted = muted;

    /// <summary>Received audio frames handed to playback on the current calls (diagnostics and checks).</summary>
    public long PlaybackFrames => (_rx.Gate?.FramesReceived ?? 0) + (_tx.Gate?.FramesReceived ?? 0);

    public void SetRxVolume(int volumePercent, int pan)
    {
        _volume = volumePercent;
        _pan = pan;
        _rx.Sink?.SetVolume(volumePercent, pan);
        _tx.Sink?.SetVolume(volumePercent, pan);
    }

    public event Action<int>? RxLevelChanged;
    public event Action<int>? TxLevelChanged;
    public event Action<string>? Log;
    public event Action<string>? Error;
    public event Action? RxCallEnded;
    public event Action? TxCallEnded;
    public event Action<bool>? RxSquelchChanged;
    public event Action<bool>? TxConfirmedChanged;

    /// <param name="microphone">Shared capture; null means no microphone (silence is transmitted).</param>
    /// <param name="audioConfig">Supplies the output device name for playback.</param>
    public SipsorceryRadioEngine(IMicrophoneSource? microphone = null, Func<AudioConfig?>? audioConfig = null)
    {
        _microphone = microphone;
        _audioConfig = audioConfig;
        _transport = new SIPTransport();
        _udp = new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0));
        _transport.AddSIPChannel(_udp);
        AcceptSipOnlyFromRadios();
        AttachTransportTrace();
        _levelTimer = new Timer(_ => DecayLevels(), null, 250, 250);
    }

    // SIP has no protection of its own here: a BYE that merely quotes the Call-ID ends a
    // call. Messages are therefore taken only from the addresses this engine calls.
    private void AcceptSipOnlyFromRadios()
    {
        var deliver = _udp.SIPMessageReceived;
        _udp.SIPMessageReceived = (channel, local, remote, buffer) =>
        {
            if (deliver == null) return Task.CompletedTask;
            if (IsRadioAddress(remote.Address)) return deliver(channel, local, remote, buffer);

            var count = Interlocked.Increment(ref _foreignSipMessages);
            if (IsWorthLogging(count))
                Log?.Invoke($"[SIP] Ignored a message from {remote.Address}: not a radio of this entry ({count} so far).");
            return Task.CompletedTask;
        };
    }

    private bool IsRadioAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var peer in _rx.Peers) if (peer.Equals(address)) return true;
        foreach (var peer in _tx.Peers) if (peer.Equals(address)) return true;
        return false;
    }

    // Dropped packets are reported, but a flood of them must not fill the log.
    private static bool IsWorthLogging(long count) => count is 1 or 10 or 100 or 1000 || count % 10000 == 0;

    private void AttachTransportTrace()
    {
        _transport.SIPRequestOutTraceEvent += (_, _, req) =>
        {
            if (req?.Method is SIPMethodsEnum.BYE or SIPMethodsEnum.CANCEL)
            {
                Log?.Invoke($"[SIP][OUT] {req.Method} {req.URI} | Call-ID:{req.Header?.CallId} | Reason:{req.Header?.Reason ?? "-"}");
                return;
            }
            if (req?.Method != SIPMethodsEnum.INVITE) return;

            var from = req.Header?.From?.FromURI?.ToString() ?? "-";
            var to = req.Header?.To?.ToURI?.ToString() ?? "-";
            var contact = req.Header?.Contact?.FirstOrDefault()?.ContactURI?.ToString() ?? "-";
            Log?.Invoke($"[SIP][OUT] INVITE {req.URI} | From:{from} | To:{to} | Contact:{contact} | CT:{req.Header?.ContentType ?? "-"} | CL:{req.Header?.ContentLength ?? -1}");
            var sdpSummary = GetSdpSummary(req.Body, req.BodyBuffer, req.SIPBodyEncoding);
            if (!string.IsNullOrWhiteSpace(sdpSummary))
                Log?.Invoke($"[SIP][OUT][SDP] {sdpSummary}");
        };

        _transport.SIPResponseInTraceEvent += (_, _, resp) =>
        {
            if (resp.Header?.CSeqMethod != SIPMethodsEnum.INVITE) return;
            var statusCode = (int)resp.Status;
            if (statusCode < 200) return;

            Log?.Invoke($"[SIP][IN] {statusCode} {resp.ReasonPhrase} (INVITE)");
            if (statusCode >= 300) return;
            var cl = resp.Header?.ContentLength ?? -1;
            if (cl <= 0 && string.IsNullOrWhiteSpace(resp.Body)) return;
            var sdpSummary = GetSdpSummary(resp.Body, resp.BodyBuffer, resp.SIPBodyEncoding);
            if (!string.IsNullOrWhiteSpace(sdpSummary))
                Log?.Invoke($"[SIP][IN][SDP] CT:{resp.Header?.ContentType ?? "-"} CL:{cl} | {sdpSummary}");
        };

        _transport.SIPRequestInTraceEvent += (_, remote, req) =>
        {
            if (req?.Method != SIPMethodsEnum.BYE) return;
            var from = req.Header?.From?.FromURI?.ToString() ?? "-";
            var to = req.Header?.To?.ToURI?.ToString() ?? "-";
            Log?.Invoke($"[SIP][IN] BYE from {remote} | Call-ID:{req.Header?.CallId} | From:{from} | To:{to} | Reason:{req.Header?.Reason ?? "-"}");
        };
    }

    // ---------------------------------------------------------------- connect / disconnect

    public Task ConnectRxAsync(string sipUri, SiteConfig site, CancellationToken ct) => ConnectLegAsync(_rx, sipUri, site, ct);

    public Task ConnectTxAsync(string sipUri, SiteConfig site, CancellationToken ct) => ConnectLegAsync(_tx, sipUri, site, ct);

    public Task DisconnectRxAsync(CancellationToken ct)
    {
        DisconnectLeg(_rx);
        return Task.CompletedTask;
    }

    public Task DisconnectTxAsync(CancellationToken ct)
    {
        DisconnectLeg(_tx);
        return Task.CompletedTask;
    }

    private async Task ConnectLegAsync(Leg leg, string sipUri, SiteConfig site, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        DisconnectLeg(leg);

        try
        {
            if (string.IsNullOrWhiteSpace(sipUri))
                throw new ArgumentException("SIP URI is empty.", nameof(sipUri));
            ArgumentNullException.ThrowIfNull(site);

            var callUri = EnsureUserPart(sipUri, leg.IsTx ? site.TxUser : site.RxUser);
            var ok = await CallAsync(leg, callUri, site, ct).ConfigureAwait(false);

            if (!ok && leg.FailStatus == 404 && ContainsUserPart(callUri))
            {
                var hostOnlyUri = ToHostOnlyUri(callUri);
                if (!string.Equals(hostOnlyUri, callUri, StringComparison.OrdinalIgnoreCase))
                {
                    Log?.Invoke($"[SIP][{leg.Name}] Retry without user: {hostOnlyUri}");
                    DisconnectLeg(leg);
                    ok = await CallAsync(leg, hostOnlyUri, site, ct).ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var ua = leg.Ua;
            var media = leg.Media;
            ok = ok && ua != null && media != null && ua.IsCallActive && !media.IsClosed;
            if (!ok)
                throw new RadioCallException($"{leg.Name} {HostOf(callUri)}: {leg.FailReason ?? Loc.T("Status_CallFailed")}");

            lock (leg.PacketLock) leg.Connected = true;
            if (leg.IsTx)
            {
                // The gate starts CLOSED; the microphone runs from now on so PTT is instant.
                IsSendingToTx = false;
                _micSubscription ??= _microphone?.Subscribe(OnMicrophoneFrame);
            }
            StartKeepAlive(leg);
            Log?.Invoke(leg.IsTx ? "[SIP][TX] Connected (PTT gate closed)" : "[SIP][RX] Connected");
        }
        catch (Exception ex)
        {
            DisconnectLeg(leg);
            if (ex is OperationCanceledException)
            {
                Log?.Invoke($"[SIP][{leg.Name}] Connect cancelled.");
                throw;
            }

            var msg = $"[SIP][{leg.Name}][ERR] {(ex is RadioCallException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}")}";
            Log?.Invoke(msg);
            if (ex is not RadioCallException) Log?.Invoke(ex.ToString());
            Error?.Invoke(msg);
            throw;
        }
    }

    private async Task<bool> CallAsync(Leg leg, string callUri, SiteConfig site, CancellationToken ct)
    {
        leg.FailStatus = null;
        leg.FailReason = null;
        PrepareLeg(leg, callUri, site);
        var ua = leg.Ua ?? throw new InvalidOperationException($"{leg.Name} user agent was not initialized.");
        var media = leg.Media ?? throw new InvalidOperationException($"{leg.Name} media session was not initialized.");

        var descriptor = CreateCallDescriptor(callUri, site, leg);
        Log?.Invoke($"[SIP][{leg.Name}] Calling {callUri} ({leg.Profile.Id})");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeoutMs);
        try
        {
            var session = new SdpSanitizingMediaSession(media, Log, leg.Name);
            var ok = await ua.Call(descriptor, session, 0).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (ok && leg.Profile.UseAssignedPttId && session.RemotePttId is int pttId)
            {
                leg.PttId = pttId;
                Log?.Invoke($"[SIP][{leg.Name}] Radio assigned PTT-ID {pttId}.");
            }
            return ok && ReferenceEquals(ua, leg.Ua);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            leg.FailReason = Loc.T("Status_NoAnswer", ConnectTimeoutMs / 1000);
            return false;
        }
        catch (NullReferenceException ex)
        {
            throw new InvalidOperationException(
                "The SIP call failed inside the media stack. Check the audio devices of this PC.", ex);
        }
    }

    private void PrepareLeg(Leg leg, string sipUri, SiteConfig site)
    {
        var ua = new SIPUserAgent(_transport, null);
        leg.Ua = ua;
        leg.Profile = site.GetProfile();
        leg.PttId = 1;
        leg.CarriesRxAudio = leg.IsTx && site.GetLinkMode() == RadioLinkMode.Transceiver;

        ua.ClientCallTrying += (_, resp) =>
            Log?.Invoke($"[SIP][{leg.Name}] Trying: {(int)resp.Status} {resp.ReasonPhrase}");
        ua.ClientCallRinging += (_, resp) =>
            Log?.Invoke($"[SIP][{leg.Name}] Ringing: {(int)resp.Status} {resp.ReasonPhrase}");
        ua.ClientCallFailed += (_, err, resp) =>
        {
            if (ReferenceEquals(ua, leg.Ua))
            {
                leg.FailStatus = resp == null ? null : (int)resp.Status;
                leg.FailReason = resp == null
                    ? (string.IsNullOrWhiteSpace(err) ? "no SIP response" : err)
                    : Loc.T("Status_RadioAnswered", (int)resp.Status, resp.ReasonPhrase);
            }
            Log?.Invoke($"[SIP][{leg.Name}] Failed: {err} ({(resp == null ? "no SIP response" : $"{(int)resp.Status} {resp.ReasonPhrase}")})");
        };
        ua.ClientCallAnswered += (_, resp) =>
        {
            Log?.Invoke($"[SIP][{leg.Name}] Answered: {(int)resp.Status} {resp.ReasonPhrase}");
            if (!ReferenceEquals(ua, leg.Ua) || Volatile.Read(ref _disposed) != 0) return;
            // Keepalives must start with the answer; the radio drops a silent session quickly.
            if (!leg.Connected)
            {
                leg.Connected = true;
                StartKeepAlive(leg);
            }
        };
        ua.OnCallHungup += dialog => OnRemoteHangup(leg, ua, dialog);

        leg.Peers = ResolvePeerAddresses(sipUri);
        leg.Local = ResolveLocalConnectionAddress(sipUri);
        Log?.Invoke($"[SIP][{leg.Name}] Local RTP/SDP address: {leg.Local}");

        Ed137MediaSession media;
        RadioAudioSink sink;
        try
        {
            sink = new RadioAudioSink(() => _audioConfig?.Invoke()?.OutputDevice);
            sink.SetVolume(_volume, _pan);
            var gate = new RxPlaybackGate(sink, () => _rxMuted);
            leg.Gate = gate;
            var endpoints = new MediaEndPoints
            {
                AudioSource = new SilentAudioSource(),
                AudioSink = gate
            };
            media = new Ed137MediaSession(endpoints, KeepAlivePeriodMs, KeepAliveMultiplier, leg.Local)
            {
                // The session filters by address itself and presents every accepted packet
                // with the signalled end point, see Ed137MediaSession.
                AcceptRtpFromAny = false,
                SdpOptions = BuildSdpOptions(site, leg.IsTx)
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize the {leg.Name} media session.", ex);
        }

        leg.Media = media;
        leg.Sink = sink;
        AttachRtpReceiveTrace(leg, media);
        AttachMediaDiagnostics(leg, media);

        // Radios are strict about the offered formats: PCMA only, no telephone-event.
        media.AudioStream.LocalTrack.RestrictCapabilities(new AudioFormat(AudioCodecsEnum.PCMA, 8, 8000, 1, null));
        media.AudioStream.LocalTrack.NoDtmfSupport = true;
        media.SetMediaStreamStatus(SDPMediaTypesEnum.audio,
            leg.IsTx ? MediaStreamStatusEnum.SendRecv : MediaStreamStatusEnum.RecvOnly);
    }

    // Only reached for calls that ended without a local disconnect: DisconnectLeg
    // detaches the user agent first.
    private void OnRemoteHangup(Leg leg, SIPUserAgent ua, SIPDialogue? dialog)
    {
        if (!ReferenceEquals(ua, leg.Ua)) return;
        StopKeepAlive(leg);
        leg.KeepAliveSent = 0;
        if (leg.IsTx)
        {
            StopTxSilence();
            lock (leg.PacketLock)
            {
                leg.Connected = false;
                ResetTxState();
            }
            Interlocked.Exchange(ref _lastTxStopMs, NowMs());
            ReleaseMicrophone();
            SetConfirmed(false);
            if (leg.CarriesRxAudio) SetSquelch(false);
            TxLevelChanged?.Invoke(0);
        }
        else
        {
            leg.Connected = false;
            SetSquelch(false);
            RxLevelChanged?.Invoke(0);
        }
        Log?.Invoke($"[SIP][{leg.Name}] Hung up | Call-ID:{dialog?.CallId}");
        (leg.IsTx ? TxCallEnded : RxCallEnded)?.Invoke();
    }

    private void DisconnectLeg(Leg leg)
    {
        var ua = leg.Ua;
        var media = leg.Media;
        if (ua != null || media != null)
            Log?.Invoke($"[SIP][{leg.Name}] Local disconnect/cleanup requested.");

        // Detach first so the hangup below is not reported as a dropped call.
        leg.Ua = null;
        leg.Media = null;
        leg.Sink = null;
        leg.Gate = null;

        if (leg.IsTx)
        {
            lock (leg.PacketLock)
            {
                leg.Connected = false;
                ResetTxState();
            }
            StopTxSilence();
            ReleaseMicrophone();
            SetConfirmed(false);
            if (leg.CarriesRxAudio) SetSquelch(false);
        }
        else
        {
            lock (leg.PacketLock) leg.Connected = false;
            SetSquelch(false);
        }

        StopKeepAlive(leg);
        leg.KeepAliveTimestamp = 0;
        leg.KeepAliveTimestampStep = 0;
        leg.KeepAliveSent = 0;

        try
        {
            if (ua?.IsCallActive == true) ua.Hangup();
            else if (ua?.IsCalling == true || ua?.IsRinging == true) ua.Cancel();
        }
        catch (Exception ex) { Log?.Invoke($"[SIP][{leg.Name}][WARN] Disconnect: {ex.Message}"); }
        try { media?.Close("Disconnected"); } catch { }
        try { ua?.Dispose(); } catch { }
    }

    private void ReleaseMicrophone()
    {
        Interlocked.Exchange(ref _micSubscription, null)?.Dispose();
    }

    // Caller holds the TX packet lock.
    private void ResetTxState()
    {
        IsSendingToTx = false;
        _txDraining = false;
        _txDrainAtMs = 0;
        _txHoldUntilMs = 0;
        _txPreKeyBuffer.Clear();
        _txGateLookBack.Clear();
        Interlocked.Exchange(ref _lastTxAudioPacketSentMs, 0);
        Interlocked.Exchange(ref _txAudioReleaseAtMs, 0);
        ResetTxNoiseGate(null);
        var drained = _txDrained;
        _txDrained = null;
        drained?.TrySetResult();
    }

    // ---------------------------------------------------------------- PTT

    public Task StartTxAudioAsync(CancellationToken ct, AudioConfig? audio = null)
    {
        lock (_tx.PacketLock)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            StartTxAudioCore(audio);
        }
        return Task.CompletedTask;
    }

    private void StartTxAudioCore(AudioConfig? audio)
    {
        if (!IsTxConnected) return;
        if (IsSendingToTx)
        {
            // Pressed again while the tail of the previous transmission was still going out.
            _txDraining = false;
            _txDrainAtMs = 0;
            _txHoldUntilMs = 0;
            return;
        }
        if (!CanTransmit)
        {
            Log?.Invoke("[PTT] Ignored: remote SDP does not allow receiving TX audio.");
            return;
        }

        ResetTxNoiseGate(audio);
        _txLeadMs = Math.Clamp(audio?.TxLeadMs ?? AudioConfig.DefaultTxLeadMs, 0, AudioConfig.MaxTxTimingMs);
        _txTailMs = Math.Clamp(audio?.TxTailMs ?? AudioConfig.DefaultTxTailMs, 0, AudioConfig.MaxTxTimingMs);

        var nowMs = NowMs();
        var lastStopMs = Interlocked.Read(ref _lastTxStopMs);
        var idleMs = lastStopMs > 0 ? nowMs - lastStopMs : long.MaxValue;
        var preKeyMs = _txLeadMs > 0 && idleMs >= TxColdIdleThresholdMs ? Math.Max(_txLeadMs, TxColdPreKeyMs) : _txLeadMs;

        IsSendingToTx = true;
        _txDraining = false;
        _txDrainAtMs = 0;
        _txHoldUntilMs = 0;
        Interlocked.Exchange(ref _lastTxAudioPacketSentMs, 0);
        Interlocked.Exchange(ref _txAudioReleaseAtMs, nowMs + preKeyMs);
        _txPreKeyBuffer.Clear();
        _txGateLookBack.Clear();

        // Basic profile: the radio is woken with a short keepalive burst carrying the PTT-ID.
        if (!_tx.Profile.KeepAliveCarriesPttType)
            SendTxKeepAliveBurst(4);
        // Key the transmitter now; the following packets are clocked by the microphone.
        SendTxAudioPacket(PcmaSilence160);
        StartTxSilence();
        Log?.Invoke($"[R2S][KA][TX] state ON ext={ExtensionHex(_tx, keepAlive: true)} lead={preKeyMs}ms tail={_txTailMs}ms idle={FormatIdleMs(idleMs)}");
        if (_txNoiseGateEnabled)
            Log?.Invoke($"[PTT][NOISEGATE] enabled open={TxNoiseGateOpenLevel}% close={TxNoiseGateCloseLevel}%");
        Log?.Invoke("[PTT] ON (RTP gate open)");
    }

    public async Task StopTxAudioAsync(CancellationToken ct)
    {
        TaskCompletionSource? drained;
        int waitMs;
        lock (_tx.PacketLock)
        {
            if (!IsSendingToTx) return;
            var nowMs = NowMs();
            if (nowMs < Interlocked.Read(ref _txAudioReleaseAtMs) && _txPreKeyBuffer.Count == 0)
            {
                // Released before the transmitter was up and nothing was said.
                StopTxAudioCore();
                return;
            }

            // The last word is still on its way: in the capture device, behind the lead-in
            // delay and in the radio's own buffer. Send it out before unkeying.
            if (!_txDraining)
            {
                _txDraining = true;
                _txDrainAtMs = nowMs + TxCaptureTailMs;
                _txHoldUntilMs = 0;
            }
            drained = _txDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Upper bound only; normally the stream ends by itself when everything was sent.
            var leadLeftMs = (int)Math.Max(0, Interlocked.Read(ref _txAudioReleaseAtMs) - nowMs);
            waitMs = leadLeftMs + TxCaptureTailMs + TxMicAliveMs
                     + (_txPreKeyBuffer.Count + TxNoiseGateLookBackFrames + 4) * 40 + _txTailMs + 300;
        }

        await Task.WhenAny(drained.Task, Task.Delay(waitMs, CancellationToken.None)).ConfigureAwait(false);
        lock (_tx.PacketLock)
        {
            // A new press during the tail cancels the release; that transmission continues.
            if (_txDraining) StopTxAudioCore();
        }
    }

    // Caller holds the TX packet lock.
    private void StopTxAudioCore()
    {
        if (!IsSendingToTx) return;
        ResetTxState();
        StopTxSilence();
        Interlocked.Exchange(ref _lastTxStopMs, NowMs());
        SendTxKeepAliveBurst(3);
        Log?.Invoke($"[R2S][KA][TX] state OFF ext={ExtensionHex(_tx, keepAlive: true)}");
        Log?.Invoke("[PTT] OFF (RTP gate closed)");
    }

    private void StartTxSilence()
    {
        if (!IsTxConnected) return;
        lock (_txSilenceLock)
        {
            // ptime 20 ms = 160 samples at 8 kHz. The first packet was sent by the caller.
            _txSilenceTimer ??= new Timer(_ => OnTxTimerTick(), null, 20, 20);
        }
    }

    private void StopTxSilence()
    {
        lock (_txSilenceLock)
        {
            _txSilenceTimer?.Dispose();
            _txSilenceTimer = null;
        }
    }

    private void OnTxTimerTick()
    {
        try
        {
            lock (_tx.PacketLock)
            {
                if (!IsTxConnected || !IsSendingToTx) return;

                var nowMs = NowMs();
                var lastMicMs = Interlocked.Read(ref _lastTxMicFrameMs);
                if (lastMicMs > 0 && nowMs - lastMicMs < TxMicAliveMs)
                    return; // microphone frames clock the stream: one packet per captured frame

                // No microphone frames: this timer keeps the transmitter keyed and the
                // stream running, and finishes a release that is under way.
                var idle = !_txDraining && nowMs >= Interlocked.Read(ref _txAudioReleaseAtMs) && _txPreKeyBuffer.Count == 0;
                PumpTx(null, nowMs);
                if (idle && nowMs - Interlocked.Read(ref _lastTxSilenceLogMs) >= 2000)
                {
                    Interlocked.Exchange(ref _lastTxSilenceLogMs, nowMs);
                    Log?.Invoke("[PTT][SILENCE] sending PCMA silence (no microphone frames).");
                }
            }
        }
        catch
        {
            // best effort; the next tick tries again
        }
    }

    private void OnMicrophoneFrame(byte[] sample)
    {
        if (sample.Length == 0) return;
        lock (_tx.PacketLock)
        {
            var nowMs = NowMs();
            Interlocked.Exchange(ref _lastTxMicFrameMs, nowMs);
            if (!IsTxConnected || !IsSendingToTx) return;

            PumpTx(sample, nowMs);

            if (!IsSendingToTx || nowMs - Interlocked.Read(ref _lastTxMicLogMs) < 1000) return;
            Interlocked.Exchange(ref _lastTxMicLogMs, nowMs);
            Log?.Invoke($"[PTT][MIC] {(_txLastFrameGated ? "gated" : "speech")} len={sample.Length} delayed={_txPreKeyBuffer.Count * 20}ms");
        }
    }

    /// <summary>
    /// Sends exactly one 20 ms packet: lead-in silence, the oldest delayed speech frame,
    /// or silence. Called once per captured microphone frame, or by the timer when the
    /// microphone delivers nothing (<paramref name="micFrame"/> is null).
    /// Caller holds the TX packet lock.
    /// </summary>
    private void PumpTx(byte[]? micFrame, long nowMs)
    {
        // After the key was released, frames are accepted a little longer: they were
        // spoken before the release and were still inside the capture device.
        var releasing = _txDraining && nowMs >= _txDrainAtMs;
        if (micFrame != null && !releasing)
            AcceptMicrophoneFrame(micFrame, nowMs);

        if (nowMs < Interlocked.Read(ref _txAudioReleaseAtMs))
        {
            SendTxAudioPacket(PcmaSilence160); // the transmitter is keying up; speech waits in the queue
            return;
        }

        if (_txPreKeyBuffer.TryDequeue(out var speech))
        {
            _txHoldUntilMs = 0;
            SendTxAudioPacket(speech);
            UpdateTxLevel(G711.Level(speech));
            return;
        }

        if (releasing)
        {
            // Everything was sent. Stay keyed while the radio plays out its buffer.
            if (_txHoldUntilMs == 0) _txHoldUntilMs = nowMs + _txTailMs;
            if (nowMs >= _txHoldUntilMs)
            {
                StopTxAudioCore();
                return;
            }
        }

        SendTxAudioPacket(PcmaSilence160);
        UpdateTxLevel(0);
    }

    // Caller holds the TX packet lock.
    private void AcceptMicrophoneFrame(byte[] frame, long nowMs)
    {
        var wasGated = _txLastFrameGated;
        _txLastFrameGated = ShouldNoiseGateTxFrame(frame, nowMs);
        if (_txLastFrameGated)
        {
            if (!wasGated)
            {
                // The gate closes: let the background fade out instead of stopping with a click.
                EnqueueTxFrame(G711.Fade(frame, fadeIn: false));
                return;
            }

            // Not sent, which also lets the delay shrink during a pause. Kept for a moment:
            // if speech starts now, its soft beginning is in these frames.
            if (_txGateLookBack.Count >= TxNoiseGateLookBackFrames)
                _txGateLookBack.Dequeue();
            _txGateLookBack.Enqueue(frame);
            return;
        }

        if (wasGated)
        {
            // The gate opens: send what led up to it first, faded in.
            var first = _txGateLookBack.TryDequeue(out var oldest) ? oldest : null;
            if (first == null)
            {
                EnqueueTxFrame(G711.Fade(frame, fadeIn: true));
                return;
            }

            EnqueueTxFrame(G711.Fade(first, fadeIn: true));
            while (_txGateLookBack.TryDequeue(out var earlier))
                EnqueueTxFrame(earlier);
        }

        EnqueueTxFrame(frame);
    }

    // Caller holds the TX packet lock.
    private void EnqueueTxFrame(byte[] sample)
    {
        if (_txPreKeyBuffer.Count >= TxQueueMaxFrames)
            _txPreKeyBuffer.Dequeue();
        _txPreKeyBuffer.Enqueue(sample);
    }

    private void ResetTxNoiseGate(AudioConfig? audio)
    {
        _txNoiseGateEnabled = audio?.Vad == true;
        _txLastFrameGated = _txNoiseGateEnabled; // the gate starts closed
        _txNoiseGateOpen = false;
        _txNoiseGateAttackFrames = 0;
        Interlocked.Exchange(ref _txNoiseGateOpenUntilMs, 0);
        Interlocked.Exchange(ref _lastTxNoiseGateLogMs, 0);
    }

    private bool ShouldNoiseGateTxFrame(byte[] sample, long nowMs)
    {
        if (!_txNoiseGateEnabled)
            return false;

        var level = G711.Level(sample);
        if (level >= TxNoiseGateOpenLevel)
        {
            _txNoiseGateAttackFrames++;
            if (_txNoiseGateAttackFrames >= TxNoiseGateAttackFrames)
            {
                _txNoiseGateOpen = true;
                Interlocked.Exchange(ref _txNoiseGateOpenUntilMs, nowMs + TxNoiseGateHangoverMs);
                return false;
            }
        }
        else if (level <= TxNoiseGateCloseLevel)
        {
            _txNoiseGateAttackFrames = 0;
        }

        var openUntilMs = Interlocked.Read(ref _txNoiseGateOpenUntilMs);
        if (_txNoiseGateOpen && nowMs < openUntilMs)
            return false;

        if (_txNoiseGateOpen && level > TxNoiseGateCloseLevel)
        {
            Interlocked.Exchange(ref _txNoiseGateOpenUntilMs, nowMs + TxNoiseGateHangoverMs);
            return false;
        }

        _txNoiseGateOpen = false;
        if (nowMs - Interlocked.Read(ref _lastTxNoiseGateLogMs) >= 2000)
        {
            Interlocked.Exchange(ref _lastTxNoiseGateLogMs, nowMs);
            Log?.Invoke("[PTT][NOISEGATE] idle mic muted; sending clean PCMA silence.");
        }
        return true;
    }

    // Caller holds the TX packet lock.
    private void SendTxAudioPacket(byte[] sample)
    {
        // A capture/timer callback already in flight must not re-key after PTT release.
        if (!IsTxConnected || !IsSendingToTx) return;

        var audio = _tx.Media?.AudioStream;
        if (audio == null) return;

        var dst = audio.DestinationEndPoint;
        if (dst == null)
        {
            TryApplyRemoteRtpDestination(audio, _tx.Ua?.Dialogue?.RemoteSDP);
            dst = audio.DestinationEndPoint;
        }
        if (dst == null) return;

        var channel = audio.GetRTPChannel();
        var track = audio.LocalTrack;
        if (channel == null || track == null) return;

        var ts = track.Timestamp;
        var packet = new RTPPacket
        {
            Header = new RTPHeader
            {
                Version = RTPHeader.RTP_VERSION,
                PaddingFlag = 0,
                HeaderExtensionFlag = 1,
                CSRCCount = 0,
                MarkerBit = 0,
                PayloadType = audio.GetSendingFormat().ID,
                SequenceNumber = track.GetNextSeqNum(),
                Timestamp = ts,
                SyncSource = track.Ssrc,
                ExtensionProfile = Ed137ExtensionProfile,
                ExtensionLength = 1,
                ExtensionPayload = BuildExtension(_tx, pttOn: true, keepAlive: false)
            },
            Payload = sample
        };

        channel.Send(RTPChannelSocketsEnum.RTP, dst, packet.GetBytes());
        Interlocked.Exchange(ref _lastTxAudioPacketSentMs, NowMs());
        MediaStreamTrackTimestampField.SetValue(track, ts + (uint)sample.Length);
    }

    // ---------------------------------------------------------------- R2S keepalive

    private void StartKeepAlive(Leg leg)
    {
        if (!leg.Connected) return;
        lock (leg.KeepAliveLock)
        {
            leg.KeepAliveTimer ??= new Timer(_ => SendKeepAlive(leg), null, 0, KeepAlivePeriodMs);
        }
    }

    private static void StopKeepAlive(Leg leg)
    {
        lock (leg.KeepAliveLock)
        {
            leg.KeepAliveTimer?.Dispose();
            leg.KeepAliveTimer = null;
        }
    }

    private void SendKeepAlive(Leg leg)
    {
        try
        {
            if (!leg.Connected) return;
            // Do not interleave keepalives with live TX audio, but keep sending them when
            // no audio packet actually left (microphone or device trouble).
            if (leg.IsTx && IsSendingToTx)
            {
                var last = Interlocked.Read(ref _lastTxAudioPacketSentMs);
                if (last > 0 && NowMs() - last < 500)
                    return;
            }

            SendKeepAlivePacket(leg);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[R2S][KA][WARN] {ex.Message}");
        }
    }

    private void SendTxKeepAliveBurst(int count)
    {
        for (var i = 0; i < count; i++)
            SendKeepAlivePacket(_tx);
    }

    private void SendKeepAlivePacket(Leg leg)
    {
        lock (leg.PacketLock)
        {
            if (!leg.Connected) return;
            var audio = leg.Media?.AudioStream;
            if (audio == null) return;

            var dst = audio.DestinationEndPoint;
            if (dst == null)
            {
                TryApplyRemoteRtpDestination(audio, leg.Ua?.Dialogue?.RemoteSDP);
                dst = audio.DestinationEndPoint;
            }
            if (dst == null) return;

            var channel = audio.GetRTPChannel();
            var track = audio.LocalTrack;
            if (channel == null || track == null) return;

            if (leg.KeepAliveTimestampStep == 0)
            {
                var rate = TryParseClockRate(audio.GetSendingFormat().Rtpmap) ?? 8000;
                leg.KeepAliveTimestampStep = (uint)(rate * KeepAlivePeriodMs / 1000);
            }
            if (leg.KeepAliveTimestamp == 0)
                leg.KeepAliveTimestamp = track.Timestamp;

            var ts = leg.KeepAliveTimestamp;
            leg.KeepAliveTimestamp = ts + leg.KeepAliveTimestampStep;
            var extension = BuildExtension(leg, pttOn: leg.IsTx && IsSendingToTx, keepAlive: true);

            var packet = new RTPPacket
            {
                Header = new RTPHeader
                {
                    Version = RTPHeader.RTP_VERSION,
                    PaddingFlag = 0,
                    HeaderExtensionFlag = 1,
                    CSRCCount = 0,
                    MarkerBit = 0,
                    PayloadType = R2sPayloadType,
                    SequenceNumber = track.GetNextSeqNum(),
                    Timestamp = ts,
                    SyncSource = track.Ssrc,
                    ExtensionProfile = Ed137ExtensionProfile,
                    ExtensionLength = 1,
                    ExtensionPayload = extension
                },
                Payload = Array.Empty<byte>()
            };

            channel.Send(RTPChannelSocketsEnum.RTP, dst, packet.GetBytes());

            var count = Interlocked.Increment(ref leg.KeepAliveSent);
            if (count <= 3 || (leg.IsTx && IsSendingToTx && count <= 8))
                Log?.Invoke($"[R2S][KA][{leg.Name}] sent #{count} to {dst} ext={BitConverter.ToString(extension)}");
        }
    }

    private static int? TryParseClockRate(string? rtpmap)
    {
        if (string.IsNullOrWhiteSpace(rtpmap)) return null;
        var parts = rtpmap.Split('/');
        if (parts.Length < 2) return null;
        return int.TryParse(parts[1], out var rate) ? rate : null;
    }

    // ---------------------------------------------------------------- ED-137 RTP header extension

    /// <summary>
    /// First word of the ED-137 header extension: PTT type (3 bits), SQU (1 bit),
    /// PTT-ID (6 bits). Audio always carries "normal PTT ON". In the basic profile the
    /// keepalives sent during a transmission keep the type at 0 and carry only the
    /// PTT-ID (0x00400000); the generic profiles send PTT ON in every packet.
    /// </summary>
    public static uint BuildExtensionWord(RadioProfile profile, int pttId, bool pttOn, bool keepAlive)
    {
        if (!pttOn) return 0;
        var type = keepAlive && !profile.KeepAliveCarriesPttType ? 0u : 1u;
        return (type << 29) | ((uint)(pttId & 0x3F) << 22);
    }

    private static byte[] BuildExtension(Leg leg, bool pttOn, bool keepAlive)
    {
        var extension = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(extension, BuildExtensionWord(leg.Profile, leg.PttId, pttOn, keepAlive));
        return extension;
    }

    private string ExtensionHex(Leg leg, bool keepAlive) =>
        BitConverter.ToString(BuildExtension(leg, leg.IsTx && IsSendingToTx, keepAlive));

    /// <summary>Reads the first extension word of a received ED-137 RTP packet.</summary>
    public static bool TryReadExtensionWord(ReadOnlySpan<byte> packet, out uint word)
    {
        word = 0;
        if (packet.Length < 20 || (packet[0] >> 6) != 2 || (packet[0] & 0x10) == 0) return false;
        var offset = 12 + (packet[0] & 0x0F) * 4;
        if (packet.Length < offset + 8) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]) != Ed137ExtensionProfile) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]) < 1) return false;
        word = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
        return true;
    }

    private void AttachRtpReceiveTrace(Leg leg, Ed137MediaSession media)
    {
        media.RadioRtpReceived += (source, data) =>
        {
            try
            {
                if (!ReferenceEquals(media, leg.Media)) return;

                if (TryReadExtensionWord(data, out var word))
                {
                    // Radio -> console: bit 28 = squelch open, bits 31-29 = PTT type on air.
                    if (!leg.IsTx || leg.CarriesRxAudio) SetSquelch((word & 0x10000000u) != 0);
                    if (leg.IsTx) SetConfirmed((word >> 29) != 0);
                }

                if (data.Length < 13 || (data[1] & 0x7F) != 8) return; // PCMA only
                if (!RTPPacket.TryParse(data, out var packet, out _) || packet?.Payload is not { Length: > 0 } payload) return;
                var level = G711.Level(payload);
                if (!leg.IsTx || leg.CarriesRxAudio)
                    UpdateLevel(ref _rxLevel, ref _lastRxLevelMs, level, RxLevelChanged);
                else
                    UpdateLevel(ref _txLevel, ref _lastTxLevelMs, level, TxLevelChanged);
            }
            catch
            {
                // malformed packet
            }
        };
    }

    private void SetSquelch(bool open)
    {
        var value = open ? 1 : 0;
        if (Interlocked.Exchange(ref _rxSquelch, value) != value) RxSquelchChanged?.Invoke(open);
    }

    private void SetConfirmed(bool keyed)
    {
        var value = keyed ? 1 : 0;
        if (Interlocked.Exchange(ref _txConfirmed, value) != value) TxConfirmedChanged?.Invoke(keyed);
    }

    private void AttachMediaDiagnostics(Leg leg, Ed137MediaSession media)
    {
        var received = 0;
        media.R2sReceived += remote =>
        {
            if (Interlocked.Increment(ref received) == 1)
                Log?.Invoke($"[R2S][{leg.Name}] Valid remote keepalive received from {remote}; inactivity watchdog refreshed.");
        };
        media.OnTimeout += kind => Log?.Invoke(
            $"[RTP][{leg.Name}][TIMEOUT] {kind}; last remote activity={media.AudioRtcpSession?.LastActivityAt:O}; received={media.AudioRtcpSession?.PacketsReceivedCount}");
        media.OnRtpClosed += reason => Log?.Invoke($"[RTP][{leg.Name}] Closed: {reason}");
        media.ForeignPacketDropped += (remote, count) =>
        {
            if (IsWorthLogging(count))
                Log?.Invoke($"[RTP][{leg.Name}] Ignored a packet from {remote.Address}: not the media address of the radio ({media.AudioStream.DestinationEndPoint?.Address.ToString() ?? "not known yet"}); {count} so far.");
        };
        media.Media.AudioSink.OnAudioSinkError += error => Log?.Invoke($"[AUDIO][{leg.Name}][SINK] {error}");
    }

    // ---------------------------------------------------------------- SIP / SDP shaping

    private static Ed137SdpOptions BuildSdpOptions(SiteConfig site, bool isTx)
    {
        var profile = site.GetProfile();
        if (!profile.StandardSdp) return new Ed137SdpOptions();

        // ED-137B/C volume 1: session type and direction are spelled out in the offer.
        var mode = site.GetLinkMode();
        var attributes = new List<string>();
        if (!isTx)
        {
            attributes.Add("type:Radio-Rxonly");
            attributes.Add("txrxmode:Rx");
        }
        else
        {
            attributes.Add("type:Radio-TxRx");
            attributes.Add(mode == RadioLinkMode.Transceiver ? "txrxmode:TxRx" : "txrxmode:Tx");
        }
        attributes.Add("sigtime:1");
        attributes.Add("ptt_rep:0");
        attributes.Add("rtphe:1");
        if (!string.IsNullOrWhiteSpace(site.Frequency))
            attributes.Add($"fid:{site.Frequency.Trim()}");
        return new Ed137SdpOptions { AdvertiseR2s = true, Attributes = attributes };
    }

    private static List<string> BuildHeaders(SiteConfig site, bool isTx)
    {
        var profile = site.GetProfile();
        var headers = new List<string>();
        if (profile.FrequencySubject)
        {
            // Basic profile: "<frequency> TxRx Radio-TxRx" / "<frequency> Rx Radio-Rxonly".
            var freq = string.IsNullOrWhiteSpace(site.Frequency) ? "123.000" : site.Frequency.Trim();
            headers.Add(isTx ? $"Subject: {freq} TxRx Radio-TxRx" : $"Subject: {freq} Rx Radio-Rxonly");
        }
        else
        {
            headers.Add("Subject: radio");
            headers.Add("Priority: normal");
        }

        if (site.GetWg67Version() is { Length: > 0 } version)
            headers.Add($"WG67-Version: {version}");
        return headers;
    }

    private SIPCallDescriptor CreateCallDescriptor(string sipUri, SiteConfig site, Leg leg)
    {
        // Use the concrete local address that RTP/SDP will use.
        var localHost = leg.Local.ToString();
        var localPort = _udp.Port > 0 ? _udp.Port : 5060;
        var fromUser = (leg.IsTx ? site.TxUser : site.RxUser)?.Trim();
        var fromUri = string.IsNullOrWhiteSpace(fromUser)
            ? $"sip:{localHost}:{localPort}"
            : $"sip:{fromUser}@{localHost}:{localPort}";

        // The 2-argument constructor is (destination URI, SDP).
        var descriptor = new SIPCallDescriptor(sipUri, string.Empty)
        {
            From = fromUri,
            To = sipUri,
            FromURIHost = localHost,
            CustomHeaders = BuildHeaders(site, leg.IsTx)
        };

        if (!string.IsNullOrWhiteSpace(fromUser))
            descriptor.FromURIUsername = fromUser;

        if (!string.IsNullOrWhiteSpace(site.User))
        {
            descriptor.Username = site.User.Trim();
            descriptor.AuthUsername = site.User.Trim();
            descriptor.Password = site.Pass?.Trim() ?? string.Empty;
        }

        return descriptor;
    }

    private static IPAddress ResolveLocalConnectionAddress(string sipUri)
    {
        // The SDP must carry the address of the interface that actually reaches the radio.
        try
        {
            var uri = SIPURI.ParseSIPURI(sipUri);
            // SIPURI.Host is "host:port"; HostAddress is the host alone.
            if (uri != null && !string.IsNullOrWhiteSpace(uri.HostAddress))
            {
                var remoteAddress = ResolveRemoteAddress(uri.HostAddress);
                if (remoteAddress != null)
                {
                    using var socket = new Socket(remoteAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    socket.Connect(new IPEndPoint(remoteAddress, ParseSipPort(uri)));
                    if (socket.LocalEndPoint is IPEndPoint local)
                        return local.Address;
                }
            }
        }
        catch
        {
            // fall back below
        }

        try
        {
            var ip = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
            if (ip != null)
                return ip;
        }
        catch
        {
            // ignore
        }

        return IPAddress.Loopback;
    }

    private static IPAddress[] ResolvePeerAddresses(string sipUri)
    {
        try
        {
            var host = SIPURI.ParseSIPURI(sipUri)?.HostAddress;
            if (string.IsNullOrWhiteSpace(host)) return Array.Empty<IPAddress>();
            var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : Dns.GetHostAddresses(host);
            return addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToArray();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static int ParseSipPort(SIPURI uri) =>
        int.TryParse(uri.HostPort, out var port) && port is > 0 and <= 65535 ? port : 5060;

    private static IPAddress? ResolveRemoteAddress(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string EnsureUserPart(string sipUri, string? user)
    {
        if (string.IsNullOrWhiteSpace(sipUri) || string.IsNullOrWhiteSpace(user) || ContainsUserPart(sipUri))
            return sipUri;

        try
        {
            var uri = SIPURI.ParseSIPURI(sipUri);
            if (uri == null || string.IsNullOrWhiteSpace(uri.Host))
                return sipUri;

            return $"sip:{user.Trim()}@{uri.Host}"; // Host already includes the port, if any
        }
        catch
        {
            return sipUri;
        }
    }

    private static string StripScheme(string sipUri) =>
        sipUri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? sipUri[4..] : sipUri;

    private static bool ContainsUserPart(string sipUri) => StripScheme(sipUri).Contains('@');

    private static string HostOf(string sipUri)
    {
        var core = StripScheme(sipUri);
        var atIdx = core.LastIndexOf('@');
        return atIdx >= 0 && atIdx < core.Length - 1 ? core[(atIdx + 1)..] : core;
    }

    private static string ToHostOnlyUri(string sipUri) => $"sip:{HostOf(sipUri)}";

    private static void TryApplyRemoteRtpDestination(AudioStream audio, string? remoteSdp)
    {
        if (string.IsNullOrWhiteSpace(remoteSdp)) return;

        try
        {
            var sdp = SDP.ParseSDPDescription(remoteSdp);
            var audioMedia = sdp?.Media?.FirstOrDefault(m => m.Media == SDPMediaTypesEnum.audio);
            if (sdp == null || audioMedia == null || audioMedia.Port <= 0) return;

            var ip = audioMedia.Connection?.ConnectionAddress
                ?? sdp.Connection?.ConnectionAddress
                ?? sdp.AddressOrHost;
            if (!IPAddress.TryParse(ip, out var remoteIp)) return;

            audio.DestinationEndPoint = new IPEndPoint(remoteIp, audioMedia.Port);
            audio.ControlDestinationEndPoint = new IPEndPoint(remoteIp, audioMedia.Port + 1);
        }
        catch
        {
            // the next keepalive tick tries again
        }
    }

    private static string GetSdpSummary(string? sdpBody, byte[]? sdpBodyBuffer, System.Text.Encoding? bodyEncoding)
    {
        if (string.IsNullOrWhiteSpace(sdpBody) && sdpBodyBuffer is { Length: > 0 })
        {
            try { sdpBody = (bodyEncoding ?? System.Text.Encoding.UTF8).GetString(sdpBodyBuffer); }
            catch { sdpBody = null; }
        }

        if (string.IsNullOrWhiteSpace(sdpBody)) return string.Empty;

        var wanted = new[]
        {
            "o=", "c=", "m=audio", "a=rtpmap", "a=sendrecv", "a=recvonly", "a=sendonly", "a=inactive",
            "a=R2S-", "a=ptime", "a=type", "a=txrxmode", "a=ptt", "a=fid", "a=rtphe", "a=sigtime", "a=bss"
        };
        var lines = sdpBody
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => wanted.Any(w => x.StartsWith(w, StringComparison.OrdinalIgnoreCase)));

        return string.Join(" | ", lines);
    }

    // ---------------------------------------------------------------- level meters

    private void DecayLevels()
    {
        var now = NowMs();
        if (now - Interlocked.Read(ref _lastRxLevelMs) > LevelDecayMs && Interlocked.Exchange(ref _rxLevel, 0) != 0)
            RxLevelChanged?.Invoke(0);
        if (now - Interlocked.Read(ref _lastTxLevelMs) > LevelDecayMs && Interlocked.Exchange(ref _txLevel, 0) != 0)
            TxLevelChanged?.Invoke(0);
    }

    private void UpdateTxLevel(int level) => UpdateLevel(ref _txLevel, ref _lastTxLevelMs, level, TxLevelChanged);

    private static void UpdateLevel(ref int levelField, ref long lastMs, int level, Action<int>? handler)
    {
        var now = NowMs();
        if (now - Interlocked.Read(ref lastMs) < LevelUpdateMinIntervalMs) return;
        Interlocked.Exchange(ref lastMs, now);
        if (Interlocked.Exchange(ref levelField, level) == level) return;
        handler?.Invoke(level);
    }

    private static long NowMs() => Environment.TickCount64;

    private static string FormatIdleMs(long idleMs)
    {
        if (idleMs == long.MaxValue)
            return "cold";
        if (idleMs < 1000)
            return $"{idleMs}ms";
        return $"{idleMs / 1000.0:0.0}s";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _levelTimer.Dispose();
        DisconnectLeg(_tx);
        DisconnectLeg(_rx);
        _transport.Shutdown();
    }
}
