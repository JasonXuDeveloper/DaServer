using System.Collections.Immutable;
using DaServer.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Nino.Core;

namespace DaServer.Tests;

/// <summary>
/// The wire manifest (ADR-076): it records fields in Nino's order, the analyzer tells breaking changes (DAS017) from
/// compatible ones not recorded yet (DAS018), and nodes of two versions read each other's messages.
/// </summary>
public class WireManifestTests
{
    private const string Types = """
        namespace Game;
        public enum Kind : byte { Dot, Line }
        [NinoType, Message] public partial record struct Move(float X, float Y);
        [NinoType, Message] public partial struct Spin { public int Speed; private int _hidden; }
        [NinoType] public partial struct Cell { public int Row; public int Column; }
        [NinoType, Message] public partial struct Paint { public Cell At; [NinoIgnore] public int Cached; public Kind Kind; public string Color; }
        [NinoType, Message] public partial record struct Look : IRequest<View>;
        [NinoType] public partial record struct View(int Range) { public int Zoom; }
        [NinoType(false)] public partial class Sparse { [NinoMember(2)] public int B; [NinoMember(1)] public int A; public int Skipped; }
        [NinoType] public interface IShape { int Sides { get; set; } }
        [NinoType] public partial class Square : IShape { public int Size; public int Sides { get; set; } }
        """;

    private const string Recorded = """
        Game.Cell frozen
        - Row: int
        - Column: int
        Game.IShape frozen
        - Sides: int
        Game.Look grows
        Game.Move grows
        - X: float
        - Y: float
        Game.Paint frozen
        - At: Game.Cell
        - Kind: Game.Kind(byte)
        - Color: string
        Game.Sparse frozen
        - A: int
        - B: int
        Game.Spin grows
        - Speed: int
        - _hidden: int
        Game.Square frozen
        - Sides: int
        - Size: int
        Game.View grows
        - Range: int
        - Zoom: int
        """;

    [Test]
    public void TheManifestRecordsFieldsInNinoOrder()
    {
        var driver = Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(new WireManifestGenerator())
            .RunGenerators(GeneratorTests.Compile(GeneratorTests.Parse(Types)));
        string text = driver.GetRunResult().GeneratedTrees.Single(t => t.FilePath.EndsWith("DaServer.WireManifest.g.cs")).ToString();

        // A value-only struct as its memory (private fields too); otherwise Nino's members: an interface's first, the
        // primary constructor's properties first, [NinoIgnore] skipped, [NinoMember] order without autoCollect; enums
        // with their underlying type. Only whole value-only types no other type nests grow (a reply too).
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
    [TestCase("public int Row; public int Column;", "public int Row; public int Column; public int Layer;", "DAS017", TestName = "A nested type cannot grow")]
    [TestCase("[NinoType, Message] public partial record struct Move(float X, float Y);", "", "DAS017", TestName = "A removed type breaks")]
    [TestCase("float X, float Y", "float X, float Y, float Z", "DAS018", TestName = "An appended field is compatible but must be recorded")]
    [TestCase("public int Zoom;", "public int Zoom; public int Tilt;", "DAS018", TestName = "A reply grows like a message")]
    [TestCase("namespace Game;", "namespace Game; [NinoType, Message] public partial record struct Jump(int H);", "DAS018", TestName = "A new type must be recorded")]
    [TestCase("public string Color;", "public string Color; public int Extra;", "DAS017", TestName = "A message with references cannot grow")]
    [TestCase("enum Kind : byte", "enum Kind : int", "DAS017", TestName = "An enum's underlying type is part of the layout")]
    [TestCase(" private int _hidden;", "", "DAS017", TestName = "A value-only message's private fields are its bytes")]
    [TestCase("public int Size; public int Sides", "public int Sides { get; set; } public int Size", "", TestName = "An interface's members come first whatever the class's order")]
    public async Task ChangesAreJudged(string before, string after, string id) =>
        Assert.That(await Check(Types.Replace(before, after), Recorded), Is.EqualTo(id.Length == 0 ? [] : new[] { id }));

    private static async Task<string[]> Check(string source, string? manifest)
    {
        var compilation = GeneratorTests.Compile(GeneratorTests.Parse(source));
        var files = manifest == null ? ImmutableArray<AdditionalText>.Empty : [new Manifest(manifest)];
        var reported = await compilation.WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions(files)).GetAnalyzerDiagnosticsAsync();
        return reported.Select(d => d.Id).Where(id => id is "DAS017" or "DAS018").Distinct().ToArray();
    }

    private sealed class Manifest(string text) : AdditionalText
    {
        public override string Path => "/project/wire.manifest";
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    [Test]
    public void NodesOfTwoVersionsReadEachOthersMessages()
    {
        // A value-only message as its memory: the newer build reads an older, shorter payload with zeros for the
        // appended field (WirePayload), the older build reads the prefix it knows.
        var older = NinoSerializer.Serialize(new OrderV1 { Item = 7, Count = 2 });
        var newer = NinoSerializer.Serialize(new OrderV2 { Item = 7, Count = 2, Gift = true });
        WirePayload.Read(older, out OrderV2 upgraded);
        WirePayload.Read(newer, out OrderV1 downgraded);
        Assert.That(upgraded, Is.EqualTo(new OrderV2 { Item = 7, Count = 2, Gift = false }));
        Assert.That(downgraded, Is.EqualTo(new OrderV1 { Item = 7, Count = 2 }));
    }

    [Test]
    public void ReadingAnOlderPayloadAllocatesNothing()
    {
        var older = NinoSerializer.Serialize(new OrderV1 { Item = 7, Count = 2 });
        WirePayload.Read(older, out OrderV2 _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) WirePayload.Read(older, out OrderV2 _);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void NinoWritesTheOrderTheManifestRecords()
    {
        // As Game.View above: the primary constructor's property, then the body's field (its memory, Nino copies it).
        Assert.That(NinoSerializer.Serialize(new LayoutProbe(1) { Body = 2 }), Is.EqualTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }));
    }
}

[NinoType] public partial record struct OrderV1 { public int Item; public long Count; }

[NinoType] public partial record struct OrderV2 { public int Item; public long Count; public bool Gift; }

[NinoType] public partial record struct LayoutProbe(int Head) { public int Body; }
