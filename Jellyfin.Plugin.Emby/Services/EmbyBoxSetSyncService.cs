using System.Net;
using System.Text;
using Jellyfin.Plugin.Emby.Api;
using Jellyfin.Plugin.Emby.Models;
using Jellyfin.Plugin.Emby.ScheduledTasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Emby.Services;

/// <summary>
/// Service responsible for federating Emby BoxSets / Collections into Jellyfin native collection structures (.xml, poster.jpg, backdrop.jpg).
/// </summary>
public static class EmbyBoxSetSyncService
{
    public static async Task<int> SyncCollectionsAsync(
        EmbyApiClient client,
        string baseUrl,
        string token,
        IReadOnlyList<EmbyItemDto> movies,
        string moviesDir,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var changes = 0;
        try
        {
            var colToMovies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in movies)
            {
                if (string.IsNullOrWhiteSpace(m.Id))
                {
                    continue;
                }

                var colNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(m.CollectionName))
                {
                    colNames.Add(m.CollectionName.Trim());
                }

                if (m.Collections != null)
                {
                    foreach (var c in m.Collections)
                    {
                        if (!string.IsNullOrWhiteSpace(c?.Name))
                        {
                            colNames.Add(c.Name.Trim());
                        }
                    }
                }

                if (colNames.Count == 0)
                {
                    continue;
                }

                var folderName = SyncEmbyLibraryTask.BuildMovieFolderName(m);
                var strmPath = Path.Combine(moviesDir, folderName, $"{folderName}.strm");

                foreach (var cn in colNames)
                {
                    if (!colToMovies.TryGetValue(cn, out var list))
                    {
                        list = new List<string>();
                        colToMovies[cn] = list;
                    }

                    list.Add(strmPath);
                }
            }

            if (colToMovies.Count == 0)
            {
                return 0;
            }

            var collectionsDir = Directory.Exists("/config/data/data/collections")
                ? "/config/data/data/collections"
                : (Directory.Exists("/config/data/collections") ? "/config/data/collections" : "/config/data/data/collections");

            Directory.CreateDirectory(collectionsDir);

            // Fetch BoxSets metadata from Emby
            var embyBoxSets = await client.GetItemsAsync("BoxSet", cancellationToken: cancellationToken).ConfigureAwait(false);
            var boxSetByName = embyBoxSets
                .Where(b => !string.IsNullOrWhiteSpace(b.Name))
                .ToDictionary(b => b.Name!, b => b, StringComparer.OrdinalIgnoreCase);

            logger.LogInformation("Emby Federation: syncing {Count} boxsets to {Dir}", colToMovies.Count, collectionsDir);

            foreach (var (colName, strmPaths) in colToMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var safeName = SyncEmbyLibraryTask.SanitizeFileName(colName);
                var bDir = Path.Combine(collectionsDir, $"{safeName} [boxset]");
                Directory.CreateDirectory(bDir);

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
                sb.AppendLine("<Item>");
                sb.AppendLine($"  <LocalTitle>{WebUtility.HtmlEncode(colName)}</LocalTitle>");
                sb.AppendLine("  <CollectionItems>");
                foreach (var p in strmPaths)
                {
                    sb.AppendLine("    <CollectionItem>");
                    sb.AppendLine($"      <Path>{WebUtility.HtmlEncode(p)}</Path>");
                    sb.AppendLine("    </CollectionItem>");
                }
                sb.AppendLine("  </CollectionItems>");
                sb.AppendLine("</Item>");

                var xmlPath = Path.Combine(bDir, "collection.xml");
                if (SyncEmbyLibraryTask.WriteIfChanged(xmlPath, sb.ToString(), logger))
                {
                    changes++;
                }

                if (boxSetByName.TryGetValue(colName, out var bs) && !string.IsNullOrWhiteSpace(bs.Id))
                {
                    var posterFile = Path.Combine(bDir, "poster.jpg");
                    if (!File.Exists(posterFile))
                    {
                        try
                        {
                            var posterUrl = $"{baseUrl.TrimEnd('/')}/emby/Items/{Uri.EscapeDataString(bs.Id)}/Images/Primary?api_key={Uri.EscapeDataString(token)}";
                            using var resp = await client.HttpClient.GetAsync(posterUrl, cancellationToken).ConfigureAwait(false);
                            if (resp.IsSuccessStatusCode)
                            {
                                var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                                if (bytes.Length > 0)
                                {
                                    await File.WriteAllBytesAsync(posterFile, bytes, cancellationToken).ConfigureAwait(false);
                                    changes++;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore artwork download failures
                        }
                    }

                    var backdropFile = Path.Combine(bDir, "backdrop.jpg");
                    if (!File.Exists(backdropFile))
                    {
                        try
                        {
                            var backdropUrl = $"{baseUrl.TrimEnd('/')}/emby/Items/{Uri.EscapeDataString(bs.Id)}/Images/Backdrop/0?api_key={Uri.EscapeDataString(token)}";
                            using var resp = await client.HttpClient.GetAsync(backdropUrl, cancellationToken).ConfigureAwait(false);
                            if (resp.IsSuccessStatusCode)
                            {
                                var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                                if (bytes.Length > 0)
                                {
                                    await File.WriteAllBytesAsync(backdropFile, bytes, cancellationToken).ConfigureAwait(false);
                                    changes++;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore artwork download failures
                        }
                    }
                }
            }

            logger.LogInformation("Emby Federation: completed syncing {Count} boxsets with {Changes} changes.", colToMovies.Count, changes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to synchronize Emby BoxSets to Jellyfin");
        }

        return changes;
    }
}
