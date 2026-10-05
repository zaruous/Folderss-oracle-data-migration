using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using Microsoft.Win32;

namespace MigrationStudio.Ui
{
    internal static class JobDefinitionDialog
    {
        public static void Show(Window owner, MigrationJob job, MigrationSettings settings)
        {
            var window = DialogKit.Create(owner, "작업 정의", 900);
            window.Height = 600;
            window.SizeToContent = SizeToContent.Manual;
            var body = DialogKit.Body(window);
            var tabs = new TabControl();
            var jsonBox = ReadOnlyBox(JobFile.Serialize(job, settings));
            var yamlBox = ReadOnlyBox(JobFile.ToYaml(job, settings));
            tabs.Items.Add(new TabItem { Header = "JSON", Content = jsonBox });
            tabs.Items.Add(new TabItem { Header = "YAML", Content = yamlBox });
            body.Children.Add(tabs);

            var copy = DialogKit.PlainButton("복사");
            copy.Click += (s, e) =>
            {
                var sel = tabs.SelectedIndex == 1 ? yamlBox.Text : jsonBox.Text;
                DialogKit.TryCopy(sel);
            };
            var saveYaml = DialogKit.PlainButton("YAML 저장");
            saveYaml.Click += (s, e) => SaveText(owner, yamlBox.Text, "YAML (*.yaml)|*.yaml");
            var saveJson = DialogKit.PlainButton("JSON 저장");
            saveJson.Click += (s, e) => SaveText(owner, jsonBox.Text, "JSON (*.json)|*.json");
            var close = DialogKit.CancelButton("닫기", true);
            close.Click += (s, e) => window.Close();
            body.Children.Add(DialogKit.Buttons(copy, saveYaml, saveJson, close));
            window.ShowDialog();
        }

        private static TextBox ReadOnlyBox(string text)
        {
            return new TextBox
            {
                Text = text ?? "",
                IsReadOnly = true,
                FontFamily = Theme.Mono,
                FontSize = 12,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private static void SaveText(Window owner, string text, string filter)
        {
            var dlg = new SaveFileDialog { Filter = filter };
            if (dlg.ShowDialog(owner) != true)
            {
                return;
            }

            File.WriteAllText(dlg.FileName, text ?? "", new System.Text.UTF8Encoding(false));
            Kit.Toast(owner, "저장했습니다: " + Path.GetFileName(dlg.FileName), "ok");
        }
    }
}
