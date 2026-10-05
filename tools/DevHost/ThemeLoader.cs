using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace DevHost
{
    internal static class ThemeLoader
    {
        public static void Apply(string themesFolder, string themeName)
        {
            var name = string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase) ? "Light.xaml" : "Black.xaml";
            var themePath = Path.Combine(themesFolder, name);
            var controlsPath = Path.Combine(themesFolder, "Controls.xaml");
            if (!File.Exists(themePath) || !File.Exists(controlsPath))
            {
                throw new FileNotFoundException("테마 파일을 찾을 수 없습니다: " + themesFolder);
            }

            var app = Application.Current;
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(LoadDictionary(themePath));
            app.Resources.MergedDictionaries.Add(LoadDictionary(controlsPath));
        }

        private static ResourceDictionary LoadDictionary(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return (ResourceDictionary)XamlReader.Load(stream);
            }
        }
    }
}
