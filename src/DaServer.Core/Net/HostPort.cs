namespace DaServer.Net;

/// <summary>"host:port" addresses (seeds, client entry points): a name, an IPv4 address, or an IPv6 one in brackets.</summary>
internal static class HostPort
{
    public static bool TryParse(string text, out string host, out int port)
    {
        int colon = text.LastIndexOf(':');
        host = colon > 0 ? text.Substring(0, colon) : text;
        if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']') host = host.Substring(1, host.Length - 2);
        return int.TryParse(colon > 0 ? text.Substring(colon + 1) : "", out port) && port is > 0 and <= 65535 && host.Length > 0;
    }
}
