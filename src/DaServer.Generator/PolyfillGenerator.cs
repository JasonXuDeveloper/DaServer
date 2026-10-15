using System.Text;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// Contract assemblies built for netstandard2.1 (Unity clients) lack <c>IsExternalInit</c>, which the init accessors of
/// message records need: declared here for compilations referencing DaServer that do not have it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PolyfillGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var needsIsExternalInit = context.CompilationProvider
            .Select(static (c, _) => c.GetTypeByMetadataName("DaServer.Rpc.IMessageSender") != null &&
                                     c.GetTypeByMetadataName("System.Runtime.CompilerServices.IsExternalInit") == null)
            .WithTrackingName("IsExternalInit");
        context.RegisterSourceOutput(needsIsExternalInit, static (spc, needed) =>
        {
            if (needed) spc.AddSource("DaServer.IsExternalInit.g.cs", SourceText.From(IsExternalInit, Encoding.UTF8));
        });
    }

    private const string IsExternalInit = Header + """
        namespace System.Runtime.CompilerServices
        {
            /// <summary>Lets records declare init accessors (declared for targets without it, generated).</summary>
            internal static class IsExternalInit
            {
            }
        }

        """;
}
