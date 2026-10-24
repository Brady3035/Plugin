using Moonfin.Romm;
using Xunit;

namespace Moonfin.Romm.Tests;

/// <summary>
/// The inventory is what keeps a RomM outage from looking like an empty library, which would
/// make artwork cleanup delete everything the library had cached.
/// </summary>
public class RommInventoryTests
{
    [Fact]
    public void FirstReadWaitsForTheFirstLoad()
    {
        var inventory = new RommInventory(_ => Task.FromResult(Snapshot("a")));

        var snapshot = inventory.Get();

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Games);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheLastGoodSnapshot()
    {
        var fail = false;
        var inventory = new RommInventory(
            _ => fail ? throw new HttpRequestException("RomM is down") : Task.FromResult(Snapshot("a")),
            maxAge: TimeSpan.Zero,
            retryAfterFailure: TimeSpan.Zero);
        var first = inventory.Get();

        fail = true;
        await inventory.StartRefresh()!;

        Assert.Same(first, inventory.Current);
        Assert.Equal("RomM is down", inventory.LastError);
    }

    [Fact]
    public async Task ChangedFiresForNewContentOnly()
    {
        var version = "a";
        var changes = 0;
        var inventory = new RommInventory(_ => Task.FromResult(Snapshot(version)), maxAge: TimeSpan.Zero);
        inventory.Changed += () => changes++;

        await inventory.StartRefresh()!;
        await inventory.StartRefresh()!;
        version = "b";
        await inventory.StartRefresh()!;

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task ALoadThatJustFailedIsNotRetriedRightAway()
    {
        var loads = 0;
        var inventory = new RommInventory(
            _ =>
            {
                loads++;
                throw new HttpRequestException("RomM is down");
            },
            retryAfterFailure: TimeSpan.FromMinutes(1),
            firstLoadWait: TimeSpan.FromSeconds(5));

        Assert.Null(inventory.Get());
        Assert.Null(inventory.Get());
        Assert.Null(inventory.StartRefresh());
        await Task.Yield();

        Assert.Equal(1, loads);
    }

    private static RommSnapshot Snapshot(string updatedAt)
    {
        var platform = new RommPlatform { Id = 10, SystemId = "SNES", Name = "SNES", Core = "snes" };
        var game = new RommGame { Id = 1, PlatformId = 10, UpdatedAt = updatedAt, Rom = new RommFile { Id = 2 } };
        return new RommSnapshot(new[] { platform }, new[] { game }, DateTime.UtcNow);
    }
}
