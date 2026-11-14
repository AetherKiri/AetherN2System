using System.Linq;
using NitroSharp.Common;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using Veldrid;
using Xunit;

namespace NitroSharp.Tests;

public sealed class MenuHitboxQueryTests
{
    [Fact]
    public void ClearingAndReusingAnExpandedListDoesNotRestorePreviousItems()
    {
        var list = new SmallList<int>();
        for (int i = 0; i < 16; i++) list.Add(i);
        list.Clear();
        for (int i = 0; i < 16; i++) list.Add(i + 100);
        Assert.Equal(Enumerable.Range(100, 16), list.AsSpan().ToArray());
    }

    [Fact]
    public void ShrinkingAnExpandedListRetainsItsFirstItemsAndCanGrowAgain()
    {
        var list = new SmallList<int>();
        for (int i = 0; i < 8; i++) list.Add(i);
        for (int i = 7; i >= 4; i--) list.Remove(i);
        Assert.Equal(new[] { 0, 1, 2, 3 }, list.AsSpan().ToArray());
        for (int i = 4; i < 8; i++) list.Add(i + 100);
        Assert.Equal(new[] { 0, 1, 2, 3, 104, 105, 106, 107 }, list.AsSpan().ToArray());
    }

    [Fact]
    public void HidingManyMenuHitSpritesDoesNotDisableTheirParentChoices()
    {
        var context = new TestContext();
        var world = new World(context.MainProcess, context.MainThread);
        var choices = new ChoiceEntity[16];
        for (int i = 0; i < choices.Length; i++)
        {
            ChoiceEntity choice = choices[i] = world.AddEntity(new ChoiceEntity(EntityName.Parse("menu" + i), context.MainThread));
            var usual = world.AddEntity(new MouseStateEntity(EntityName.Parse("MouseUsual"), choice));
            world.AddEntity(new SolidColorRect(EntityName.Parse("hit"), usual, 1, RgbaFloat.Red,
                new Size<DesignPixel>(220, 20)));
        }
        Entity[] hits = world.Query(EntityQuery.Parse("menu*/MouseUsual/hit")).AsSpan().ToArray();
        Assert.Equal(16, hits.Length);
        Assert.All(hits, entity => Assert.True(entity is SolidColorRect));
        foreach (Entity hit in hits) Assert.True(hit.Request(NsEntityAction.Disable));
        Assert.All(choices, choice => Assert.True(choice.IsClickable));
        Assert.All(choices, choice => Assert.True(choice.GetHitTarget()!.IsHidden));
        world.BeginFrame();
        world.DisposeAll();
    }
}
