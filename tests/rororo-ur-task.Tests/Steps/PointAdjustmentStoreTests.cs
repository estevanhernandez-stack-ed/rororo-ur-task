using System.IO;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class PointAdjustmentStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-adj-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_dir, "adjustments.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Set_then_get_round_trips()
    {
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p2", 5400534998, 100, new PointAdjustment(48, 401, new Rgb(1, 2, 3)));
        Assert.Equal(new PointAdjustment(48, 401, new Rgb(1, 2, 3)), new PointAdjustmentStore(PathIn).Get("m1", "p2", 5400534998, 100));
    }

    [Fact]
    public void Other_account_and_other_scale_are_untouched()
    {
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p2", 5400534998, 100, new PointAdjustment(48, 401));
        Assert.Null(s.Get("m1", "p2", 1647274201, 100));
        Assert.Null(s.Get("m1", "p2", 5400534998, 125));
        Assert.Null(s.Get("m1", "p3", 5400534998, 100));
    }

    [Fact]
    public void Remove_restores_the_recorded_point()
    {
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p2", 1, 100, new PointAdjustment(1, 1));
        s.Remove("m1", "p2", 1, 100);
        Assert.Null(s.Get("m1", "p2", 1, 100));
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty_and_is_kept_aside_on_write()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathIn, "{ not json");
        var s = new PointAdjustmentStore(PathIn);
        Assert.Null(s.Get("m1", "p1", 1, 100));
        s.Set("m1", "p1", 1, 100, new PointAdjustment(2, 2));
        Assert.True(File.Exists(PathIn + ".bad"));
        Assert.NotNull(new PointAdjustmentStore(PathIn).Get("m1", "p1", 1, 100));
    }

    [Fact]
    public void A_file_locked_by_a_writer_reads_as_no_adjustment()
    {
        // The points overlay (plan 2) saves while a playback reads. A read that loses the race
        // must play the recorded point, not throw into the step runner.
        var s = new PointAdjustmentStore(PathIn);
        s.Set("m1", "p1", 1, 100, new PointAdjustment(2, 2));
        using var hold = new FileStream(PathIn, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(new PointAdjustmentStore(PathIn).Get("m1", "p1", 1, 100));
    }

    [Fact]
    public void Default_path_is_outside_the_macros_folder()
    {
        var adj = Path.GetDirectoryName(PointAdjustmentStore.DefaultPath())!;
        Assert.NotEqual(Path.GetFullPath(MacroStore.DefaultDirectory()), Path.GetFullPath(adj));
        Assert.EndsWith(Path.Combine("626Labs", "RoRoRoUrTask"), adj);
    }
}
