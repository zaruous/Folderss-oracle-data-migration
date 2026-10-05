using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Services;

namespace MigrationStudio.Ui.Modals
{
    /// <summary>매핑 템플릿 내보내기: 템플릿 이름을 묻는다(UI-MIG-007). 이름을 정하면 호출한 쪽이 저장 위치를 묻는다.</summary>
    internal static class TemplateExportDialog
    {
        /// <summary>정한 이름. 취소하면 null.</summary>
        public static string Show(Window owner, string defaultName)
        {
            var window = DialogKit.Create(owner, "매핑 템플릿 내보내기", 440);
            var body = DialogKit.Body(window);
            body.Children.Add(Theme.Secondary("현재 작업의 테이블·SQL 원본 매핑과 컬럼 매핑을 파일 하나로 내보냅니다. 접속 정보와 비밀번호는 들어가지 않습니다."));
            ((TextBlock)body.Children[body.Children.Count - 1]).TextWrapping = TextWrapping.Wrap;
            var nameBox = new TextBox { Text = defaultName ?? "mapping", FontFamily = Theme.Mono, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
            var field = Kit.Field("템플릿 이름", nameBox, true, "다른 작업에서 가져올 때 보이는 이름입니다");
            field.Margin = new Thickness(0, 12, 0, 12);
            body.Children.Add(field);

            string result = null;
            var export = DialogKit.PrimaryButton("내보내기");
            export.Click += (s, e) =>
            {
                var text = nameBox.Text == null ? "" : nameBox.Text.Trim();
                if (text.Length == 0)
                {
                    nameBox.Focus();
                    return;
                }

                result = text;
                window.DialogResult = true;
            };
            export.IsDefault = true;
            body.Children.Add(DialogKit.Buttons(DialogKit.CancelButton("취소", true), export));
            DialogKit.FocusOnLoad(window, nameBox);
            if (AppServices.DevHostCaptureMode)
            {
                AppServices.DevHostShotWindow = window;
                window.Show();
                return null;
            }

            return window.ShowDialog() == true ? result : null;
        }
    }
}
