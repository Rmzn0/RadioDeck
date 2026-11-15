using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RadioDeck.Models;
using RadioDeck.Services.Localization;
using RadioDeck.ViewModels;

namespace RadioDeck.Services;

/// <summary>How a radio represents its operating frequency in SNMP.</summary>
public enum SnmpFrequencyUnit
{
    /// <summary>Integer in kHz, e.g. 118100.</summary>
    Khz,
    /// <summary>Integer in Hz, e.g. 118100000.</summary>
    Hz,
    /// <summary>Text in MHz, e.g. "118.100".</summary>
    MhzText
}

/// <summary>One unit to ask: the receiver, the transmitter or the transceiver.</summary>
public sealed record SnmpEndpoint(string Host, string Oid);

/// <summary>Where and how to reach the frequency object(s) of a radio.</summary>
public sealed record SnmpTarget(string Community, int Port, bool Version1, SnmpFrequencyUnit Unit, IReadOnlyList<SnmpEndpoint> Endpoints)
{
    public const string UnitKhz = "kHz";
    public const string UnitHz = "Hz";
    public const string UnitMhzText = "MHzText";
    public const string Version2c = "2c";
    public const string VersionOne = "1";

    public static SnmpFrequencyUnit ParseUnit(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "HZ" => SnmpFrequencyUnit.Hz,
        "MHZTEXT" => SnmpFrequencyUnit.MhzText,
        _ => SnmpFrequencyUnit.Khz
    };

    public static string UnitName(SnmpFrequencyUnit unit) => unit switch
    {
        SnmpFrequencyUnit.Hz => UnitHz,
        SnmpFrequencyUnit.MhzText => UnitMhzText,
        _ => UnitKhz
    };

    public static string NormalizeVersion(string? value) => value?.Trim() == VersionOne ? VersionOne : Version2c;

    /// <summary>
    /// The transmitter first, then the receiver. Each unit is asked at its own SNMP address
    /// (default: its SIP address) for its own object (default: the common frequency object).
    /// </summary>
    public static SnmpTarget For(SiteConfig site)
    {
        var endpoints = new List<SnmpEndpoint>();
        var commonOid = site.GetSnmpFrequencyOid() ?? "";

        void Add(string? snmpHost, string? sipHost, string? oid)
        {
            var host = SiteViewModel.NormalizeHost(string.IsNullOrWhiteSpace(snmpHost) ? sipHost : snmpHost);
            var cleanOid = (oid ?? "").Trim().TrimStart('.');
            if (host.Length == 0 || cleanOid.Length == 0) return;
            if (endpoints.Any(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) && e.Oid == cleanOid)) return;
            endpoints.Add(new SnmpEndpoint(host, cleanOid));
        }

        if (site.UsesTxLeg())
            Add(site.SnmpTxHost, site.EffectiveTxHost, string.IsNullOrWhiteSpace(site.SnmpTxFrequencyOid) ? commonOid : site.SnmpTxFrequencyOid);
        if (site.UsesRxLeg())
            Add(site.SnmpRxHost, site.EffectiveRxHost, commonOid);

        return new SnmpTarget(
            string.IsNullOrWhiteSpace(site.SnmpCommunity) ? "public" : site.SnmpCommunity.Trim(),
            site.SnmpPort is >= 1 and <= 65535 ? site.SnmpPort : 161,
            NormalizeVersion(site.SnmpVersion) == VersionOne,
            ParseUnit(site.SnmpFrequencyUnit),
            endpoints);
    }
}

/// <summary>Minimal SNMP v1/v2c GET and SET for the operating frequency.</summary>
public sealed class SnmpFrequencyClient
{
    private const int TimeoutMs = 1500;

    /// <summary>Writes the frequency to every unit of the radio.</summary>
    public async Task<IReadOnlyList<SnmpSetResult>> SetFrequencyAsync(SnmpTarget target, int frequencyKhz, CancellationToken ct)
    {
        var results = new List<SnmpSetResult>();
        foreach (var endpoint in target.Endpoints)
            results.Add(await SetAsync(endpoint, frequencyKhz, target, ct));
        return results;
    }

