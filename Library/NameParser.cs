using System.Text.RegularExpressions;

namespace PhoenicxTorBox.Library;

/// <summary>Title, year, season and episode read from a release name.</summary>
public sealed record ParsedName(string Title, int? Year = null, int? Season = null, int? Episode = null)
{
    /// <summary>Gets a value indicating whether this is an episode.</summary>
    public bool IsEpisode => Season is not null && Episode is not null;
}

/// <summary>Turns messy release names into a title / year / season / episode.</summary>
public static class NameParser
{
    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly Regex Ext = new(@"\.[A-Za-z0-9]{2,4}$", RegexOptions.Compiled);
    private static readonly Regex SitePrefix = new(@"^\s*(www\.)?[A-Za-z0-9\-]+\.(com|org|net|in|ws|xyz|cc|me|to|se|li|pw|lol|fans|io|co|ms|bz|ink|day|life|fyi|win|vip|art|mx|zip|boo|dad|bike|cat|rs|sx|ph|tv|gs|ac|ltd|uk|re|lc|nz|at|am)\s*[-–_]+\s*", Ic);
    private static readonly Regex GroupPrefix = new(@"^\s*[\[【][^\]】]*[\]】]\s*", RegexOptions.Compiled);

    private static readonly Regex[] EpPatterns =
    {
        new(@"(?<![A-Za-z0-9])[Ss](\d{1,2})[ ._\-]?[Ee][Pp]?(\d{1,3})(?!\d)", RegexOptions.Compiled),
        new(@"(?<![A-Za-z0-9])(\d{1,2})x(\d{2,3})(?!\d)", RegexOptions.Compiled),
        new(@"Season[ ._\-]?(\d{1,2})[ ._\-]*Episode[ ._\-]?(\d{1,3})", Ic),
    };

    private static readonly Regex[] SeasonOnly =
    {
        new(@"(?<![A-Za-z0-9])[Ss](\d{1,2})(?![0-9])", RegexOptions.Compiled),
        new(@"Season[ ._\-]?(\d{1,2})", Ic),
    };

    private static readonly Regex[] EpisodeOnly =
    {
        new(@"(?<![A-Za-z0-9])[Ee][Pp]?(\d{1,3})(?!\d)", RegexOptions.Compiled),
        new(@"Episode[ ._\-]?(\d{1,3})", Ic),
        new(@"^\s*(\d{1,3})(?!\d)", RegexOptions.Compiled),
    };

    private static readonly Regex YearRx = new(@"(?<![0-9])(19[2-9]\d|20[0-4]\d)(?![0-9])", RegexOptions.Compiled);

    private static readonly Regex Junk = new(
        @"(?<![A-Za-z0-9])(2160p|1080p|1080i|720p|576p|480p|4k|uhd|web[ ._\-]?dl|web[ ._\-]?rip|bluray|blu[ ._\-]ray|brrip|bdrip|bd[ ._\-]?rip|hdrip|dvdrip|dvdscr|hdtv|hdcam|camrip|predvd|x264|x265|h[ ._]?264|h[ ._]?265|hevc|remux|proper|repack|extended|unrated|directors[ ._]cut|imax|hdr10|hdr|dovi|10bit|8bit|amzn|dsnp|hmax|atvp|zee5|jhs|ddp?\d?[ ._]?\d?|aac|dts|truehd|atmos|esub|esubs|dual[ ._\-]audio|multi[ ._\-]audio|uncut|tamil|telugu|hindi|malayalam|kannada|eng|true)(?![A-Za-z0-9])",
        Ic);

    private static readonly Regex SeasonWord = new(@"^(season|s)\s*\d*$", Ic);
    private static readonly Regex Resolution = new(@"(?<![A-Za-z0-9])(2160p|4k|uhd|1080p|720p|576p|480p)(?![A-Za-z0-9])", Ic);

    /// <summary>Parses one name (a file name or a folder name).</summary>
    public static ParsedName Parse(string raw)
    {
        var name = Prep(raw);

        foreach (var p in EpPatterns)
        {
            var m = p.Match(name);
            if (!m.Success)
            {
                continue;
            }

            var before = name[..m.Index];
            var title = Clean(before);
            var ym = YearRx.Match(before);
            if (ym.Success && ym.Index > 0)
            {
                title = Clean(name[..ym.Index]);
            }

            return new ParsedName(
                title,
                ym.Success ? int.Parse(ym.Groups[1].Value) : null,
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value));
        }

        // Movie: title before the year.
        foreach (Match ym in YearRx.Matches(name))
        {
            if (ym.Index == 0)
            {
                continue;
            }

            var title = Clean(name[..ym.Index]);
            if (title.Length == 0)
            {
                continue;
            }

            return new ParsedName(title, int.Parse(ym.Groups[1].Value));
        }

