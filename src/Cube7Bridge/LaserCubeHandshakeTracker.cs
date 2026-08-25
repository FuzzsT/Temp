using System.Security.Cryptography;
using System.Text.Json;

namespace Cube7Bridge;

/// <summary>
/// Passive handshake diagnostics for the LaserCube network protocol.
/// This class never generates authentication responses and never changes device state.
/// It only classifies traffic already observed by the hook.
/// </summary>
public sealed class LaserCubeHandshakeTracker
{
    private readonly string _summaryPath;
    private readonly object _sync = new();
    private int _highestStage;

    public LaserCubeHandshakeTracker(string captureDirectory)
    {
        _summaryPath = Path.Combine(captureDirectory, "handshake-summary.ndjson");
    }

    public string? Observe(HookRecord record)
    {
        if (record.Payload.Length == 0) return null;

        byte opcode = record.Payload[0];
        int length = record.Payload.Length;
        string? stage = null;
        int stageNumber = 0;
        string detail;

        switch (opcode)
        {
            case 0x27 when length == 1:
                stage = "DISCOVERY_REQUEST";
                stageNumber = 1;
                detail = "LaserOS requested GET_ALIVE on the discovery path";
                break;
            case 0x27 when length == 2 && record.Payload[1] == 0x00:
                stage = "DISCOVERY_ACCEPTED";
                stageNumber = 2;
                detail = "exact LaserCube alive response 27 00 observed";
                break;
            case 0x77 when length == 1:
                stage = "FULL_INFO_REQUEST";
                stageNumber = 3;
                detail = "LaserOS requested 0x77 full device information";
                break;
            case 0x77 when length == 64 && record.Payload[1] == 0x00 && record.Payload[2] == 0x00:
                stage = "FULL_INFO_ACCEPTED";
                stageNumber = 4;
                detail = "64-byte full-info response with success/result version bytes observed";
                break;
            case 0xB0 when length > 2:
                stage = "AUTH_REQUEST";
                stageNumber = 5;
                detail = "LaserOS emitted a security request; bridge remains passive";
                break;
            case 0xB0 when length == 2 && record.Payload[1] == 0x00:
                stage = "AUTH_REQUEST_ACK";
                stageNumber = 6;
                detail = "device acknowledged the security request";
                break;
            case 0xB1 when length == 1:
                stage = "AUTH_RESPONSE_QUERY";
                stageNumber = 7;
                detail = "LaserOS requested the security response";
                break;
            case 0xB1 when length >= 3:
                stage = "AUTH_RESPONSE_CAPTURED";
                stageNumber = 8;
                detail = record.Payload[1] == 0x00 && record.Payload[2] == 0x00
                    ? "security response transport status is success; LaserOS callback still decides authenticity"
                    : "security response transport status is not success";
                break;
            case 0x78 or 0x82 or 0x8A or 0x8D or 0xA0 when _highestStage >= 8:
                stage = "POST_AUTH_DEVICE_TRAFFIC";
                stageNumber = 9;
                detail = "device initialization/control traffic observed after security-response stage";
                break;
            default:
                return null;
        }

        string digest = Convert.ToHexString(SHA256.HashData(record.Payload)).ToLowerInvariant();
        string direction = record.Direction == 1 ? "TX" : "RX";
        bool advanced;

        lock (_sync)
        {
            advanced = stageNumber > _highestStage;
            if (advanced) _highestStage = stageNumber;

            var row = new
            {
                timeUtc = DateTime.FromFileTimeUtc((long)record.Timestamp100ns),
                stage,
                stageNumber,
                advanced,
                direction,
                api = record.Api,
                remote = record.RemoteAddress,
                port = record.Port,
                payloadLength = length,
                payloadSha256 = digest,
                detail
            };
            File.AppendAllText(_summaryPath, JsonSerializer.Serialize(row) + Environment.NewLine);
        }

        if (!advanced) return null;
        return $"[stage] {stage} len={length} sha256={digest[..16]}... — {detail}";
    }
}
