using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The naming convention <c>&lt;WHERE&gt; | &lt;ROUTE&gt; n/N | &lt;what&gt; [account] (notes)</c>
/// is the library's structure. The names below are real ones from Este's library.
/// </summary>
public class MacroNameTests
{
    [Fact]
    public void FullConvention_SplitsEveryPart()
    {
        var p = MacroName.Parse("PLZ | booth 1/3 | Make Listing + paste search + pick + Confirm (clipboard = name)");

        Assert.Equal("PLZ", p.Where);
        Assert.Equal("PLZ", p.Group);
        Assert.Equal("booth", p.Route);
        Assert.Equal(1, p.StepN);
        Assert.Equal(3, p.StepOf);
        Assert.Equal("Make Listing + paste search + pick + Confirm (clipboard = name)", p.What);
        Assert.Null(p.Account);
    }

    [Fact]
    public void BracketTag_IsTheAccount()
    {
        var p = MacroName.Parse("PLZ | booth 1/3 | Make Listing + paste search + pick + Confirm [egg tab] (clipboard = name)");

        Assert.Equal("PLZ", p.Group);
        Assert.Equal("booth", p.Route);
        Assert.Equal(1, p.StepN);
        Assert.Equal("egg tab", p.Account);
    }

    [Fact]
    public void AnyGroup_WithRouteStep()
    {
        var p = MacroName.Parse("ANY | trade 2/7 | list search: ELeonDog (rewritten per run)");

        Assert.Equal("ANY", p.Group);
        Assert.Equal("trade", p.Route);
        Assert.Equal(2, p.StepN);
        Assert.Equal(7, p.StepOf);
        Assert.Equal("list search: ELeonDog (rewritten per run)", p.What);
    }

    [Fact]
    public void TwoSegments_HaveNoRoute_AndWhatIsTheSecond()
    {
        var p = MacroName.Parse("ANY | move W 250ms");

        Assert.Equal("ANY", p.Group);
        Assert.Null(p.Route);
        Assert.Null(p.StepN);
        Assert.Null(p.StepOf);
        Assert.Equal("move W 250ms", p.What);
    }

    [Fact]
    public void TwoSegments_WithParensAndCommas()
    {
        var p = MacroName.Parse("PLZ | booth step-away (S 400, from E spot)");

        Assert.Equal("PLZ", p.Group);
        Assert.Null(p.Route);
        Assert.Equal("booth step-away (S 400, from E spot)", p.What);
    }

    [Fact]
    public void MultiWordWhere_AndAccountAmongNotes()
    {
        var p = MacroName.Parse("HW Z4 | egg 3/6 | walk to boss pad + fight, tap ~11s [CECPapa] (from button arrival)");

        Assert.Equal("HW Z4", p.Group);
        Assert.Equal("egg", p.Route);
        Assert.Equal(3, p.StepN);
        Assert.Equal(6, p.StepOf);
        Assert.Equal("CECPapa", p.Account);
    }

    [Fact]
    public void CombinedStep_TakesTheFirstNumber()
    {
        var p = MacroName.Parse("HW Z4 | egg 2/6 + 4/6 | teleport to Z4 + camera top-down (DIRECT)");

        Assert.Equal("egg", p.Route);
        Assert.Equal(2, p.StepN);
        Assert.Equal(6, p.StepOf);
    }

    [Fact]
    public void RouteWithoutStep_KeepsTheWholeRoute()
    {
        var p = MacroName.Parse("HW ANY | pumpkin route | any zone (not Z1) -> EDGE of the Giant Pumpkin ring");

        Assert.Equal("HW ANY", p.Group);
        Assert.Equal("pumpkin route", p.Route);
        Assert.Null(p.StepN);
        Assert.Null(p.StepOf);
        Assert.Equal("any zone (not Z1) -> EDGE of the Giant Pumpkin ring", p.What);
    }

    [Fact]
    public void ExtraSeparators_StayInWhat()
    {
        var p = MacroName.Parse("PLZ | booth 2/3 | paste price | then wait");

        Assert.Equal("booth", p.Route);
        Assert.Equal("paste price | then wait", p.What);
    }

    [Fact]
    public void PlainName_IsUngrouped()
    {
        var p = MacroName.Parse("Crystal");

        Assert.Equal(MacroName.UngroupedGroup, p.Group);
        Assert.Null(p.Where);
        Assert.Null(p.Route);
        Assert.Equal("Crystal", p.What);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlank_IsUngrouped(string? name)
    {
        var p = MacroName.Parse(name);

        Assert.Equal(MacroName.UngroupedGroup, p.Group);
        Assert.Null(p.Where);
        Assert.Equal(string.Empty, p.What);
    }

    [Fact]
    public void BlankWhere_IsUngrouped()
    {
        var p = MacroName.Parse(" | jump");

        Assert.Equal(MacroName.UngroupedGroup, p.Group);
    }

    [Fact]
    public void PipeWithoutSpaces_IsNotASeparator()
    {
        var p = MacroName.Parse("a|b");

        Assert.Equal(MacroName.UngroupedGroup, p.Group);
        Assert.Equal("a|b", p.What);
    }
}
