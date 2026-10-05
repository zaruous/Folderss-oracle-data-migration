using System;
using System.Collections.Generic;
using System.Linq;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Validation
{
    /// <summary>검사 결과에서 "고치러 가기"가 가리키는 화면. Page: connection · tables · columns · sql · run.</summary>
    public sealed class FixAction
    {
        public string Page { get; set; }
        public string MappingId { get; set; }
        public string Column { get; set; }
    }

    /// <summary>실행 전 검증 결과 한 줄. 검증 엔진이 만들고 검증 화면이 묶음별로 보여 준다.</summary>
    public sealed class ValidationItem
    {
        /// <summary>접속 · 객체 · 매핑 · 형식 · 제약 · 공간·실행</summary>
        public string Group { get; set; }
        public string Check { get; set; }
        public string Target { get; set; }
        /// <summary><see cref="CheckLevels"/> 값.</summary>
        public string Level { get; set; }
        public string Detail { get; set; }
        public FixAction Fix { get; set; }
        /// <summary>특정 매핑에 속하는 항목이면 그 매핑 id. null이면 전역 항목(접속·메타데이터 …) — 실행을 항상 막는다.</summary>
        public string MappingId { get; set; }
        /// <summary>표본으로 잰 값이면 "표본 5%" 같은 꼬리말. 아니면 null.</summary>
        public string Sample { get; set; }
    }

    public sealed class GateResult
    {
        public bool Blocked { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public List<ValidationItem> Blocking { get; set; } = new List<ValidationItem>();
    }

    /// <summary>실행 차단 판정(UI-MIG-005 5장): 전역 ERROR는 항상 막고, 매핑에 속한 ERROR는 고른 매핑일 때만 막는다.</summary>
    public static class ValidationGate
    {
        public static GateResult Evaluate(IEnumerable<ValidationItem> items, ISet<string> selectedMappingIds)
        {
            var result = new GateResult();
            if (items == null)
            {
                return result;
            }

            foreach (var item in items)
            {
                if (string.Equals(item.Level, CheckLevels.Warn, StringComparison.Ordinal))
                {
                    if (item.MappingId == null || selectedMappingIds == null || selectedMappingIds.Contains(item.MappingId))
                    {
                        result.Warnings++;
                    }

                    continue;
                }

                if (!string.Equals(item.Level, CheckLevels.Error, StringComparison.Ordinal))
                {
                    continue;
                }

                var applies = item.MappingId == null || selectedMappingIds == null || selectedMappingIds.Contains(item.MappingId);
                if (applies)
                {
                    result.Errors++;
                    result.Blocking.Add(item);
                }
            }

            result.Blocked = result.Errors > 0;
            return result;
        }
    }
}
