using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvGuide;

public sealed class HlsManager : IDisposable
{
    private readonly ChannelManager _channelManager;
    private readonly ScheduleGenerator _scheduleGenerator;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<HlsManager> _logger;
    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new();
    private readonly Timer _cleanupTimer;

    public HlsManager(
        ChannelManager channelManager,
        ScheduleGenerator scheduleGenerator,
        IMediaEncoder mediaEncoder,
        IServerApplicationHost appHost,
        ILogger<HlsManager> logger)
    {
        _channelManager = channelManager;
        _scheduleGenerator = scheduleGenerator;
        _mediaEncoder = mediaEncoder;
        _appHost = appHost;
        _logger = logger;
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Returns the session temp directory for a channel, starting the HLS
    /// producer if it isn't already running.
    /// </summary>
    public string EnsureSession(string channelId)
    {
        while (true)
        {
            var session = _sessions.GetOrAdd(channelId, CreateSession);

            // If the producer has finished (all slots exhausted or error),
            // tear it down and create a fresh one.
            if (session.Task != null && session.Task.IsCompleted)
            {
                _logger.LogInformation("HLS session for {ChannelId} has ended, recreating", channelId);
                StopSession(channelId);
                continue;
            }

            session.LastAccess = DateTime.UtcNow;
            return session.Dir;
        }
    }

    /// <summary>
    /// Updates the last-access timestamp so the idle cleanup doesn't kill
    /// a session that is still being watched.
    /// </summary>
    public void Touch(string channelId)
    {
        if (_sessions.TryGetValue(channelId, out var session))
        {
            session.LastAccess = DateTime.UtcNow;
        }
    }

    private HlsSession CreateSession(string channelId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tvguide", channelId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);

        var cts = new CancellationTokenSource();
        var session = new HlsSession(dir, cts);

        _logger.LogInformation("HLS session created for channel {ChannelId} in {Dir}", channelId, dir);

        session.Task = Task.Run(() => ProduceLoop(channelId, session, cts.Token));
        return session;
    }

    private async Task ProduceLoop(string channelId, HlsSession session, CancellationToken ct)
    {
        try
        {
            var genres = _channelManager.GetGenres();
            var genre = ChannelManager.ChannelIdToGenre(channelId, genres);
            var items = _channelManager.GetItemsForGenre(genre);

            if (items.Count == 0)
            {
                _logger.LogWarning("HLS producer for {ChannelId}: no items found", channelId);
                return;
            }

            var slots = _scheduleGenerator.GetSlotsFromNow(channelId, items, DateTime.UtcNow, 20);
            if (slots.Count == 0)
            {
                return;
            }

            var seekSeconds = (DateTime.UtcNow - slots[0].StartUtc).TotalSeconds;

            _logger.LogInformation(
                "HLS producing {Count} slots for {ChannelId}, first: {Name} at {Seek:F0}s",
                slots.Count, channelId, slots[0].Item.Name, seekSeconds);

            await RunPlaylistFFmpeg(channelId, slots, seekSeconds, session, ct).ConfigureAwait(false);

            _logger.LogInformation("HLS producer for {ChannelId}: finished", channelId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("HLS producer for {ChannelId}: cancelled", channelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HLS producer for {ChannelId}: unexpected error", channelId);
        }
    }

    private async Task RunPlaylistFFmpeg(
        string channelId,
        IReadOnlyList<ScheduleSlot> slots,
        double seekSeconds,
        HlsSession session,
        CancellationToken ct)
    {
        var ffmpegPath = _mediaEncoder.EncoderPath;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var args = process.StartInfo.ArgumentList;
        args.Add("-nostdin");
        args.Add("-err_detect");
        args.Add("ignore_err");
        args.Add("-fflags");
        args.Add("+discardcorrupt+genpts");
        for (int i = 0; i < slots.Count; i++)
        {
            // Pace each input in wall-clock time so the live playlist advances
            // with the current schedule rather than racing through it.
            args.Add("-re");
            if (i == 0 && seekSeconds > 1.0)
            {
                args.Add("-ss");
                args.Add(seekSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }

            args.Add("-i");
            args.Add(slots[i].Item.Path);
        }

        args.Add("-filter_complex");
        args.Add(BuildConcatFilterGraph(slots.Count));
        args.Add("-map");
        args.Add("[vout]");
        args.Add("-map");
        args.Add("[aout]");
        // libx264 ultrafast: handles all pixel formats, resolutions, and codec
        // transitions without crashes. We normalize every scheduled input in
        // the filter graph first, then concat decoded A/V instead of relying
        // on the ffconcat demuxer, which is brittle with mixed codecs,
        // resolutions, and audio layouts.
        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("ultrafast");
        args.Add("-tune");
        args.Add("zerolatency");
        // Force regular keyframes so the HLS muxer can actually honor the
        // target segment length. Without this, x264's default GOP produces
        // ~10s segments and Jellyfin times out waiting for startup segments.
        args.Add("-force_key_frames");
        args.Add("expr:gte(t,n_forced*3)");
        args.Add("-b:v");
        args.Add("4M");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-c:a");
        args.Add("aac");
        // Keep the audio stream shape explicit in each HLS segment. Without
        // these parameters, Jellyfin's downstream FFmpeg remux can probe the
        // source as AAC with sample_rate=0/channels=0 and fail to start.
        args.Add("-profile:a");
        args.Add("aac_low");
        args.Add("-ar");
        args.Add("48000");
        args.Add("-ac");
        args.Add("2");
        args.Add("-b:a");
        args.Add("384k");
        args.Add("-sn");
        args.Add("-f");
        args.Add("hls");
        args.Add("-hls_time");
        args.Add("3");
        args.Add("-hls_list_size");
        args.Add("10");
        args.Add("-hls_flags");
        args.Add("temp_file+independent_segments");
        // Use the published server URL for segment URLs. Clients fetch
        // segments directly from this URL.
        args.Add("-hls_base_url");
        args.Add($"{_appHost.GetSmartApiUrl(string.Empty)}/api/tvguide/hls/{channelId}/");
        args.Add("-hls_segment_filename");
        args.Add(Path.Combine(session.Dir, "seg%d.ts"));
        args.Add(Path.Combine(session.Dir, "stream.m3u8"));

        process.Start();
        session.CurrentProcess = process;

        string stderr = null;
        var stderrTask = Task.Run(() => stderr = process.StandardError.ReadToEnd(), CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled — kill the process below
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            session.CurrentProcess = null;

            var exitCode = process.ExitCode;
            if (exitCode != 0 && exitCode != 137)
            {
                var tail = stderr?.Length > 2000 ? stderr.Substring(stderr.Length - 2000) : stderr;
                _logger.LogError("HLS FFmpeg exited {ExitCode}. stderr: {Stderr}", exitCode, tail);
            }
            else
            {
                _logger.LogDebug("HLS FFmpeg exited {ExitCode}", exitCode);
            }
        }
    }

    private static string BuildConcatFilterGraph(int inputCount)
    {
        var graph = new StringBuilder();

        for (int i = 0; i < inputCount; i++)
        {
            graph.Append('[')
                .Append(i)
                .Append(":v]scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,")
                .Append("pad=1920:1080:(ow-iw)/2:(oh-ih)/2,")
                .Append("setsar=1,")
                .Append("fps=30000/1001,")
                .Append("format=yuv420p,")
                .Append("settb=AVTB,")
                .Append("setpts=PTS-STARTPTS")
                .Append("[v")
                .Append(i)
                .Append("];");

            graph.Append('[')
                .Append(i)
                .Append(":a]aformat=sample_rates=48000:channel_layouts=stereo,")
                .Append("aresample=48000:async=1:first_pts=0,")
                .Append("asetpts=PTS-STARTPTS")
                .Append("[a")
                .Append(i)
                .Append("];");
        }

        for (int i = 0; i < inputCount; i++)
        {
            graph.Append("[v").Append(i).Append("][a").Append(i).Append(']');
        }

        graph.Append("concat=n=")
            .Append(inputCount)
            .Append(":v=1:a=1[vout][aout]");

        return graph.ToString();
    }

    private void StopSession(string channelId)
    {
        if (_sessions.TryRemove(channelId, out var session))
        {
            _logger.LogInformation("HLS session stopped for channel {ChannelId}", channelId);
            session.Cts.Cancel();

            if (session.CurrentProcess is { HasExited: false } p)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }

            try
            {
                if (Directory.Exists(session.Dir))
                {
                    Directory.Delete(session.Dir, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up HLS dir {Dir}", session.Dir);
            }

            session.Cts.Dispose();
        }
    }

    private void Cleanup(object state)
    {
        foreach (var (channelId, session) in _sessions)
        {
            if (DateTime.UtcNow - session.LastAccess > TimeSpan.FromMinutes(5))
            {
                _logger.LogInformation("HLS session idle timeout for channel {ChannelId}", channelId);
                StopSession(channelId);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var channelId in _sessions.Keys.ToList())
        {
            StopSession(channelId);
        }
    }

    private sealed class HlsSession
    {
        public HlsSession(string dir, CancellationTokenSource cts)
        {
            Dir = dir;
            Cts = cts;
        }

        public string Dir { get; }
        public CancellationTokenSource Cts { get; }
        public Task Task { get; set; }
        public Process CurrentProcess { get; set; }
        public DateTime LastAccess { get; set; } = DateTime.UtcNow;

    }
}
