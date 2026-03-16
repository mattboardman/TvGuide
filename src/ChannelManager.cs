using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
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

    public List<ChannelInfo> GetChannels()
    {
        var genres = GetGenres();
        var channels = new List<ChannelInfo>();

        for (int i = 0; i < genres.Count; i++)
        {
            channels.Add(new ChannelInfo
            {
                Id = GenreToChannelId(genres[i]),
                Name = genres[i],
                Number = (i + 1).ToString(),
                ChannelType = ChannelType.TV,
                ChannelGroup = "TvGuide",
                HasImage = false,
            });
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
}
