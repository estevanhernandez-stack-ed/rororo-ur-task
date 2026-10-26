// tests/rororo-ur-task.Tests/Ipc/ReasonsTests.cs
using System.IO;
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Ipc;

public class ReasonsTests
{
    private const string NewerSchema =
        "Made by a newer version of Ur Task (macro schema 9; this version reads up to 4). Update Ur Task to use it.";

    private static Macro NewMacro(string id) => new(
        SchemaVersion: 2, Id: id, Name: "m", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(long userId)
        => new(1000 + (int)userId, userId, $"alt-{userId}", $"acct-{userId}");

    private static MacroStore.LoadResult Loaded(IReadOnlyList<Macro>? macros = null, params MacroStore.LoadFailure[] failures)
        => new(macros ?? Array.Empty<Macro>(), failures);

    private static MacroRunInvoker Build(
        Func<MacroStore.LoadResult> load, bool breakglass = false, PlaybackRegistry? registry = null,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<SequenceResult?>>? play = null,
        Action<string>? log = null)
        => new(load,
            snapshot: () => new[] { Alt(123) },
            resolveForegroundUserId: () => 123,
            isBusy: () => false,
            playWithResult: play ?? ((m, t, d, c) => Task.FromResult<SequenceResult?>(null)),
            registry: registry, log: log, isBreakglass: () => breakglass);

    private static RunMacroRequest Req(string macroId)
        => new("1.0", "RunMacro", macroId, new[] { "123" }, null, "626labs.ur-ocr");

    [Fact]
    public void Every_existing_code_is_unchanged()
    {
        Assert.Equal("busy", Reasons.Busy);
        Assert.Equal("refused", Reasons.Refused);
        Assert.Equal("unknown-macro", Reasons.UnknownMacro);
        Assert.Equal("no-targets-resolved", Reasons.NoTargetsResolved);
        Assert.Equal("unknown-playback", Reasons.UnknownPlayback);
        Assert.Equal("version-mismatch", Reasons.VersionMismatch);
        Assert.Equal("breakglass", Reasons.Breakglass);
        Assert.Equal("macro-unreadable", Reasons.MacroUnreadable);
        Assert.Equal("keep-alive-preempted", Reasons.KeepAlivePreempted);
        Assert.Equal("check-failed", Reasons.CheckFailed);
        Assert.Equal("aborted", Reasons.Aborted);
        Assert.Equal("error", Reasons.Error);
        Assert.Equal("skipped", Reasons.Skipped);
        Assert.Equal("user", Actors.User);
        Assert.Equal("keep-alive", Actors.KeepAlive);
        Assert.Equal("host", Actors.Host);
        Assert.Equal("ur-task", Actors.UrTask);
        Assert.Equal("agent:626labs.ur-ocr", Actors.Agent("626labs.ur-ocr"));
    }

    [Fact]
    public async Task A_macro_whose_file_failed_to_load_is_macro_unreadable()
    {
        var id = Guid.NewGuid().ToString();
        var inv = Build(() => Loaded(null, new MacroStore.LoadFailure(Path.Combine("macros", $"{id}.json"), NewerSchema)));

        var r = await inv.RunAsync(Req(id), default);

        Assert.False(r.Ok);
        Assert.Equal("macro-unreadable", r.Reason);
        Assert.Equal(NewerSchema, r.Detail);
        Assert.Equal("Update Ur Task to use it.", r.Advice);
    }

    [Fact]
    public async Task An_id_with_no_file_at_all_is_still_unknown_macro()
    {
        var inv = Build(() => Loaded(null, new MacroStore.LoadFailure(Path.Combine("macros", $"{Guid.NewGuid()}.json"), NewerSchema)));

        var r = await inv.RunAsync(Req(Guid.NewGuid().ToString()), default);

        Assert.Equal("unknown-macro", r.Reason);
        Assert.Null(r.Advice);
    }

    [Fact]
    public void ListMacros_returns_the_readable_ones_and_logs_each_failure_once()
    {
        var good = NewMacro(Guid.NewGuid().ToString());
        var bad = Path.Combine("macros", $"{Guid.NewGuid()}.json");
        var logged = new List<string>();
        var inv = Build(() => Loaded(new[] { good }, new MacroStore.LoadFailure(bad, "Unexpected character.")), log: logged.Add);

        Assert.Single(inv.ListMacros());
        Assert.Single(inv.ListMacros());

        Assert.Single(logged);
        Assert.Contains("Unexpected character.", logged[0]);
    }

    [Fact]
    public void Old_shapes_are_unchanged_when_actor_and_advice_are_null()
    {
        var run = JsonSerializer.Serialize(RunMacroResponse.Refused("busy", "A sequence is already running."), BridgeContract.Json);
        Assert.Equal(
            "{\"ok\":false,\"queued\":false,\"reason\":\"busy\",\"detail\":\"A sequence is already running.\"}", run);

        var get = JsonSerializer.Serialize(GetPlaybackResponse.Refused("unknown-playback", "x"), BridgeContract.Json);
        Assert.Equal("{\"ok\":false,\"reason\":\"unknown-playback\",\"detail\":\"x\"}", get);
        Assert.DoesNotContain("actor", run + get);
        Assert.DoesNotContain("advice", run + get);
    }

    [Fact]
    public async Task Breakglass_refusal_names_the_user_as_actor()
    {
        var inv = Build(() => Loaded(), breakglass: true);

        var r = await inv.RunAsync(Req(Guid.NewGuid().ToString()), default);

        Assert.Equal("breakglass", r.Reason);
        Assert.Equal("user", r.Actor);
    }

    [Fact]
    public async Task Preempted_playback_reports_keep_alive_as_actor()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var inv = Build(
            () => Loaded(new[] { m }),
            registry: new PlaybackRegistry(),
            play: (macro, targets, d, c) => Task.FromResult<SequenceResult?>(new SequenceResult(
                new[] { new AltOutcome(targets[0], PlaybackOutcome.Aborted, "Stopped for keep-alive.", Code: Reasons.KeepAlivePreempted) },
                0, 1, 0, TimeSpan.Zero)));

        var run = await inv.RunAsync(Req(m.Id), default);
        GetPlaybackResponse got = default!;
        for (var i = 0; i < 200; i++)
        {
            got = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", run.PlaybackId, "626labs.ur-ocr"));
            if (got.State != "running") break;
            await Task.Delay(10);
        }

        Assert.Equal("failed", got.State);
        Assert.Equal("keep-alive-preempted", got.Reason);
        Assert.Equal("keep-alive", got.Actor);
        Assert.Equal("Give long holds a max so keep-alive can take its turn.", got.Advice);
    }

    [Fact]
    public void A_blank_playback_id_names_the_calling_agent()
    {
        var inv = Build(() => Loaded());

        var got = inv.GetPlayback(new GetPlaybackRequest("1.0", "GetPlayback", "", "626labs.ur-ocr"));

        Assert.Equal("refused", got.Reason);
        Assert.Equal("agent:626labs.ur-ocr", got.Actor);
    }
}
