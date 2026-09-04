using Obsync.Shared;
using Obsync.Shared.Models;
using Xunit;

namespace Obsync.Shared.Tests;

/// <summary>
/// "Every N hours" is only true when N divides the day evenly. The trigger is <c>0 0 0/N * * ?</c>,
/// whose step restarts at midnight, so for any other N the interval is not a period: "every 23
/// hours" really runs twice a day, 23 hours apart and then one hour apart. The description said it
/// anyway, and nothing in the product ever told the user otherwise.
/// </summary>
public sealed class ScheduleDescriptionTests
{
    private static ScheduleProfile Hourly(int interval) =>
        new() { Kind = ScheduleKind.Hourly, IntervalHours = interval };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void Describe_IntervalThatDividesTheDay_KeepsThePlainWording(int interval)
    {
        // These are genuine periods, so the original phrasing is accurate and stays untouched.
        Assert.Equal($"Every {interval} hours", Hourly(interval).Describe());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    public void Describe_EveryHour_IsUnchanged(int interval)
    {
        // Non-positive intervals normalize to "every hour" everywhere else too.
        Assert.Equal("Every hour", Hourly(interval).Describe());
    }

    [Theory]
    [InlineData(5, "Every 5 hours from midnight (00:00, 05:00, 10:00, 15:00, 20:00)")]
    [InlineData(7, "Every 7 hours from midnight (00:00, 07:00, 14:00, 21:00)")]
    [InlineData(13, "Every 13 hours from midnight (00:00, 13:00)")]
    [InlineData(23, "Every 23 hours from midnight (00:00, 23:00)")]
    public void Describe_IntervalThatDoesNotDivideTheDay_NamesTheTimesItActuallyRuns(
        int interval, string expected)
    {
        Assert.Equal(expected, Hourly(interval).Describe());
    }

    [Fact]
    public void Describe_KeepsTheMaintenanceWindowSuffix()
    {
        // The window half of the string is asserted elsewhere; this pins that the new cadence
        // wording composes with it rather than replacing it.
        var schedule = Hourly(7);
        schedule.MaintenanceWindowEnabled = true;
        schedule.WindowStart = new TimeOnly(22, 0);
        schedule.WindowEnd = new TimeOnly(5, 0);
        schedule.DayScope = MaintenanceDayScope.WeekdaysOnly;

        Assert.Equal(
            "Every 7 hours from midnight (00:00, 07:00, 14:00, 21:00) · within 22:00–05:00, weekdays",
            schedule.Describe());
    }

    [Theory]
    [InlineData(ScheduleKind.Manual)]
    [InlineData(ScheduleKind.Daily)]
    [InlineData(ScheduleKind.Weekly)]
    [InlineData(ScheduleKind.Cron)]
    public void DescribeHourlyPattern_IsOnlyForTheHourlyCadence(ScheduleKind kind)
    {
        Assert.Null(new ScheduleProfile { Kind = kind }.DescribeHourlyPattern());
    }

    [Theory]
    [InlineData(5, "Runs at 00:00, 05:00, 10:00, 15:00, 20:00.", 4)]
    [InlineData(7, "Runs at 00:00, 07:00, 14:00, 21:00.", 3)]
    [InlineData(13, "Runs at 00:00, 13:00.", 11)]
    public void DescribeHourlyPattern_NamesTheShortLastGap(int interval, string runs, int gap)
    {
        var pattern = Hourly(interval).DescribeHourlyPattern();

        Assert.StartsWith(runs, pattern);
        Assert.Contains($"last gap of the day is {gap} hours, not {interval}", pattern);
    }

    [Fact]
    public void DescribeHourlyPattern_UsesTheSingularForAOneHourGap()
    {
        // "every 23 hours" is the sharpest case: two runs a day, an hour apart.
        Assert.Contains("last gap of the day is 1 hour, not 23", Hourly(23).DescribeHourlyPattern());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(12)]
    public void DescribeHourlyPattern_SaysNothingAboutAGapWhenTheIntervalDividesTheDay(int interval)
    {
        var pattern = Hourly(interval).DescribeHourlyPattern();

        Assert.DoesNotContain("last gap", pattern);
        Assert.StartsWith("Runs at 00:00,", pattern);
    }

    [Theory]
    [InlineData(1, "Runs on the hour, every hour.")]
    [InlineData(2, "Runs on the hour, every 2 hours.")]
    [InlineData(3, "Runs on the hour, every 3 hours.")]
    public void DescribeHourlyPattern_DoesNotListTwelveOrMoreTimes(int interval, string expected)
    {
        // Listing 24 or 12 times would be noise, and these intervals divide the day evenly anyway,
        // so naming the cadence says everything the list would.
        Assert.Equal(expected, Hourly(interval).DescribeHourlyPattern());
    }

    [Fact]
    public void DescribeHourlyPattern_AgreesWithTheTimesDescribeNames()
    {
        // The two strings are built for different places; they must not be able to disagree.
        for (var interval = 1; interval <= ScheduleProfile.MaxHourlyInterval; interval++)
        {
            var schedule = Hourly(interval);
            var pattern = schedule.DescribeHourlyPattern();

            Assert.NotNull(pattern);
            if (24 % interval != 0)
            {
                foreach (var hour in Enumerable.Range(0, 24).Where(h => h % interval == 0))
                {
                    var time = $"{hour:00}:00";
                    Assert.Contains(time, pattern);
                    Assert.Contains(time, schedule.Describe());
                }
            }
        }
    }
}
