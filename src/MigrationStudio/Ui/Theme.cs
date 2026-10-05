using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Ui
{
    /// <summary>Folderss 테마 리소스 키와 플러그인 공통 색·글꼴.</summary>
    internal static class Theme
    {
        public const string WindowBackground = "WindowBackground";
        public const string PanelBackground = "PanelBackground";
        public const string SurfaceBackground = "SurfaceBackground";
        public const string ControlBackground = "ControlBackground";
        public const string ControlHover = "ControlHoverBrush";
        public const string ControlPressed = "ControlPressedBrush";
        public const string Border = "BorderBrush";
        public const string PrimaryText = "PrimaryText";
        public const string SecondaryText = "SecondaryText";
        public const string DisabledText = "DisabledTextBrush";
        public const string Accent = "AccentBrush";
        public const string AccentHover = "AccentHoverBrush";
        public const string Selection = "SelectionBrush";
        public const string RowHover = "RowHoverBrush";
        public const string AppFont = "AppFontFamily";

        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, D2Coding, Courier New");
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static readonly Brush Danger = Frozen(0xE0, 0x6C, 0x75);
        public static readonly Brush Warning = Frozen(0xD1, 0x9A, 0x66);
        public static readonly Brush Success = Frozen(0x3F, 0xB2, 0x7F);

        /// <summary>성공색 15% 알파.</summary>
        public static readonly Brush SuccessTint = Tint(Success, 0x26);

        /// <summary>경고색 17% 알파.</summary>
        public static readonly Brush WarningTint = Tint(Warning, 0x2B);

        /// <summary>위험색 15% 알파.</summary>
        public static readonly Brush DangerTint = Tint(Danger, 0x26);

        /// <summary>SQL 원본 꼬리표 · 바인드 이름(밝/어두 테마 공통 중간 톤).</summary>
        public static readonly Brush SqlTag = Frozen(0xA6, 0x7C, 0xD6);
        public static readonly Brush BindName = Frozen(0xC7, 0x9A, 0x3A);

        private static readonly Brush Green = Frozen(0x2E, 0xA0, 0x5B);
        private static readonly Brush Yellow = Frozen(0xC9, 0x93, 0x0A);
        private static readonly Brush Red = Frozen(0xD9, 0x36, 0x3E);

        public static Brush ConnectionColor(string color)
        {
            switch (color)
            {
                case "green": return Green;
                case "yellow": return Yellow;
                case "red": return Red;
                default: return null;
            }
        }

        public static string ConnectionColorTitle(string color)
        {
            switch (color)
            {
                case "green": return "초록 — 개발";
                case "yellow": return "노랑 — 검증";
                case "red": return "빨강 — 운영";
                default: return "없음";
            }
        }

        public static T Foreground<T>(T element, string key = PrimaryText) where T : FrameworkElement
        {
            element.SetResourceReference(TextElement.ForegroundProperty, key);
            return element;
        }

        public static T Background<T>(T element, string key) where T : FrameworkElement
        {
            if (element is Control)
            {
                element.SetResourceReference(Control.BackgroundProperty, key);
            }
            else if (element is Panel)
            {
                element.SetResourceReference(Panel.BackgroundProperty, key);
            }
            else if (element is System.Windows.Controls.Border borderEl)
            {
                borderEl.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, key);
            }
            else if (element is TextBlock)
            {
                element.SetResourceReference(TextBlock.BackgroundProperty, key);
            }

            return element;
        }

        public static TextBlock Text(string text, string key = PrimaryText)
        {
            return Foreground(new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap }, key);
        }

        public static TextBlock Secondary(string text)
        {
            var block = Text(text, SecondaryText);
            block.FontSize = 11.5;
            return block;
        }

        public static void ApplyWindow(Window window)
        {
            window.SetResourceReference(Window.BackgroundProperty, WindowBackground);
            window.SetResourceReference(Window.ForegroundProperty, PrimaryText);
            window.SetResourceReference(Window.FontFamilyProperty, AppFont);
            window.FontSize = 13;
        }

        public static System.Windows.Controls.Border DbBadge(ConnectionProfile profile)
        {
            var text = new TextBlock
            {
                Text = profile == null ? "접속 없음" : profile.Name,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badge = new System.Windows.Controls.Border
            {
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 0, 5, 0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = text
            };
            var fill = profile == null ? null : ConnectionColor(profile.Color);
            if (profile == null)
            {
                text.Foreground = Danger;
                badge.BorderBrush = Danger;
            }
            else if (fill != null)
            {
                text.Foreground = Brushes.White;
                badge.Background = fill;
                badge.BorderBrush = fill;
            }
            else if (profile.WriteBlocked)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, SecondaryText);
                badge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, Theme.Border);
            }
            else
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, Accent);
                badge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, Accent);
            }

            return badge;
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush Tint(Brush source, byte alpha)
        {
            var solid = source as SolidColorBrush;
            if (solid == null)
            {
                return Brushes.Transparent;
            }

            var brush = new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
            brush.Freeze();
            return brush;
        }
    }
}
