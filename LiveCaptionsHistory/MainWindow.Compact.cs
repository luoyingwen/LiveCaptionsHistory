using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;

namespace LiveCaptionsHistory;

public partial class MainWindow
{
    public static RoutedUICommand ToggleCompactCommand { get; } = new("切换精炼模式", nameof(ToggleCompactCommand), typeof(MainWindow));
    public static RoutedUICommand ExitCompactCommand { get; } = new("返回完整界面", nameof(ExitCompactCommand), typeof(MainWindow));
    public static RoutedUICommand MoveCompactCommand { get; } = new("移动字幕窗口", nameof(MoveCompactCommand), typeof(MainWindow));
    private bool _compactMode;
    private Rect _fullBounds;
    private Rect _compactBounds = Rect.Empty;
    private Size _fullMinimum;
    private WindowState _fullState;
    private int _modeRevision;

    private void CompactModeClick(object sender, RoutedEventArgs e) => SetCompactMode(!_compactMode);
    private void ToggleCompactExecuted(object sender, ExecutedRoutedEventArgs e) => SetCompactMode(!_compactMode);
    private void ExitCompactExecuted(object sender, ExecutedRoutedEventArgs e) => SetCompactMode(false);
    private void CompactCommandCanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _compactMode && !_closed;

    private void SetCompactMode(bool compact)
    {
        if (_closed || compact == _compactMode) return;
        var offset = TranscriptBox.VerticalOffset;
        _preservedTextOffset = offset;
        var selectionStart = TranscriptBox.SelectionStart;
        var selectionLength = TranscriptBox.SelectionLength;
        var revision = ++_modeRevision;
        _changingText = true;
        if (compact)
        {
            _fullState = WindowState;
            _fullBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            _fullMinimum = new Size(MinWidth, MinHeight);
            WindowState = WindowState.Normal;
            MinWidth = 280;
            MinHeight = 100;
            WindowStyle = WindowStyle.None;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                UseAeroCaptionButtons = false
            });
            if (_compactBounds.IsEmpty)
                _compactBounds = new Rect(_fullBounds.Left, _fullBounds.Top, Math.Min(640, _fullBounds.Width), 240);
            ApplyBounds(_compactBounds);
        }
        else
        {
            _compactBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            WindowState = WindowState.Normal;
            WindowChrome.SetWindowChrome(this, null);
            WindowStyle = WindowStyle.SingleBorderWindow;
            MinWidth = _fullMinimum.Width;
            MinHeight = _fullMinimum.Height;
            ApplyBounds(_fullBounds);
            WindowState = _fullState;
        }
        _compactMode = compact;
        var fullVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HeaderPanel.Visibility = ToolbarPanel.Visibility = FooterPanel.Visibility = fullVisibility;
        TranscriptHeading.Visibility = FollowPanel.Visibility = fullVisibility;
        CompactDragArea.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        RootLayout.Margin = compact ? new Thickness(0) : new Thickness(24, 18, 24, 16);
        Background = (System.Windows.Media.Brush)FindResource(compact ? "SurfaceBrush" : "BackgroundBrush");
        TranscriptBox.Padding = compact ? new Thickness(14, 22, 12, 12) : new Thickness(18, 4, 14, 20);
        TranscriptBox.Margin = compact ? new Thickness(5, 0, 5, 5) : new Thickness(0);
        TranscriptBox.ToolTip = compact ? "拖动上沿或按住 Alt 拖动以移动窗口。右键可操作；Esc 返回完整界面。" : null;
        UpdateEmptyState();
        CommandManager.InvalidateRequerySuggested();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (revision != _modeRevision || _closed) return;
            TranscriptBox.Select(selectionStart, selectionLength);
            if (_following) TranscriptBox.ScrollToEnd();
            else TranscriptBox.ScrollToVerticalOffset(offset);
            _changingText = false;
            TranscriptBox.Focus();
        });
    }
    private void ApplyBounds(Rect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }
    private void UpdateEmptyState()
    {
        var empty = TranscriptBox.Text.Length == 0;
        EmptyState.Visibility = empty && !_compactMode ? Visibility.Visible : Visibility.Collapsed;
        CompactEmptyState.Visibility = empty && _compactMode ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CompactDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_compactMode && !_closed && e.LeftButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            DragMove();
        }
    }
    private void WindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Normal left-drag still selects text; scrolling keeps its usual meaning.
        if (_compactMode && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            CompactDragMouseDown(sender, e);
    }
    private void MoveCompactExecuted(object sender, ExecutedRoutedEventArgs e) => MoveCompactWindow(e.Parameter as string);
    private void MoveWindowClick(object sender, RoutedEventArgs e) => MoveCompactWindow((sender as MenuItem)?.Tag as string);
    private void MoveCompactWindow(string? direction)
    {
        if (!_compactMode || _closed) return;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        switch (direction)
        {
            case "left": Left -= 20; break;
            case "right": Left += 20; break;
            case "up": Top -= 20; break;
            case "down": Top += 20; break;
        }
    }
    private void TranscriptMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuSourceStatus.Header = _captions.Recording ? _lastSourceState : "已暂停，历史记录已保留";
        MenuCompactMode.Header = _compactMode ? "返回完整界面" : "精炼模式";
        MenuMoveWindow.Visibility = _compactMode ? Visibility.Visible : Visibility.Collapsed;
        MenuTopmost.IsChecked = Topmost;
        MenuPause.Header = _captions.Recording ? "暂停记录" : "继续记录";
        MenuCopySelection.IsEnabled = TranscriptBox.SelectionLength > 0;
    }
    private void MenuTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = MenuTopmost.IsChecked;
        TopmostCheck.IsChecked = Topmost;
    }
    private void CopySelectionClick(object sender, RoutedEventArgs e)
    {
        if (TranscriptBox.SelectionLength == 0) return;
        try { Clipboard.SetText(TranscriptBox.SelectedText); }
        catch (System.Runtime.InteropServices.COMException) { Notice.Text = "剪贴板正被占用，请重试。"; }
    }
    private void CloseWindowClick(object sender, RoutedEventArgs e) => Close();
}
