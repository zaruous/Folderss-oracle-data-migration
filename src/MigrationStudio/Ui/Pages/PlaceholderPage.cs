using System.Windows.Controls;
using MigrationStudio.Logic;

namespace MigrationStudio.Ui.Pages
{
    internal sealed class PlaceholderPage : PageFrame
    {
        private static readonly string[] DesignDocs =
        {
            "UI-MIG-002",
            "UI-MIG-003",
            "UI-MIG-004",
            "UI-MIG-005",
            "UI-MIG-006"
        };

        private static readonly string[] Icons =
        {
            Ui.Icons.Table,
            Ui.Icons.Table,
            Ui.Icons.Table,
            Ui.Icons.Checklist,
            Ui.Icons.Play
        };

        public PlaceholderPage(int stepIndex)
        {
            var doc = stepIndex >= 1 && stepIndex < DesignDocs.Length + 1 ? DesignDocs[stepIndex - 1] : "UI-MIG-00x";
            SetStep(stepIndex, Labels.StepTitles[stepIndex], "다음 단계에서 구현됩니다.");
            SetBody(Kit.EmptyState(Icons[stepIndex], "다음 단계에서 구현됩니다", "설계: docs/design-docs/" + doc + "_Design.md"));
            SetFooterHint("");
        }
    }
}
