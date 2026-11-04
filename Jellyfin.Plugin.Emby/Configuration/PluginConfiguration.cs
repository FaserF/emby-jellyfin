using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Emby.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        EmbyServerUrl = string.Empty;
        EmbyUsername = string.Empty;
        EmbyPassword = string.Empty;
        EmbyApiKey = string.Empty;
        EmbyUserId = string.Empty;
        EmbyServerId = string.Empty;
        EnableStreamingChannel = true;
        EnablePublicFallbackForUnlinkedUsers = true;
        PrimaryJellyfinUser = string.Empty;
        PrimaryJellyfinUserId = string.Empty;
        EnableLibraryFederation = true;
        EnableMoviesFederation = true;
        EnableSeriesFederation = true;
        AttachToExistingLibraries = false;
        TargetMoviesLibraryName = "Filme (Emby)";
        TargetSeriesLibraryName = "Serien (Emby)";
        CreateDedicatedEmbyLibraries = true;
        StrmLibraryPath = "/data/emby";
        PublicServerUrl = string.Empty;
        UseDirectEmbyStreamUrls = true;
        EnablePlayheadSync = false;
        EnableFavoriteSync = false;
        FetchArtworkFromEmby = true;
        UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        EnableSeerrRequestGuard = true;
        SeerrUrl = "http://jellyseerr:5055";
        SeerrApiKey = string.Empty;
        SonarrUrl = "http://sonarr:8989";
        SonarrApiKey = string.Empty;
        RadarrUrl = "http://radarr:7878";
        RadarrApiKey = string.Empty;
        ExcludedTmdbIds = string.Empty;
        UserAccounts = new List<EmbyUserAccountEntry>();
    }

    public string EmbyServerUrl { get; set; }
    public string EmbyUsername { get; set; }
    public string EmbyPassword { get; set; }
    public string EmbyApiKey { get; set; }
    public string EmbyUserId { get; set; }
    public string EmbyServerId { get; set; }

    public bool EnableStreamingChannel { get; set; }
    public bool EnablePublicFallbackForUnlinkedUsers { get; set; }
    public string PrimaryJellyfinUser { get; set; }
    public string PrimaryJellyfinUserId { get; set; }

    public bool EnableLibraryFederation { get; set; }
    public bool EnableMoviesFederation { get; set; }
    public bool EnableSeriesFederation { get; set; }
    public bool AttachToExistingLibraries { get; set; }
    public string TargetMoviesLibraryName { get; set; }
    public string TargetSeriesLibraryName { get; set; }
    public bool CreateDedicatedEmbyLibraries { get; set; }
    public string StrmLibraryPath { get; set; }
    public string PublicServerUrl { get; set; }
    public bool UseDirectEmbyStreamUrls { get; set; }

    public bool EnablePlayheadSync { get; set; }
    public bool EnableFavoriteSync { get; set; }
    public bool FetchArtworkFromEmby { get; set; }
    public string UserAgent { get; set; }

    public bool EnableSeerrRequestGuard { get; set; }
    public string SeerrUrl { get; set; }
    public string SeerrApiKey { get; set; }
    public string SonarrUrl { get; set; }
    public string SonarrApiKey { get; set; }
    public string RadarrUrl { get; set; }
    public string RadarrApiKey { get; set; }
    public string ExcludedTmdbIds { get; set; }

    public List<EmbyUserAccountEntry> UserAccounts { get; set; }

    public EmbyUserAccountEntry? GetAccountForUser(string? usernameOrId)
    {
        var normalized = NormalizeId(usernameOrId);
        if (!string.IsNullOrWhiteSpace(normalized) && UserAccounts != null)
        {
            var match = UserAccounts.FirstOrDefault(u =>
                string.Equals(NormalizeId(u.JellyfinUser), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeId(u.JellyfinUserId), normalized, StringComparison.OrdinalIgnoreCase));
            if (match != null && (!string.IsNullOrWhiteSpace(match.AccessToken) || !string.IsNullOrWhiteSpace(match.EmbyPassword)))
            {
                return match;
            }
        }

        if ((EnablePublicFallbackForUnlinkedUsers || IsPrimaryUser(usernameOrId)) &&
            (!string.IsNullOrWhiteSpace(EmbyApiKey) || !string.IsNullOrWhiteSpace(EmbyPassword)))
        {
            return new EmbyUserAccountEntry
            {
                JellyfinUser = PrimaryJellyfinUser,
                JellyfinUserId = PrimaryJellyfinUserId,
                EmbyServerUrl = EmbyServerUrl,
                EmbyUsername = EmbyUsername,
                EmbyPassword = EmbyPassword,
                EmbyUserId = EmbyUserId,
                AccessToken = EmbyApiKey,
                ServerId = EmbyServerId
            };
        }

        return null;
    }

    public bool IsPrimaryUser(string? usernameOrId)
    {
        if (string.IsNullOrWhiteSpace(usernameOrId))
        {
            return false;
        }

        var norm = NormalizeId(usernameOrId);
        return (!string.IsNullOrWhiteSpace(PrimaryJellyfinUser) && string.Equals(norm, NormalizeId(PrimaryJellyfinUser), StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(PrimaryJellyfinUserId) && string.Equals(norm, NormalizeId(PrimaryJellyfinUserId), StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeId(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Replace("-", string.Empty, StringComparison.Ordinal).Trim();

    public string GetNormalizedEmbyServerUrl(string? overrideUrl = null)
    {
        var url = !string.IsNullOrWhiteSpace(overrideUrl) ? overrideUrl : EmbyServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var envUrl = Environment.GetEnvironmentVariable("EMBY_PEER_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
            {
                url = envUrl;
            }
        }

        return string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/');
    }

    public string GetPublicServerUrl(string? requestScheme = null, string? requestHost = null)
    {
        if (!string.IsNullOrWhiteSpace(requestScheme) && !string.IsNullOrWhiteSpace(requestHost))
        {
            return $"{requestScheme}://{requestHost}".TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(PublicServerUrl))
        {
            return PublicServerUrl.TrimEnd('/');
        }

        var published = Environment.GetEnvironmentVariable("JELLYFIN_PublishedServerUrl");
        if (!string.IsNullOrWhiteSpace(published))
        {
            return published.TrimEnd('/');
        }

        return "http://127.0.0.1:8096";
    }
}

public class EmbyUserAccountEntry
{
    public string JellyfinUser { get; set; } = string.Empty;
    public string JellyfinUserId { get; set; } = string.Empty;
    public string EmbyServerUrl { get; set; } = string.Empty;
    public string EmbyUsername { get; set; } = string.Empty;
    public string EmbyPassword { get; set; } = string.Empty;
    public string EmbyUserId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
}
