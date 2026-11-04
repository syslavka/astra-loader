using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using NLCleaner.Services;

namespace NLCleaner;

public partial class MainWindow : Window
{
    private readonly bool _dryRun, _manual, _restore;
    private readonly string? _capture;
    private readonly ObservableCollection<Activity> _activity = [];
    private readonly List<Activity> _allActivity = [];
    private CancellationTokenSource? _cancellation;
    private bool _running, _closeRequested;
    private int _phasesDone;
    private readonly bool _animationTest;
    private readonly SemaphoreSlim _animationGate = new(1, 1);
    private bool _minimizing, _closingAnimation, _allowClose;
    private WindowState _previousWindowState = WindowState.Normal;
    private bool _motionEnabled = true;

    public MainWindow(bool dryRun, bool manual, bool restore, string? capture, bool animationTest = false)
    {
        InitializeComponent();
        _dryRun = dryRun; _manual = manual; _restore = restore; _capture = capture;
        _animationTest = animationTest;
        ActivityList.ItemsSource = _activity;
        AccountName.Text = Environment.UserName;
        if (!Elevation.IsAdministrator) ModeText.Text = "ОБЫЧНЫЙ ДОСТУП";
        if (dryRun) { ModeText.Text = "БЕЗ ИЗМЕНЕНИЙ"; StatusBadge.Text = "● ПРОВЕРКА"; }
        RefreshPreferences();
        OperationText.Text = manual ? "Очистка начнётся после нажатия кнопки" : restore ? "Восстановление доступа…" : dryRun ? "Проверка без изменений системы…" : "Автоматическая очистка (--run)…";
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        await AnimateSurface(true);
        if (_animationTest)
        {
            await MinimizeAnimated();
            await Task.Delay(100);
            WindowState = WindowState.Normal;
            await _animationGate.WaitAsync();
            _animationGate.Release();
            if (WindowState != WindowState.Normal || RootSurface.Opacity < 0.99 || !RootSurface.IsHitTestVisible)
                throw new InvalidOperationException("Window animation did not restore the surface.");
            var initialWidth = Width;
            var initialHeight = Height;
            WindowState = WindowState.Maximized;
            await Task.Delay(80);
            WindowState = WindowState.Normal;
            Width = MinWidth; Height = MinHeight;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (NukeButton.ActualWidth < 300 || LogPanel.ActualHeight < 120)
                throw new InvalidOperationException("Compact layout does not fit its controls.");
            Width = initialWidth; Height = initialHeight;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "INFO", "Animation, maximize/restore and compact layout checks passed."));
        }
        if (!_manual) await Run(_restore);
        if (_capture is not null)
        {
            // Explicit preview-only capture, used by the source QA workflow.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(_capture)) encoder.Save(stream);
            Close();
        }
    }

    private async Task Run(bool restoring, bool preview = false)
    {
        if (_running) return;
        var dryRun = _dryRun || preview;
        if (!dryRun && !Elevation.IsAdministrator)
        {
            try
            {
                if (Elevation.StartOperation(restoring)) Close();
                else AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "INFO", "Запрос прав отменён. Изменений нет."));
            }
            catch (Exception ex) { AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "ERROR", ex.Message)); }
            return;
        }
        _running = true;
        _cancellation = new CancellationTokenSource();
        NukeButton.IsEnabled = RestoreButton.IsEnabled = PreviewButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusBadge.Text = dryRun ? "● ПРОВЕРКА" : "● ВЫПОЛНЯЕТСЯ";
        OperationText.Text = restoring ? "Восстановление доступа…" : dryRun ? "Проверка плана без изменений системы…" : "Удаление Roblox и применение блокировок…";
        Progress.Value = 0; _phasesDone = 0;
        RemovalState.Text = FolderState.Text = NetworkState.Text = "Ожидание";
        try
        {
            var engine = new CleanerEngine(dryRun, entry => Dispatcher.BeginInvoke(() => AddActivity(entry)),
                (index, text) => Dispatcher.BeginInvoke(() =>
                {
                    var label = index switch { 0 => RemovalState, 1 => FolderState, _ => NetworkState };
                    label.Text = text switch { "Working" => "Выполняется", "Previewed" => "Проверено", "Review log" => "Есть ошибки", "Removed" => "Удалено", "Locked" => "Заблокировано", "Applied" => "Применено", "Restored" => "Восстановлено", "Not required" => "Не требуется", _ => text };
                    label.Foreground = (Brush)new BrushConverter().ConvertFromString(text == "Review log" ? "#E6B578" : "#A4CCDF")!;
                    if (text != "Working") Progress.Value = ++_phasesDone;
                }));
            var result = await Task.Run(() => engine.Run(restoring, _cancellation.Token));
            StatusBadge.Text = result.Cancelled ? "● ОСТАНОВЛЕНО" : result.Errors > 0 ? "● ЕСТЬ ОШИБКИ" : dryRun ? "● ПРОВЕРЕНО" : "● ЗАВЕРШЕНО";
            OperationText.Text = result.Cancelled ? "Остановлено · применённые блокировки можно снять" : result.Errors > 0 ? $"Ошибок: {result.Errors} · см. журнал" : dryRun ? "Проверка завершена · система не изменена" : restoring ? "Доступ восстановлен" : "Удаление и блокировка завершены";
        }
        catch (Exception ex)
        {
            AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "ERROR", ex.Message));
            StatusBadge.Text = "● ЕСТЬ ОШИБКИ"; OperationText.Text = "Операция не завершена · см. журнал";
        }
        finally
        {
            _running = false;
            _cancellation.Dispose(); _cancellation = null;
            NukeButton.IsEnabled = RestoreButton.IsEnabled = PreviewButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            if (_closeRequested) Close();
        }
    }

    private void AddActivity(Activity entry)
    {
        _allActivity.Add(entry);
        _activity.Add(entry);
        if (_activity.Count > 1500) _activity.RemoveAt(0);
        LogCount.Text = $"{_allActivity.Count} событий";
        ActivityList.ScrollIntoView(entry);
    }

    private async void NukeClick(object sender, RoutedEventArgs e) => await Run(false);
    private void AboutClick(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        $"Astra Loader {typeof(App).Assembly.GetName().Version?.ToString(3)}\n\nУдаление Roblox и управление блокировками установки.\n\nПапка приложения:\n{AppContext.BaseDirectory}\n\nДанные восстановления:\n{Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}\\NL-Cleaner\\<SID>\n\nПриложение работает локально; удалённые файлы не восстанавливаются.",
        "О программе Astra Loader", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!GlassBackdrop.TryEnable(this)) Background = new SolidColorBrush(Color.FromRgb(23, 28, 45));
    }
    private void OnGlassMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_motionEnabled) return;
        if (RootSurface.ActualWidth <= 0 || RootSurface.ActualHeight <= 0) return;
        var location = e.GetPosition(RootSurface);
        var point = new Point(Math.Clamp(location.X / RootSurface.ActualWidth, 0, 1), Math.Clamp(location.Y / RootSurface.ActualHeight, 0, 1));
        LiquidLight.Center = LiquidLight.GradientOrigin = point;
    }
    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (!_closingAnimation) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private async void PreviewClick(object sender, RoutedEventArgs e) => await Run(false, true);
    private void CleanupNavigationClick(object sender, RoutedEventArgs e) => NukeButton.Focus();
    private void LogNavigationClick(object sender, RoutedEventArgs e) { LogPanel.BringIntoView(); ActivityList.Focus(); }
    private async void RestoreClick(object sender, RoutedEventArgs e) => await Run(true);
    private void StopClick(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); StopButton.IsEnabled = false; }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private async void MinimizeClick(object sender, RoutedEventArgs e) => await MinimizeAnimated();
    private async Task MinimizeAnimated()
    {
        if (_minimizing || _closingAnimation) return;
        _minimizing = true;
        try { await AnimateSurface(false); WindowState = WindowState.Minimized; }
        finally { _minimizing = false; }
    }

    private async void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var previous = _previousWindowState;
        _previousWindowState = WindowState;
        if (WindowState == WindowState.Minimized)
        {
            // Also cover native minimization through the taskbar or keyboard.
            RootSurface.BeginAnimation(OpacityProperty, null);
            RootSurface.Opacity = 0;
            return;
        }
        if (previous == WindowState.Minimized && !_closingAnimation) await AnimateSurface(true);
    }

    private async Task AnimateSurface(bool visible)
    {
        await _animationGate.WaitAsync();
        try
        {
            RootSurface.IsHitTestVisible = false;
            var duration = _motionEnabled && SystemParameters.ClientAreaAnimation ? visible ? 260 : 150 : 0;
            var easing = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn };
            void Animate(Animatable target, DependencyProperty property, double value)
            {
                var from = (double)target.GetValue(property);
                target.BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
            }
            // UIElement is not Animatable; its opacity uses the same animation clock shape.
            RootSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(RootSurface.Opacity, visible ? 1 : 0,
                TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
            Animate(SurfaceScale, ScaleTransform.ScaleXProperty, visible ? 1 : 0.96);
            Animate(SurfaceScale, ScaleTransform.ScaleYProperty, visible ? 1 : 0.96);
            Animate(SurfaceOffset, TranslateTransform.YProperty, visible ? 0 : 18);
            if (duration > 0) await Task.Delay(duration + 30);
            RootSurface.IsHitTestVisible = visible;
        }
        finally { _animationGate.Release(); }
    }

    private async Task FinishClosing()
    {
        await AnimateSurface(false);
        _allowClose = true;
        Close();
    }
    private void RefreshPreferences()
    {
        StartupToggle.IsEnabled = MotionToggle.IsEnabled = !_dryRun;
        try
        {
            StartupToggle.IsChecked = DesktopPreferences.StartupEnabled;
            StartupStatus.Text = StartupToggle.IsChecked == true ? "Меню при входе · без удаления" : "Выключен";
            _motionEnabled = DesktopPreferences.MotionEnabled;
            MotionToggle.IsChecked = _motionEnabled;
        }
        catch (Exception ex)
        {
            StartupStatus.Text = "Нет доступа к настройкам";
            StartupToggle.IsEnabled = false;
            AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "WARN", $"Настройки недоступны: {ex.Message}"));
        }
    }
    private void StartupToggleClick(object sender, RoutedEventArgs e)
    {
        if (_dryRun) return;
        try
        {
            DesktopPreferences.SetStartup(StartupToggle.IsChecked == true);
            AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "INFO", StartupToggle.IsChecked == true ? "Автозапуск меню включён. Автоматической очистки нет." : "Автозапуск меню выключен."));
        }
        catch (Exception ex) { AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "ERROR", ex.Message)); }
        RefreshPreferences();
    }
    private void MotionToggleClick(object sender, RoutedEventArgs e)
    {
        if (_dryRun) return;
        try { DesktopPreferences.MotionEnabled = MotionToggle.IsChecked == true; }
        catch (Exception ex) { AddActivity(new Activity(DateTime.Now.ToString("HH:mm:ss"), "ERROR", ex.Message)); }
        RefreshPreferences();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (!_running)
        {
            if (!_closingAnimation) { _closingAnimation = true; _ = FinishClosing(); }
            return;
        }
        _closeRequested = true;
        _cancellation?.Cancel();
        OperationText.Text = "Завершение текущей операции перед закрытием…";
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Text log (*.log)|*.log", FileName = $"AstraLoader-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllLines(dialog.FileName, _allActivity.Select(a => a.ToString())); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "Export log"); }
    }
}
