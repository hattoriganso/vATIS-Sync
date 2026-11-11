using VatisSync.Core;

namespace VatisSync.Tests;

public class ReviewCornerTests : TempDirTests
{
    [Theory]
    [InlineData("TopLeft", ScreenCorner.TopLeft)]
    [InlineData("topright", ScreenCorner.TopRight)]
    [InlineData("BottomLeft", ScreenCorner.BottomLeft)]
    [InlineData("BottomRight", ScreenCorner.BottomRight)]
    [InlineData("middle", ScreenCorner.BottomRight)] // unknown: the default corner, keep every other setting
    [InlineData("42", ScreenCorner.BottomRight)]
    [InlineData("", ScreenCorner.BottomRight)]
    public void The_saved_corner_is_read_tolerantly_and_nothing_else_is_lost(string value, ScreenCorner expected)
    {
        var loaded = new SettingsStore(Write("s.json", $"{{ \"reviewCorner\": \"{value}\", \"intervalMinutes\": 15 }}")).Load();
        Assert.Equal(expected, loaded.ReviewCorner);
        Assert.Equal(15, loaded.IntervalMinutes);
    }

    [Fact]
    public void The_default_is_bottom_right_and_the_choice_is_remembered_as_a_word()
    {
        Assert.Equal(ScreenCorner.BottomRight, new AppSettings().ReviewCorner);
        Assert.Equal(ScreenCorner.BottomRight, new SettingsStore(Write("a.json", "{ \"intervalMinutes\": 7 }")).Load().ReviewCorner);

        var store = new SettingsStore(Path.Combine(Dir, "sub", "settings.json"));
        store.Save(new AppSettings { ReviewCorner = ScreenCorner.TopLeft });
        Assert.Equal(ScreenCorner.TopLeft, store.Load().ReviewCorner);
        Assert.Contains("\"TopLeft\"", File.ReadAllText(store.FilePath));
    }
}
