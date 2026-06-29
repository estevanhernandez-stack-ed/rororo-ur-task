using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

public class UserPreferencesBridgeToggleTests
{
    [Fact]
    public void AcceptPluginRunRequests_DefaultsOn()
        => Assert.True(new UserPreferences().AcceptPluginRunRequests);
}
