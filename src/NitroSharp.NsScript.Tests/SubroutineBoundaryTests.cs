using System;
using System.IO;
using System.Linq;
using System.Text;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.NsScript.Tests;

public sealed class SubroutineBoundaryTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void LastSceneDialogueTableIsExcludedFromBytecodeLength(int dialogueCount)
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-subroutine-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dialogue = string.Join("\n", Enumerable.Range(0, dialogueCount)
                .Select(i => $"<PRE @box>\n[line{i}]\nDialogue {i}.\n</PRE>"));
            File.WriteAllText(Path.Combine(root, "test.nss"), $"chapter main {{ call_scene @->last; }}\nscene last {{\n{dialogue}\n}}", Encoding.UTF8);
            var compilation = new Compilation(root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("test.nss")], root, "globals");
            Assert.Empty(emitted.Diagnostics);
            using var moduleStream = File.OpenRead(Path.Combine(root, "test.nsx"));
            NsxModule module = NsxModule.LoadModule(moduleStream, "test");
            Assert.True(module.TryLookupSubroutineIndex("last", out int index));
            Subroutine scene = module.GetSubroutine(index);
            Assert.Equal(dialogueCount, scene.DialogueBlocks.Length);
            string markup = string.Concat(scene.DialogueBlocks.SelectMany(block => block.Parts)
                .OfType<CompiledDialogueBlockPart.Markup>().Select(part => part.GetText(module)));
            Assert.Contains($"Dialogue {dialogueCount - 1}.", markup);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
