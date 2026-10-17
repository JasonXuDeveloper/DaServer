using System.Collections.Immutable;
using DaServer.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        [NinoType(format: NinoFormat.VersionTolerant)] public partial class Profile { public string Name; public int Level; }
        [NinoType(format: NinoFormat.VersionTolerant)] public sealed partial class Hero : Profile { public int Gold; }
        [NinoType(format: NinoFormat.Keyed)] public sealed partial class Settings { public int Volume; public string Theme; }
        public interface INamed { string Title { get; set; } }
        [NinoType] public sealed partial class Badge : INamed { public Badge(int id) => Id = id; public int Id { get; } public int Rank { get; } string INamed.Title { get; set; } }
        [NinoType] public partial class Shape { public int X; }
        [NinoType(trackReferences: true)] public sealed partial class Circle : Shape { public new int X; public int this[int i] => i; }
        """;

    private const string Recorded = """
        Game.Badge (sealed class, positional)
        - Id: int
        Game.Cell (memory(0, Pack=4))
        - Row: int
        - Column: int
        Game.Circle (sealed class, positional, tracked)
        - Shape.X: int
        - X: int
        Game.Hero (sealed class, version tolerant)
        - Name: string
        - Level: int
        - Gold: int
        Game.Hop (class, positional)
        - Next: Game.Link
        - Children: System.Collections.Generic.List<Game.Link>
        - Label: string utf8
        - Distance: int
        Game.IShape (abstract, positional)
        - Sides: int
        Game.Link (class, positional)
        - Next: Game.Link
        - Children: System.Collections.Generic.List<Game.Link>
        - Label: string utf8
        Game.Look (memory)
        = Game.View
        Game.Move (memory)
        - Hero: Game.HeroId memory {Value: long}
        - X: float
        - Y: float
        Game.Paint (struct, positional)
        - At: Game.Cell
        - Kind: Game.Kind(byte)
        - Color: string
        - Trail: Game.Cell[]
        Game.Profile (class, version tolerant)
        - Name: string
        - Level: int
        Game.Settings (sealed class, keyed)
        - Volume: int
        - Theme: string
        Game.Shape (class, positional)
        - X: int
        Game.Sparse (class, positional)
        - A: int
        - B: int
        Game.Spin (struct, positional)
        - Speed: int
        Game.Square (class, positional)
        - Sides: int
        - Size: int
        Game.Tile (struct, positional)
        - Sides: int
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

        // A raw struct (every field serialized and raw) as its memory; otherwise its header and format and Nino's members:
        // an interface's first, the primary constructor's properties first, [NinoIgnore] skipped, [NinoMember] order
        // without autoCollect, get-only properties only when a constructor takes them, no explicit interface
        // implementations or indexers, a hidden base member named with its type; enums with their underlying type; an
        // unmanaged struct without its own entry (an id) inline; replies.
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
    [TestCase(" private int _hidden;", "", "DAS017", TestName = "A struct that turns raw changes its bytes")]
    [TestCase("private byte _shade;", "private short _shade;", "", TestName = "A field Nino does not serialize is not part of the layout")]
    [TestCase("public int Size; public int Sides", "public int Sides { get; set; } public int Size", "", TestName = "An interface's members come first whatever the class's order")]
    [TestCase("namespace Game;", "namespace Game; [NinoType, Message] public partial record struct Jump(int H);", "DAS018", TestName = "A new type must be recorded")]
    [TestCase("[NinoUtf8] public string Label;", "public string Label;", "DAS017", TestName = "A string's encoding is part of the layout")]
    [TestCase("public int Distance;", "public int Distance; public int Hops;", "DAS017", TestName = "A type that inherits NinoType is recorded")]
    [TestCase("namespace Game;", "namespace Game; public partial class Jump : Link { }", "DAS017", TestName = "A new subtype of a recorded type breaks: older builds lack its type id")]
    [TestCase("public sealed partial class Badge", "public partial class Badge", "DAS017", TestName = "A sealed class that is no longer sealed breaks: a type id replaces its null flag")]
    [TestCase("trackReferences: true", "trackReferences: false", "DAS017", TestName = "Reference tracking is part of the encoding")]
    [TestCase("public int Rank { get; }", "public int Rank { get; set; }", "DAS017", TestName = "A get-only property that becomes settable is a new field")]
    [TestCase("public int Gold; }", "public int Gold; public int Gems; }", "DAS018", TestName = "A version-tolerant type takes fields at its end")]
    [TestCase("public int Gold; }", "public int Gems; public int Gold; }", "DAS017", TestName = "A version-tolerant type takes no field before its last")]
    [TestCase("public string Name; public int Level; }", "public string Name; public int Level; public int Age; }", "DAS017,DAS018",
        TestName = "A field appended to a version-tolerant base shifts its subtype's fields")]
    [TestCase("public int Volume; public string Theme;", "public string Theme; public int Volume;", "", TestName = "A keyed type's order is free")]
    [TestCase("public int Volume; public string Theme;", "public int Volume;", "DAS018", TestName = "A keyed type drops a key")]
    [TestCase("public int Volume; public string Theme;", "public long Volume; public string Theme;", "DAS017", TestName = "A key keeps its type")]
    [TestCase("format: NinoFormat.Keyed", "format: NinoFormat.VersionTolerant", "DAS017", TestName = "A type's format is part of its encoding")]
    public async Task ChangesAreJudged(string before, string after, string id) =>
        Assert.That(await Check(Types.Replace(before, after), Recorded), Is.EqualTo(id.Length == 0 ? [] : id.Split(',')));

    private const string Shared = """
        namespace Shared;
        [NinoType] public abstract partial class Event { public long At; }
        public struct Pos { public int X; private int _y; }
        [NinoType] public partial record struct Spot(byte Floor, long Cell);
        [NinoType] public partial record Card(string Name, int Level) { public string Guild { get; private set; } = ""; }
        """;

    private const string UsesShared = """
        namespace Game;
        [NinoType, Message] public partial struct Found { public Shared.Pos Where; public Shared.Card Card; }
        [NinoType, Message] public partial struct Seen { public Shared.Spot At; }
        """;

    [Test]
    public async Task AnotherAssemblysTypesReadTheSameFromSourceAndMetadata()
    {
        // The IDE references a project as source, the command line as metadata: the manifest must not differ.
        var shared = CSharpCompilation.Create("Shared", [GeneratorTests.Parse(Shared)], GeneratorTests.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.That(shared.Emit(image).Success);
        string fromSource = ManifestOf(GeneratorTests.Compile(GeneratorTests.Parse(UsesShared)).AddReferences(shared.ToMetadataReference()));
        string fromMetadata = ManifestOf(GeneratorTests.Compile(GeneratorTests.Parse(UsesShared)).AddReferences(MetadataReference.CreateFromImage(image.ToArray())));
        Assert.That(fromSource, Is.EqualTo(fromMetadata));
        Assert.That(fromSource, Does.Contain("- Where: Shared.Pos memory {X: int, _y: int}").And.Contain("- Card: Shared.Card\n"));
        // Another assembly's [NinoType] struct with auto-properties is its memory, from source and from metadata alike.
        Assert.That(fromSource, Does.Contain("Game.Seen (memory)\n- At: Shared.Spot memory {Floor: byte, Cell: long}"));

        // A new subtype of another assembly's [NinoType] base: older builds of this one cannot read its type id.
        var subtype = GeneratorTests.Compile(GeneratorTests.Parse(UsesShared + "public partial class Quest : Shared.Event { }"))
            .AddReferences(shared.ToMetadataReference());
        var reported = await subtype.WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions([new Manifest(fromSource)])).GetAnalyzerDiagnosticsAsync();
        Assert.That(reported.Select(d => d.Id).Distinct(), Is.EqualTo(new[] { "DAS017" }));
    }

    private static string ManifestOf(Compilation compilation)
    {
        var driver = CSharpGeneratorDriver.Create([new WireManifestGenerator().AsSourceGenerator()], optionsProvider: new Updating())
            .RunGenerators(compilation);
        return driver.GetRunResult().GeneratedTrees.Single().ToString().Replace("\r", "");
    }

    private static async Task<string[]> Check(string source, string? manifest)
    {
        var compilation = GeneratorTests.Compile(GeneratorTests.Parse(source));
        var files = manifest == null ? ImmutableArray<AdditionalText>.Empty : [new Manifest(manifest)];
        var reported = await compilation.WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions(files)).GetAnalyzerDiagnosticsAsync();
        return reported.Select(d => d.Id).Where(id => id is "DAS017" or "DAS018").Distinct().Order().ToArray();
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
    public void ATruncatedPayloadIsRefusedNotReadPastItsEnd()
    {
        // Nino 4 checks every read against the span: a truncated or hostile payload throws instead.
        var full = NinoSerializer.Serialize(new OrderV2 { Item = 7, Count = 2, Gift = true });
        Assert.Throws<InvalidOperationException>(() => NinoDeserializer.Deserialize<OrderV2>(full.AsSpan(0, 8)));
        Assert.That(NinoDeserializer.Deserialize<OrderV2>(full), Is.EqualTo(new OrderV2 { Item = 7, Count = 2, Gift = true }));
    }

    [Test]
    public void ReadingAPayloadAllocatesNothing()
    {
        var payload = NinoSerializer.Serialize(new OrderV2 { Item = 7, Count = 2 });
        NinoDeserializer.Deserialize<OrderV2>(payload);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) NinoDeserializer.Deserialize<OrderV2>(payload);
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    [Test]
    public void NinoWritesWhatTheManifestRecords()
    {
        // As Game.View above: the primary constructor's property, then the body's field (its memory, Nino copies it).
        Assert.That(NinoSerializer.Serialize(new LayoutProbe(1) { Body = 2 }), Is.EqualTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }));
        // As Game.Spin: a private field is not serialized, so the struct is written member by member (not raw).
        Assert.That(NinoSerializer.Serialize(new PrivateProbe { Shown = 3 }), Is.EqualTo(new byte[] { 3, 0, 0, 0 }));
        // As Game.Badge: a null flag, then the get-only property a constructor takes (not the one none takes).
        Assert.That(NinoSerializer.Serialize(new CtorProbe(4)), Is.EqualTo(new byte[] { 1, 4, 0, 0, 0 }));
    }
}

[NinoType] public partial record struct OrderV2 { public int Item; public long Count; public bool Gift; }

[NinoType] public partial record struct LayoutProbe(int Head) { public int Body; }

[NinoType] public partial struct PrivateProbe { public int Shown; private int _hidden; public void Hide(int value) => _hidden = value; }

[NinoType] public sealed partial class CtorProbe(int id) { public int Id { get; } = id; public int Rank { get; } = 9; }
