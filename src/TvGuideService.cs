using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvGuide;

public class TvGuideService : ILiveTvService
{
    private readonly ChannelManager _channelManager;
    private readonly ScheduleGenerator _scheduleGenerator;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<TvGuideService> _logger;

    public TvGuideService(
        ChannelManager channelManager,
        ScheduleGenerator scheduleGenerator,
        IServerApplicationHost appHost,
        ILogger<TvGuideService> logger)
    {
        _channelManager = channelManager;
        _scheduleGenerator = scheduleGenerator;
        _appHost = appHost;
        _logger = logger;
    }

    public string Name => "TvGuide";

    public string HomePageUrl => string.Empty;

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = _channelManager.GetChannels();
        _logger.LogInformation("TvGuide providing {Count} genre channels", channels.Count);
        return Task.FromResult<IEnumerable<ChannelInfo>>(channels);
    }

    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var genres = _channelManager.GetGenres();
        var genre = ChannelManager.ChannelIdToGenre(channelId, genres);
        var items = _channelManager.GetItemsForGenre(genre);

        if (items.Count == 0)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        var programs = new List<ProgramInfo>();
        var currentWeekStart = ScheduleGenerator.GetWeekStart(startDateUtc);

        while (currentWeekStart < endDateUtc)
        {
            var schedule = _scheduleGenerator.GenerateWeekSchedule(channelId, items, currentWeekStart);
            foreach (var slot in schedule)
            {
                if (slot.EndUtc > startDateUtc && slot.StartUtc < endDateUtc)
                {
                    programs.Add(SlotToProgramInfo(slot, channelId));
                }
            }

            currentWeekStart = currentWeekStart.AddDays(7);
        }

        return Task.FromResult<IEnumerable<ProgramInfo>>(programs);
    }

    public Task<MediaSourceInfo> GetChannelStream(
        string channelId, string streamId, CancellationToken cancellationToken)
    {
        var genres = _channelManager.GetGenres();
        var genre = ChannelManager.ChannelIdToGenre(channelId, genres);
        var items = _channelManager.GetItemsForGenre(genre);

        // Use the published server URL so clients can reach the stream directly.
        // GetSmartApiUrl returns PublishedServerUriBySubnet if configured,
        // otherwise falls back to the network bind address.
        var baseUrl = _appHost.GetSmartApiUrl(string.Empty);
        var hlsUrl = $"{baseUrl}/api/tvguide/hls/{channelId}/stream.m3u8";

        _logger.LogInformation("TvGuide GetChannelStream for {ChannelId}, URL: {Url}", channelId, hlsUrl);

        var mediaSource = new MediaSourceInfo
        {
            Id = channelId,
            Path = hlsUrl,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            IsInfiniteStream = true,
            BufferMs = 0,
            // DirectStream = false: Jellyfin 10.11.5 hardcodes EnableDirectStream=false
            // for HTTP sources (MediaInfoHelper.cs:252), so this has no effect anyway.
            // DirectPlay = false: Jellyfin proxies HLS via stream.hls?Static=true which
            // is a static file proxy — the client can't poll for updated segment lists,
            // breaking live HLS playback. Direct stream is hardcoded off for HTTP in
            // Jellyfin 10.11.5 (MediaInfoHelper.cs:252). So we always go through the
            // transcode path (~15s startup) which reads our HLS and re-muxes to its own.
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            Container = "hls",
            RequiresOpening = false,
            RequiresClosing = false,
            SupportsProbing = false,
        };

        // Everything is re-encoded: video to H.264 (libx264), audio to AAC.
        // This ensures uniform codecs across all concat file transitions.
        mediaSource.MediaStreams = new List<MediaStream>
        {
            new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = "h264",
                Profile = "High",
                Level = 41,
                BitRate = 4000000,
                Width = 1920,
                Height = 1080,
                IsDefault = true,
                PixelFormat = "yuv420p",
                BitDepth = 8,
            },
            new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = 1,
                Codec = "aac",
                BitRate = 384000,
                SampleRate = 48000,
                Channels = 2,
                ChannelLayout = "stereo",
                IsDefault = true,
            },
        };
        mediaSource.Bitrate = 4384000;

        return Task.FromResult(mediaSource);
    }

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId, CancellationToken cancellationToken)
    {
        var source = GetChannelStream(channelId, null, cancellationToken).Result;
        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    // Timer/recording methods — no-ops for virtual channels.

    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<TimerInfo>());

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(
        CancellationToken cancellationToken, ProgramInfo program = null)
        => Task.FromResult(new SeriesTimerInfo());

    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<SeriesTimerInfo>());

    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ResetTuner(string id, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private ProgramInfo SlotToProgramInfo(ScheduleSlot slot, string channelId)
    {
        var item = slot.Item;

        // Resolve the best image source: item itself, or parent series for episodes
        string imagePath = item.PrimaryImagePath;
        if (string.IsNullOrEmpty(imagePath) && item is Episode ep)
        {
            imagePath = ep.Series?.PrimaryImagePath;
        }

        if (string.IsNullOrEmpty(imagePath))
        {
            _logger.LogDebug(
                "No image for {Type} {Name} (Id={Id})",
                item.GetType().Name, item.Name, item.Id);
        }

        var program = new ProgramInfo
        {
            Id = $"tvguide_{channelId}_{slot.StartUtc.Ticks}",
            ChannelId = channelId,
            Name = item is Episode e ? (e.Series?.Name ?? item.Name) : item.Name,
            Overview = item.Overview,
            StartDate = slot.StartUtc,
            EndDate = slot.EndUtc,
            ProductionYear = item.ProductionYear,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            ImagePath = imagePath,
            HasImage = !string.IsNullOrEmpty(imagePath),
            IsMovie = item is Movie,
            IsSeries = item is Episode,
        };

        if (item.Genres != null && item.Genres.Length > 0)
        {
            program.Genres = item.Genres.ToList();
        }

        if (item is Episode episode)
        {
            program.EpisodeTitle = episode.Name;
            program.SeasonNumber = episode.ParentIndexNumber;
            program.EpisodeNumber = episode.IndexNumber;
            program.SeriesId = episode.Series?.Id.ToString("N");
        }

        return program;
    }
}
