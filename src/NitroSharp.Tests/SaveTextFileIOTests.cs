using System;
using System.IO;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.Tests;

public sealed class SaveTextFileIOTests
{
    [Fact]
    public void FileDispatchUsesTheReturnValueAndOptionalAppendFlag()
    {
        var dispatcher = new BuiltInFunctionDispatcher([], []);
        var builtins = new FileBuiltins();
        ConstantValue? result = dispatcher.Dispatch(builtins, BuiltInFunction.ReadFile, [ConstantValue.String("0001/cmt.npf")]);
        Assert.Equal("saved dialogue", result!.Value.AsString());
        Assert.Equal("0001/cmt.npf", builtins.ReadPath);
        dispatcher.Dispatch(builtins, BuiltInFunction.WriteFile, [ConstantValue.String("0001/cmt.npf"), ConstantValue.String("new dialogue")]);
        Assert.Equal(("0001/cmt.npf", "new dialogue", false), builtins.Written);
        dispatcher.Dispatch(builtins, BuiltInFunction.WriteFile, [ConstantValue.String("0001/cmt.npf"), ConstantValue.String("more"), ConstantValue.True]);
        Assert.Equal(("0001/cmt.npf", "more", true), builtins.Written);
    }

    private sealed class FileBuiltins : BuiltInFunctions
    {
        public string? ReadPath;
        public (string, string, bool) Written;
        public override string ReadFile(string path) { ReadPath = path; return "saved dialogue"; }
        public override void WriteFile(string path, string text, bool append = false) => Written = (path, text, append);
    }

    [Fact]
    public void MetadataSurvivesOverwriteAppendAndFreshReads()
    {
        string root = Path.Combine(Path.GetTempPath(), "n2-save-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = SaveTextFileIO.ResolvePath(root, "0001\\cmt.npf")!;
            Assert.Equal("", SaveTextFileIO.Read(path));
            Assert.False(SaveTextFileIO.Exists(root, "0001/cmt.npf"));
            SaveTextFileIO.Write(path, "村正 · 邪念篇", append: false);
            Assert.True(SaveTextFileIO.Exists(root, "0001/cmt.npf"));
            Assert.True(SaveTextFileIO.Exists(root, "0001\\cmt.npf"));
            Assert.True(SaveTextFileIO.Exists(root, path));
            Assert.Equal("村正 · 邪念篇", SaveTextFileIO.Read(path));
            SaveTextFileIO.Write(path, "\n次の台詞", append: true);
            Assert.Equal("村正 · 邪念篇\n次の台詞", SaveTextFileIO.Read(path));
            SaveTextFileIO.Write(path, "刃鳴散らす", append: false);
            Assert.Equal("刃鳴散らす", SaveTextFileIO.Read(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WritesRemainInsideSavedata()
    {
        string root = Path.Combine(Path.GetTempPath(), "n2-savedata");
        Assert.Null(SaveTextFileIO.ResolvePath(root, "../other.dat"));
        Assert.Null(SaveTextFileIO.ResolvePath(root, "..\\other.dat"));
        Assert.Null(SaveTextFileIO.ResolvePath(root, root + "-other/data"));
        Assert.Equal(Path.Combine(root, "0001", "date.npf"), SaveTextFileIO.ResolvePath(root, Path.Combine(root, "0001", "date.npf")));
    }
}
