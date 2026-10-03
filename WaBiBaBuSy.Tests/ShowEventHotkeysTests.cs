using WaBiBaBuSy.Models.Show;
using Xunit;

namespace WaBiBaBuSy.Tests;

public class ShowEventHotkeysTests
{
    [Theory]
    [InlineData(1, ShowEventType.Spotlight)]
    [InlineData(2, ShowEventType.Celebration)]
    [InlineData(3, ShowEventType.Traversal)]
    [InlineData(4, ShowEventType.Blackout)]
    [InlineData(5, ShowEventType.Announce)]
    public void Slot_MapsToType(int slot, ShowEventType type) => Assert.Equal(type, ShowEventHotkeys.TypeForSlot(slot));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void UnknownSlot_IsNull(int slot) => Assert.Null(ShowEventHotkeys.TypeForSlot(slot));

    [Fact]
    public void Gesture_Text() => Assert.Equal("Ctrl+Alt+3", ShowEventHotkeys.Gesture(3));

    [Fact]
    public void Slots_AreOneToFive() => Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ShowEventHotkeys.Slots);
}
