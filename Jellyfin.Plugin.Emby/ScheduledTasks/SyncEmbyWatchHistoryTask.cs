using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.ScheduledTasks;

public class SyncEmbyWatchHistoryTask : IScheduledTask
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyncEmbyWatchHistoryTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public SyncEmbyWatchHistoryTask(
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<SyncEmbyWatchHistoryTask> logger,
        ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "Sync Emby Watch Progress & Favorites to Jellyfin";
    public string Key => "SyncEmbyWatchHistoryTask";
    public string Description => "Synchronizes watched status, playback resume positions, and favorites from the federated Emby server into Jellyfin user profiles.";
    public string Category => "Emby Federation";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.EnablePlayheadSync)
        {
            progress.Report(100);
            return;
        }

        using var client = new EmbyApiClient(_httpClientFactory.CreateClient("Emby"), _loggerFactory.CreateLogger<EmbyApiClient>());
        var accounts = new List<EmbyUserAccountEntry>();
        if (config.UserAccounts != null)
        {
            accounts.AddRange(config.UserAccounts.Where(a => !string.IsNullOrWhiteSpace(a.JellyfinUserId)));
        }

        if (accounts.Count == 0 && !string.IsNullOrWhiteSpace(config.PrimaryJellyfinUserId))
        {
            var primary = config.GetAccountForUser(config.PrimaryJellyfinUserId);
            if (primary != null)
            {
                accounts.Add(primary);
            }
        }

        if (accounts.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var allLibraryItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Movie },
            Recursive = true
        });

        for (var i = 0; i < accounts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = accounts[i];
            if (!Guid.TryParse(account.JellyfinUserId, out var userGuid))
            {
                continue;
            }

            var user = _userManager.GetUserById(userGuid);
            if (user == null)
            {
                continue;
            }

            try
            {
                var playedItems = await client.GetItemsAsync("Movie,Episode", filters: "IsPlayed", limit: 200, jellyfinUserOrId: account.JellyfinUserId, cancellationToken: cancellationToken).ConfigureAwait(false);
                var resumeItems = await client.GetItemsAsync("Movie,Episode", filters: "IsResumable", limit: 100, jellyfinUserOrId: account.JellyfinUserId, cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (var embyItem in playedItems.Concat(resumeItems))
                {
                    var candidate = allLibraryItems.FirstOrDefault(e =>
                        string.Equals(EmbyIdKeys.GetItemId(e.ProviderIds), embyItem.Id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(EmbyIdKeys.ExtractItemIdFromPath(e.Path), embyItem.Id, StringComparison.OrdinalIgnoreCase));

                    if (candidate == null)
                    {
                        continue;
                    }

                    var userData = _userDataManager.GetUserData(user, candidate);
                    if (userData == null || embyItem.UserData == null)
                    {
                        continue;
                    }

                    var modified = false;
                    if (embyItem.UserData.Played && !userData.Played)
                    {
                        userData.Played = true;
                        userData.PlaybackPositionTicks = 0;
                        userData.PlayCount = Math.Max(1, userData.PlayCount);
                        userData.LastPlayedDate = DateTime.UtcNow;
                        modified = true;
                    }
                    else if (embyItem.UserData.PlaybackPositionTicks > 0 &&
                             Math.Abs(userData.PlaybackPositionTicks - embyItem.UserData.PlaybackPositionTicks) > TimeSpan.FromSeconds(10).Ticks)
                    {
                        userData.PlaybackPositionTicks = embyItem.UserData.PlaybackPositionTicks;
                        userData.LastPlayedDate = DateTime.UtcNow;
                        modified = true;
                    }

                    if (modified)
                    {
                        _userDataManager.SaveUserData(user, candidate, userData, MediaBrowser.Model.Entities.UserDataSaveReason.Import, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not sync Emby watch history for user {User}", account.JellyfinUser);
            }

            progress.Report((double)(i + 1) / accounts.Count * 100.0);
        }
    }
}

public class ClearEmbyIDsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ClearEmbyIDsTask> _logger;

    private static readonly string[] AllEmbyKeys =
    {
        EmbyIdKeys.Item,
        EmbyIdKeys.Movie,
        EmbyIdKeys.Series,
        EmbyIdKeys.Season,
        EmbyIdKeys.Episode,
        "Emby"
    };

    public ClearEmbyIDsTask(ILibraryManager libraryManager, ILogger<ClearEmbyIDsTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "Clear Emby Provider IDs";
    public string Key => "ClearEmbyIDsTask";
    public string Description => "Removes all Emby external provider IDs from Series, Seasons, Episodes, and Movies.";
    public string Category => "Emby Federation";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie },
            Recursive = true
        });

        var total = items.Count;
        var cleared = 0;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            var modified = false;

            if (item.ProviderIds != null)
            {
                foreach (var k in AllEmbyKeys)
                {
                    if (item.ProviderIds.Remove(k))
                    {
                        modified = true;
                    }
                }
            }

            if (modified)
            {
                cleared++;
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            if (total > 0)
            {
                progress.Report((double)(i + 1) / total * 100.0);
            }
        }

        _logger.LogInformation("Cleared Emby provider IDs from {Count} items", cleared);
    }
}
