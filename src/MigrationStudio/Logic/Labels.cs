namespace MigrationStudio.Logic
{
    internal static class Roles
    {
        public const string Source = "source";
        public const string Target = "target";
    }

    internal static class Labels
    {
        public static readonly string[] StepKeys = { "connection", "tables", "columns", "validation", "run" };
        public static readonly string[] StepTitles = { "접속", "테이블 매핑", "컬럼 매핑", "검증", "실행" };

        public const string ComboPlaceholder = "— 접속을 고르세요";
        public const string NextPhaseTooltip = "다음 단계에서 구현";
    }
}
