using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Emby.Api;
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
    public string Description => "Synchronizes Movies, Series, Seasons, and Episodes from the configured Emby server into lightweight .strm and .nfo pointers (with TMDb/TVDb/IMDb IDs) and triggers Jellyfin & Seerr scans.";
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
        var changes = await PerformSyncAsync(client, config, _logger, progress, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
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

        if (config.EnableMoviesFederation)
        {
            var movies = await client.GetItemsAsync("Movie", cancellationToken: cancellationToken).ConfigureAwait(false);
            progress?.Report(30);
            var activeMovieDirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var m in movies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(m.Id) || IsIgnored(m, ignored))
                {
                    continue;
                }

                var folderName = BuildMovieFolderName(m);
                activeMovieDirs.Add(folderName);
                var mDir = Path.Combine(moviesDir, folderName);

                var streamUrl = BuildStrmContent(config, baseUrl, token, m.Id);
                if (WriteIfChanged(Path.Combine(mDir, $"{folderName}.strm"), streamUrl))
                {
                    changes++;
                }

                if (WriteIfChanged(Path.Combine(mDir, $"{folderName}.nfo"), FormatMovieNfo(m, baseUrl, serverId)))
                {
                    changes++;
                }
            }

            foreach (var dir in Directory.GetDirectories(moviesDir))
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

        progress?.Report(55);

        if (config.EnableSeriesFederation)
        {
            var seriesList = await client.GetItemsAsync("Series", cancellationToken: cancellationToken).ConfigureAwait(false);
            var episodes = await client.GetItemsAsync("Episode", cancellationToken: cancellationToken).ConfigureAwait(false);
            progress?.Report(80);

            var seriesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var activeSeriesDirs = new HashSet<string>(StringComparer.Ordinal);

            foreach (var s in seriesList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(s.Id) || IsIgnored(s, ignored))
                {
                    continue;
                }

                var folderName = BuildSeriesFolderName(s);
                activeSeriesDirs.Add(folderName);
                var sDir = Path.Combine(tvDir, folderName);
                seriesMap[s.Id] = sDir;

                if (WriteIfChanged(Path.Combine(sDir, "tvshow.nfo"), FormatSeriesNfo(s, baseUrl, serverId)))
                {
                    changes++;
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
                if (!seriesMap.TryGetValue(sid, out var sDir))
                {
                    var fallbackFolder = $"{SanitizeFileName(ep.SeriesName ?? "Unknown Series")} [embyseriesid-{(!string.IsNullOrWhiteSpace(sid) ? sid : ep.Id)}]";
                    activeSeriesDirs.Add(fallbackFolder);
                    sDir = Path.Combine(tvDir, fallbackFolder);
                    seriesMap[sid] = sDir;
                }

                var seasonNum = ep.ParentIndexNumber ?? 1;
                var epNum = ep.IndexNumber ?? 1;
                var seasonDir = Path.Combine(sDir, $"Season {seasonNum:D2}");
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

            foreach (var dir in Directory.GetDirectories(tvDir))
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

    private static bool WriteIfChanged(string path, string content)
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
        catch
        {
            return false;
        }
    }

    public static string FormatMovieNfo(EmbyItemDto item, string baseUrl, string serverId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
        sb.AppendLine("<movie>");
        sb.AppendLine($"  <title>{WebUtility.HtmlEncode(item.Name)}</title>");
        if (item.ProductionYear.HasValue)
        {
            sb.AppendLine($"  <year>{item.ProductionYear.Value}</year>");
        }

        var effSrv = !string.IsNullOrWhiteSpace(item.ServerId) ? item.ServerId : serverId;
        var webLink = $"{baseUrl.TrimEnd('/')}/web/index.html#!/item?id={Uri.EscapeDataString(item.Id)}&serverId={Uri.EscapeDataString(effSrv)}";

        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            sb.AppendLine($"  <plot>{WebUtility.HtmlEncode(item.Overview.Trim())}</plot>");
        }

        sb.AppendLine($"  <website>{WebUtility.HtmlEncode(webLink)}</website>");
        sb.AppendLine($"  <homepage>{WebUtility.HtmlEncode(webLink)}</homepage>");

        if (item.GetProviderId("Tmdb") is { } tmdb)
        {
            sb.AppendLine($"  <tmdbid>{WebUtility.HtmlEncode(tmdb)}</tmdbid>");
            sb.AppendLine($"  <uniqueid type=\"tmdb\" default=\"true\">{WebUtility.HtmlEncode(tmdb)}</uniqueid>");
        }

        if (item.GetProviderId("Imdb") is { } imdb)
        {
            sb.AppendLine($"  <imdbid>{WebUtility.HtmlEncode(imdb)}</imdbid>");
            sb.AppendLine($"  <uniqueid type=\"imdb\">{WebUtility.HtmlEncode(imdb)}</uniqueid>");
        }

        if (item.GetProviderId("Tvdb") is { } tvdb)
        {
            sb.AppendLine($"  <tvdbid>{WebUtility.HtmlEncode(tvdb)}</tvdbid>");
            sb.AppendLine($"  <uniqueid type=\"tvdb\">{WebUtility.HtmlEncode(tvdb)}</uniqueid>");
        }

        sb.AppendLine($"  <uniqueid type=\"EmbyMovie\">{WebUtility.HtmlEncode(item.Id)}</uniqueid>");
        sb.AppendLine($"  <uniqueid type=\"EmbyItem\">{WebUtility.HtmlEncode(item.Id)}</uniqueid>");

        if (item.Genres != null)
        {
            foreach (var g in item.Genres)
            {
                sb.AppendLine($"  <genre>{WebUtility.HtmlEncode(g)}</genre>");
            }
        }

        sb.AppendLine("  <tag>Emby-Federated</tag>");
        AppendFileInfoXml(sb, item);
        sb.AppendLine("</movie>");
        return sb.ToString();
    }

    public static string FormatSeriesNfo(EmbyItemDto item, string baseUrl, string serverId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
        sb.AppendLine("<tvshow>");
        sb.AppendLine($"  <title>{WebUtility.HtmlEncode(item.Name)}</title>");
        if (item.ProductionYear.HasValue)
        {
            sb.AppendLine($"  <year>{item.ProductionYear.Value}</year>");
        }

        var effSrv = !string.IsNullOrWhiteSpace(item.ServerId) ? item.ServerId : serverId;
        var webLink = $"{baseUrl.TrimEnd('/')}/web/index.html#!/item?id={Uri.EscapeDataString(item.Id)}&serverId={Uri.EscapeDataString(effSrv)}";

        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            sb.AppendLine($"  <plot>{WebUtility.HtmlEncode(item.Overview.Trim())}</plot>");
        }

        sb.AppendLine($"  <website>{WebUtility.HtmlEncode(webLink)}</website>");
        sb.AppendLine($"  <homepage>{WebUtility.HtmlEncode(webLink)}</homepage>");

        if (item.GetProviderId("Tvdb") is { } tvdb)
        {
            sb.AppendLine($"  <id>{WebUtility.HtmlEncode(tvdb)}</id>");
            sb.AppendLine($"  <tvdbid>{WebUtility.HtmlEncode(tvdb)}</tvdbid>");
            sb.AppendLine($"  <uniqueid type=\"tvdb\" default=\"true\">{WebUtility.HtmlEncode(tvdb)}</uniqueid>");
        }

        if (item.GetProviderId("Tmdb") is { } tmdb)
        {
            sb.AppendLine($"  <tmdbid>{WebUtility.HtmlEncode(tmdb)}</tmdbid>");
            sb.AppendLine($"  <uniqueid type=\"tmdb\">{WebUtility.HtmlEncode(tmdb)}</uniqueid>");
        }

        if (item.GetProviderId("Imdb") is { } imdb)
        {
            sb.AppendLine($"  <imdb_id>{WebUtility.HtmlEncode(imdb)}</imdb_id>");
            sb.AppendLine($"  <uniqueid type=\"imdb\">{WebUtility.HtmlEncode(imdb)}</uniqueid>");
        }

        sb.AppendLine($"  <uniqueid type=\"EmbySeries\">{WebUtility.HtmlEncode(item.Id)}</uniqueid>");
        sb.AppendLine("  <tag>Emby-Federated</tag>");
        sb.AppendLine("</tvshow>");
        return sb.ToString();
    }

    public static string FormatEpisodeNfo(EmbyItemDto item)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
        sb.AppendLine("<episodedetails>");
        sb.AppendLine($"  <title>{WebUtility.HtmlEncode(item.Name)}</title>");
        sb.AppendLine($"  <season>{item.ParentIndexNumber ?? 1}</season>");
        sb.AppendLine($"  <episode>{item.IndexNumber ?? 1}</episode>");
        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            sb.AppendLine($"  <plot>{WebUtility.HtmlEncode(item.Overview)}</plot>");
        }

        if (item.GetProviderId("Tvdb") is { } tvdb)
        {
            sb.AppendLine($"  <uniqueid type=\"tvdb\" default=\"true\">{WebUtility.HtmlEncode(tvdb)}</uniqueid>");
        }

        if (item.GetProviderId("Tmdb") is { } tmdb)
        {
            sb.AppendLine($"  <uniqueid type=\"tmdb\">{WebUtility.HtmlEncode(tmdb)}</uniqueid>");
        }

        if (item.GetProviderId("Imdb") is { } imdb)
        {
            sb.AppendLine($"  <uniqueid type=\"imdb\">{WebUtility.HtmlEncode(imdb)}</uniqueid>");
        }

        sb.AppendLine($"  <uniqueid type=\"EmbyEpisode\">{WebUtility.HtmlEncode(item.Id)}</uniqueid>");
        AppendFileInfoXml(sb, item);
        sb.AppendLine("</episodedetails>");
        return sb.ToString();
    }

    private static void AppendFileInfoXml(StringBuilder sb, EmbyItemDto item)
    {
        var ms = item.MediaSources?.FirstOrDefault();
        if (ms?.MediaStreams == null || ms.MediaStreams.Count == 0)
        {
            return;
        }

        var ticks = ms.RunTimeTicks ?? item.RunTimeTicks ?? 0;
        var durSec = ticks > 0 ? ticks / 10_000_000 : 0;

        sb.AppendLine("  <fileinfo>");
        sb.AppendLine("    <streamdetails>");
        foreach (var st in ms.MediaStreams)
        {
            if (string.Equals(st.Type, "Video", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("      <video>");
                if (!string.IsNullOrWhiteSpace(st.Codec))
                {
                    sb.AppendLine($"        <codec>{WebUtility.HtmlEncode(st.Codec)}</codec>");
                }

                if (st.Width.HasValue && st.Height.HasValue)
                {
                    sb.AppendLine($"        <width>{st.Width.Value}</width>");
                    sb.AppendLine($"        <height>{st.Height.Value}</height>");
                }

                if (durSec > 0)
                {
                    sb.AppendLine($"        <durationinseconds>{durSec}</durationinseconds>");
                }

                sb.AppendLine("      </video>");
            }
            else if (string.Equals(st.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("      <audio>");
                if (!string.IsNullOrWhiteSpace(st.Codec))
                {
                    sb.AppendLine($"        <codec>{WebUtility.HtmlEncode(st.Codec)}</codec>");
                }

                if (!string.IsNullOrWhiteSpace(st.Language))
                {
                    sb.AppendLine($"        <language>{WebUtility.HtmlEncode(st.Language)}</language>");
                }

                if (!string.IsNullOrWhiteSpace(st.Title ?? st.DisplayTitle))
                {
                    sb.AppendLine($"        <title>{WebUtility.HtmlEncode(st.DisplayTitle ?? st.Title)}</title>");
                }

                sb.AppendLine($"        <channels>{st.Channels ?? 2}</channels>");
                if (st.BitRate.HasValue)
                {
                    sb.AppendLine($"        <bitrate>{st.BitRate.Value}</bitrate>");
                }

                sb.AppendLine("      </audio>");
            }
            else if (string.Equals(st.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("      <subtitle>");
                if (!string.IsNullOrWhiteSpace(st.Language))
                {
                    sb.AppendLine($"        <language>{WebUtility.HtmlEncode(st.Language)}</language>");
                }

                if (!string.IsNullOrWhiteSpace(st.Codec))
                {
                    sb.AppendLine($"        <format>{WebUtility.HtmlEncode(st.Codec)}</format>");
                }

                if (!string.IsNullOrWhiteSpace(st.Title ?? st.DisplayTitle))
                {
                    sb.AppendLine($"        <title>{WebUtility.HtmlEncode(st.DisplayTitle ?? st.Title)}</title>");
                }

                if (st.IsForced)
                {
                    sb.AppendLine("        <forced>true</forced>");
                }

                sb.AppendLine("      </subtitle>");
            }
        }

        sb.AppendLine("    </streamdetails>");
        sb.AppendLine("  </fileinfo>");
    }

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

    private static bool IsIgnored(EmbyItem item, HashSet<string> ignored)
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
