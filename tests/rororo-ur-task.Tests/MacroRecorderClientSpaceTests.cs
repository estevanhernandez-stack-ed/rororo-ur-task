using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MacroRecorderClientSpaceTests
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;

    [Fact]
    public void BuildMouseEvent_WithOrigin_RecordsClientRelative()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_LBUTTONDOWN, 150, 260, 0u, 10, (100, 200));
        Assert.NotNull(evt);
        Assert.Equal(MacroEventKind.MouseDown, evt!.Kind);
        Assert.Equal(50, evt.X);
        Assert.Equal(60, evt.Y);
        Assert.Equal(1, evt.MouseButton);
    }

    [Fact]
    public void BuildMouseEvent_WithoutOrigin_RecordsAbsolute()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_LBUTTONDOWN, 150, 260, 0u, 10, null);
        Assert.Equal(150, evt!.X);
        Assert.Equal(260, evt.Y);
    }

    [Fact]
    public void BuildMouseEvent_WheelDelta_SurvivesConversion()
    {
        // mouseData high word = signed wheel delta (120 = one notch up).
        var evt = MacroRecorder.BuildMouseEvent(WM_MOUSEWHEEL, 150, 260, 120u << 16, 10, (100, 200));
        Assert.Equal(MacroEventKind.MouseWheel, evt!.Kind);
        Assert.Equal(120, evt.WheelDelta);
        Assert.Equal(50, evt.X);
    }

    [Fact]
    public void BuildMouseEvent_XButton_MapsButtonId()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_XBUTTONDOWN, 0, 0, 2u << 16, 10, null);
        Assert.Equal(5, evt!.MouseButton); // X2
    }

    [Fact]
    public void BuildMouseEvent_MouseMove_MapsKind()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_MOUSEMOVE, 10, 20, 0u, 5, (10, 20));
        Assert.Equal(MacroEventKind.MouseMove, evt!.Kind);
        Assert.Equal(0, evt.X);
        Assert.Equal(0, evt.Y);
    }

    [Fact]
    public void BuildMouseEvent_UnknownMessage_ReturnsNull()
    {
        Assert.Null(MacroRecorder.BuildMouseEvent(0x9999, 0, 0, 0u, 0, null));
    }
}
