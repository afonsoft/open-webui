using System.Net;
using System.Net.Sockets;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Guard SSRF compartilhado das tools que buscam rede
/// (<c>fetch_url</c>, <c>browser_screenshot</c>): resolve o host e barra se
/// qualquer endereço for privado, loopback, link-local, CGNAT ou reservado
/// — fail-closed quando o DNS não resolve. <paramref name="allowLoopback"/>
/// libera 127.0.0.0/8 e ::1 para ferramentas cujo caso de uso É o host
/// local (screenshot do preview que o agente subiu), mantendo o bloqueio
/// de LAN/metadata de cloud.
/// </summary>
public static class SsrfGuard
{
    /// <summary>True quando o host resolve para endereço que deve ser bloqueado.</summary>
    public static async Task<bool> IsBlockedAsync(
        string host, bool allowLoopback, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true; // não resolve → fail-closed
        }
        catch (ArgumentException)
        {
            return true; // não resolve → fail-closed
        }

        if (addresses.Length == 0)
        {
            return true;
        }

        return addresses.Any(a => IsBlockedAddress(a, allowLoopback));
    }

    private static bool IsBlockedAddress(IPAddress address, bool allowLoopback)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (allowLoopback && IPAddress.IsLoopback(address))
            {
                return false;
            }

            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.IPv6Any)
                || IsInPrefix(address, 0xfc, 7); // fc00::/7 unique-local
        }

        var bytes = address.GetAddressBytes();
        if (allowLoopback && bytes[0] == 127)
        {
            return false;
        }

        return bytes[0] == 0                                  // 0.0.0.0/8
            || bytes[0] == 10                                 // 10/8
            || bytes[0] == 127                                // loopback
            || (bytes[0] == 169 && bytes[1] == 254)           // link-local
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) // 172.16/12
            || (bytes[0] == 192 && bytes[1] == 168)           // 192.168/16
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127); // CGNAT 100.64/10
    }

    private static bool IsInPrefix(IPAddress address, int prefixHighByte, int prefixBits)
    {
        var b = address.GetAddressBytes()[0];
        var mask = 0xff << (8 - prefixBits) & 0xff;
        return (b & mask) == (prefixHighByte & mask);
    }
}
