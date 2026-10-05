using System;
using System.Collections.Generic;
using System.Linq;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;

namespace MigrationStudio.Logic
{
    /// <summary>검증 화면의 한 묶음(접속 · 객체 …). 항목은 ERROR → WARN → INFO → SKIP → PASS 순.</summary>
    public sealed class ValidationGroup
    {
        public string Name { get; set; }
        public List<ValidationItem> Items { get; set; } = new List<ValidationItem>();
    }

    public sealed class ValidationCounts
    {
        public int Pass { get; set; }
        public int Warn { get; set; }
        public int Error { get; set; }
        public int Info { get; set; }
        public int Skip { get; set; }
        public int Total { get { return Pass + Warn + Error + Info + Skip; } }
    }

    /// <summary>차단 안내 상자 종류: none(진행 중·아직 없음) · err · warn · ok.</summary>
    public sealed class GateNotice
    {
        public string Kind { get; set; } = "none";
        /// <summary>굵게 보일 앞부분.</summary>
        public string Lead { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>안내 끝의 "실행 화면으로 ›" 링크를 보일지.</summary>
        public bool ShowRunLink { get; set; }
    }

    /// <summary>검증 세션 한 번의 상태(화면이 쥔다). JobVersion은 검증을 시작할 때의 작업 버전 — 달라지면 "검증 뒤 작업이 바뀜".</summary>
    public sealed class PreSessionSummary
    {
        public bool Running { get; set; }
        public int ItemCount { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public int Passes { get; set; }
        public bool Stale { get; set; }
        public bool Exists { get; set; }
    }

    /// <summary>실행 후 검증의 한 묶음(작업 하나). 항목은 들어온 순서 그대로(P01 행 수 → P06 NULL 수).</summary>
    public sealed class PostGroup
    {
        public string Name { get; set; }
        public List<PostItem> Items { get; set; } = new List<PostItem>();
    }

    /// <summary>검증 화면의 순수 논리(WPF 없음, 시험 대상).</summary>
    public static class ValidationLogic
    {
        private static readonly string[] LevelOrder =
        {
            CheckLevels.Error, CheckLevels.Warn, CheckLevels.Info, CheckLevels.Skip, CheckLevels.Pass
        };

        public static int LevelRank(string level)
        {
            var i = Array.IndexOf(LevelOrder, level);
            return i < 0 ? LevelOrder.Length : i;
        }

        public static ValidationCounts Count(IEnumerable<ValidationItem> items)
        {
            var c = new ValidationCounts();
            foreach (var item in items ?? Enumerable.Empty<ValidationItem>())
            {
                switch (item.Level)
                {
                    case CheckLevels.Pass: c.Pass++; break;
                    case CheckLevels.Warn: c.Warn++; break;
                    case CheckLevels.Error: c.Error++; break;
                    case CheckLevels.Info: c.Info++; break;
                    case CheckLevels.Skip: c.Skip++; break;
                }
            }

            return c;
        }

        /// <summary>"문제만" 보기: WARN·ERROR만.</summary>
        public static List<ValidationItem> Filter(IEnumerable<ValidationItem> items, bool onlyIssues)
        {
            return (items ?? Enumerable.Empty<ValidationItem>())
                .Where(i => !onlyIssues || i.Level == CheckLevels.Error || i.Level == CheckLevels.Warn)
                .ToList();
        }

        /// <summary>묶음은 처음 나온 순서, 묶음 안은 수준 순(같은 수준은 들어온 순서 유지).</summary>
        public static List<ValidationGroup> GroupItems(IEnumerable<ValidationItem> items)
        {
            var groups = new List<ValidationGroup>();
            foreach (var item in items ?? Enumerable.Empty<ValidationItem>())
            {
                var name = string.IsNullOrEmpty(item.Group) ? "—" : item.Group;
                var g = groups.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
                if (g == null)
                {
                    g = new ValidationGroup { Name = name };
                    groups.Add(g);
                }

                g.Items.Add(item);
            }

            foreach (var g in groups)
            {
                g.Items = g.Items.Select((it, idx) => new { it, idx })
                    .OrderBy(x => LevelRank(x.it.Level)).ThenBy(x => x.idx)
                    .Select(x => x.it).ToList();
            }

            return groups;
        }

        public static ValidationCounts CountPost(IEnumerable<PostItem> items)
        {
            return Count((items ?? Enumerable.Empty<PostItem>()).Select(i => new ValidationItem { Level = i.Level }));
        }

