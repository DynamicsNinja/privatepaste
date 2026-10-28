using System.Net;
using System.Net.Sockets;

namespace PrivatePaste.Server.Auth;

/// <summary>Rate-limit partition key for login attempts.</summary>
public static class LoginPartition
{
    /// <summary>
    /// IPv4 (including IPv4-mapped IPv6) by full address; IPv6 by its /64 prefix, since a single client usually
    /// controls a whole /64 and could otherwise rotate addresses to dodge the limit. Null (TestServer) is "unknown".
    /// </summary>
    public static string KeyFor(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
