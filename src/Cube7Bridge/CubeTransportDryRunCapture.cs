using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cube7Bridge;

/// <summary>
/// Converts renderer frames to Cube dataFormatType=3 and persists the official
/// plaintext AD/A5 transport envelope for inspection. This class performs no
/// BLE I/O, no encryption, and no optical-output state changes.
/// </summary>
public sealed class CubeTransportDryRunCapture : IDisposable
{
    private readonly int _bufferMax;
    private readonly int _dataFormatType;
    private readonly StreamWriter _summary;
    private readonly FileStream _raw;
    private readonly object _sync = new();

    public string SummaryPath { get; }
    public string RawPath { get; }

    public CubeTransportDryRunCapture(string directory, int bufferMax, int dataFormatType)
    {
        if (bufferMax <= 47 || bufferMax > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bufferMax));
        if (dataFormatType != 3)
            throw new ArgumentOutOfRangeException(nameof(dataFormatType), "This dry-run bridge currently validates Cube dataFormatType=3 only.");

        _bufferMax = bufferMax;
        _dataFormatType = dataFormatType;
        Directory.CreateDirectory(directory);
        SummaryPath = Path.Combine(directory, "cube-transport-plaintext.ndjson");
        RawPath = Path.Combine(directory, "cube-transport-plaintext.bin");
        _summary = new StreamWriter(new FileStream(SummaryPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _raw = new FileStream(RawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public void Write(RendererFrame frame, bool invertY)
    {
        var converted = CubeFormat3Translator.Translate(frame, invertY);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var frames = CubeOfficialProtocol.BuildPatternTransferFrames(
            converted.LayerPayload,
            _dataFormatType,
            _bufferMax,
            timestamp,
            runArg: 0);

        lock (_sync)
        {
            long rawOffset = _raw.Position;
            var frameLengths = new int[frames.Length];
            var commands = new string[frames.Length];
            int totalRawBytes = 0;

            for (int i = 0; i < frames.Length; i++)
            {
                byte[] transportFrame = frames[i];
                if (transportFrame.Length > _bufferMax)
                    throw new InvalidDataException($"Transport frame {i + 1} exceeds bufferMax {_bufferMax}: {transportFrame.Length}");

                frameLengths[i] = transportFrame.Length;
                commands[i] = $"0x{transportFrame[0]:X2}";

                Span<byte> prefix = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)transportFrame.Length));
                _raw.Write(prefix);
                _raw.Write(transportFrame);
                totalRawBytes += 4 + transportFrame.Length;
            }
            _raw.Flush();

            var row = new
            {
                timeUtc = DateTime.UtcNow,
                rendererId = $"0x{frame.RendererId:X}",
                rate = frame.Rate,
                pointCount = converted.PointCount,
                layerPayloadLength = converted.LayerPayload.Length,
                layerPayloadSha256 = Convert.ToHexString(SHA256.HashData(converted.LayerPayload)).ToLowerInvariant(),
                frameCount = frames.Length,
                frameLengths,
                commands,
                bufferMax = _bufferMax,
                dataFormatType = _dataFormatType,
                rawOffset,
                totalRawBytes,
                encrypted = false,
                dryRun = true,
                physicalOutput = false
            };
            _summary.WriteLine(JsonSerializer.Serialize(row));
        }
    }

    public void Dispose()
    {
        _summary.Dispose();
        _raw.Dispose();
    }
}
