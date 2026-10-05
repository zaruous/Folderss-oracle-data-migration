using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MigrationStudio.Ui
{
    internal sealed class RowGridColumn
    {
        public string Header { get; set; }
        public GridLength Width { get; set; } = new GridLength(1, GridUnitType.Star);
        public TextAlignment HeaderAlign { get; set; } = TextAlignment.Left;
    }

    internal sealed class RowGrid
    {
        private readonly List<RowGridColumn> _columns = new List<RowGridColumn>();
        private readonly StackPanel _rows = new StackPanel();
        private readonly Grid _header = new Grid();
        private object _selectedTag;

        public RowGrid(params RowGridColumn[] columns)
        {
            if (columns != null)
            {
                _columns.AddRange(columns);
            }

            Root = new StackPanel { Tag = "RowGrid" };
            AddHeader();
            Root.Children.Add(_rows);
        }

        public StackPanel Root { get; }

        public void AddHeader()
        {
            _header.Children.Clear();
            _header.ColumnDefinitions.Clear();
            _header.RowDefinitions.Clear();
            _header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var i = 0; i < _columns.Count; i++)
            {
                _header.ColumnDefinitions.Add(new ColumnDefinition { Width = _columns[i].Width });
                var tb = Theme.Secondary(_columns[i].Header ?? "");
                tb.FontSize = 11;
                tb.FontWeight = FontWeights.SemiBold;
                tb.TextAlignment = _columns[i].HeaderAlign;
                tb.Margin = new Thickness(4, 0, 4, 4);
                Grid.SetColumn(tb, i);
                _header.Children.Add(tb);
            }

            var headBorder = new Border
            {
                Padding = new Thickness(4, 6, 4, 2),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = _header
            };
            headBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            if (Root.Children.Count == 0)
            {
                Root.Children.Add(headBorder);
            }
            else if (Root.Children[0] is Border)
            {
                Root.Children[0] = headBorder;
            }
            else
            {
                Root.Children.Insert(0, headBorder);
            }
        }

        public void AddRow(FrameworkElement[] cells, object tag, Action onSelect, Action onDoubleClick)
        {
            var rowGrid = new Grid { MinHeight = 32 };
            rowGrid.ColumnDefinitions.Clear();
            for (var i = 0; i < _columns.Count; i++)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = _columns[i].Width });
            }

            for (var i = 0; i < cells.Length && i < _columns.Count; i++)
            {
                if (cells[i] != null)
                {
                    cells[i].Margin = new Thickness(4, 2, 4, 2);
                    Grid.SetColumn(cells[i], i);
                    rowGrid.Children.Add(cells[i]);
                }
            }

            var border = new Border
            {
                Child = rowGrid,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Tag = tag,
                Cursor = Cursors.Hand
            };
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            border.MouseEnter += (s, e) => border.SetResourceReference(Border.BackgroundProperty, Theme.RowHover);
            border.MouseLeave += (s, e) => ApplySelectionStyle(border, tag);
            border.MouseLeftButtonUp += (s, e) =>
            {
                SelectRow(tag);
                if (onSelect != null)
                {
                    onSelect();
                }
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount >= 2 && onDoubleClick != null)
                {
                    onDoubleClick();
                    e.Handled = true;
                }
            };

            ApplySelectionStyle(border, tag);
            _rows.Children.Add(border);
        }

        /// <summary>표 안의 묶음 머리 줄(예: "접속 2"). 전체 열에 걸쳐 SurfaceBackground로 칠한다.</summary>
        public void AddGroupHeader(string title, int count)
        {
            var text = new TextBlock { Margin = new Thickness(8, 4, 8, 4) };
            text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold });
            if (count >= 0)
            {
                var n = new System.Windows.Documents.Run("  " + count) { FontSize = 11 };
                n.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Theme.DisabledText);
                text.Inlines.Add(n);
            }

            text.SetResourceReference(TextBlock.ForegroundProperty, Theme.PrimaryText);
            var border = new Border { Child = text, BorderThickness = new Thickness(0, 0, 0, 1) };
            border.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            _rows.Children.Add(border);
        }

        public void SelectRow(object tag)
        {
            _selectedTag = tag;
            foreach (Border row in _rows.Children)
            {
                if (row.Tag != null)
                {
                    ApplySelectionStyle(row, row.Tag);
                }
            }
        }

        public void Clear()
        {
            _rows.Children.Clear();
            _selectedTag = null;
        }

        private void ApplySelectionStyle(Border border, object tag)
        {
            var sel = ReferenceEquals(tag, _selectedTag);
            if (sel)
            {
                border.SetResourceReference(Border.BackgroundProperty, Theme.Selection);
                border.BorderThickness = new Thickness(2, 0, 0, 1);
                border.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            }
            else
            {
                border.ClearValue(Border.BackgroundProperty);
                border.BorderThickness = new Thickness(0, 0, 0, 1);
                border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            }
        }
    }
}
