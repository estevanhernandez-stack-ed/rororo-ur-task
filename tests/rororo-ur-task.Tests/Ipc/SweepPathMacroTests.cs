// tests/rororo-ur-task.Tests/Ipc/SweepPathMacroTests.cs
using System.Text.Json;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Ipc;

public class SweepPathMacroTests
{
    // The wire shape Ur OCR's SweepPathClientTests pins, byte for byte apart from the target.
    private const string ContractJson = """
        { "contractVersion": "1.0", "method": "SweepPath", "callerPluginId": "626labs.ur-ocr",
          "target": "123456789",
          "client": { "w": 800, "h": 599 },
          "path": [ { "x": 450, "y": 300 }, { "x": 450, "y": 250 }, { "x": 400, "y": 250 }, { "x": 450, "y": 300 } ],
          "step": 50, "dwellMs": 400,
          "guard": { "x": 55, "y": 289, "w": 3, "h": 3, "expect": { "r": 255, "g": 19, "b": 90 }, "tolerance": 30 } }
        """;

    private static SweepPathRequest Contract() => JsonSerializer.Deserialize<SweepPathRequest>(ContractJson, BridgeContract.Json)!;

    private static readonly ClearAtGuard Dot = new(55, 289, 3, 3, new Rgb(255, 19, 90), 30);

    /// <summary>Ring 1 around 400,300 at 50 px, from the east block and back to it.</summary>
    private static readonly (int X, int Y)[] Ring1 =
    {
        (450, 300), (450, 250), (400, 250), (350, 250), (350, 300), (350, 350), (400, 350), (450, 350), (450, 300),
    };

    private static SweepPathRequest Valid(IEnumerable<(int X, int Y)>? path = null, int step = 50, int dwellMs = 400) => new(
        "1.0", "SweepPath", "626labs.ur-ocr", "123", new ClearAtClient(800, 599),
        (path ?? Ring1).Select(p => new SweepPoint(p.X, p.Y)).ToList(), step, dwellMs, Dot);

    /// <summary><paramref name="n"/> points on the 50 px lattice from 100,100: back and forth along a
    /// row, then one block down and back up to the start, so it closes.</summary>
    private static IEnumerable<(int X, int Y)> Long(int n)
        => Enumerable.Range(0, n - 2).Select(i => (100 + i % 2 * 50, 100)).Append((100, 150)).Append((100, 100));

    [Fact]
    public void The_contract_example_deserializes()
    {
        var r = Contract();
        Assert.Equal(("SweepPath", "123456789", 800, 599, 50, 400), (r.Method, r.Target, r.Client!.W, r.Client.H, r.Step, r.DwellMs));
        Assert.Equal(new[] { new SweepPoint(450, 300), new SweepPoint(450, 250), new SweepPoint(400, 250), new SweepPoint(450, 300) }, r.Path);
        Assert.Equal(Dot, r.Guard);
    }

    [Fact]
    public void Build_makes_one_unsaved_sweep_step_with_the_guard()
    {
        var m = SweepPathMacro.Build(Contract(), "sweep-pb1");

        Assert.Equal(("sweep-pb1", "SweepPath (4 points)", Macro.CoordSpaceClient, (int?)800, (int?)599),
            (m.Id, m.Name, m.CoordSpace, m.RecordedClientW, m.RecordedClientH));
        Assert.Empty(m.Events);
        var s = Assert.IsType<SweepStep>(Assert.Single(m.Steps!));
        Assert.Equal((0, 400, 1), (s.DelayMs, s.DwellMs, s.Button));
        Assert.Equal(Contract().Path, s.Path);
        Assert.Equal(new ScreenGuard(55, 289, 3, 3, new Rgb(255, 19, 90), 30), m.Guard);
        Assert.False(ClearAtMacro.SharesBaseline(m.Id));
    }

    [Fact]
    public void Build_names_the_macro_by_its_point_count()
        => Assert.Equal("SweepPath (9 points)", SweepPathMacro.Build(Valid(), "sweep-x").Name);

    [Fact]
    public void The_built_step_passes_the_step_validator()
        => Assert.Null(StepValidator.Validate(SweepPathMacro.Build(Valid(Long(256)), "sweep-x").Steps!));

    [Fact]
    public void The_log_lines_name_the_start_the_step_the_dwell_and_the_guard()
    {
        var r = Contract();
        var m = SweepPathMacro.Build(r, "sweep-pb1");

        Assert.Equal("bridge playback pb1 'SweepPath (4 points)' on alt-1: from 450,300, 50 px steps, 400 ms a point",
            SweepPathMacro.StartLine("pb1", m, "alt-1", r));
        Assert.Equal("SweepPath guard at (55,289), expecting #FF135A ±30", SweepPathMacro.GuardLine(r));
    }

