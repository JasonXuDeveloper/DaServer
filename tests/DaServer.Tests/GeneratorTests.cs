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

    private static CSharpCompilation Compile(params SyntaxTree[] trees) => Compile(OutputKind.DynamicallyLinkedLibrary, trees);

    private static CSharpCompilation Compile(OutputKind kind, params SyntaxTree[] trees) => CSharpCompilation.Create("GeneratorTest", trees,
        References, new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>The framework plus every assembly next to the tests (DaServer, Hosting, Nino, EasyEcs...).</summary>
    private static readonly MetadataReference[] References =
        ((string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator)
        .Concat(System.IO.Directory.GetFiles(System.AppContext.BaseDirectory, "*.dll"))
        .GroupBy(System.IO.Path.GetFileName).Select(g => g.First())
        .Where(path => !System.IO.Path.GetFileName(path).StartsWith("DaServer.Tests"))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();

    private static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(Usings + source, ParseOptions);

    private static GeneratorDriver Driver() => CSharpGeneratorDriver.Create(
        [new AskGenerator().AsSourceGenerator(), new RegistrationGenerator().AsSourceGenerator(), new EntryPointGenerator().AsSourceGenerator(),
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
            public readonly record struct RoomId(long Value) : IActorId;
            public readonly record struct HeroId(long Value) : IActorId;
            public sealed class Shop : Service
            {
                [Handle] public int On(Price m) => m.Count;
                [Handle] public ValueTask On(Restock m) => default;
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
                [Handle] public ValueTask<int> On(Heal m) => new(m.Hp);
                [Handle] public HeroId On(Spawn _) => Id; // ids cross the wire as they are
            }
            public sealed class Screen { [Handle] public void On(Shown _) { } }
            """);
        Assert.That(diagnostics, Is.Empty);
        string registration = sources.Single(s => s.Contains("RegisterHandlerType"));
        Assert.That(registration, Does.Contain("typeof(global::Game.Room), true").And.Contain("typeof(global::Game.Shop), false"));
        Assert.That(registration, Does.Contain("context.ReplyValue(Unsafe.As<global::Game.Shop>(target).On(m))"), "a reply returned synchronously");
        Assert.That(registration, Does.Contain("return context.Reply(Unsafe.As<global::Game.Hero>(target).On(m))"), "a reply returned later");
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
    [TestCase("[NinoType, Message] public partial record struct Ask : IRequest<int>; public sealed class Bad : Service { [Handle] public ValueTask<long> On(Ask e) => default; }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void A(Tick e) { } [Handle] public void B(Tick e) { } }", "DAS001")]
    [TestCase("public struct Tick { } public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("[NinoType] public partial class Tick { } public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("[Message, FromClient] public struct Bad { }", "DAS002")]
    [TestCase("[NinoType] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("public struct Bad : IRequest<int> { }", "DAS002")]
    [TestCase("public struct Data { } [NinoType, Message] public partial record struct Bad : IRequest<Data>;", "DAS003")]
    [TestCase("[NinoType, Message, WireId(5)] public partial record struct A; [NinoType, Message, WireId(5)] public partial record struct B;", "DAS004")]
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
    [TestCase("[NinoType, Message, ToClient] public partial record struct Ping : IRequest<int>; [NinoType, Message] public partial record struct Tick; public sealed class Bad : Service { [Handle] public async ValueTask On(Tick e) => await Cluster.Ask(Caller, new Ping()); }", "DAS008")]
    [TestCase("[NinoType, Message, ToClient] public partial record struct Ping : IRequest<int>; public static class Bad { public static ValueTask<int> Go(ICluster c) => c.Ask(ClientGroup.All, new Ping()); }", "DAS008")]
    [TestCase("[NinoType, Message, FromClient, ToClient] public partial record struct Tick; public static class Bad { public static void Go(DaServer.Client.ClusterClient c) => c.Send(ClientGroup.All, new Tick()); }", "DAS008")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public async ValueTask<int> On(Q m) => await ReplyWhen(default, 1); }", "DAS011")]
    [TestCase("public readonly record struct BadId(long Value) : IActorId; [NinoType, Message] public partial record struct Q : IRequest<int>; public sealed class Bad : Actor<BadId> { [Handle] public ValueTask<int> On(Q m) { var r = ReplyWhen(default, 1); return r; } }", "DAS011")]
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
    public void NodeExecutablesGetAnEntryPointUnlessTheyWriteOne()
    {
        const string node = "[NinoType, Message] public partial record struct Price(int N) : IRequest<int>; public sealed class Shop : Service { [Handle] public int On(Price m) => m.N; }";
        Driver().RunGeneratorsAndUpdateCompilation(Compile(OutputKind.ConsoleApplication, Parse(node)), out var generated, out _);
        Assert.That(generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        Assert.That(generated.GetEntryPoint(default)?.ContainingType.ToDisplayString(), Is.EqualTo("DaServer.Generated.NodeProgram"));

        var own = Parse(node + " public static class Program { public static void Main() { } }");
        Driver().RunGeneratorsAndUpdateCompilation(Compile(OutputKind.ConsoleApplication, own), out var custom, out _);
        Assert.That(custom.GetEntryPoint(default)?.ContainingType.Name, Is.EqualTo("Program"), "a written entry point wins");

        Driver().RunGeneratorsAndUpdateCompilation(Compile(Parse(node)), out var library, out _);
        Assert.That(library.SyntaxTrees.Any(t => t.FilePath.EndsWith("DaServer.Program.g.cs")), Is.False, "libraries get none");
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
            if (name is not ("Requests" or "ClientMessages" or "HostedTypes" or "UserEntryPoint")) continue;
            Assert.That(steps.SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
        }
    }
}
