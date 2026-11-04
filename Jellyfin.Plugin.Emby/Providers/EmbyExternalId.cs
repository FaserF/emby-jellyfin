using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Emby.Providers;

public static class EmbyIdKeys
{
    public const string Item = "EmbyItem";
    public const string Series = "EmbySeries";
    public const string Season = "EmbySeason";
    public const string Episode = "EmbyEpisode";
    public const string Movie = "EmbyMovie";

    private static readonly Regex ItemPathRegex = new(@"\[(?:EmbyItem|EmbyMovie|EmbyEpisode|embyid)-([a-zA-Z0-9_-]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeriesPathRegex = new(@"\[(?:EmbySeries|embyseriesid)-([a-zA-Z0-9_-]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? GetItemId(IDictionary<string, string>? ids)
    {
        if (ids == null)
        {
            return null;
        }

        foreach (var k in new[] { Item, Movie, Episode, Series, Season, "Emby" })
        {
            if (ids.TryGetValue(k, out var val) && !string.IsNullOrWhiteSpace(val))
            {
                return val.Trim();
            }
        }

        return null;
    }

    public static void SetItemId(IHasProviderIds item, string embyId)
    {
        item.SetProviderId(Item, embyId);
        item.SetProviderId("Emby", embyId);
    }

    public static void SetMovieId(IHasProviderIds item, string embyId)
    {
        item.SetProviderId(Movie, embyId);
        item.SetProviderId(Item, embyId);
        item.SetProviderId("Emby", embyId);
    }

    public static void SetSeriesId(IHasProviderIds item, string embyId)
    {
        item.SetProviderId(Series, embyId);
        item.SetProviderId(Item, embyId);
    }

    public static void SetEpisodeId(IHasProviderIds item, string embyId)
    {
        item.SetProviderId(Episode, embyId);
        item.SetProviderId(Item, embyId);
    }

    public static string? ExtractItemIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var m = ItemPathRegex.Match(path);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        // Also check if .strm file on disk contains /emby/Videos/{id}/stream
        try
        {
            if (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var content = File.ReadAllText(path).Trim();
                var mUrl = Regex.Match(content, @"/emby/Videos/([a-zA-Z0-9_-]+)/stream", RegexOptions.IgnoreCase);
                if (mUrl.Success)
                {
                    return mUrl.Groups[1].Value;
                }

                var mPlugin = Regex.Match(content, @"/api/embyPlugin/stream/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
                if (mPlugin.Success)
                {
                    return mPlugin.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Ignore file read errors
        }

        return null;
    }

    public static string? ExtractSeriesIdFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var m = SeriesPathRegex.Match(path);
        return m.Success ? m.Groups[1].Value : ExtractItemIdFromPath(path);
    }
}

public class EmbyMovieExternalId : IExternalId
{
    public string ProviderName => "Emby Movie";
    public string Key => EmbyIdKeys.Movie;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
    public string? UrlFormatString => null;
    public bool Supports(IHasProviderIds item) => item is Movie;
}

public class EmbySeriesExternalId : IExternalId
{
    public string ProviderName => "Emby Series";
    public string Key => EmbyIdKeys.Series;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Series;
    public string? UrlFormatString => null;
    public bool Supports(IHasProviderIds item) => item is Series;
}

public class EmbyEpisodeExternalId : IExternalId
{
    public string ProviderName => "Emby Episode";
    public string Key => EmbyIdKeys.Episode;
    public ExternalIdMediaType? Type => ExternalIdMediaType.Episode;
    public string? UrlFormatString => null;
    public bool Supports(IHasProviderIds item) => item is Episode;
}
