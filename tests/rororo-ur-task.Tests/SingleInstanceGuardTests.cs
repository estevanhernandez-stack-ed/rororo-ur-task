using System;
using System.Linq;
using System.Threading;
using Labs626.UrTask;
using Xunit;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The guard the host's author guide opens with, and this app shipped without: one copy per
/// session. The bug it closes was observed live (v1.19 C2, RoRoRo finding F-096): a manual launch
/// raced the host's autostart, the losing copy could not register the global hotkeys, showed its
/// full window and reported "Not connected to RoRoRo" with 0 macros — indistinguishable from a
/// genuine host failure. These tests pin the two things a process-level guard CAN have pinned in
/// a unit test: the name is well-formed (including the guide's carriage-return trap), and the
/// createdNew signal the guard branches on actually distinguishes first from second.
/// </summary>
public class SingleInstanceGuardTests
{
    [Fact]
    public void TheMutexName_IsSessionLocal_KeyedOnThePluginId_AndCarriesNoControlCharacters()
    {
        var name = App.SingleInstanceMutexName;

        // Local\ scopes to the user's session — two Windows users each get their own copy.
        Assert.StartsWith(@"Local\", name, StringComparison.Ordinal);

        // Keyed on the plugin id so two DIFFERENT plugins never collide, and so the constant here
        // cannot silently drift from the identity the host knows this plugin by.
        Assert.Contains(PluginRuntime.PluginId, name, StringComparison.Ordinal);

        // The guide's trap, asserted: in an interpolated string \r is a carriage return, so a name
        // built as $"Local\rororo-…" compiles, never collides, and never guards anything. A control
        // character anywhere in this name means that bug has been reintroduced.
        Assert.DoesNotContain(name, c => char.IsControl(c));
    }

    [Fact]
    public void TheSecondAcquire_SeesItIsNotFirst()
    {
        // A throwaway name so this test never collides with a real running Ur Task (or a parallel
        // test run) — the semantics under test are the kernel object's, not the constant's.
        var name = $@"Local\rororo-plugin-test-{Guid.NewGuid():N}";

        using var first = new Mutex(initiallyOwned: true, name, out var firstIsFirst);
        using var second = new Mutex(initiallyOwned: true, name, out var secondIsFirst);

        Assert.True(firstIsFirst);
        Assert.False(secondIsFirst); // the signal App.OnStartup's guard branches on
    }
}
