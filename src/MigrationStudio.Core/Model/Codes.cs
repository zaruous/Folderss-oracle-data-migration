using System;
using System.Collections.Generic;
using System.Linq;

namespace MigrationStudio.Core.Model
{
    public static class SourceTypes
    {
        public const string Table = "TABLE";
        public const string Sql = "SQL";
    }

    public static class ExecutionModes
    {
        public const string Full = "FULL";
        public const string Incremental = "INCREMENTAL";
        public const string Cdc = "CDC";
    }

    /// <summary>
    /// 원본에서 지운 행을 대상에 어떻게 반영하나(매핑 단위). 증분·CDC에서만 쓴다.
    /// NONE: 따라가지 않음(대상에 남음 — 차이만 보임). MARK: 대상 행을 지우지 않고 표시 열에 값을 넣음(되돌릴 수 있음).
    /// </summary>
    public static class DeleteModes
    {
        public const string None = "NONE";
        public const string Mark = "MARK";

        public static bool IsMark(string mode)
        {
            return string.Equals(mode, Mark, StringComparison.Ordinal);
        }

        public static string Label(string mode)
        {
            return IsMark(mode) ? "대상에 삭제 표시" : "따라가지 않음";
        }
    }

    public static class ErrorPolicies
    {
        /// <summary>행 오류는 오류 테이블(LOG ERRORS)에 남기고 계속, 일시 오류는 3회 재시도.</summary>
        public const string Continue = "CONTINUE";
        public const string Stop = "STOP";
        public const string Retry = "RETRY";

        public static string Label(string policy)
        {
            switch (policy)
            {
                case Continue: return "계속 + 오류 테이블";
                case Stop: return "오류 시 중지";
                case Retry: return "3회 재시도";
                default: return policy ?? "";
            }
        }
    }

    /// <summary>쓰기 방식 하나의 성질.</summary>
    public sealed class WriteModeInfo
    {
        public string Value { get; set; }
        public string Label { get; set; }
        /// <summary>병합 키가 필요한가(MERGE ON · DELETE 조건).</summary>
        public bool NeedsKey { get; set; }
        /// <summary>대상의 기존 행을 지우는가(실행 직전 확인 체크).</summary>
        public bool Destructive { get; set; }
    }

    public static class WriteModes
    {
        public const string InsertOnly = "INSERT_ONLY";
        public const string Merge = "MERGE";
        public const string TruncateInsert = "TRUNCATE_INSERT";
        public const string DeleteInsert = "DELETE_INSERT";

        public static readonly IReadOnlyList<WriteModeInfo> All = new[]
        {
            new WriteModeInfo { Value = InsertOnly, Label = "INSERT ONLY" },
            new WriteModeInfo { Value = Merge, Label = "INSERT + UPDATE", NeedsKey = true },
            new WriteModeInfo { Value = TruncateInsert, Label = "TRUNCATE + INSERT", Destructive = true },
            new WriteModeInfo { Value = DeleteInsert, Label = "DELETE + INSERT", NeedsKey = true, Destructive = true }
        };

        /// <summary>모르는 값이면 INSERT ONLY로 본다.</summary>
        public static WriteModeInfo Of(string mode)
        {
            return All.FirstOrDefault(m => m.Value == mode) ?? All[0];
        }
    }

    public static class NullRules
    {
        public const string Allow = "ALLOW";
        public const string Reject = "REJECT";
        public const string Default = "DEFAULT";
        public const string Sysdate = "SYSDATE";
        public const string Empty = "EMPTY";
        public const string Custom = "CUSTOM";

        public static readonly IReadOnlyList<string> All = new[] { Allow, Reject, Default, Sysdate, Empty, Custom };

        public static string Label(string rule)
        {
            switch (rule)
            {
                case Allow: return "NULL 허용";
                case Reject: return "행 거부";
                case Default: return "기본값";
                case Sysdate: return "SYSDATE";
                case Empty: return "빈 문자열";
                case Custom: return "사용자 식";
                default: return rule ?? "";
            }
        }
    }

    /// <summary>검사 결과 수준. 순위: PASS &lt; INFO·SKIP &lt; WARN &lt; ERROR.</summary>
    public static class CheckLevels
    {
        public const string Pass = "PASS";
        public const string Info = "INFO";
        public const string Skip = "SKIP";
        public const string Warn = "WARN";
        public const string Error = "ERROR";

        public static int Rank(string level)
        {
            switch (level)
            {
                case Error: return 3;
                case Warn: return 2;
                case Info:
                case Skip: return 1;
                default: return 0;
            }
        }

        /// <summary>가장 나쁜 수준. 비었으면 PASS.</summary>
        public static string Worst(IEnumerable<string> levels)
        {
            var worst = Pass;
            foreach (var level in levels ?? Enumerable.Empty<string>())
            {
                if (Rank(level) > Rank(worst))
                    worst = level;
            }
            return worst;
        }
    }
}
