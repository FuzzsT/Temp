using System.Buffers.Binary;
using System.Reflection;
using Cube7Bridge;

static void Fail(string message) => throw new InvalidOperationException(message);
static void Require(bool ok, string message) { if (!ok) Fail(message); }

var asm = typeof(RendererPoint).Assembly;
var palette = asm.GetType("Cube7Bridge.CubeAppPalette");
Require(palette is not null, "CubeAppPalette type is missing");

var countProp = palette!.GetProperty("Count", BindingFlags.Public | BindingFlags.Static);
Require(countProp is not null, "CubeAppPalette.Count is missing");
Require((int)countProp!.GetValue(null)! == 250, "Cube.zip palette must contain exactly 250 entries");

var exact = palette.GetMethod("IndexOfExact", BindingFlags.Public | BindingFlags.Static);
Require(exact is not null, "CubeAppPalette.IndexOfExact is missing");
int E(byte r, byte g, byte b) => (int)exact!.Invoke(null, [r, g, b])!;
Require(E(0,0,0) == 0, "palette black index");
Require(E(255,255,255) == 1, "palette white index");
Require(E(255,0,0) == 2, "palette red index");
Require(E(255,255,0) == 3, "palette yellow index");
Require(E(0,255,0) == 4, "palette green index");
Require(E(0,255,255) == 5, "palette cyan index");
Require(E(0,0,255) == 6, "palette blue index");
Require(E(255,0,255) == 7, "palette magenta index");
Require(E(64,64,64) == 249, "palette final gray index from Cube.zip");

var nearest = palette.GetMethod("FindNearestIndex", BindingFlags.Public | BindingFlags.Static);
Require(nearest is not null, "CubeAppPalette.FindNearestIndex is missing");
int N(byte r, byte g, byte b) => (int)nearest!.Invoke(null, [r, g, b])!;
Require(N(250, 2, 1) == 2, "nearest red quantization");

var translator = asm.GetType("Cube7Bridge.CubeFormat3Translator");
Require(translator is not null, "CubeFormat3Translator type is missing");
var translate = translator!.GetMethod("Translate", BindingFlags.Public | BindingFlags.Static, [typeof(RendererFrame), typeof(bool)]);
Require(translate is not null, "CubeFormat3Translator.Translate(RendererFrame,bool) is missing");

var frame = new RendererFrame(30000, 0, 0x1234, [
    new RendererPoint(-1.0f, -1.0f, 0x00FF0000, 1),
    new RendererPoint( 1.0f,  1.0f, 0x0000FF00, 1),
]);
var converted = translate!.Invoke(null, [frame, false]);
Require(converted is not null, "format3 conversion returned null");
var convertedType = converted!.GetType();
var payload = (byte[])convertedType.GetProperty("LayerPayload")!.GetValue(converted)!;
var pointCount = (int)convertedType.GetProperty("PointCount")!.GetValue(converted)!;
Require(pointCount == 2, "format3 point count");
Require(payload.Length == 14, "type3 must be 2-byte count + 6 bytes/point");
Require(payload[0] == 0x00 && payload[1] == 0x02, "big-endian layer point count");
Require(payload[2] == 0x00 && payload[3] == 0x00 && payload[4] == 0x00 && payload[5] == 0x00, "-1,-1 maps to 0,0");
Require(payload[6] == 0x40 && payload[7] == 0x02, "first point state=64 + red palette index");
Require(payload[8] == 0xFF && payload[9] == 0xFF && payload[10] == 0xFF && payload[11] == 0xFF, "+1,+1 maps to 65535,65535");
Require(payload[12] == 0x80 && payload[13] == 0x04, "last point end=128 + green palette index");

var repeated = new RendererFrame(30000, 0, 0x5678, [new RendererPoint(0, 0, 0x000000FF, 3)]);
var repeatedConverted = translate.Invoke(null, [repeated, false])!;
Require((int)repeatedConverted.GetType().GetProperty("PointCount")!.GetValue(repeatedConverted)! == 3, "renderer repeat count must be expanded");

var captureType = asm.GetType("Cube7Bridge.CubeFormat3Capture");
Require(captureType is not null, "CubeFormat3Capture type is missing");
var ctor = captureType!.GetConstructor([typeof(string)]);
Require(ctor is not null, "CubeFormat3Capture(string) constructor is missing");
var write = captureType.GetMethod("Write", BindingFlags.Public | BindingFlags.Instance, [typeof(RendererFrame), typeof(bool)]);
Require(write is not null, "CubeFormat3Capture.Write(RendererFrame,bool) is missing");

