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
    private const long TsPacketSize = 188;
    private const int MaxIndexedKeyframes = 512;
    private const long ResumeKeyframeProximityBytes = 96 * 1024;
    private const long MinResumeLiveEdgeBufferBytes = 320 * 1024;

    private readonly string _channelId;
    private readonly IReadOnlyList<ScheduleSlot> _slots;
    private readonly double _seekSeconds;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<TvGuideLiveStream> _logger;
    private readonly CancellationTokenSource _liveStreamCancellationTokenSource = new();
    private readonly object _keyframePositionsLock = new();
    private readonly object _readPositionLock = new();
    private readonly string _tempFilePath;
    private readonly List<long> _keyframePositions = new();
    private Process? _process;
    private Task<string>? _stderrTask;
    private Task? _processMonitorTask;
    private Task? _keyframeIndexerTask;
    private DateTime _dateOpened;
    private int _streamOpenCount;
    private long _resumePosition;

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
        EnableStreamSharing = false;
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
        _keyframeIndexerTask = Task.Run(() => IndexKeyframesAsync(_liveStreamCancellationTokenSource.Token));
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

        if (_keyframeIndexerTask is not null)
        {
            try
            {
                await _keyframeIndexerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DeleteFileAsync(_tempFilePath).ConfigureAwait(false);
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

        var age = DateTime.UtcNow - _dateOpened;
        var fileLength = stream.Length;
        var streamNumber = Interlocked.Increment(ref _streamOpenCount);
        var startPosition = 0L;
        var readMode = "start";

        var (resumePosition, hasIndexedKeyframe) = GetResumePosition(fileLength);
        if (resumePosition > 0 && TrySeek(stream, resumePosition, SeekOrigin.Begin))
        {
            startPosition = stream.Position;
            readMode = hasIndexedKeyframe ? "resume-keyframe" : "resume";
        }
        else if (age.TotalSeconds > 10 && ConsumerCount > 1 && TrySeek(stream, -20000, SeekOrigin.End))
        {
            startPosition = stream.Position;
            readMode = "live-edge";
        }

        _logger.LogInformation(
            "Opening TvGuide read stream {StreamNumber} for {UniqueId} ({ChannelId}); age={AgeSeconds:F3}s fileLength={FileLength} consumerCount={ConsumerCount} readMode={ReadMode} resumePosition={ResumePosition} position={Position}",
            streamNumber,
            UniqueId,
            _channelId,
            age.TotalSeconds,
            fileLength,
            ConsumerCount,
            readMode,
            resumePosition,
            stream.Position);

        return new LoggedStream(
            stream,
            this,
            _logger,
            _channelId,
            UniqueId,
            streamNumber,
            startPosition);
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
        var config = Plugin.GetCurrentConfiguration();
        args.Add("-nostdin");
        args.Add("-err_detect");
        args.Add("ignore_err");
        args.Add("-fflags");
        args.Add("+discardcorrupt+genpts");
        TvGuideFfmpeg.AddNormalizedConcatInputs(args, _slots, _seekSeconds, _mediaEncoder);
        TvGuideFfmpeg.AddNormalizedOutputEncoding(args, _slots, config, 0.25);
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

    private async Task DeleteFileAsync(string path, int retryCount = 0)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp TvGuide file {FilePath}", path);
            if (retryCount >= 40)
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
            await DeleteFileAsync(path, retryCount + 1).ConfigureAwait(false);
        }
    }

    private async Task IndexKeyframesAsync(CancellationToken cancellationToken)
    {
        var packet = new byte[TsPacketSize];
        int? pmtPid = null;
        int? videoPid = null;
        long packetOffset = 0;

        using var stream = new FileStream(
            _tempFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            81920,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await FillPacketAsync(stream, packet, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                await Task.Delay(OutputPollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (bytesRead < TsPacketSize)
            {
                stream.Seek(-bytesRead, SeekOrigin.Current);
                await Task.Delay(OutputPollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            ProcessPacket(packet, packetOffset, ref pmtPid, ref videoPid);
            packetOffset += TsPacketSize;
        }
    }

    private async Task<int> FillPacketAsync(FileStream stream, byte[] packet, CancellationToken cancellationToken)
    {
        int totalRead = 0;

        while (totalRead < packet.Length)
        {
            var bytesRead = await stream.ReadAsync(packet.AsMemory(totalRead, packet.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalRead += bytesRead;
        }

        return totalRead;
    }

    private void ProcessPacket(byte[] packet, long packetOffset, ref int? pmtPid, ref int? videoPid)
    {
        if (packet[0] != 0x47)
        {
            return;
        }

        var payloadUnitStartIndicator = (packet[1] & 0x40) != 0;
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var adaptationFieldControl = (packet[3] >> 4) & 0x03;
        var hasAdaptation = (adaptationFieldControl & 0x02) != 0;
        var hasPayload = (adaptationFieldControl & 0x01) != 0;
        var payloadOffset = 4;
        var randomAccessIndicator = false;

        if (hasAdaptation)
        {
            if (payloadOffset >= packet.Length)
            {
                return;
            }

            var adaptationLength = packet[payloadOffset];
            if (adaptationLength > 0 && payloadOffset + 1 < packet.Length)
            {
                randomAccessIndicator = (packet[payloadOffset + 1] & 0x40) != 0;
            }

            payloadOffset += adaptationLength + 1;
        }

        if (!hasPayload || payloadOffset >= packet.Length)
        {
            return;
        }

        if (pid == 0 && payloadUnitStartIndicator)
        {
            pmtPid = ParsePat(packet, payloadOffset) ?? pmtPid;
            return;
        }

        if (pmtPid.HasValue && pid == pmtPid.Value && payloadUnitStartIndicator)
        {
            videoPid = ParsePmtForVideoPid(packet, payloadOffset) ?? videoPid;
            return;
        }

        if (videoPid.HasValue && pid == videoPid.Value && payloadUnitStartIndicator && randomAccessIndicator)
        {
            RecordKeyframePosition(packetOffset);
        }
    }

    private int? ParsePat(byte[] packet, int payloadOffset)
    {
        var sectionOffset = GetPsiSectionOffset(packet, payloadOffset);
        if (sectionOffset < 0 || sectionOffset + 8 > packet.Length || packet[sectionOffset] != 0x00)
        {
            return null;
        }

        var sectionLength = ((packet[sectionOffset + 1] & 0x0F) << 8) | packet[sectionOffset + 2];
        var sectionEnd = System.Math.Min(packet.Length, sectionOffset + 3 + sectionLength - 4);
        var programOffset = sectionOffset + 8;

        while (programOffset + 4 <= sectionEnd)
        {
            var programNumber = (packet[programOffset] << 8) | packet[programOffset + 1];
            var programPid = ((packet[programOffset + 2] & 0x1F) << 8) | packet[programOffset + 3];
            if (programNumber != 0)
            {
                return programPid;
            }

            programOffset += 4;
        }

        return null;
    }

    private int? ParsePmtForVideoPid(byte[] packet, int payloadOffset)
    {
        var sectionOffset = GetPsiSectionOffset(packet, payloadOffset);
        if (sectionOffset < 0 || sectionOffset + 12 > packet.Length || packet[sectionOffset] != 0x02)
        {
            return null;
        }

        var sectionLength = ((packet[sectionOffset + 1] & 0x0F) << 8) | packet[sectionOffset + 2];
        var sectionEnd = System.Math.Min(packet.Length, sectionOffset + 3 + sectionLength - 4);
        var programInfoLength = ((packet[sectionOffset + 10] & 0x0F) << 8) | packet[sectionOffset + 11];
        var streamOffset = sectionOffset + 12 + programInfoLength;

        while (streamOffset + 5 <= sectionEnd)
        {
            var streamType = packet[streamOffset];
            var elementaryPid = ((packet[streamOffset + 1] & 0x1F) << 8) | packet[streamOffset + 2];
            var esInfoLength = ((packet[streamOffset + 3] & 0x0F) << 8) | packet[streamOffset + 4];
            if (streamType == 0x1B)
            {
                return elementaryPid;
            }

            streamOffset += 5 + esInfoLength;
        }

        return null;
    }

    private int GetPsiSectionOffset(byte[] packet, int payloadOffset)
    {
        if (payloadOffset >= packet.Length)
        {
            return -1;
        }

        var pointerField = packet[payloadOffset];
        var sectionOffset = payloadOffset + 1 + pointerField;
        return sectionOffset < packet.Length ? sectionOffset : -1;
    }

    private void RecordKeyframePosition(long position)
    {
        lock (_keyframePositionsLock)
        {
            if (_keyframePositions.Count > 0 && _keyframePositions[^1] == position)
            {
                return;
            }

            _keyframePositions.Add(position);
            if (_keyframePositions.Count > MaxIndexedKeyframes)
            {
                _keyframePositions.RemoveRange(0, _keyframePositions.Count - MaxIndexedKeyframes);
            }
        }
    }

    private (long Position, bool IsIndexedKeyframe) GetResumePosition(long fileLength)
    {
        var requestedPosition = 0L;
        lock (_readPositionLock)
        {
            requestedPosition = AlignToTsPacketBoundary(System.Math.Clamp(_resumePosition, 0, fileLength));
        }

        if (requestedPosition <= 0)
        {
            return (0, false);
        }

        var liveEdgeDistance = fileLength - requestedPosition;

        lock (_keyframePositionsLock)
        {
            for (int i = _keyframePositions.Count - 1; i >= 0; i--)
            {
                var indexedPosition = _keyframePositions[i];
                if (indexedPosition <= requestedPosition)
                {
                    if (requestedPosition - indexedPosition < ResumeKeyframeProximityBytes
                        && liveEdgeDistance < MinResumeLiveEdgeBufferBytes
                        && i > 0)
                    {
                        indexedPosition = _keyframePositions[i - 1];
                    }

                    return (indexedPosition, true);
                }
            }
        }

        return (requestedPosition, false);
    }

    private long AdvanceResumePosition(long position)
    {
        lock (_readPositionLock)
        {
            var alignedPosition = AlignToTsPacketBoundary(position);
            if (alignedPosition > _resumePosition)
            {
                _resumePosition = alignedPosition;
            }

            return _resumePosition;
        }
    }

    private static long AlignToTsPacketBoundary(long position)
        => position <= 0 ? 0 : position - (position % TsPacketSize);

    private bool TrySeek(FileStream stream, long offset, SeekOrigin origin)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        try
        {
            stream.Seek(offset, origin);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to seek TvGuide live stream");
            return false;
        }
    }

    private sealed class LoggedStream : Stream
    {
        private readonly Stream _inner;
        private readonly TvGuideLiveStream _owner;
        private readonly ILogger<TvGuideLiveStream> _logger;
        private readonly string _channelId;
        private readonly string _uniqueId;
        private readonly int _streamNumber;
        private readonly long _startPosition;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _bytesRead;
        private bool _disposed;

        public LoggedStream(
            Stream inner,
            TvGuideLiveStream owner,
            ILogger<TvGuideLiveStream> logger,
            string channelId,
            string uniqueId,
            int streamNumber,
            long startPosition)
        {
            _inner = inner;
            _owner = owner;
            _logger = logger;
            _channelId = channelId;
            _uniqueId = uniqueId;
            _streamNumber = streamNumber;
            _startPosition = startPosition;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
            => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            _bytesRead += bytesRead;
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = _inner.Read(buffer);
            _bytesRead += bytesRead;
            return bytesRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _bytesRead += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => _inner.Seek(offset, origin);

        public override void SetLength(long value)
            => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
            => _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer)
            => _inner.Write(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                base.Dispose(disposing);
                return;
            }

            if (disposing)
            {
                var finalPosition = GetFinalPosition();
                var resumePosition = _owner.AdvanceResumePosition(finalPosition);
                _logger.LogInformation(
                    "Disposed TvGuide read stream {StreamNumber} for {UniqueId} ({ChannelId}); elapsed={ElapsedSeconds:F3}s bytesRead={BytesRead} startPosition={StartPosition} finalPosition={FinalPosition} resumePosition={ResumePosition}",
                    _streamNumber,
                    _uniqueId,
                    _channelId,
                    _stopwatch.Elapsed.TotalSeconds,
                    _bytesRead,
                    _startPosition,
                    finalPosition,
                    resumePosition);
                _inner.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        private long GetFinalPosition()
        {
            if (!_inner.CanSeek)
            {
                return _startPosition + _bytesRead;
            }

            try
            {
                return _inner.Position;
            }
            catch (Exception)
            {
                return _startPosition + _bytesRead;
            }
        }
    }
}
