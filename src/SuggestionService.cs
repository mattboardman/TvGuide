using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace Jellyfin.Plugin.TvGuide;

public class ChannelSuggestion
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("itemName")]
    public string ItemName { get; set; } = string.Empty;

    [JsonPropertyName("itemType")]
    public string ItemType { get; set; } = string.Empty;

    [JsonPropertyName("channelName")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public class SuggestionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SuggestionService> _logger;

    private const string SystemPrompt = """
        You are a TV channel programming assistant. You will be given a list of
        custom TV channels and media items not assigned to any custom channel.
        Suggest which items would be a good fit for which channels.

        IMPORTANT:
        - Only suggest an item if it is genuinely a good thematic fit.
        - NOT every item needs a channel. If nothing fits, leave it out.
        - Each item can be suggested for at most one channel.
        - The channelName in your response MUST exactly match one of the custom
          channel names listed. Do NOT invent new channel names or use genre names.
        - The itemId in your response MUST be the exact ID string shown in brackets
          (e.g. "534204dadc2f15cc7f1740911f0b1b96"), NOT the item name.
        """;

    private static readonly string JsonSchema = """
        {
            "type": "object",
            "properties": {
                "suggestions": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "itemId": { "type": "string" },
                            "channelName": { "type": "string" },
                            "reason": { "type": "string" }
                        },
                        "required": ["itemId", "channelName", "reason"],
                        "additionalProperties": false
                    }
                }
            },
            "required": ["suggestions"],
            "additionalProperties": false
        }
        """;

    private volatile SuggestionResult _lastResult;

    public SuggestionService(ILibraryManager libraryManager, ILogger<SuggestionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public void StartAsync()
    {
        var result = new SuggestionResult();
        _lastResult = result;

        _ = Task.Run(async () =>
        {
            try
            {
                result.Suggestions = await GetSuggestionsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background suggestion job failed");
                result.Error = ex.Message;
            }
            finally
            {
                result.IsCompleted = true;
            }
        });
    }

    public SuggestionResult GetResult() => _lastResult;

    public sealed class SuggestionResult
    {
        public volatile bool IsCompleted;
        public List<ChannelSuggestion> Suggestions;
        public string Error;
    }

    public async Task<List<ChannelSuggestion>> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.GetCurrentConfiguration();

        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            return new List<ChannelSuggestion>();
        }

        var enabledCustomChannels = config.CustomChannels
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Name))
            .ToList();

        if (enabledCustomChannels.Count == 0)
        {
            return new List<ChannelSuggestion>();
        }

        // Build set of all item IDs and names already in a custom channel.
        // Normalize IDs to "N" format (no hyphens) since config stores hyphenated GUIDs.
        // Also track names because seasons have different IDs than the parent series.
        var assignedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignedItemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in enabledCustomChannels)
        {
            foreach (var id in channel.ItemIds)
            {
                if (Guid.TryParse(id, out var guid))
                {
                    assignedItemIds.Add(guid.ToString("N"));
                    var item = _libraryManager.GetItemById(guid);
                    if (item is not null)
                    {
                        assignedItemNames.Add(item.Name);
                    }
                }
            }
        }

        // Build set of enabled genre channels and their excluded item IDs
        var enabledGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genreExcludedIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var allGenres = _libraryManager.GetGenres(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            Recursive = true,
        }).Items.Select(g => g.Item.Name).Where(g => !string.IsNullOrWhiteSpace(g));

        foreach (var genre in allGenres)
        {
            var genreOverride = config.GenreOverrides.FirstOrDefault(o => o.Genre == genre);
            if (genreOverride is not null && !genreOverride.Enabled)
            {
                continue;
            }

            enabledGenres.Add(genre);
            if (genreOverride?.ExcludedItemIds is { Count: > 0 })
            {
                var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in genreOverride.ExcludedItemIds)
                {
                    normalized.Add(Guid.TryParse(id, out var g) ? g.ToString("N") : id);
                }

                genreExcludedIds[genre] = normalized;
            }
        }

        // Get all Movies and Series from the library
        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
        });

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unassigned = allItems
            .Where(item =>
            {
                var idStr = item.Id.ToString("N");

                // Already in a custom channel (by ID or name, since seasons
                // have different IDs than the parent series)
                if (assignedItemIds.Contains(idStr) || assignedItemNames.Contains(item.Name))
                {
                    return false;
                }

                // In an enabled genre channel (has a matching genre and isn't excluded)
                foreach (var genre in item.Genres)
                {
                    if (enabledGenres.Contains(genre)
                        && (!genreExcludedIds.TryGetValue(genre, out var excluded) || !excluded.Contains(idStr)))
                    {
                        return false;
                    }
                }

                // Deduplicate by name (e.g. multiple seasons of the same series)
                if (!seenNames.Add(item.Name))
                {
                    return false;
                }

                return true;
            })
            .ToList();

        if (unassigned.Count == 0)
        {
            return new List<ChannelSuggestion>();
        }

        var userMessage = BuildUserMessage(enabledCustomChannels, unassigned);

        _logger.LogInformation(
            "Requesting OpenAI suggestions for {UnassignedCount} unassigned items across {ChannelCount} custom channels",
            unassigned.Count,
            enabledCustomChannels.Count);

        _logger.LogDebug("OpenAI user prompt: {Prompt}", userMessage);

        var model = string.IsNullOrWhiteSpace(config.OpenAiModel) ? "gpt-5-mini" : config.OpenAiModel;
        var client = new ChatClient(model, config.OpenAiApiKey);

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "channel_suggestions",
                jsonSchema: BinaryData.FromString(JsonSchema),
                jsonSchemaIsStrict: true),
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(userMessage),
        };

        // Use a standalone timeout — Jellyfin's request CancellationToken
        // fires too early for a large OpenAI call.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var completion = await client.CompleteChatAsync(messages, options, cts.Token)
            .ConfigureAwait(false);

        var responseText = completion.Value.Content[0].Text;

        _logger.LogDebug("OpenAI raw response: {Response}", responseText);

        var parsed = JsonSerializer.Deserialize<SuggestionsResponse>(responseText);
        if (parsed?.Suggestions is null)
        {
            return new List<ChannelSuggestion>();
        }

        // Validate channel names and enrich with item metadata
        var validChannelNames = new HashSet<string>(
            enabledCustomChannels.Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        var itemLookup = unassigned.ToDictionary(
            i => i.Id.ToString("N"),
            i => i,
            StringComparer.OrdinalIgnoreCase);

        var results = new List<ChannelSuggestion>();
        foreach (var s in parsed.Suggestions)
        {
            if (!validChannelNames.Contains(s.ChannelName))
            {
                continue;
            }

            if (!itemLookup.TryGetValue(s.ItemId, out var item))
            {
                continue;
            }

            results.Add(new ChannelSuggestion
            {
                ItemId = s.ItemId,
                ItemName = item.Name,
                ItemType = item is Movie ? "Movie" : "Series",
                ChannelName = s.ChannelName,
                Reason = s.Reason,
            });
        }

        _logger.LogInformation("OpenAI returned {Count} valid suggestions", results.Count);

        return results;
    }

    private string BuildUserMessage(
        List<CustomChannelDefinition> channels,
        List<BaseItem> unassigned)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Custom Channels");

        foreach (var channel in channels)
        {
            sb.AppendLine($"- \"{channel.Name}\"");
        }

        sb.AppendLine();
        sb.AppendLine("## Unassigned Items");

        foreach (var item in unassigned)
        {
            var type = item is Movie ? "Movie" : "Series";
            var genres = item.Genres.Length > 0
                ? string.Join(", ", item.Genres)
                : "None";
            sb.AppendLine($"- [ID:{item.Id:N}] \"{item.Name}\" ({type}) - Genres: {genres}");
        }

        return sb.ToString();
    }

    private sealed class SuggestionsResponse
    {
        [JsonPropertyName("suggestions")]
        public List<SuggestionItem>? Suggestions { get; set; }
    }

    private sealed class SuggestionItem
    {
        [JsonPropertyName("itemId")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("channelName")]
        public string ChannelName { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }
}
