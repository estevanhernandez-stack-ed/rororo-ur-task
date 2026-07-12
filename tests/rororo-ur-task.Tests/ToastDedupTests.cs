using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Covers <see cref="ToastDedup.ShouldSuppress"/> — the pure dedup/rate-limit
/// rule behind <c>RecorderViewModel.ShowError</c>. Split out from the VM
/// specifically so it's testable without a WPF Dispatcher or a live
/// PluginRuntime (RecorderViewModel itself isn't unit-testable here — see
/// AssignmentRowTests' header comment for why).
/// </summary>
public class ToastDedupTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    [Fact]
    public void FirstEverMessage_IsNotSuppressed()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(ToastDedup.ShouldSuppress(null, default, "boom", now, Window));
    }

    [Fact]
    public void SameMessage_WithinWindow_IsSuppressed()
    {
        var last = DateTimeOffset.UtcNow;
        var now = last + TimeSpan.FromSeconds(3);
        Assert.True(ToastDedup.ShouldSuppress("boom", last, "boom", now, Window));
    }

    [Fact]
    public void SameMessage_OutsideWindow_IsNotSuppressed()
    {
        var last = DateTimeOffset.UtcNow;
        var now = last + TimeSpan.FromSeconds(11);
        Assert.False(ToastDedup.ShouldSuppress("boom", last, "boom", now, Window));
    }

    [Fact]
    public void DifferentMessage_WithinWindow_IsNotSuppressed()
    {
        var last = DateTimeOffset.UtcNow;
        var now = last + TimeSpan.FromSeconds(1);
        Assert.False(ToastDedup.ShouldSuppress("boom", last, "bang", now, Window));
    }

    [Fact]
    public void SameMessage_ExactlyAtWindowBoundary_IsNotSuppressed()
    {
        // (now - lastShownAt) < window — equality means the window has fully
        // elapsed, so it must NOT suppress.
        var last = DateTimeOffset.UtcNow;
        var now = last + Window;
        Assert.False(ToastDedup.ShouldSuppress("boom", last, "boom", now, Window));
    }
}
