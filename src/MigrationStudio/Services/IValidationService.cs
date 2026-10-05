using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Validation;

namespace MigrationStudio.Services
{
    /// <summary>실행 전 검증을 돌린다. 화면은 이 인터페이스만 안다(구현: ValidationService, DevHost 시험용 가짜).</summary>
    internal interface IValidationService
    {
        /// <summary>
        /// 검사를 하나씩 끝나는 대로 <paramref name="onItem"/>으로 알리고(스레드 풀 스레드에서 호출될 수 있다 — 화면이 UI 스레드로 보낸다)
        /// 전체 목록을 돌려준다. 취소하면 OperationCanceledException.
        /// </summary>
        Task<List<ValidationItem>> RunPreAsync(Action<ValidationItem> onItem, CancellationToken ct);
    }
}
