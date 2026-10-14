using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DaServer.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

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
        [new ContractGenerator().AsSourceGenerator(), new HostGenerator().AsSourceGenerator()], parseOptions: ParseOptions,
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
    public async Task ValidContractsAndImplementationsCompile()
    {
        var (sources, diagnostics) = await Run("""
            namespace Game;
            [Service] public interface IShop { ValueTask<int> Price(string item, int count); void Log(string s); ValueTask Clear(); }
            [Actor, Client(AnyKey = true)] public interface IRoom { ValueTask<string?> Topic(); }
            [Actor] public interface IHero { [Client] void Move(float x, float y); ValueTask Heal(int hp); }
            [Nino.Core.NinoType] public readonly partial record struct Opened([property: Key] long Room);
            [Push] public interface IRoomClient { void OnTopic(string topic); }
            public sealed class Shop : Service, IShop
            {
                public ValueTask<int> Price(string item, int count) => new(count);
                public void Log(string s) { }
                public ValueTask Clear() => default;
            }
            public sealed class Room : Actor, IRoom
            {
                public ValueTask<string?> Topic() => new((string?)null);
                [Handle] public void On(Opened e) { }
            }
            public sealed class Hero : Actor, IHero { public void Move(float x, float y) { } public ValueTask Heal(int hp) => default; }
            """);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(sources.Single(s => s.Contains("RegisterImplementation")),
            Does.Contain("typeof(global::Game.Room)").And.Contain("EventHandlerInfo.Of<global::Game.Opened>(").And.Contain(".On(e)"));
        Assert.That(sources, Has.Some.Contains("long global::DaServer.Rpc.IKeyed.EventKey => Room;"), "generated event key");
        Assert.That(sources, Has.Some.Contains("ClientAccess.AnyKey"));
        Assert.That(sources, Has.Some.Contains("new RpcMethodInfo(MoveId, \"Move\", true, ClientAccess.SessionKey)"));
        Assert.That(sources, Has.Some.Contains("new RpcMethodInfo(HealId, \"Heal\", false, ClientAccess.None)"), "per-method access");
        Assert.That(sources, Has.Some.Contains("Hero(this DaServer.Client.ClusterClient client)"), "keyless client accessor");
        Assert.That(sources, Has.Some.Contains("public readonly struct ShopProxy : global::Game.IShop"));
        Assert.That(sources, Has.Some.Contains("RoomClient(this IRpcInvoker cluster, ClientGroup group)"));
    }

    [TestCase("[Service] public interface IBad { Task<int> A(); }", "DAS001")]
    [TestCase("[Service] public interface IBad { ValueTask A(ref int x); }", "DAS002")]
    [TestCase("[Service] public interface IBad { ValueTask A<T>(); }", "DAS003")]
    [TestCase("[Push] public interface IBad { ValueTask A(); }", "DAS004")]
    [TestCase("[Service] public interface IBad { [WireId(5)] ValueTask A(); [WireId(5)] ValueTask B(); }", "DAS005")]
    [TestCase("[Actor] public interface IA { ValueTask A(); } public sealed class Bad : IA { public ValueTask A() => default; }", "DAS006")]
    [TestCase("[Service] public interface IS { ValueTask A(); } public sealed class Bad : Service, IS { public Bad(int x) { } public ValueTask A() => default; }", "DAS007")]
    [TestCase("[Service] public interface IBad { int P { get; } }", "DAS008")]
    [TestCase("public struct Data { } [Service] public interface IBad { ValueTask A(Data d); }", "DAS009")]
    [TestCase("public struct Data { } [Service] public interface IBad { ValueTask<Data[]> A(); }", "DAS009")]
    [TestCase("public struct Happened { } public sealed class Bad : Service { [Handle] public void On(Happened e) { } }", "DAS009")]
    [TestCase("[Nino.Core.NinoType] public partial record struct Tick(int N); public sealed class Bad : Actor { [Handle] public void On(Tick e) { } }", "DAS010")]
    [TestCase("[Actor] public interface IA { } [Service] public interface IS { } public sealed class Bad : Actor, IA, IS { }", "DAS011")]
    [TestCase("[Service, Actor] public interface IBad { ValueTask A(); }", "DAS012")]
    [TestCase("[Push] public interface IBad { [Client] void A(); }", "DAS012")]
    [TestCase("[Service] public interface IBad { [Client(AnyKey = true)] ValueTask A(); }", "DAS012")]
    [TestCase("[Nino.Core.NinoType] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] public int On(Tick e) => 0; }", "DAS013")]
    [TestCase("[Nino.Core.NinoType] public partial record struct Tick(int N); public sealed class Bad : Service { [Handle] void On(Tick e) { } }", "DAS013")]
    [TestCase("[Nino.Core.NinoType] public record struct Tick([property: Key] string Id);", "DAS014")]
    [TestCase("[Nino.Core.NinoType] public record struct Tick([property: Key] long Id);", "DAS014")]
    public async Task MisuseIsReportedByTheAnalyzer(string source, string id)
    {
        var (_, diagnostics) = await Run(source);
        Assert.That(diagnostics.Where(d => d.StartsWith("DAS")), Is.EqualTo(new[] { id }));
    }

    [Test]
    public void NodeExecutablesGetAnEntryPointUnlessTheyWriteOne()
    {
        const string node = "[Service] public interface IShop { ValueTask<int> Price(int n); } public sealed class Shop : Service, IShop { public ValueTask<int> Price(int n) => new(n); }";
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
        var contracts = Parse("[Service] public interface IShop { ValueTask<int> Price(int n); } public sealed class Shop : Service, IShop { public ValueTask<int> Price(int n) => new(n); }");
        var other = Parse("public class Unrelated { }");
        var compilation = Compile(contracts, other);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(other, Parse("public class Unrelated { int x; }")));

        foreach (var result in driver.GetRunResult().Results)
        foreach (var (name, steps) in result.TrackedSteps)
        {
            if (name is not ("Contracts" or "HostedTypes" or "UserEntryPoint")) continue;
            Assert.That(steps.SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
        }
    }
}
