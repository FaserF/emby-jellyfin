using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Emby.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Common;

public class LocalMediaInfo
{
    public string Path { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string NormalizedTitle { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? TvdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
}

public static class LocalLibraryMatcher
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".flv", ".iso"
    };

    private static readonly Regex SeasonEpRegex = new(@"[sS](?<season>\d{1,2})[eE](?<episode>\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex SeasonDirRegex = new(@"Season\s*(?<season>\d{1,2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EpOnlyRegex = new(@"[eE](?<episode>\d{1,3})|\b(?<episode>\d{1,3})\s*-", RegexOptions.Compiled);

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var clean = Regex.Replace(title, @"\s*\[.*?\]", "");
        clean = Regex.Replace(clean, @"\s*\(\d{4}\).*", "");
        clean = Regex.Replace(clean, @"[\._]", " ");
        clean = Regex.Replace(clean, @"\b(19\d\d|20\d\d)\b.*", "");
        clean = Regex.Replace(clean, @"[^a-zA-Z0-9]+", " ").Trim().ToLowerInvariant();
        return clean;
    }

    public static List<string> GetLocalLocations(ILibraryManager? libraryManager, string collectionType, string defaultPath)
    {
        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (libraryManager != null)
        {
            try
            {
                var folders = libraryManager.GetVirtualFolders();
                foreach (var vf in folders)
                {
                    var isTypeMatch = string.Equals(vf.CollectionType?.ToString(), collectionType, StringComparison.OrdinalIgnoreCase);
                    var isEmbyLib = vf.Name.Contains("Emby", StringComparison.OrdinalIgnoreCase);
                    if (isTypeMatch && !isEmbyLib && vf.Locations != null)
                    {
                        foreach (var loc in vf.Locations)
                        {
                            if (!string.IsNullOrWhiteSpace(loc) && !loc.Contains("/emby", StringComparison.OrdinalIgnoreCase) && Directory.Exists(loc))
                            {
                                list.Add(loc);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore failure
            }
        }

        if (list.Count == 0 && Directory.Exists(defaultPath))
        {
            list.Add(defaultPath);
        }

        var dellPath = defaultPath.Replace("/data/", "/data/dell/");
        if (Directory.Exists(dellPath))
        {
            list.Add(dellPath);
        }

        return list.ToList();
    }

    public static List<LocalMediaInfo> ScanLocalSeries(List<string> locations, ILogger logger)
    {
        var result = new List<LocalMediaInfo>();
        foreach (var loc in locations)
        {
            try
            {
                if (!Directory.Exists(loc))
                {
                    continue;
                }

                foreach (var dir in Directory.GetDirectories(loc))
                {
                    try
                    {
                        var dirName = Path.GetFileName(dir);
                        var info = new LocalMediaInfo
                        {
                            Path = dir,
                            FolderName = dirName,
                            NormalizedTitle = NormalizeTitle(dirName)
                        };

                        var yearMatch = Regex.Match(dirName, @"\((19\d\d|20\d\d)\)");
                        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var y))
                        {
                            info.Year = y;
                        }

                        var nfoPath = Path.Combine(dir, "tvshow.nfo");
                        if (File.Exists(nfoPath))
                        {
                            try
                            {
                                var nfoContent = File.ReadAllText(nfoPath, Encoding.UTF8);
                                var tmdb = Regex.Match(nfoContent, @"<tmdbid>(\d+)</tmdbid>", RegexOptions.IgnoreCase);
                                if (tmdb.Success) info.TmdbId = tmdb.Groups[1].Value;

                                var tvdb = Regex.Match(nfoContent, @"<(?:tvdbid|id)>(\d+)</(?:tvdbid|id)>", RegexOptions.IgnoreCase);
                                if (tvdb.Success) info.TvdbId = tvdb.Groups[1].Value;

                                var imdb = Regex.Match(nfoContent, @"<(?:imdb_id|imdbid)>(tt\d+)</(?:imdb_id|imdbid)>", RegexOptions.IgnoreCase);
                                if (imdb.Success) info.ImdbId = imdb.Groups[1].Value;

                                var titleMatch = Regex.Match(nfoContent, @"<title>(.*?)</title>", RegexOptions.IgnoreCase);
                                if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
                                {
                                    info.NormalizedTitle = NormalizeTitle(WebUtility.HtmlDecode(titleMatch.Groups[1].Value));
                                }

                                var yearNfo = Regex.Match(nfoContent, @"<year>(\d{4})</year>", RegexOptions.IgnoreCase);
                                if (yearNfo.Success && int.TryParse(yearNfo.Groups[1].Value, out var yn))
                                {
                                    info.Year = yn;
                                }
                            }
                            catch
                            {
                                // ignore nfo parse
                            }
                        }

                        result.Add(info);
                    }
                    catch
                    {
                        // ignore dir read
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to scan local series in {Loc}", loc);
            }
        }

        return result;
    }

    public static List<LocalMediaInfo> ScanLocalMovies(List<string> locations, ILogger logger)
    {
        var result = new List<LocalMediaInfo>();
        foreach (var loc in locations)
        {
            try
            {
                if (!Directory.Exists(loc))
                {
                    continue;
                }

                foreach (var dir in Directory.GetDirectories(loc))
                {
                    try
                    {
                        var dirName = Path.GetFileName(dir);
                        var info = new LocalMediaInfo
                        {
                            Path = dir,
                            FolderName = dirName,
                            NormalizedTitle = NormalizeTitle(dirName)
                        };

                        var yearMatch = Regex.Match(dirName, @"\((19\d\d|20\d\d)\)");
                        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var y))
                        {
                            info.Year = y;
                        }

                        // Check if any video file exists in this directory or subdirectories
                        var hasVideo = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                            .Any(f => VideoExtensions.Contains(Path.GetExtension(f)));

                        if (!hasVideo)
                        {
                            continue;
                        }

                        var nfoFiles = Directory.GetFiles(dir, "*.nfo");
                        foreach (var nfoPath in nfoFiles)
                        {
                            try
                            {
                                var nfoContent = File.ReadAllText(nfoPath, Encoding.UTF8);
                                var tmdb = Regex.Match(nfoContent, @"<tmdbid>(\d+)</tmdbid>", RegexOptions.IgnoreCase);
                                if (tmdb.Success) info.TmdbId = tmdb.Groups[1].Value;

                                var imdb = Regex.Match(nfoContent, @"<(?:imdb_id|imdbid)>(tt\d+)</(?:imdb_id|imdbid)>", RegexOptions.IgnoreCase);
                                if (imdb.Success) info.ImdbId = imdb.Groups[1].Value;

                                var titleMatch = Regex.Match(nfoContent, @"<title>(.*?)</title>", RegexOptions.IgnoreCase);
                                if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
                                {
                                    info.NormalizedTitle = NormalizeTitle(WebUtility.HtmlDecode(titleMatch.Groups[1].Value));
                                }

                                var yearNfo = Regex.Match(nfoContent, @"<year>(\d{4})</year>", RegexOptions.IgnoreCase);
                                if (yearNfo.Success && int.TryParse(yearNfo.Groups[1].Value, out var yn))
                                {
                                    info.Year = yn;
                                }
                            }
                            catch
                            {
                                // ignore
                            }
                        }

                        result.Add(info);
                    }
                    catch
                    {
                        // ignore dir read
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to scan local movies in {Loc}", loc);
            }
        }

        return result;
    }

    public static LocalMediaInfo? MatchLocalSeries(EmbyItemDto s, List<LocalMediaInfo> localSeries)
    {
        var sTmdb = s.GetProviderId("Tmdb");
        var sTvdb = s.GetProviderId("Tvdb");
        var sImdb = s.GetProviderId("Imdb");
        var sNorm = NormalizeTitle(s.Name);

        foreach (var local in localSeries)
        {
            if (!string.IsNullOrWhiteSpace(sTmdb) && !string.IsNullOrWhiteSpace(local.TmdbId) &&
                string.Equals(sTmdb, local.TmdbId, StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            if (!string.IsNullOrWhiteSpace(sTvdb) && !string.IsNullOrWhiteSpace(local.TvdbId) &&
                string.Equals(sTvdb, local.TvdbId, StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            if (!string.IsNullOrWhiteSpace(sImdb) && !string.IsNullOrWhiteSpace(local.ImdbId) &&
                string.Equals(sImdb, local.ImdbId, StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            if (!string.IsNullOrWhiteSpace(sNorm) && sNorm.Length >= 3 &&
                string.Equals(sNorm, local.NormalizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                if (s.ProductionYear.HasValue && local.Year.HasValue)
                {
                    if (Math.Abs(s.ProductionYear.Value - local.Year.Value) <= 1)
                    {
                        return local;
                    }
                }
                else
                {
                    return local;
                }
            }
        }

        return null;
    }

    public static LocalMediaInfo? MatchLocalMovie(EmbyItemDto m, List<LocalMediaInfo> localMovies)
    {
        var mTmdb = m.GetProviderId("Tmdb");
        var mImdb = m.GetProviderId("Imdb");
        var mNorm = NormalizeTitle(m.Name);

        foreach (var local in localMovies)
        {
            if (!string.IsNullOrWhiteSpace(mTmdb) && !string.IsNullOrWhiteSpace(local.TmdbId) &&
                string.Equals(mTmdb, local.TmdbId, StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            if (!string.IsNullOrWhiteSpace(mImdb) && !string.IsNullOrWhiteSpace(local.ImdbId) &&
                string.Equals(mImdb, local.ImdbId, StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            if (!string.IsNullOrWhiteSpace(mNorm) && mNorm.Length >= 3 &&
                string.Equals(mNorm, local.NormalizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                if (m.ProductionYear.HasValue && local.Year.HasValue)
                {
                    if (Math.Abs(m.ProductionYear.Value - local.Year.Value) <= 1)
                    {
                        return local;
                    }
                }
                else
                {
                    return local;
                }
            }
        }

        return null;
    }

    public static HashSet<(int Season, int Episode)> IndexLocalVideos(string seriesDir)
    {
        var set = new HashSet<(int, int)>();
        if (!Directory.Exists(seriesDir))
        {
            return set;
        }

        try
        {
            var files = Directory.EnumerateFiles(seriesDir, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (!VideoExtensions.Contains(ext))
                {
                    continue;
                }

                var fileName = Path.GetFileNameWithoutExtension(file);
                var parentDir = Path.GetFileName(Path.GetDirectoryName(file) ?? string.Empty);

                var matchSE = SeasonEpRegex.Matches(fileName);
                if (matchSE.Count > 0)
                {
                    foreach (Match m in matchSE)
                    {
                        if (int.TryParse(m.Groups["season"].Value, out var s) &&
                            int.TryParse(m.Groups["episode"].Value, out var e))
                        {
                            set.Add((s, e));
                        }
                    }
                    continue;
                }

                var dirMatch = SeasonDirRegex.Match(parentDir);
                if (dirMatch.Success && int.TryParse(dirMatch.Groups["season"].Value, out var seasonFromDir))
                {
                    var epMatches = EpOnlyRegex.Matches(fileName);
                    foreach (Match em in epMatches)
                    {
                        if (int.TryParse(em.Groups["episode"].Value, out var ep))
                        {
                            set.Add((seasonFromDir, ep));
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore enumeration errors
        }

        return set;
    }

    public static void CleanupStaleStrm(string seasonDir, int seasonNum, int epNum, ILogger logger)
    {
        if (!Directory.Exists(seasonDir)) return;
        try
        {
            var files = Directory.GetFiles(seasonDir, "*.strm");
            foreach (var f in files)
            {
                var fName = Path.GetFileName(f);
                var m = SeasonEpRegex.Match(fName);
                if (m.Success &&
                    int.TryParse(m.Groups["season"].Value, out var s) &&
                    int.TryParse(m.Groups["episode"].Value, out var e) &&
                    s == seasonNum && e == epNum)
                {
                    try
                    {
                        File.Delete(f);
                        var nfo = Path.ChangeExtension(f, ".nfo");
                        if (File.Exists(nfo))
                        {
                            File.Delete(nfo);
                        }
                        logger.LogInformation("Deleted stale .strm for local episode S{Season:D2}E{Episode:D2}: {Path}", s, e, f);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Failed to delete stale strm {Path}", f);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }
}
