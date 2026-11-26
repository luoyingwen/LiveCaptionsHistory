using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiveCaptionsHistory;

public partial class MainWindow : Window
{
    private readonly CaptionAccumulator _captions = new();
    private readonly CaptionReader _reader = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string _sessionPath;
    private readonly string _historyDirectory;
    private bool _following = true;
    private bool _changingText;
    private double _preservedTextOffset;
    private bool _dirty;
    private Task _saveTask = Task.CompletedTask;
    private bool _closed;
    private bool _closeAfterSave;
    private bool _changingLanguage;
    private int _unreadUpdates;
    private string _lastSourceState = "正在启动本机识别引擎…";
    private sealed record RecognitionLanguage(string Code, string Name)
    {
        public override string ToString() => Name;
    }

    public MainWindow()
    {
        InitializeComponent();
        TopmostCheck.IsChecked = true;
        TranscriptBox.AddHandler(RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(KeepTranscriptViewport), handledEventsToo: true);
        _historyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptionsHistory", "Sessions");
        _sessionPath = Path.Combine(_historyDirectory, $"字幕-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.txt");
        SessionLabel.Text = $"本次记录  {DateTime.Now:MM月dd日 HH:mm}";
        _reader.Reading += reading => Dispatcher.BeginInvoke(() => Receive(reading));
        _saveTimer.Tick += async (_, _) => await SaveAsync();
        _earlyTimer.Tick += (_, _) => FlushEarlyTranslation();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Full and compact mode share this HWND. Set the affinity before the window is shown.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            && !SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WdaExcludeFromCapture))
        {
            Trace.TraceWarning("Could not exclude the caption window from capture (Win32 error {0}).",
                Marshal.GetLastWin32Error());
        }
    }

    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _reader.Start();
        _saveTimer.Start();
    }
    private void Receive(CaptionReading reading)
    {
        if (_closed || !_reader.IsCurrent(reading)) return;
        _lastSourceState = reading.State;
        SourceStatus.Text = reading.State;
        UpdateLanguages(reading);
        if (reading.SourceClosed || reading.Languages is not null || reading.Language is not null)
            _activeRecognitionLanguage = reading.SourceClosed ? null : reading.Language;
        if (reading.SourceClosed)
        {
            DiscardUnfinishedTranslation();
            _captions.SourceClosed();
            RenderText();
        }
        if (reading.Text is { } text)
        {
            var changed = _captions.ObserveRecognition(text, reading.IsFinal, out var finalized);
            ObserveTranslation(changed, finalized);
            if (changed)
            {
                _dirty = true;
                RenderText();
            }
            else if (finalized is not null && _earlyMode)
                RenderTranscript(_captions.DisplayText, translationOnly: true);
        }
        UpdateRecordingStatus();
    }
    private void UpdateLanguages(CaptionReading reading)
    {
        if (reading.Languages is null && reading.Language is null) return;
        _changingLanguage = true;
        try
        {
            var selected = reading.Language ?? LanguagePicker.SelectedValue as string;
            if (reading.Languages is not null)
            {
                LanguagePicker.ItemsSource = reading.Languages.Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(code => new RecognitionLanguage(code, code.ToLowerInvariant() switch
                    {
                        "zh-cn" => "中文（简体）",
                        "en-us" => "英语",
                        _ => code
                    })).ToArray();
                LanguagePicker.IsEnabled = LanguagePicker.Items.Count > 0;
            }
            if (selected is not null)
                LanguagePicker.SelectedItem = LanguagePicker.Items.Cast<RecognitionLanguage>()
                    .FirstOrDefault(item => string.Equals(item.Code, selected, StringComparison.OrdinalIgnoreCase));
        }
        finally { _changingLanguage = false; }
    }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingLanguage || LanguagePicker.SelectedItem is not RecognitionLanguage language) return;
        DiscardUnfinishedTranslation();
        _captions.SourceClosed();
        RenderText();
        _lastSourceState = SourceStatus.Text = "正在切换识别语言…";
        _reader.SetLanguage(language.Code);
        UpdateRecordingStatus();
    }
    private void UpdateRecordingStatus()
    {
        RecordStatus.Text = !_captions.Recording ? "已暂停" : _lastSourceState.StartsWith("已连接") ? "正在记录" : "等待字幕";
        StatusDot.Fill = (Brush)FindResource(_captions.Recording && _lastSourceState.StartsWith("已连接") ? "AccentBrush" : "MutedBrush");
    }
    private void RenderText()
    {
        if (RenderTranscript(_captions.DisplayText, translationOnly: false)) _dirty = true;
    }
    private bool RenderTranscript(string text, bool translationOnly)
    {
        var old = TranscriptBox.Text;
        if (text == old) return false;
        var selectionStart = TranscriptBox.SelectionStart;
        var selectionLength = TranscriptBox.SelectionLength;
        var offset = TranscriptBox.VerticalOffset;
        _preservedTextOffset = offset;
        var prefix = CaptionAccumulator.CommonPrefix(old, text);
        _changingText = true;
        TranscriptBox.BeginChange();
        try
        {
            if (prefix == old.Length)
            {
                // Append without moving the editing selection/caret. Selecting
                // the suffix first makes a focused TextBox scroll to its caret.
                TranscriptBox.AppendText(text[prefix..]);
            }
            else
            {
                // A translation may arrive for an older line. Leave the suffix and its selection intact.
                var suffix = translationOnly ? CommonSuffix(old, text, prefix) : 0;
                var oldEnd = old.Length - suffix;
                var newEnd = text.Length - suffix;
                TranscriptBox.Select(prefix, oldEnd - prefix);
                TranscriptBox.SelectedText = text[prefix..newEnd];
                if (translationOnly)
                {
                    int MapSelection(int position) => position >= oldEnd ? position + newEnd - oldEnd
                        : position <= prefix ? position : Math.Min(position, newEnd);
                    var start = MapSelection(selectionStart);
                    TranscriptBox.Select(start, MapSelection(selectionStart + selectionLength) - start);
                }
                else TranscriptBox.Select(Math.Min(selectionStart, text.Length), Math.Min(selectionLength, Math.Max(0, text.Length - selectionStart)));
            }
        }
        finally { TranscriptBox.EndChange(); }
        UpdateEmptyState();
        CountLabel.Text = $"{text.Length:N0} 字";
        if (!translationOnly && !_following) _unreadUpdates++;
        // Wait for final line layout. Caret-induced scrolls are suppressed before
        // painting, and we move only if wrapping changed the bottom position.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_closed) return;
            var target = _following ? Math.Max(0, TranscriptBox.ExtentHeight - TranscriptBox.ViewportHeight) : offset;
            if (Math.Abs(TranscriptBox.VerticalOffset - target) > 0.5)
                TranscriptBox.ScrollToVerticalOffset(target);
            _changingText = false;
            UpdateFollowStatus();
        });
        return true;
    }
    private static int CommonSuffix(string old, string text, int prefix)
    {
        var suffix = 0;
        var limit = Math.Min(old.Length, text.Length) - prefix;
        while (suffix < limit && old[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;
        return suffix > 0 && char.IsLowSurrogate(old[old.Length - suffix]) ? suffix - 1 : suffix;
    }
    private void KeepTranscriptViewport(object sender, RequestBringIntoViewEventArgs e)
    {
        if (!_changingText) return;
        // WPF's caret moves the text view's IScrollInfo directly, then bubbles
        // BringIntoView. Restore that same scroll info in this turn, before a
        // frame can display the temporary caret location. Do not wait for idle.
        var scroll = FindTextScrollInfo(TranscriptBox);
        if (scroll is null) return;
        var target = _following ? Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight) : _preservedTextOffset;
        scroll.SetVerticalOffset(target);
        e.Handled = true;
    }
    private static IScrollInfo? FindTextScrollInfo(DependencyObject element)
    {
        // Prefer the text view over its outer ScrollContentPresenter.
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindTextScrollInfo(VisualTreeHelper.GetChild(element, i)) is { } found) return found;
        return element is IScrollInfo { ScrollOwner: not null } scroll ? scroll : null;
    }
    private void TranscriptScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_changingText || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.VerticalChange == 0) return;
        _following = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 8;
        if (_following) _unreadUpdates = 0;
        UpdateFollowStatus();
    }
    private void TranscriptWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0) { _following = false; UpdateFollowStatus(); }
    }
    private void UpdateFollowStatus()
    {
        if (FollowStatus is null) return;
        FollowStatus.Text = !_captions.Recording ? "记录已暂停，仍可滚动回看" : _following ? "跟随最新字幕" : _unreadUpdates > 0 ? "正在回看 · 有新字幕" : "正在回看，新的字幕继续记录";
        LatestButton.Content = "回到最新";
    }
    private void LatestClick(object sender, RoutedEventArgs e)
    {
        _following = true;
        _unreadUpdates = 0;
        TranscriptBox.ScrollToEnd();
        UpdateFollowStatus();
    }
    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (_captions.Recording)
        {
            DiscardUnfinishedTranslation();
            _captions.Pause();
            _reader.Pause();
            PauseButton.Content = "继续记录";
            Notice.Text = "已暂停保存新字幕，已有记录仍可滚动查看。";
        }
        else
        {
            _captions.Resume();
            _reader.Resume();
            PauseButton.Content = "暂停记录";
            Notice.Text = "已恢复，暂停期间的内容不会补录。";
        }
        RenderText();
        UpdateRecordingStatus();
        UpdateFollowStatus();
    }
    private void FontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptBox is null || FontPicker.SelectedItem is not ComboBoxItem item) return;
        TranscriptBox.FontSize = double.Parse(item.Content.ToString()!);
        if (_following) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TranscriptBox.ScrollToEnd());
    }
    private void TopmostChanged(object sender, RoutedEventArgs e) => Topmost = (sender as CheckBox)?.IsChecked == true;
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranscriptBox.Text)) { Notice.Text = "还没有可复制的字幕。"; return; }
        try { Clipboard.SetText(TranscriptBox.Text); Notice.Text = "已复制全部字幕。"; }
        catch (System.Runtime.InteropServices.COMException) { Notice.Text = "剪贴板正被占用，请重试。"; }
    }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranscriptBox.Text)) { Notice.Text = "还没有可导出的字幕。"; return; }
        var dialog = new SaveFileDialog { FileName = $"字幕记录-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "文本文件 (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await ExportTranscriptAsync(dialog.FileName); Notice.Text = "字幕已导出。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice.Text = "无法写入所选文件，请选择其他位置。"; }
    }
    private Task ExportTranscriptAsync(string path) => File.WriteAllTextAsync(path, TranscriptBox.Text, new UTF8Encoding(true));
    private void ReconnectClick(object sender, RoutedEventArgs e)
    {
        DiscardUnfinishedTranslation();
        _captions.SourceClosed();
        RenderText();
        _lastSourceState = SourceStatus.Text = "正在重新连接本机识别引擎…";
        _reader.Restart();
        UpdateRecordingStatus();
    }
    private void OpenHistoryClick(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_historyDirectory); Process.Start(new ProcessStartInfo(_historyDirectory) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { Notice.Text = "无法打开目录：" + _historyDirectory; }
    }
    private Task SaveAsync()
    {
        if (!_saveTask.IsCompleted) return _saveTask;
        if (!_dirty) return Task.CompletedTask;
        var text = _captions.Text;
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        _dirty = false;
        return _saveTask = SaveSnapshotAsync(text);
    }
    private async Task SaveSnapshotAsync(string text)
    {
        try
        {
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(_historyDirectory);
                await File.WriteAllTextAsync(_sessionPath + ".tmp", text, new UTF8Encoding(true));
                File.Move(_sessionPath + ".tmp", _sessionPath, true);
            });
            SaveStatus.Text = $"已自动保存 {DateTime.Now:HH:mm:ss} · 仅本机";
            SaveStatus.ToolTip = _sessionPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
            SaveStatus.Text = "自动保存失败，请使用“导出文本”保留记录。";
            SaveStatus.ToolTip = ex.Message;
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterSave) return;
        e.Cancel = true;
        if (_closed) return;
        _closed = true;
        StopTranslation();
        _saveTimer.Stop();
        IsEnabled = false;
        _captions.Pause();
        Notice.Text = "正在保存并关闭…";
        // Begin stopping audio now; window closure waits only for durable text.
        _ = _reader.PauseAsync();
        await _saveTask;
        await SaveAsync();
        if (_dirty && !string.IsNullOrWhiteSpace(_captions.Text))
        {
            _closed = false;
            IsEnabled = true;
            if (_compactMode) SetCompactMode(false);
            PauseButton.Content = "继续记录";
            _lastSourceState = SourceStatus.Text = "已暂停，记录尚未保存";
            UpdateRecordingStatus();
            UpdateFollowStatus();
            _saveTimer.Start();
            Notice.Text = "尚有未保存的字幕，请先导出再关闭窗口。";
            return;
        }
        _closeAfterSave = true;
        // Empty/already-saved sessions may not yield at either await above.
        // Close on the next dispatcher turn, outside the current Closing event.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ((App)Application.Current).FinishShutdown(_reader.DisposeAsync().AsTask());
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LatestClick(sender, e); e.Handled = true;
        }
    }
}
