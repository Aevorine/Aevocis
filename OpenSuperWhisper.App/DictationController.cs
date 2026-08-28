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
    private readonly VoiceCommandStore _voiceCommands;
    private readonly MacroStore _macros;
    private readonly MacroExecutor _macroExecutor;

    private bool _isRecording;
    private Task<bool>? _startTask;

    /// <summary>Length (in characters of the string handed to InjectText, before SendInput-level
    /// sanitization - a very close approximation, not a byte-exact guarantee) of the last text
    /// this app itself successfully injected, or 1 if the last thing it did was send Enter
    /// (Backspace merges lines back together in effectively every text editor, so it's undoable
    /// the same way). Reset to 0 whenever something not undoable this way happens (a macro ran,
    /// or a "删除这段" cancellation already consumed it). Used only by F05's CancelDictation
    /// command to best-effort undo the app's own last action - see its doc comment on
    /// VoiceCommandAction for why this can never reach into arbitrary pre-existing text in
    /// another app.</summary>
    private int _lastInjectedLength;

    public event Action? RecordingStarted;
    public event Action? RecordingStopped;
    public event Action<string>? TranscriptionCompleted;

    /// <summary>Raised when a F05 voice command or F13 macro was matched and run instead of a
    /// normal dictation - lets the UI (recording overlay, tray tooltip) know the "正在处理" state
    /// is over, without treating it as a transcript to save into history.</summary>
    public event Action<string>? CommandExecuted;

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
        VoiceCommandStore voiceCommands,
        MacroStore macros)
    {
        _recorder = recorder;
        _engine = engine;
        _injector = injector;
        _hotkey = hotkey;
        _history = history;
        _settings = settings;
        _terms = terms;
        _voiceCommands = voiceCommands;
        _macros = macros;
        _macroExecutor = new MacroExecutor(injector);

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

            // F05: 口头命令优先于普通听写处理——命中即执行对应动作，不再走术语纠错/标点补全/常规
            // 注入这条链（UppercaseSuffix 例外：它对"命令词前面那部分内容"复用同一条后处理链，
            // 见 HandleVoiceCommand）。在原始识别文本上匹配，不等术语纠错/标点补全先改动它，这样
            // 命令词不会被这两步意外改写而错过匹配。
            var commandMatch = VoiceCommandMatcher.Match(text, _voiceCommands.Load());
            if (commandMatch.Matched)
            {
                HandleVoiceCommand(commandMatch, text);
                return;
            }

            // F13: 语音宏——整句命中触发短语则依次执行动作序列（启动程序/打字/发送按键），
            // 同样不进入常规听写处理，也不计入历史（历史记录的是听写内容，不是执行的动作）。
            var macro = MacroExecutor.Match(text, _macros.Load());
            if (macro is not null)
            {
                Log.Info($"语音宏命中：\"{text}\" -> 「{macro.TriggerPhrase}」");
                var errors = _macroExecutor.Execute(macro);
                if (errors.Count > 0)
                    Log.Error($"语音宏「{macro.TriggerPhrase}」部分动作失败：{string.Join("; ", errors)}");
                // 宏执行的是启动程序/打字等复合动作，"删除这段"撤销的是"上一次听写注入的文字"这个
                // 更窄的概念，两者语义不同，宏跑完后不再认为有可撤销的上一次听写内容。
                _lastInjectedLength = 0;
                CommandExecuted?.Invoke($"宏：{macro.TriggerPhrase}");
                return;
            }

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

            // InjectText throws if the text didn't actually land (SendInput reported fewer
            // events accepted than sent) - history is only saved / TranscriptionCompleted only
            // raised below this line if it really did.
            _injector.InjectText(text);
            _lastInjectedLength = text.Length;
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
    /// Runs the action for a matched F05 口头命令. Never falls through to the normal dictation
    /// pipeline below it in OnPressEnded - a matched command always either does its action or
    /// (UppercaseSuffix with no remaining text - can't actually happen, VoiceCommandMatcher
    /// already filters that out) is a no-op, but the utterance itself is never typed literally.
    /// </summary>
    private void HandleVoiceCommand(VoiceCommandMatcher.MatchResult match, string rawText)
    {
        switch (match.Action)
        {
            case VoiceCommandAction.CancelDictation:
                if (_lastInjectedLength > 0)
                {
                    try
                    {
                        _injector.SendVirtualKey(VirtualKeys.Backspace, _lastInjectedLength);
                        Log.Info($"语音命令「{rawText}」：已发送 {_lastInjectedLength} 次退格，尝试撤销上一次听写内容");
                    }
                    catch (Exception ex)
                    {
                        // Best-effort: the primary job of "删除这段" (discard THIS utterance) has
                        // already succeeded by the time we get here - a failed backspace-undo of
                        // the PREVIOUS utterance must not be treated as this command having
                        // failed outright.
                        Log.Error("语音命令「删除这段」：撤销上一次听写内容时退格发送失败", ex);
                    }
                    _lastInjectedLength = 0;
                }
                else
                {
                    Log.Info($"语音命令「{rawText}」：本次听写内容本身被放弃，没有可撤销的上一次内容");
                }
                CommandExecuted?.Invoke("命令：删除这段");
                break;

            case VoiceCommandAction.SendEnter:
                _injector.SendVirtualKey(VirtualKeys.Enter);
                // Backspace right after Enter merges the two lines back together in effectively
                // every text editor - so this Enter is itself undoable via a later "删除这段",
                // the same way injected text is.
                _lastInjectedLength = 1;
                Log.Info($"语音命令「{rawText}」：已发送回车键");
                CommandExecuted?.Invoke("命令：换行");
                break;

            case VoiceCommandAction.UppercaseSuffix:
                var remaining = match.RemainingText;
                var corrections = _terms.Load();
                if (corrections.Count > 0) remaining = TermDictionary.Apply(remaining, corrections);
                if (_settings.AutocorrectPunctuation) remaining = PunctuationFixer.Apply(remaining);
                var upper = remaining.ToUpperInvariant();

                _injector.InjectText(upper);
                _lastInjectedLength = upper.Length;
                _history.Add(new TranscriptionRecord { Timestamp = DateTimeOffset.Now, Text = upper });
                Log.Info($"语音命令「{rawText}」：大写后注入 \"{upper}\"");
                TranscriptionCompleted?.Invoke(upper);
                break;
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
