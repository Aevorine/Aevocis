using System.Diagnostics;
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
using Velopack;
using Velopack.Sources;

namespace OpenSuperWhisper.App;

public partial class App : Application
{
    private const string GithubRepoUrl = "https://github.com/Aevorine/OpenSuperWhisper_Windows";

    private TaskbarIcon? _trayIcon;
    private ContextMenu? _trayMenu;
    private MenuItem? _downloadUpdateItem;
    private DictationController? _controller;
    private MainWindow? _mainWindow;
    private RecordingOverlayWindow? _overlayWindow;
    private DispatcherTimer? _overlayHideFallbackTimer;

    private SettingsStore? _settingsStore;
    private AppSettings? _settings;
    private GlobalPushToTalkHotkey? _pushToTalkHotkey;
    private ITranscriptionEngine? _engine;

    // F26/F27: real installer + one-click auto-update, via Velopack. CheckForUpdatesAsync/
    // DownloadUpdatesAsync are in-process SDK calls; only ApplyUpdatesAndRestart shells out to
    // the bundled Update.exe. _pendingUpdate holds what CheckForUpdatesAsync found so the tray
    // menu item click can download+apply it without checking again.
    private readonly UpdateManager _updateManager = new(new GithubSource(GithubRepoUrl, null, false));
    private UpdateInfo? _pendingUpdate;
    private bool _downloadingUpdate;

    // Readiness is tracked as two independent gates so a retry doesn't redundantly redo the
    // half that already succeeded (in particular, re-running WhisperFactory.FromPath needlessly
    // would leak the previous factory).
    private bool _engineReady;
    private bool _hotkeyReady;
    private bool IsReady => _engineReady && _hotkeyReady;

