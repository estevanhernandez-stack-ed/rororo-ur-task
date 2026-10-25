using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests.Ipc;

public class PlaybackRegistryTests
{
    private DateTimeOffset _now = new(2026, 9, 27, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reports_running_then_failed_with_the_sentence()
    {
        var reg = new PlaybackRegistry(() => _now);
        reg.Started("a");
        Assert.Equal("running", reg.Get("a").State);
        reg.Finished("a", PlaybackState.Failed, "check-failed", "CElCPapa: step 3 'Teleport opener' expected green.", 2);
        var r = reg.Get("a");
        Assert.True(r.Ok);
        Assert.Equal(("failed", "check-failed", 2), (r.State, r.Reason, r.StepIndex));
        Assert.StartsWith("CElCPapa: step 3", r.Detail);
    }

    [Fact]
    public void A_finished_ClearAt_names_its_no_outline_points_and_other_playbacks_name_none()
    {
        var reg = new PlaybackRegistry(() => _now);
        reg.Started("a");
        reg.Finished("a", PlaybackState.Finished, null, null, null, new[] { 2, 5 });
        reg.Started("b");
        reg.Finished("b", PlaybackState.Finished, null, null, null);
        Assert.Equal(new[] { 2, 5 }, reg.Get("a").NoOutline);
        Assert.Null(reg.Get("b").NoOutline);
    }

    [Fact]
    public void Finished_playbacks_expire_after_ten_minutes()
    {
        var reg = new PlaybackRegistry(() => _now);
        reg.Started("a");
        reg.Finished("a", PlaybackState.Finished, null, null, null);
        _now += TimeSpan.FromMinutes(9);
        Assert.True(reg.Get("a").Ok);
        _now += TimeSpan.FromMinutes(2);
        var gone = reg.Get("a");
        Assert.False(gone.Ok);
        Assert.Equal("unknown-playback", gone.Reason);
        Assert.Equal("No playback with id 'a'. Finished playbacks are kept for 10 minutes.", gone.Detail);
    }

    [Fact]
    public void Running_playbacks_never_expire()
    {
        var reg = new PlaybackRegistry(() => _now);
        reg.Started("a");
        _now += TimeSpan.FromHours(3);
        Assert.Equal("running", reg.Get("a").State);
    }

    [Fact]
    public void A_missing_id_is_refused_with_a_sentence()
    {
        var r = new PlaybackRegistry(() => _now).Get(null);
        Assert.False(r.Ok);
        Assert.Equal("Name the playback id that RunMacro returned.", r.Detail);
    }
}
