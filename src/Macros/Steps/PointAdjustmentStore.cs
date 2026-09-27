using System.IO;
using System.Text.Json;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>A per-account, per-display-scale replacement for one point, in the macro's
/// recorded client frame. Expect/Other replace the check's colours when re-sampled.</summary>
public sealed record PointAdjustment(int X, int Y, Rgb? Expect = null, Rgb? Other = null);

/// <summary>
/// adjustments.json: macro id → point id → Roblox user id → display scale → adjustment.
/// Lives in Ur Task's data folder, never in macros\ (Ur Task and Ur OCR read every .json there
/// as a macro). Re-read when the file changes, so an open overlay's edits reach playback.
/// </summary>
public sealed class PointAdjustmentStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, PointAdjustment>>>> _data = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private bool _corrupt;

    public PointAdjustmentStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "626Labs", "RoRoRoUrTask", "adjustments.json");

    public PointAdjustment? Get(string macroId, string pointId, long userId, int scale)
    {
        lock (_gate)
        {
            try
            {
                Refresh();
            }
            catch (IOException ex)
            {
                // A write racing this read (the overlay saves while a playback reads). Missing one
                // run's adjustment is better than failing the playback.
                Labs626.UrTask.Diagnostics.DiagLog.Write($"adjustments.json unreadable, playing recorded points: {ex.Message}");
                return null;
            }
            return _data.TryGetValue(macroId, out var pts) && pts.TryGetValue(pointId, out var accts)
                && accts.TryGetValue(userId.ToString(), out var scales) && scales.TryGetValue(scale.ToString(), out var a)
                ? a : null;
        }
    }

    public void Set(string macroId, string pointId, long userId, int scale, PointAdjustment adjustment)
    {
        lock (_gate)
        {
            Refresh();
            var pts = GetOrAdd(_data, macroId);
            var accts = GetOrAdd(pts, pointId);
            var scales = GetOrAdd(accts, userId.ToString());
            scales[scale.ToString()] = adjustment;
            Write();
        }
    }

    public void Remove(string macroId, string pointId, long userId, int scale)
    {
        lock (_gate)
        {
            Refresh();
            if (_data.TryGetValue(macroId, out var pts) && pts.TryGetValue(pointId, out var accts)
                && accts.TryGetValue(userId.ToString(), out var scales) && scales.Remove(scale.ToString()))
                Write();
        }
    }

    private void Refresh()
    {
        if (!File.Exists(_path)) { _data = new(); _corrupt = false; return; }
        var stamp = File.GetLastWriteTimeUtc(_path);
        if (stamp == _loadedStamp) return;
        try
        {
            _data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, PointAdjustment>>>>>(
                File.ReadAllText(_path), Json) ?? new();
            _corrupt = false;
        }
        catch (JsonException)
        {
            _data = new();
            _corrupt = true;
        }
        _loadedStamp = stamp;
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (_corrupt && File.Exists(_path)) { File.Copy(_path, _path + ".bad", overwrite: true); _corrupt = false; }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        File.Move(tmp, _path, overwrite: true);
        _loadedStamp = File.GetLastWriteTimeUtc(_path);
    }

    private static Dictionary<string, T> GetOrAdd<T>(Dictionary<string, Dictionary<string, T>> d, string key) where T : notnull
    {
        if (!d.TryGetValue(key, out var v)) d[key] = v = new Dictionary<string, T>();
        return v;
    }
}
