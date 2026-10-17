using System.Collections.Immutable;
using DaServer.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Nino.Core;

namespace DaServer.Tests;

/// <summary>
/// The wire manifest (ADR-076): it records how Nino writes each type and the fields its bytes hold, the analyzer fails
/// the build on any change to a recorded layout (DAS017) and asks to record new types (DAS018), and a truncated
/// payload of a value-only type is read safely.
/// </summary>
public class WireManifestTests
{
    private const string Types = """
        namespace Game;
        public enum Kind : byte { Dot, Line }
        public readonly record struct HeroId(long Value) : IActorId;
        [NinoType, Message] public partial record struct Move(HeroId Hero, float X, float Y);
        [NinoType, Message] public partial struct Spin { public int Speed; private int _hidden; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
        [NinoType] public partial struct Cell { public int Row; public int Column; }
        [NinoType, Message] public partial struct Paint { public Cell At; [NinoIgnore] public int Cached; public Kind Kind; public string Color; public Cell[] Trail; }
        [NinoType, Message] public partial record struct Look : IRequest<View>;
        [NinoType] public partial record struct View(int Range) { public int Zoom; }
        [NinoType(false)] public partial class Sparse { [NinoMember(2)] public int B; [NinoMember(1)] public int A; public int Skipped; }
        [NinoType] public interface IShape { int Sides { get; set; } }
        [NinoType] public partial class Square : IShape { public int Size; public int Sides { get; set; } }
        [NinoType] public partial struct Tile : IShape { public int Sides { get; set; } private byte _shade; }
        [NinoType] public partial class Link { public Link Next; public System.Collections.Generic.List<Link> Children; [NinoUtf8] public string Label; }
        public partial class Hop : Link { public int Distance; }
        """;

    private const string Recorded = """
        Game.Cell (memory(0, Pack=4))
        - Row: int
        - Column: int
        Game.Hop (typed members)
        - Next: Game.Link
        - Children: System.Collections.Generic.List<Game.Link>
        - Label utf8: string
        - Distance: int
        Game.IShape (typed members)
        - Sides: int
        Game.Link (typed members)
        - Next: Game.Link
        - Children: System.Collections.Generic.List<Game.Link>
        - Label utf8: string
        Game.Look (memory)
        = Game.View
        Game.Move (memory)
        - Hero: Game.HeroId memory {Value: long}
        - X: float
        - Y: float
        Game.Paint (members)
        - At: Game.Cell
        - Kind: Game.Kind(byte)
        - Color: string
        - Trail: Game.Cell[]
        Game.Sparse (typed members)
        - A: int
        - B: int
        Game.Spin (memory)
        - Speed: int
        - _hidden: int
        Game.Square (typed members)
        - Sides: int
        - Size: int
        Game.Tile (typed memory)
        - Sides: int
        - _shade: byte
        Game.View (memory)
        - Range: int
        - Zoom: int
        """;

    [Test]
    public void TheManifestRecordsFieldsInNinoOrder()
    {
        var driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create([new WireManifestGenerator().AsSourceGenerator()],
                optionsProvider: new Updating())
            .RunGenerators(GeneratorTests.Compile(GeneratorTests.Parse(Types)));
        string text = driver.GetRunResult().GeneratedTrees.Single(t => t.FilePath.EndsWith("DaServer.WireManifest.g.cs")).ToString();

        // A value-only struct as its memory (private fields too); otherwise Nino's members: an interface's first, the
        // primary constructor's properties first, [NinoIgnore] skipped, [NinoMember] order without autoCollect; classes
        // behind a type id; enums with their underlying type; a type without its own entry (an id) inline; replies.
        text = text[text.IndexOf("Game.", StringComparison.Ordinal)..text.LastIndexOf("*/", StringComparison.Ordinal)].Replace("\r", "");
        Assert.That(text, Is.EqualTo(Recorded.Replace("\r", "") + "\n"));
    }

    [Test]
    public async Task AMatchingManifestIsQuiet() =>
        Assert.That(await Check(Types, Recorded), Is.Empty);

    [Test]
    public async Task WithoutAManifestNothingIsChecked() =>
        Assert.That(await Check(Types.Replace("float X, float Y", "float Y"), null), Is.Empty);

