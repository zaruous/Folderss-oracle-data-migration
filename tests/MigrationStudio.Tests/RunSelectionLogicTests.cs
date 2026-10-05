using System;
using System.Collections.Generic;
using MigrationStudio.Core.Model;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    /// <summary>실행 화면 작업 선택 — 실행 화면이 매핑보다 먼저 만들어져도 나중에 추가한 매핑이 기본으로 골라져야 한다.</summary>
    public sealed class RunSelectionLogicTests
    {
        private static Mapping M(string id, bool use = true)
        {
            return new Mapping { Id = id, Use = use, Source = id, Target = id };
        }

        [Fact]
        public void Mappings_added_after_first_sync_are_selected_by_default()
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var known = new HashSet<string>(StringComparer.Ordinal);
            RunLogic.SyncSelection(selected, known, new List<Mapping>());
            Assert.Empty(selected);

            RunLogic.SyncSelection(selected, known, new[] { M("a"), M("b"), M("c", false) });
            Assert.Equal(new[] { "a", "b" }, new SortedSet<string>(selected));
        }

        [Fact]
        public void User_deselection_survives_resync_and_removed_mappings_drop_out()
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var known = new HashSet<string>(StringComparer.Ordinal);
            RunLogic.SyncSelection(selected, known, new[] { M("a"), M("b") });
            selected.Remove("a");

            RunLogic.SyncSelection(selected, known, new[] { M("a"), M("b"), M("d") });
            Assert.Equal(new[] { "b", "d" }, new SortedSet<string>(selected));

            RunLogic.SyncSelection(selected, known, new[] { M("a"), M("d") });
            Assert.Equal(new[] { "d" }, new SortedSet<string>(selected));
        }
    }
}
