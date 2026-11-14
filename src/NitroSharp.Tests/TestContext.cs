using NitroSharp.Text;

namespace NitroSharp.Tests;

internal sealed class TestContext
{
    public TestContext()
    {
        MainProcess = new(EntityName.Parse("test"), null, new FontSettings());
        MainThread = new Thread(EntityName.Parse("main"), MainProcess, default, isMain: true);
    }

    public Process MainProcess { get; }
    public Thread MainThread { get; }
}
