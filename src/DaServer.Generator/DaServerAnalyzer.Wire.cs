using System.Collections.Concurrent;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DaServer.Generator;

/// <summary>
/// With a committed <c>wire.manifest</c> (an additional file), the assembly's [NinoType] types are checked against it
/// (ADR-076): what breaks deployed peers or stored records is an error (DAS017), what is compatible but not recorded
/// yet a warning (DAS018), so the manifest always describes the layout the next change is checked against.
/// </summary>
public sealed partial class DaServerAnalyzer
{
    private static void CheckWireManifest(CompilationStartAnalysisContext start)
    {
        // While the manifest is rewritten (-p:UpdateWireManifest=true) only breaking changes stop the build; "force"
        // records them too (the developer accepts that deployed peers and stored records can no longer be read).
        start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.UpdateWireManifest", out var update);
        bool updating = string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(update, "force", StringComparison.OrdinalIgnoreCase)) return;
        AdditionalText? manifest = null;
        foreach (var file in start.Options.AdditionalFiles)
            if (Path.GetFileName(file.Path) == WireManifest.FileName) manifest = file;
        if (manifest == null) return;

        var types = new ConcurrentBag<(WireType Type, Location? Location)>();
        start.RegisterSymbolAction(symbolContext =>
        {
            var type = (INamedTypeSymbol)symbolContext.Symbol;
            if (WireManifest.Read(type) is { } wire) types.Add((wire, type.Locations.FirstOrDefault()));
        }, SymbolKind.NamedType);

        start.RegisterCompilationEndAction(end =>
        {
            var text = manifest.GetText(end.CancellationToken);
            if (text == null) return;
            var baseline = WireManifest.Parse(text.ToString());
            var current = types.Select(t => t.Type).ToList();
            var locations = types.ToDictionary(t => t.Type.Name, t => t.Location, StringComparer.Ordinal);
            foreach (var (name, what, breaking) in WireManifest.Compare(baseline, current))
            {
                if (!breaking && updating) continue;
                if (!locations.TryGetValue(name, out var location))
                {
                    var line = text.Lines[baseline[name].Line];
                    location = Location.Create(manifest.Path, line.Span, text.Lines.GetLinePositionSpan(line.Span));
                }

                end.ReportDiagnostic(Diagnostic.Create(breaking ? Rules.WireBreak : Rules.WireUnrecorded, location, name, what));
            }
        });
    }
}