    [Fact]
    public void Validate_accepts_the_contract_example_ring_1_and_256_points()
    {
        Assert.Null(SweepPathMacro.Validate(Contract()));
        Assert.Null(SweepPathMacro.Validate(Valid()));
        Assert.Null(SweepPathMacro.Validate(Valid(Long(256))));
    }

    [Theory]
    [InlineData("no-target", "SweepPath needs a target account.")]
    [InlineData("zero-target", "SweepPath target '0' is not a decimal user id.")]
    [InlineData("foreground-target", "SweepPath needs a decimal user id; got an invalid target.")]
    [InlineData("no-client", "SweepPath needs the client size the path was measured in.")]
    [InlineData("2-points", "SweepPath takes 3 to 256 points; got 2.")]
    [InlineData("257-points", "SweepPath takes 3 to 256 points; got 257.")]
    [InlineData("step-7", "SweepPath needs a step of 8 to 240 px; got 7.")]
    [InlineData("step-241", "SweepPath needs a step of 8 to 240 px; got 241.")]
    [InlineData("dwell-49", "SweepPath needs a dwellMs of 50 to 5000; got 49.")]
    [InlineData("dwell-5001", "SweepPath needs a dwellMs of 50 to 5000; got 5001.")]
    [InlineData("no-guard", "SweepPath needs a guard: the pixel it watches while the button is down.")]
    [InlineData("guard-no-colour", "SweepPath has a guard with no expected colour.")]
    [InlineData("null-point", "SweepPath point 2 is empty.")]
    [InlineData("point-outside", "SweepPath point 2 at 800,300 is outside the 800x599 client.")]
    [InlineData("off-lattice", "SweepPath point 2 at 475,300 is not a whole number of 50 px steps from the start at 450,300.")]
    [InlineData("repeat", "SweepPath point 3 repeats point 2; every move must go to another block.")]
    [InlineData("open", "SweepPath must end on its start block at 450,300, where the button comes up; it ends at 450,350.")]
    public void Validate_refuses_with_one_sentence(string @case, string sentence)
    {
        var ok = Valid();
        IReadOnlyList<SweepPoint> With(int index, SweepPoint? p)
        {
            var list = ok.Path!.ToList();
            list[index] = p!;
            return list;
        }
        var r = @case switch
        {
            "no-target" => ok with { Target = null },
            "zero-target" => ok with { Target = "0" },
            "foreground-target" => ok with { Target = "foreground" },
            "no-client" => ok with { Client = null },
            "2-points" => Valid(new[] { (450, 300), (450, 250) }),
            "257-points" => Valid(Long(257)),
            "step-7" => ok with { Step = 7 },
            "step-241" => ok with { Step = 241 },
            "dwell-49" => ok with { DwellMs = 49 },
            "dwell-5001" => ok with { DwellMs = 5001 },
            "no-guard" => ok with { Guard = null },
            "guard-no-colour" => ok with { Guard = Dot with { Expect = null } },
            "null-point" => ok with { Path = With(1, null) },
            "point-outside" => ok with { Path = With(1, new SweepPoint(800, 300)) },
            "off-lattice" => ok with { Path = With(1, new SweepPoint(475, 300)) },
            "repeat" => Valid(new[] { (450, 300), (450, 250), (450, 250), (450, 300) }),
            "open" => Valid(Ring1.Take(8)),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        Assert.Equal(sentence, SweepPathMacro.Validate(r));
    }

    // freePath (0.12.0, additive): the ore sweep's points sit wherever the ore is, so the lattice
    // rule is the one rule it skips. The wire shape Ur OCR sends, step omitted.
    private const string FreePathJson = """
        { "contractVersion": "1.0", "method": "SweepPath", "callerPluginId": "626labs.ur-ocr",
          "target": "123456789",
          "client": { "w": 800, "h": 599 },
          "path": [ { "x": 437, "y": 281 }, { "x": 461, "y": 263 }, { "x": 353, "y": 322 }, { "x": 437, "y": 281 } ],
          "freePath": true, "dwellMs": 400,
          "guard": { "x": 55, "y": 289, "w": 3, "h": 3, "expect": { "r": 255, "g": 19, "b": 90 }, "tolerance": 30 } }
        """;

    private static SweepPathRequest FreeContract() => JsonSerializer.Deserialize<SweepPathRequest>(FreePathJson, BridgeContract.Json)!;

    private static readonly (int X, int Y)[] OffLattice = { (437, 281), (461, 263), (353, 322), (437, 281) };

    private static SweepPathRequest Free(IEnumerable<(int X, int Y)>? path = null, int step = 0, int dwellMs = 400)
        => Valid(path ?? OffLattice, step, dwellMs) with { FreePath = true };

    [Fact]
    public void A_request_without_freePath_is_a_lattice_path()
    {
        Assert.False(Contract().FreePath);
        Assert.False(Valid().FreePath);
    }

    [Fact]
    public void The_freePath_example_deserializes_with_no_step()
    {
        var r = FreeContract();
        Assert.True(r.FreePath);
        Assert.Equal(0, r.Step);
        Assert.Equal(new[] { new SweepPoint(437, 281), new SweepPoint(461, 263), new SweepPoint(353, 322), new SweepPoint(437, 281) }, r.Path);
    }

    [Fact]
    public void A_freePath_request_serializes_freePath_and_a_lattice_one_does_not()
    {
        Assert.Contains("\"freePath\":true", JsonSerializer.Serialize(Free(), BridgeContract.Json));
        Assert.DoesNotContain("freePath", JsonSerializer.Serialize(Valid(), BridgeContract.Json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_accepts_a_free_path_off_the_lattice_with_step_0_or_a_step_in_range()
    {
        Assert.Null(SweepPathMacro.Validate(FreeContract()));
        Assert.Null(SweepPathMacro.Validate(Free()));
        Assert.Null(SweepPathMacro.Validate(Free(step: 50)));
        Assert.Null(SweepPathMacro.Validate(Free(Long(256))));
    }

    [Fact]
    public void Without_freePath_the_same_path_is_still_refused_off_the_lattice()
        => Assert.Equal("SweepPath point 2 at 461,263 is not a whole number of 50 px steps from the start at 437,281.",
            SweepPathMacro.Validate(Valid(OffLattice)));

    [Theory]
    [InlineData("no-target", "SweepPath needs a target account.")]
    [InlineData("no-client", "SweepPath needs the client size the path was measured in.")]
    [InlineData("2-points", "SweepPath takes 3 to 256 points; got 2.")]
    [InlineData("257-points", "SweepPath takes 3 to 256 points; got 257.")]
    [InlineData("step-7", "SweepPath needs a step of 8 to 240 px; got 7.")]
    [InlineData("step-241", "SweepPath needs a step of 8 to 240 px; got 241.")]
    [InlineData("step-negative", "SweepPath needs a step of 8 to 240 px; got -1.")]
    [InlineData("dwell-49", "SweepPath needs a dwellMs of 50 to 5000; got 49.")]
    [InlineData("dwell-5001", "SweepPath needs a dwellMs of 50 to 5000; got 5001.")]
    [InlineData("no-guard", "SweepPath needs a guard: the pixel it watches while the button is down.")]
    [InlineData("point-outside", "SweepPath point 2 at 800,263 is outside the 800x599 client.")]
    [InlineData("repeat", "SweepPath point 3 repeats point 2; every move must go to another block.")]
    [InlineData("open", "SweepPath must end on its start block at 437,281, where the button comes up; it ends at 353,322.")]
    public void A_free_path_keeps_every_other_rule(string @case, string sentence)
    {
        var ok = Free();
        var r = @case switch
        {
            "no-target" => ok with { Target = null },
            "no-client" => ok with { Client = null },
            "2-points" => Free(new[] { (437, 281), (461, 263) }),
            "257-points" => Free(Long(257)),
            "step-7" => ok with { Step = 7 },
            "step-241" => ok with { Step = 241 },
            "step-negative" => ok with { Step = -1 },
            "dwell-49" => ok with { DwellMs = 49 },
            "dwell-5001" => ok with { DwellMs = 5001 },
            "no-guard" => ok with { Guard = null },
            "point-outside" => Free(new[] { (437, 281), (800, 263), (353, 322), (437, 281) }),
            "repeat" => Free(new[] { (437, 281), (461, 263), (461, 263), (437, 281) }),
            "open" => Free(OffLattice.Take(3)),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        Assert.Equal(sentence, SweepPathMacro.Validate(r));
    }

    [Fact]
    public void A_free_path_builds_the_same_sweep_step()
    {
        var m = SweepPathMacro.Build(FreeContract(), "sweep-pb2");
        var s = Assert.IsType<SweepStep>(Assert.Single(m.Steps!));
        Assert.Equal(FreeContract().Path, s.Path);
        Assert.Equal(400, s.DwellMs);
        Assert.Null(StepValidator.Validate(m.Steps!));
    }

    [Fact]
    public void A_free_path_start_line_says_free_path_instead_of_a_step()
    {
        var r = FreeContract();
        Assert.Equal("bridge playback pb2 'SweepPath (4 points)' on alt-1: from 437,281, free path, 400 ms a point",
            SweepPathMacro.StartLine("pb2", SweepPathMacro.Build(r, "sweep-pb2"), "alt-1", r));
    }
}
