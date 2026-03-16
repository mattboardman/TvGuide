using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
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

    public static void AddNormalizedOutputEncoding(ICollection<string> args, int inputCount)
    {
        args.Add("-filter_complex");
        args.Add(BuildConcatFilterGraph(inputCount));
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
        args.Add("-force_key_frames");
        args.Add("expr:gte(t,n_forced*3)");
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
}
