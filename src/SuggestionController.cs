using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvGuide;

[ApiController]
[Route("api/tvguide")]
[Authorize(Policy = "RequiresElevation")]
public class SuggestionController : ControllerBase
{
    private readonly SuggestionService _suggestionService;
    private readonly ScheduleGenerator _scheduleGenerator;
    private readonly HlsManager _hlsManager;
    private readonly ILogger<SuggestionController> _logger;

    public SuggestionController(
        SuggestionService suggestionService,
        ScheduleGenerator scheduleGenerator,
        HlsManager hlsManager,
        ILogger<SuggestionController> logger)
    {
        _suggestionService = suggestionService;
        _scheduleGenerator = scheduleGenerator;
        _hlsManager = hlsManager;
        _logger = logger;
    }

    /// <summary>
    /// Starts an async suggestion job. Returns immediately with status "running".
    /// Client should poll GET /api/tvguide/suggestions for results.
    /// </summary>
    [HttpPost("suggestions")]
    public IActionResult StartSuggestions()
    {
        var config = Plugin.GetCurrentConfiguration();
        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            return BadRequest(new { error = "OpenAI API key is not configured." });
        }

        _suggestionService.StartAsync();
        return Ok(new { status = "running" });
    }

    /// <summary>
    /// Polls for suggestion results. Returns status + results when done.
    /// </summary>
    [HttpGet("suggestions")]
    public IActionResult GetSuggestions()
    {
        var result = _suggestionService.GetResult();

        if (result is null)
        {
            return Ok(new { status = "idle", suggestions = Array.Empty<ChannelSuggestion>() });
        }

        if (!result.IsCompleted)
        {
            return Ok(new { status = "running", suggestions = Array.Empty<ChannelSuggestion>() });
        }

        if (result.Error is not null)
        {
            return Ok(new { status = "error", error = result.Error, suggestions = Array.Empty<ChannelSuggestion>() });
        }

        return Ok(new { status = "done", suggestions = result.Suggestions });
    }

    [HttpPost("reshuffle")]
    public IActionResult Reshuffle()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, new { error = "Plugin not initialized." });
        }

        plugin.Configuration.ScheduleEpoch++;
        plugin.SaveConfiguration();

        _scheduleGenerator.ClearCache();
        _hlsManager.StopAllSessions();

        _logger.LogInformation("Schedule reshuffled, epoch is now {Epoch}", plugin.Configuration.ScheduleEpoch);

        return Ok(new { epoch = plugin.Configuration.ScheduleEpoch });
    }
}
