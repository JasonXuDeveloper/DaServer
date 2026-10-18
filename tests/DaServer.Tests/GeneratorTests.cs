using System.Collections.Immutable;
using DaServer.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DaServer.Tests;

/// <summary>
/// Drives the DaServer generators and analyzer through Roslyn: valid input compiles cleanly, invalid input is reported
/// by the analyzer (never by the generators), and unrelated edits hit the incremental cache.
/// </summary>
public class GeneratorTests
{
    private const string Usings = """
        using System.Threading.Tasks;
        using DaServer;
        using DaServer.Runtime;
        using Nino.Core;

        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    internal static CSharpCompilation Compile(params SyntaxTree[] trees) => Compile(OutputKind.DynamicallyLinkedLibrary, trees);

    private static CSharpCompilation Compile(OutputKind kind, params SyntaxTree[] trees) => CSharpCompilation.Create("GeneratorTest", trees,
        References, new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>The framework plus every assembly next to the tests (DaServer, Hosting, Nino, EasyEcs...).</summary>
    internal static readonly MetadataReference[] References =
        ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator)
        .Concat(System.IO.Directory.GetFiles(System.AppContext.BaseDirectory, "*.dll"))
        .GroupBy(System.IO.Path.GetFileName).Select(g => g.First())
        .Where(path => !System.IO.Path.GetFileName(path).StartsWith("DaServer.Tests"))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();

    internal static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(Usings + source, ParseOptions);

    private static GeneratorDriver Driver() => CSharpGeneratorDriver.Create(
        [new AskGenerator().AsSourceGenerator(), new RegistrationGenerator().AsSourceGenerator(),
            new PolyfillGenerator().AsSourceGenerator()], parseOptions: ParseOptions,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    /// <summary>Generated sources and every error or DASxxx diagnostic (generator, compiler and analyzer).</summary>
    private static async Task<(string[] Sources, string[] Diagnostics)> Run(string source)
    {
        Driver().RunGeneratorsAndUpdateCompilation(Compile(Parse(source)), out var output, out var generatorDiagnostics);
        Assert.That(generatorDiagnostics, Is.Empty, "generators never report diagnostics");
        var all = await output.WithAnalyzers([new DaServerAnalyzer()]).GetAllDiagnosticsAsync();
        var reported = all.Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("DAS"))
            .Select(d => d.Id).Distinct().ToArray();
        return (output.SyntaxTrees.Skip(1).Select(t => t.ToString()).ToArray(), reported);
    }

    [Test]
    public void SharedAndClientCodeIsGeneratedAsCSharp9()
    {
        // Contracts and client code also build in Unity (C# 9, the Roslyn 4.3 build of the generators): what the
        // generators emit for an assembly without the server runtime must compile as C# 9.
        string[] server = ["DaServer.dll", "DaServer.Hosting.dll", "DaServer.Testing.dll", "DaServer.Simulation.dll",
            "DaServer.Persistence.PostgreSql.dll", "DaServer.Aspire.dll", "EasyEcs.dll"];
        var references = References.Where(r => !server.Contains(System.IO.Path.GetFileName(r.Display))).ToArray();
        var csharp9 = new CSharpParseOptions(LanguageVersion.CSharp9);
        var source = CSharpSyntaxTree.ParseText("""
            using DaServer;
            using Nino.Core;

