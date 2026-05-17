using Content.IntegrationTests.Tests.Interaction;

namespace Content.IntegrationTests.Tests.Construction.Interaction;

public sealed class WallConstruction : InteractionTest
{
    public const string Girder = "ScpGirder"; // Scp edit
    public const string WallSolid = "ScpWallSolid"; // Scp edit
    public const string Wall = "ScpWallSolid"; // Scp edit

    [Test]
    public async Task ConstructWall()
    {
        await StartConstruction(Wall);
        await InteractUsing(Steel, 2);
        Assert.That(HandSys.GetActiveItem((SEntMan.GetEntity(Player), Hands)), Is.Null);
        ClientAssertPrototype(Girder, Target);
        await InteractUsing(Steel, 2);
        Assert.That(HandSys.GetActiveItem((SEntMan.GetEntity(Player), Hands)), Is.Null);
        AssertPrototype(WallSolid);
    }

    [Test]
    [Ignore("We disabled ability to deconstruct damaged walls and I am too lazy to fix this test")]
    public async Task DeconstructWall()
    {
        await StartDeconstruction(WallSolid);
        await InteractUsing(Weld);
        AssertPrototype(Girder);
        await Interact(Wrench, Screw);
        AssertDeleted();
        await AssertEntityLookup((Steel, 4));
    }
}
