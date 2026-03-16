#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvGuide;

public sealed class TvGuideLiveStream : ILiveStream, IDirectStreamProvider
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OutputPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly string _channelId;
    private readonly IReadOnlyList<ScheduleSlot> _slots;
    private readonly double _seekSeconds;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<TvGuideLiveStream> _logger;
    private readonly CancellationTokenSource _liveStreamCancellationTokenSource = new();
    private readonly string _tempFilePath;
    private Process? _process;
    private Task<string>? _stderrTask;
    private Task? _processMonitorTask;
    private DateTime _dateOpened;

    public TvGuideLiveStream(
        string channelId,
        IReadOnlyList<ScheduleSlot> slots,
        double seekSeconds,
        MediaSourceInfo mediaSource,
        IMediaEncoder mediaEncoder,
        IServerApplicationHost appHost,
        IConfigurationManager configurationManager,
        ILogger<TvGuideLiveStream> logger)
    {
        _channelId = channelId;
        _slots = slots;
        _seekSeconds = seekSeconds;
        _mediaEncoder = mediaEncoder;
        _appHost = appHost;
        _logger = logger;
        MediaSource = mediaSource;
        UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        _tempFilePath = Path.Combine(configurationManager.GetTranscodePath(), UniqueId + ".ts");

        ConsumerCount = 1;
        EnableStreamSharing = true;
        TunerHostId = string.Empty;
        OriginalStreamId = string.Empty;
    }

    public int ConsumerCount { get; set; }

    public string OriginalStreamId { get; set; }

    public string TunerHostId { get; }

    public bool EnableStreamSharing { get; private set; }

    public MediaSourceInfo MediaSource { get; set; }

    public string UniqueId { get; }

    public async Task Open(CancellationToken openCancellationToken)
    {
        _liveStreamCancellationTokenSource.Token.ThrowIfCancellationRequested();
        openCancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(_tempFilePath) ?? throw new InvalidOperationException("Path can't be a root directory."));

        var process = CreateProcess();
        _process = process;

        _logger.LogInformation(
            "Opening TvGuide live stream for {ChannelId} with {SlotCount} scheduled items into {FilePath}",
            _channelId,
            _slots.Count,
            _tempFilePath);

        process.Start();
        _stderrTask = process.StandardError.ReadToEndAsync();
        _processMonitorTask = MonitorProcessAsync(process);

        try
        {
            await WaitForInitialOutputAsync(process, openCancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await Close().ConfigureAwait(false);
            throw;
        }

        _dateOpened = DateTime.UtcNow;
        MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
        MediaSource.Protocol = MediaProtocol.Http;
    }

    public async Task Close()
    {
        EnableStreamSharing = false;

        _logger.LogInformation("Closing TvGuide live stream {UniqueId}", UniqueId);

        await _liveStreamCancellationTokenSource.CancelAsync().ConfigureAwait(false);

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to kill ffmpeg for TvGuide live stream {UniqueId}", UniqueId);
            }
        }

        if (_processMonitorTask is not null)
        {
            try
            {
                await _processMonitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DeleteTempFileAsync().ConfigureAwait(false);
    }

    public Stream GetStream()
    {
        var stream = new FileStream(
            _tempFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            81920,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        if ((DateTime.UtcNow - _dateOpened).TotalSeconds > 10)
        {
            TrySeek(stream, -20000);
        }

        return stream;
    }

    public void Dispose()
    {
        _process?.Dispose();
        _liveStreamCancellationTokenSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private Process CreateProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        var args = process.StartInfo.ArgumentList;
        args.Add("-nostdin");
        args.Add("-err_detect");
        args.Add("ignore_err");
        args.Add("-fflags");
        args.Add("+discardcorrupt+genpts");
        TvGuideFfmpeg.AddNormalizedConcatInputs(args, _slots, _seekSeconds, _mediaEncoder);
        TvGuideFfmpeg.AddNormalizedOutputEncoding(args, _slots.Count);
        args.Add("-muxdelay");
        args.Add("0");
        args.Add("-muxpreload");
        args.Add("0");
        args.Add("-f");
        args.Add("mpegts");
        args.Add(_tempFilePath);

        return process;
    }

    private async Task WaitForInitialOutputAsync(Process process, CancellationToken cancellationToken)
    {
        var openedAt = DateTime.UtcNow;

        while (DateTime.UtcNow - openedAt < OpenTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _liveStreamCancellationTokenSource.Token.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"TvGuide ffmpeg exited before producing output for {_channelId}: {await GetStderrTailAsync().ConfigureAwait(false)}");
            }

            if (File.Exists(_tempFilePath))
            {
                var fileInfo = new FileInfo(_tempFilePath);
                if (fileInfo.Length > 0)
                {
                    return;
                }
            }

            await Task.Delay(OutputPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for TvGuide stream output for {_channelId}.");
    }

    private async Task MonitorProcessAsync(Process process)
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        var stderrTail = await GetStderrTailAsync().ConfigureAwait(false);

        EnableStreamSharing = false;

        if (_liveStreamCancellationTokenSource.IsCancellationRequested || process.ExitCode == 0 || process.ExitCode == 137)
        {
            _logger.LogDebug(
                "TvGuide ffmpeg exited {ExitCode} for {ChannelId}",
                process.ExitCode,
                _channelId);
            return;
        }

        _logger.LogError(
            "TvGuide ffmpeg exited {ExitCode} for {ChannelId}. stderr: {Stderr}",
            process.ExitCode,
            _channelId,
            stderrTail);
    }

    private async Task<string> GetStderrTailAsync()
    {
        var stderr = _stderrTask is null ? string.Empty : await _stderrTask.ConfigureAwait(false);
        if (stderr.Length <= 2000)
        {
            return stderr;
        }

        return stderr[^2000..];
    }

    private async Task DeleteTempFileAsync(int retryCount = 0)
    {
        if (!File.Exists(_tempFilePath))
        {
            return;
        }

        try
        {
            File.Delete(_tempFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp TvGuide stream file {FilePath}", _tempFilePath);
            if (retryCount >= 40)
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
            await DeleteTempFileAsync(retryCount + 1).ConfigureAwait(false);
        }
    }

    private void TrySeek(FileStream stream, long offset)
    {
        if (!stream.CanSeek)
        {
            return;
        }

        try
        {
            stream.Seek(offset, SeekOrigin.End);
        }
        catch (IOException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to seek TvGuide live stream");
        }
    }
}
