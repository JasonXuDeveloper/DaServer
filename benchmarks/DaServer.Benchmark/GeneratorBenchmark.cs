using BenchmarkDotNet.Attributes;
using DaServer.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DaServer.Benchmark;

/// <summary>
/// What the generators and the analyzer cost the IDE and the build on a large business assembly: 1,000 messages (half
/// of them requests), 200 actors and 50 services with 5 handlers each, 5,000 call sites, among 500 unrelated classes.
/// Generate: a full run (a build). Edit: one more run after an edit unrelated to DaServer (a keystroke in the IDE),
/// which must hit the incremental cache. Analyze: the analyzer over the whole compilation.
/// </summary>
[MemoryDiagnoser]
public class GeneratorBenchmark
{
    private const int Messages = 1000, Actors = 200, Services = 50, HandlersPerType = 5, CallSites = 5000, Unrelated = 500;

    private CSharpCompilation _compilation = null!;
    private CSharpCompilation _edited = null!;
    private GeneratorDriver _warm = null!;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    [GlobalSetup]
    public void Setup()
    {
        var unrelated = Parse(Classes("Unrelated", 0));
        _compilation = CSharpCompilation.Create("Business", [Parse(Contracts()), Parse(HandlerTypes()), Parse(Calls()), unrelated],
            References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        _edited = _compilation.ReplaceSyntaxTree(unrelated, Parse(Classes("Unrelated", 1)));
        _warm = Driver().RunGeneratorsAndUpdateCompilation(_compilation, out var generated, out _);
        var errors = generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException("The benchmark assembly does not compile: " + string.Join("; ", errors));
    }

    [Benchmark]
    public GeneratorDriver Generate() => Driver().RunGenerators(_compilation);

    [Benchmark]
    public GeneratorDriver Edit() => _warm.RunGenerators(_edited);

    [Benchmark]
    public int Analyze() =>
        _compilation.WithAnalyzers([new DaServerAnalyzer()]).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult().Length;

    /// <summary>The cost the compiler adds for any analyzer looking at the same things (the floor of <see cref="Analyze"/>).</summary>
    [Benchmark]
    public int AnalyzeNothing() =>
        _compilation.WithAnalyzers([new NothingAnalyzer()]).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult().Length;

    // A measuring stick run in process, never loaded by a compiler: the rules for shipped analyzers do not apply.
#pragma warning disable RS1041, RS1036, RS2008
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class NothingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Never = new("NOP001", "", "", "", DiagnosticSeverity.Hidden, true);

        public override System.Collections.Immutable.ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Never];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(static _ => { }, SymbolKind.NamedType);
            context.RegisterOperationAction(static _ => { }, Microsoft.CodeAnalysis.OperationKind.Invocation);
        }
    }
#pragma warning restore RS1041, RS1036, RS2008

    private static GeneratorDriver Driver() => CSharpGeneratorDriver.Create(
        [new AskGenerator().AsSourceGenerator(), new RegistrationGenerator().AsSourceGenerator(),
            new EntryPointGenerator().AsSourceGenerator(), new PolyfillGenerator().AsSourceGenerator()],
        parseOptions: ParseOptions);

    private static SyntaxTree Parse(string source) => CSharpSyntaxTree.ParseText(source, ParseOptions);

    private static string Contracts()
    {
        var sb = new System.Text.StringBuilder("using DaServer; using Nino.Core; namespace Game;\n");
        for (int i = 0; i < Actors; i++) sb.Append($"public readonly record struct A{i}Id(long Value) : IActorId;\n");
        for (int i = 0; i < Messages; i++)
            sb.Append(i % 2 == 0
                ? $"[NinoType, Message, FromClient] public partial record struct M{i}(int X) : IRequest<int>;\n"
                : $"[NinoType, Message] public partial record struct M{i}(int X);\n");
        return sb.ToString();
    }

    private static string HandlerTypes()
    {
        var sb = new System.Text.StringBuilder("using System.Threading.Tasks; using DaServer; using DaServer.Runtime; namespace Game;\n");
        int message = 0;
        for (int i = 0; i < Actors + Services; i++)
        {
            sb.Append(i < Actors ? $"public sealed class A{i} : Actor<A{i}Id> {{\n" : $"public sealed class S{i} : Service {{\n");
            for (int h = 0; h < HandlersPerType; h++, message = (message + 1) % Messages)
                sb.Append(message % 2 == 0
                    ? $"  [Handle] public int On(M{message} m) => m.X;\n"
                    : $"  [Handle] public void On(M{message} m) {{ }}\n");
            sb.Append("}\n");
        }

        return sb.ToString();
    }

    private static string Calls()
    {
        var sb = new System.Text.StringBuilder("using System.Threading.Tasks; using DaServer; using DaServer.Runtime; namespace Game;\n");
        sb.Append("public sealed class Traffic : Service {\n  [Handle] public async ValueTask On(M1 _) {\n");
        for (int i = 0; i < CallSites; i++)
        {
            int m = i % Messages;
            sb.Append(m % 2 == 0
                ? $"    await Cluster.Ask(new A{i % Actors}Id(1), new M{m}(1));\n"
                : $"    Cluster.Send(new A{i % Actors}Id(1), new M{m}(1));\n");
        }

        return sb.Append("  }\n}\n").ToString();
    }

    private static string Classes(string prefix, int edit)
    {
        var sb = new System.Text.StringBuilder("namespace Other;\n");
        for (int i = 0; i < Unrelated; i++)
            sb.Append($"public class {prefix}{i} {{ public int Value(int x) => x + {(i == 0 ? edit : 0)}; }}\n");
        return sb.ToString();
    }

    /// <summary>The framework plus every assembly next to the benchmark (DaServer, Nino, EasyEcs...).</summary>
    private static MetadataReference[] References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Concat(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        .GroupBy(Path.GetFileName).Select(g => g.First())
        .Where(path => !Path.GetFileName(path).StartsWith("DaServer.Benchmark"))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();
}
