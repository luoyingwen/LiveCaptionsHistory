using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace LiveCaptionsHistory;

public partial class MainWindow
{
    private readonly DispatcherTimer _earlyTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _changingTranslation;
    private bool _earlyMode;
    private string? _activeRecognitionLanguage;
    private CaptionTranslationRun? _translation;
    private EarlyCaptionTranslation? _early;
    private int _pendingTranslations;
    private int _failedTranslations;

    private void TranslationChanged(object sender, RoutedEventArgs e)
    {
        if (_closed || _changingTranslation) return;
        var enable = sender == TranslationCheck ? TranslationCheck.IsChecked == true : MenuTranslation.IsChecked;
        if (!enable)
        {
            StopTranslation();
            return;
        }
        if (_translation is null && CaptionLlmClient.TryCreate() is { } client)
        {
            var run = new CaptionTranslationRun(client, TranslationCompleted);
            _translation = run;
            _early = new(_captions, run.TryQueue);
        }
        SetTranslationChecks();
        UpdateTranslationStatus();
        if (_translation is null)
            MessageBox.Show(this, $"未找到有效的翻译配置。请设置 CtrlLookup 的端点和模型后重试：\n{CaptionLlmClient.SettingsPath}",
                "字幕翻译未开启", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EarlyTranslationChanged(object sender, RoutedEventArgs e)
    {
        if (_closed || _changingTranslation || _translation is null) return;
        _earlyMode = sender == EarlyTranslationCheck ? EarlyTranslationCheck.IsChecked == true : MenuEarlyTranslation.IsChecked;
        SetTranslationChecks();
        if (_earlyMode) ObserveTranslation(changed: true, finalized: null);
        else
        {
            DiscardUnfinishedTranslation();
            RenderTranscript(_captions.DisplayText, translationOnly: true);
        }
        UpdateTranslationStatus();
    }

    private void ObserveTranslation(bool changed, FinalizedCaption? finalized)
    {
        if (_translation is null) return;
        if (_earlyMode && _early is { } early && CaptionTranslationRun.IsEnglish(_activeRecognitionLanguage))
        {
            var now = Environment.TickCount64;
            if (finalized is { } line) early.Observe(line, final: true, now);
            else if (changed && _captions.Recording && _captions.CurrentText.Length > 0)
                early.Observe(new(_captions.CurrentIndex, _captions.CurrentText), final: false, now);
            else return;
            RefreshEarlyTimer();
        }
        else if (_translation.TryQueue(finalized, _activeRecognitionLanguage)) _pendingTranslations++;
        else return;
        UpdateTranslationStatus();
    }

    private void RefreshEarlyTimer() => _earlyTimer.IsEnabled = _earlyMode && _early?.HasDebouncedWork == true;

    private void FlushEarlyTranslation()
    {
        _early?.Flush(Environment.TickCount64);
        RefreshEarlyTimer();
        UpdateTranslationStatus();
    }

    private void DiscardUnfinishedTranslation()
    {
        _early?.DiscardCurrent();
        RefreshEarlyTimer();
        UpdateTranslationStatus();
    }

    private void TranslationCompleted(CaptionTranslationRun run, CaptionTranslationRequest request, string? translation)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            // Cancellation alone does not reject a reply already queued on the dispatcher.
            if (_closed || !ReferenceEquals(_translation, run)) return;
            if (request.Revision == 0)
            {
                _pendingTranslations--;
                if (translation is null) _failedTranslations++;
                else if (_captions.ApplyTranslation(request.Caption, translation)) RenderTranscript(_captions.DisplayText, translationOnly: true);
            }
            else if (_early?.Complete(request, translation) == true) RenderTranscript(_captions.DisplayText, translationOnly: true);
            UpdateTranslationStatus();
        });
    }

    private void StopTranslation()
    {
        var run = _translation;
        _translation = null;
        _early?.Stop();
        _early = null;
        _earlyMode = false;
        _pendingTranslations = _failedTranslations = 0;
        RefreshEarlyTimer();
        SetTranslationChecks();
        UpdateTranslationStatus();
        if (run is not null)
        {
            if (!_closed) RenderTranscript(_captions.DisplayText, translationOnly: true);
            _ = FinishTranslationAsync(run);
        }
    }

    private void UpdateTranslationStatus()
    {
        var visibility = _translation is null ? Visibility.Collapsed : Visibility.Visible;
        TranslationStatus.Visibility = MenuTranslationStatus.Visibility = visibility;
        if (visibility == Visibility.Collapsed) return;
        var pending = _pendingTranslations + (_early?.PendingCount ?? 0);
        var failed = _failedTranslations + (_early?.FailedCount ?? 0);
        var text = pending > 0 ? $"字幕翻译：{pending} 条等待译文" : _earlyMode
            ? "字幕翻译：已开启提前翻译，未定稿的译文可能修订" : "字幕翻译：英语字幕定稿后翻译";
        if (pending > 0 && _earlyMode) text += " · 提前翻译";
        if (failed > 0) text += $" · {failed} 条失败，保留英语";
        TranslationStatus.Text = text;
        MenuTranslationStatus.Header = text;
    }

    private void SetTranslationChecks()
    {
        _changingTranslation = true;
        try
        {
            TranslationCheck.IsChecked = MenuTranslation.IsChecked = EarlyTranslationCheck.IsEnabled = MenuEarlyTranslation.IsEnabled = _translation is not null;
            EarlyTranslationCheck.IsChecked = MenuEarlyTranslation.IsChecked = _earlyMode;
        }
        finally { _changingTranslation = false; }
    }

    private static async Task FinishTranslationAsync(CaptionTranslationRun run)
    {
        try { await run.StopAsync().ConfigureAwait(false); }
        catch (Exception) { Trace.TraceWarning("Could not cleanly shut down caption translation."); }
    }
}
