using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.TvGuide;

internal static class TvGuideFfmpeg
{
    public static void AddNormalizedConcatInputs(
        ICollection<string> args,
        IReadOnlyList<ScheduleSlot> slots,
        double seekSeconds,
        IMediaEncoder mediaEncoder)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            args.Add("-re");
            if (i == 0 && seekSeconds > 1.0)
            {
                args.Add("-ss");
                args.Add(seekSeconds.ToString("F3", CultureInfo.InvariantCulture));
            }

            args.Add("-i");
            args.Add(BuildInputArgument(slots[i].Item, mediaEncoder));
        }
    }

    public static void AddNormalizedOutputEncoding(
        ICollection<string> args,
        IReadOnlyList<ScheduleSlot> slots,
        double keyFrameIntervalSeconds = 3.0)
    {
        var outputFps = DetermineOutputFps(slots);

        args.Add("-filter_complex");
        args.Add(BuildConcatFilterGraph(slots.Count, outputFps.Argument));
        args.Add("-map");
        args.Add("[vout]");
        args.Add("-map");
        args.Add("[aout]");
        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("ultrafast");
        args.Add("-tune");
        args.Add("zerolatency");
        args.Add("-sc_threshold");
        args.Add("0");
        args.Add("-force_key_frames");
        args.Add("expr:gte(t,n_forced*" + keyFrameIntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + ")");
        args.Add("-b:v");
        args.Add("4M");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        args.Add("-c:a");
        args.Add("aac");
        args.Add("-profile:a");
        args.Add("aac_low");
        args.Add("-ar");
        args.Add("48000");
        args.Add("-ac");
        args.Add("2");
        args.Add("-b:a");
        args.Add("384k");
        args.Add("-sn");
    }

    private static string BuildConcatFilterGraph(int inputCount, string outputFps)
    {
        var graph = new StringBuilder();

        for (int i = 0; i < inputCount; i++)
        {
            graph.Append('[')
                .Append(i)
                .Append(":v]scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,")
                .Append("pad=1920:1080:(ow-iw)/2:(oh-ih)/2,")
                .Append("setsar=1,")
                .Append("fps=")
                .Append(outputFps)
                .Append(',')
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

    private static OutputFps DetermineOutputFps(IReadOnlyList<ScheduleSlot> slots)
    {
        if (slots.Count == 0)
        {
            return new OutputFps("30000/1001", 29.97003);
        }

        var videoStream = slots[0].Item
            .GetMediaStreams()
            .FirstOrDefault(stream => stream.Type == MediaStreamType.Video);

        var referenceFps = videoStream?.ReferenceFrameRate;
        if (!referenceFps.HasValue || referenceFps.Value <= 0)
        {
            return new OutputFps("30000/1001", 29.97003);
        }

        var chosen = new OutputFps("30000/1001", 29.97003);
        var candidates = new OutputFps[]
        {
            new("24000/1001", 23.976024),
            new("24", 24.0),
            new("25", 25.0),
            new("30000/1001", 29.97003),
            new("30", 30.0),
            new("50", 50.0),
            new("60000/1001", 59.94006),
            new("60", 60.0),
        };

        var smallestDelta = double.MaxValue;

        foreach (var candidate in candidates)
        {
            var delta = System.Math.Abs(referenceFps.Value - candidate.NumericValue);
            if (delta < smallestDelta)
            {
                smallestDelta = delta;
                chosen = candidate;
            }
        }

        return chosen;
    }

    private static string BuildInputArgument(BaseItem item, IMediaEncoder mediaEncoder)
    {
        var mediaSource = new MediaSourceInfo
        {
            Path = item.Path,
            Protocol = MediaProtocol.File,
        };

        if (item is Video video)
        {
            mediaSource.VideoType = video.VideoType;
            mediaSource.IsoType = video.IsoType;
        }

        return NormalizeArgumentForArgumentList(mediaEncoder.GetInputPathArgument(item.Path, mediaSource));
    }

    private static string NormalizeArgumentForArgumentList(string inputArgument)
    {
        if (string.IsNullOrEmpty(inputArgument))
        {
            return inputArgument;
        }

        if (inputArgument.Length >= 2
            && inputArgument[0] == '"'
            && inputArgument[^1] == '"')
        {
            return inputArgument[1..^1];
        }

        var quotedPathIndex = inputArgument.IndexOf(":\"", System.StringComparison.Ordinal);
        if (quotedPathIndex >= 0 && inputArgument[^1] == '"')
        {
            return inputArgument.Substring(0, quotedPathIndex + 1)
                + inputArgument.Substring(quotedPathIndex + 2, inputArgument.Length - quotedPathIndex - 3);
        }

        return inputArgument;
    }

    private readonly record struct OutputFps(string Argument, double NumericValue);
}
