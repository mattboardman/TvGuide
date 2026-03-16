using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvGuide;

public class TvGuideService : ILiveTvService, ISupportsDirectStreamProvider
{
    private readonly ChannelManager _channelManager;
    private readonly ScheduleGenerator _scheduleGenerator;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TvGuideService> _logger;

    public TvGuideService(
        ChannelManager channelManager,
        ScheduleGenerator scheduleGenerator,
        IMediaEncoder mediaEncoder,
        IServerApplicationHost appHost,
        IConfigurationManager configurationManager,
        ILoggerFactory loggerFactory,
        ILogger<TvGuideService> logger)
    {
        _channelManager = channelManager;
        _scheduleGenerator = scheduleGenerator;
        _mediaEncoder = mediaEncoder;
        _appHost = appHost;
        _configurationManager = configurationManager;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public string Name => "TvGuide";

    public string HomePageUrl => string.Empty;

    public Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
        => GetChannelsInternalAsync(cancellationToken);

    private async Task<IEnumerable<ChannelInfo>> GetChannelsInternalAsync(CancellationToken cancellationToken)
    {
        var channels = _channelManager.GetChannels();
        await _channelManager.ClearPersistedChannelImagesAsync(channels, Name, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("TvGuide providing {Count} genre channels", channels.Count);
        return channels;
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
        return Task.FromResult(CreateManagedMediaSource(channelId));
    }

    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(
        string channelId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new List<MediaSourceInfo> { CreateManagedMediaSource(channelId) });
    }

    public async Task<ILiveStream> GetChannelStreamWithDirectStreamProvider(
        string channelId,
        string streamId,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        var existing = string.IsNullOrEmpty(streamId)
            ? null
            : currentLiveStreams.FirstOrDefault(i => string.Equals(i.OriginalStreamId, streamId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && existing.EnableStreamSharing)
        {
            existing.ConsumerCount++;
            _logger.LogInformation(
                "Reusing TvGuide live stream {StreamId} for {ChannelId}; consumer count is now {ConsumerCount}",
                streamId,
                channelId,
                existing.ConsumerCount);
            return existing;
        }

        var slots = GetSlotsFromNow(channelId);
        if (slots.Count == 0)
        {
            throw new InvalidOperationException($"No scheduled items found for channel {channelId}.");
        }

        var seekSeconds = Math.Max(0, (DateTime.UtcNow - slots[0].StartUtc).TotalSeconds);
        var liveStream = new TvGuideLiveStream(
            channelId,
            slots,
            seekSeconds,
            CreateManagedMediaSource(channelId),
            _mediaEncoder,
            _appHost,
            _configurationManager,
            _loggerFactory.CreateLogger<TvGuideLiveStream>());
        liveStream.OriginalStreamId = streamId ?? channelId;

        _logger.LogInformation(
            "Opening managed TvGuide live stream for {ChannelId} with {SlotCount} scheduled items",
            channelId,
            slots.Count);

        await liveStream.Open(cancellationToken).ConfigureAwait(false);
        return liveStream;
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

    private MediaSourceInfo CreateManagedMediaSource(string channelId)
    {
        var mediaSource = new MediaSourceInfo
        {
            Id = channelId,
            Path = string.Empty,
            Protocol = MediaProtocol.Http,
            IsRemote = false,
            IsInfiniteStream = true,
            BufferMs = 0,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            Container = "ts",
            RequiresOpening = true,
            RequiresClosing = true,
            SupportsProbing = false,
            IgnoreDts = true,
            UseMostCompatibleTranscodingProfile = true,
            MediaStreams = new List<MediaStream>
            {
                new()
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
                    NalLengthSize = "0",
                    IsInterlaced = false,
                },
                new()
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
            },
            Bitrate = 4384000,
        };

        return mediaSource;
    }

    private List<ScheduleSlot> GetSlotsFromNow(string channelId)
    {
        var genres = _channelManager.GetGenres();
        var genre = ChannelManager.ChannelIdToGenre(channelId, genres);
        var items = _channelManager.GetItemsForGenre(genre);

        return _scheduleGenerator.GetSlotsFromNow(channelId, items, DateTime.UtcNow, 20);
    }

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
