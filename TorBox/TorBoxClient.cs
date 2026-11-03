using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.TorBox;

/// <summary>The three kinds of downloads TorBox keeps.</summary>
public enum SourceType
{
    /// <summary>Torrents.</summary>
    Torrent,

    /// <summary>Usenet downloads.</summary>
    Usenet,

    /// <summary>Web downloads.</summary>
    WebDl,
}

/// <summary>One playable video file in the TorBox account.</summary>
public sealed record TorBoxFile(
    SourceType Source,
    long ItemId,
    long FileId,
    string FileName,
    string FullPath,
    string ItemName,
    long Size,
    IReadOnlyList<string> Tags);

/// <summary>Talks to api.torbox.app.</summary>
public sealed class TorBoxClient
{
    private const string Base = "https://api.torbox.app/v1/api";

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "avi", "m4v", "mov", "wmv", "ts", "m2ts", "webm", "mpg", "mpeg", "flv", "vob",
    };

    private static readonly Regex SampleRx = new(@"(^|[ ._\-\[(])sample([ ._\-\])]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TorBoxClient> _log;
    private readonly ConcurrentDictionary<string, (string Url, DateTime Expires)> _links = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TorBoxClient"/> class.
    /// </summary>
    public TorBoxClient(IHttpClientFactory httpFactory, ILogger<TorBoxClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    /// <summary>Path segment in the TorBox API.</summary>
    public static string PathOf(SourceType t) => t switch
    {
        SourceType.Usenet => "usenet",
        SourceType.WebDl => "webdl",
        _ => "torrents",
    };

    /// <summary>Item id query parameter of requestdl.</summary>
    public static string IdParamOf(SourceType t) => t switch
    {
        SourceType.Usenet => "usenet_id",
        SourceType.WebDl => "web_id",
        _ => "torrent_id",
    };

    /// <summary>Short name used in play links.</summary>
    public static string ShortOf(SourceType t) => t switch
    {
        SourceType.Usenet => "u",
        SourceType.WebDl => "w",
        _ => "t",
    };

    /// <summary>Reads the short name back.</summary>
    public static SourceType? FromShort(string s) => s switch
    {
        "t" => SourceType.Torrent,
        "u" => SourceType.Usenet,
        "w" => SourceType.WebDl,
        _ => null,
    };

    private static string Key => Plugin.Instance?.Configuration.TorBoxApiKey?.Trim() ?? string.Empty;

    private HttpClient NewClient(string key)
    {
        var c = _httpFactory.CreateClient("PhoenicxTorBox");
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenicxTorBox/1.0");
        return c;
    }

    /// <summary>Checks the key and returns a short "Connected as ..." text.</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        var key = Key;
        if (key.Length == 0)
        {
            throw new InvalidOperationException("Add your TorBox API key first.");
        }

        using var c = NewClient(key);
        using var doc = await GetJsonAsync(c, $"{Base}/user/me", ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
        {
            return "Connected as " + email.GetString();
        }

        return "Connected";
    }

    /// <summary>All finished video files from torrents, usenet and web downloads.</summary>
    public async Task<List<TorBoxFile>> GetAllVideoFilesAsync(bool usenet, bool webdl, CancellationToken ct)
    {
        var key = Key;
        if (key.Length == 0)
        {
            throw new InvalidOperationException("Add your TorBox API key in the plugin settings.");
        }

        using var c = NewClient(key);
        var all = new List<TorBoxFile>();

        // Torrents must work; a failing usenet / web list (e.g. not on the plan) is only logged.
        all.AddRange(await GetTypeAsync(c, SourceType.Torrent, ct).ConfigureAwait(false));
        foreach (var (on, type) in new[] { (usenet, SourceType.Usenet), (webdl, SourceType.WebDl) })
        {
            if (!on)
            {
                continue;
            }

            try
            {
                all.AddRange(await GetTypeAsync(c, type, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Phoenicx TorBox: could not read {Type} list: {Msg}", PathOf(type), ex.Message);
            }
        }

        return all;
    }

    private async Task<List<TorBoxFile>> GetTypeAsync(HttpClient c, SourceType type, CancellationToken ct)
    {
        var outList = new List<TorBoxFile>();
        const int limit = 1000;
        var offset = 0;
        while (true)
        {
            var url = $"{Base}/{PathOf(type)}/mylist?bypass_cache=true&offset={offset}&limit={limit}";
            using var doc = await GetJsonAsync(c, url, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                break;
            }

            var items = data.ValueKind switch
            {
                JsonValueKind.Array => data.EnumerateArray().ToList(),
                JsonValueKind.Object => new List<JsonElement> { data },
                _ => new List<JsonElement>(),
            };

            foreach (var it in items)
            {
                if (it.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var ready = IsTrue(it, "download_present") || IsTrue(it, "download_finished") || IsTrue(it, "cached");
                if (!ready || !TryLong(it, "id", out var itemId))
                {
                    continue;
                }

                var itemName = Str(it, "name");
                var tags = ReadTags(it);
                if (!it.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var f in files.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object || !TryLong(f, "id", out var fileId))
                    {
                        continue;
                    }

                    var full = Str(f, "name");
                    var shortName = Str(f, "short_name");
                    if (shortName.Length == 0)
                    {
                        shortName = full.Split('/').Last();
                    }

                    TryLong(f, "size", out var size);
                    var mime = Str(f, "mimetype");
                    var ext = Path.GetExtension(shortName).TrimStart('.');
                    if (!VideoExt.Contains(ext) && !mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Skip samples and tiny extras.
                    if (SampleRx.IsMatch(shortName) && size < 400L * 1024 * 1024)
                    {
                        continue;
                    }

                    if (size > 0 && size < 40L * 1024 * 1024)
                    {
                        continue;
                    }

                    outList.Add(new TorBoxFile(type, itemId, fileId, shortName, full, itemName, size, tags));
                }
            }

            if (items.Count < limit)
            {
                break;
            }

            offset += limit;
        }

        return outList;
    }

    /// <summary>
    /// A fresh direct link (TorBox CDN) for one file. Links are kept for 45 minutes, because
    /// clients ask for playback info several times when starting one video.
    /// </summary>
    public async Task<string> GetStreamLinkAsync(SourceType type, long itemId, long fileId, CancellationToken ct)
    {
        var cacheKey = $"{ShortOf(type)}:{itemId}:{fileId}";
        if (_links.TryGetValue(cacheKey, out var hit) && hit.Expires > DateTime.UtcNow)
        {
            return hit.Url;
        }

        var key = Key;
        if (key.Length == 0)
        {
            throw new InvalidOperationException("No TorBox API key set.");
        }

        using var c = NewClient(key);
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var url = $"{Base}/{PathOf(type)}/requestdl?token={Uri.EscapeDataString(key)}&{IdParamOf(type)}={itemId}&file_id={fileId}&zip_link=false&redirect=false";
                using var doc = await GetJsonAsync(c, url, ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                    && d.GetString() is { } link && link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    _links[cacheKey] = (link, DateTime.UtcNow.AddMinutes(45));
                    if (_links.Count > 500)
                    {
                        foreach (var old in _links.Where(kv => kv.Value.Expires < DateTime.UtcNow).ToList())
                        {
                            _links.TryRemove(old.Key, out _);
                        }
                    }

                    return link;
                }

                last = new InvalidOperationException("TorBox did not return a link.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }

            await Task.Delay(700 * (attempt + 1), ct).ConfigureAwait(false);
        }

        throw last ?? new InvalidOperationException("Could not get a TorBox link.");
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient c, string url, CancellationToken ct)
    {
        using var r = await c.GetAsync(url, ct).ConfigureAwait(false);
        var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"TorBox returned HTTP {(int)r.StatusCode}.");
        }

        var root = doc.RootElement;
        var failed = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False;
        if (!r.IsSuccessStatusCode || failed)
        {
            var detail = Str(root, "detail");
            if (detail.Length == 0)
            {
                detail = Str(root, "error");
            }

            doc.Dispose();
            throw new InvalidOperationException("TorBox: " + (detail.Length > 0 ? detail : $"HTTP {(int)r.StatusCode}"));
        }

        return doc;
    }

    /// <summary>The download's TorBox tags (a list, or a comma separated text).</summary>
    private static List<string> ReadTags(JsonElement it)
    {
        var tags = new List<string>();
        if (!it.TryGetProperty("tags", out var t))
        {
            return tags;
        }

        IEnumerable<string> raw = t.ValueKind switch
        {
            JsonValueKind.Array => t.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty
                    : x.ValueKind == JsonValueKind.Object ? Str(x, "name") : string.Empty),
            JsonValueKind.String => (t.GetString() ?? string.Empty).Split(','),
            _ => Array.Empty<string>(),
        };
        foreach (var r in raw)
        {
            var v = r.Trim();
            if (v.Length > 0 && !tags.Contains(v, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(v);
            }
        }

        return tags;
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryLong(JsonElement e, string name, out long value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var v))
        {
            return false;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.TryGetInt64(out value) || (v.TryGetDouble(out var dbl) && (value = (long)dbl) >= 0);
        }

        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out value);
    }
}
