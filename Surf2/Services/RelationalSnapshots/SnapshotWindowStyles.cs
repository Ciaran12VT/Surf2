using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Surf2.Services.RelationalSnapshots;

internal static class SnapshotWindowStyles
{
    public static Button Command(string text, string style = "SurfPrimaryButtonStyle")
    {
        var button = new Button { Content = text, MinWidth = 82, Padding = new(10, 5, 10, 5), Margin = new(3) };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        return button;
    }
    public static Button ToolButton(string glyph, string name)
    {
        var button = new Button { Content = glyph, FontFamily = new("Segoe MDL2 Assets"),
            ToolTip = name, Width = 34, Height = 32, Padding = new(4), Margin = new(3) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SurfPrimaryButtonStyle");
        return button;
    }
    public static ScrollViewer Status(TextBlock text) => new()
    { Content = text, MaxHeight = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public static void ThemeWindow(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, AppThemeService.WindowBackgroundBrushKey);
        window.SetResourceReference(Control.ForegroundProperty, AppThemeService.TextBrushKey);
    }
    public static void ThemeInput(Control input)
    {
        input.SetResourceReference(Control.BackgroundProperty, AppThemeService.InputBackgroundBrushKey);
        input.SetResourceReference(Control.ForegroundProperty, AppThemeService.InputTextBrushKey);
        input.SetResourceReference(Control.BorderBrushProperty, AppThemeService.StrongBorderBrushKey);
    }
    public static void ThemeGrid(DataGrid grid)
    {
        grid.SetResourceReference(Control.BackgroundProperty, AppThemeService.SurfaceBrushKey);
        grid.SetResourceReference(Control.ForegroundProperty, AppThemeService.TextBrushKey);
        grid.SetResourceReference(Control.BorderBrushProperty, AppThemeService.BorderBrushKey);
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, AppThemeService.SurfaceBrushKey);
        grid.SetResourceReference(DataGrid.AlternatingRowBackgroundProperty, AppThemeService.SurfaceAltBrushKey);
        grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, AppThemeService.BorderBrushKey);
        grid.SetResourceReference(DataGrid.VerticalGridLinesBrushProperty, AppThemeService.BorderBrushKey);
        var header = new Style(typeof(DataGridColumnHeader));
        header.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(AppThemeService.PanelBrushKey)));
        header.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(AppThemeService.TextBrushKey)));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
        header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        header.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        grid.ColumnHeaderStyle = header;
        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cell.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(AppThemeService.TextBrushKey)));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(AppThemeService.SelectionBrushKey)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(AppThemeService.SelectionTextBrushKey)));
        cell.Triggers.Add(selected); grid.CellStyle = cell;
        foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
        {
            var text = new Style(typeof(TextBlock));
            text.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground))
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) }));
            text.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            text.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(TextBlock.Text))
            { RelativeSource = new RelativeSource(RelativeSourceMode.Self) }));
            column.ElementStyle = text;
        }
    }
}
