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
    private static void CheckWireManifest(CompilationAnalysisContext context)
    {
        // While the manifest is rewritten (-p:UpdateWireManifest=true) only breaking changes stop the build; "force"
        // records them too (the developer accepts that deployed peers and stored records can no longer be read).
        context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.UpdateWireManifest", out var update);
        bool updating = string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(update, "force", StringComparison.OrdinalIgnoreCase)) return;
        AdditionalText? manifest = null;
        foreach (var file in context.Options.AdditionalFiles)
            if (Path.GetFileName(file.Path) == WireManifest.FileName) manifest = file;
        if (manifest == null) return;

        var text = manifest.GetText(context.CancellationToken);
        if (text == null) return;
        if (!text.ToString().Contains(WireManifest.Grammar))
        {
            // Recorded in an earlier layout text (another Nino): every member would read as changed. One report instead.
            var first = text.Lines[0];
            context.ReportDiagnostic(Diagnostic.Create(Rules.WireBreak, Location.Create(manifest.Path, first.Span, text.Lines.GetLinePositionSpan(first.Span)),
                WireManifest.FileName, "was recorded in an earlier layout text: check the Nino update's notes for changed bytes, then record it again"));
            return;
        }

        var baseline = WireManifest.Parse(text.ToString());
        var types = WireManifest.All(context.Compilation.Assembly, context.CancellationToken);
        var current = types.Select(t => t.Type).ToList();
        var locations = new Dictionary<string, Location?>(StringComparer.Ordinal);
        foreach (var (type, symbol) in types) locations[type.Name] = symbol.Locations.FirstOrDefault();
        foreach (var (name, what, breaking) in WireManifest.Compare(baseline, current))
        {
            if (!breaking && updating) continue;
            if (!locations.TryGetValue(name, out var location))
            {
                var line = text.Lines[baseline[name].Line];
                location = Location.Create(manifest.Path, line.Span, text.Lines.GetLinePositionSpan(line.Span));
            }

            context.ReportDiagnostic(Diagnostic.Create(breaking ? Rules.WireBreak : Rules.WireUnrecorded, location, name, what));
        }
    }
}
