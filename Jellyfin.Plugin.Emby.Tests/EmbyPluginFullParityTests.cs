using System.Text.Json;
using FluentAssertions;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Configuration;
using Jellyfin.Plugin.Emby.Models;
using Jellyfin.Plugin.Emby.Providers;
using Jellyfin.Plugin.Emby.ScheduledTasks;
using Xunit;

namespace Jellyfin.Plugin.Emby.Tests;

public class EmbyPluginFullParityTests
{
    [Fact]
    public void PluginConfiguration_ShouldHaveCleanUniversalDefaults()
    {
        var config = new PluginConfiguration();

        config.EmbyServerUrl.Should().BeEmpty();
        config.EmbyUsername.Should().BeEmpty();
        config.EmbyPassword.Should().BeEmpty();
        config.EmbyApiKey.Should().BeEmpty();

        config.EnableStreamingChannel.Should().BeTrue();
        config.EnablePublicFallbackForUnlinkedUsers.Should().BeTrue();
        config.EnableLibraryFederation.Should().BeTrue();
        config.EnableMoviesFederation.Should().BeTrue();
        config.EnableSeriesFederation.Should().BeTrue();
        config.AttachToExistingLibraries.Should().BeFalse();
        config.TargetMoviesLibraryName.Should().Be("Filme (Emby)");
        config.TargetSeriesLibraryName.Should().Be("Serien (Emby)");
        config.CreateDedicatedEmbyLibraries.Should().BeTrue();
        config.StrmLibraryPath.Should().Be("/data/emby");
        config.UseDirectEmbyStreamUrls.Should().BeTrue();

        config.EnablePlayheadSync.Should().BeFalse();
        config.EnableFavoriteSync.Should().BeFalse();
        config.FetchArtworkFromEmby.Should().BeTrue();

        config.EnableSeerrRequestGuard.Should().BeTrue();
        config.SeerrUrl.Should().Be("http://jellyseerr:5055");
        config.SonarrUrl.Should().Be("http://sonarr:8989");
        config.RadarrUrl.Should().Be("http://radarr:7878");
        config.EnableIncrementalSync.Should().BeTrue();
        config.SyncIntervalHours.Should().Be(24);
        config.ApiRequestDelayMs.Should().Be(50);
        config.UserAccounts.Should().NotBeNull();
    }

    [Fact]
    public void PluginConfiguration_IncrementalSync_ShouldPreserveSettings()
    {
        var config = new PluginConfiguration();
        config.EnableIncrementalSync.Should().BeTrue();

        config.LastSyncUtc = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        config.LastSyncItemCount = 42;

        config.LastSyncUtc.Should().NotBeNull();
        config.LastSyncItemCount.Should().Be(42);
    }

    [Fact]
    public void PluginConfiguration_ShouldAllowMultiUserMappingAndFallback()
    {
        var config = new PluginConfiguration
        {
            EmbyServerUrl = "https://emby.shared.example.com",
            EmbyApiKey = "shared_api_key_456",
            PrimaryJellyfinUser = "admin",
            EnablePublicFallbackForUnlinkedUsers = true
        };

        // Custom linked user
        config.UserAccounts.Add(new EmbyUserAccountEntry
        {
            JellyfinUser = "alice",
            JellyfinUserId = "alice_id_123",
            EmbyServerUrl = "https://emby.alice.example.com",
            EmbyUsername = "alice_emby",
            AccessToken = "alice_token_789"
        });

        // Alice gets her dedicated account
        var aliceAcc = config.GetAccountForUser("alice");
        aliceAcc.Should().NotBeNull();
        aliceAcc!.AccessToken.Should().Be("alice_token_789");
        aliceAcc.EmbyServerUrl.Should().Be("https://emby.alice.example.com");

        // Unlinked user gets shared fallback
        var bobAcc = config.GetAccountForUser("bob");
        bobAcc.Should().NotBeNull();
        bobAcc!.AccessToken.Should().Be("shared_api_key_456");
        bobAcc.EmbyServerUrl.Should().Be("https://emby.shared.example.com");
    }

    [Fact]
    public void EmbyIdKeys_ConstantsShouldBeExact()
    {
        EmbyIdKeys.Item.Should().Be("EmbyItem");
        EmbyIdKeys.Movie.Should().Be("EmbyMovie");
        EmbyIdKeys.Series.Should().Be("EmbySeries");
        EmbyIdKeys.Episode.Should().Be("EmbyEpisode");
    }

