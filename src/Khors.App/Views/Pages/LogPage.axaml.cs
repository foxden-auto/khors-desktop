using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Khors.App.Views.Pages;

public partial class LogPage : UserControl
{
    // Держим журнал прокрученным к последним строкам, пока пользователь не ушёл вверх.
    private bool _followTail = true;

    public LogPage()
    {
        InitializeComponent();
        LogScroll.ScrollChanged += OnLogScrollChanged;
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0)
        {
            if (_followTail)
            {
                Dispatcher.UIThread.Post(
                    () => LogScroll.Offset = new Vector(LogScroll.Offset.X, LogScroll.Extent.Height),
                    DispatcherPriority.Background);
            }

            return;
        }

        if (e.OffsetDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            _followTail = LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - 4;
        }
    }
}
