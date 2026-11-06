using FluentAssertions;
using Jellyfin.Plugin.Emby.Common;
using Jellyfin.Plugin.Emby.Models;
using Xunit;

namespace Jellyfin.Plugin.Emby.Tests;

public class LocalLibraryMatcherTests
{
    [Theory]
    [InlineData("Suits", "suits")]
    [InlineData("Suits (2011)", "suits")]
    [InlineData("Suits (2011) [tvdbid-247808]", "suits")]
    [InlineData("The.Rookie.2018", "the rookie")]
    [InlineData("The Rookie (2018) [EmbySeries-12345]", "the rookie")]
    public void NormalizeTitle_ShouldNormalizeCleanly(string input, string expected)
    {
        var result = LocalLibraryMatcher.NormalizeTitle(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void MatchLocalSeries_ShouldMatchByProviderId()
    {
        var localList = new List<LocalMediaInfo>
        {
            new()
            {
                Path = "/data/tv/Suits",
                FolderName = "Suits",
                NormalizedTitle = "suits",
                Year = 2011,
                TmdbId = "37680",
                ImdbId = "tt1632701",
                TvdbId = "247808"
            }
        };

        var embyItem = new EmbyItemDto
        {
            Name = "Suits",
            ProductionYear = 2011,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tvdb", "247808" },
                { "Tmdb", "37680" }
            }
        };

        var match = LocalLibraryMatcher.MatchLocalSeries(embyItem, localList);
        match.Should().NotBeNull();
        match!.Path.Should().Be("/data/tv/Suits");
    }

    [Fact]
    public void MatchLocalSeries_ShouldMatchByTitleAndYear()
    {
        var localList = new List<LocalMediaInfo>
        {
            new()
            {
                Path = "/data/tv/The Rookie",
                FolderName = "The Rookie",
                NormalizedTitle = "the rookie",
                Year = 2018
            }
        };

        var embyItem = new EmbyItemDto
        {
            Name = "The Rookie (2018)",
            ProductionYear = 2018,
            ProviderIds = new Dictionary<string, string>()
        };

        var match = LocalLibraryMatcher.MatchLocalSeries(embyItem, localList);
        match.Should().NotBeNull();
        match!.Path.Should().Be("/data/tv/The Rookie");
    }

    [Fact]
    public void MatchLocalMovie_ShouldMatchByTmdbId()
    {
        var localList = new List<LocalMediaInfo>
        {
            new()
            {
                Path = "/data/movies/Inception (2010)",
                FolderName = "Inception (2010)",
                NormalizedTitle = "inception",
                Year = 2010,
                TmdbId = "27205"
            }
        };

        var embyItem = new EmbyItemDto
        {
            Name = "Inception",
            ProductionYear = 2010,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tmdb", "27205" }
            }
        };

        var match = LocalLibraryMatcher.MatchLocalMovie(embyItem, localList);
        match.Should().NotBeNull();
        match!.Path.Should().Be("/data/movies/Inception (2010)");
    }
}