    [TestCase("float X, float Y", "float Y, float X", "DAS017", TestName = "Reordered fields break")]
    [TestCase("float X, float Y", "float X, int Y", "DAS017", TestName = "A retyped field breaks")]
    [TestCase("float X, float Y", "float X", "DAS017", TestName = "A removed field breaks")]
    [TestCase("float X, float Y", "float X, float Y, float Z", "DAS017", TestName = "An appended field breaks: layouts are frozen")]
    [TestCase("(HeroId Hero,", "(HeroId Hero,", "", TestName = "An unchanged message is quiet")]
    [TestCase("HeroId(long Value)", "HeroId(int Value)", "DAS017", TestName = "A nested id without an entry is part of the layout")]
    [TestCase("[NinoType, Message] public partial record struct Move(HeroId Hero, float X, float Y);", "", "DAS017", TestName = "A removed type breaks")]
    [TestCase("IRequest<View>", "IRequest<int>", "DAS017", TestName = "A changed reply breaks")]
    [TestCase("Pack = 4", "Pack = 1", "DAS017", TestName = "A struct layout change breaks")]
    [TestCase("public partial struct Spin", "public partial class Spin", "DAS017", TestName = "A struct that becomes a class breaks")]
    [TestCase("enum Kind : byte", "enum Kind : int", "DAS017", TestName = "An enum's underlying type is part of the layout")]
    [TestCase(" private int _hidden;", "", "DAS017", TestName = "A value-only message's private fields are its bytes")]
    [TestCase("public int Size; public int Sides", "public int Sides { get; set; } public int Size", "", TestName = "An interface's members come first whatever the class's order")]
    [TestCase("namespace Game;", "namespace Game; [NinoType, Message] public partial record struct Jump(int H);", "DAS018", TestName = "A new type must be recorded")]
    [TestCase("[NinoUtf8] public string Label;", "public string Label;", "DAS017", TestName = "A string's encoding is part of the layout")]
    [TestCase("private byte _shade;", "private short _shade;", "DAS017", TestName = "A typed value-only struct's private fields are its bytes")]
    [TestCase("public int Distance;", "public int Distance; public int Hops;", "DAS017", TestName = "A type that inherits NinoType is recorded")]
    [TestCase("namespace Game;", "namespace Game; public partial class Jump : Link { }", "DAS017", TestName = "A new subtype of a recorded type breaks: older builds lack its type id")]
    public async Task ChangesAreJudged(string before, string after, string id) =>
        Assert.That(await Check(Types.Replace(before, after), Recorded), Is.EqualTo(id.Length == 0 ? [] : new[] { id }));

    private static async Task<string[]> Check(string source, string? manifest)
    {
        var compilation = GeneratorTests.Compile(GeneratorTests.Parse(source));
        var files = manifest == null ? ImmutableArray<AdditionalText>.Empty : [new Manifest(manifest)];
        var reported = await compilation.WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions(files)).GetAnalyzerDiagnosticsAsync();
        return reported.Select(d => d.Id).Where(id => id is "DAS017" or "DAS018").Distinct().ToArray();
    }

    /// <summary>Build options of <c>-p:UpdateWireManifest=true</c>.</summary>
    private sealed class Updating : AnalyzerConfigOptionsProvider
    {
        private sealed class Options(bool global) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = "true";
                return global && key == "build_property.UpdateWireManifest";
            }
        }

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(true);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(false);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(false);
    }

    private sealed class Manifest(string text) : AdditionalText
    {
        public override string Path => "/project/wire.manifest";
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    [Test]
    public void ATruncatedValueOnlyPayloadIsRefusedBeforeItIsRead()
    {
        // Nino reads a value-only type as memory without a length check; WirePayload refuses a shorter payload
        // (truncated or hostile) instead of reading past it, and reads a full one as Nino does.
        var full = NinoSerializer.Serialize(new OrderV2 { Item = 7, Count = 2, Gift = true });
        Assert.Throws<InvalidOperationException>(() => WirePayload.Read(full.AsSpan(0, 8), out OrderV2 _));
        WirePayload.Read(full, out OrderV2 whole);
        Assert.That(whole, Is.EqualTo(new OrderV2 { Item = 7, Count = 2, Gift = true }));
        var typed = NinoSerializer.Serialize(new Marked { Value = 3 });
        Assert.That(typed, Has.Length.EqualTo(sizeof(int) + sizeof(long)), "a type id, then the memory");
        Assert.Throws<InvalidOperationException>(() => WirePayload.Read(typed.AsSpan(0, sizeof(long)), out Marked _));
        WirePayload.Read(typed, out Marked marked);
        Assert.That(marked.Value, Is.EqualTo(3));
        WirePayload.Read(NinoSerializer.Serialize<int?>(5), out int? optional);
        Assert.That(optional, Is.EqualTo(5), "a nullable reply is written as its memory too");
    }

    [Test]
    public void ReadingAPayloadAllocatesNothing()
    {
        var payload = NinoSerializer.Serialize(new OrderV2 { Item = 7, Count = 2 });
        WirePayload.Read(payload, out OrderV2 _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) WirePayload.Read(payload, out OrderV2 _);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void NinoWritesTheOrderTheManifestRecords()
    {
        // As Game.View above: the primary constructor's property, then the body's field (its memory, Nino copies it).
        Assert.That(NinoSerializer.Serialize(new LayoutProbe(1) { Body = 2 }), Is.EqualTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }));
    }
}

[NinoType] public partial record struct OrderV2 { public int Item; public long Count; public bool Gift; }

[NinoType] public partial record struct LayoutProbe(int Head) { public int Body; }

[NinoType] public interface IMarked;

[NinoType] public partial struct Marked : IMarked { public long Value; }