        /// <summary>작업(Group)별 묶음, 처음 나온 순서.</summary>
        public static List<PostGroup> GroupPost(IEnumerable<PostItem> items)
        {
            var groups = new List<PostGroup>();
            foreach (var item in items ?? Enumerable.Empty<PostItem>())
            {
                var name = string.IsNullOrEmpty(item.Group) ? "—" : item.Group;
                var g = groups.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
                if (g == null)
                {
                    g = new PostGroup { Name = name };
                    groups.Add(g);
                }

                g.Items.Add(item);
            }

            return groups;
        }

        /// <summary>행 수 검사가 PASS이면 배지 글자를 MATCH로(원본 = 대상 + 거부), 그 밖은 수준 그대로.</summary>
        public static string PostBadgeText(PostItem item)
        {
            return item.Level == CheckLevels.Pass && string.Equals(item.Check, "행 수", StringComparison.Ordinal) ? "MATCH" : item.Level;
        }

        /// <summary>실행 후 검증을 시작할 수 있는 실행 상태(끝난 뒤에만).</summary>
        public static bool CanStartPost(string runState)
        {
            return runState == RunStates.Done || runState == RunStates.Stopped || runState == RunStates.Failed;
        }

        /// <summary>조치 링크 글자(PASS 항목은 링크를 보이지 않으니 호출하지 않는다).</summary>
        public static string FixLabel(FixAction fix)
        {
            if (fix == null)
            {
                return null;
            }

            switch (fix.Page)
            {
                case "connection": return "접속 ›";
                case "tables": return "테이블 매핑 ›";
                case "columns": return "컬럼 ›";
                case "sql": return "SQL 편집 ›";
                case "run": return "실행 ›";
                default: return null;
            }
        }

        /// <summary>조치 링크가 이동할 단계 번호(0부터). sql은 단계가 아니라 편집기를 열므로 -1.</summary>
        public static int FixStep(FixAction fix)
        {
            if (fix == null)
            {
                return -1;
            }

            switch (fix.Page)
            {
                case "connection": return 0;
                case "tables": return 1;
                case "columns": return 2;
                case "run": return 4;
                default: return -1;
            }
        }

        /// <summary>끝난 검증의 차단 안내 상자(POC gate). 고른 매핑 기준 ERROR만 센다.</summary>
        public static GateNotice Gate(IList<ValidationItem> items, ISet<string> selectedMappingIds, bool running)
        {
            if (running)
            {
                return new GateNotice();
            }

            var gate = ValidationGate.Evaluate(items, selectedMappingIds);
            if (gate.Blocked)
            {
                return new GateNotice
                {
                    Kind = "err",
                    Lead = "ERROR " + gate.Errors + "건 — ",
                    Text = "이관을 실행할 수 없습니다. 고친 뒤 다시 검증하세요. Dry Run은 할 수 있습니다."
                };
            }

            var anyWarn = (items ?? new List<ValidationItem>()).Any(i => i.Level == CheckLevels.Warn);
            return new GateNotice
            {
                Kind = anyWarn ? "warn" : "ok",
                Lead = anyWarn ? "경고를 확인했다면 실행할 수 있습니다. " : "모두 통과했습니다. ",
                Text = "",
                ShowRunLink = true
            };
        }

        /// <summary>단계 막대 번호 원 상태와 한 줄 요약(검증 단계).</summary>
        public static StepInfo StepSummary(PreSessionSummary pre)
        {
            if (pre == null || !pre.Exists)
            {
                return new StepInfo { State = "", Summary = "아직 안 함" };
            }

            if (pre.Running)
            {
                return new StepInfo { State = "busy", Summary = "검사 중… " + pre.ItemCount };
            }

            var state = pre.Errors > 0 ? "error" : pre.Stale || pre.Warnings > 0 ? "warn" : "done";
            var text = pre.Stale
                ? "검증 뒤 바뀜"
                : "PASS " + pre.Passes + " · WARN " + pre.Warnings + " · ERROR " + pre.Errors;
            return new StepInfo { State = state, Summary = text };
        }

        /// <summary>진행 막대 값(0~100): 항목 수 / 대략 34개, 끝나기 전에는 96%를 넘지 않는다.</summary>
        public static double Progress(int itemCount)
        {
            return Math.Min(96, itemCount / 34.0 * 100);
        }

        /// <summary>"마지막 검증 14:47:13 · 3.1초"</summary>
        public static string LastRunText(DateTime at, long elapsedMs)
        {
            return "마지막 검증 " + at.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " · " +
                   (elapsedMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "초";
        }
    }
}
