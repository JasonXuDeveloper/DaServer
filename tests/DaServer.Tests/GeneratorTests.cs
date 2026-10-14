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

    private static CSharpCompilation Compile(params SyntaxTree[] trees) => CSharpCompilation.Create("GeneratorTest", trees,
        System.AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

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
            public interface IShop : IService { ValueTask<int> Price(string item, int count); void Log(string s); ValueTask Clear(); }
            [ClientAccessible(AnyKey = true)]
            public interface IRoom : IActor { ValueTask<string?> Topic(); }
            public interface IRoomClient : IClientCallback { void OnTopic(string topic); }
            public sealed class Shop : Service, IShop
            {
                public ValueTask<int> Price(string item, int count) => new(count);
                public void Log(string s) { }
                public ValueTask Clear() => default;
            }
            [Role("game")]
            public sealed class Room : Actor, IRoom { public ValueTask<string?> Topic() => new((string?)null); }
            """);
        Assert.That(diagnostics, Is.Empty);
        Assert.That(sources.Single(s => s.Contains("RegisterImplementation")),
            Does.Contain("typeof(global::Game.Room)").And.Contain("\"game\""));
        Assert.That(sources, Has.Some.Contains("public readonly struct ShopProxy : global::Game.IShop"));
        Assert.That(sources, Has.Some.Contains("RoomClient(this IRpcInvoker cluster, ClientGroup group)"));
    }

    [TestCase("public interface IBad : IService { Task<int> A(); }", "DAS001")]
    [TestCase("public interface IBad : IService { ValueTask A(ref int x); }", "DAS002")]
    [TestCase("public interface IBad : IService { ValueTask A<T>(); }", "DAS003")]
    [TestCase("public interface IBad : IClientCallback { ValueTask A(); }", "DAS004")]
    [TestCase("public interface IBad : IService { [RpcId(5)] ValueTask A(); [RpcId(5)] ValueTask B(); }", "DAS005")]
    [TestCase("public interface IA : IActor { ValueTask A(); } public sealed class Bad : IA { public ValueTask A() => default; }", "DAS006")]
    [TestCase("public interface IS : IService { ValueTask A(); } public sealed class Bad : Service, IS { public Bad(int x) { } public ValueTask A() => default; }", "DAS007")]
    [TestCase("public interface IBad : IService { int P { get; } }", "DAS008")]
    public async Task MisuseIsReportedByTheAnalyzer(string source, string id)
    {
        var (_, diagnostics) = await Run(source);
        Assert.That(diagnostics.Where(d => d.StartsWith("DAS")), Is.EqualTo(new[] { id }));
    }

    [Test]
    public void UnrelatedEditsHitTheCache()
    {
        var contracts = Parse("public interface IShop : IService { ValueTask<int> Price(int n); } public sealed class Shop : Service, IShop { public ValueTask<int> Price(int n) => new(n); }");
        var other = Parse("public class Unrelated { }");
        var compilation = Compile(contracts, other);
        var driver = Driver().RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(other, Parse("public class Unrelated { int x; }")));

        foreach (var result in driver.GetRunResult().Results)
        foreach (var (name, steps) in result.TrackedSteps)
        {
            if (name is not ("Contracts" or "HostedTypes")) continue;
            Assert.That(steps.SelectMany(s => s.Outputs).Select(o => o.Reason),
                Is.All.AnyOf(IncrementalStepRunReason.Cached, IncrementalStepRunReason.Unchanged), name);
        }
    }
}
