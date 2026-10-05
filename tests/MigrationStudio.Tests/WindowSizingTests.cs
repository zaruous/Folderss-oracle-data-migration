using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    /// <summary>본체 기본 900×600 창을 처음 열 때 1/4 키우는 규칙.</summary>
    public sealed class WindowSizingTests
    {
        [Fact]
        public void Host_default_is_recognised_with_1px_tolerance()
        {
            Assert.True(WindowSizing.IsHostDefault(900, 600));
            Assert.True(WindowSizing.IsHostDefault(900.6, 599.5));
            Assert.False(WindowSizing.IsHostDefault(1125, 750));
            Assert.False(WindowSizing.IsHostDefault(902, 600));
        }

        [Fact]
        public void Preferred_is_a_quarter_larger_on_a_normal_screen()
        {
            var size = WindowSizing.Preferred(1920, 1040);
            Assert.Equal(1125, size.Width);
            Assert.Equal(750, size.Height);
            Assert.Equal(1.25, size.Width / WindowSizing.HostDefaultWidth, 2);
            Assert.Equal(1.25, size.Height / WindowSizing.HostDefaultHeight, 2);
        }

        [Fact]
        public void Preferred_shrinks_to_work_area_but_not_below_host_default()
        {
            var small = WindowSizing.Preferred(1100, 700);
            Assert.Equal(1060, small.Width);
            Assert.Equal(660, small.Height);

            var tiny = WindowSizing.Preferred(800, 600);
            Assert.Equal(900, tiny.Width);
            Assert.Equal(600, tiny.Height);
        }
    }
}
