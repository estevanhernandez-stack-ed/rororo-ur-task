using System.IO;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Tests.Steps;

public class MacroV4FileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-v4-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Macro Sample() => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(),
        Name: "v4 sample",
        RecordMode: "PerWindow",
        RecordedAgainstUserId: 1,
        RecordedAgainstDisplayName: "fixture",
        InterAltDelayMs: null,
        RecordedAtUnixMs: 1,
        Events: new[] { new MacroEvent(10, MacroEventKind.MouseDown, 0, 5, 6, 1, 0), new MacroEvent(90, MacroEventKind.MouseUp, 0, 5, 6, 1, 0) },
        CoordSpace: Macro.CoordSpaceClient,
        RecordedClientW: 800,
        RecordedClientH: 599,
        RecordedDisplayScale: 100,
        Steps: new MacroStep[]
        {
            new KeyStep(0, 0x41, true),
            new PointStep(1500, "p1", "Teleport opener", 42, 398,
                Check: new ColorCheck(new CheckBox(), new Rgb(139, 224, 58), new Rgb(128, 128, 128)), CheckEnabled: true),
            new FirstMatchStep(400, "f1", "Best mine", new[]
            {
                new PointStep(0, "f1a", "#8", 137, 390, Check: new ColorCheck(new CheckBox(), new Rgb(139, 224, 58), new Rgb(150, 150, 160))),
                new PointStep(0, "f1b", "#7", 312, 390, Check: new ColorCheck(new CheckBox(), new Rgb(139, 224, 58), new Rgb(150, 150, 160))),
            }, NoMatchAction.Skip),
            new DragStep(0, 2, 400, 300, 0, 300, 400),
            new WheelStep(0, 400, 300, -120),
            new PointerMoveStep(0, 0, 200, 300),
            new WaitStep(250),
            new RawStep(0, new[] { new MacroEvent(0, MacroEventKind.MouseDown, 0, 1, 1, 1, 0) }, "test"),
        });

    [Fact]
    public void Round_trips_every_step_kind_through_the_store()
    {
        var store = new MacroStore(_dir);
        var m = Sample();
        store.Save(m);
        var loaded = Assert.Single(store.LoadAll().Macros);
        Assert.Equal(4, loaded.SchemaVersion);
        Assert.Equal(100, loaded.RecordedDisplayScale);
        Assert.Equal(m.Steps!.Count, loaded.Steps!.Count);
        var p = Assert.IsType<PointStep>(loaded.Steps[1]);
        Assert.Equal("Teleport opener", p.Label);
        Assert.True(p.CheckEnabled);
        Assert.Equal(new Rgb(128, 128, 128), p.Check!.Other);
        var fm = Assert.IsType<FirstMatchStep>(loaded.Steps[2]);
        Assert.Equal(NoMatchAction.Skip, fm.OnNoMatch);
        Assert.Equal(2, fm.Candidates.Count);
        Assert.IsType<RawStep>(loaded.Steps[^1]);
    }

    [Fact]
    public void V4_file_still_carries_the_original_events()
    {
        var store = new MacroStore(_dir);
        var m = Sample();
        store.Save(m);
        var json = File.ReadAllText(Path.Combine(_dir, m.Id + ".json"));
        Assert.Contains("\"events\"", json);
        Assert.Contains("\"steps\"", json);
        Assert.Contains("\"kind\": \"point\"", json);
        Assert.Contains("\"onNoMatch\": \"skip\"", json); // spec casing, not the C# member name
    }

    [Fact]
    public void A_check_without_a_box_loads_and_is_refused_with_a_sentence()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "point", "delayMs": 0, "id": "p1", "x": 10, "y": 20, "checkEnabled": true,
                       "check": { "expect": { "r": 1, "g": 2, "b": 3 } } } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        Assert.Equal("Step 1 'p1' has a check with no box.", StepValidator.Validate(m.Steps!));
    }

    [Fact]
    public void A_check_without_an_expected_colour_is_a_listed_failure()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "point", "delayMs": 0, "id": "p1", "x": 10, "y": 20,
                       "check": { "box": { "offsetX": -2, "offsetY": -2, "w": 5, "h": 5 } } } ] }
        """);
        var result = new MacroStore(_dir).LoadAll();
        Assert.Empty(result.Macros);
        Assert.Contains(result.Failures, f => f.Path.EndsWith(id + ".json") && f.Reason.Contains("expect"));
    }

    [Fact]
    public void V3_file_loads_with_no_steps()
    {
        var store = new MacroStore(_dir);
        var v3 = Sample() with { Steps = null, RecordedDisplayScale = null };
        store.Save(v3);
        var loaded = Assert.Single(store.LoadAll().Macros);
        Assert.False(loaded.HasSteps);
    }

    [Fact]
    public void Kind_does_not_have_to_come_first()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "name": "agent written", "recordedAtUnixMs": 1,
          "events": [], "coordSpace": "client", "recordedClientW": 800, "recordedClientH": 599,
          "steps": [ { "delayMs": 0, "id": "p1", "x": 10, "y": 20, "kind": "point" } ] }
        """);
        var result = new MacroStore(_dir).LoadAll();
        Assert.Empty(result.Failures);
        Assert.IsType<PointStep>(Assert.Single(result.Macros).Steps![0]);
    }

    [Fact]
    public void Unknown_kind_is_a_listed_failure_not_a_crash()
    {
        var store = new MacroStore(_dir);
        store.Save(Sample());
        var bad = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, bad + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{bad}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "teleport", "delayMs": 0 } ] }
        """);
        var result = store.LoadAll();
        Assert.Single(result.Macros);
        Assert.Contains(result.Failures, f => f.Path.EndsWith(bad + ".json"));
    }

    [Fact]
    public void Negative_delays_are_clamped_on_load()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "wait", "delayMs": -500 } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        Assert.Equal(0, m.Steps![0].DelayMs);
    }

    [Fact]
    public void Bundle_import_keeps_steps()
    {
        var json = MacroBundle.Serialize(new[] { Sample() }, 1);
        var parsed = Assert.Single(MacroBundle.Parse(json).Macros);
        var imported = MacroBundle.PrepareForImport(parsed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.True(imported.HasSteps);
        Assert.Equal(Macro.CurrentSchemaVersion, imported.SchemaVersion);
    }

    [Fact]
    public void Validator_accepts_a_good_list() => Assert.Null(StepValidator.Validate(Sample().Steps!));

    [Fact]
    public void Validator_refuses_first_match_without_candidates()
    {
        var err = StepValidator.Validate(new MacroStep[] { new FirstMatchStep(0, "f1", "Best mine", Array.Empty<PointStep>()) });
        Assert.Equal("Step 1 'Best mine' is a first match with no candidates.", err);
    }

    [Fact]
    public void Validator_refuses_a_candidate_without_a_check()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "f1a", "#8", 1, 1) }),
        });
        Assert.Equal("Step 1 'Best mine': candidate '#8' has no colour to check.", err);
    }

    [Fact]
    public void Validator_refuses_an_enabled_check_with_no_sample()
    {
        var err = StepValidator.Validate(new MacroStep[] { new PointStep(0, "p1", null, 1, 1, CheckEnabled: true) });
        Assert.Equal("Step 1 'p1' has its check on but no colour sample.", err);
    }

    [Fact]
    public void Validator_refuses_duplicate_point_ids()
    {
        var err = StepValidator.Validate(new MacroStep[] { new PointStep(0, "p1", null, 1, 1), new PointStep(0, "p1", null, 2, 2) });
        Assert.Equal("Step 2 reuses point id 'p1'.", err);
    }

    [Fact]
    public void Validator_refuses_an_oversized_box()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new PointStep(0, "p1", "Big", 1, 1, Check: new ColorCheck(new CheckBox(0, 0, 12, 12), new Rgb(0, 0, 0)), CheckEnabled: true),
        });
        Assert.Equal("Step 1 'Big' has a check box larger than 9x9.", err);
    }

    [Fact]
    public void Validator_refuses_an_empty_box()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new PointStep(0, "p1", "Flat", 1, 1, Check: new ColorCheck(new CheckBox(0, 0, 0, 5), new Rgb(0, 0, 0)), CheckEnabled: true),
        });
        Assert.Equal("Step 1 'Flat' has an empty check box.", err);
    }

    [Fact]
    public void Validator_refuses_a_point_with_no_id()
    {
        Assert.Equal("Step 1 has no point id.", StepValidator.Validate(new MacroStep[] { new PointStep(0, null!, null, 1, 1) }));
        Assert.Equal("Step 1 'Best mine': a candidate has no point id.", StepValidator.Validate(new MacroStep[]
        {
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, " ", "#8", 1, 1, Check: new ColorCheck(new CheckBox(), new Rgb(0, 0, 0))) }),
        }));
    }

    [Fact]
    public void Validator_refuses_a_candidate_check_with_no_box()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "f1a", "#8", 1, 1, Check: new ColorCheck(null!, new Rgb(0, 0, 0))) }),
        });
        Assert.Equal("Step 1 'Best mine': candidate '#8' has a check with no box.", err);
    }

    [Fact]
    public void A_hold_round_trips_through_the_store()
    {
        var store = new MacroStore(_dir);
        var m = Sample() with
        {
            Steps = new MacroStep[]
            {
                new HoldStep(300, "spot-N", "Spot N", 400, 244, Check: new HoldCheck(new CheckBox(), 20)),
                new HoldStep(0, "h2", null, 1, 1, 2, new HoldCheck(new CheckBox(-1, -1, 3, 3)), MaxMs: 1500),
            },
        };
        store.Save(m);
        var json = File.ReadAllText(Path.Combine(_dir, m.Id + ".json"));
        Assert.Contains("\"kind\": \"hold\"", json);

        var loaded = Assert.Single(store.LoadAll().Macros);
        var a = Assert.IsType<HoldStep>(loaded.Steps![0]);
        Assert.Equal((300, "spot-N", "Spot N", 400, 244, 1), (a.DelayMs, a.Id, a.Label, a.X, a.Y, a.Button));
        Assert.Equal(new CheckBox(), a.Check!.Box);
        Assert.Equal(20, a.Check.Tolerance);
        Assert.Null(a.MaxMs);
        var b = Assert.IsType<HoldStep>(loaded.Steps[1]);
        Assert.Equal((2, 1500), (b.Button, b.MaxMs));
        Assert.Equal(new CheckBox(-1, -1, 3, 3), b.Check!.Box);
    }

    [Fact]
    public void An_agent_written_hold_loads_with_its_defaults()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "coordSpace": "client", "recordedClientW": 800, "recordedClientH": 599,
          "steps": [ { "kind": "hold", "delayMs": 300, "id": "spot-N", "x": 400, "y": 244,
                       "check": { "box": { "offsetX": -2, "offsetY": -2, "w": 5, "h": 5 } } } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        var h = Assert.IsType<HoldStep>(m.Steps![0]);
        Assert.Equal(1, h.Button);
        Assert.Equal(ColorCheck.DefaultTolerance, h.Check!.Tolerance);
        Assert.Null(h.MaxMs);
        Assert.Null(StepValidator.Validate(m.Steps));
    }

    [Fact]
    public void A_hold_check_without_a_box_loads_and_is_refused_with_a_sentence()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "hold", "delayMs": 0, "id": "spot-N", "label": "Spot N", "x": 400, "y": 244,
                       "check": { "tolerance": 20 } } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        Assert.Equal("Step 1 'Spot N' has a check with no box.", StepValidator.Validate(m.Steps!));
    }

    [Fact]
    public void Validator_refuses_bad_holds_with_a_sentence()
    {
        HoldStep H(HoldCheck? check = null, int? maxMs = null, string id = "spot-N")
            => new(0, id, "Spot N", 400, 244, Check: check, MaxMs: maxMs);
        var ok = new HoldCheck(new CheckBox());

        Assert.Null(StepValidator.Validate(new MacroStep[] { H(ok) }));
        Assert.Equal("Step 1 has no point id.", StepValidator.Validate(new MacroStep[] { H(ok, id: " ") }));
        Assert.Equal("Step 1 'Spot N' is a hold with no check.", StepValidator.Validate(new MacroStep[] { H() }));
        Assert.Equal("Step 1 'Spot N' has an empty check box.", StepValidator.Validate(new MacroStep[] { H(new HoldCheck(new CheckBox(0, 0, 0, 5))) }));
        Assert.Equal("Step 1 'Spot N' has a check box larger than 9x9.", StepValidator.Validate(new MacroStep[] { H(new HoldCheck(new CheckBox(0, 0, 12, 12))) }));
        Assert.Equal("Step 1 'Spot N' has a hold tolerance below 1.", StepValidator.Validate(new MacroStep[] { H(new HoldCheck(new CheckBox(), 0)) }));
        Assert.Equal("Step 1 'Spot N' has a maxMs below 1.", StepValidator.Validate(new MacroStep[] { H(ok, maxMs: 0) }));
        Assert.Equal("Step 2 reuses point id 'spot-N'.", StepValidator.Validate(new MacroStep[] { new PointStep(0, "spot-N", null, 1, 1), H(ok) }));
    }

    [Fact]
    public void A_hold_estimates_its_limit_or_nothing_for_an_open_hold()
    {
        Assert.Equal(300 + StepTiming.JumpWiggleMs + 2000, StepTiming.EstimateMs(new HoldStep(300, "h", null, 1, 1, MaxMs: 2000)));
        Assert.Equal(300 + StepTiming.JumpWiggleMs, StepTiming.EstimateMs(new HoldStep(300, "h", null, 1, 1)));
    }

    [Fact]
    public void SkipIfOther_loads_from_json_by_its_spec_name()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "steps": [ { "kind": "firstMatch", "delayMs": 0, "id": "am-off", "label": "Auto Mine off",
                       "candidates": [ { "delayMs": 0, "id": "am-off-dot", "x": 40, "y": 300,
                         "check": { "box": { "offsetX": 15, "offsetY": -11, "w": 3, "h": 3 },
                                    "expect": { "r": 125, "g": 246, "b": 13 }, "other": { "r": 255, "g": 19, "b": 90 } } } ],
                       "onNoMatch": "skipIfOther" } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        Assert.Equal(NoMatchAction.SkipIfOther, Assert.IsType<FirstMatchStep>(m.Steps![0]).OnNoMatch);
        Assert.Null(StepValidator.Validate(m.Steps));
    }

    [Fact]
    public void Validator_refuses_skipIfOther_on_a_candidate_with_no_other_colour()
    {
        var err = StepValidator.Validate(new MacroStep[]
        {
            new FirstMatchStep(0, "am-off", "Auto Mine off", new[]
            {
                new PointStep(0, "am-off-dot", "dot", 40, 300, Check: new ColorCheck(new CheckBox(15, -11, 3, 3), new Rgb(125, 246, 13))),
            }, NoMatchAction.SkipIfOther),
        });
        Assert.Equal("Step 1 'Auto Mine off': candidate 'dot' has no other colour, so skipIfOther can never skip.", err);
    }

    [Fact]
    public void Reach_round_trips_on_holds_and_points()
    {
        var store = new MacroStore(_dir);
        var m = Sample() with
        {
            Steps = new MacroStep[]
            {
                new HoldStep(0, "spot-N", "Spot N", 400, 244, Check: new HoldCheck(new CheckBox()),
                    Reach: new OutlineCheck(new CheckBox(-40, -40, 80, 80), 60)),
                new PointStep(0, "p1", "Ore", 456, 300, Reach: new OutlineCheck(new CheckBox(-30, -30, 60, 60), 25, 240)),
            },
        };
        store.Save(m);
        var json = File.ReadAllText(Path.Combine(_dir, m.Id + ".json"));
        Assert.Contains("\"reach\": {", json);
        Assert.Contains("\"minCount\": 60", json);
        Assert.Contains("\"whiteMin\": 225", json);

        var loaded = Assert.Single(store.LoadAll().Macros);
        var h = Assert.IsType<HoldStep>(loaded.Steps![0]);
        Assert.Equal(new OutlineCheck(new CheckBox(-40, -40, 80, 80), 60, 225), h.Reach);
        var p = Assert.IsType<PointStep>(loaded.Steps[1]);
        Assert.Equal(new OutlineCheck(new CheckBox(-30, -30, 60, 60), 25, 240), p.Reach);
    }

    [Fact]
    public void An_agent_written_reach_loads_with_its_default_whiteMin()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(_dir, id + ".json"), $$"""
        { "schemaVersion": 4, "id": "{{id}}", "recordedAtUnixMs": 1, "events": [],
          "coordSpace": "client", "recordedClientW": 800, "recordedClientH": 599,
          "steps": [ { "kind": "hold", "delayMs": 0, "id": "spot-N", "x": 400, "y": 244,
                       "check": { "box": { "offsetX": -2, "offsetY": -2, "w": 5, "h": 5 } },
                       "reach": { "box": { "offsetX": -40, "offsetY": -40, "w": 80, "h": 80 }, "minCount": 60 } } ] }
        """);
        var m = Assert.Single(new MacroStore(_dir).LoadAll().Macros);
        var h = Assert.IsType<HoldStep>(m.Steps![0]);
        Assert.Equal(OutlineCheck.DefaultWhiteMin, h.Reach!.WhiteMin);
        Assert.Equal(60, h.Reach.MinCount);
        Assert.Null(StepValidator.Validate(m.Steps));
    }

    [Fact]
    public void Validator_refuses_bad_reach_checks_with_a_sentence()
    {
        HoldStep H(OutlineCheck reach) => new(0, "spot-N", "Spot N", 400, 244, Check: new HoldCheck(new CheckBox()), Reach: reach);
        string? V(params MacroStep[] steps) => StepValidator.Validate(steps);
        var box = new CheckBox(-40, -40, 80, 80);

        Assert.Null(V(H(new OutlineCheck(box, 60))));
        Assert.Null(V(H(new OutlineCheck(new CheckBox(-60, -60, 120, 120), 14400, 1))));
        Assert.Equal("Step 1 'Spot N' has a reach check with no box.", V(H(new OutlineCheck(null!, 60))));
        Assert.Equal("Step 1 'Spot N' has an empty reach box.", V(H(new OutlineCheck(new CheckBox(0, 0, 0, 80), 60))));
        Assert.Equal("Step 1 'Spot N' has a reach box larger than 120x120.", V(H(new OutlineCheck(new CheckBox(0, 0, 121, 80), 60))));
        Assert.Equal("Step 1 'Spot N' has a reach minCount below 1.", V(H(new OutlineCheck(box, 0))));
        Assert.Equal("Step 1 'Spot N' has a reach minCount larger than its box, so it can never pass.", V(H(new OutlineCheck(new CheckBox(0, 0, 4, 4), 17))));
        Assert.Equal("Step 1 'Spot N' has a reach whiteMin outside 1 to 255.", V(H(new OutlineCheck(box, 60, 256))));
        Assert.Equal("Step 1 'Spot N' has a reach whiteMin outside 1 to 255.", V(H(new OutlineCheck(box, 60, 0))));

        var green = new ColorCheck(new CheckBox(), new Rgb(139, 224, 58));
        Assert.Null(V(new PointStep(0, "p1", "Ore", 400, 244, Reach: new OutlineCheck(box, 60))));
        Assert.Null(V(new PointStep(0, "p1", "Ore", 400, 244, Check: green, CheckEnabled: false, Reach: new OutlineCheck(box, 60))));
        Assert.Equal("Step 1 'Ore' has a reach minCount below 1.", V(new PointStep(0, "p1", "Ore", 400, 244, Reach: new OutlineCheck(box, 0))));
        Assert.Equal("Step 1 'Ore' has both a colour check and a reach check; use one.",
            V(new PointStep(0, "p1", "Ore", 400, 244, Check: green, CheckEnabled: true, Reach: new OutlineCheck(box, 60))));
        Assert.Equal("Step 1 'Best mine': candidate '#8' has a reach check, which a first match does not use.",
            V(new FirstMatchStep(0, "f1", "Best mine", new[] { new PointStep(0, "f1a", "#8", 1, 1, Check: green, Reach: new OutlineCheck(box, 60)) })));
    }

    [Fact]
    public void A_reach_check_does_not_change_the_estimate()
    {
        // The estimate is a floor: a skipped step can end sooner, a held one later.
        var reach = new OutlineCheck(new CheckBox(-40, -40, 80, 80), 60);
        Assert.Equal(StepTiming.EstimateMs(new HoldStep(300, "h", null, 1, 1)), StepTiming.EstimateMs(new HoldStep(300, "h", null, 1, 1, Reach: reach)));
        Assert.Equal(StepTiming.EstimateMs(new PointStep(300, "p", null, 1, 1)), StepTiming.EstimateMs(new PointStep(300, "p", null, 1, 1, Reach: reach)));
        Assert.Equal(300, StepTiming.ReachGraceMs);
    }
}
