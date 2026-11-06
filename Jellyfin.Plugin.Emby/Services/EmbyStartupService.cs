using System.Text.Json;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.Providers;
using Jellyfin.Plugin.Emby.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Services;

/// <summary>
/// Universal Startup &amp; background service for Emby Federation:
/// 1. Bootstraps configuration from optional local seed JSON (/config/data/plugins/Emby/emby_seed.json) or environment variables (EMBY_PEER_URL, EMBY_PEER_USERNAME, EMBY_PEER_PASSWORD) without hardcoding any personal data in code.
/// 2. Injects &lt;script src="/api/embyPlugin/Script" defer&gt;&lt;/script&gt; into Jellyfin's index.html.
/// 3. Automatically attaches /data/emby/movies and /data/emby/tv to the configured Jellyfin libraries (or creates dedicated libraries) so Seerr &amp; Suggestarr mark all Emby items as AVAILABLE.
/// 4. Hooks ISessionManager.PlaybackStopped and IUserDataManager.UserDataSaved to sync watched status &amp; favorites back to Emby.
/// </summary>
public class EmbyStartupService : IHostedService
{
    private readonly IServerApplicationPaths _appPaths;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmbyStartupService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public EmbyStartupService(
        IServerApplicationPaths appPaths,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IHttpClientFactory httpClientFactory,
        ILogger<EmbyStartupService> logger,
        ILoggerFactory loggerFactory)
    {
        _appPaths = appPaths;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        TryBootstrapExternalConfiguration();
        TryInjectIndexHtmlScript();
        await EnsureEmbyLibrariesAsync().ConfigureAwait(false);

        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _userDataManager.UserDataSaved += OnUserDataSaved;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void TryBootstrapExternalConfiguration()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            var modified = false;

            // 1. Optional Environment Variable Bootstrap (Zero hardcoded secrets)
            var envUrl = Environment.GetEnvironmentVariable("EMBY_PEER_URL");
            var envUser = Environment.GetEnvironmentVariable("EMBY_PEER_USERNAME");
            var envPass = Environment.GetEnvironmentVariable("EMBY_PEER_PASSWORD");
            var envApiKey = Environment.GetEnvironmentVariable("EMBY_PEER_API_KEY");
            var envSeerrKey = Environment.GetEnvironmentVariable("JELLYSEERR_API_KEY") ?? Environment.GetEnvironmentVariable("SEER_TOKEN");

            if (!string.IsNullOrWhiteSpace(envUrl) && string.IsNullOrWhiteSpace(config.EmbyServerUrl))
            {
                config.EmbyServerUrl = envUrl.Trim();
                modified = true;
            }

            if (!string.IsNullOrWhiteSpace(envUser) && string.IsNullOrWhiteSpace(config.EmbyUsername))
            {
                config.EmbyUsername = envUser.Trim();
                modified = true;
            }

            if (!string.IsNullOrWhiteSpace(envPass) && string.IsNullOrWhiteSpace(config.EmbyPassword))
            {
                config.EmbyPassword = envPass.Trim();
                modified = true;
            }

            if (!string.IsNullOrWhiteSpace(envApiKey) && string.IsNullOrWhiteSpace(config.EmbyApiKey))
            {
                config.EmbyApiKey = envApiKey.Trim();
                modified = true;
            }

            if (!string.IsNullOrWhiteSpace(envSeerrKey) && string.IsNullOrWhiteSpace(config.SeerrApiKey))
            {
                config.SeerrApiKey = envSeerrKey.Trim();
                modified = true;
            }

            // 2. Optional JSON seed file (/config/data/plugins/Emby/emby_seed.json or /config/plugins/Emby/emby_seed.json)
            var seedPaths = new[]
            {
                "/config/data/plugins/Emby/emby_seed.json",
                "/config/data/data/plugins/Emby/emby_seed.json",
                "/config/plugins/Emby/emby_seed.json"
            };

            foreach (var seedFile in seedPaths)
            {
                if (!File.Exists(seedFile))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(seedFile));
                var root = doc.RootElement;

                if (root.TryGetProperty("embyServerUrl", out var uProp) && !string.IsNullOrWhiteSpace(uProp.GetString()) && string.IsNullOrWhiteSpace(config.EmbyServerUrl))
                {
                    config.EmbyServerUrl = uProp.GetString()!.Trim();
                    modified = true;
                }

                if (root.TryGetProperty("embyUsername", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString()) && string.IsNullOrWhiteSpace(config.EmbyUsername))
                {
                    config.EmbyUsername = nProp.GetString()!.Trim();
                    modified = true;
                }

                if (root.TryGetProperty("embyPassword", out var pProp) && !string.IsNullOrWhiteSpace(pProp.GetString()) && string.IsNullOrWhiteSpace(config.EmbyPassword))
                {
                    config.EmbyPassword = pProp.GetString()!;
                    modified = true;
                }

                if (root.TryGetProperty("embyApiKey", out var kProp) && !string.IsNullOrWhiteSpace(kProp.GetString()) && string.IsNullOrWhiteSpace(config.EmbyApiKey))
                {
                    config.EmbyApiKey = kProp.GetString()!.Trim();
                    modified = true;
                }

                if (root.TryGetProperty("embyUserId", out var uidProp) && !string.IsNullOrWhiteSpace(uidProp.GetString()) && string.IsNullOrWhiteSpace(config.EmbyUserId))
                {
                    config.EmbyUserId = uidProp.GetString()!.Trim();
                    modified = true;
                }

                if (root.TryGetProperty("seerrApiKey", out var skProp) && !string.IsNullOrWhiteSpace(skProp.GetString()) && string.IsNullOrWhiteSpace(config.SeerrApiKey))
                {
                    config.SeerrApiKey = skProp.GetString()!.Trim();
                    modified = true;
                }

                if (root.TryGetProperty("sonarrApiKey", out var snProp) && !string.IsNullOrWhiteSpace(snProp.GetString()) && string.IsNullOrWhiteSpace(config.SonarrApiKey))
                {
                    config.SonarrApiKey = snProp.GetString()!.Trim();
                    modified = true;
                }

                if (root.TryGetProperty("radarrApiKey", out var rdProp) && !string.IsNullOrWhiteSpace(rdProp.GetString()) && string.IsNullOrWhiteSpace(config.RadarrApiKey))
                {
                    config.RadarrApiKey = rdProp.GetString()!.Trim();
                    modified = true;
                }

                break;
            }

