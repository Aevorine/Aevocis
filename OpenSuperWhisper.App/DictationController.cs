using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Storage;

namespace OpenSuperWhisper.App;

/// <summary>Wires hotkey -> recorder -> transcription engine -> text injector -> history together.</summary>
public sealed class DictationController : IDisposable
{
    private readonly IAudioRecorder _recorder;
    private readonly ITranscriptionEngine _engine;
    private readonly ITextInjector _injector;
    private readonly IHotkeyListener _hotkey;
    private readonly HistoryStore _history;
    private readonly AppSettings _settings;

    private bool _isRecording;

    public event Action? RecordingStarted;
    public event Action? RecordingStopped;
    public event Action<string>? TranscriptionCompleted;

    /// <summary>Raised when starting or stopping the microphone itself fails (e.g. no
    /// microphone present, or it's exclusively held by another app).</summary>
    public event Action<string>? RecordingFailed;

    public DictationController(
        IAudioRecorder recorder,
        ITranscriptionEngine engine,
        ITextInjector injector,
        IHotkeyListener hotkey,
        HistoryStore history,
        AppSettings settings)
    {
        _recorder = recorder;
        _engine = engine;
        _injector = injector;
        _hotkey = hotkey;
        _history = history;
        _settings = settings;

        _hotkey.PressStarted += OnPressStarted;
        _hotkey.PressEnded += OnPressEnded;
    }

    /// <summary>Registers the global hotkey. Returns false if OS hook registration failed
    /// (see GlobalPushToTalkHotkey.LastWin32Error for why) - the caller must not treat the app
    /// as armed in that case.</summary>
    public bool Start() => _hotkey.Start();

    /// <summary>
    /// Runs on the WH_KEYBOARD_LL hook thread. Must return fast - Windows enforces a
    /// responsiveness watchdog on low-level hooks that can stall keyboard input system-wide if
    /// it doesn't - so the actual (not-guaranteed-fast) audio device open is dispatched to a
    /// background thread instead of happening inline here.
    /// </summary>
    private void OnPressStarted()
    {
        if (_isRecording) return;
        // Set the guard immediately (synchronously, on the hook thread), not just after Start()
        // succeeds: a second key-down that arrives while the background Start() is still in
        // flight must not race a second concurrent call into MicRecorder. It's reset back to
        // false below if Start() actually fails, so a bad mic doesn't permanently no-op every
        // future press.
        _isRecording = true;
        Task.Run(() =>
        {
            try
            {
                _recorder.Start();
                RecordingStarted?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("启动录音失败", ex);
                _isRecording = false;
                RecordingFailed?.Invoke("麦克风不可用");
            }
        });
    }

    private async void OnPressEnded()
    {
        if (!_isRecording) return;
        _isRecording = false;

        float[] samples;
        try
        {
            // Stop() is also dispatched off the hook thread for the same responsiveness reason
            // as Start() above (NAudio's StopRecording can briefly block).
            samples = await Task.Run(() => _recorder.Stop());
        }
        catch (Exception ex)
        {
            Log.Error("停止录音失败", ex);
            RecordingStopped?.Invoke();
            return;
        }
        RecordingStopped?.Invoke();

        // Shorter than ~0.1s: treat as an accidental tap, not a real utterance.
        if (samples.Length < 1600) return;

        try
        {
            var text = await _engine.TranscribeAsync(samples, _settings.Language);
            if (string.IsNullOrWhiteSpace(text) || IsNonSpeechMarker(text)) return;

            // InjectText throws if the text didn't actually land (SendInput reported fewer
            // events accepted than sent) - history is only saved / TranscriptionCompleted only
            // raised below this line if it really did.
            _injector.InjectText(text);
            _history.Add(new TranscriptionRecord { Timestamp = DateTimeOffset.Now, Text = text });
            TranscriptionCompleted?.Invoke(text);
        }
        catch (Exception ex)
        {
            // Swallowed as far as the caller is concerned - this runs off the low-level keyboard
            // hook thread's continuation, and an unhandled exception here has no safe place to
            // go - but it's now logged instead of fully discarded.
            Log.Error("听写处理失败（转写或文本注入阶段）", ex);
        }
    }

    /// <summary>
    /// Whisper reports silence/non-speech audio (dead air, background noise, music) as a
    /// bracketed tag instead of real words - e.g. "[BLANK_AUDIO]", "(silence)". A push-to-talk
    /// tap with nothing said produces exactly this, and it must never get typed into the user's
    /// focused app or saved into history as if it were a transcript.
    /// </summary>
    private static bool IsNonSpeechMarker(string text)
    {
        var t = text.Trim();
        return (t.StartsWith('[') && t.EndsWith(']')) ||
               (t.StartsWith('(') && t.EndsWith(')'));
    }

    public void Dispose()
    {
        _hotkey.PressStarted -= OnPressStarted;
        _hotkey.PressEnded -= OnPressEnded;
        _hotkey.Dispose();
        _recorder.Dispose();
        _engine.Dispose();
    }
}
