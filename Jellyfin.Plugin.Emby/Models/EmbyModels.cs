using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Emby.Models;

public class EmbyAuthResponse
{
    [JsonPropertyName("AccessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("ServerId")]
    public string ServerId { get; set; } = string.Empty;

    [JsonPropertyName("User")]
    public EmbyUserDto? User { get; set; }
}

public class EmbyUserDto
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Policy")]
    public EmbyUserPolicyDto? Policy { get; set; }
}

public class EmbyUserPolicyDto
{
    [JsonPropertyName("EnableContentDownloading")]
    public bool EnableContentDownloading { get; set; }

    [JsonPropertyName("EnableMediaPlayback")]
    public bool EnableMediaPlayback { get; set; }
}

public class EmbyItemsEnvelope
{
    [JsonPropertyName("Items")]
    public List<EmbyItemDto> Items { get; set; } = new();

    [JsonPropertyName("TotalRecordCount")]
    public int TotalRecordCount { get; set; }
}

public class EmbyItemDto
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ServerId")]
    public string ServerId { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("OriginalTitle")]
    public string? OriginalTitle { get; set; }

    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("Overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("ProductionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("PremiereDate")]
    public DateTime? PremiereDate { get; set; }

    [JsonPropertyName("CommunityRating")]
    public float? CommunityRating { get; set; }

    [JsonPropertyName("OfficialRating")]
    public string? OfficialRating { get; set; }

    [JsonPropertyName("CustomRating")]
    public string? CustomRating { get; set; }

    [JsonPropertyName("CriticRating")]
    public float? CriticRating { get; set; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }

    [JsonPropertyName("SortName")]
    public string? SortName { get; set; }

    [JsonPropertyName("ForcedSortName")]
    public string? ForcedSortName { get; set; }

    [JsonPropertyName("Taglines")]
    public List<string>? Taglines { get; set; }

    [JsonPropertyName("Tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("Studios")]
    public List<EmbyStudioDto>? Studios { get; set; }

    [JsonPropertyName("People")]
    public List<EmbyPersonDto>? People { get; set; }

    [JsonPropertyName("Chapters")]
    public List<EmbyChapterDto>? Chapters { get; set; }

    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("AirDays")]
    public List<string>? AirDays { get; set; }

    [JsonPropertyName("AirTime")]
    public string? AirTime { get; set; }

    [JsonPropertyName("DateCreated")]
    public DateTime? DateCreated { get; set; }

    [JsonPropertyName("SeriesId")]
    public string? SeriesId { get; set; }

    [JsonPropertyName("SeriesName")]
    public string? SeriesName { get; set; }

    [JsonPropertyName("SeasonId")]
    public string? SeasonId { get; set; }

    [JsonPropertyName("ParentIndexNumber")]
    public int? ParentIndexNumber { get; set; }

    [JsonPropertyName("IndexNumber")]
    public int? IndexNumber { get; set; }

    [JsonPropertyName("Genres")]
    public List<string>? Genres { get; set; }

    [JsonPropertyName("ProviderIds")]
    public Dictionary<string, string>? ProviderIds { get; set; }

    [JsonPropertyName("MediaSources")]
    public List<EmbyMediaSourceDto>? MediaSources { get; set; }

    [JsonPropertyName("UserData")]
    public EmbyUserDataDto? UserData { get; set; }

    [JsonPropertyName("ImageTags")]
    public Dictionary<string, string>? ImageTags { get; set; }

    [JsonPropertyName("BackdropImageTags")]
    public List<string>? BackdropImageTags { get; set; }

    public string? GetProviderId(string key)
    {
        if (ProviderIds == null)
        {
            return null;
        }

        foreach (var kv in ProviderIds)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                return kv.Value.Trim();
            }
        }

        return null;
    }
}

public class EmbyMediaSourceDto
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("Container")]
    public string? Container { get; set; }

    [JsonPropertyName("Size")]
    public long? Size { get; set; }

    [JsonPropertyName("Bitrate")]
    public int? Bitrate { get; set; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }

    [JsonPropertyName("MediaStreams")]
    public List<EmbyMediaStreamDto>? MediaStreams { get; set; }
}

public class EmbyMediaStreamDto
{
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("Index")]
    public int Index { get; set; }

    [JsonPropertyName("Codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("Language")]
    public string? Language { get; set; }

    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("DisplayTitle")]
    public string? DisplayTitle { get; set; }

    [JsonPropertyName("Width")]
    public int? Width { get; set; }

    [JsonPropertyName("Height")]
    public int? Height { get; set; }

    [JsonPropertyName("BitRate")]
    public int? BitRate { get; set; }

    [JsonPropertyName("Channels")]
    public int? Channels { get; set; }

    [JsonPropertyName("SampleRate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("IsDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("IsForced")]
    public bool IsForced { get; set; }

    [JsonPropertyName("IsExternal")]
    public bool IsExternal { get; set; }

    [JsonPropertyName("DisplayLanguage")]
    public string? DisplayLanguage { get; set; }

    [JsonPropertyName("ChannelLayout")]
    public string? ChannelLayout { get; set; }

    [JsonPropertyName("Profile")]
    public string? Profile { get; set; }

    [JsonPropertyName("Level")]
    public double? Level { get; set; }

    [JsonPropertyName("PixelFormat")]
    public string? PixelFormat { get; set; }

    [JsonPropertyName("AspectRatio")]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("VideoRange")]
    public string? VideoRange { get; set; }

    [JsonPropertyName("AverageFrameRate")]
    public float? AverageFrameRate { get; set; }

    [JsonPropertyName("RealFrameRate")]
    public float? RealFrameRate { get; set; }

    [JsonPropertyName("BitDepth")]
    public int? BitDepth { get; set; }

    [JsonPropertyName("IsInterlaced")]
    public bool IsInterlaced { get; set; }

    [JsonPropertyName("IsHearingImpaired")]
    public bool IsHearingImpaired { get; set; }

    [JsonPropertyName("IsTextSubtitleStream")]
    public bool IsTextSubtitleStream { get; set; }

    [JsonPropertyName("SupportsExternalStream")]
    public bool SupportsExternalStream { get; set; }
}

public class EmbyUserDataDto
{
    [JsonPropertyName("PlaybackPositionTicks")]
    public long PlaybackPositionTicks { get; set; }

    [JsonPropertyName("PlayCount")]
    public int PlayCount { get; set; }

    [JsonPropertyName("IsFavorite")]
    public bool IsFavorite { get; set; }

    [JsonPropertyName("Played")]
    public bool Played { get; set; }
}

public class EmbyAvailabilityMatch
{
    public bool Available { get; set; }
    public string EmbyItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string EmbyWebUrl { get; set; } = string.Empty;
    public string DirectStreamUrl { get; set; } = string.Empty;
}

public class EmbyPersonDto
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Role")]
    public string? Role { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Id")]
    public string? Id { get; set; }

    [JsonPropertyName("PrimaryImageTag")]
    public string? PrimaryImageTag { get; set; }
}

public class EmbyStudioDto
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Id")]
    public string? Id { get; set; }
}

public class EmbyChapterDto
{
    [JsonPropertyName("StartPositionTicks")]
    public long StartPositionTicks { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("ImageTag")]
    public string? ImageTag { get; set; }
}
