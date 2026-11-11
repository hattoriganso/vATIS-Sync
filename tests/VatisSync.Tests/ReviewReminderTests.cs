using VatisSync.Core;

namespace VatisSync.Tests;

public class ReviewReminderTests : TempDirTests
{
    [Theory]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    [InlineData(0, 1)]     // never a reminder that fires at once, over and over
    [InlineData(-4, 1)]
    [InlineData(500, 60)]
    public void The_reminder_time_is_kept_within_sensible_limits(int saved, int expected) =>
        Assert.Equal(expected, new SettingsStore(Write("s.json", $"{{ \"reviewReminderMinutes\": {saved}, \"intervalMinutes\": 15 }}")).Load().ReviewReminderMinutes);

    [Fact]
    public void The_default_is_three_minutes_and_the_choice_is_remembered()
    {
        Assert.Equal(3, new AppSettings().ReviewReminderMinutes);
        Assert.Equal(3, new SettingsStore(Write("a.json", "{ \"intervalMinutes\": 7 }")).Load().ReviewReminderMinutes);

        var store = new SettingsStore(Path.Combine(Dir, "sub", "settings.json"));
        store.Save(new AppSettings { ReviewReminderMinutes = 10 });
        Assert.Equal(10, store.Load().ReviewReminderMinutes);
    }
}
