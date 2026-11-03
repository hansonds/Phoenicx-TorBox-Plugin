using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PhoenicxTorBox.TorBox;

namespace PhoenicxTorBox.Library;

/// <summary>The play links written into the .strm files: /PhoenicxTorBox/Play/{t|u|w}/{item}/{file}?sig=...</summary>
public static class PlayLinks
{
    private static readonly Regex LinkRx = new(@"/PhoenicxTorBox/Play/([tuw])/(\d+)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Builds the play link for one file.</summary>
    public static string Build(string publicUrl, SourceType type, long itemId, long fileId)
    {
        var t = TorBoxClient.ShortOf(type);
        return $"{publicUrl.TrimEnd('/')}/PhoenicxTorBox/Play/{t}/{itemId}/{fileId}?sig={Sign(t, itemId, fileId)}";
    }

    /// <summary>Signature that proves the link was made by this server.</summary>
    public static string Sign(string t, long itemId, long fileId)
    {
        var secret = Plugin.Instance?.Configuration.SigningSecret ?? string.Empty;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes($"{t}/{itemId}/{fileId}"));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>Checks a signature.</summary>
    public static bool Verify(string t, long itemId, long fileId, string? sig) =>
        !string.IsNullOrEmpty(sig)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(t, itemId, fileId)),
            Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));

    /// <summary>Reads a play link (e.g. a media source path) back into its parts.</summary>
    public static bool TryParse(string? path, out SourceType type, out long itemId, out long fileId)
    {
        type = SourceType.Torrent;
        itemId = 0;
        fileId = 0;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var m = LinkRx.Match(path);
        if (!m.Success || TorBoxClient.FromShort(m.Groups[1].Value.ToLowerInvariant()) is not { } t)
        {
            return false;
        }

        type = t;
        return long.TryParse(m.Groups[2].Value, out itemId) && long.TryParse(m.Groups[3].Value, out fileId);
    }
}
