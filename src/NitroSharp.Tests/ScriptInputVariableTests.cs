using System;
using System.IO;
using System.Text;
using NitroSharp.Input;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;
using Xunit;

namespace NitroSharp.Tests;

public sealed class ScriptInputVariableTests
{
    [Theory]
    [InlineData(Key.Return)]
    [InlineData(Key.KeypadEnter)]
    public void DynamicallyLoadedEnterVariablePublishesOnePressEvenAfterChoiceConsumption(Key key)
    {
        using var scripts = new Scripts();
        using var window = new HostedWindow(new SizeU<ScreenPixel>(320, 180));
        using var input = new InputContext(window);
        window.Event(1, (int)key, 0, 0);
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.True(input.ConsumeChoicePress(pointer: false));
        Assert.True(scripts.Vm.SystemVariables.ReturnKeyDown.AsBool());
        Assert.False(scripts.Vm.SystemVariables.LeftButtonDown.AsBool());
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.False(scripts.Vm.SystemVariables.ReturnKeyDown.AsBool());
        window.Event(2, (int)key, 0, 0);
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.False(scripts.Vm.SystemVariables.ReturnKeyDown.AsBool());
    }

    [Fact]
    public void DynamicallyLoadedLeftButtonVariablePublishesOnePressEvenAfterChoiceConsumption()
    {
        using var scripts = new Scripts();
        using var window = new HostedWindow(new SizeU<ScreenPixel>(320, 180));
        using var input = new InputContext(window);
        window.Event(4, 1, 120, 80);
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.True(input.ConsumeChoicePress(pointer: true));
        Assert.True(scripts.Vm.SystemVariables.LeftButtonDown.AsBool());
        Assert.False(scripts.Vm.SystemVariables.ReturnKeyDown.AsBool());
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.False(scripts.Vm.SystemVariables.LeftButtonDown.AsBool());
        window.Event(5, 1, 120, 80);
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.False(scripts.Vm.SystemVariables.LeftButtonDown.AsBool());
    }

    [Fact]
    public void SpaceAdvanceDoesNotSetTheEnterVariable()
    {
        using var scripts = new Scripts();
        using var window = new HostedWindow(new SizeU<ScreenPixel>(320, 180));
        using var input = new InputContext(window);
        window.Event(1, (int)Key.Space, 0, 0);
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.True(input.Consume(InputAction.Advance));
        Assert.False(scripts.Vm.SystemVariables.ReturnKeyDown.AsBool());
        input.Update(window, scripts.Vm.SystemVariables);
        Assert.False(scripts.Vm.SystemVariables.ReturnKeyDown.AsBool());
    }

    private sealed class Scripts : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "script-input-test-" + Guid.NewGuid().ToString("N"));
        private readonly Stream _globals;
        public NsScriptVM Vm { get; }

        public Scripts()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "initial.nss"), "chapter main { }", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_root, "ending.nss"), "chapter main { $SYSTEM_keydown_enter=false; $SYSTEM_l_button_down=false; }", Encoding.UTF8);
            var compilation = new Compilation(_root, Encoding.UTF8);
            var emitted = compilation.Emit([compilation.GetSourceModule("initial.nss")], _root, "globals");
            Assert.Empty(emitted.Diagnostics);
            _globals = File.OpenRead(Path.Combine(_root, "globals"));
            Vm = new NsScriptVM(new FileSystemNsxModuleLocator(_root, _root, Encoding.UTF8), _globals);
            Assert.False(Vm.GlobalsLookup.Variables.ContainsKey("SYSTEM_keydown_enter"));
            Assert.False(Vm.GlobalsLookup.Variables.ContainsKey("SYSTEM_l_button_down"));
            Assert.NotNull(Vm.CreateThread("ending", "main"));
        }

        public void Dispose()
        {
            _globals.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
