using System.Windows;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>What was on the clipboard before a paste. <see cref="HasOther"/>: no text, but something
/// else (an image, files), which a paste step cannot put back. A read that failed also says HasOther,
/// so the step never claims to have restored what it could not see.</summary>
internal sealed record ClipboardContents(string? Text, bool HasOther);

/// <summary>The clipboard as a paste step needs it. A seam so StepRunner tests never touch the real one.</summary>
internal interface IStepClipboard
{
    ClipboardContents Read();
    bool SetText(string text);
    bool Clear();
}

/// <summary>
/// The real clipboard, through WPF on a short-lived STA thread: the clipboard is OLE and needs one,
/// and steps run on the thread pool. <c>copy: true</c> flushes the data, so it stays after that
/// thread ends. Every call returns rather than throws: a busy clipboard (another app holding it open)
/// is a false the step reports, never an exception mid-playback.
/// </summary>
internal sealed class StaStepClipboard : IStepClipboard
{
    public static readonly StaStepClipboard Instance = new();

    // WPF retries a busy clipboard on its own for about a second; this only caps a wedged thread.
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(3);

    public ClipboardContents Read()
        => OnSta(() => Clipboard.ContainsText()
               ? new ClipboardContents(Clipboard.GetText(), HasOther: false)
               : new ClipboardContents(null, HasOther: Clipboard.GetDataObject()?.GetFormats().Length > 0))
           ?? new ClipboardContents(null, HasOther: true);

    public bool SetText(string text) => OnSta(() => { Clipboard.SetDataObject(text, copy: true); return true; });

    public bool Clear() => OnSta(() => { Clipboard.Clear(); return true; });

    private static T? OnSta<T>(Func<T> call)
    {
        T? result = default;
        var thread = new Thread(() =>
        {
            try { result = call(); }
            catch { result = default; }
        })
        { IsBackground = true, Name = "RoRoRoUrTask-PasteClipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(CallLimit) ? result : default;
    }
}
