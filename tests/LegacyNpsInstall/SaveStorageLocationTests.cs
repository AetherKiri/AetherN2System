using System;
using System.IO;
using AetherN2System;
using Xunit;

namespace NitroSharp.Tests;

public sealed class SaveStorageLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "n2-save-location-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "game");
    private string Writable => Path.Combine(_root, "app");
    private string Identity => SaveStorageLocation.Identity("product", "Default");
    private string Previous => Path.Combine(Writable, "savedata", Identity);
    private string Preferred => Path.Combine(Game, "savedata", "AetherN2System", Identity);

    public SaveStorageLocationTests()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Writable);
    }

    [Fact]
    public void WritableGameDirectoryIsPreferredAndStableWhenAppDirectoryChanges()
    {
        string first = SaveStorageLocation.Select(Game, Writable, "product", "Default");
        string second = SaveStorageLocation.Select(Game, Path.Combine(_root, "another-app"), "product", "Default");
        Assert.Equal(Preferred, first);
        Assert.Equal(first, second);
        Assert.Empty(Directory.GetFiles(first));
    }

    [Fact]
    public void ObstructedGameSavePathFallsBackWithoutChangingOriginalGameFiles()
    {
        File.WriteAllText(Path.Combine(Game, "savedata"), "original-game-file");
        string selected = SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Assert.Equal(Previous, selected);
        Assert.Equal("original-game-file", File.ReadAllText(Path.Combine(Game, "savedata")));
        Assert.Empty(Directory.GetFiles(selected));
    }

    [Fact]
    public void MigrationCopiesSupportedSavesAndSidecarsWithoutReplacingOrDeleting()
    {
        Write(Previous, "0001/0001.sav", "old-checkpoint");
        Write(Previous, "0001/date.npf", "old-date");
        Write(Previous, "0001/thum.npf", "old-thumbnail");
        Write(Previous, "common/val.npf", "common-flags");
        Write(Previous, "9998/9998.sav", "quick-save");
        Write(Previous, "0001/unrelated.txt", "unrelated");
        Write(Previous, "unrelated/other.sav", "unrelated");
        Write(Preferred, "0001/0001.sav", "new-checkpoint");
        Write(Preferred, "0001/thum.npf", "new-thumbnail");

        Assert.Equal(Preferred, SaveStorageLocation.Select(Game, Writable, "product", "Default"));

        Assert.Equal("new-checkpoint", File.ReadAllText(Path.Combine(Preferred, "0001/0001.sav")));
        Assert.False(File.Exists(Path.Combine(Preferred, "0001/date.npf")));
        Assert.Equal("new-thumbnail", File.ReadAllText(Path.Combine(Preferred, "0001/thum.npf")));
        Assert.Equal("common-flags", File.ReadAllText(Path.Combine(Preferred, "common/val.npf")));
        Assert.Equal("quick-save", File.ReadAllText(Path.Combine(Preferred, "9998/9998.sav")));
        Assert.False(File.Exists(Path.Combine(Preferred, "0001/unrelated.txt")));
        Assert.False(Directory.Exists(Path.Combine(Preferred, "unrelated")));
        Assert.Equal("old-checkpoint", File.ReadAllText(Path.Combine(Previous, "0001/0001.sav")));
        Assert.Single(Directory.GetFiles(Previous, ".migrated-to-game-*"));
        Assert.Empty(Directory.GetFiles(Preferred, ".migrated-to-game-*"));
    }

    [Fact]
    public void ExistingCommonCheckpointKeepsItsOwnMetadata()
    {
        Write(Previous, "common/val.npf", "old-flags");
        Write(Previous, "common/date.npf", "old-date");
        Write(Preferred, "common/val.npf", "new-flags");

        SaveStorageLocation.Select(Game, Writable, "product", "Default");

        Assert.Equal("new-flags", File.ReadAllText(Path.Combine(Preferred, "common/val.npf")));
        Assert.False(File.Exists(Path.Combine(Preferred, "common/date.npf")));
    }

    [Fact]
    public void DeletingASlotAfterMigrationDoesNotRestoreItOnNextLaunch()
    {
        Write(Previous, "0001/0001.sav", "old-checkpoint");
        SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Directory.Delete(Path.Combine(Preferred, "0001"), recursive: true);
        SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Assert.False(Directory.Exists(Path.Combine(Preferred, "0001")));
        Assert.True(File.Exists(Path.Combine(Previous, "0001/0001.sav")));
    }

    [Fact]
    public void DeletingTheWholeGameSaveDirectoryDoesNotRestoreOldAppSaves()
    {
        Write(Previous, "0001/0001.sav", "old-checkpoint");
        SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Directory.Delete(Path.Combine(Game, "savedata"), recursive: true);
        string selected = SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Assert.Equal(Preferred, selected);
        Assert.Empty(Directory.GetFileSystemEntries(selected));
        Assert.True(File.Exists(Path.Combine(Previous, "0001/0001.sav")));
    }

    [Fact]
    public void MigrationRunsOnlyOnceForEachTarget()
    {
        Write(Previous, "0001/0001.sav", "old-checkpoint");
        SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Write(Previous, "0002/0002.sav", "later-old-checkpoint");
        SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Assert.False(Directory.Exists(Path.Combine(Preferred, "0002")));
        string anotherGame = Path.Combine(_root, "another-game");
        string selected = SaveStorageLocation.Select(anotherGame, Writable, "product", "Default");
        Assert.True(File.Exists(Path.Combine(selected, "0002/0002.sav")));
        Assert.Equal(2, Directory.GetFiles(Previous, ".migrated-to-game-*").Length);
    }

    [Fact]
    public void MigrationSkipsSymbolicSlotDirectoriesAndFiles()
    {
        string outside = Path.Combine(_root, "outside");
        Write(outside, "0002.sav", "outside");
        Directory.CreateDirectory(Path.Combine(Previous, "0001"));
        Directory.CreateSymbolicLink(Path.Combine(Previous, "0002"), outside);
        File.CreateSymbolicLink(Path.Combine(Previous, "0001", "0001.sav"), Path.Combine(outside, "0002.sav"));
        string selected = SaveStorageLocation.Select(Game, Writable, "product", "Default");
        Assert.False(Directory.Exists(Path.Combine(selected, "0002")));
        Assert.False(File.Exists(Path.Combine(selected, "0001", "0001.sav")));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "0002.sav")));
    }

    private static void Write(string root, string relative, string value)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
