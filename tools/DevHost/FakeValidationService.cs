using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Services;

namespace DevHost
{
    /// <summary>Oracle 없이 검증 화면을 보이는 시험용 가짜. POC runPre와 같은 모양(ERROR 1·WARN 4·INFO 4, 묶음 6개)의 결과를 돌려준다.</summary>
    internal sealed class FakeValidationService : IValidationService
    {
        private readonly int _freezeAt;
        private readonly int _delayMs;

        /// <param name="freezeAt">0보다 크면 그 개수에서 멈춘다(진행 중 화면 캡처용).</param>
        public FakeValidationService(int freezeAt, int delayMs)
        {
            _freezeAt = freezeAt;
            _delayMs = delayMs;
        }

        public async Task<List<ValidationItem>> RunPreAsync(Action<ValidationItem> onItem, CancellationToken ct)
        {
            var all = Canned();
            var result = new List<ValidationItem>();
            foreach (var item in all)
            {
                ct.ThrowIfCancellationRequested();
                if (_delayMs > 0)
                {
                    await Task.Delay(_delayMs, ct).ConfigureAwait(false);
                }

                result.Add(item);
                if (onItem != null)
                {
                    onItem(item);
                }

                if (_freezeAt > 0 && result.Count >= _freezeAt)
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
            }

            return result;
        }

        private static ValidationItem I(string group, string check, string target, string level, string detail, string page = null, string mapping = null, string column = null, string sample = null)
        {
            return new ValidationItem
            {
                Group = group,
                Check = check,
                Target = target,
                Level = level,
                Detail = detail,
                MappingId = mapping,
                Sample = sample,
                Fix = page == null ? null : new FixAction { Page = page, MappingId = mapping, Column = column }
            };
        }

        private static List<ValidationItem> Canned()
        {
            return new List<ValidationItem>
            {
                I("접속", "원본 접속", "LEGACY_PROD", CheckLevels.Pass, "Oracle 19c · 31 ms · 10.10.10.21:1521/LEGACY · 읽기 전용(SELECT만)"),
                I("접속", "대상 접속", "NEXT_PROD", CheckLevels.Pass, "Oracle 19c · 18 ms · 10.20.10.35:1521/NEXTDB"),
                I("객체", "원본 테이블 존재", "LEGACY_APP", CheckLevels.Pass, "SRC_CUSTOMER, SRC_ORDER, SRC_CUSTOMER_GRADE (3개)"),
                I("객체", "대상 테이블 존재", "NEXT_APP", CheckLevels.Pass, "TB_MEMBER, TB_SALES_ORDER, TB_MEMBER_GRADE"),
                I("매핑", "원본 SQL", "SQL SQLMAP_MEMBER → TB_MEMBER", CheckLevels.Pass, "구문·객체·별칭 통과 · 결과 열 7개 · 바인드 :LAST_ID", "sql", "tm-sql-member"),
                I("매핑", "컬럼 매핑", "SRC_CUSTOMER → TB_MEMBER", CheckLevels.Pass, "7 / 7 컬럼", "columns", "tm-customer"),
                I("매핑", "컬럼 매핑", "SRC_ORDER → TB_SALES_ORDER", CheckLevels.Info, "6 / 7 컬럼 · 비워 둠: CHANNEL_CD(NOT NULL — 제약 참고)", "columns", "tm-order"),
                I("형식", "데이터 형식 호환성", "", CheckLevels.Pass, "매핑한 열의 형식이 모두 호환됨"),
                I("형식", "VARCHAR 길이", "TB_MEMBER", CheckLevels.Warn, "PHONE_NO VARCHAR2(30) → MOBILE_NO VARCHAR2(20)\n잘림 위험(Truncation Risk) · 실측 최대 13자라 지금 데이터는 들어감", "columns", "tm-customer", "MOBILE_NO", "표본 5%"),
                I("형식", "NUMBER 정밀도", "TB_SALES_ORDER", CheckLevels.Warn, "ORDER_AMT NUMBER(15,2) → TOTAL_AMT NUMBER(13,2)\n정수부가 줄어듭니다 · 실측 최대 4,820,000.00", "columns", "tm-order", "TOTAL_AMT"),
                I("제약", "NOT NULL", "TB_SALES_ORDER.CHANNEL_CD", CheckLevels.Error, "NOT NULL 컬럼에 값이 없음 — 모든 행이 ORA-01400으로 거부됨", "columns", "tm-order", "CHANNEL_CD"),
                I("제약", "NOT NULL", "TB_MEMBER.MEMBER_NAME", CheckLevels.Warn, "NULL 37행은 거부되어 오류 테이블로 감", "columns", "tm-customer", "MEMBER_NAME", "표본 5%"),
                I("제약", "PK / Unique Key", "TB_MEMBER", CheckLevels.Pass, "대상 PK MEMBER_ID ← CUSTOMER_ID", "columns", "tm-customer"),
                I("제약", "중복 키", "SRC_CUSTOMER", CheckLevels.Pass, "원본 CUSTOMER_ID 중복 0건 · 대상 기존 58,225행은 갱신됨", null, null, null, "표본 5%"),
                I("제약", "참조 무결성(FK)", "TB_SALES_ORDER.MEMBER_ID", CheckLevels.Pass, "FK_SALES_ORDER_MEMBER → TB_MEMBER · 부모 작업이 먼저 실행됨"),
                I("공간·실행", "대상 테이블스페이스", "NEXT_DATA", CheckLevels.Pass, "필요 약 1.12 GB(행 × 평균 행 길이 × 1.35, 인덱스 포함) / 여유 182.4 GB"),
                I("공간·실행", "운영 DB 쓰기", "NEXT_PROD", CheckLevels.Warn, "TB_MEMBER_GRADE: TRUNCATE + INSERT — 되돌릴 수 없음, 실행 전에 한 번 더 묻습니다", "tables"),
                I("공간·실행", "오류 테이블", "ERR$_TB_MEMBER", CheckLevels.Info, "없으면 실행 전에 만듭니다(DBMS_ERRLOG.CREATE_ERROR_LOG) · 거부 행은 RUN_ID와 함께 남음"),
                I("공간·실행", "체크포인트", "SRC_CUSTOMER → TB_MEMBER", CheckLevels.Info, "지난 실행(R-20261002-234107)이 CUSTOMER_ID = 850,000에서 멈춤 — 실행 화면의 [체크포인트에서 재개]로 이어서 할 수 있음", "run")
            };
        }
    }
}
