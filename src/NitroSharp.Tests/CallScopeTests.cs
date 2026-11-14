using NitroSharp.NsScript;
using NitroSharp.Text;
using Xunit;

namespace NitroSharp.Tests;

public sealed class CallScopeTests
{
    [Fact]
    public void NestedCallsQueryTheirOwnNamespaceAndRestoreTheCaller()
    {
        var process = new Process(EntityName.Parse("process"), null, new FontSettings());
        var thread = new Thread(EntityName.Parse("main"), process, default, isMain: true);
        var world = new World(process, thread);
        var caller = world.AddEntity(new BlankEntity(EntityName.Parse("background"), thread));
        thread.EnterCallScope();
        Assert.True(world.TryResolvePath(EntityPath.Parse("background"), out var name, out var parent));
        Assert.Same(thread.EntityContext, parent);
        var callee = world.AddEntity(new BlankEntity(name, parent));
        Assert.Same(callee, world.Query(EntityQuery.Parse("*"))[0]);
        Assert.Single(world.Query(EntityQuery.Parse("*")).AsSpan().ToArray());
        thread.EnterCallScope();
        Assert.Empty(world.Query(EntityQuery.Parse("*")).AsSpan().ToArray());
        thread.LeaveCallScope();
        Assert.Same(callee, world.Get(EntityPath.Parse("background")));
        thread.LeaveCallScope();
        Assert.Same(caller, world.Get(EntityPath.Parse("background")));
        Assert.Single(world.Query(EntityQuery.Parse("*")).AsSpan().ToArray());
        world.BeginFrame();
        world.DisposeAll();
    }

    [Fact]
    public void DeletingAParentImmediatelyInvalidatesItsChildAliases()
    {
        var process = new Process(EntityName.Parse("process"), null, new FontSettings());
        var thread = new Thread(EntityName.Parse("main"), process, default, isMain: true);
        var world = new World(process, thread);
        var parent = world.AddEntity(new BlankEntity(EntityName.Parse("window"), thread));
        var child = world.AddEntity(new BlankEntity(EntityName.Parse("image"), parent));
        child.SetAlias(EntityAlias.Parse("image"));
        Assert.Same(child, world.Query(EntityQuery.Parse("@image"))[0]);
        world.DestroyEntity(parent);
        Assert.Empty(world.Query(EntityQuery.Parse("@image")).AsSpan().ToArray());
        Assert.True(child.IsPendingDestruction);
        world.DestroyEntity(child);
        world.BeginFrame();
        Assert.False(child.IsEnabled);
        world.DisposeAll();
    }

    [Fact]
    public void LockedCalleeEntitiesSurviveReturnUntilExplicitlyDeleted()
    {
        var process = new Process(EntityName.Parse("process"), null, new FontSettings());
        var thread = new Thread(EntityName.Parse("main"), process, default, isMain: true);
        var world = new World(process, thread);
        thread.EnterCallScope();
        var retained = world.AddEntity(new BlankEntity(EntityName.Parse("retained"), thread.EntityContext));
        retained.Request(NitroSharp.NsScript.NsEntityAction.Lock);
        retained.SetAlias(EntityAlias.Parse("retained"));
        var ordinary = world.AddEntity(new BlankEntity(EntityName.Parse("temporary"), thread.EntityContext));
        ordinary.SetAlias(EntityAlias.Parse("temporary"));
        thread.LeaveCallScope();
        Assert.Empty(world.Query(EntityQuery.Parse("*")).AsSpan().ToArray());
        Assert.Same(retained, world.Query(EntityQuery.Parse("@retained"))[0]);
        Assert.Empty(world.Query(EntityQuery.Parse("@temporary")).AsSpan().ToArray());
        world.BeginFrame();
        Assert.True(retained.IsLocked);
        world.DisposeAll();
    }
}
