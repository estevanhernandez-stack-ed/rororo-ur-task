using System;
using System.Linq;
using System.Reflection;
using Labs626.UrTask.Theming;

namespace Labs626.UrTask.Tests;

/// <summary>
/// What is left of <see cref="HostThemeReader"/> after the host started sending its palette.
/// <para>
/// This file used to hold eight tests, seven of which covered machinery that no longer exists:
/// reading RoRoRo's <c>settings.json</c> for an active theme id, parsing <c>themes\&lt;id&gt;.json</c>
/// off disk, and resolving an id against a hand-copied mirror of RoRoRo's built-in themes. That
/// mirror is the reason flatline never reached this plugin — a built-in theme lives in RoRoRo's
/// code and is never written to disk, so an id absent from the copy fell through to Brand. F-091.
/// </para>
/// <para>
/// Deleting the tests with the mechanism is the point, not collateral. A test suite that still
/// covered the reader would report healthy coverage of code the plugin no longer runs.
/// </para>
/// </summary>
public class HostThemeReaderTests
{
    [Fact]
    public void BlendTowards_TintsDeterministically()
    {
        // Brand RowBg toward brand White at 4% — the RowHoverBrush derivation. Survives the feed
        // because hover is derived FROM a palette rather than carried in one; the host has no
        // hover slot to send.
        var hover = HostThemeReader.BlendTowards("#15263A", "#FFFFFF", 0.04);
        Assert.Equal("#1E2F42", hover);

        Assert.Null(HostThemeReader.BlendTowards("nope", "#FFFFFF", 0.04));
        Assert.Null(HostThemeReader.BlendTowards("#15263A", "junk", 0.04));
    }

    [Fact]
    public void Brand_IsTheStandaloneFallback_AndStillParses()
    {
        // The one hardcoded palette that survives, and it is not a mirror of anything: it is the
        // colour a window renders in before any host connection exists. Ur Task runs standalone,
        // so "no host" is a supported state rather than an error path.
        var brand = HostThemeReader.Brand;

        foreach (var hex in new[]
        {
            brand.Bg, brand.Cyan, brand.Magenta, brand.White,
            brand.MutedText, brand.Divider, brand.RowBg,
        })
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        }
    }

    /// <summary>
    /// The regression guard for the defect this cycle removed.
    /// <para>
    /// The failure was not "flatline is missing from the table" — it was that a table existed at
    /// all. Adding flatline to it would have fixed the symptom and left the sixth built-in theme
    /// broken in exactly the same way. So the thing worth asserting is the absence of the
    /// mechanism: no palette constants beyond the single standalone fallback, and no reader for
    /// RoRoRo's storage.
    /// </para>
    /// </summary>
    [Fact]
    public void TheMirrorIsGone_AndSoIsEveryWayOfReadingTheHostsStorage()
    {
        var type = typeof(HostThemeReader);

        var palettes = type
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(HostThemePalette)
                     || f.FieldType == typeof(HostThemePalette[]))
            .Select(f => f.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(HostThemeReader.Brand) }, palettes);

        foreach (var gone in new[] { "ResolveActive", "ParseThemeFile", "ReadActiveThemeId", "DefaultHostFolder" })
        {
            Assert.Null(type.GetMethod(gone, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
        }
    }
}