            namespace Game
            {
                [NinoType, Message] public partial struct Price : IRequest<int> { public int Count; }
                [NinoType, Message, FromClient] public partial struct Move { public float X; }
                [NinoType, Message, ToClient] public partial struct Shown { public int N; }
                public sealed class Pushes { [Handle] public void On(Shown m) { } }
            }
            """, csharp9);
        var compilation = CSharpCompilation.Create("SharedTest", [source], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        CSharpGeneratorDriver.Create([new AskGenerator().AsSourceGenerator(), new RegistrationGenerator().AsSourceGenerator(),
                new PolyfillGenerator().AsSourceGenerator()], parseOptions: csharp9)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var generated = output.SyntaxTrees.Skip(1).ToArray();
        Assert.That(generated.Select(t => t.ToString()), Has.Some.Contains("PriceAsk").And.Some.Contains("RegisterHandlers"));
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task ValidMessagesAndHandlersCompile()
    {
        var (sources, diagnostics) = await Run("""
            namespace Game;
            [NinoType, Message] public partial record struct Price(string Item, int Count) : IRequest<int>;
            [NinoType, Message] public partial record struct Restock(string Item);
            [NinoType, Message, FromClient] public partial record struct Move(float X, float Y);
            [NinoType, Message, FromClient] public partial record struct Topic : IRequest<string?>;
            [NinoType, Message] public partial record struct Heal(int Hp) : IRequest<int>;
            [NinoType, Message] public partial record struct Opened;
            [NinoType, Message, ToClient] public partial record struct Shown;
            [NinoType, Message] public partial record struct Spawn : IRequest<HeroId>;
            [NinoType, Message] public partial record struct Plan(int Steps) : IRequest<int>;
            [NinoType, Message] public partial record struct Where : IRequest<Spot>;
            public struct Spot { public int X, Y; }
            public readonly record struct RoomId(long Value) : IActorId;
            public readonly record struct HeroId(long Value) : IActorId;
            public sealed class Shop : Service
            {
                [Handle] public int On(Price m) => m.Count;
                [Handle] public Reply On(Restock m) => default;
            }
            public sealed class Room : Actor<RoomId>
            {
                [Handle] public string? On(Topic _) => null;
                [Handle] public void On(Opened _) { }
            }
            public sealed class Hero : Actor<HeroId>
            {
                private const int MaxHp = 100;
                private static readonly int[] Levels = [1, 2, 3];
                [Handle] public void On(Move m) { }
                [Handle] public Reply<int> On(Heal m) => new(m.Hp);
                [Handle] public HeroId On(Spawn _) => Id; // ids cross the wire as they are
                // Awaiting comes back to the shard within the turn: no DAS015.
                [Handle] public async Reply<int> On(Plan m) { await Task.Yield(); await Task.CompletedTask.ConfigureAwait(true); return m.Steps; }
            }
            public sealed class Screen { [Handle] public void On(Shown _) { } }
            """);
        Assert.That(diagnostics, Is.Empty);
        string registration = sources.Single(s => s.Contains("RegisterHandlerType"));
        Assert.That(registration, Does.Contain("typeof(global::Game.Room), true").And.Contain("typeof(global::Game.Shop), false"));
        Assert.That(registration, Does.Contain("context.ReplyValue(Unsafe.As<global::Game.Shop>(target).On(m))"), "a reply returned synchronously");
        Assert.That(registration, Does.Contain("return context.Reply(Unsafe.As<global::Game.Hero>(target).On(m).AsValueTask())"), "a reply returned later");
        Assert.That(registration, Does.Contain("HandlerRegistry.RegisterHandlers(typeof(global::Game.Screen)"), "client handlerType");
        Assert.That(sources, Has.Some.Contains("Ask<TAddr>(this global::DaServer.IMessageSender sender, TAddr address, in global::Game.Heal request)"));
        Assert.That(sources, Has.Some.Contains("sender.Ask<global::Game.Topic, string?>(in request)"));
        Assert.That(sources, Has.Some.Contains("ClientMessages.Register<global::Game.Move>()")
            .And.Some.Contains("ClientMessages.Register<global::Game.Topic>()"));
    }

    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] void On(Tick e) { } }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void On(ref Tick e) { } }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public int On(Tick e) => 0; }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Ask : IRequest<int>; public sealed class Bad : Service { [Handle] public void On(Ask e) { } }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Ask : IRequest<int>; public sealed class Bad : Service { [Handle] public Reply<long> On(Ask e) => default; }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void A(Tick e) { } [Handle] public void B(Tick e) { } }", "DAS001")]
    [TestCase("public struct Tick { } public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("[NinoType] public partial class Tick { } public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("[Message, FromClient] public struct Bad { }", "DAS002")]
    [TestCase("[NinoType] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("public struct Bad : IRequest<int> { }", "DAS002")]
    [TestCase("public class Data { } [NinoType, Message] public partial record struct Bad : IRequest<Data>;", "DAS003")]
    [TestCase("[NinoType, Message] public partial record struct Bad : IRequest<System.Action>;", "DAS003")]
    [TestCase("[NinoType, Message] public partial record struct Bad : IRequest<object>;", "DAS003")]
    [TestCase("[NinoType, Message, NinoFormerName(\"X\")] public partial record struct A; [NinoType, Message, NinoFormerName(\"X\")] public partial record struct B;", "DAS004")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public sealed class Bad : Service { public Bad(int x) { } [Handle] public void On(Tick e) { } }", "DAS005")]
    [TestCase("[NinoType, Message, ToClient] public partial record struct Tick; public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS006")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public sealed class Ui { [Handle] public void On(Tick e) { } }", "DAS006")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public sealed class Bad : Service { [Handle] public void On(Tick e) => Cluster.Send(Caller, e); }", "DAS006")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public static class Bad { public static void Go(DaServer.Client.ClusterClient c) => c.Send(new Tick()); }", "DAS006")]
    [TestCase("[NinoType, Message, ToClient] public partial record struct Tick; public static class Bad { public static void Go(ICluster c) => c.Send(new Tick()); }", "DAS006")]
    [TestCase("[NinoType] public partial record struct Tick; public static class Bad { public static void Go(ICluster c) => c.Publish(new Tick()); }", "DAS002")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public sealed class Bad : Service { private int _count; [Handle] public void On(Tick e) => _count++; }", "DAS007")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; public sealed class Bad : Actor<BadId> { public static int Total; }", "DAS007")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; public sealed class Bad : Actor<BadId> { public string Name { get; set; } = \"\"; }", "DAS007")]
    [TestCase("[NinoType, Message, ToClient] public partial record struct Ping : IRequest<int>; [NinoType, Message] public partial record struct Tick; public sealed class Bad : Service { [Handle] public async Reply On(Tick e) => await Cluster.Ask(Caller, new Ping()); }", "DAS008")]
    [TestCase("[NinoType, Message, ToClient] public partial record struct Ping : IRequest<int>; public static class Bad { public static Reply<int> Go(ICluster c) => c.Ask(ClientGroup.All, new Ping()); }", "DAS008")]
    [TestCase("[NinoType, Message, FromClient, ToClient] public partial record struct Tick; public static class Bad { public static void Go(DaServer.Client.ClusterClient c) => c.Send(ClientGroup.All, new Tick()); }", "DAS008")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public async Reply<int> On(Q m) => await ReplyWhen(default, 1); }", "DAS011")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public Reply<int> On(Q m) { var r = ReplyWhen(default, 1); return r; } }", "DAS011")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public int On(Q m) => Cluster.Ask(Id, new Q()).AsValueTask().Result; }", "DAS009")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public int On(Q m) => Cluster.Ask(Id, new Q()).AsTask().GetAwaiter().GetResult(); }", "DAS009")]
    [TestCase("[NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Service { [Handle] public async Reply<int> On(Q m) { await Task.Delay(10); return 1; } }", "DAS009")]
    [TestCase("[NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Service { private static readonly object Gate = new(); [Handle] public int On(Q m) { lock (Gate) return 1; } }", "DAS009")]
    [TestCase("public partial class Bad : EasyEcs.EcsSystem { protected override void OnUpdate() => System.Threading.Thread.Sleep(1); }", "DAS009")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public partial class Bad : EasyEcs.EcsSystem { protected override void OnUpdate() => _ = World.Cluster.Ask(new BadId(1), new Q()); }", "DAS012")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public async Reply<int> On(Q m) { await Cluster.Ask(Id, new Q()).AsValueTask().ConfigureAwait(false); return 1; } }", "DAS015")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public async Reply<int> On(Q m) { await Task.Run(() => 1); return 1; } }", "DAS015")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public int On(Q m) { System.Threading.ThreadPool.QueueUserWorkItem(_ => { }); return 1; } }", "DAS015")]
    [TestCase("public partial class Bad : EasyEcs.EcsSystem { protected override void OnUpdate() => System.Threading.Tasks.Parallel.For(0, 4, static i => { }); }", "DAS015")]
    [TestCase("public partial class Bad : EasyEcs.EcsSystem { private int _ticks; protected override void OnUpdate() => _ticks++; }", "DAS007")]
    [TestCase("public record struct Hp(int Value) : EasyEcs.IComponent;", "DAS013")]
    [TestCase("[Persist] public partial class Bad { }", "DAS014")]
    [TestCase("[Handoff] public partial class Bad { }", "DAS014")]
    [TestCase("public partial struct Bad : IPlaced { public long Placement => 0; }", "DAS016")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message, FromClient] public partial record struct Q; public sealed class Bad : Actor<BadId> { [Handle] public void On(Q m) => Cluster.Send(Caller, new Disconnect()); }", "DAS020")]
    [TestCase("[NinoType, Message, ToClient] public partial record struct Bad(int[] Cells);", "DAS019")]
    [TestCase("[NinoType, Message, FromClient] public partial struct Bad { public System.Collections.Generic.List<int> Picks; }", "DAS019")]
    public async Task MisuseIsReportedByTheAnalyzer(string source, string id)
    {
        var (_, diagnostics) = await Run(source);
        Assert.That(diagnostics.Where(d => d.StartsWith("DAS")), Is.EqualTo(new[] { id }));
    }

    [Test]
    public async Task SharedLogicReferencesNoServerOrTransport()
    {
        // A <DaServerShared> assembly runs on servers and clients: referencing the runtime or the transport is DAS010.
        var shared = new SharedOption();
        var bad = Compile(Parse("public sealed class Rules { }"));
        var reported = await bad.WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions([], shared)).GetAnalyzerDiagnosticsAsync();
        Assert.That(reported.Select(d => d.Id).Distinct(), Is.EqualTo(new[] { "DAS010" }));

        var good = CSharpCompilation.Create("Rules", [Parse("public sealed class Rules { }")],
            References.Where(r => r.Display is not { } path || !ServerAssembly(System.IO.Path.GetFileNameWithoutExtension(path))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        reported = await good.WithAnalyzers([new DaServerAnalyzer()], new AnalyzerOptions([], shared)).GetAnalyzerDiagnosticsAsync();
        Assert.That(reported.Where(d => d.Id == "DAS010"), Is.Empty);

        static bool ServerAssembly(string name) => name is "DaServer" or "DaServer.Hosting" or "DaServer.Core" or "DaServer.Client";
    }

    [Test]
    public async Task PersistedComponentsMustToleratePastVersions()
    {
        // In a components project (no server runtime referenced): a positional [Persist] component is DAS014.
        var references = References.Where(r => r.Display is not { } path || System.IO.Path.GetFileNameWithoutExtension(path) != "DaServer");
        async Task<string[]> Ids(string source) => (await CSharpCompilation.Create("Components", [Parse(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithAnalyzers([new DaServerAnalyzer()]).GetAnalyzerDiagnosticsAsync()).Select(d => d.Id).Distinct().ToArray();

        Assert.That(await Ids("[NinoType, Persist] public partial record struct Gold(int N) : EasyEcs.IComponent;"), Is.EqualTo(new[] { "DAS014" }));
        Assert.That(await Ids("[NinoType(format: NinoFormat.VersionTolerant), Persist] public partial record struct Gold(int N) : EasyEcs.IComponent;"), Is.Empty);
        Assert.That(await Ids("[NinoType(format: NinoFormat.Keyed), Handoff] public partial record struct Gold(int N) : EasyEcs.IComponent;"), Is.Empty);
    }

    [Test]
    public void NodesRunTheSystemsOfSharedLogicTheyReference()
    {
        var shared = CSharpCompilation.Create("Rules", [Parse("""
            [assembly: DaServerShared]
            namespace Rules;
            public sealed class Glide : EasyEcs.EcsSystem { }
            internal sealed class Hidden : EasyEcs.EcsSystem { }
            """)], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var node = Compile(Parse("[NinoType, Message] public partial record struct Tick; public sealed class Clock : Service { [Handle] public void On(Tick _) { } }"))
            .AddReferences(shared.ToMetadataReference());

        Driver().RunGeneratorsAndUpdateCompilation(node, out var generated, out _);
        string registration = generated.SyntaxTrees.Single(t => t.FilePath.EndsWith("DaServer.Registration.g.cs")).ToString();
        Assert.That(registration, Does.Contain("global::Rules.Glide"));
        Assert.That(registration, Does.Not.Contain("Hidden"), "only public systems");
    }

    /// <summary>Build options of a project with <c>&lt;DaServerShared&gt;true&lt;/DaServerShared&gt;</c>.</summary>
    private sealed class SharedOption : AnalyzerConfigOptionsProvider
    {
        private sealed class Options(bool shared) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = "true";
                return shared && key == "build_property.DaServerShared";
            }
        }

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(true);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(false);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(false);
    }

    [Test]
    public void UnrelatedEditsHitTheCache()
    {
        var messages = Parse("[NinoType, Message, FromClient] public partial record struct Price(int N) : IRequest<int>; public sealed class Shop : Service { [Handle] public int On(Price m) => m.N; }");
        var other = Parse("public class Unrelated { }");
        var compilation = Compile(messages, other);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(other, Parse("public class Unrelated { int x; }")));

        foreach (var result in driver.GetRunResult().Results)
        foreach (var (name, steps) in result.TrackedSteps)
        {
            if (name is not ("Requests" or "ClientMessages" or "HostedTypes" or "UserEntryPoint" or "WireManifest")) continue;
            Assert.That(steps.SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
        }
    }

    [Test]
    public void AnalyzerTypeIdsAreNinos()
    {
        // The analyzer reports collisions with its own reading of the symbols; the runtime registers with Nino's ids.
        var compilation = Compile().AddReferences(MetadataReference.CreateFromFile(typeof(GeneratorTests).Assembly.Location));
        uint Of(System.Type type) => DaServer.Generator.Shapes.TypeIdOf(compilation.GetTypeByMetadataName(type.FullName!)!);

        Assert.That(Of(typeof(Add)), Is.EqualTo(TypeId<Add>.Value), "namespaced");
        Assert.That(Of(typeof(Nested)), Is.EqualTo(TypeId<Nested>.Value), "nested");
        Assert.That(Of(typeof(Nested.Inner)), Is.EqualTo(TypeId<Nested.Inner>.Value), "nested twice");
        Assert.That(Of(typeof(Former)), Is.EqualTo(TypeId<Former>.Value), "[NinoFormerName]");
    }

    public struct Nested
    {
        public struct Inner;
    }

    [Nino.Core.NinoFormerName("Game.Before.Former")]
    public struct Former;
}
