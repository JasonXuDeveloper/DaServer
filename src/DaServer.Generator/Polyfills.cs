// Roslyn loads analyzers/generators into many hosts (VS, Rider, .NET Framework MSBuild), so this project must target
// netstandard2.0 regardless of the runtime's target framework. netstandard2.0 lacks IsExternalInit, which the C#
// compiler needs for the `record` models used here; do not delete this file.

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
