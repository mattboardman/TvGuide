using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.TvGuide;

public record ScheduleSlot(BaseItem Item, DateTime StartUtc, DateTime EndUtc);

public class ScheduleGenerator
{
    private static readonly TimeSpan FallbackDuration = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, List<ScheduleSlot>> _cache = new();

    public void ClearCache() => _cache.Clear();

    public List<ScheduleSlot> GenerateWeekSchedule(string channelId, List<BaseItem> items, DateTime weekStart)
    {
        if (items.Count == 0)
        {
            return new List<ScheduleSlot>();
        }

        var epoch = Plugin.GetCurrentConfiguration().ScheduleEpoch;
        var cacheKey = $"{epoch}:{weekStart:yyyy-MM-dd}:{channelId}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        int year = weekStart.Year;
        int week = ISOWeek.GetWeekOfYear(weekStart);
        long seed = ComputeSeed(year, week, channelId, epoch);

        var shuffled = DeterministicShuffle(items, seed);
        var schedule = new List<ScheduleSlot>();
        var cursor = weekStart;
        var weekEnd = weekStart.AddDays(7);
        int idx = 0;

        while (cursor < weekEnd)
        {
            var item = shuffled[idx % shuffled.Count];
            var duration = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
                ? TimeSpan.FromTicks(item.RunTimeTicks.Value)
                : FallbackDuration;

            var endTime = cursor + duration;
            if (endTime > weekEnd)
            {
                endTime = weekEnd;
            }

            schedule.Add(new ScheduleSlot(item, cursor, endTime));
            cursor = endTime;
            idx++;

            if (idx > 0 && idx % shuffled.Count == 0)
            {
                seed++;
                shuffled = DeterministicShuffle(items, seed);
            }
        }

        _cache.TryAdd(cacheKey, schedule);
        return schedule;
    }

    public (ScheduleSlot Slot, long SeekPositionTicks) GetCurrentSlot(
        string channelId, List<BaseItem> items, DateTime utcNow)
    {
        var weekStart = GetWeekStart(utcNow);
        var schedule = GenerateWeekSchedule(channelId, items, weekStart);

        foreach (var slot in schedule)
        {
            if (utcNow >= slot.StartUtc && utcNow < slot.EndUtc)
            {
                var seekTicks = (utcNow - slot.StartUtc).Ticks;
                return (slot, seekTicks);
            }
        }

        // Fallback to first slot if somehow nothing matches
        return (schedule[0], 0);
    }

    public List<ScheduleSlot> GetSlotsFromNow(
        string channelId, List<BaseItem> items, DateTime utcNow, int count)
    {
        var result = new List<ScheduleSlot>();
        if (items.Count == 0 || count <= 0)
        {
            return result;
        }

        var weekStart = GetWeekStart(utcNow);
        var cursor = utcNow;

        while (result.Count < count)
        {
            var schedule = GenerateWeekSchedule(channelId, items, weekStart);
            bool found = false;

            foreach (var slot in schedule)
            {
                if (!found && cursor >= slot.StartUtc && cursor < slot.EndUtc)
                {
                    found = true;
                }

                if (!found)
                {
                    continue;
                }

                result.Add(slot);
                if (result.Count >= count)
                {
                    break;
                }
            }

            if (result.Count >= count)
            {
                break;
            }

            weekStart = weekStart.AddDays(7);
            cursor = weekStart;
        }

        return result;
    }

    public static DateTime GetWeekStart(DateTime utc)
    {
        // Monday 00:00:00 UTC of the current ISO week
        int diff = ((int)utc.DayOfWeek - 1 + 7) % 7;
        return utc.Date.AddDays(-diff);
    }

    private static long ComputeSeed(int year, int week, string channelId, int epoch = 0)
    {
        var input = $"{year}:{week}:{channelId}:{epoch}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return BitConverter.ToInt64(hash, 0);
    }

    private static List<T> DeterministicShuffle<T>(List<T> source, long seed)
    {
        var rng = new Random((int)(seed & 0x7FFFFFFF));
        var list = new List<T>(source);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
