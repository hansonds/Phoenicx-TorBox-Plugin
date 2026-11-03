using System.Text;
using System.Text.Json;
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

    /// <summary>Gets or sets names that could not be read (skipped).</summary>
    public List<string> Skipped { get; set; } = new();
}

/// <summary>Turns the TorBox account into folders of .strm files for Jellyfin.</summary>
public sealed class LibrarySync
{
    private static readonly char[] Invalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private readonly TorBoxClient _torbox;
    private readonly ILibraryMonitor _monitor;
    private readonly ILogger<LibrarySync> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncStatus? _status;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySync"/> class.
    /// </summary>
    public LibrarySync(TorBoxClient torbox, ILibraryMonitor monitor, ILogger<LibrarySync> log)
    {
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
        var files = await _torbox.GetAllVideoFilesAsync(cfg.IncludeUsenet, cfg.IncludeWebDownloads, ct).ConfigureAwait(false);
        progress?.Report(40);

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
        var url = cfg.PublicServerUrl.Trim();

        // ---- Movies: one folder per title + year; several files = versions.
        foreach (var group in movies.GroupBy(m => NameParser.Normalize(m.P.Title) + "|" + m.P.Year))
        {
            // Inside one download, several files with the same title are parts/extras: keep the largest.
            var picks = group
                .GroupBy(m => (m.F.Source, m.F.ItemId))
                .Select(g => g.OrderByDescending(x => x.F.Size).First())
                .OrderByDescending(x => x.F.Size)
                .ToList();
            var first = picks[0].P;
            var baseName = Safe(first.Year is { } y ? $"{first.Title} ({y})" : first.Title);
            var dir = Path.Combine(plugin.MoviesPath, baseName);
            foreach (var (name, pick) in VersionNames(baseName, picks))
            {
                desired[Path.Combine(dir, name + ".strm")] = PlayLinks.Build(url, pick.F.Source, pick.F.ItemId, pick.F.FileId);
            }

            status.Movies++;
        }

        // ---- Shows: one folder per series, Season NN folders, SxxEyy files.
        foreach (var show in episodes.GroupBy(e => NameParser.Normalize(e.P.Title)))
        {
            var withYear = show.FirstOrDefault(e => e.P.Year is not null);
            var title = (withYear.F is not null ? withYear.P : show.First().P).Title;
            var year = withYear.F is not null ? withYear.P.Year : null;
            var showName = Safe(year is { } y ? $"{title} ({y})" : title);
            var showDir = Path.Combine(plugin.ShowsPath, showName);
            foreach (var ep in show.GroupBy(e => (e.P.Season!.Value, e.P.Episode!.Value)))
            {
                var (s, e) = ep.Key;
                var baseName = Safe($"{title} S{s:00}E{e:00}");
                var seasonDir = Path.Combine(showDir, $"Season {s:00}");
                var picks = ep.OrderByDescending(x => x.F.Size).ToList();
                foreach (var (name, pick) in VersionNames(baseName, picks))
                {
                    desired[Path.Combine(seasonDir, name + ".strm")] = PlayLinks.Build(url, pick.F.Source, pick.F.ItemId, pick.F.FileId);
                }

                status.Episodes++;
            }

            status.Shows++;
        }

        progress?.Report(60);

        // ---- Write the changes.
        Directory.CreateDirectory(plugin.MoviesPath);
        Directory.CreateDirectory(plugin.ShowsPath);
        var existing = Directory.EnumerateFiles(plugin.MoviesPath, "*.strm", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(plugin.ShowsPath, "*.strm", SearchOption.AllDirectories))
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

        // A TorBox hiccup (empty or half list) must not wipe the library.
        if (toRemove.Count > 10 && desired.Count < existing.Count / 2)
        {
            _log.LogWarning(
                "Phoenicx TorBox: TorBox returned far fewer files than before ({Now} vs {Before}); not removing anything this time",
                desired.Count,
                existing.Count);
            toRemove.Clear();
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

        progress?.Report(85);

        // Ask Jellyfin to look at the changed folders only (not a full library scan).
        foreach (var folder in changedFolders)
        {
            try
            {
                _monitor.ReportFileSystemChanged(folder);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Phoenicx TorBox: refresh of {Folder} not queued", folder);
            }
        }

        status.Ok = true;
        status.FinishedUtc = DateTime.UtcNow;
        status.Message = $"{status.Movies} movies, {status.Shows} series ({status.Episodes} episodes). Added {status.Added}, removed {status.Removed}.";
        _log.LogInformation("Phoenicx TorBox: sync done. {Message}", status.Message);
        progress?.Report(100);
        return status;
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
        var stop = new[] { plugin.MoviesPath, plugin.ShowsPath, plugin.LibraryRoot };
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
