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

    private static readonly string Recorded = WireManifest.Grammar + "\n" + """
        Game.Badge (null flag, positional, id 0D604888)
        - Id: int raw(4)
        Game.Cell (no header, positional, raw, id 10406113)
        - Row: int@0 raw(4)
        - Column: int@4 raw(4)
        Game.Circle (null flag, positional, tracked, id AD28B007)
        - X: int raw(4)
        - X: int raw(4)
        Game.Hero (null flag, version tolerant, id FC84D6ED)
        - Name: string string
        - Level: int raw(4)
        - Gold: int raw(4)
        Game.Hop (type id, positional, id C4B40332)
        - Next: Game.Link type(Game.Link)
        - Children: System.Collections.Generic.List<Game.Link> seq(type(Game.Link))
        - Label: string utf8
        - Distance: int raw(4)
        Game.IShape (type id, positional, id BDFF01A3)
        Game.Kind (enum of byte)
        - Dot: 0
        - Line: 1
        Game.Link (type id, positional, id 309CABDD)
        - Next: Game.Link type(Game.Link)
        - Children: System.Collections.Generic.List<Game.Link> seq(type(Game.Link))
        - Label: string utf8
        Game.Look (no header, positional, raw, id 5EB8F11A)
        = Game.View
        Game.Move (no header, positional, raw, id EBFC19FC)
        - Hero: Game.HeroId@0 raw(8){Value:long@0:raw(8)}
        - X: float@8 raw(4)
        - Y: float@12 raw(4)
        Game.Paint (no header, positional, id D6BE6A7F)
        - At: Game.Cell raw(8){Row:int@0:raw(4),Column:int@4:raw(4)}
        - Kind: Game.Kind raw(1)
        - Color: string string
        - Trail: Game.Cell[] seq(raw(8){Row:int@0:raw(4),Column:int@4:raw(4)})
        Game.Profile (type id, version tolerant, id EB1B87E6)
        - Name: string string
        - Level: int raw(4)
        Game.Settings (null flag, keyed, id 8BE6A948)
        - Volume: int raw(4):key=0xCDF49275
        - Theme: string string:key=0x588A617C
        Game.Shape (type id, positional, id 0BB65D22)
        - X: int raw(4)
        Game.Sparse (type id, positional, id 69A64D13)
        - A: int raw(4)
        - B: int raw(4)
        Game.Spin (no header, positional, id E0B4AE79)
        - Speed: int raw(4)
        Game.Square (type id, positional, id E9B3DE04)
        - Sides: int raw(4)
        - Size: int raw(4)
        Game.Tile (no header, positional, id 4206B509)
        - Sides: int raw(4)
        Game.View (no header, positional, raw, id 37003A50)
        - Range: int@0 raw(4)
        - Zoom: int@4 raw(4)
        """;

    [Test]
    public void TheManifestRecordsFieldsInNinoOrder()
    {
        // Nino's own layouts: a raw struct (every field serialized and raw) and other types with their header, format
        // and type id; members in Nino's order (an interface's first, the primary constructor's properties first,
        // [NinoIgnore] skipped, [NinoMember] order without autoCollect, get-only properties only when a constructor
        // takes them, a hidden base member named with its type) and how each is written; plus what the manifest adds:
        // replies. Raw memory is Nino's to spell out: every field with its offset, nested structs included.
        string text = ManifestOf(GeneratorTests.Compile(GeneratorTests.Parse(Types)));
        Assert.That(text[text.IndexOf(WireManifest.Grammar, StringComparison.Ordinal)..], Is.EqualTo(Recorded.Replace("\r", "") + "\n"));
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
    [TestCase("public partial struct Spin", "public partial class Spin", "DAS017", TestName = "A struct that becomes a class breaks")]
    [TestCase("enum Kind : byte", "enum Kind : int", "DAS017", TestName = "An enum's underlying type is part of the layout")]
    [TestCase("{ Dot, Line }", "{ Line, Dot }", "DAS017", TestName = "A renumbered enum member breaks")]
    [TestCase("{ Dot, Line }", "{ Dot }", "DAS017", TestName = "A removed enum member breaks")]
    [TestCase("{ Dot, Line }", "{ Dot, Line, Arc }", "DAS018", TestName = "A new enum member must be recorded")]
    [TestCase(" private int _hidden;", "", "DAS017", TestName = "A struct that turns raw changes its bytes")]
    [TestCase("Pack = 4)", "Pack = 4, Size = 16)", "DAS017", TestName = "A raw struct's size is part of its layout")]
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
    [TestCase("public int Volume;", "[NinoFormerName(\"Volume\")] public int Loudness;", "", TestName = "A keyed member renamed with its former name keeps its key")]
    [TestCase("public int Volume;", "public int Loudness;", "DAS018", TestName = "A keyed member renamed without its former name is another key")]
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
        var shared = WithNino(CSharpCompilation.Create("Shared", [GeneratorTests.Parse(Shared)], GeneratorTests.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        using var image = new MemoryStream();
        Assert.That(shared.Emit(image).Success);
        string fromSource = ManifestOf(GeneratorTests.Compile(GeneratorTests.Parse(UsesShared)).AddReferences(shared.ToMetadataReference()));
        string fromMetadata = ManifestOf(GeneratorTests.Compile(GeneratorTests.Parse(UsesShared)).AddReferences(MetadataReference.CreateFromImage(image.ToArray())));
        Assert.That(fromSource, Is.EqualTo(fromMetadata));
        Assert.That(fromSource, Does.Contain("- Where: Shared.Pos raw(8){X:int@0:raw(4),_y:int@4:raw(4)}"), "a plain struct of another assembly: its fields");
        Assert.That(fromSource, Does.Contain("- At: Shared.Spot@0 raw(16){Floor:byte@0:raw(1),Cell:long@8:raw(8)} (no header, positional, raw, id F975C19A)"),
            "another assembly's [NinoType]: its encoding and id");

        // A new subtype of another assembly's [NinoType] base: older builds of this one cannot read its type id.
        var subtype = GeneratorTests.Compile(GeneratorTests.Parse(UsesShared + "public partial class Quest : Shared.Event { }"))
            .AddReferences(shared.ToMetadataReference());
        var reported = await WithNino(subtype).WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions([new Manifest(fromSource)])).GetAnalyzerDiagnosticsAsync();
        Assert.That(reported.Select(d => d.Id).Distinct(), Is.EqualTo(new[] { "DAS017" }));
    }

    /// <summary>Nino's generator (its NuGet package's), run first: its <c>[assembly: NinoTypeLayout]</c> is what the manifest reads.</summary>
    private static readonly ISourceGenerator Nino = ((IIncrementalGenerator)Activator.CreateInstance(
        System.Reflection.Assembly.LoadFrom(typeof(WireManifestTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "NinoGenerator").Value!)
            .GetType("Nino.Generator.Generation.NinoGenerator", throwOnError: true)!)!).AsSourceGenerator();

    private static Compilation WithNino(Compilation compilation)
    {
        CSharpGeneratorDriver.Create([Nino], parseOptions: (CSharpParseOptions)compilation.SyntaxTrees.First().Options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return output;
    }

    /// <summary>The manifest the build writes: the built assembly read as the analyzer reads it.</summary>
    private static string ManifestOf(Compilation compilation) =>
        WireManifest.Format(WireManifest.All(WithNino(compilation).Assembly, CancellationToken.None).Select(t => t.Type).ToList()).Replace("\r", "");

    private static async Task<string[]> Check(string source, string? manifest)
    {
        var compilation = WithNino(GeneratorTests.Compile(GeneratorTests.Parse(source)));
        var files = manifest == null ? ImmutableArray<AdditionalText>.Empty : [new Manifest(manifest)];
        var reported = await compilation.WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions(files)).GetAnalyzerDiagnosticsAsync();
        return reported.Select(d => d.Id).Where(id => id is "DAS017" or "DAS018").Distinct().Order().ToArray();
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