            if (modified)
            {
                Plugin.Instance?.SaveConfiguration();
                _logger.LogInformation("Bootstrapped Emby Federation configuration for server {Server}", config.EmbyServerUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not bootstrap Emby seed configuration");
        }
    }

    private void TryInjectIndexHtmlScript()
    {
        try
        {
            var webPath = _appPaths.WebPath;
            if (string.IsNullOrWhiteSpace(webPath))
            {
                return;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                return;
            }

            const string scriptTag = "<script src=\"/api/embyPlugin/Script\" defer></script>";
            var html = File.ReadAllText(indexFile);
            if (!html.Contains("/api/embyPlugin/Script", StringComparison.OrdinalIgnoreCase))
            {
                var updated = html.Replace("</body>", $"{scriptTag}</body>", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(indexFile, updated);
                _logger.LogInformation("Injected Emby Federation client script into Jellyfin index.html");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inject Emby script into index.html");
        }
    }

    private async Task EnsureEmbyLibrariesAsync()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnableLibraryFederation || string.IsNullOrWhiteSpace(config.StrmLibraryPath))
        {
            return;
        }

        try
        {
            var rootDir = config.StrmLibraryPath.Trim();
            var moviesDir = Path.Combine(rootDir, "movies");
            var tvDir = Path.Combine(rootDir, "tv");
            Directory.CreateDirectory(moviesDir);
            Directory.CreateDirectory(tvDir);

            var vfs = _libraryManager.GetVirtualFolders();

            if (config.AttachToExistingLibraries)
            {
                var moviesLibName = !string.IsNullOrWhiteSpace(config.TargetMoviesLibraryName) ? config.TargetMoviesLibraryName : "Filme";
                var seriesLibName = !string.IsNullOrWhiteSpace(config.TargetSeriesLibraryName) ? config.TargetSeriesLibraryName : "Serien";

                var moviesVf = vfs.FirstOrDefault(v => string.Equals(v.Name, moviesLibName, StringComparison.OrdinalIgnoreCase))
                               ?? vfs.FirstOrDefault(v => v.CollectionType == CollectionTypeOptions.movies || string.Equals(v.CollectionType?.ToString(), "movies", StringComparison.OrdinalIgnoreCase));

                if (moviesVf != null && config.EnableMoviesFederation && (moviesVf.Locations == null || !moviesVf.Locations.Contains(moviesDir)))
                {
                    _libraryManager.AddMediaPath(moviesVf.Name, new MediaPathInfo { Path = moviesDir });
                    _logger.LogInformation("Attached Emby movies path {Path} to Jellyfin library '{Library}'", moviesDir, moviesVf.Name);
                }

                var seriesVf = vfs.FirstOrDefault(v => string.Equals(v.Name, seriesLibName, StringComparison.OrdinalIgnoreCase))
                               ?? vfs.FirstOrDefault(v => v.CollectionType == CollectionTypeOptions.tvshows || string.Equals(v.CollectionType?.ToString(), "tvshows", StringComparison.OrdinalIgnoreCase));

                if (seriesVf != null && config.EnableSeriesFederation && (seriesVf.Locations == null || !seriesVf.Locations.Contains(tvDir)))
                {
                    _libraryManager.AddMediaPath(seriesVf.Name, new MediaPathInfo { Path = tvDir });
                    _logger.LogInformation("Attached Emby series path {Path} to Jellyfin library '{Library}'", tvDir, seriesVf.Name);
                }
            }

            if (config.CreateDedicatedEmbyLibraries)
            {
                var moviesLibName = !string.IsNullOrWhiteSpace(config.TargetMoviesLibraryName) ? config.TargetMoviesLibraryName : "Filme (Emby)";
                var seriesLibName = !string.IsNullOrWhiteSpace(config.TargetSeriesLibraryName) ? config.TargetSeriesLibraryName : "Serien (Emby)";

                // Detach from legacy default libraries if present
                var legacyMoviesVf = vfs.FirstOrDefault(v => string.Equals(v.Name, "Filme", StringComparison.OrdinalIgnoreCase));
                if (legacyMoviesVf != null && legacyMoviesVf.Locations != null && legacyMoviesVf.Locations.Contains(moviesDir))
                {
                    try
                    {
                        _libraryManager.RemoveMediaPath(legacyMoviesVf.Name, moviesDir);
                        _logger.LogInformation("Detached {Path} from legacy library '{Library}'", moviesDir, legacyMoviesVf.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not detach from legacy movies library");
                    }
                }

                var legacySeriesVf = vfs.FirstOrDefault(v => string.Equals(v.Name, "Serien", StringComparison.OrdinalIgnoreCase));
                if (legacySeriesVf != null && legacySeriesVf.Locations != null && legacySeriesVf.Locations.Contains(tvDir))
                {
                    try
                    {
                        _libraryManager.RemoveMediaPath(legacySeriesVf.Name, tvDir);
                        _logger.LogInformation("Detached {Path} from legacy library '{Library}'", tvDir, legacySeriesVf.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not detach from legacy series library");
                    }
                }

                var existingMovies = vfs.FirstOrDefault(v => string.Equals(v.Name, moviesLibName, StringComparison.OrdinalIgnoreCase));
                if (config.EnableMoviesFederation && existingMovies == null)
                {
                    var movieOpts = BuildFastEmbyLibraryOptions(moviesDir, "Movie");
                    await _libraryManager.AddVirtualFolder(moviesLibName, CollectionTypeOptions.movies, movieOpts, true).ConfigureAwait(false);
                    _logger.LogInformation("Created dedicated Jellyfin library '{Library}' pointing to {Path}", moviesLibName, moviesDir);
                }

                var existingSeries = vfs.FirstOrDefault(v => string.Equals(v.Name, seriesLibName, StringComparison.OrdinalIgnoreCase));
                if (config.EnableSeriesFederation && existingSeries == null)
                {
                    var seriesOpts = BuildFastEmbyLibraryOptions(tvDir, "Series");
                    await _libraryManager.AddVirtualFolder(seriesLibName, CollectionTypeOptions.tvshows, seriesOpts, true).ConfigureAwait(false);
                    _logger.LogInformation("Created dedicated Jellyfin library '{Library}' pointing to {Path}", seriesLibName, tvDir);
                }
            }

            var targetMoviesName = !string.IsNullOrWhiteSpace(config.TargetMoviesLibraryName) ? config.TargetMoviesLibraryName : "Filme (Emby)";
            var targetSeriesName = !string.IsNullOrWhiteSpace(config.TargetSeriesLibraryName) ? config.TargetSeriesLibraryName : "Serien (Emby)";
            if (config.EnableMoviesFederation)
            {
                EnsureLibraryArtwork(moviesDir, targetMoviesName, "movies");
            }

            if (config.EnableSeriesFederation)
            {
                EnsureLibraryArtwork(tvDir, targetSeriesName, "tv");
            }

            // Trigger initial background sync if credentials are configured
            if (!string.IsNullOrWhiteSpace(config.GetNormalizedEmbyServerUrl()))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new EmbyApiClient(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());
                        var changed = await SyncEmbyLibraryTask.PerformSyncAsync(client, config, _logger, null, CancellationToken.None, libraryManager: _libraryManager).ConfigureAwait(false);
                        if (changed > 0)
                        {
                            _libraryManager.QueueLibraryScan();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Initial Emby library federation sync failed");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed ensuring Emby federated library folders");
        }
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.EnablePlayheadSync || e.Item == null || !e.PlayedToCompletion)
            {
                return;
            }

            var embyId = EmbyIdKeys.GetItemId(e.Item.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(e.Item.Path);
            if (string.IsNullOrWhiteSpace(embyId))
            {
                return;
            }

            var userKey = e.Users?.FirstOrDefault()?.Id.ToString("N");
            using var client = new EmbyApiClient(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());
            await client.SetPlayedStatusAsync(embyId, true, userKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error syncing playback completion to Emby");
        }
    }

    private async void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            if (!config.EnableFavoriteSync || e.Item == null || e.UserData == null)
            {
                return;
            }

            if (e.SaveReason != UserDataSaveReason.UpdateUserRating && e.SaveReason != UserDataSaveReason.TogglePlayed)
            {
                return;
            }

            var embyId = EmbyIdKeys.GetItemId(e.Item.ProviderIds) ?? EmbyIdKeys.ExtractItemIdFromPath(e.Item.Path);
            if (string.IsNullOrWhiteSpace(embyId))
            {
                return;
            }

            var userKey = e.UserId.Equals(Guid.Empty) ? null : e.UserId.ToString("N");
            using var client = new EmbyApiClient(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());

            if (e.SaveReason == UserDataSaveReason.UpdateUserRating)
            {
                await client.SetFavoriteStatusAsync(embyId, e.UserData.IsFavorite, userKey, CancellationToken.None).ConfigureAwait(false);
            }
            else if (e.SaveReason == UserDataSaveReason.TogglePlayed && config.EnablePlayheadSync)
            {
                await client.SetPlayedStatusAsync(embyId, e.UserData.Played, userKey, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error syncing user data to Emby");
        }
    }

    private static LibraryOptions BuildFastEmbyLibraryOptions(string path, string type)
    {
        return new LibraryOptions
        {
            Enabled = true,
            EnablePhotos = false,
            EnableRealtimeMonitor = false,
            EnableLUFSScan = false,
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            SaveLocalMetadata = false,
            AutomaticallyAddToCollection = false,
            PathInfos = new[] { new MediaPathInfo { Path = path } },
            TypeOptions = new[]
            {
                new TypeOptions
                {
                    Type = type,
                    MetadataFetchers = new[] { "Nfo" },
                    MetadataFetcherOrder = new[] { "Nfo" },
                    ImageFetchers = new[] { "Nfo" },
                    ImageFetcherOrder = new[] { "Nfo" }
                }
            }
        };
    }

    private void EnsureLibraryArtwork(string mediaDir, string libName, string type)
    {
        try
        {
            var asmDir = Path.GetDirectoryName(typeof(EmbyStartupService).Assembly.Location) ?? string.Empty;
            var artworkSubdir = Path.Combine(asmDir, "Artwork");

            // Clean up any unwanted logo/backdrop/banner images from mediaDir that cause movies/shows to inherit library logos
            foreach (var unwanted in new[] { "logo.png", "backdrop.png", "banner.png" })
            {
                var p = Path.Combine(mediaDir, unwanted);
                if (File.Exists(p))
                {
                    try
                    {
                        File.Delete(p);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
            }

            var rootDefaultDir = Path.Combine(_appPaths.DataPath, "root", "default", libName);
            try
            {
                Directory.CreateDirectory(rootDefaultDir);
            }
            catch
            {
                // Ignore folder creation errors
            }

            // Remove unwanted logo from library root tile so it does not suppress plain text titles
            var rootLogo = Path.Combine(rootDefaultDir, "logo.png");
            if (File.Exists(rootLogo))
            {
                try
                {
                    File.Delete(rootLogo);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            // Only deploy poster image to rootDefaultDir for home screen library tile
            var posterCandidates = new[]
            {
                Path.Combine(artworkSubdir, $"{type}_poster.png"),
                Path.Combine(asmDir, $"{type}_poster.png"),
                Path.Combine(asmDir, "poster.png")
            };

            var foundPoster = posterCandidates.FirstOrDefault(File.Exists);
            if (foundPoster != null && Directory.Exists(rootDefaultDir))
            {
                var dstPoster = Path.Combine(rootDefaultDir, "poster.png");
                if (!File.Exists(dstPoster))
                {
                    File.Copy(foundPoster, dstPoster, overwrite: true);
                }
            }

            _logger.LogInformation("Ensured library artwork deployed for '{Library}' ({Type})", libName, type);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not ensure library artwork for {Library}", libName);
        }
    }
}
