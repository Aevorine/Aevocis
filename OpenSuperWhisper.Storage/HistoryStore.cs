using System.Text.Json;
using OpenSuperWhisper.Core;
using OpenSuperWhisper.Core.Models;

namespace OpenSuperWhisper.Storage;

public sealed class HistoryStore
{
    private const int MaxItems = 200;
    private readonly string _path;
    private readonly List<TranscriptionRecord> _items;

    /// <summary>True if the most recent load found a history file that was present but
    /// unreadable as JSON, and reset to an empty history because of it.</summary>
    public bool LastLoadWasReset { get; private set; }

    /// <summary>True if the most recent load hit an IOException/UnauthorizedAccessException
    /// (file locked by another process, permission denied, etc.). While degraded, saves are
    /// skipped so an intact-but-temporarily-locked file is never overwritten with an empty list.</summary>
    public bool IsDegraded { get; private set; }

    public HistoryStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "history.json");
        _items = LoadFromDisk();
    }

    public IReadOnlyList<TranscriptionRecord> Items => _items;

    public void Add(TranscriptionRecord record)
    {
        _items.Insert(0, record);
        if (_items.Count > MaxItems)
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        SaveToDisk();
    }

    /// <summary>Empties the history, in memory and on disk, so the user has a way to purge
    /// sensitive previously-dictated content.</summary>
    public void Clear()
    {
        _items.Clear();
        SaveToDisk();
    }

    /// <summary>F23 自动过期: removes records older than <paramref name="retentionDays"/> days
    /// (measured from now). A non-positive value means "keep forever" and is a no-op - callers
    /// don't need to special-case the disabled setting themselves. Returns how many were
    /// removed, purely so the caller can log a meaningful message instead of a fixed string.</summary>
    public int PurgeOlderThan(int retentionDays)
    {
        if (retentionDays <= 0) return 0;

        var cutoff = DateTimeOffset.Now - TimeSpan.FromDays(retentionDays);
        var removed = _items.RemoveAll(r => r.Timestamp < cutoff);
        if (removed > 0) SaveToDisk();
        return removed;
    }

    private List<TranscriptionRecord> LoadFromDisk()
    {
        LastLoadWasReset = false;
        IsDegraded = false;

        if (!File.Exists(_path)) return new List<TranscriptionRecord>();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<TranscriptionRecord>>(json) ?? new List<TranscriptionRecord>();
        }
        catch (Exception ex) when (ex is JsonException or FileNotFoundException)
        {
            Log.Error("历史记录文件已损坏，重置为空", ex);
            LastLoadWasReset = true;
            return new List<TranscriptionRecord>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("历史记录文件读取失败（可能被占用或无权限），本次以空历史运行且不会保存覆盖原文件", ex);
            IsDegraded = true;
            return new List<TranscriptionRecord>();
        }
    }

    private void SaveToDisk()
    {
        if (IsDegraded)
        {
            Log.Error("跳过历史记录保存：上次加载时检测到历史文件被占用/无权限，避免覆盖磁盘上的原文件");
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Log.Error("保存历史记录失败", ex);
        }
    }
}
