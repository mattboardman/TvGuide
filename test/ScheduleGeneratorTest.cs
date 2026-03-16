using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using NUnit.Framework;

namespace Jellyfin.Plugin.TvGuide.Tests;

[TestFixture]
public class ScheduleGeneratorTest
{
    private static List<BaseItem> CreateTestItems(int count, long runtimeMinutes = 45)
    {
        var items = new List<BaseItem>();
        for (int i = 0; i < count; i++)
        {
            items.Add(new Movie
            {
                Name = $"Movie {i}",
                Id = Guid.NewGuid(),
                RunTimeTicks = TimeSpan.FromMinutes(runtimeMinutes).Ticks,
            });
        }

        return items;
    }

    [Test]
    public void GenerateWeekSchedule_ProducesDeterministicOutput()
    {
        var gen = new ScheduleGenerator();
        var items = CreateTestItems(5);
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc); // Monday

        var schedule1 = gen.GenerateWeekSchedule("test_channel", items, weekStart);
        // Clear cache to force regeneration
        var gen2 = new ScheduleGenerator();
        var schedule2 = gen2.GenerateWeekSchedule("test_channel", items, weekStart);

        Assert.That(schedule1.Count, Is.EqualTo(schedule2.Count));
        for (int i = 0; i < schedule1.Count; i++)
        {
            Assert.That(schedule1[i].Item.Name, Is.EqualTo(schedule2[i].Item.Name));
            Assert.That(schedule1[i].StartUtc, Is.EqualTo(schedule2[i].StartUtc));
            Assert.That(schedule1[i].EndUtc, Is.EqualTo(schedule2[i].EndUtc));
        }
    }

    [Test]
    public void GenerateWeekSchedule_CoversFullWeek()
    {
        var gen = new ScheduleGenerator();
        var items = CreateTestItems(3, 60);
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);

        var schedule = gen.GenerateWeekSchedule("test_channel", items, weekStart);

        Assert.That(schedule[0].StartUtc, Is.EqualTo(weekStart));
        Assert.That(schedule[^1].EndUtc, Is.EqualTo(weekStart.AddDays(7)));
    }

    [Test]
    public void GenerateWeekSchedule_SlotsAreContiguous()
    {
        var gen = new ScheduleGenerator();
        var items = CreateTestItems(4, 30);
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);

        var schedule = gen.GenerateWeekSchedule("test_channel", items, weekStart);

        for (int i = 1; i < schedule.Count; i++)
        {
            Assert.That(schedule[i].StartUtc, Is.EqualTo(schedule[i - 1].EndUtc),
                $"Gap between slot {i - 1} and {i}");
        }
    }

    [Test]
    public void GenerateWeekSchedule_DifferentChannelsProduceDifferentSchedules()
    {
        var gen = new ScheduleGenerator();
        var items = CreateTestItems(10);
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);

        var schedule1 = gen.GenerateWeekSchedule("comedy", items, weekStart);
        var gen2 = new ScheduleGenerator();
        var schedule2 = gen2.GenerateWeekSchedule("drama", items, weekStart);

        // At least one slot should have a different item
        bool anyDifferent = false;
        var minCount = Math.Min(schedule1.Count, schedule2.Count);
        for (int i = 0; i < minCount; i++)
        {
            if (schedule1[i].Item.Name != schedule2[i].Item.Name)
            {
                anyDifferent = true;
                break;
            }
        }

        Assert.That(anyDifferent, Is.True, "Different channels should produce different schedules");
    }

    [Test]
    public void GenerateWeekSchedule_EmptyItems_ReturnsEmpty()
    {
        var gen = new ScheduleGenerator();
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);

        var schedule = gen.GenerateWeekSchedule("test", new List<BaseItem>(), weekStart);

        Assert.That(schedule, Is.Empty);
    }

    [Test]
    public void GenerateWeekSchedule_ItemWithoutRuntime_UsesFallback()
    {
        var gen = new ScheduleGenerator();
        var items = new List<BaseItem>
        {
            new Movie { Name = "No Runtime", Id = Guid.NewGuid(), RunTimeTicks = null },
        };
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);

        var schedule = gen.GenerateWeekSchedule("test", items, weekStart);

        // Fallback is 30 minutes, so each slot should be 30 min
        Assert.That(schedule[0].EndUtc - schedule[0].StartUtc, Is.EqualTo(TimeSpan.FromMinutes(30)));
    }

    [Test]
    public void GetCurrentSlot_ReturnsCorrectSlotAndSeek()
    {
        var gen = new ScheduleGenerator();
        var items = CreateTestItems(3, 60);
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);

        // 90 minutes into the week = halfway through item 2 (index 1)
        var queryTime = weekStart.AddMinutes(90);
        var (slot, seekTicks) = gen.GetCurrentSlot("test", items, queryTime);

        Assert.That(slot.StartUtc, Is.LessThanOrEqualTo(queryTime));
        Assert.That(slot.EndUtc, Is.GreaterThan(queryTime));
        Assert.That(seekTicks, Is.GreaterThan(0));
        Assert.That(TimeSpan.FromTicks(seekTicks).TotalMinutes, Is.EqualTo(30).Within(1));
    }

    [Test]
    public void GetSlotsFromNow_ReturnsRequestedCount()
    {
        var gen = new ScheduleGenerator();
        var items = CreateTestItems(10, 60);
        var weekStart = new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc);
        var queryTime = weekStart.AddHours(2);

        var slots = gen.GetSlotsFromNow("test", items, queryTime, 5);

        Assert.That(slots.Count, Is.EqualTo(5));
        Assert.That(slots[0].StartUtc, Is.LessThanOrEqualTo(queryTime));
        Assert.That(slots[0].EndUtc, Is.GreaterThan(queryTime));
    }

    [Test]
    public void GetSlotsFromNow_CrossesWeekBoundary()
    {
        var gen = new ScheduleGenerator();
        var items = CreateTestItems(2, 60);
        var queryTime = new DateTime(2026, 3, 15, 23, 30, 0, DateTimeKind.Utc);

        var slots = gen.GetSlotsFromNow("test", items, queryTime, 3);

        Assert.That(slots.Count, Is.EqualTo(3));
        Assert.That(slots[0].StartUtc, Is.EqualTo(new DateTime(2026, 3, 15, 23, 0, 0, DateTimeKind.Utc)));
        Assert.That(slots[1].StartUtc, Is.EqualTo(new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(slots[2].StartUtc, Is.EqualTo(new DateTime(2026, 3, 16, 1, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void GetWeekStart_ReturnsMonday()
    {
        // Wednesday March 11 2026
        var wednesday = new DateTime(2026, 3, 11, 14, 30, 0, DateTimeKind.Utc);
        var weekStart = ScheduleGenerator.GetWeekStart(wednesday);

        Assert.That(weekStart, Is.EqualTo(new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(weekStart.DayOfWeek, Is.EqualTo(DayOfWeek.Monday));
    }

    [Test]
    public void GetWeekStart_MondayReturnsItself()
    {
        var monday = new DateTime(2026, 3, 9, 10, 0, 0, DateTimeKind.Utc);
        var weekStart = ScheduleGenerator.GetWeekStart(monday);

        Assert.That(weekStart, Is.EqualTo(new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void GetWeekStart_SundayReturnsPreviousMonday()
    {
        var sunday = new DateTime(2026, 3, 15, 23, 59, 0, DateTimeKind.Utc);
        var weekStart = ScheduleGenerator.GetWeekStart(sunday);

        Assert.That(weekStart, Is.EqualTo(new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(weekStart.DayOfWeek, Is.EqualTo(DayOfWeek.Monday));
    }
}

[TestFixture]
public class ChannelManagerTest
{
    [Test]
    public void GenreToChannelId_Lowercases_And_Replaces_Spaces()
    {
        Assert.That(ChannelManager.GenreToChannelId("Science Fiction"), Is.EqualTo("tvguide_science_fiction"));
        Assert.That(ChannelManager.GenreToChannelId("Comedy"), Is.EqualTo("tvguide_comedy"));
    }

    [Test]
    public void ChannelIdToGenre_RoundTrips()
    {
        var genres = new List<string> { "Action", "Comedy", "Science Fiction" };

        foreach (var genre in genres)
        {
            var channelId = ChannelManager.GenreToChannelId(genre);
            var resolved = ChannelManager.ChannelIdToGenre(channelId, genres);
            Assert.That(resolved, Is.EqualTo(genre));
        }
    }
}
