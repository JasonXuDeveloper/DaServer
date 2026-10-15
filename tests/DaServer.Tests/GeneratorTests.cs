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
        using DaServer.Rpc;
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
        [new MessageGenerator().AsSourceGenerator(), new HostGenerator().AsSourceGenerator()], parseOptions: ParseOptions,
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
            [NinoType, Message(Flow.FromClient)] public partial record struct Move(float X, float Y);
            [NinoType, Message(Flow.FromClient, AnyKey = true)] public partial record struct Topic : IRequest<string?>;
            [NinoType, Message] public partial record struct Heal(int Hp) : IRequest<int>;
            [NinoType, Message] public partial record struct Opened;
            [NinoType, Message(Flow.ToClient)] public partial record struct Shown;
            public sealed class Shop : Service
            {
                [Handle] public int On(Price m) => m.Count;
                [Handle] public ValueTask On(Restock m) => default;
            }
            public sealed class Room : Actor
            {
                [Handle] public string? On(Topic _) => null;
                [Handle] public void On(Opened _) { }
            }
            public sealed class Hero : Actor
            {
                [Handle] public void On(Move m) { }
                [Handle] public ValueTask<int> On(Heal m) => new(m.Hp);
            }
            public sealed class Screen { [Handle] public void On(Shown _) { } }
            """);
        Assert.That(diagnostics, Is.Empty);
        string registration = sources.Single(s => s.Contains("RegisterHandlerType"));
        Assert.That(registration, Does.Contain("typeof(global::Game.Room), true").And.Contain("typeof(global::Game.Shop), false"));
        Assert.That(registration, Does.Contain("context.ReplyValue(Unsafe.As<global::Game.Shop>(target).On(m))"), "a reply returned synchronously");
        Assert.That(registration, Does.Contain("return context.Reply(Unsafe.As<global::Game.Hero>(target).On(m))"), "a reply returned later");
        Assert.That(registration, Does.Contain("MessageRegistry.RegisterHandlers(typeof(global::Game.Screen)"), "client handlerType");
        Assert.That(sources, Has.Some.Contains("Ask(this global::DaServer.Rpc.IMessageSender sender, long key, in global::Game.Heal request)"));
        Assert.That(sources, Has.Some.Contains("sender.Ask<global::Game.Topic, string?>(in request)"));
        Assert.That(sources, Has.Some.Contains("RegisterClientMessage<global::Game.Move>(anyKey: false)")
            .And.Some.Contains("RegisterClientMessage<global::Game.Topic>(anyKey: true)"));
    }

    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] void On(Tick e) { } }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void On(ref Tick e) { } }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public int On(Tick e) => 0; }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Ask : IRequest<int>; public sealed class Bad : Service { [Handle] public void On(Ask e) { } }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Ask : IRequest<int>; public sealed class Bad : Service { [Handle] public ValueTask<long> On(Ask e) => default; }", "DAS001")]
    [TestCase("[NinoType, Message] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void A(Tick e) { } [Handle] public void B(Tick e) { } }", "DAS001")]
    [TestCase("public struct Tick { } public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("[NinoType] public partial class Tick { } public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("[Message(Flow.FromClient)] public struct Bad { }", "DAS002")]
    [TestCase("[NinoType] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS002")]
    [TestCase("public struct Bad : IRequest<int> { }", "DAS002")]
    [TestCase("public struct Data { } [NinoType, Message] public partial record struct Bad : IRequest<Data>;", "DAS003")]
    [TestCase("[NinoType, Message, WireId(5)] public partial record struct A; [NinoType, Message, WireId(5)] public partial record struct B;", "DAS004")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public sealed class Bad : Service { public Bad(int x) { } [Handle] public void On(Tick e) { } }", "DAS005")]
    [TestCase("[NinoType, Message(Flow.ToClient)] public partial record struct Tick; public sealed class Bad : Service { [Handle] public void On(Tick e) { } }", "DAS006")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public sealed class Ui { [Handle] public void On(Tick e) { } }", "DAS006")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public sealed class Bad : Service { [Handle] public void On(Tick e) => Cluster.Push(CallerSession, e); }", "DAS006")]
    [TestCase("[NinoType, Message] public partial record struct Tick; public static class Bad { public static void Go(DaServer.Client.ClusterClient c) => c.Send(new Tick()); }", "DAS006")]
    [TestCase("[NinoType, Message(Flow.ToClient)] public partial record struct Tick; public static class Bad { public static void Go(ICluster c) => c.Send(1, new Tick()); }", "DAS006")]
    [TestCase("[NinoType] public partial record struct Tick; public static class Bad { public static void Go(ICluster c) => c.Publish(new Tick()); }", "DAS002")]
    public async Task MisuseIsReportedByTheAnalyzer(string source, string id)
    {
        var (_, diagnostics) = await Run(source);
        Assert.That(diagnostics.Where(d => d.StartsWith("DAS")), Is.EqualTo(new[] { id }));
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
        var messages = Parse("[NinoType, Message(Flow.FromClient)] public partial record struct Price(int N) : IRequest<int>; public sealed class Shop : Service { [Handle] public int On(Price m) => m.N; }");
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
