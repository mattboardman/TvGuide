using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.TvGuide;

public class ChannelManager
{
    private readonly ILibraryManager _libraryManager;

    public ChannelManager(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public List<string> GetGenres()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
        };

        var result = _libraryManager.GetGenres(query);
        return result.Items
            .Select(g => g.Item.Name)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .OrderBy(g => g)
            .ToList();
    }

    public List<BaseItem> GetItemsForGenre(string genre)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Genres = new[] { genre },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
        }).ToList();
    }

    public List<BaseItem> GetItemsForCustomChannel(CustomChannelDefinition channel)
    {
        var items = new List<BaseItem>();
        foreach (var idStr in channel.ItemIds)
        {
            if (!Guid.TryParse(idStr, out var guid))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(guid);
            if (item is null)
            {
                continue;
            }

            if (item is Series)
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = new[] { guid },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                    OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                });
                items.AddRange(episodes);
            }
            else if (item is Movie || item is Episode)
            {
                items.Add(item);
            }
        }

        return items;
    }

    public List<BaseItem> ResolveItemsForChannel(string channelId)
    {
        var config = Plugin.GetCurrentConfiguration();

        if (IsCustomChannel(channelId))
        {
            var channel = config.CustomChannels
                .FirstOrDefault(c => CustomChannelToId(c.Name) == channelId);
            return channel is not null ? GetItemsForCustomChannel(channel) : new List<BaseItem>();
        }

        var genres = GetGenres();
        var genre = ChannelIdToGenre(channelId, genres);
        var items = GetItemsForGenre(genre);

        var genreOverride = config.GenreOverrides.FirstOrDefault(o => o.Genre == genre);
        if (genreOverride?.ExcludedItemIds is { Count: > 0 })
        {
            var excludedGuids = new HashSet<Guid>();
            foreach (var idStr in genreOverride.ExcludedItemIds)
            {
                if (Guid.TryParse(idStr, out var g))
                {
                    excludedGuids.Add(g);
                }
            }

            items = items.Where(item =>
            {
                if (excludedGuids.Contains(item.Id))
                {
                    return false;
                }

                if (item is Episode ep && ep.Series is not null
                    && excludedGuids.Contains(ep.Series.Id))
                {
                    return false;
                }

                return true;
            }).ToList();
        }

        return items;
    }

    public List<ChannelInfo> GetChannels()
    {
        var genres = GetGenres();
        var config = Plugin.GetCurrentConfiguration();
        var channels = new List<ChannelInfo>();
        int channelNumber = 1;

        for (int i = 0; i < genres.Count; i++)
        {
            var genreOverride = config.GenreOverrides.FirstOrDefault(o => o.Genre == genres[i]);
            if (genreOverride is not null && !genreOverride.Enabled)
            {
                continue;
            }

            var displayName = genreOverride is not null && !string.IsNullOrEmpty(genreOverride.DisplayName)
                ? genreOverride.DisplayName
                : genres[i];

            channels.Add(new ChannelInfo
            {
                Id = GenreToChannelId(genres[i]),
                Name = displayName,
                Number = channelNumber.ToString(),
                ChannelType = ChannelType.TV,
                ChannelGroup = "TvGuide",
                HasImage = false,
            });
            channelNumber++;
        }

        foreach (var custom in config.CustomChannels)
        {
            if (string.IsNullOrWhiteSpace(custom.Name) || !custom.Enabled)
            {
                continue;
            }

            var items = GetItemsForCustomChannel(custom);
            if (items.Count == 0)
            {
                continue;
            }

            channels.Add(new ChannelInfo
            {
                Id = CustomChannelToId(custom.Name),
                Name = custom.Name,
                Number = channelNumber.ToString(),
                ChannelType = ChannelType.TV,
                ChannelGroup = "TvGuide Custom",
                HasImage = false,
            });
            channelNumber++;
        }

        return channels;
    }

    public async Task ClearPersistedChannelImagesAsync(
        IReadOnlyCollection<ChannelInfo> channels,
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (channels.Count == 0)
        {
            return;
        }

        var channelIds = channels.Select(c => c.Id).ToHashSet();
        var liveChannels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel },
        }).OfType<LiveTvChannel>()
            .Where(channel => string.Equals(channel.ServiceName, serviceName))
            .Where(channel => channelIds.Contains(channel.ExternalId))
            .ToList();

        foreach (var channel in liveChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var primaryImages = channel.GetImages(ImageType.Primary).ToList();
            if (primaryImages.Count == 0)
            {
                continue;
            }

            channel.RemoveImages(primaryImages);
            await channel.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string GenreToChannelId(string genre)
        => "tvguide_" + genre.ToLowerInvariant().Replace(" ", "_");

    public static string ChannelIdToGenre(string channelId, List<string> genres)
    {
        foreach (var genre in genres)
        {
            if (GenreToChannelId(genre) == channelId)
            {
                return genre;
            }
        }

        return channelId.Replace("tvguide_", string.Empty).Replace("_", " ");
    }

    public static string CustomChannelToId(string name)
        => "tvguide_custom_" + Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "_").Trim('_');

    public static bool IsCustomChannel(string channelId)
        => channelId.StartsWith("tvguide_custom_", StringComparison.Ordinal);
}