        return new ParsedName(Clean(name));
    }

    /// <summary>
    /// Parses a file from its own name first, then its folders and the download name to fill in a
    /// missing title / season / episode.
    /// </summary>
    public static ParsedName ParseFile(string fileName, string fullPath, string itemName)
    {
        var f = Parse(fileName);
        if (f.IsEpisode && f.Title.Length >= 2)
        {
            return f;
        }

        var parts = fullPath.Split('/');
        var folders = parts.Length > 1 ? Enumerable.Reverse(parts[..^1]).ToList() : new List<string>();
        var contexts = folders.Append(itemName).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        if (f.IsEpisode)
        {
            foreach (var c in contexts)
            {
                var p = Parse(c);
                if (p.Title.Length >= 2 && !SeasonWord.IsMatch(p.Title.Trim()))
                {
                    return new ParsedName(p.Title, p.Year ?? f.Year, f.Season, f.Episode);
                }
            }

            return f;
        }

        // No SxxEyy in the file name: maybe a folder has the season and the file only the episode.
        int? season = null;
        string? showTitle = null;
        int? showYear = null;
        foreach (var c in contexts)
        {
            var prepped = Prep(c);
            foreach (var sp in SeasonOnly)
            {
                var m = sp.Match(prepped);
                if (!m.Success)
                {
                    continue;
                }

                season ??= int.Parse(m.Groups[1].Value);
                var t = Clean(prepped[..m.Index]);
                if (t.Length >= 2 && showTitle is null)
                {
                    showTitle = t;
                    var ym = YearRx.Match(prepped[..m.Index]);
                    if (ym.Success && ym.Index > 0)
                    {
                        showTitle = Clean(prepped[..ym.Index]);
                        showYear = int.Parse(ym.Groups[1].Value);
                    }
                }

                break;
            }

            if (season is not null && showTitle is not null)
            {
                break;
            }
        }

        if (season is not null)
        {
            var fn = Prep(fileName);
            foreach (var ep in EpisodeOnly)
            {
                var m = ep.Match(fn);
                if (!m.Success)
                {
                    continue;
                }

                var e = int.Parse(m.Groups[1].Value);
                if (e <= 0 || e >= 400)
                {
                    continue;
                }

                var title = showTitle;
                if (title is null)
                {
                    foreach (var c in contexts)
                    {
                        var p = Parse(c);
                        if (p.Title.Length >= 2 && !SeasonWord.IsMatch(p.Title.Trim()))
                        {
                            title = p.Title;
                            break;
                        }
                    }
                }

                if (title is not null)
                {
                    return new ParsedName(title, showYear, season, e);
                }
            }
        }

        // Movie. If the file name is weak (no year, very short), try the download name.
        if (f.Year is null || f.Title.Length < 2)
        {
            foreach (var c in contexts)
            {
                var p = Parse(c);
                if (p.Year is not null && p.Title.Length >= 2 && !p.IsEpisode)
                {
                    return p;
                }
            }
        }

        return f;
    }

    /// <summary>Key used to group files of the same title.</summary>
    public static string Normalize(string t) =>
        string.Concat(t.ToLowerInvariant().Replace("&", "and", StringComparison.Ordinal).Where(char.IsLetterOrDigit));

    /// <summary>Resolution label (2160p, 1080p, ...) for telling versions apart.</summary>
    public static string? ResolutionOf(string name)
    {
        var m = Resolution.Match(name);
        if (!m.Success)
        {
            return null;
        }

        var v = m.Groups[1].Value.ToLowerInvariant();
        return v is "4k" or "uhd" ? "2160p" : v;
    }

    /// <summary>Reads "Title (Year)" written by the user in a name fix.</summary>
    public static (string Title, int? Year) SplitTitleYear(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(.*?)\s*\((\d{4})\)\s*$");
        return m.Success ? (m.Groups[1].Value.Trim(), int.Parse(m.Groups[2].Value)) : (text.Trim(), null);
    }

    private static string Prep(string name)
    {
        var n = Ext.Replace(name.Trim(), string.Empty);
        for (var i = 0; i < 3; i++)
        {
            var before = n;
            n = SitePrefix.Replace(n, string.Empty, 1);
            n = GroupPrefix.Replace(n, string.Empty, 1);
            if (n == before)
            {
                break;
            }
        }

        return n;
    }

    private static string Clean(string s)
    {
        var t = s;
        var j = Junk.Match(t);
        if (j.Success && j.Index > 0)
        {
            t = t[..j.Index];
        }

        t = Regex.Replace(t, "[._]", " ");
        t = Regex.Replace(t, @"\[[^\]]*\]", " ");
        t = Regex.Replace(t, @"\(\s*\)", " ");
        t = Regex.Replace(t, @"[\(\[\{]+\s*$", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = Regex.Replace(t, @"^[\-–:\s]+|[\-–:\s]+$", string.Empty);
        return t.Trim();
    }
}