var tempDir = Path.Combine(Path.GetTempPath(), "cube7-format3-contract-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    var capture = ctor!.Invoke([tempDir]);
    write!.Invoke(capture, [frame, false]);
    (capture as IDisposable)?.Dispose();

    var rawPath = Path.Combine(tempDir, "cube-format3.bin");
    var ndjsonPath = Path.Combine(tempDir, "cube-format3.ndjson");
    Require(File.Exists(rawPath), "cube-format3.bin was not created");
    Require(File.Exists(ndjsonPath), "cube-format3.ndjson was not created");

    var raw = File.ReadAllBytes(rawPath);
    Require(raw.Length == 4 + payload.Length, "raw capture must contain one length-prefixed payload");
    Require(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)) == payload.Length, "raw length prefix mismatch");
    Require(raw.AsSpan(4).SequenceEqual(payload), "raw dry-run payload differs from CubeFormat3Translator output");

    var line = File.ReadLines(ndjsonPath).Single();
    Require(line.Contains("\"pointCount\":2"), "ndjson pointCount missing");
    Require(line.Contains("\"payloadLength\":14"), "ndjson payloadLength missing");
    Require(line.Contains("\"dryRun\":true"), "ndjson must declare dryRun=true");
    Require(line.Contains("\"physicalOutput\":false"), "ndjson must declare physicalOutput=false");
}
finally
{
    try { Directory.Delete(tempDir, true); } catch { }
}

// RED contract for the v26-confirmed AD/A5/85 transport envelope.
var official = asm.GetType("Cube7Bridge.CubeOfficialProtocol");
Require(official is not null, "CubeOfficialProtocol type is missing");
var buildTransfer = official!.GetMethod(
    "BuildPatternTransferFrames",
    BindingFlags.Public | BindingFlags.Static,
    [typeof(byte[]), typeof(int), typeof(int), typeof(string), typeof(byte)]);
Require(buildTransfer is not null, "CubeOfficialProtocol.BuildPatternTransferFrames is missing");

var transferData = Enumerable.Range(0, 250).Select(i => (byte)i).ToArray();
var transferFrames = (byte[][])buildTransfer!.Invoke(null, [transferData, 3, 100, "20260825123456", (byte)0])!;
Require(transferFrames.Length > 1, "250 bytes with bufferMax=100 must require multiple frames");
Require(transferFrames.All(f => f.Length <= 100), "every transport frame must obey bufferMax");
Require(transferFrames[0][0] == 0xAD, "first transport frame must be 0xAD");
Require(transferFrames[0][1] == 0x12 && transferFrames[0][2] == 0x34, "first transport frame address must be 0x1234");
Require(BinaryPrimitives.ReadUInt16BigEndian(transferFrames[0].AsSpan(3, 2)) == transferFrames[0].Length - 5, "0xAD payload length field mismatch");
Require(transferFrames[0][5] == 2 && transferFrames[0][6] == 2 && transferFrames[0][7] == 0 && transferFrames[0][8] == 3, "0xAD namespace must be REAL_TIME_PLAY/PLAY_START/option0/type3");
Require(BinaryPrimitives.ReadUInt32BigEndian(transferFrames[0].AsSpan(9, 4)) == transferData.Length, "0xAD total data length mismatch");
Require(transferFrames[0][13] == 1 && transferFrames[0][14] == 0, "0xAD layerCount/runArg mismatch");
Require(transferFrames[0].Length - 47 == 53, "first chunk capacity must be bufferMax-47");
Require(transferFrames.Skip(1).All(f => f[0] == 0xA5), "continuation frames must be 0xA5");
Require(transferFrames.Skip(1).All(f => BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(3, 2)) == f.Length - 5), "0xA5 payload length field mismatch");
Require((transferFrames[0].Length - 47) + transferFrames.Skip(1).Sum(f => f.Length - 5) == transferData.Length, "transport chunk payload bytes must reconstruct original data");

var parseAck = official.GetMethod("ParseDataAck", BindingFlags.Public | BindingFlags.Static, [typeof(byte[])]);
Require(parseAck is not null, "CubeOfficialProtocol.ParseDataAck is missing");
var ack = parseAck!.Invoke(null, [new byte[] { 0x85, 0x12, 0x34, 0x00 }])!;
var ackType = ack.GetType();
Require((byte)ackType.GetProperty("Command")!.GetValue(ack)! == 0x85, "ACK command mismatch");
Require((ushort)ackType.GetProperty("Address")!.GetValue(ack)! == 0x1234, "ACK address mismatch");
Require((byte)ackType.GetProperty("Status")!.GetValue(ack)! == 0, "ACK status mismatch");
Require((bool)ackType.GetProperty("IsSuccess")!.GetValue(ack)!, "ACK status 0 must be success");

Console.WriteLine("FORMAT3 CONTRACT PASS: palette=250, type3=6B/point, dry-run artifacts, v26 AD/A5/85 envelope");