    /// <summary>
    /// Velopack needs to run its own bootstrap (applies a pending update, or handles
    /// install/uninstall hooks) before anything else in the process - including before WPF's own
    /// startup - so this app supplies its own Main instead of relying on WPF's auto-generated one
    /// (see &lt;StartupObject&gt; in the csproj). VelopackApp.Build().Run() is safe to call even
    /// when not running from a real Velopack install (e.g. `dotnet run` from bin/Debug during
    /// development) - it just no-ops in that case.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    public App()
    {
        // Process-wide safety net: without these, an exception on the UI thread or any other
        // thread that isn't explicitly caught takes the whole app down with zero trace. These
        // don't attempt to keep the app alive (that could leave it running in a broken state) -
        // they just make sure the reason gets logged before the process goes away.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private static readonly System.Diagnostics.Stopwatch StartupStopwatch = System.Diagnostics.Stopwatch.StartNew();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Log.Info($"进程启动，t={StartupStopwatch.ElapsedMilliseconds}ms");

        var settingsStore = new SettingsStore();
        var historyStore = new HistoryStore();
        var termsStore = new TermDictionaryStore();
        var settings = settingsStore.Load();
        _settingsStore = settingsStore;
        _settings = settings;

        if (string.IsNullOrWhiteSpace(settings.ModelPath) || !File.Exists(settings.ModelPath))
        {
            settings.ModelPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-small.bin");
            settingsStore.Save(settings);
        }

        // F29: shown once per install, non-modally so it never delays model loading/hotkey
        // registration below - the user can read it while the app finishes getting ready.
        if (!settings.HasSeenOnboarding)
        {
            new OnboardingWindow(settings, settingsStore).Show();
        }

        var purgedCount = historyStore.PurgeOlderThan(settings.HistoryRetentionDays);
        if (purgedCount > 0)
            Log.Info($"历史记录自动过期：清理了 {purgedCount} 条超过 {settings.HistoryRetentionDays} 天的记录");

        IAudioRecorder recorder = new MicRecorder();
        ITranscriptionEngine engine = new WhisperTranscriptionEngine();
        _engine = engine;
        ITextInjector injector = new UnicodeTextInjector();
        var pushToTalkHotkey = new GlobalPushToTalkHotkey(settings.PushToTalkVirtualKeyCode);
        pushToTalkHotkey.SetAppSpecificHotkeys(settings.AppSpecificHotkeys); // F12
        _pushToTalkHotkey = pushToTalkHotkey;
        IHotkeyListener hotkey = pushToTalkHotkey;

        _controller = new DictationController(recorder, engine, injector, hotkey, historyStore, settings, termsStore);
        _mainWindow = new MainWindow(historyStore, settings, ShowSettingsWindow);
        _overlayWindow = new RecordingOverlayWindow();

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "超语音 - 正在加载模型...",
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "app.ico"))
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
        var checkUpdateItem = new MenuItem { Header = "检查更新" };
        checkUpdateItem.Click += (_, _) => _ = CheckForUpdatesAsync(manualTrigger: true);
        var quitItem = new MenuItem { Header = "退出" };
        quitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(showItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(retryItem);
        menu.Items.Add(checkUpdateItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);
        _trayIcon.ContextMenu = menu;
        _trayMenu = menu;

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
        Log.Info($"就绪，t={StartupStopwatch.ElapsedMilliseconds}ms（进程启动到可用听写的总耗时）");

        // Fire-and-forget, best-effort update check - must never delay or block reaching "就绪"
        // above, and must never surface a failure to the user (see CheckForUpdatesAsync).
        _ = CheckForUpdatesAsync(manualTrigger: false);
    }

    /// <summary>
    /// Checks GitHub Releases (via Velopack's UpdateManager) for a newer version. Silent by
    /// design on any failure (network down, GitHub unreachable, or - very common during
    /// development - the app isn't a real Velopack install at all, which throws
    /// NotInstalledException every single time it's run via `dotnet run`/F5) - this app works
    /// fully offline, so a failed check is unremarkable and must not interrupt anything. When a
    /// newer version is found, adds (or refreshes) the "下载新版本" tray menu item and shows one
    /// balloon tip. <paramref name="manualTrigger"/> additionally shows a "已是最新版本" balloon
    /// when nothing newer was found, so a manual "检查更新" click from the tray menu isn't
    /// silently a no-op.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manualTrigger)
    {
        UpdateInfo? update;
        try
        {
            update = await _updateManager.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"更新检查失败：{ex.Message}");
            return;
        }

        _pendingUpdate = update;
        if (update != null)
        {
            Dispatcher.Invoke(() =>
            {
                EnsureDownloadUpdateMenuItem(update.TargetFullRelease.Version.ToString());
                _trayIcon?.ShowBalloonTip(
                    "超语音",
                    $"发现新版本 v{update.TargetFullRelease.Version}，点击托盘图标菜单里的「下载新版本」一键更新",
                    BalloonIcon.Info);
            });
        }
        else if (manualTrigger)
        {
            Dispatcher.Invoke(() => _trayIcon?.ShowBalloonTip("超语音", "已是最新版本", BalloonIcon.Info));
        }
    }

    /// <summary>
    /// Inserts the "下载新版本" tray menu item above "显示主界面" the first time a newer version
    /// is found, or just refreshes its label on a later check. Never left in the menu when no
    /// newer version has ever been found. Clicking it downloads the update in-process and then
    /// hands off to Velopack's bundled Update.exe to apply it and restart the app - the user
    /// never has to leave the tray menu or manually run an installer.
    /// </summary>
    private void EnsureDownloadUpdateMenuItem(string latestVersion)
    {
        if (_downloadUpdateItem != null)
        {
            _downloadUpdateItem.Header = $"下载新版本 v{latestVersion}";
            return;
        }

        var item = new MenuItem { Header = $"下载新版本 v{latestVersion}" };
        item.Click += (_, _) => _ = DownloadAndApplyUpdateAsync();
        _downloadUpdateItem = item;
        _trayMenu!.Items.Insert(0, item);
    }

    /// <summary>
    /// Downloads the pending update and restarts into it. Guarded against double-clicks
    /// (_downloadingUpdate) since a slow download makes it easy to click the menu item twice.
    /// Any failure (network drop mid-download, disk full, etc.) is shown to the user rather than
    /// swallowed - unlike the background CheckForUpdatesAsync, this is a deliberate user action,
    /// so a silent failure here would look like the click did nothing.
    /// </summary>
    private async Task DownloadAndApplyUpdateAsync()
    {
        if (_downloadingUpdate || _pendingUpdate is not { } update) return;
        _downloadingUpdate = true;
        try
        {
            _trayIcon?.ShowBalloonTip("超语音", "正在下载新版本...", BalloonIcon.Info);
            await _updateManager.DownloadUpdatesAsync(update);
            _trayIcon?.ShowBalloonTip("超语音", "下载完成，即将重启到新版本", BalloonIcon.Info);
            _updateManager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            Log.Error("下载或应用更新失败", ex);
            _trayIcon?.ShowBalloonTip("超语音", $"更新失败：{ex.Message}", BalloonIcon.Error);
        }
        finally
        {
            _downloadingUpdate = false;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("未处理的 UI 线程异常", e.Exception);
        CrashReporter.Write(e.Exception, "UI 线程未处理异常");
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error(
            e.IsTerminating ? "未处理的进程级异常（进程即将终止）" : "未处理的进程级异常",
            e.ExceptionObject as Exception);
        if (e.ExceptionObject is Exception ex)
            CrashReporter.Write(ex, e.IsTerminating ? "进程级未处理异常（即将终止）" : "进程级未处理异常");
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
        var window = new SettingsWindow(
            _settings!,
            _settingsStore!,
            vk => _pushToTalkHotkey!.SetVirtualKeyCode(vk),
            appHotkeys => _pushToTalkHotkey!.SetAppSpecificHotkeys(appHotkeys)); // F12
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
