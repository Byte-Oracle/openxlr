using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

// Shared with the browser native host, which must forward exactly the origins
// the daemon accepts and in the form the daemon reports them.
#if OPENXLR_UI
namespace OpenXLR.UI;
#else
namespace OpenXLR.Core.Mixing;
#endif

/// <summary>
/// The origin of an HTTP or HTTPS page in the form a Chromium browser
/// serializes it. .NET writes some addresses differently: an IPv4-mapped
/// IPv6 host such as <c>[::ffff:102:304]</c> comes back as
/// <c>[::ffff:1.2.3.4]</c>. IPv6 hosts are therefore written here by the URL
/// standard's rules, so the daemon reports the same text the browser sent.
/// Hosts .NET cannot parse, such as ones holding <c>*</c> or <c>!</c>, have no
/// canonical form and are refused.
/// </summary>
internal static class BrowserOrigin
{
    internal const int MaxLength = 512;

    /// <summary>The canonical origin, or null when the text is not a bare HTTP or HTTPS origin.</summary>
    internal static string? Canonical(string? value)
    {
        if (value is not { Length: > 0 and <= MaxLength } || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Host.Length == 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        string host;
        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address)) return null;
            host = "[" + Ipv6(address) + "]";
        }
        else host = uri.IdnHost;
        string origin = uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        return origin.Length <= MaxLength ? origin : null;
    }

    /// <summary>
    /// Eight lowercase hexadecimal pieces, with the first longest run of two
    /// or more zero pieces written as <c>::</c>, and never a dotted IPv4 tail.
    /// </summary>
    private static string Ipv6(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        int[] pieces = new int[8];
        for (int i = 0; i < 8; i++) pieces[i] = bytes[2 * i] << 8 | bytes[2 * i + 1];
        int start = -1, length = 1;
        for (int i = 0; i < 8;)
        {
            if (pieces[i] != 0) { i++; continue; }
            int run = i;
            while (i < 8 && pieces[i] == 0) i++;
            if (i - run > length) { start = run; length = i - run; }
        }
        var text = new StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            if (i == start)
            {
                text.Append(i == 0 ? "::" : ":");
                i += length - 1;
                continue;
            }
            text.Append(pieces[i].ToString("x", CultureInfo.InvariantCulture));
            if (i < 7) text.Append(':');
        }
        return text.ToString();
    }
}
