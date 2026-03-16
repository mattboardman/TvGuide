using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TvGuide;

[ApiController]
[Route("api/tvguide")]
[AllowAnonymous]
public class TvGuideStreamController : ControllerBase
{
    private readonly HlsManager _hlsManager;

    public TvGuideStreamController(HlsManager hlsManager)
    {
        _hlsManager = hlsManager;
    }

    [HttpGet("hls/{channelId}/stream.m3u8")]
    public async Task<IActionResult> GetPlaylist(string channelId)
    {
        var dir = _hlsManager.EnsureSession(channelId);
        var m3u8 = Path.Combine(dir, "stream.m3u8");

        // Wait for FFmpeg to produce the first playlist (up to 30s)
        for (int i = 0; i < 300 && !System.IO.File.Exists(m3u8); i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        if (!System.IO.File.Exists(m3u8))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        _hlsManager.Touch(channelId);

        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        return PhysicalFile(m3u8, "application/vnd.apple.mpegurl");
    }

    [HttpGet("hls/{channelId}/{segment}")]
    public IActionResult GetSegment(string channelId, string segment)
    {
        // Only allow .ts files
        if (!segment.EndsWith(".ts"))
        {
            return NotFound();
        }

        var dir = _hlsManager.EnsureSession(channelId);
        var path = Path.Combine(dir, segment);

        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        _hlsManager.Touch(channelId);

        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        return PhysicalFile(path, "video/mp2t");
    }
}
