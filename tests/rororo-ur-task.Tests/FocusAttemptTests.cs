using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class FocusAttemptTests
{
    [Fact]
    public void Tuple_conversion_keeps_old_rigs_meaning()
    {
        FocusAttempt ok = (true, null);
        Assert.True(ok.Ok);
        Assert.Null(ok.Cause);
        Assert.Null(ok.Detail);

        FocusAttempt refused = (false, "x");
        Assert.False(refused.Ok);
        Assert.Equal(MissCause.FocusRefused, refused.Cause);
        Assert.Equal("x", refused.Detail);
    }
}
