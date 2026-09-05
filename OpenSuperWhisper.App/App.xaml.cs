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
    private VoiceCommandStore? _voiceCommandStore;
    private MacroStore? _macroStore;
    private GlobalPushToTalkHotkey? _pushToTalkHotkey;
    // v1.2.0 双引擎：DictationController 拿到的是这个切换器（ITranscriptionEngine），真实引擎
    // （闪电/SenseVoice 或 Whisper）由 SwitchEngineAsync 在里面热插拔，控制器无感知。
    private EngineSwitcher? _engine;
    private string _startupEngineInitPath = "";
    private readonly ModelDownloadService _modelDownloader = new();
    private string _modelsBaseDir = "";

    /// <summary>SenseVoice 识别模型目录（随安装包捆绑，Velopack 更新时随版本走）。</summary>
    private static string SenseVoiceModelDir => Path.Combine(AppContext.BaseDirectory, "Models", "sensevoice");

    /// <summary>ct-transformer 中英标点模型（随安装包捆绑）。文件缺失时 SenseVoice 引擎自动降级为不加标点。</summary>
    private static string PunctuationModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "punct", "model.int8.onnx");

    // F01: guards SwitchModelAsync against overlapping calls (e.g. mashing Save, or Save while a
    // previous switch's download is still in flight) - only one switch runs at a time.
    private readonly SemaphoreSlim _modelSwitchLock = new(1, 1);

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

        // F18: idle by default (including during model loading below - that's a one-time cost,
        // not the repeated "listening" cost this is meant to protect against) so the app stays
        // out of the way of whatever else is running (a game, a big build); RecordingStarted
        // above briefly raises this back for the few seconds a dictation is actually in flight.
        SetIdlePriority();

        var settingsStore = new SettingsStore();
        var historyStore = new HistoryStore();
        var termsStore = new TermDictionaryStore();
        var voiceCommandStore = new VoiceCommandStore();
        var macroStore = new MacroStore();
        var settings = settingsStore.Load();
        _settingsStore = settingsStore;
        _settings = settings;
        _voiceCommandStore = voiceCommandStore;
        _macroStore = macroStore;

        _modelsBaseDir = Path.Combine(AppContext.BaseDirectory, "Models");

        // v1.2.0 引擎装配：默认「闪电」（SenseVoice int8，随安装包捆绑）；选了 Whisper 但它的
        // 模型文件本地不存在（v1.2.0 起 Whisper 模型不再捆绑、全部按需下载，或用户删了缓存）时，
        // 本次会话回退到闪电并提示，而不是在托盘图标都还没出现时就悄悄开一个几百 MB 的下载——
        // RecognitionEngine 设置本身不动，用户去设置里重新保存即可触发下载。
        var startupEngineKey = settings.RecognitionEngine == "whisper" ? "whisper" : "sensevoice";
        string? whisperFallbackNotice = null;
        if (startupEngineKey == "whisper")
        {
            var startupModel = ModelCatalog.Resolve(settings.ModelSize);
            var startupModelPath = ModelCatalog.GetLocalPath(startupModel, _modelsBaseDir);
            if (File.Exists(startupModelPath))
            {
                _startupEngineInitPath = startupModelPath;
                if (settings.ModelPath != startupModelPath)
                {
                    settings.ModelPath = startupModelPath;
                    settingsStore.Save(settings);
                }
            }
            else
            {
                Log.Info($"Whisper 模型 {startupModel.Key} 本地文件不存在（{startupModelPath}），本次启动回退到闪电引擎，可在设置里重新选择 Whisper 以触发下载");
                whisperFallbackNotice = "Whisper 模型尚未下载，本次已改用闪电引擎；要用 Whisper 请到设置里重新保存一次";
                startupEngineKey = "sensevoice";
            }
        }
        ITranscriptionEngine initialEngine;
        if (startupEngineKey == "sensevoice")
        {
            initialEngine = new SenseVoiceTranscriptionEngine(PunctuationModelPath);
            _startupEngineInitPath = SenseVoiceModelDir;
        }
        else
        {
            initialEngine = new WhisperTranscriptionEngine();
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
        var engine = new EngineSwitcher(initialEngine);
        _engine = engine;
        ITextInjector injector = new UnicodeTextInjector();
        var pushToTalkHotkey = new GlobalPushToTalkHotkey(settings.PushToTalkVirtualKeyCode);
        pushToTalkHotkey.SetAppSpecificHotkeys(settings.AppSpecificHotkeys); // F12
        pushToTalkHotkey.SetMode(settings.PushToTalkMode); // F09: apply the saved Hold/Toggle choice
        _pushToTalkHotkey = pushToTalkHotkey;
        IHotkeyListener hotkey = pushToTalkHotkey;
        IDraftConfirmation draftConfirmation = new DraftConfirmationService(Dispatcher);

        _controller = new DictationController(recorder, engine, injector, hotkey, historyStore, settings, termsStore, draftConfirmation, voiceCommandStore, macroStore);
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
        // where TranscriptionCompleted is never raised. It's restarted (not just started once)
        // on every partial-transcript update too, so a long recognition doesn't hit the old
        // deadline while still visibly making progress - and, per F18, its firing is also the
        // safety net that guarantees process priority always comes back down even on those same
        // silent-failure paths (see SetIdlePriority below).
        _controller.RecordingStarted += () =>
        {
            // F18: raise process priority for the "actively working" window (recording through
            // transcription) so push-to-talk stays snappy even if something else (a game, a big
            // build) is hogging the CPU; SetIdlePriority below brings it back down the instant
            // that window ends. Process.PriorityClass isn't a UI call, so no Dispatcher needed.
            SetBusyPriority();
            Dispatcher.Invoke(() =>
            {
                _trayIcon.ToolTipText = "超语音 - 正在听...";
                _overlayHideFallbackTimer?.Stop();
                _overlayWindow!.ShowListening();
            });
        };
        _controller.RecordingStopped += () => Dispatcher.Invoke(() =>
        {
            _trayIcon.ToolTipText = "超语音 - 识别中...";
            _overlayWindow!.ShowTranscribing();
            RestartOverlayHideFallbackTimer();
        });
        _controller.PartialTranscriptionUpdated += partial => Dispatcher.Invoke(() =>
        {
            _overlayWindow!.UpdatePartialText(partial);
            RestartOverlayHideFallbackTimer();
        });
        _controller.TranscriptionCompleted += _ =>
        {
            SetIdlePriority();
            Dispatcher.Invoke(() =>
            {
                _trayIcon.ToolTipText = "超语音 - 就绪";
                _overlayHideFallbackTimer?.Stop();
                _overlayWindow!.HideOverlay();
                _mainWindow.RefreshHistory();
            });
        };
        _controller.RecordingFailed += reason =>
        {
            SetIdlePriority();
            Dispatcher.Invoke(() =>
            {
                _overlayHideFallbackTimer?.Stop();
                _overlayWindow!.HideOverlay();
                _trayIcon.ToolTipText = $"超语音 - {reason}";
                _trayIcon.ShowBalloonTip("超语音", reason, BalloonIcon.Warning);
            });
        };
        // F05/F13: a matched voice command or macro also ends the "正在处理" state shown by the
        // overlay/tray tooltip, exactly like a normal completed dictation - it's just not a
        // transcript, so it doesn't touch history. Also restores idle priority (F18), same as a
        // normal TranscriptionCompleted - a matched command/macro is a "we're done working" event
        // just like a completed transcription is.
        _controller.CommandExecuted += _ =>
        {
            SetIdlePriority();
            Dispatcher.Invoke(() =>
            {
                _trayIcon.ToolTipText = "超语音 - 就绪";
                _overlayHideFallbackTimer?.Stop();
                _overlayWindow!.HideOverlay();
            });
        };

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
        if (whisperFallbackNotice is not null)
            _trayIcon.ShowBalloonTip("超语音", whisperFallbackNotice, BalloonIcon.Info);

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
                await _engine!.InitializeAsync(_startupEngineInitPath);
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

    /// <summary>
    /// F18: raises the process priority for the "actively working" window - recording through
    /// transcription. BelowNormal (idle default) to Normal would already stop this process from
    /// dragging on foreground-heavy work like a game or a big build; AboveNormal is used instead
    /// so the few seconds where a real user is actively waiting on push-to-talk stay snappy even
    /// under contention, without going as far as the OS-level starvation risk of a Realtime/High
    /// class. Never throws: PriorityClass can fail if the OS denies the change (e.g. restrictive
    /// job object/sandbox), and a failed priority bump must never take dictation down with it.
    /// </summary>
    private static void SetBusyPriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch (Exception ex) { Log.Error("提升进程优先级失败（不影响听写本身）", ex); }
    }

    /// <summary>F18: the idle-default counterpart to SetBusyPriority - see there for why
    /// BelowNormal, not Normal, is idle here.</summary>
    private static void SetIdlePriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) { Log.Error("恢复进程优先级失败（不影响听写本身）", ex); }
    }

    /// <summary>
    /// (Re)arms the overlay's stuck-state recovery timer - see its call sites for why it needs to
    /// both start fresh after RecordingStopped and restart on every partial-transcript update.
    /// Also doubles as the F18 safety net: DictationController.OnPressEnded has a couple of
    /// silent-by-design early-return paths (e.g. a too-short tap, a blank-audio result, or - less
    /// by design - an exception while stopping the recorder or injecting text) where neither
    /// TranscriptionCompleted nor RecordingFailed ever fires, which would otherwise leave process
    /// priority stuck raised indefinitely; this timer already exists to recover the overlay from
    /// exactly those paths, so it lowers priority back down at the same time.
    /// </summary>
    private void RestartOverlayHideFallbackTimer()
    {
        _overlayHideFallbackTimer?.Stop();
        _overlayHideFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _overlayHideFallbackTimer.Tick += (_, _) =>
        {
            _overlayHideFallbackTimer!.Stop();
            _overlayWindow!.HideOverlay();
            SetIdlePriority();
        };
        _overlayHideFallbackTimer.Start();
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
            _voiceCommandStore!,
            _macroStore!,
            vk => _pushToTalkHotkey!.SetVirtualKeyCode(vk),
            appHotkeys => _pushToTalkHotkey!.SetAppSpecificHotkeys(appHotkeys), // F12
            mode => _pushToTalkHotkey!.SetMode(mode), // F09
            SwitchModelAsync, // F01
            SwitchEngineAsync); // v1.2.0 双引擎
        window.ShowDialog();
    }

    /// <summary>
    /// F01/F08: switches the running app to <paramref name="option"/> without a restart - downloads
    /// it first if it isn't cached locally yet (no-op if it already is), reinitializes the shared
    /// WhisperTranscriptionEngine in place (old model's memory is freed, not leaked - see
    /// WhisperTranscriptionEngine.InitializeAsync), and only then persists the choice to
    /// settings.json (so a failed/cancelled switch never leaves settings pointing at a model that
    /// isn't actually loaded). While in flight, DictationController.TranscriptionEngineReady is
    /// false so a press-to-talk started mid-switch is refused immediately rather than silently
    /// waiting on the engine's internal lock. Returns (success, errorMessage) instead of throwing -
    /// SettingsWindow shows errorMessage to the user and leaves the window open to retry.
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage)> SwitchModelAsync(
        ModelOption option, IProgress<ModelDownloadProgress>? progress)
    {
        await _modelSwitchLock.WaitAsync();
        try
        {
            // v1.2.0：模型选择只对 Whisper 引擎有意义。闪电引擎在用时改这个下拉，只记住偏好
            // （下次切到 Whisper 时才按它下载/加载），不动正在跑的引擎。
            if (_settings!.RecognitionEngine != "whisper")
            {
                if (_settings.ModelSize != option.Key)
                {
                    _settings.ModelSize = option.Key;
                    _settingsStore!.Save(_settings);
                    Log.Info($"Whisper 模型偏好已记录为 {option.Key}（当前引擎是闪电，切到 Whisper 时生效）");
                }
                return (true, null);
            }

            if (_settings.ModelSize == option.Key && File.Exists(_settings.ModelPath))
                return (true, null); // already the active model - nothing to do

            _controller!.TranscriptionEngineReady = false;
            try
            {
                var path = await _modelDownloader.EnsureLocalAsync(option, _modelsBaseDir, progress);
                await _engine!.InitializeAsync(path);

                _settings.ModelSize = option.Key;
                _settings.ModelPath = path;
                _settingsStore!.Save(_settings);
                Log.Info($"识别模型已切换为 {option.Key}（{path}），无需重启");
                return (true, null);
            }
            finally
            {
                _controller.TranscriptionEngineReady = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"切换识别模型到 {option.Key} 失败", ex);
            return (false, ex.Message);
        }
        finally
        {
            _modelSwitchLock.Release();
        }
    }

    /// <summary>
    /// v1.2.0：运行时切换识别引擎（"sensevoice" ⇄ "whisper"），不重启。切到 Whisper 时按当前
    /// ModelSize 偏好先确保模型在本地（没有就下载，进度回报给设置窗口）；新引擎完整初始化成功后
    /// 才通过 EngineSwitcher 热插拔并落盘设置——失败时旧引擎原样在用，听写不受影响。
    /// 与 SwitchModelAsync 共用一把锁：引擎切换和模型切换不允许并发。
    /// </summary>
    private async Task<(bool Success, string? ErrorMessage)> SwitchEngineAsync(
        string engineKey, IProgress<ModelDownloadProgress>? progress)
    {
        await _modelSwitchLock.WaitAsync();
        try
        {
            engineKey = engineKey == "whisper" ? "whisper" : "sensevoice";
            if (_settings!.RecognitionEngine == engineKey)
                return (true, null);

            _controller!.TranscriptionEngineReady = false;
            try
            {
                ITranscriptionEngine newEngine;
                string initPath;
                if (engineKey == "whisper")
                {
                    var option = ModelCatalog.Resolve(_settings.ModelSize);
                    initPath = await _modelDownloader.EnsureLocalAsync(option, _modelsBaseDir, progress);
                    newEngine = new WhisperTranscriptionEngine();
                }
                else
                {
                    initPath = SenseVoiceModelDir;
                    newEngine = new SenseVoiceTranscriptionEngine(PunctuationModelPath);
                }

                try
                {
                    await newEngine.InitializeAsync(initPath);
                }
                catch
                {
                    newEngine.Dispose();
                    throw;
                }

                await _engine!.SwapAsync(newEngine);
                _settings.RecognitionEngine = engineKey;
                if (engineKey == "whisper") _settings.ModelPath = initPath;
                _settingsStore!.Save(_settings);
                Log.Info($"识别引擎已切换为 {(engineKey == "whisper" ? "Whisper" : "闪电（SenseVoice）")}，无需重启");
                return (true, null);
            }
            finally
            {
                _controller.TranscriptionEngineReady = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"切换识别引擎到 {engineKey} 失败", ex);
            return (false, ex.Message);
        }
        finally
        {
            _modelSwitchLock.Release();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _overlayHideFallbackTimer?.Stop();
        _controller?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
