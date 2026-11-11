using VatisSync.Core;

namespace VatisSync.Tests;

public class ReviewScreenTests : TempDirTests
{
    // A main screen, and a second one to its left (negative coordinates, as Windows reports it).
    private static readonly ScreenArea Main = new(@"\\.\DISPLAY1", 0, 0, 1920, 1040, IsPrimary: true);
    private static readonly ScreenArea Left = new(@"\\.\DISPLAY2", -1280, 100, 0, 1024, IsPrimary: false);

    [Theory]
    [InlineData(ScreenCorner.BottomRight, 1920 - 400 - 16, 1040 - 70 - 16)]
    [InlineData(ScreenCorner.BottomLeft, 16, 1040 - 70 - 16)]
    [InlineData(ScreenCorner.TopRight, 1920 - 400 - 16, 16)]
    [InlineData(ScreenCorner.TopLeft, 16, 16)]
    public void The_strip_sits_in_the_chosen_corner_clear_of_the_edges(ScreenCorner corner, int x, int y) =>
        Assert.Equal((x, y), Main.Place(corner, 400, 70, 16));

    [Fact]
    public void A_screen_to_the_left_of_the_main_one_is_placed_inside_its_own_area()
    {
        Assert.Equal((-1280 + 16, 100 + 16), Left.Place(ScreenCorner.TopLeft, 400, 70, 16));
        Assert.Equal((0 - 400 - 16, 1024 - 70 - 16), Left.Place(ScreenCorner.BottomRight, 400, 70, 16));
    }

    [Fact]
    public void A_box_bigger_than_the_screen_still_keeps_its_top_left_on_it()
    {
        var tiny = new ScreenArea("x", 0, 0, 300, 50, IsPrimary: true);
        Assert.Equal((0, 0), tiny.Place(ScreenCorner.BottomRight, 400, 70, 16));
    }

    [Fact]
    public void The_chosen_screen_is_used_and_the_main_screen_is_the_fallback()
    {
        var screens = new[] { Left, Main };
        Assert.Same(Left, ScreenPicker.Pick(screens, @"\\.\DISPLAY2"));
        Assert.Same(Left, ScreenPicker.Pick(screens, @"\\.\display2")); // case does not matter
        Assert.Same(Main, ScreenPicker.Pick(screens, ""));              // none chosen
        Assert.Same(Main, ScreenPicker.Pick(screens, null));
        Assert.Same(Main, ScreenPicker.Pick(screens, @"\\.\DISPLAY9")); // unplugged since
    }

    [Fact]
    public void With_no_screen_information_nothing_is_picked_and_with_no_main_flag_the_first_is_used()
    {
        Assert.Null(ScreenPicker.Pick([], @"\\.\DISPLAY2"));
        Assert.Same(Left, ScreenPicker.Pick([Left], ""));
    }

    [Fact]
    public void The_saved_screen_is_remembered_trimmed_and_nonsense_is_dropped()
    {
        Assert.Equal("", new AppSettings().ReviewScreen);
        Assert.Equal("", new SettingsStore(Write("a.json", "{ \"intervalMinutes\": 7 }")).Load().ReviewScreen);
        Assert.Equal(@"\\.\DISPLAY2", new SettingsStore(Write("b.json", "{ \"reviewScreen\": \"  \\\\\\\\.\\\\DISPLAY2 \" }")).Load().ReviewScreen);
        Assert.Equal("", new SettingsStore(Write("c.json", $"{{ \"reviewScreen\": \"{new string('x', 200)}\" }}")).Load().ReviewScreen);
        Assert.Equal("", new SettingsStore(Write("d.json", "{ \"reviewScreen\": null, \"intervalMinutes\": 15 }")).Load().ReviewScreen);

        var store = new SettingsStore(Path.Combine(Dir, "sub", "settings.json"));
        store.Save(new AppSettings { ReviewScreen = @"\\.\DISPLAY2" });
        Assert.Equal(@"\\.\DISPLAY2", store.Load().ReviewScreen);
    }
}
