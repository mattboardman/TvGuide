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

        // Stream just the current item. When it ends, Jellyfin will call
        // GetChannelStream again for the next scheduled program.
        var (slot, _) = _scheduleGenerator.GetCurrentSlot(channelId, items, DateTime.UtcNow);
        var seekSeconds = (DateTime.UtcNow - slot.StartUtc).TotalSeconds;

        _logger.LogInformation(
            "Starting TvGuide stream for channel {ChannelId} ({Genre}): {Name} at {Seek:F0}s",
            channelId, genre, slot.Item.Name, seekSeconds);

        await StreamFile(slot.Item.Path, seekSeconds, cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamFile(string filePath, double seekSeconds, CancellationToken cancellationToken)
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
        args.Add("-nostdin");

        if (seekSeconds > 1.0)
        {
            args.Add("-ss");
            args.Add(seekSeconds.ToString("F3"));
        }

        args.Add("-i");
        args.Add(filePath);
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-map");
        args.Add("0:a:0");
        args.Add("-c");
        args.Add("copy");
        args.Add("-sn");
        args.Add("-f");
        args.Add("matroska");
        args.Add("-fflags");
        args.Add("+genpts");
        args.Add("pipe:1");

        process.Start();

        string stderr = null;
        var stderrTask = Task.Run(() => stderr = process.StandardError.ReadToEnd(), CancellationToken.None);

        long totalBytes = 0;

        try
        {
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;
            int bytesRead;

            while ((bytesRead = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                totalBytes += bytesRead;
            }

            _logger.LogInformation("TvGuide stream completed: {TotalMB:F1} MB written", totalBytes / (1024.0 * 1024.0));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TvGuide stream cancelled (client disconnected), {TotalMB:F1} MB written",
                totalBytes / (1024.0 * 1024.0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TvGuide stream error after {TotalMB:F1} MB", totalBytes / (1024.0 * 1024.0));
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
            await stderrTask.ConfigureAwait(false);

            var exitCode = process.ExitCode;
            var stderrTail = stderr?.Length > 2000 ? stderr.Substring(stderr.Length - 2000) : stderr;

            if (exitCode != 0 && exitCode != 137)
            {
                _logger.LogError("TvGuide FFmpeg exited {ExitCode}, {TotalMB:F1} MB written. stderr: {Stderr}",
                    exitCode, totalBytes / (1024.0 * 1024.0), stderrTail);
            }
            else
            {
                _logger.LogDebug("TvGuide FFmpeg exited {ExitCode}, {TotalMB:F1} MB. stderr: {Stderr}",
                    exitCode, totalBytes / (1024.0 * 1024.0), stderrTail);
            }
        }
    }
}
