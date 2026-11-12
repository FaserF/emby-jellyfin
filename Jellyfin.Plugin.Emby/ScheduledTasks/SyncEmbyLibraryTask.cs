using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Common;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.ScheduledTasks;

public class SyncEmbyLibraryTask : IScheduledTask
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncEmbyLibraryTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private static readonly Regex UnsafeFileChars = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);

    public SyncEmbyLibraryTask(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        ILogger<SyncEmbyLibraryTask> logger,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Sync Federated Emby Library to Jellyfin (.strm & .nfo)";
    public string Key => "SyncEmbyLibraryTask";
    public string Description => "Synchronizes Movies, Series, Seasons, and Episodes from the configured Emby server into lightweight .strm and .nfo pointers with smart local merging (Prio 1 Local, Prio 2 Stream).";
    public string Category => "Emby Federation";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(30).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(5);
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableLibraryFederation)
        {
            progress.Report(100);
            return;
        }

        using var client = new EmbyApiClient(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());
        var changes = await PerformSyncAsync(client, config, _logger, progress, cancellationToken, isManual: true, libraryManager: _libraryManager).ConfigureAwait(false);
        if (changes > 0)
        {
            _libraryManager.QueueLibraryScan();
            await TriggerSeerrScanAsync(client.HttpClient, config, _logger, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    public static async Task<int> PerformSyncAsync(
        EmbyApiClient client,
        PluginConfiguration config,
        ILogger logger,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        bool isManual = false,
        ILibraryManager? libraryManager = null)
    {
        if (!isManual && config.EnableIncrementalSync && config.LastSyncUtc.HasValue)
        {
            var interval = TimeSpan.FromHours(config.SyncIntervalHours > 0 ? config.SyncIntervalHours : 24);
            if (DateTime.UtcNow - config.LastSyncUtc.Value < interval)
            {
                logger.LogInformation("Emby library sync skipped: last sync was at {LastSync} UTC (protection interval is {Hours}h).", config.LastSyncUtc.Value, config.SyncIntervalHours);
                return 0;
            }
        }

        var (baseUrl, token, _, serverId) = await client.EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("Emby library sync skipped: Emby server URL or credentials are not configured.");
            return 0;
        }

        var rootDir = !string.IsNullOrWhiteSpace(config.StrmLibraryPath) ? config.StrmLibraryPath.Trim() : "/data/emby";
        var moviesDir = Path.Combine(rootDir, "movies");
        var tvDir = Path.Combine(rootDir, "tv");
        Directory.CreateDirectory(moviesDir);
        Directory.CreateDirectory(tvDir);

        var ignored = LoadIgnoredIdentifiers(rootDir, config);
        var changes = 0;
        var minDate = !isManual && config.EnableIncrementalSync ? config.LastSyncUtc : null;

        var localSeriesLocs = LocalLibraryMatcher.GetLocalLocations(libraryManager, "tvshows", "/data/tv");
        var localSeriesList = LocalLibraryMatcher.ScanLocalSeries(localSeriesLocs, logger);
        var localMovieLocs = LocalLibraryMatcher.GetLocalLocations(libraryManager, "movies", "/data/movies");
        var localMoviesList = LocalLibraryMatcher.ScanLocalMovies(localMovieLocs, logger);

        logger.LogInformation("Emby Federation: Found {LocalSeriesCount} local series across {LocCount} folders for smart merge.", localSeriesList.Count, localSeriesLocs.Count);

        if (config.EnableMoviesFederation)
        {
            var movies = await client.GetItemsAsync("Movie", minDateLastSaved: minDate, cancellationToken: cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Emby Federation: fetched {Count} movies (minDate: {MinDate})", movies.Count, minDate);
            if (config.ApiRequestDelayMs > 0)
            {
                await Task.Delay(config.ApiRequestDelayMs, cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(30);
            var activeMovieDirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var m in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(m.Id) || IsIgnored(m, ignored))
                {
                    continue;
                }

                var matchedLocal = LocalLibraryMatcher.MatchLocalMovie(m, localMoviesList);
                var folderName = BuildMovieFolderName(m);
                var embyMovieDir = Path.Combine(moviesDir, folderName);

                if (matchedLocal != null)
                {
                    if (Directory.Exists(embyMovieDir))
                    {
                        try
                        {
                            Directory.Delete(embyMovieDir, recursive: true);
                            changes++;
                            logger.LogInformation("Pruned duplicate Emby movie directory {Path} (Prio 1 local copy exists at {LocalPath})", embyMovieDir, matchedLocal.Path);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Could not delete merged Emby movie dir {Path}", embyMovieDir);
                        }
                    }
                    continue;
                }

                activeMovieDirs.Add(folderName);
                var streamUrl = BuildStrmContent(config, baseUrl, token, m.Id);
                if (WriteIfChanged(Path.Combine(embyMovieDir, $"{folderName}.strm"), streamUrl, logger))
                {
                    changes++;
                }

                if (WriteIfChanged(Path.Combine(embyMovieDir, $"{folderName}.nfo"), FormatMovieNfo(m, baseUrl, serverId), logger))
                {
                    changes++;
                }
            }

            if (minDate == null && activeMovieDirs.Count > 0)
            {
                var existingDirs = Directory.GetDirectories(moviesDir);
                if (existingDirs.Length > 20 && activeMovieDirs.Count < (existingDirs.Length / 2))
                {
                    logger.LogWarning(
                        "Emby Federation: Movie prune aborted! Fetched {ActiveCount} but library has {ExistingCount} directories.",
                        activeMovieDirs.Count, existingDirs.Length);
                }
                else
                {
                    foreach (var dir in existingDirs)
                    {
                        var dName = Path.GetFileName(dir);
                        if (!activeMovieDirs.Contains(dName))
                        {
                            try
                            {
                                Directory.Delete(dir, recursive: true);
                                changes++;
                            }
                            catch
                            {
                                // Ignore deletion errors
                            }
                        }
                    }
                }
            }

            var colChanges = await Jellyfin.Plugin.Emby.Services.EmbyBoxSetSyncService.SyncCollectionsAsync(
                client, baseUrl, token, movies, moviesDir, logger, cancellationToken).ConfigureAwait(false);
            changes += colChanges;
        }

        progress?.Report(55);

        if (config.EnableSeriesFederation)
        {
            var seriesList = await client.GetItemsAsync("Series", minDateLastSaved: minDate, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (config.ApiRequestDelayMs > 0)
            {
                await Task.Delay(config.ApiRequestDelayMs, cancellationToken).ConfigureAwait(false);
            }
            var episodes = await client.GetItemsAsync("Episode", minDateLastSaved: minDate, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (config.ApiRequestDelayMs > 0)
            {
                await Task.Delay(config.ApiRequestDelayMs, cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(80);

            var seriesMap = new Dictionary<string, (string Path, bool IsMerged, HashSet<(int, int)> LocalVideos)>(StringComparer.OrdinalIgnoreCase);
            var activeSeriesDirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var s in seriesList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(s.Id) || IsIgnored(s, ignored))
                {
                    continue;
                }

                var matchedLocal = LocalLibraryMatcher.MatchLocalSeries(s, localSeriesList);
                if (matchedLocal != null)
                {
                    var sDir = matchedLocal.Path;
                    var localVideos = LocalLibraryMatcher.IndexLocalVideos(sDir);
                    seriesMap[s.Id] = (sDir, true, localVideos);

                    var embyDuplicateDir = Path.Combine(tvDir, BuildSeriesFolderName(s));
                    if (Directory.Exists(embyDuplicateDir))
                    {
                        try
                        {
                            Directory.Delete(embyDuplicateDir, recursive: true);
                            changes++;
                            logger.LogInformation("Deleted duplicate Emby series dir {Path} - merged into local {LocalPath}", embyDuplicateDir, sDir);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Could not delete old Emby dir {Path}", embyDuplicateDir);
                        }
                    }

                    // Look for any other lingering folder for this series id in tvDir
                    try
                    {
                        foreach (var existingFolder in Directory.GetDirectories(tvDir))
                        {
                            if (existingFolder.Contains($"[EmbySeries-{s.Id}]", StringComparison.OrdinalIgnoreCase))
                            {
                                Directory.Delete(existingFolder, recursive: true);
                                changes++;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }

                    var localNfo = Path.Combine(sDir, "tvshow.nfo");
                    if (!File.Exists(localNfo))
                    {
                        if (WriteIfChanged(localNfo, FormatSeriesNfo(s, baseUrl, serverId)))
                        {
                            changes++;
                        }
                    }
                }
                else
                {
                    var folderName = BuildSeriesFolderName(s);
                    activeSeriesDirs.Add(folderName);
                    var sDir = Path.Combine(tvDir, folderName);
                    seriesMap[s.Id] = (sDir, false, new HashSet<(int, int)>());

                    if (WriteIfChanged(Path.Combine(sDir, "tvshow.nfo"), FormatSeriesNfo(s, baseUrl, serverId)))
                    {
                        changes++;
                    }
                }
            }

            foreach (var ep in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(ep.Id))
                {
                    continue;
                }

                var sid = ep.SeriesId ?? string.Empty;
                string sDir;
                bool isMerged = false;
                HashSet<(int, int)> localVideos;

                if (seriesMap.TryGetValue(sid, out var seriesInfo))
                {
                    sDir = seriesInfo.Path;
                    isMerged = seriesInfo.IsMerged;
                    localVideos = seriesInfo.LocalVideos;
                }
                else
                {
                    var fallbackFolder = $"{SanitizeFileName(ep.SeriesName ?? "Unknown Series")} [embyseriesid-{(!string.IsNullOrWhiteSpace(sid) ? sid : ep.Id)}]";
                    activeSeriesDirs.Add(fallbackFolder);
                    sDir = Path.Combine(tvDir, fallbackFolder);
                    localVideos = new HashSet<(int, int)>();
                    seriesMap[sid] = (sDir, false, localVideos);
                }

                var seasonNum = ep.ParentIndexNumber ?? 1;
                var epNum = ep.IndexNumber ?? 1;
                var seasonDir = LocalLibraryMatcher.ResolveSeasonDirectory(sDir, seasonNum);

                if (isMerged && localVideos.Contains((seasonNum, epNum)))
                {
                    // Prio 1: Local video exists! Clean up any stale strm and skip
                    LocalLibraryMatcher.CleanupStaleStrm(seasonDir, seasonNum, epNum, logger);
                    continue;
                }

                // Prio 2: Emby stream pointer
                var epStem = $"S{seasonNum:D2}E{epNum:D2} - {SanitizeFileName(ep.Name)} [EmbyEpisode-{ep.Id}]";
                var streamUrl = BuildStrmContent(config, baseUrl, token, ep.Id);

                if (WriteIfChanged(Path.Combine(seasonDir, $"{epStem}.strm"), streamUrl))
                {
                    changes++;
                }

                if (WriteIfChanged(Path.Combine(seasonDir, $"{epStem}.nfo"), FormatEpisodeNfo(ep)))
                {
                    changes++;
                }
            }

            if (minDate == null && activeSeriesDirs.Count > 0)
            {
                var existingDirs = Directory.GetDirectories(tvDir);
                if (existingDirs.Length > 20 && activeSeriesDirs.Count < (existingDirs.Length / 2))
                {
                    logger.LogWarning(
                        "Emby Federation: Series prune aborted! Fetched {ActiveCount} but library has {ExistingCount} directories.",
                        activeSeriesDirs.Count, existingDirs.Length);
                }
                else
                {
                    foreach (var dir in existingDirs)
                    {
                        var dName = Path.GetFileName(dir);
                        if (!activeSeriesDirs.Contains(dName))
                        {
                            try
                            {
                                Directory.Delete(dir, recursive: true);
                                changes++;
                            }
                            catch
                            {
                                // Ignore deletion errors
                            }
                        }
                    }
                }
            }
        }

        logger.LogInformation("Emby Federation sync completed with {Changes} file updates.", changes);
        return changes;
    }

    private static string BuildStrmContent(PluginConfiguration config, string baseUrl, string token, string embyItemId)
    {
        if (config.UseDirectEmbyStreamUrls)
        {
            return $"{baseUrl.TrimEnd('/')}/emby/Videos/{Uri.EscapeDataString(embyItemId)}/stream?static=true&api_key={Uri.EscapeDataString(token)}\n";
        }

        var pubUrl = config.GetPublicServerUrl();
        return $"{pubUrl.TrimEnd('/')}/api/embyPlugin/stream/{Uri.EscapeDataString(embyItemId)}/stream.mkv\n";
    }

    public static string BuildMovieFolderName(EmbyItemDto m)
    {
        var baseName = SanitizeFileName(m.Name);
        var yearPart = m.ProductionYear.HasValue ? $" ({m.ProductionYear.Value})" : string.Empty;
        var tmdb = m.GetProviderId("Tmdb");
        var imdb = m.GetProviderId("Imdb");
        var idTag = !string.IsNullOrWhiteSpace(tmdb)
            ? $" [tmdbid-{tmdb}]"
            : !string.IsNullOrWhiteSpace(imdb)
                ? $" [imdbid-{imdb}]"
                : string.Empty;
        return $"{baseName}{yearPart}{idTag} [EmbyMovie-{m.Id}]";
    }

    public static string BuildSeriesFolderName(EmbyItemDto s)
    {
        var baseName = SanitizeFileName(s.Name);
        var yearPart = s.ProductionYear.HasValue ? $" ({s.ProductionYear.Value})" : string.Empty;
        var tvdb = s.GetProviderId("Tvdb");
        var tmdb = s.GetProviderId("Tmdb");
        var idTag = !string.IsNullOrWhiteSpace(tvdb)
            ? $" [tvdbid-{tvdb}]"
            : !string.IsNullOrWhiteSpace(tmdb)
                ? $" [tmdbid-{tmdb}]"
                : string.Empty;
        return $"{baseName}{yearPart}{idTag} [EmbySeries-{s.Id}]";
    }

    public static string SanitizeFileName(string? name)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
        var cleaned = UnsafeFileChars.Replace(raw, " - ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '.');
        return cleaned.Length > 140 ? cleaned[..140].Trim() : (string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned);
    }

    public static bool WriteIfChanged(string path, string content, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path, Encoding.UTF8);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, content, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to write file {Path}", path);
            return false;
        }
    }

    public static string FormatMovieNfo(EmbyItemDto item, string baseUrl, string serverId) =>
        EmbyNfoFormatter.FormatMovieNfo(item, baseUrl, serverId);

    public static string FormatSeriesNfo(EmbyItemDto item, string baseUrl, string serverId) =>
        EmbyNfoFormatter.FormatSeriesNfo(item, baseUrl, serverId);

    public static string FormatEpisodeNfo(EmbyItemDto item) =>
        EmbyNfoFormatter.FormatEpisodeNfo(item);

    private static async Task TriggerSeerrScanAsync(HttpClient http, PluginConfiguration config, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            return;
        }

        try
        {
            var url = $"{config.SeerrUrl.TrimEnd('/')}/api/v1/settings/jobs/jellyfin-recently-added-scan/run";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("X-Api-Key", config.SeerrApiKey.Trim());
            using var resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Triggered Seerr Jellyfin recently-added scan (HTTP {Status})", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not trigger Seerr scan after Emby sync");
        }
    }

    private static HashSet<string> LoadIgnoredIdentifiers(string rootDir, PluginConfiguration config)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(config.ExcludedTmdbIds))
        {
            foreach (var p in config.ExcludedTmdbIds.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ignored.Add(p.Trim());
            }
        }

        var ignoreFilePath = Path.Combine(rootDir, ".embyignore");
        if (File.Exists(ignoreFilePath))
        {
            try
            {
                foreach (var line in File.ReadAllLines(ignoreFilePath))
                {
                    var trimmed = line.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith('#'))
                    {
                        ignored.Add(trimmed);
                    }
                }
            }
            catch
            {
                // Ignore read errors
            }
        }

        return ignored;
    }

    private static bool IsIgnored(EmbyItemDto item, HashSet<string> ignored)
    {
        if (ignored.Count == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.Id) && ignored.Contains(item.Id))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(item.Name) && ignored.Contains(item.Name))
        {
            return true;
        }

        if (item.GetProviderId("Tmdb") is { } tmdb && ignored.Contains(tmdb))
        {
            return true;
        }

        if (item.GetProviderId("Imdb") is { } imdb && ignored.Contains(imdb))
        {
            return true;
        }

        if (item.GetProviderId("Tvdb") is { } tvdb && ignored.Contains(tvdb))
        {
            return true;
        }

        return false;
    }
}
