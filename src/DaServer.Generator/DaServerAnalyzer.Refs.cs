using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DaServer.Generator;

/// <summary>
/// DAS022: a handler's <c>ref</c> / <c>in</c> component parameters point into the actor's row. A structural change of
/// the actor's entity (adding or removing a component, tags included: <c>self.Set(..)</c>, <c>self.SetSaveNow()</c>,
/// <c>self.Remove&lt;T&gt;()</c>) moves the entity to another table, so a use of such a parameter after it reads or
/// writes the old row: the write is lost without an error. Finish with the components first, or read one again
/// (<c>self.Get&lt;T&gt;()</c>) after the change.
/// </summary>
public sealed partial class DaServerAnalyzer
{
    private static readonly string[] StructuralPrefixes = ["Set", "Remove", "GetOrAdd", "Add", "Destroy", "Enable", "Disable"];

    private static void CheckStaleRefs(OperationAnalysisContext context)
    {
        if (context.ContainingSymbol is not IMethodSymbol { Parameters.Length: > 1 } method || !Support.HasAttribute(method, Support.HandleAttribute) ||
            method.ContainingType is not { } type || !ServerHandlers.IsHandlerType(type))
            return;

        List<IParameterSymbol>? components = null, entities = null;
        for (int i = 1; i < method.Parameters.Length; i++) // the first is the message
        {
            var p = method.Parameters[i];
            if (p.RefKind is RefKind.Ref or RefKind.In && p.Type.IsValueType && Support.Implements(p.Type, "EasyEcs.IComponent"))
                (components ??= []).Add(p);
            else if (p.RefKind == RefKind.None && Support.Is(p.Type, "EasyEcs.Entity")) (entities ??= []).Add(p);
        }

        if (components == null || entities == null) return;
        List<IInvocationOperation>? changes = null;
        foreach (var operation in context.Operation.Descendants())
            if (operation is IInvocationOperation call && IsStructural(call, entities)) (changes ??= []).Add(call);
        if (changes == null) return;

        var reported = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        foreach (var operation in context.Operation.Descendants())
        {
            if (operation is not IParameterReferenceOperation reference || !components.Contains(reference.Parameter, SymbolEqualityComparer.Default) ||
                reported.Contains(reference.Parameter))
                continue;
            foreach (var change in changes)
                if (RunsAfter(change.Syntax, operation.Syntax))
                {
                    reported.Add(reference.Parameter);
                    context.ReportDiagnostic(Diagnostic.Create(Rules.StaleComponentRef, operation.Syntax.GetLocation(), reference.Parameter.Name,
                        change.Syntax.ToString()));
                    break;
                }
        }
    }

    /// <summary>
    /// Whether <paramref name="use"/> may run after <paramref name="change"/>: it comes later in the method, and no block
    /// around the change but not around the use leaves the method at its end (an early return or throw).
    /// </summary>
    private static bool RunsAfter(SyntaxNode change, SyntaxNode use)
    {
        if (use.SpanStart <= change.SpanStart) return false;
        for (var node = change.Parent; node != null && !node.Span.Contains(use.Span); node = node.Parent)
            if (node is Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax { Statements: { Count: > 0 } statements } &&
                statements[statements.Count - 1] is Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.ThrowStatementSyntax)
                return false;
        return true;
    }

    /// <summary>A call that adds or removes a component of one of the handler's <c>Entity</c> parameters (or destroys it).</summary>
    private static bool IsStructural(IInvocationOperation call, List<IParameterSymbol> entities)
    {
        var receiver = call.TargetMethod.IsExtensionMethod || call.Instance == null ? call.Arguments.FirstOrDefault()?.Value : call.Instance;
        if (receiver is IConversionOperation conversion) receiver = conversion.Operand;
        if (receiver is not IParameterReferenceOperation p || !entities.Contains(p.Parameter, SymbolEqualityComparer.Default)) return false;
        foreach (var prefix in StructuralPrefixes)
            if (call.TargetMethod.Name.StartsWith(prefix, System.StringComparison.Ordinal)) return true;
        return false;
    }
}
