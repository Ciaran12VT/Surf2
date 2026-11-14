using System.Windows;
using System.Windows.Controls;

namespace Surf2;

internal static class RemoteFolderDialogUi
{
    internal static StackPanel Body(Window window, string title, double width = 680, double height = 540)
    {
        window.Title = title; window.Width = width; window.Height = height; window.MinWidth = 480; window.MinHeight = 300;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return panel;
    }
    internal static TextBox Field(Panel panel, string label, string value = "")
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 3) });
        var field = new TextBox { Text = value, MinHeight = 28, Padding = new Thickness(5), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(field); return field;
    }
    internal static TextBlock Note(Panel panel, string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };
        panel.Children.Add(label); return label;
    }
    internal static Button Button(Panel panel, string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 8, 0), MinWidth = 90 };
        button.Click += handler; panel.Children.Add(button); return button;
    }
}
