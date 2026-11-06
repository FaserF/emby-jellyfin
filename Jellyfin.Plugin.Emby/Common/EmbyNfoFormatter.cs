using System.Net;
using System.Text;
using Jellyfin.Plugin.Emby.Models;

namespace Jellyfin.Plugin.Emby.Common;

public static class EmbyNfoFormatter
{
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

        var colName = !string.IsNullOrWhiteSpace(item.CollectionName)
            ? item.CollectionName
            : item.Collections?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Name))?.Name;

        if (!string.IsNullOrWhiteSpace(colName))
        {
            sb.AppendLine("  <set>");
            sb.AppendLine($"    <name>{WebUtility.HtmlEncode(colName.Trim())}</name>");
            sb.AppendLine("  </set>");
        }

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

    public static void AppendFileInfoXml(StringBuilder sb, EmbyItemDto item)
    {
        var ms = item.MediaSources?.FirstOrDefault();
        var ticks = ms?.RunTimeTicks ?? item.RunTimeTicks ?? 0;
        var durSec = ticks > 0 ? ticks / 10_000_000 : 0;

        if (ms?.MediaStreams != null && ms.MediaStreams.Count > 0)
        {
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

                    if (!string.IsNullOrWhiteSpace(st.AspectRatio))
                    {
                        sb.AppendLine($"        <aspect>{WebUtility.HtmlEncode(st.AspectRatio)}</aspect>");
                    }

                    if (st.BitRate.HasValue)
                    {
                        sb.AppendLine($"        <bitrate>{st.BitRate.Value}</bitrate>");
                    }

                    if (st.BitDepth.HasValue)
                    {
                        sb.AppendLine($"        <bitdepth>{st.BitDepth.Value}</bitdepth>");
                    }

                    if (!string.IsNullOrWhiteSpace(st.VideoRange))
                    {
                        sb.AppendLine($"        <hdrtype>{WebUtility.HtmlEncode(st.VideoRange)}</hdrtype>");
                    }

                    sb.AppendLine($"        <scantype>{(st.IsInterlaced ? "interlaced" : "progressive")}</scantype>");

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

                    if (!string.IsNullOrWhiteSpace(st.DisplayTitle ?? st.Title))
                    {
                        sb.AppendLine($"        <title>{WebUtility.HtmlEncode(st.DisplayTitle ?? st.Title)}</title>");
                    }

                    sb.AppendLine($"        <channels>{st.Channels ?? 2}</channels>");
                    if (st.BitRate.HasValue)
                    {
                        sb.AppendLine($"        <bitrate>{st.BitRate.Value}</bitrate>");
                    }

                    if (st.SampleRate.HasValue)
                    {
                        sb.AppendLine($"        <samplingrate>{st.SampleRate.Value}</samplingrate>");
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

                    if (!string.IsNullOrWhiteSpace(st.DisplayTitle ?? st.Title))
                    {
                        sb.AppendLine($"        <title>{WebUtility.HtmlEncode(st.DisplayTitle ?? st.Title)}</title>");
                    }

                    if (st.IsForced)
                    {
                        sb.AppendLine("        <forced>true</forced>");
                    }
                    if (st.IsDefault)
                    {
                        sb.AppendLine("        <default>true</default>");
                    }

                    sb.AppendLine("      </subtitle>");
                }
            }

            sb.AppendLine("    </streamdetails>");
            sb.AppendLine("  </fileinfo>");
        }

        if (item.Chapters != null && item.Chapters.Count > 0)
        {
            sb.AppendLine("  <chapters>");
            foreach (var ch in item.Chapters)
            {
                sb.AppendLine("    <chapter>");
                if (!string.IsNullOrWhiteSpace(ch.Name))
                {
                    sb.AppendLine($"      <name>{WebUtility.HtmlEncode(ch.Name.Trim())}</name>");
                }
                sb.AppendLine($"      <startpositionticks>{ch.StartPositionTicks}</startpositionticks>");
                sb.AppendLine("    </chapter>");
            }
            sb.AppendLine("  </chapters>");
        }
    }
}
