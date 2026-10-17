using DaServer.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// DaServer.WireTool <assembly> <references file> <wire.manifest>: write the manifest of the built assembly, read from
// its metadata with every reference it was compiled against (one path per line), as the analyzer checks it.
if (args.Length != 3)
{
    Console.Error.WriteLine("usage: DaServer.WireTool <assembly> <references file> <wire.manifest>");
    return 2;
}

var built = MetadataReference.CreateFromFile(args[0]);
var references = File.ReadAllLines(args[1]).Where(static line => line.Length > 0 && File.Exists(line))
    .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("WireManifest", references: [.. references, built]);
if (compilation.GetAssemblyOrModuleSymbol(built) is not IAssemblySymbol assembly)
{
    Console.Error.WriteLine($"Cannot read {args[0]}");
    return 1;
}

var types = WireManifest.All(assembly, CancellationToken.None);
if (types.Count == 0) return 0; // no wire types: no manifest
string text = WireManifest.Format(types.Select(static t => t.Type).ToList());
if (!File.Exists(args[2]) || File.ReadAllText(args[2]) != text) File.WriteAllText(args[2], text);
return 0;
