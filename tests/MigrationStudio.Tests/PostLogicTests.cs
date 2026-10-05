using System.Collections.Generic;
using System.Linq;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class PostLogicTests
    {
        private static PostItem P(string group, string check, string level)
        {
            return new PostItem { Group = group, Check = check, Level = level };
        }

        [Fact]
        public void GroupPost_keeps_arrival_order_per_task()
        {
            var items = new[]
            {
                P("A → B", "행 수", CheckLevels.Pass), P("C → D", "행 수", CheckLevels.Warn),
                P("A → B", "PK 누락", CheckLevels.Pass), P("A → B", "해시", CheckLevels.Pass)
            };
            var groups = ValidationLogic.GroupPost(items);
            Assert.Equal(new[] { "A → B", "C → D" }, groups.Select(g => g.Name).ToArray());
            Assert.Equal(new[] { "행 수", "PK 누락", "해시" }, groups[0].Items.Select(i => i.Check).ToArray());
        }

        [Fact]
        public void Badge_text_is_match_only_for_passing_row_count()
        {
            Assert.Equal("MATCH", ValidationLogic.PostBadgeText(P("g", "행 수", CheckLevels.Pass)));
            Assert.Equal("PASS", ValidationLogic.PostBadgeText(P("g", "해시", CheckLevels.Pass)));
            Assert.Equal("ERROR", ValidationLogic.PostBadgeText(P("g", "행 수", CheckLevels.Error)));
            Assert.Equal("SKIP", ValidationLogic.PostBadgeText(P("g", "행 수", CheckLevels.Skip)));
        }

        [Fact]
        public void CountPost_tallies_levels()
        {
            var c = ValidationLogic.CountPost(new[] { P("g", "a", CheckLevels.Pass), P("g", "b", CheckLevels.Pass), P("g", "c", CheckLevels.Warn), P("g", "d", CheckLevels.Error) });
            Assert.Equal(2, c.Pass);
            Assert.Equal(1, c.Warn);
            Assert.Equal(1, c.Error);
        }

        [Fact]
        public void Post_validation_starts_only_after_a_run_ends()
        {
            Assert.True(ValidationLogic.CanStartPost(RunStates.Done));
            Assert.True(ValidationLogic.CanStartPost(RunStates.Stopped));
            Assert.True(ValidationLogic.CanStartPost(RunStates.Failed));
            Assert.False(ValidationLogic.CanStartPost(RunStates.Running));
            Assert.False(ValidationLogic.CanStartPost(RunStates.Paused));
            Assert.False(ValidationLogic.CanStartPost(RunStates.Idle));
        }
    }
}
