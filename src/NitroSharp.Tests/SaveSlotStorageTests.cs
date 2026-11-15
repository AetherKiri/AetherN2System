using System;
using System.IO;
using NitroSharp.Saving;
using Xunit;

namespace NitroSharp.Tests;

public sealed class SaveSlotStorageTests
{
    [Fact]
    public void DifferentGamesAndProfilesKeepSeparateSlotsInsideSharedHostDirectory()
    {
        string root = Path.GetTempPath();
        string first = GameSaveManager.SaveDirectory(root, "game-one", "Default");
        Assert.Equal(first, GameSaveManager.SaveDirectory(root, "game-one", "Default"));
        Assert.NotEqual(first, GameSaveManager.SaveDirectory(root, "game-two", "Default"));
        Assert.NotEqual(first, GameSaveManager.SaveDirectory(root, "game-one", "Translation"));
        Assert.Equal(Path.Combine(root, "savedata"), Path.GetDirectoryName(first));
    }

    [Fact]
    public void CheckpointsSurviveReopeningAndShorterOverwriteWithoutTouchingCommonData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "n2-save-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new SaveSlotStorage(directory, "game", "profile");
            storage.Write(0, [9, 8, 7]);
            storage.Write(1, new byte[8192]);
            storage.Write(1, [1, 2, 3]);
            storage = new SaveSlotStorage(directory, "game", "profile");
            Assert.Equal(new byte[] { 1, 2, 3 }, storage.Read(1));
            Assert.Equal(new byte[] { 9, 8, 7 }, storage.Read(0));
            Assert.NotNull(storage.GetDate(1));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
            storage.Delete(1);
            Assert.False(storage.Exists(1));
            Assert.True(storage.Exists(0));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DamagedTruncatedAndForeignSavesAreRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), "n2-save-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new SaveSlotStorage(directory, "game", "profile");
            storage.Write(1, [1, 2, 3]);
            var foreign = new SaveSlotStorage(directory, "other-game", "profile");
            Assert.False(foreign.Exists(1));
            Assert.Throws<InvalidDataException>(() => foreign.Read(1));
            string file = Path.Combine(storage.SlotDirectory(1), "0001.sav");
            byte[] bytes = File.ReadAllBytes(file);
            bytes[^1] ^= 1;
            File.WriteAllBytes(file, bytes);
            Assert.Throws<InvalidDataException>(() => storage.Read(1));
            File.WriteAllBytes(file, bytes[..^1]);
            Assert.Throws<InvalidDataException>(() => storage.Read(1));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
