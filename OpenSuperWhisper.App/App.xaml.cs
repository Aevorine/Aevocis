using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using OpenSuperWhisper.Audio;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Hotkeys;
using OpenSuperWhisper.Recognition;
using OpenSuperWhisper.Storage;
using OpenSuperWhisper.TextInjection;

namespace OpenSuperWhisper.App;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private DictationController? _controller;
    private MainWindow? _mainWindow;
    private RecordingOverlayWindow? _overlayWindow;
    private DispatcherTimer? _overlayHideFallbackTimer;

    private SettingsStore? _settingsStore;
    private AppSettings? _settings;
    private GlobalPushToTalkHotkey? _pushToTalkHotkey;
    private ITranscriptionEngine? _engine;

    // Readiness is tracked as two independent gates so a retry doesn't redundantly redo the
    // half that already succeeded (in particular, re-running WhisperFactory.FromPath needlessly
    // would leak the previous factory).
    private bool _engineReady;
    private bool _hotkeyReady;
    private bool IsReady => _engineReady && _hotkeyReady;

    public App()
    {
        // Process-wide safety net: without these, an exception on the UI thread or any other
        // thread that isn't explicitly caught takes the whole app down with zero trace. These
        // don't attempt to keep the app alive (that could leave it running in a broken state) -
        // they just make sure the reason gets logged before the process goes away.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settingsStore = new SettingsStore();
        var historyStore = new HistoryStore();
        var settings = settingsStore.Load();
        _settingsStore = settingsStore;
        _settings = settings;

        if (string.IsNullOrWhiteSpace(settings.ModelPath) || !File.Exists(settings.ModelPath))
        {
            settings.ModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-tiny.en.bin");
            settingsStore.Save(settings);
        }

        IAudioRecorder recorder = new MicRecorder();
        ITranscriptionEngine engine = new WhisperTranscriptionEngine();
        _engine = engine;
        ITextInjector injector = new UnicodeTextInjector();
        var pushToTalkHotkey = new GlobalPushToTalkHotkey(settings.PushToTalkVirtualKeyCode);
        _pushToTalkHotkey = pushToTalkHotkey;
        IHotkeyListener hotkey = pushToTalkHotkey;

        _controller = new DictationController(recorder, engine, injector, hotkey, historyStore, settings);
        _mainWindow = new MainWindow(historyStore, settings, ShowSettingsWindow);
        _overlayWindow = new RecordingOverlayWindow();

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "超语音 - 正在加载模型...",
            Icon = System.Drawing.SystemIcons.Application
        };
        _trayIcon.TrayLeftMouseUp += (_, _) =>
        {
            if (!IsReady) { _ = RetryInitializationAsync(isFirstAttempt: false); return; }
            ToggleMainWindow();
        };

        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "显示主界面" };
        showItem.Click += (_, _) => ShowMainWindow();
        var settingsItem = new MenuItem { Header = "设置..." };
        settingsItem.Click += (_, _) => ShowSettingsWindow();
        var retryItem = new MenuItem { Header = "重试初始化" };
        retryItem.Click += (_, _) => _ = RetryInitializationAsync(isFirstAttempt: false);
        var quitItem = new MenuItem { Header = "退出" };
        quitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(retryItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);
        _trayIcon.ContextMenu = menu;

        // Recording overlay: shows while listening, switches text while recognizing, and
        // disappears once the transcript lands. A fallback timer guards against the overlay
        // getting stuck forever on the (silent, by design) early-return paths in
        // DictationController.OnPressEnded - e.g. a too-short tap or a blank-audio result -
        // where TranscriptionCompleted is never raised.
        _controller.RecordingStarted += () => Dispatcher.Invoke(() =>
        {
            _trayIcon.ToolTipText = "超语音 - 正在听...";
            _overlayHideFallbackTimer?.Stop();
            _overlayWindow!.ShowListening();
        });
        _controller.RecordingStopped += () => Dispatcher.Invoke(() =>
        {
            _trayIcon.ToolTipText = "超语音 - 识别中...";
            _overlayWindow!.ShowTranscribing();
            _overlayHideFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _overlayHideFallbackTimer.Tick += (_, _) =>
            {
                _overlayHideFallbackTimer!.Stop();
                _overlayWindow!.HideOverlay();
            };
            _overlayHideFallbackTimer.Start();
        });
        _controller.TranscriptionCompleted += _ =>
        {
            Dispatcher.Invoke(() =>
            {
                _trayIcon.ToolTipText = "超语音 - 就绪";
                _overlayHideFallbackTimer?.Stop();
                _overlayWindow!.HideOverlay();
                _mainWindow.RefreshHistory();
            });
        };
        _controller.RecordingFailed += reason => Dispatcher.Invoke(() =>
        {
            _overlayHideFallbackTimer?.Stop();
            _overlayWindow!.HideOverlay();
            _trayIcon.ToolTipText = $"超语音 - {reason}";
            _trayIcon.ShowBalloonTip("超语音", reason, BalloonIcon.Warning);
        });

        // One-time notices for anything Load() had to silently paper over before the tray icon
        // existed to tell the user about it.
        if (settingsStore.LastLoadWasReset)
            _trayIcon.ShowBalloonTip("超语音", "设置文件已损坏，已重置为默认值", BalloonIcon.Warning);
        if (settingsStore.IsDegraded)
            _trayIcon.ShowBalloonTip("超语音", "设置文件暂时无法读取（可能被占用），本次使用临时默认设置，不会覆盖原文件", BalloonIcon.Warning);
        if (historyStore.LastLoadWasReset)
            _trayIcon.ShowBalloonTip("超语音", "历史记录文件已损坏，已重置为空", BalloonIcon.Warning);
        if (historyStore.IsDegraded)
            _trayIcon.ShowBalloonTip("超语音", "历史记录文件暂时无法读取（可能被占用），本次以空历史运行，不会覆盖原文件", BalloonIcon.Warning);

        await RetryInitializationAsync(isFirstAttempt: true);
    }

    /// <summary>
    /// Brings the app from "not armed" to "armed" - model loaded AND hotkey registered. Safe to
    /// call repeatedly (from the tray icon click, the tray menu's "重试初始化", or the initial
    /// startup call): each gate only does its (possibly slow/leaky-if-repeated) work once, and
    /// no-ops once both gates are satisfied. The critical invariant this restores versus the
    /// original code: the hotkey is never started (the app never looks "就绪") unless the model
    /// actually loaded, so a failed dictation attempt can't silently hang forever.
    /// </summary>
    private async Task RetryInitializationAsync(bool isFirstAttempt)
    {
        if (IsReady) return;
        _trayIcon!.ToolTipText = "超语音 - 正在初始化...";

        if (!_engineReady)
        {
            try
            {
                await _engine!.InitializeAsync(_settings!.ModelPath);
                _engineReady = true;
            }
            catch (Exception ex)
            {
                Log.Error("语音识别模型初始化失败", ex);
                _trayIcon.ToolTipText = "超语音 - 未就绪（模型加载失败），点击托盘图标重试";
                if (isFirstAttempt)
                    MessageBox.Show($"语音识别模型加载失败：{ex.Message}\n\n点击托盘图标可重试。", "超语音", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    _trayIcon.ShowBalloonTip("超语音", "模型加载仍然失败，点击托盘图标可再次重试", BalloonIcon.Warning);
                return;
            }
        }

        if (!_hotkeyReady)
        {
            _hotkeyReady = _controller!.Start();
            if (!_hotkeyReady)
            {
                Log.Error($"全局热键注册失败，Win32 错误码 {_pushToTalkHotkey!.LastWin32Error}");
                _trayIcon.ToolTipText = "超语音 - 未就绪（热键注册失败），点击托盘图标重试";
                if (isFirstAttempt)
                    MessageBox.Show("全局热键注册失败（可能与其他程序冲突）。点击托盘图标可重试。", "超语音", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    _trayIcon.ShowBalloonTip("超语音", "热键注册仍然失败，点击托盘图标可再次重试", BalloonIcon.Warning);
                return;
            }
        }

        _trayIcon.ToolTipText = "超语音 - 就绪";
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("未处理的 UI 线程异常", e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error(
            e.IsTerminating ? "未处理的进程级异常（进程即将终止）" : "未处理的进程级异常",
            e.ExceptionObject as Exception);
    }

    private void ToggleMainWindow()
    {
        if (_mainWindow!.IsVisible) _mainWindow.Hide();
        else ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow!.RefreshHistory();
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private void ShowSettingsWindow()
    {
        var window = new SettingsWindow(_settings!, _settingsStore!, vk => _pushToTalkHotkey!.SetVirtualKeyCode(vk));
        window.ShowDialog();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _overlayHideFallbackTimer?.Stop();
        _controller?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
