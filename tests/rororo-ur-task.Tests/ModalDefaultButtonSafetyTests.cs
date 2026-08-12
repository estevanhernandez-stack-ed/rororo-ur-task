using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace rororo_ur_task.Tests;

/// <summary>
/// Enter must not perform the consequential action.
///
/// <para>Found 2026-08-11 while bringing this repo up to the host's standards. All three dialogs
/// put <c>IsDefault="True"</c> on the affirmative button and none wired <c>IsCancel</c> at all,
/// so <b>Enter deleted a macro</b>, Enter dismissed the multi-window warning by doing the very
/// thing it warned about, and Esc did nothing anywhere — the dialogs could not be dismissed from
/// the keyboard.</para>
///
/// <para>The host repo learned this the expensive way: <c>RobloxAlreadyRunningWindow</c> shipped
/// with <c>IsDefault</c> on "Close Roblox for me" on a modal that appears unprompted at startup,
/// so a reflexive Enter force-closed every running client. This suite is that lesson, ported.</para>
///
/// <para>These parse the shipped XAML rather than instantiating a Window, which would need an STA
/// dispatcher and a full WPF app context. The property under test lives in the markup, so the
/// markup is what we assert on — and it keeps these in the STANDALONE suite, which is the one CI
/// runs on every PR.</para>
/// </summary>
public class ModalDefaultButtonSafetyTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// Buttons that do something consequential when clicked. Any of these carrying IsDefault is a
    /// bug, not a preference.
    /// </summary>
    public static TheoryData<string, string> ConsequentialButtons() => new()
    {
        { "DeleteMacroConfirmDialog.xaml", "DELETE" },  // destroys a recording the user made
        { "MultiWindowConfirmDialog.xaml", "PLAY" },    // drives live game clients
    };

    [Theory]
    [MemberData(nameof(ConsequentialButtons))]
    public void ConsequentialButton_IsNeverTheEnterKeyDefault(string modalFile, string label)
    {
        var button = FindButton(modalFile, label);
        Assert.True(button is not null, $"'{label}' not found in {modalFile} — did the label change?");

        Assert.False(
            IsDefault(button!),
            $"'{label}' in {modalFile} is the Enter-key default. A confirmation whose default IS "
            + "the action it confirms is not a confirmation.");
    }

    [Fact]
    public void DeleteConfirm_DefaultsToCancel()
        => Assert.Equal("CANCEL", DefaultButtonLabel("DeleteMacroConfirmDialog.xaml"));

    [Fact]
    public void MultiWindowConfirm_DefaultsToCancel()
        => Assert.Equal("CANCEL", DefaultButtonLabel("MultiWindowConfirmDialog.xaml"));

    /// <summary>
    /// RENAME is deliberately the Enter default, and is NOT in
    /// <see cref="ConsequentialButtons"/>. This is a text-entry dialog: you type a name and press
    /// Enter, which is correct and expected. The rule is "Enter must not perform the consequential
    /// action", not "Enter must never do anything" — and a rename is trivially reversible by
    /// renaming again. Asserted explicitly rather than left off the list silently, so a future
    /// reader sees the reasoning instead of assuming an oversight.
    /// </summary>
    [Fact]
    public void RenameDialog_DefaultsToRename_DeliberatelyNotAConfirmation()
        => Assert.Equal("RENAME", DefaultButtonLabel("RenameMacroDialog.xaml"));

    [Theory]
    [InlineData("DeleteMacroConfirmDialog.xaml")]
    [InlineData("MultiWindowConfirmDialog.xaml")]
    [InlineData("RenameMacroDialog.xaml")]
    public void EachModal_HasExactlyOneDefaultButton(string modalFile)
        => Assert.Single(Buttons(modalFile), IsDefault);

    /// <summary>
    /// Every dialog needs an Esc route. All three shipped without one — a modal you cannot dismiss
    /// from the keyboard is a dead end for anyone not reaching for the mouse.
    /// </summary>
    [Theory]
    [InlineData("DeleteMacroConfirmDialog.xaml")]
    [InlineData("MultiWindowConfirmDialog.xaml")]
    [InlineData("RenameMacroDialog.xaml")]
    public void EachModal_CanBeDismissedWithEscape(string modalFile)
        => Assert.Single(Buttons(modalFile), IsCancel);

    /// <summary>
    /// THE TEST THAT WOULD HAVE CAUGHT IT. Every one of these dialogs handles Enter in C# — a
    /// window-level (or TextBox-level) KeyDown that sets DialogResult directly and closes. That
    /// handler runs before the markup's <c>IsDefault</c> gets any say, so the XAML assertions above
    /// can pass in full while Enter still performs the consequential action.
    /// <para>
    /// Which is exactly what happened on 2026-08-11: IsDefault was moved to CANCEL, the markup tests
    /// went green, and Enter still deleted the macro. Verified by hand in a live smoke, not by this
    /// suite. Asserting on markup for behaviour that markup does not control is how a test tells you
    /// something true about the wrong thing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DeleteMacroConfirmDialog.xaml.cs")]
    [InlineData("MultiWindowConfirmDialog.xaml.cs")]
    public void ConfirmDialogs_DoNotConfirmOnEnterInCodeBehind(string codeBehind)
    {
        var source = File.ReadAllText(SourcePath(codeBehind));

        // Find the Enter branch and check what DialogResult it sets. `true` is the affirmative,
        // consequential outcome in both of these dialogs.
        var enterLine = source
            .Split('\n')
            .FirstOrDefault(l => l.Contains("Key.Enter") && l.Contains("DialogResult"));

        Assert.True(enterLine is not null,
            $"{codeBehind} has no Enter branch setting DialogResult — if the handler was removed, "
            + "delete this assertion deliberately rather than letting it pass vacuously.");

        Assert.DoesNotContain("DialogResult = true", enterLine!);
    }

    /// <summary>
    /// Rename is the deliberate exception, and it is asserted rather than omitted. Its Enter handler
    /// lives on the TextBox and SHOULD submit — you type a name and press Enter. A rename is also
    /// reversible by renaming again.
    /// </summary>
    [Fact]
    public void RenameDialog_DoesSubmitOnEnter_Deliberately()
    {
        var source = File.ReadAllText(SourcePath("RenameMacroDialog.xaml.cs"));
        var enterLine = source.Split('\n').FirstOrDefault(l => l.Contains("Key.Enter") && l.Contains("DialogResult"));

        Assert.True(enterLine is not null, "RenameMacroDialog lost its Enter-to-submit handler.");
        Assert.Contains("DialogResult = true", enterLine!);
    }

    private static string SourcePath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rororo-ur-task.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.True(dir is not null, "Could not find the repo root from the test binary.");

        var path = Path.Combine(dir!.FullName, "src", "UI", file);
        Assert.True(File.Exists(path), $"{file} not found at {path} — moved or renamed?");
        return path;
    }

    private static bool IsDefault(XElement button)
        => string.Equals((string?)button.Attribute("IsDefault"), "True", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancel(XElement button)
        => string.Equals((string?)button.Attribute("IsCancel"), "True", StringComparison.OrdinalIgnoreCase);

    private static string DefaultButtonLabel(string modalFile)
        => (string?)Buttons(modalFile).Single(IsDefault).Attribute("Content")
           ?? throw new InvalidOperationException($"default button in {modalFile} has no Content");

    private static XElement? FindButton(string modalFile, string content)
        => Buttons(modalFile).FirstOrDefault(b => (string?)b.Attribute("Content") == content);

    private static IEnumerable<XElement> Buttons(string modalFile)
        => XDocument.Load(ModalPath(modalFile)).Descendants(Presentation + "Button");

    /// <summary>
    /// Reads the dialog from source rather than a copy beside the test binary.
    /// <para>
    /// Copying was the obvious approach and it broke the build: this is a WPF project whose Page
    /// glob is <c>**/*.xaml</c>, so it re-compiled the copies out of <c>tests\bin\</c> as a second
    /// set of pages — duplicate <c>InitializeComponent</c> and <c>_contentLoaded</c> on every
    /// dialog. Walking up to the repo root avoids the whole class of problem and asserts on the
    /// file a human actually edits.
    /// </para>
    /// </summary>
    private static string ModalPath(string modalFile)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rororo-ur-task.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null,
            "Could not find the repo root (walked up from the test binary looking for "
            + "rororo-ur-task.csproj). This test reads dialog markup from src\\UI\\ directly.");

        var path = Path.Combine(dir!.FullName, "src", "UI", modalFile);
        Assert.True(File.Exists(path), $"{modalFile} not found at {path} — was the dialog moved or renamed?");
        return path;
    }
}
