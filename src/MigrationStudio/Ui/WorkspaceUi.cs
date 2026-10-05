using System.Windows;
using System.Windows.Controls;

namespace MigrationStudio.Ui
{
    /// <summary>DB Helper WorkspaceUi와 같은 밑줄 탭 버튼.</summary>
    internal static class WorkspaceUi
    {
        public static Button SelectorButton(UIElement content, out Border underline)
        {
            underline = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(9, 3, 9, 1),
                Child = content
            };
            var button = new Button
            {
                Content = underline,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0, 0, 1, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            button.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            return button;
        }

        public static void SetSelected(Button button, Border underline, bool selected)
        {
            if (selected)
            {
                underline.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            }
            else
            {
                underline.ClearValue(Border.BorderBrushProperty);
            }

            Theme.Background(button, selected ? Theme.PanelBackground : Theme.SurfaceBackground);
        }
    }
}
