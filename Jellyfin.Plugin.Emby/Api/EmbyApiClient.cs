using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Api;

public sealed class EmbyApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EmbyApiClient> _logger;
    private static readonly JsonSerializerOptions JsonOpts = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        opts.Converters.Add(new FlexibleStringConverter());
        return opts;
    }

    public EmbyApiClient(HttpClient httpClient, ILogger<EmbyApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(45);
    }

    public HttpClient HttpClient => _httpClient;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private void PrepareHeaders(HttpRequestMessage req, string? token = null)
    {
        var ua = !string.IsNullOrWhiteSpace(Config.UserAgent)
            ? Config.UserAgent
            : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation(
            "X-Emby-Authorization",
            "MediaBrowser Client=\"Emby Web\", Device=\"JellyfinFederation\", DeviceId=\"jellyfin-plugin-emby\", Version=\"4.8.10.0\"");

        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.TryAddWithoutValidation("X-Emby-Token", token);
        }
    }

    public async Task<EmbyAuthResponse?> AuthenticateByNameAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = serverUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/emby/Users/AuthenticateByName");
            PrepareHeaders(req);
            var payload = JsonSerializer.Serialize(new { Username = username.Trim(), Pw = password });
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Emby AuthenticateByName failed with HTTP {Status} on {Url}", (int)resp.StatusCode, baseUrl);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EmbyAuthResponse>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error authenticating against Emby server {Url}", baseUrl);
            return null;
        }
    }

    public async Task<(string BaseUrl, string Token, string UserId, string ServerId)> EnsureAuthenticatedAsync(
        string? jellyfinUserOrId = null,
        CancellationToken cancellationToken = default)
    {
        var config = Config;
        var account = config.GetAccountForUser(jellyfinUserOrId);

        var baseUrl = config.GetNormalizedEmbyServerUrl(account?.EmbyServerUrl);
        var token = !string.IsNullOrWhiteSpace(account?.AccessToken) ? account!.AccessToken : config.EmbyApiKey;
        var uid = !string.IsNullOrWhiteSpace(account?.EmbyUserId) ? account!.EmbyUserId : config.EmbyUserId;
        var srvId = !string.IsNullOrWhiteSpace(account?.ServerId) ? account!.ServerId : config.EmbyServerId;
        var user = !string.IsNullOrWhiteSpace(account?.EmbyUsername) ? account!.EmbyUsername : config.EmbyUsername;
        var pass = !string.IsNullOrWhiteSpace(account?.EmbyPassword) ? account!.EmbyPassword : config.EmbyPassword;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (string.Empty, string.Empty, string.Empty, string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(uid))
        {
            return (baseUrl, token, uid, srvId);
        }

        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            var auth = await AuthenticateByNameAsync(baseUrl, user, pass, cancellationToken).ConfigureAwait(false);
            if (auth != null && !string.IsNullOrWhiteSpace(auth.AccessToken))
            {
                token = auth.AccessToken;
                uid = auth.User?.Id ?? string.Empty;
                srvId = auth.ServerId;

                if (account != null && !string.IsNullOrWhiteSpace(account.JellyfinUser))
                {
                    account.AccessToken = token;
                    account.EmbyUserId = uid;
                    account.ServerId = srvId;
                }

                if (string.IsNullOrWhiteSpace(config.EmbyApiKey))
                {
                    config.EmbyApiKey = token;
                    config.EmbyUserId = uid;
                    config.EmbyServerId = srvId;
                }

                Plugin.Instance?.SaveConfiguration();
                return (baseUrl, token, uid, srvId);
            }
        }

        return (baseUrl, token ?? string.Empty, uid ?? string.Empty, srvId ?? string.Empty);
    }

    public async Task<List<EmbyItemDto>> GetItemsAsync(
        string includeItemTypes,
        string? parentId = null,
        string? searchTerm = null,
        int? limit = null,
        string? sortBy = null,
        string? sortOrder = null,
        string? filters = null,
        string? jellyfinUserOrId = null,
        DateTime? minDateLastSaved = null,
        CancellationToken cancellationToken = default)
    {
        var (baseUrl, token, uid, _) = await EnsureAuthenticatedAsync(jellyfinUserOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(uid))
        {
            return new List<EmbyItemDto>();
        }

        var fields = "ProviderIds,ProductionYear,PremiereDate,Overview,Genres,CommunityRating,CriticRating,OfficialRating,CustomRating,RunTimeTicks,MediaSources,MediaStreams,SeriesName,SeriesId,ParentIndexNumber,IndexNumber,UserData,People,Studios,Tags,Taglines,Chapters,OriginalTitle,SortName,Status,AirDays,AirTime,DateCreated,Collections,CollectionName";
        var sb = new StringBuilder($"{baseUrl}/emby/Users/{Uri.EscapeDataString(uid)}/Items?Recursive=true&Fields={fields}");

        if (minDateLastSaved.HasValue)
        {
            sb.Append($"&MinDateLastSaved={Uri.EscapeDataString(minDateLastSaved.Value.ToString("o"))}");
        }

        if (!string.IsNullOrWhiteSpace(includeItemTypes))
        {
            sb.Append($"&IncludeItemTypes={Uri.EscapeDataString(includeItemTypes)}");
        }

        if (!string.IsNullOrWhiteSpace(parentId))
        {
            sb.Append($"&ParentId={Uri.EscapeDataString(parentId)}");
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            sb.Append($"&SearchTerm={Uri.EscapeDataString(searchTerm)}");
        }

        if (limit.HasValue && limit.Value > 0)
        {
            sb.Append($"&Limit={limit.Value}");
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            sb.Append($"&SortBy={Uri.EscapeDataString(sortBy)}");
        }

        if (!string.IsNullOrWhiteSpace(sortOrder))
        {
            sb.Append($"&SortOrder={Uri.EscapeDataString(sortOrder)}");
        }

        if (!string.IsNullOrWhiteSpace(filters))
        {
            sb.Append($"&Filters={Uri.EscapeDataString(filters)}");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, sb.ToString());
            PrepareHeaders(req, token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Clear token and retry once
                Config.EmbyApiKey = string.Empty;
                var retryAuth = await EnsureAuthenticatedAsync(jellyfinUserOrId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(retryAuth.Token))
                {
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, sb.ToString());
                    PrepareHeaders(req2, retryAuth.Token);
                    using var resp2 = await _httpClient.SendAsync(req2, cancellationToken).ConfigureAwait(false);
                    if (resp2.IsSuccessStatusCode)
                    {
                        var json2 = await resp2.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        return JsonSerializer.Deserialize<EmbyItemsEnvelope>(json2, JsonOpts)?.Items ?? new List<EmbyItemDto>();
                    }
                }

                return new List<EmbyItemDto>();
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new List<EmbyItemDto>();
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EmbyItemsEnvelope>(json, JsonOpts)?.Items ?? new List<EmbyItemDto>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed fetching Emby items ({Types})", includeItemTypes);
            return new List<EmbyItemDto>();
        }
    }

    public async Task<EmbyItemDto?> GetItemAsync(
        string embyItemId,
        string? jellyfinUserOrId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(embyItemId))
        {
            return null;
        }

        var (baseUrl, token, uid, _) = await EnsureAuthenticatedAsync(jellyfinUserOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(uid))
        {
            return null;
        }

        try
        {
            var url = $"{baseUrl}/emby/Users/{Uri.EscapeDataString(uid)}/Items/{Uri.EscapeDataString(embyItemId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            PrepareHeaders(req, token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EmbyItemDto>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch Emby item {ItemId}", embyItemId);
            return null;
        }
    }

    public async Task<EmbyAvailabilityMatch> CheckAvailabilityByExternalIdAsync(
        string mediaType,
        string? tmdbId,
        string? tvdbId = null,
        string? imdbId = null,
        CancellationToken cancellationToken = default)
    {
        var result = new EmbyAvailabilityMatch { Available = false, MediaType = mediaType };
        var (baseUrl, token, _, srvId) = await EnsureAuthenticatedAsync(null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            return result;
        }

        var embyType = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase)
            ? "Series"
            : "Movie";

        var items = await GetItemsAsync(embyType, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            var itemTmdb = item.GetProviderId("Tmdb");
            var itemTvdb = item.GetProviderId("Tvdb");
            var itemImdb = item.GetProviderId("Imdb");

            var matchTmdb = !string.IsNullOrWhiteSpace(tmdbId) && string.Equals(itemTmdb, tmdbId.Trim(), StringComparison.OrdinalIgnoreCase);
            var matchTvdb = !string.IsNullOrWhiteSpace(tvdbId) && string.Equals(itemTvdb, tvdbId.Trim(), StringComparison.OrdinalIgnoreCase);
            var matchImdb = !string.IsNullOrWhiteSpace(imdbId) && string.Equals(itemImdb, imdbId.Trim(), StringComparison.OrdinalIgnoreCase);

            if (matchTmdb || matchTvdb || matchImdb)
            {
                var effectiveSrvId = !string.IsNullOrWhiteSpace(item.ServerId) ? item.ServerId : srvId;
                result.Available = true;
                result.EmbyItemId = item.Id;
                result.Title = item.Name;
                result.Year = item.ProductionYear;
                result.EmbyWebUrl = $"{baseUrl}/web/index.html#!/item?id={item.Id}&serverId={effectiveSrvId}";
                result.DirectStreamUrl = $"{baseUrl}/emby/Videos/{item.Id}/stream?static=true&api_key={token}";
                return result;
            }
        }

        return result;
    }

    public async Task<bool> SetPlayedStatusAsync(
        string embyItemId,
        bool played,
        string? jellyfinUserOrId = null,
        CancellationToken cancellationToken = default)
    {
        var (baseUrl, token, uid, _) = await EnsureAuthenticatedAsync(jellyfinUserOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(uid))
        {
            return false;
        }

        try
        {
            var method = played ? HttpMethod.Post : HttpMethod.Delete;
            var url = $"{baseUrl}/emby/Users/{Uri.EscapeDataString(uid)}/PlayedItems/{Uri.EscapeDataString(embyItemId)}";
            using var req = new HttpRequestMessage(method, url);
            PrepareHeaders(req, token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SetFavoriteStatusAsync(
        string embyItemId,
        bool isFavorite,
        string? jellyfinUserOrId = null,
        CancellationToken cancellationToken = default)
    {
        var (baseUrl, token, uid, _) = await EnsureAuthenticatedAsync(jellyfinUserOrId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(uid))
        {
            return false;
        }

        try
        {
            var method = isFavorite ? HttpMethod.Post : HttpMethod.Delete;
            var url = $"{baseUrl}/emby/Users/{Uri.EscapeDataString(uid)}/FavoriteItems/{Uri.EscapeDataString(embyItemId)}";
            using var req = new HttpRequestMessage(method, url);
            PrepareHeaders(req, token);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public string GetPrimaryImageUrl(string baseUrl, string embyItemId, string? token = null, int maxWidth = 600)
    {
        var cleanBase = baseUrl.TrimEnd('/');
        var q = !string.IsNullOrWhiteSpace(token) ? $"?maxWidth={maxWidth}&api_key={Uri.EscapeDataString(token)}" : $"?maxWidth={maxWidth}";
        return $"{cleanBase}/emby/Items/{Uri.EscapeDataString(embyItemId)}/Images/Primary{q}";
    }

    public string GetBackdropImageUrl(string baseUrl, string embyItemId, string? token = null, int maxWidth = 1920)
    {
        var cleanBase = baseUrl.TrimEnd('/');
        var q = !string.IsNullOrWhiteSpace(token) ? $"?maxWidth={maxWidth}&api_key={Uri.EscapeDataString(token)}" : $"?maxWidth={maxWidth}";
        return $"{cleanBase}/emby/Items/{Uri.EscapeDataString(embyItemId)}/Images/Backdrop/0{q}";
    }

    public string GetDirectStreamUrl(string baseUrl, string embyItemId, string token) =>
        $"{baseUrl.TrimEnd('/')}/emby/Videos/{Uri.EscapeDataString(embyItemId)}/stream?static=true&api_key={Uri.EscapeDataString(token)}";

    public static string FormatRichOverview(EmbyItemDto item, string baseUrl, string serverId)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Overview))
        {
            sb.AppendLine(item.Overview.Trim());
            sb.AppendLine();
        }

        var effSrv = !string.IsNullOrWhiteSpace(item.ServerId) ? item.ServerId : serverId;
        var webLink = $"{baseUrl.TrimEnd('/')}/web/index.html#!/item?id={item.Id}&serverId={effSrv}";
        sb.Append($"⚡ Federated via Emby ({baseUrl}) • Direct Link: {webLink}");
        return sb.ToString();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var l)) return l.ToString();
                if (reader.TryGetDouble(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Null:
                return null;
            default:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                return doc.RootElement.GetRawText();
            }
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
