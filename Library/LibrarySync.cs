using System.Text;
using System.Text.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using PhoenicxTorBox.TorBox;

namespace PhoenicxTorBox.Library;

/// <summary>Result of the last sync, shown on the plugin page.</summary>
public sealed class SyncStatus
{
    /// <summary>Gets or sets when the sync finished (UTC).</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether it worked.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets a short message.</summary>
    public string Message { get; set; } = "Not synced yet.";

    /// <summary>Gets or sets the number of movies.</summary>
    public int Movies { get; set; }

    /// <summary>Gets or sets the number of series.</summary>
    public int Shows { get; set; }

    /// <summary>Gets or sets the number of episodes.</summary>
    public int Episodes { get; set; }

    /// <summary>Gets or sets files added this run.</summary>
    public int Added { get; set; }

    /// <summary>Gets or sets files removed this run.</summary>
    public int Removed { get; set; }

    /// <summary>Gets or sets the file count at which a big removal was held back (checked again next sync).</summary>
    public int? HeldBackAt { get; set; }

    /// <summary>Gets or sets when that big removal was first seen (UTC).</summary>
    public DateTime? HeldBackSinceUtc { get; set; }

    /// <summary>Gets or sets an extra note for the plugin page.</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets the number of subtitle files in the library.</summary>
    public int Subtitles { get; set; }

    /// <summary>Gets or sets subtitle files downloaded this run.</summary>
    public int SubtitlesAdded { get; set; }

    /// <summary>Gets or sets how many Jellyfin libraries use the TorBox folders.</summary>
    public int LibrariesScanned { get; set; }

    /// <summary>Gets or sets the tag folders made in this run.</summary>
    public List<TagFolder> Tags { get; set; } = new();

    /// <summary>Gets or sets names that could not be read (skipped).</summary>
    public List<string> Skipped { get; set; } = new();
}

/// <summary>One folder made for a TorBox tag.</summary>
public sealed class TagFolder
{
    /// <summary>Gets or sets the tag.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder to add as a library.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets how many movies / series it holds.</summary>
    public int Titles { get; set; }
}

/// <summary>Turns the TorBox account into folders of .strm files for Jellyfin.</summary>
public sealed class LibrarySync
{
    private static readonly char[] Invalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private readonly TorBoxClient _torbox;
    private readonly ILibraryMonitor _monitor;
    private readonly IServerApplicationHost _host;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibrarySync> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncStatus? _status;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySync"/> class.
    /// </summary>
    public LibrarySync(TorBoxClient torbox, ILibraryMonitor monitor, IServerApplicationHost host, ILibraryManager libraryManager, ILogger<LibrarySync> log)
    {
        _libraryManager = libraryManager;
        _host = host;
        _torbox = torbox;
        _monitor = monitor;
        _log = log;
    }

    /// <summary>Gets the last result.</summary>
    public SyncStatus Status => _status ??= LoadStatus();

    private static string StatusFile => Path.Combine(Plugin.Instance!.LibraryRoot, ".phoenicx-torbox-status.json");

