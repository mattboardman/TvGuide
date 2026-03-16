using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvGuide;

[ApiController]
[Route("api/tvguide")]
[AllowAnonymous]
public class TvGuideStreamController : ControllerBase
{
    private readonly ChannelManager _channelManager;
    private readonly ScheduleGenerator _scheduleGenerator;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<TvGuideStreamController> _logger;

    public TvGuideStreamController(
        ChannelManager channelManager,
        ScheduleGenerator scheduleGenerator,
        IMediaEncoder mediaEncoder,
        ILogger<TvGuideStreamController> logger)
    {
        _channelManager = channelManager;
        _scheduleGenerator = scheduleGenerator;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    [HttpGet("stream/{channelId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task StreamChannel(string channelId, CancellationToken cancellationToken)
    {
        var genres = _channelManager.GetGenres();
        var genre = ChannelManager.ChannelIdToGenre(channelId, genres);
        var items = _channelManager.GetItemsForGenre(genre);

        if (items.Count == 0)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "video/x-matroska";
        Response.Headers["Cache-Control"] = "no-cache, no-store";
        Response.Headers["Connection"] = "keep-alive";

        await Response.StartAsync(cancellationToken).ConfigureAwait(false);

        // Get current + upcoming slots for the concat playlist
        var slots = _scheduleGenerator.GetSlotsFromNow(channelId, items, DateTime.UtcNow, 10);
        if (slots.Count == 0)
        {
            return;
        }

        var seekTicks = (DateTime.UtcNow - slots[0].StartUtc).Ticks;
        var seekSeconds = TimeSpan.FromTicks(seekTicks).TotalSeconds;

        _logger.LogInformation(
            "Starting TvGuide stream for channel {ChannelId} ({Genre}), {SlotCount} items queued, first: {Name} at {Seek:F0}s",
            channelId, genre, slots.Count, slots[0].Item.Name, seekSeconds);

        // Build concat file for FFmpeg
        var concatFile = Path.GetTempFileName();
        try
        {
            await WriteConcatFile(concatFile, slots, seekSeconds).ConfigureAwait(false);
            await StreamConcat(concatFile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { System.IO.File.Delete(concatFile); } catch { }
        }
    }

    private static async Task WriteConcatFile(string path, List<ScheduleSlot> slots, double seekSeconds)
    {
        using var writer = new StreamWriter(path);
        await writer.WriteLineAsync("ffconcat version 1.0").ConfigureAwait(false);

        for (int i = 0; i < slots.Count; i++)
        {
            var filePath = slots[i].Item.Path.Replace("'", "'\\''");
            await writer.WriteLineAsync($"file '{filePath}'").ConfigureAwait(false);

            if (i == 0 && seekSeconds > 1.0)
            {
                await writer.WriteLineAsync($"inpoint {seekSeconds:F3}").ConfigureAwait(false);
            }
        }
    }

    private async Task StreamConcat(string concatFile, CancellationToken cancellationToken)
    {
        var ffmpegPath = _mediaEncoder.EncoderPath;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var args = process.StartInfo.ArgumentList;
        args.Add("-f");
        args.Add("concat");
        args.Add("-safe");
        args.Add("0");
        args.Add("-i");
        args.Add(concatFile);
        args.Add("-c");
        args.Add("copy");
        args.Add("-f");
        args.Add("matroska");
        args.Add("-fflags");
        args.Add("+genpts");
        args.Add("pipe:1");

        process.Start();

        string stderr = null;
        var stderrTask = Task.Run(() => stderr = process.StandardError.ReadToEnd(), CancellationToken.None);

        try
        {
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;
            int bytesRead;
            long totalBytes = 0;

            while ((bytesRead = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                totalBytes += bytesRead;
            }

            _logger.LogDebug("TvGuide stream completed: {TotalMB:F1} MB written", totalBytes / (1024.0 * 1024.0));

            if (totalBytes == 0)
            {
                await stderrTask.ConfigureAwait(false);
                _logger.LogError("TvGuide FFmpeg produced 0 bytes. Exit code: {Exit}, stderr: {Stderr}",
                    process.HasExited ? process.ExitCode : -1,
                    stderr?.Length > 2000 ? stderr.Substring(stderr.Length - 2000) : stderr);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TvGuide stream cancelled (client disconnected)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TvGuide stream error");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