    /// <summary>The frequency of the first unit that answers.</summary>
    public async Task<SnmpGetResult> GetFrequencyAsync(SnmpTarget target, CancellationToken ct)
    {
        SnmpGetResult? lastError = null;
        foreach (var endpoint in target.Endpoints)
        {
            var result = await GetAsync(endpoint, target, ct);
            if (result.Success) return result;
            lastError = result;
        }

        return lastError ?? new SnmpGetResult(string.Empty, false, 0, "no host");
    }

    /// <summary>Asks every unit; used by the test button in the settings.</summary>
    public async Task<IReadOnlyList<SnmpGetResult>> GetAllAsync(SnmpTarget target, CancellationToken ct)
    {
        var results = new List<SnmpGetResult>();
        foreach (var endpoint in target.Endpoints)
            results.Add(await GetAsync(endpoint, target, ct));
        return results;
    }

    private static async Task<SnmpSetResult> SetAsync(SnmpEndpoint endpoint, int frequencyKhz, SnmpTarget target, CancellationToken ct)
    {
        try
        {
            var requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var value = target.Unit switch
            {
                SnmpFrequencyUnit.Hz => EncodeInteger(checked(frequencyKhz * 1000)),
                SnmpFrequencyUnit.MhzText => EncodeOctetString((frequencyKhz / 1000.0).ToString("F3", CultureInfo.InvariantCulture)),
                _ => EncodeInteger(frequencyKhz)
            };
            var response = await ExchangeAsync(endpoint, target, BuildPacket(0xA3, requestId, target, endpoint.Oid, value), ct);
            if (response.RequestId != requestId)
                return new SnmpSetResult(endpoint.Host, false, $"unexpected request-id {response.RequestId}");
            if (response.ErrorStatus != 0)
                return new SnmpSetResult(endpoint.Host, false, $"{GetErrorName(response.ErrorStatus)} (index {response.ErrorIndex})");
            return new SnmpSetResult(endpoint.Host, true, "ok");
        }
        catch (OperationCanceledException)
        {
            return new SnmpSetResult(endpoint.Host, false, Loc.T("Snmp_NoAnswer"));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return new SnmpSetResult(endpoint.Host, false, Loc.T("Snmp_NoAnswer")); // nothing listens on that port
        }
        catch (Exception ex)
        {
            return new SnmpSetResult(endpoint.Host, false, ex.Message);
        }
    }

    private static async Task<SnmpGetResult> GetAsync(SnmpEndpoint endpoint, SnmpTarget target, CancellationToken ct)
    {
        try
        {
            var requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            var response = await ExchangeAsync(endpoint, target,
                BuildPacket(0xA0, requestId, target, endpoint.Oid, EncodeTlv(0x05, Array.Empty<byte>())), ct);
            if (response.RequestId != requestId)
                return new SnmpGetResult(endpoint.Host, false, 0, $"unexpected request-id {response.RequestId}");
            if (response.ErrorStatus != 0)
                return new SnmpGetResult(endpoint.Host, false, 0, $"{GetErrorName(response.ErrorStatus)} (index {response.ErrorIndex})");
            return new SnmpGetResult(endpoint.Host, true, ToKhz(response.ValueTag, response.Value, target.Unit), "ok");
        }
        catch (OperationCanceledException)
        {
            return new SnmpGetResult(endpoint.Host, false, 0, Loc.T("Snmp_NoAnswer"));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return new SnmpGetResult(endpoint.Host, false, 0, Loc.T("Snmp_NoAnswer")); // nothing listens on that port
        }
        catch (Exception ex)
        {
            return new SnmpGetResult(endpoint.Host, false, 0, ex.Message);
        }
    }

