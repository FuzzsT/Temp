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

Console.WriteLine("FORMAT3 CONTRACT PASS: Cube.zip palette=250, 6-byte type3 records, renderer repeat expansion, dry-run only");