    [Fact]
    public void EmbyItem_ProviderIdsShouldParseProperly()
    {
        var json = """
        {
            "Id": "98765",
            "Name": "Sample Movie",
            "Type": "Movie",
            "ProductionYear": 2024,
            "RunTimeTicks": 72000000000,
            "CommunityRating": 8.5,
            "ProviderIds": {
                "Tmdb": "12345",
                "Imdb": "tt9876543",
                "Tvdb": "54321"
            },
            "MediaSources": [
                {
                    "Id": "src1",
                    "Container": "mkv",
                    "MediaStreams": [
                        {
                            "Type": "Video",
                            "Codec": "hevc",
                            "Width": 3840,
                            "Height": 2160,
                            "AspectRatio": "16:9",
                            "BitRate": 25000000
                        },
                        {
                            "Type": "Audio",
                            "Codec": "eac3",
                            "Channels": 6,
                            "Language": "ger"
                        }
                    ]
                }
            ]
        }
        """;

        var item = JsonSerializer.Deserialize<EmbyItemDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        item.Should().NotBeNull();
        item!.Id.Should().Be("98765");
        item.Name.Should().Be("Sample Movie");
        item.Type.Should().Be("Movie");
        item.ProductionYear.Should().Be(2024);
        item.ProviderIds.Should().NotBeNull();
        item.ProviderIds!["Tmdb"].Should().Be("12345");
        item.ProviderIds["Imdb"].Should().Be("tt9876543");
        item.MediaSources.Should().NotBeNull();
        item.MediaSources!.Should().HaveCount(1);
        item.MediaSources[0].MediaStreams.Should().HaveCount(2);
        item.MediaSources[0].MediaStreams![0].Codec.Should().Be("hevc");
        item.MediaSources[0].MediaStreams![0].Width.Should().Be(3840);
        item.MediaSources[0].MediaStreams![1].Channels.Should().Be(6);
    }

    [Fact]
    public void FormatMovieNfo_ShouldContainTmdbAndFileInfo()
    {
        var item = new EmbyItemDto
        {
            Id = "1001",
            Name = "Inception",
            OriginalTitle = "Inception",
            ProductionYear = 2010,
            Overview = "A thief who steals corporate secrets through dream-sharing technology.",
            CommunityRating = 8.8f,
            RunTimeTicks = 88800000000,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tmdb", "27205" },
                { "Imdb", "tt1375666" }
            },
            MediaSources = new List<EmbyMediaSourceDto>
            {
                new()
                {
                    Id = "src1",
                    Container = "mkv",
                    MediaStreams = new List<EmbyMediaStreamDto>
                    {
                        new() { Type = "Video", Codec = "h264", Width = 1920, Height = 1080 },
                        new() { Type = "Audio", Codec = "dts", Channels = 6, Language = "eng" }
                    }
                }
            }
        };

        var nfo = SyncEmbyLibraryTask.FormatMovieNfo(item, "https://emby.example.com", "srv123");
        nfo.Should().Contain("<title>Inception</title>");
        nfo.Should().Contain("<year>2010</year>");
        nfo.Should().Contain("<tmdbid>27205</tmdbid>");
        nfo.Should().Contain("<imdbid>tt1375666</imdbid>");
        nfo.Should().Contain("<uniqueid type=\"tmdb\" default=\"true\">27205</uniqueid>");
        nfo.Should().Contain("<uniqueid type=\"EmbyMovie\">1001</uniqueid>");
        nfo.Should().Contain("<streamdetails>");
        nfo.Should().Contain("<codec>h264</codec>");
        nfo.Should().Contain("<width>1920</width>");
        nfo.Should().Contain("<height>1080</height>");
        nfo.Should().Contain("<channels>6</channels>");
    }

    [Fact]
    public void FormatSeriesNfo_ShouldContainTvdbAndTmdb()
    {
        var item = new EmbyItemDto
        {
            Id = "2001",
            Name = "Breaking Bad",
            ProductionYear = 2008,
            Overview = "A chemistry teacher diagnosed with inoperable lung cancer turns to manufacturing methamphetamine.",
            ProviderIds = new Dictionary<string, string>
            {
                { "Tvdb", "81189" },
                { "Tmdb", "1396" },
                { "Imdb", "tt0903747" }
            }
        };

        var nfo = SyncEmbyLibraryTask.FormatSeriesNfo(item, "https://emby.example.com", "srv123");
        nfo.Should().Contain("<title>Breaking Bad</title>");
        nfo.Should().Contain("<tvdbid>81189</tvdbid>");
        nfo.Should().Contain("<tmdbid>1396</tmdbid>");
        nfo.Should().Contain("<imdb_id>tt0903747</imdb_id>");
        nfo.Should().Contain("<uniqueid type=\"tvdb\" default=\"true\">81189</uniqueid>");
    }

    [Fact]
    public void FormatEpisodeNfo_ShouldContainSeasonAndEpisodeNumbers()
    {
        var item = new EmbyItemDto
        {
            Id = "3001",
            Name = "Pilot",
            ParentIndexNumber = 1,
            IndexNumber = 1,
            ProductionYear = 2008,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tvdb", "349232" },
                { "Tmdb", "62085" }
            }
        };

        var nfo = SyncEmbyLibraryTask.FormatEpisodeNfo(item);
        nfo.Should().Contain("<title>Pilot</title>");
        nfo.Should().Contain("<season>1</season>");
        nfo.Should().Contain("<episode>1</episode>");
        nfo.Should().Contain("<uniqueid type=\"tvdb\" default=\"true\">349232</uniqueid>");
        nfo.Should().Contain("<uniqueid type=\"tmdb\">62085</uniqueid>");
    }
}