    /// <summary>Reads TorBox and updates the .strm folders.</summary>
    public async Task<SyncStatus> RunAsync(IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = await RunCoreAsync(progress, ct).ConfigureAwait(false);
            _status = status;
            SaveStatus(status);
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Phoenicx TorBox: sync failed");
            var failed = new SyncStatus { FinishedUtc = DateTime.UtcNow, Ok = false, Message = ex.Message };
            _status = failed;
            SaveStatus(failed);
            return failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncStatus> RunCoreAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not loaded.");
        var cfg = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.PublicServerUrl))
        {
            throw new InvalidOperationException("Set the public server address in the plugin settings.");
        }

        progress?.Report(2);
        var all = await _torbox.GetAllVideoFilesAsync(cfg.IncludeUsenet, cfg.IncludeWebDownloads, ct).ConfigureAwait(false);
        progress?.Report(40);
        var files = all.Where(f => !f.IsSubtitle).ToList();
        var subtitleOwner = MatchSubtitles(all.Where(f => f.IsSubtitle).ToList(), files);
        var desiredSubs = new Dictionary<string, TorBoxFile>(StringComparer.Ordinal);

        // Puts a video's subtitle files next to its .strm, named the way Jellyfin reads them:
        // "Leo (2023).en.srt", "Leo (2023).ta.forced.srt".
        void AddSubtitles(string strmPath, TorBoxFile video)
        {
            if (!subtitleOwner.TryGetValue(video, out var subs))
            {
                return;
            }

            var stem = strmPath[..^".strm".Length];
            var videoStem = Path.GetFileNameWithoutExtension(video.FileName);
            foreach (var sub in subs)
            {
                var (lang, kind) = SubtitleNames.Read(sub.FileName, videoStem);
                var name = stem + (lang is null ? string.Empty : "." + lang) + (kind is null ? string.Empty : "." + kind);
                var ext = Path.GetExtension(sub.FileName).ToLowerInvariant();
                var path = name + ext;
                for (var n = 2; desiredSubs.ContainsKey(path); n++)
                {
                    path = $"{name}.{n}{ext}";
                }

                desiredSubs[path] = sub;
            }
        }

        var fixes = ParseFixes(cfg.NameFixes);
        var status = new SyncStatus();
        var movies = new List<(ParsedName P, TorBoxFile F)>();
        var episodes = new List<(ParsedName P, TorBoxFile F)>();

        foreach (var f in files)
        {
            var p = NameParser.ParseFile(f.FileName, f.FullPath, f.ItemName);
            foreach (var (text, title, year) in fixes)
            {
                if (f.ItemName.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || f.FullPath.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || f.FileName.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    p = p with { Title = title, Year = year ?? p.Year };
                    break;
                }
            }

            if (p.Title.Length < 1 || NameParser.Normalize(p.Title).Length == 0)
            {
                if (status.Skipped.Count < 50)
                {
                    status.Skipped.Add(f.FullPath.Length > 0 ? f.FullPath : f.FileName);
                }

                continue;
            }

            (p.IsEpisode ? episodes : movies).Add((p, f));
        }

        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        // The .strm files point at this server's own local address, so Jellyfin itself (reading
        // media info, Docker on Oracle) never needs to go out to the internet and back in.
        // Apps never see it: the filter swaps in a TorBox link or the public address.
        var url = LocalUrl();

        // One spelling per tag ("Cinema" and "cinema" are the same folder).
        var tagNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in files.SelectMany(f => f.Tags))
        {
            var safe = Safe(t);
            if (safe.Length > 0)
            {
                tagNames.TryAdd(safe, safe);
            }
        }

        // A movie found once with and once without a year: use the year when only one is known.
        var yearsByTitle = movies
            .Where(m => m.P.Year is not null)
            .GroupBy(m => NameParser.Normalize(m.P.Title))
            .ToDictionary(g => g.Key, g => g.Select(m => m.P.Year!.Value).Distinct().ToList());
        for (var i = 0; i < movies.Count; i++)
        {
            var (p, f) = movies[i];
            if (p.Year is null && yearsByTitle.TryGetValue(NameParser.Normalize(p.Title), out var ys) && ys.Count == 1)
            {
                movies[i] = (p with { Year = ys[0] }, f);
            }
        }

        // Each file goes into its tag folders (Tags/<tag>), or Movies / Shows when it has no tag.
        string[] BucketsOf(TorBoxFile f) =>
            cfg.FoldersByTag && f.Tags.Count > 0
                ? f.Tags.Select(Safe).Where(t => t.Length > 0).Select(t => tagNames[t]).Distinct(StringComparer.Ordinal).DefaultIfEmpty(string.Empty).ToArray()
                : [string.Empty];

        var tagCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var movieKeys = new HashSet<string>(StringComparer.Ordinal);
        var showKeys = new HashSet<string>(StringComparer.Ordinal);
        var episodeKeys = new HashSet<string>(StringComparer.Ordinal);

        // ---- Movies: one folder per title + year; several files = versions.
        foreach (var group in movies
                     .SelectMany(m => BucketsOf(m.F).Select(b => (Bucket: b, m.P, m.F)))
                     .GroupBy(m => (m.Bucket, Key: NameParser.Normalize(m.P.Title) + "|" + m.P.Year)))
        {
            // Inside one download, several files with the same title are parts/extras: keep the largest.
            var picks = group
                .GroupBy(m => (m.F.Source, m.F.ItemId))
                .Select(g => g.OrderByDescending(x => x.F.Size).First())
                .OrderByDescending(x => x.F.Size)
                .Select(x => (x.P, x.F))
                .ToList();
            var first = picks[0].P;
            var baseName = Safe(first.Year is { } y ? $"{first.Title} ({y})" : first.Title);
            var bucket = group.Key.Bucket;
            var dir = Path.Combine(bucket.Length == 0 ? plugin.MoviesPath : Path.Combine(plugin.TagsPath, bucket), baseName);
            foreach (var (name, pick) in VersionNames(baseName, picks))
            {
                var strm = Path.Combine(dir, name + ".strm");
                desired[strm] = PlayLinks.Build(url, pick.F.Source, pick.F.ItemId, pick.F.FileId);
                AddSubtitles(strm, pick.F);
            }

            movieKeys.Add(group.Key.Key);
            if (bucket.Length > 0)
            {
                tagCounts[bucket] = tagCounts.GetValueOrDefault(bucket) + 1;
            }
        }

        // ---- Shows: one folder per series, Season NN folders, SxxEyy files.
        foreach (var show in episodes
                     .SelectMany(e => BucketsOf(e.F).Select(b => (Bucket: b, e.P, e.F)))
                     .GroupBy(e => (e.Bucket, Key: NameParser.Normalize(e.P.Title))))
        {
            var withYear = show.FirstOrDefault(e => e.P.Year is not null);
            var title = (withYear.F is not null ? withYear.P : show.First().P).Title;
            var year = withYear.F is not null ? withYear.P.Year : null;
            var showName = Safe(year is { } y ? $"{title} ({y})" : title);
            var bucket = show.Key.Bucket;
            var showDir = Path.Combine(bucket.Length == 0 ? plugin.ShowsPath : Path.Combine(plugin.TagsPath, bucket), showName);
            foreach (var ep in show.GroupBy(e => (e.P.Season!.Value, e.P.Episode!.Value)))
            {
                var (s, e) = ep.Key;
                var baseName = Safe($"{title} S{s:00}E{e:00}");
                var seasonDir = Path.Combine(showDir, $"Season {s:00}");
                // Jellyfin has no versions for episodes (a second file shows as a duplicate), so
                // the largest file of the episode is used.
                var best = ep.OrderByDescending(x => x.F.Size).First();
                var strm = Path.Combine(seasonDir, baseName + ".strm");
                desired[strm] = PlayLinks.Build(url, best.F.Source, best.F.ItemId, best.F.FileId);
                AddSubtitles(strm, best.F);

                episodeKeys.Add(show.Key.Key + "|" + s + "|" + e);
            }

            showKeys.Add(show.Key.Key);
            if (bucket.Length > 0)
            {
                tagCounts[bucket] = tagCounts.GetValueOrDefault(bucket) + 1;
            }
        }

        status.Movies = movieKeys.Count;
        status.Shows = showKeys.Count;
        status.Episodes = episodeKeys.Count;
        status.Tags = tagCounts.Select(kv => new TagFolder { Tag = kv.Key, Path = Path.Combine(plugin.TagsPath, kv.Key), Titles = kv.Value }).ToList();

        progress?.Report(60);

        // ---- Write the changes.
        Directory.CreateDirectory(plugin.MoviesPath);
        Directory.CreateDirectory(plugin.ShowsPath);
        Directory.CreateDirectory(plugin.TagsPath);
        var onDisk = new[] { plugin.MoviesPath, plugin.ShowsPath, plugin.TagsPath }
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .ToList();
        var existing = onDisk.Where(p => p.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        var existingSubs = onDisk
            .Where(p => TorBoxClient.SubtitleExt.Contains(Path.GetExtension(p).TrimStart('.')))
            .ToHashSet(StringComparer.Ordinal);

        var changedFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, content) in desired)
        {
            ct.ThrowIfCancellationRequested();
            if (existing.Contains(path) && SafeRead(path) == content)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            if (!existing.Contains(path))
            {
                status.Added++;
            }

            changedFolders.Add(TopFolder(plugin, path));
        }

        var toRemove = existing.Where(p => !desired.ContainsKey(p)).ToList();

        // A TorBox hiccup (empty or half list) must not wipe the library. A real big clean-up
        // on TorBox shows the same smaller list again at the next sync, and is applied then.
        var previousHeld = Status.HeldBackAt;
        var heldSince = Status.HeldBackSinceUtc;
        status.HeldBackAt = null;
        status.HeldBackSinceUtc = null;
        if (desired.Count == 0 && existing.Count > 0)
        {
            // TorBox answering with nothing at all is an outage or maintenance, never a real
            // "delete everything": the library is kept as it is.
            _log.LogWarning("Phoenicx TorBox: TorBox returned no files at all; keeping the library unchanged");
            status.Note = "TorBox returned no files (maintenance?). The library was kept as it is.";
            toRemove.Clear();
        }
        else if (toRemove.Count > 10 && desired.Count < existing.Count / 2)
        {
            // Applied only when TorBox still shows the same smaller list at least 6 hours later.
            if (previousHeld is { } held && heldSince is { } since
                && DateTime.UtcNow - since >= TimeSpan.FromHours(6)
                && Math.Abs(held - desired.Count) <= Math.Max(2, held / 20))
            {
                _log.LogInformation("Phoenicx TorBox: TorBox still has {Now} files, removing the rest", desired.Count);
            }
            else
            {
                _log.LogWarning(
                    "Phoenicx TorBox: TorBox returned far fewer files than before ({Now} vs {Before}); removing only if the next sync shows the same",
                    desired.Count,
                    existing.Count);
                status.HeldBackAt = desired.Count;
                status.HeldBackSinceUtc = previousHeld is { } h && Math.Abs(h - desired.Count) <= Math.Max(2, h / 20) && heldSince is not null
                    ? heldSince
                    : DateTime.UtcNow;
                status.Note = $"TorBox suddenly has far fewer files ({desired.Count} vs {existing.Count}). Nothing removed yet; if TorBox still shows this after 6 hours, they will be removed.";
                toRemove.Clear();
            }
        }

        foreach (var path in toRemove)
        {
            try
            {
                File.Delete(path);
                status.Removed++;
                changedFolders.Add(TopFolder(plugin, path));
                RemoveEmptyDirs(Path.GetDirectoryName(path)!, plugin);
            }
            catch (IOException ex)
            {
                _log.LogWarning("Phoenicx TorBox: could not remove {Path}: {Msg}", path, ex.Message);
            }
        }

        // ---- Subtitles: download new ones (they don't change, so existing files are kept).
        var subFailures = 0;
        var newSubs = 0;
        foreach (var (path, sub) in desiredSubs)
        {
            ct.ThrowIfCancellationRequested();
            if (existingSubs.Contains(path))
            {
                continue;
            }

            // Spread a big first download over several syncs.
            if (newSubs >= 300)
            {
                break;
            }

            try
            {
                var bytes = await _torbox.DownloadSmallAsync(sub, ct).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                newSubs++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                subFailures++;
                _log.LogWarning("Phoenicx TorBox: subtitle {Name} not downloaded: {Msg}", sub.FileName, ex.Message);
            }
        }

        // Remove subtitles whose movie / episode is gone (not while TorBox looked half empty).
        if (desired.Count > 0 && (toRemove.Count > 0 || desired.Count >= existing.Count / 2))
        {
            foreach (var path in existingSubs.Where(p => !desiredSubs.ContainsKey(p)))
            {
                try
                {
                    File.Delete(path);
                    RemoveEmptyDirs(Path.GetDirectoryName(path)!, plugin);
                }
                catch (IOException ex)
                {
                    _log.LogWarning("Phoenicx TorBox: could not remove {Path}: {Msg}", path, ex.Message);
                }
            }
        }

        status.Subtitles = desiredSubs.Count;
        status.SubtitlesAdded = newSubs;

        progress?.Report(85);

        // Scan the Jellyfin libraries that use these folders (only those, not OneDrive etc.).
        // Also on a run without changes, so a library added after the first sync gets filled.
        await ScanOurLibrariesAsync(plugin, status, ct).ConfigureAwait(false);

        status.Ok = true;
        status.FinishedUtc = DateTime.UtcNow;
        status.Message = $"{status.Movies} movies, {status.Shows} series ({status.Episodes} episodes), {status.Tags.Count} tag folder(s). Added {status.Added}, removed {status.Removed}. Subtitles: {status.Subtitles} ({status.SubtitlesAdded} new). Libraries scanned: {status.LibrariesScanned}." + (status.Note is null ? string.Empty : " " + status.Note);
        _log.LogInformation("Phoenicx TorBox: sync done. {Message}", status.Message);
        progress?.Report(100);
        return status;
    }

    /// <summary>
    /// Decides which video each subtitle file belongs to, inside the same TorBox download:
    /// same file name start, same season/episode, a folder named after the video, or (for a
    /// movie download) the main video.
    /// </summary>
    private static Dictionary<TorBoxFile, List<TorBoxFile>> MatchSubtitles(List<TorBoxFile> subs, List<TorBoxFile> videos)
    {
        var owner = new Dictionary<TorBoxFile, List<TorBoxFile>>();
        var videosByItem = videos.GroupBy(v => (v.Source, v.ItemId)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var sub in subs)
        {
            if (!videosByItem.TryGetValue((sub.Source, sub.ItemId), out var candidates))
            {
                continue;
            }

            TorBoxFile? match = null;
            if (candidates.Count == 1)
            {
                match = candidates[0];
            }
            else
            {
                var subStem = Path.GetFileNameWithoutExtension(sub.FileName);
                match = candidates
                    .Where(v => subStem.StartsWith(Path.GetFileNameWithoutExtension(v.FileName), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.FileName.Length)
                    .FirstOrDefault();

                match ??= candidates.FirstOrDefault(v =>
                    sub.FullPath.Contains("/" + Path.GetFileNameWithoutExtension(v.FileName) + "/", StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    var sp = NameParser.ParseFile(sub.FileName, sub.FullPath, sub.ItemName);
                    if (sp.IsEpisode)
                    {
                        match = candidates.FirstOrDefault(v =>
                        {
                            var vp = NameParser.ParseFile(v.FileName, v.FullPath, v.ItemName);
                            return vp.Season == sp.Season && vp.Episode == sp.Episode;
                        });
                    }
                }

                // A movie download with an extra video: the subtitles are for the movie itself.
                if (match is null && !candidates.Any(v => NameParser.ParseFile(v.FileName, v.FullPath, v.ItemName).IsEpisode))
                {
                    match = candidates.OrderByDescending(v => v.Size).First();
                }
            }

            if (match is not null)
            {
                if (!owner.TryGetValue(match, out var list))
                {
                    owner[match] = list = new List<TorBoxFile>();
                }

                list.Add(sub);
            }
        }

        return owner;
    }

    private async Task ScanOurLibrariesAsync(Plugin plugin, SyncStatus status, CancellationToken ct)
    {
        var root = Path.GetFullPath(plugin.LibraryRoot).TrimEnd('/') + "/";
        var found = 0;
        foreach (var vf in _libraryManager.GetVirtualFolders())
        {
            var ours = vf.Locations?.Any(l => (Path.GetFullPath(l).TrimEnd('/') + "/").StartsWith(root, StringComparison.Ordinal)) == true;
            if (!ours || !Guid.TryParse(vf.ItemId, out var id) || _libraryManager.GetItemById(id) is not Folder folder)
            {
                continue;
            }

            found++;
            try
            {
                _log.LogInformation("Phoenicx TorBox: scanning library {Name}", vf.Name);
                await folder.ValidateChildren(new Progress<double>(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Phoenicx TorBox: scan of {Name} failed", vf.Name);
            }
        }

        status.LibrariesScanned = found;
        if (found == 0)
        {
            _log.LogInformation("Phoenicx TorBox: no Jellyfin library uses {Root} yet - add the folders as libraries", root);
        }
    }

    private string LocalUrl()
    {
        // The method name differs between Jellyfin versions, so it is looked up by name.
        try
        {
            var t = _host.GetType();
            foreach (var name in new[] { "GetLoopbackHttpApiUrl", "GetApiUrlForLocalAccess" })
            {
                var m = t.GetMethods().FirstOrDefault(x => x.Name == name && x.GetParameters().All(p => p.IsOptional));
                var args = m?.GetParameters().Select(p => p.ParameterType == typeof(bool) ? (object?)false : p.DefaultValue).ToArray();
                if (m?.Invoke(_host, args) is string u && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return u.TrimEnd('/');
                }
            }

            if (t.GetProperty("HttpPort")?.GetValue(_host) is int port && port > 0)
            {
                return $"http://127.0.0.1:{port}";
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Phoenicx TorBox: no local address found");
        }

        return "http://127.0.0.1:8096";
    }

    private static IEnumerable<(string Name, (ParsedName P, TorBoxFile F) Pick)> VersionNames(
        string baseName,
        List<(ParsedName P, TorBoxFile F)> picks)
    {
        if (picks.Count == 1)
        {
            yield return (baseName, picks[0]);
            yield break;
        }

        // Jellyfin shows "Name - label" files in one folder as versions of the same title.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        foreach (var pick in picks)
        {
            var label = NameParser.ResolutionOf(pick.F.FileName) ?? NameParser.ResolutionOf(pick.F.ItemName) ?? "Version";
            var unique = label;
            while (!used.Add(unique))
            {
                unique = $"{label} {++n}";
            }

            yield return ($"{baseName} - {unique}", pick);
        }
    }

    private static List<(string Text, string Title, int? Year)> ParseFixes(string? text)
    {
        var list = new List<(string, string, int?)>();
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var i = line.IndexOf('=', StringComparison.Ordinal);
            if (i <= 0)
            {
                continue;
            }

            var match = line[..i].Trim();
            var (title, year) = NameParser.SplitTitleYear(line[(i + 1)..]);
            if (match.Length > 0 && title.Length > 0)
            {
                list.Add((match, title, year));
            }
        }

        return list;
    }

    private static string Safe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(Invalid, ch) >= 0 || char.IsControl(ch) ? ' ' : ch);
        }

        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return s.Length > 120 ? s[..120].Trim() : s;
    }

    private static string TopFolder(Plugin plugin, string filePath)
    {
        var tagRel = Path.GetRelativePath(plugin.TagsPath, filePath);
        if (!tagRel.StartsWith("..", StringComparison.Ordinal))
        {
            var parts = tagRel.Split(Path.DirectorySeparatorChar);
            return parts.Length > 2 ? Path.Combine(plugin.TagsPath, parts[0], parts[1]) : Path.Combine(plugin.TagsPath, parts[0]);
        }

        foreach (var root in new[] { plugin.MoviesPath, plugin.ShowsPath })
        {
            var rel = Path.GetRelativePath(root, filePath);
            if (!rel.StartsWith("..", StringComparison.Ordinal))
            {
                var first = rel.Split(Path.DirectorySeparatorChar)[0];
                return Path.Combine(root, first);
            }
        }

        return Path.GetDirectoryName(filePath)!;
    }

    private static void RemoveEmptyDirs(string dir, Plugin plugin)
    {
        var stop = new[] { plugin.MoviesPath, plugin.ShowsPath, plugin.TagsPath, plugin.LibraryRoot };
        var d = dir;
        while (!string.IsNullOrEmpty(d) && !stop.Contains(d, StringComparer.Ordinal) && Directory.Exists(d)
               && !Directory.EnumerateFileSystemEntries(d).Any())
        {
            Directory.Delete(d);
            d = Path.GetDirectoryName(d);
        }
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static SyncStatus LoadStatus()
    {
        try
        {
            if (Plugin.Instance is not null && File.Exists(StatusFile))
            {
                return JsonSerializer.Deserialize<SyncStatus>(File.ReadAllText(StatusFile)) ?? new SyncStatus();
            }
        }
        catch (Exception)
        {
            // Ignore a broken status file.
        }

        return new SyncStatus();
    }

    private void SaveStatus(SyncStatus s)
    {
        try
        {
            Directory.CreateDirectory(Plugin.Instance!.LibraryRoot);
            File.WriteAllText(StatusFile, JsonSerializer.Serialize(s));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Phoenicx TorBox: status not saved");
        }
    }
}
