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
        [NinoType, Message] public partial record struct Move(float X, float Y);
        [NinoType] public partial struct Cell { public int Row; public int Column; }
        [NinoType, Message] public partial struct Paint { public Cell At; [NinoIgnore] public int Cached; public string Color; }
        [NinoType, Message] public partial record struct Look : IRequest<View>;
        [NinoType] public partial record struct View(int Range) { public int Zoom; }
        [NinoType(false)] public partial class Sparse { [NinoMember(2)] public int B; [NinoMember(1)] public int A; public int Skipped; }
        """;

    private const string Recorded = """
        Game.Cell frozen
        - Row: int
        - Column: int
        Game.Look grows
        Game.Move grows
        - X: float
        - Y: float
        Game.Paint grows
        - At: Game.Cell
        - Color: string
        Game.Sparse frozen
        - A: int
        - B: int
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

        // The primary constructor's properties first, [NinoIgnore] skipped, [NinoMember] order without autoCollect;
        // a nested type and a type nothing sends whole are frozen, a reply grows.
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
    public async Task ChangesAreJudged(string before, string after, string id) =>
        Assert.That(await Check(Types.Replace(before, after), Recorded), Is.EqualTo(new[] { id }));

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
        // WEAK_VERSION_TOLERANCE (DaServer.Abstractions.targets): the newer node reads an older message with the
        // appended field at its default, the older node ignores what it does not know.
        var older = NinoSerializer.Serialize(new OrderV1 { Item = 7, Note = "gift" });
        var newer = NinoSerializer.Serialize(new OrderV2 { Item = 7, Note = "gift", Count = 3 });
        Assert.That(NinoDeserializer.Deserialize<OrderV2>(older), Is.EqualTo(new OrderV2 { Item = 7, Note = "gift", Count = 0 }));
        Assert.That(NinoDeserializer.Deserialize<OrderV1>(newer), Is.EqualTo(new OrderV1 { Item = 7, Note = "gift" }));
    }

    [Test]
    public void NinoWritesTheOrderTheManifestRecords()
    {
        // As Game.View above: the primary constructor's property, then the body's field.
        Assert.That(NinoSerializer.Serialize(new LayoutProbe(1) { Body = 2 }), Is.EqualTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }));
    }
}

[NinoType] public partial record struct OrderV1 { public int Item; public string Note; }

[NinoType] public partial record struct OrderV2 { public int Item; public string Note; public int Count; }

[NinoType] public partial record struct LayoutProbe(int Head) { public int Body; }
