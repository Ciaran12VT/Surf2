using System.Windows;
using System.Windows.Threading;

namespace Surf2;

public partial class StartupSplashWindow : Window
{
    public StartupSplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CenterOnPrimaryWorkArea();
    }

    public void CenterOnPrimaryWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;

        Left = workArea.Left + ((workArea.Width - width) / 2);
        Top = workArea.Top + ((workArea.Height - height) / 2);

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            double measuredWidth = ActualWidth > 0 ? ActualWidth : width;
            double measuredHeight = ActualHeight > 0 ? ActualHeight : height;
            Left = workArea.Left + ((workArea.Width - measuredWidth) / 2);
            Top = workArea.Top + ((workArea.Height - measuredHeight) / 2);
        }), DispatcherPriority.Loaded);
    }
}
