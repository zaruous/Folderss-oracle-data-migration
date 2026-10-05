using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Ui;

namespace DevHost
{
    internal static class Gallery
    {
        public static Window Create()
        {
            var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16) };
            var panel = new StackPanel();
            panel.Children.Add(Kit.SectionLabel("Kit 부품 전시"));
            panel.Children.Add(Kit.Card("Card", Kit.IconButton(Icons.Add, "추가"), Kit.Field("필드", new TextBox(), true, "힌트"), Kit.Pill("ok", "Connected", Icons.Check)));
            panel.Children.Add(Kit.Notice("info", new System.Collections.Generic.List<System.Windows.Documents.Inline> { new System.Windows.Documents.Run("info 알림") }));
            panel.Children.Add(Kit.Notice("warn", new System.Collections.Generic.List<System.Windows.Documents.Inline> { new System.Windows.Documents.Run("warn 알림") }));
            panel.Children.Add(Kit.Segmented(new System.Collections.Generic.List<Kit.SegmentOption>
            {
                new Kit.SegmentOption { Value = "a", Label = "전체 이관" },
                new Kit.SegmentOption { Value = "b", Label = "증분 이관" }
            }, "b", v => { }));
            var errItems = new System.Collections.Generic.List<Kit.RadioCardItem>
            {
                new Kit.RadioCardItem { Value = "c", Title = "계속", Description = "설명", Extra = new TextBox { Width = 92 } },
                new Kit.RadioCardItem { Value = "s", Title = "중지", Description = "설명" }
            };
            panel.Children.Add(Kit.RadioCards("g", errItems, "c", v => { }));
            panel.Children.Add(Kit.RoleTag(true));
            panel.Children.Add(Kit.RoleTag(false));
            panel.Children.Add(Kit.DbBadge(null));
            root.Content = panel;
            return new Window { Title = "Kit Gallery", Content = root, Width = 720, Height = 640 };
        }
    }
}
