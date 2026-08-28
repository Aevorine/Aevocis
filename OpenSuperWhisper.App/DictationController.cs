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
    private readonly TermDictionaryStore _terms;
    private readonly IDraftConfirmation _draftConfirmation;

    private bool _isRecording;
    private Task<bool>? _startTask;

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
        AppSettings settings,
        TermDictionaryStore terms,
        IDraftConfirmation draftConfirmation)
    {
        _recorder = recorder;
        _engine = engine;
        _injector = injector;
        _hotkey = hotkey;
        _history = history;
        _settings = settings;
        _terms = terms;
        _draftConfirmation = draftConfirmation;

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
        _startTask = Task.Run(() =>
        {
            try
            {
                _recorder.Start(_settings.MicrophoneDeviceId);
                RecordingStarted?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("启动录音失败", ex);
                _isRecording = false;
                RecordingFailed?.Invoke("麦克风不可用");
                return false;
            }
        });
    }

    private async void OnPressEnded()
    {
        if (!_isRecording) return;
        _isRecording = false;

        // Start() runs on its own background task (see OnPressStarted) so the hook thread never
        // blocks on it - but that means a quick press-release can reach here before Start() has
        // actually opened the mic. Without waiting for it first, Stop() below would race ahead,
        // find _waveIn still null, and silently return zero samples every time - recording
        // "worked" (the overlay still shows "正在听" once Start() eventually finishes) but
        // nothing was ever captured, and nothing gets typed. isFalse (started == false) means
        // Start() itself failed - RecordingFailed already handled telling the user that.
        var started = _startTask is not null && await _startTask;
        if (!started) return;

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

        // Peak amplitude of what was actually captured, logged every time (not just on
        // failure): "recording looked like it started, nothing was typed" is otherwise
        // indistinguishable between "the mic captured real speech but Whisper/injection failed"
        // and "the mic captured near-silence" (wrong/muted input device, volume too low) -
        // exactly the ambiguity that made this bug take three attempts to actually root-cause.
        var peak = PeakAmplitude(samples);
        Log.Info($"录音结束：{samples.Length} 个采样点（约 {samples.Length / 16000.0:F2} 秒），峰值音量 {peak:F3}（0~1，低于约 0.01 基本等于没录到声音）");

        // Shorter than ~0.1s: treat as an accidental tap, not a real utterance.
        if (samples.Length < 1600) return;

        try
        {
            var text = await _engine.TranscribeAsync(samples, _settings.Language);
            Log.Info($"识别结果：\"{text}\"");
            if (string.IsNullOrWhiteSpace(text) || IsNonSpeechMarker(text)) return;

            var corrections = _terms.Load();
            if (corrections.Count > 0)
            {
                var corrected = TermDictionary.Apply(text, corrections);
                if (corrected != text)
                    Log.Info($"专业词汇纠错：\"{text}\" -> \"{corrected}\"");
                text = corrected;
            }

            if (_settings.AutocorrectPunctuation)
            {
                var fixedText = PunctuationFixer.Apply(text);
                if (fixedText != text)
                    Log.Info($"标点自动补全：\"{text}\" -> \"{fixedText}\"");
                text = fixedText;
            }

            // F11: optional "show draft first" gate. Never blocks this thread - ConfirmAsync's
            // task only completes once the user acts (Enter/Esc) or dismisses the confirm window
            // another way; everything else (a new hotkey press, the tray icon, etc.) stays fully
            // responsive while it's pending. null means the user cancelled - nothing gets typed
            // or saved to history for this utterance.
            if (_settings.ShowDraftBeforeInject)
            {
                var confirmed = await _draftConfirmation.ConfirmAsync(text);
                if (confirmed is null)
                {
                    Log.Info("草稿确认已取消，未注入文本");
                    return;
                }
                text = confirmed;
            }

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

    /// <summary>Max absolute sample value, normalized to 0~1. A real spoken utterance at a
    /// normal distance from the mic typically peaks well above 0.05-0.1; anything under ~0.01
    /// across the whole recording is effectively silence - the wrong/muted input device, or the
    /// mic volume turned down, not something Whisper could ever have transcribed.</summary>
    private static float PeakAmplitude(float[] samples)
    {
        float peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak;
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
