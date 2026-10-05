using System;
using System.Collections.Generic;

namespace MigrationStudio.Core.Validation
{
    /// <summary>
    /// 실행 후 검증 결과 한 줄(UI-MIG-005 4.2 P01~P06). 원본과 대상을 따로 집계해 비교하므로 두 값을 함께 가진다.
    /// 행 수 검사(P01)가 PASS이면 화면은 배지를 "MATCH"로 보인다.
    /// </summary>
    public sealed class PostItem
    {
        /// <summary>작업 라벨(예: "SRC_CUSTOMER → TB_MEMBER"). 묶음 머리가 된다.</summary>
        public string Group { get; set; }
        /// <summary>행 수 · PK 누락 · 중복 키 · 샘플 데이터 · 해시 · NULL 수 · 열 이름</summary>
        public string Check { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        /// <summary><see cref="MigrationStudio.Core.Model.CheckLevels"/> 값.</summary>
        public string Level { get; set; }
        public string Detail { get; set; }
        public string MappingId { get; set; }
    }

    /// <summary>실행 후 검증에 필요한 실행 결과(엔진의 마지막 스냅숏과 실행 정보).</summary>
    public sealed class PostValidationRequest
    {
        public string RunId { get; set; }
        public bool Dry { get; set; }
        public Engine.RunSnapshot Final { get; set; }
    }

    /// <summary>실행 후 검증 서비스 계약. 화면은 이것만 안다.</summary>
    public interface IPostValidationRunner
    {
        /// <summary>검사를 하나씩 끝나는 대로 <paramref name="onItem"/>으로 알리고(스레드 풀에서 호출될 수 있음) 전체 목록을 돌려준다.</summary>
        System.Threading.Tasks.Task<List<PostItem>> RunPostAsync(PostValidationRequest request, Action<PostItem> onItem, System.Threading.CancellationToken ct);
    }
}