    private static async Task<SnmpResponse> ExchangeAsync(SnmpEndpoint endpoint, SnmpTarget target, byte[] packet, CancellationToken ct)
    {
        using var udp = new UdpClient();
        // A connected socket takes an answer only from the unit that was asked.
        udp.Connect(endpoint.Host, target.Port);
        await udp.SendAsync(packet, packet.Length);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));
        var rx = await udp.ReceiveAsync(timeout.Token);
        return ParseResponse(rx.Buffer);
    }

    private static byte[] BuildPacket(byte pduTag, int requestId, SnmpTarget target, string oid, byte[] value)
    {
        var varBinds = EncodeSequence(EncodeSequence(Combine(EncodeOid(oid), value)));
        var pdu = EncodeTlv(pduTag, Combine(EncodeInteger(requestId), EncodeInteger(0), EncodeInteger(0), varBinds));
        return EncodeSequence(Combine(EncodeInteger(target.Version1 ? 0 : 1), EncodeOctetString(target.Community), pdu));
    }

    private readonly record struct SnmpResponse(int RequestId, int ErrorStatus, int ErrorIndex, byte ValueTag, byte[] Value);

    private static SnmpResponse ParseResponse(byte[] data)
    {
        var offset = 0;
        var top = ReadTlv(data, ref offset);
        if (top.Tag != 0x30)
            throw new InvalidOperationException("invalid SNMP response");

        var p = 0;
        var content = top.Value;
        _ = ReadTlv(content, ref p); // version
        _ = ReadTlv(content, ref p); // community
        var pdu = ReadTlv(content, ref p);
        if (pdu.Tag != 0xA2)
            throw new InvalidOperationException("unexpected PDU tag");

        var pp = 0;
        var requestId = DecodeInteger(ReadTlv(pdu.Value, ref pp).Value);
        var errorStatus = DecodeInteger(ReadTlv(pdu.Value, ref pp).Value);
        var errorIndex = DecodeInteger(ReadTlv(pdu.Value, ref pp).Value);
        if (errorStatus != 0)
            return new SnmpResponse(requestId, errorStatus, errorIndex, 0x05, Array.Empty<byte>());

        var varBindList = ReadTlv(pdu.Value, ref pp);
        if (varBindList.Tag != 0x30)
            throw new InvalidOperationException("missing varbind list");

        var vbOfs = 0;
        var firstVarBind = ReadTlv(varBindList.Value, ref vbOfs);
        if (firstVarBind.Tag != 0x30)
            throw new InvalidOperationException("missing varbind");

        var singleOfs = 0;
        _ = ReadTlv(firstVarBind.Value, ref singleOfs); // oid
        var valueTlv = ReadTlv(firstVarBind.Value, ref singleOfs);
        // An exception value (noSuchObject/noSuchInstance/endOfMibView) means the OID is not supported.
        if (valueTlv.Tag is 0x80 or 0x81 or 0x82)
            throw new InvalidOperationException(Loc.T("Snmp_NoSuchObject"));
        return new SnmpResponse(requestId, errorStatus, errorIndex, valueTlv.Tag, valueTlv.Value);
    }

    /// <summary>
    /// Radios answer with INTEGER, Counter32, Gauge32/Unsigned32, TimeTicks or a text,
    /// depending on the MIB. The configured unit says what the number means.
    /// </summary>
    private static int ToKhz(byte tag, byte[] value, SnmpFrequencyUnit unit)
    {
        double number;
        switch (tag)
        {
            case 0x02:
                number = DecodeInteger(value);
                break;
            case 0x41 or 0x42 or 0x43:
                number = DecodeUnsigned(value);
                break;
            case 0x04:
                var text = Encoding.ASCII.GetString(value).Trim('\0', ' ', '\r', '\n', '\t').Replace(',', '.');
                if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
                    throw new InvalidOperationException(Loc.T("Snmp_NotANumber", text.Length > 24 ? text[..24] : text));
                break;
            default:
                throw new InvalidOperationException($"unexpected value tag 0x{tag:X2}");
        }

        var khz = unit switch
        {
            SnmpFrequencyUnit.Hz => number / 1000.0,
            SnmpFrequencyUnit.MhzText => number * 1000.0,
            _ => number
        };
        if (double.IsNaN(khz) || khz < 0 || khz > 10_000_000)
            throw new InvalidOperationException(Loc.T("Snmp_NotANumber", number.ToString(CultureInfo.InvariantCulture)));
        return (int)Math.Round(khz, MidpointRounding.AwayFromZero);
    }

    private static double DecodeUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        return value;
    }
    private static string GetErrorName(int code) => code switch
    {
        0 => "noError",
        1 => "tooBig",
        2 => "noSuchName",
        3 => "badValue",
        4 => "readOnly",
        5 => "genErr",
        6 => "noAccess",
        7 => "wrongType",
        8 => "wrongLength",
        9 => "wrongEncoding",
        10 => "wrongValue",
        11 => "noCreation",
        12 => "inconsistentValue",
        13 => "resourceUnavailable",
        14 => "commitFailed",
        15 => "undoFailed",
        16 => "authorizationError",
        17 => "notWritable",
        18 => "inconsistentName",
        _ => $"error-{code}"
    };

    // Only non-negative values are ever sent (request ids, zero, a frequency in kHz).
    private static byte[] EncodeInteger(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Span<byte> temp = stackalloc byte[5];
        var pos = temp.Length;
        var v = value;
        do
        {
            temp[--pos] = (byte)(v & 0xFF);
            v >>= 8;
        }
        while (v > 0);

        if ((temp[pos] & 0x80) != 0)
            temp[--pos] = 0x00;

        return EncodeTlv(0x02, temp[pos..].ToArray());
    }

    private static int DecodeInteger(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return 0;
        // Two's complement: start from the sign of the first byte.
        long value = (bytes[0] & 0x80) != 0 ? -1 : 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }
        return (int)value;
    }

    private static byte[] EncodeOctetString(string value)
    {
        return EncodeTlv(0x04, System.Text.Encoding.ASCII.GetBytes(value ?? string.Empty));
    }

    private static byte[] EncodeOid(string oidText)
    {
        var parts = oidText.Split('.').Select(int.Parse).ToArray();
        if (parts.Length < 2)
            throw new ArgumentException("invalid OID", nameof(oidText));

        var body = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
        for (var i = 2; i < parts.Length; i++)
        {
            body.AddRange(EncodeOidSubId(parts[i]));
        }

        return EncodeTlv(0x06, body.ToArray());
    }

    private static IEnumerable<byte> EncodeOidSubId(int subId)
    {
        if (subId < 128)
            return new[] { (byte)subId };

        Span<byte> tmp = stackalloc byte[5];
        var pos = tmp.Length;
        var value = subId;

        tmp[--pos] = (byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            tmp[--pos] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        return tmp[pos..].ToArray();
    }

    private static byte[] EncodeSequence(byte[] content) => EncodeTlv(0x30, content);

    private static byte[] EncodeTlv(byte tag, byte[] value)
    {
        var length = EncodeLength(value.Length);
        var output = new byte[1 + length.Length + value.Length];
        output[0] = tag;
        Buffer.BlockCopy(length, 0, output, 1, length.Length);
        Buffer.BlockCopy(value, 0, output, 1 + length.Length, value.Length);
        return output;
    }

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
            return new[] { (byte)length };
        if (length <= 0xFF)
            return new[] { (byte)0x81, (byte)length };
        return new[] { (byte)0x82, (byte)(length >> 8), (byte)(length & 0xFF) };
    }

    private static (byte Tag, byte[] Value) ReadTlv(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
            throw new InvalidOperationException("truncated TLV");

        var tag = data[offset++];
        if (offset >= data.Length)
            throw new InvalidOperationException("truncated length");

        int length = data[offset++];
        if ((length & 0x80) != 0)
        {
            var count = length & 0x7F;
            if (count <= 0 || count > 2)
                throw new InvalidOperationException("unsupported length");
            length = 0;
            for (var i = 0; i < count; i++)
            {
                if (offset >= data.Length) throw new InvalidOperationException("truncated length bytes");
                length = (length << 8) | data[offset++];
            }
        }

        if (length < 0 || offset + length > data.Length)
            throw new InvalidOperationException("invalid TLV length");

        var value = new byte[length];
        Buffer.BlockCopy(data, offset, value, 0, length);
        offset += length;
        return (tag, value);
    }

    private static byte[] Combine(params byte[][] chunks)
    {
        var total = chunks.Sum(static c => c.Length);
        var output = new byte[total];
        var ofs = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, output, ofs, chunk.Length);
            ofs += chunk.Length;
        }
        return output;
    }
}

public readonly record struct SnmpSetResult(string Host, bool Success, string Message);
public readonly record struct SnmpGetResult(string Host, bool Success, int FrequencyKhz, string Message);
