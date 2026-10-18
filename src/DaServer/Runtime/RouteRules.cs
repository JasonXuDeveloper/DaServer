namespace DaServer.Runtime;

/// <summary>
/// Route completeness (N4) over the routes of a cluster's node projects, as lines (<see cref="Routes"/>):
/// <c>M\t{message}\t{flags}</c> for a message a project's contracts declare (flags: <c>F</c> from clients, <c>T</c> to
/// clients, <c>R</c> a request) and <c>H\t{message}\t{handler}\t{A|S}</c> for an actor's or a service's handler. Plain
/// strings, so the Aspire pipeline step (DaServer.Aspire, which compiles this file too) judges what node projects listed
/// in check mode without loading them.
/// </summary>
internal static class RouteRules
{
    /// <summary>
    /// Messages no actor or service handles (<c>[ToClient]</c>-only messages excepted: clients handle them), and requests
    /// two or more service types handle (refused at run time as ambiguous). Empty when every message has its handler.
    /// </summary>
    public static List<string> Problems(IEnumerable<string> routes)
    {
        var declared = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var handlers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var services = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in routes)
        {
            var parts = line.Split('\t');
            if (parts is ["M", var message, var flags]) declared[message] = flags;
            else if (parts is ["H", var handled, var handler, var kind])
            {
                Add(handlers, handled, handler);
                if (kind == "S") Add(services, handled, handler);
            }
        }

        var problems = new List<string>();
        foreach (var (message, flags) in declared)
        {
            bool fromClient = flags.Contains('F');
            if (flags.Contains('T') && !fromClient) continue; // clients handle it
            if (!handlers.ContainsKey(message))
                problems.Add($"{message}: no actor or service of these node projects handles it" +
                             (fromClient ? " (a [FromClient] message: clients' sends are refused)" : ""));
            else if (flags.Contains('R') && services.TryGetValue(message, out var answering) && answering.Count > 1)
                problems.Add($"{message}: a request answered by {answering.Count} service types ({string.Join(", ", answering)}): it is refused as ambiguous");
        }

        return problems;
    }

    private static void Add(Dictionary<string, List<string>> map, string message, string handler)
    {
        if (!map.TryGetValue(message, out var list)) map[message] = list = [];
        if (!list.Contains(handler)) list.Add(handler);
    }
}
