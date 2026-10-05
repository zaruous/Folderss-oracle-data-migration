using System.Collections.Generic;
using System.Linq;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class ValidationLogicTests
    {
        private static ValidationItem Item(string group, string level, string mapping = null, string check = "c")
        {
            return new ValidationItem { Group = group, Check = check, Level = level, MappingId = mapping, Detail = "d" };
        }

        [Fact]
        public void Count_tallies_each_level()
        {
            var c = ValidationLogic.Count(new[]
            {
                Item("a", CheckLevels.Pass), Item("a", CheckLevels.Pass), Item("a", CheckLevels.Warn),
                Item("b", CheckLevels.Error), Item("b", CheckLevels.Info), Item("b", CheckLevels.Skip)
            });
            Assert.Equal(2, c.Pass);
            Assert.Equal(1, c.Warn);
            Assert.Equal(1, c.Error);
            Assert.Equal(1, c.Info);
            Assert.Equal(1, c.Skip);
            Assert.Equal(6, c.Total);
        }

        [Fact]
        public void GroupItems_keeps_first_seen_group_order_and_sorts_by_level_stably()
        {
            var items = new List<ValidationItem>
            {
                Item("접속", CheckLevels.Pass, null, "p1"),
                Item("제약", CheckLevels.Pass, null, "p2"),
                Item("접속", CheckLevels.Error, null, "e1"),
                Item("제약", CheckLevels.Warn, null, "w1"),
                Item("제약", CheckLevels.Warn, null, "w2"),
                Item("제약", CheckLevels.Info, null, "i1")
            };
            var groups = ValidationLogic.GroupItems(items);
            Assert.Equal(new[] { "접속", "제약" }, groups.Select(g => g.Name).ToArray());
            Assert.Equal(new[] { "e1", "p1" }, groups[0].Items.Select(i => i.Check).ToArray());
            Assert.Equal(new[] { "w1", "w2", "i1", "p2" }, groups[1].Items.Select(i => i.Check).ToArray());
        }

        [Fact]
        public void Filter_issues_only_keeps_warn_and_error()
        {
            var items = new[] { Item("a", CheckLevels.Pass), Item("a", CheckLevels.Info), Item("a", CheckLevels.Warn), Item("a", CheckLevels.Error) };
            Assert.Equal(2, ValidationLogic.Filter(items, true).Count);
            Assert.Equal(4, ValidationLogic.Filter(items, false).Count);
        }

        [Fact]
        public void Gate_global_error_always_blocks_mapping_error_only_when_selected()
        {
            var items = new[] { Item("a", CheckLevels.Error, "m1"), Item("a", CheckLevels.Error, "m2") };
            var onlyM2 = ValidationGate.Evaluate(items, new HashSet<string> { "m2" });
            Assert.True(onlyM2.Blocked);
            Assert.Equal(1, onlyM2.Errors);
            var none = ValidationGate.Evaluate(items, new HashSet<string> { "m3" });
            Assert.False(none.Blocked);

            var global = ValidationGate.Evaluate(new[] { Item("접속", CheckLevels.Error, null) }, new HashSet<string>());
            Assert.True(global.Blocked);
        }

        [Fact]
        public void Gate_warnings_only_does_not_block()
        {
            var r = ValidationGate.Evaluate(new[] { Item("a", CheckLevels.Warn, "m1"), Item("a", CheckLevels.Pass) }, new HashSet<string> { "m1" });
            Assert.False(r.Blocked);
            Assert.Equal(1, r.Warnings);
        }

        [Fact]
        public void Gate_notice_kinds()
        {
            var sel = new HashSet<string>();
            Assert.Equal("none", ValidationLogic.Gate(new List<ValidationItem>(), sel, true).Kind);
            var err = ValidationLogic.Gate(new List<ValidationItem> { Item("a", CheckLevels.Error) }, sel, false);
            Assert.Equal("err", err.Kind);
            Assert.Equal("ERROR 1건 — ", err.Lead);
            var warn = ValidationLogic.Gate(new List<ValidationItem> { Item("a", CheckLevels.Warn) }, sel, false);
            Assert.Equal("warn", warn.Kind);
            Assert.True(warn.ShowRunLink);
            var ok = ValidationLogic.Gate(new List<ValidationItem> { Item("a", CheckLevels.Pass) }, sel, false);
            Assert.Equal("ok", ok.Kind);
            Assert.Equal("모두 통과했습니다. ", ok.Lead);
        }

        [Fact]
        public void StepSummary_states()
        {
            Assert.Equal("아직 안 함", ValidationLogic.StepSummary(null).Summary);
            Assert.Equal("busy", ValidationLogic.StepSummary(new PreSessionSummary { Exists = true, Running = true, ItemCount = 3 }).State);
            var err = ValidationLogic.StepSummary(new PreSessionSummary { Exists = true, Errors = 1, Warnings = 4, Passes = 14 });
            Assert.Equal("error", err.State);
            Assert.Equal("PASS 14 · WARN 4 · ERROR 1", err.Summary);
            var stale = ValidationLogic.StepSummary(new PreSessionSummary { Exists = true, Stale = true, Passes = 10 });
            Assert.Equal("warn", stale.State);
            Assert.Equal("검증 뒤 바뀜", stale.Summary);
            Assert.Equal("done", ValidationLogic.StepSummary(new PreSessionSummary { Exists = true, Passes = 3 }).State);
        }

        [Fact]
        public void Fix_labels_and_steps()
        {
            Assert.Equal("컬럼 ›", ValidationLogic.FixLabel(new FixAction { Page = "columns" }));
            Assert.Equal("SQL 편집 ›", ValidationLogic.FixLabel(new FixAction { Page = "sql" }));
            Assert.Null(ValidationLogic.FixLabel(null));
            Assert.Equal(2, ValidationLogic.FixStep(new FixAction { Page = "columns" }));
            Assert.Equal(4, ValidationLogic.FixStep(new FixAction { Page = "run" }));
            Assert.Equal(-1, ValidationLogic.FixStep(new FixAction { Page = "sql" }));
        }

        [Fact]
        public void Progress_is_capped_below_100_and_last_run_text_formats()
        {
            Assert.Equal(96, ValidationLogic.Progress(500));
            Assert.True(ValidationLogic.Progress(17) > 49 && ValidationLogic.Progress(17) < 51);
            Assert.Equal("마지막 검증 14:47:13 · 3.1초", ValidationLogic.LastRunText(new System.DateTime(2026, 10, 3, 14, 47, 13), 3140));
        }
    }
}
