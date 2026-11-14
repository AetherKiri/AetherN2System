using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Compiler;

namespace NitroSharp.NsScript.VM;

public readonly struct SourceMappingScope(NsxModuleLocator moduleLocator) : IDisposable
{
    private readonly Dictionary<string, SourceText> _sourceTexts = new();

    public SourceText GetSourceText(string moduleName)
    {
        if (!_sourceTexts.TryGetValue(moduleName, out SourceText? sourceText))
        {
            _sourceTexts[moduleName] = sourceText = moduleLocator.LoadSourceText(moduleName);
        }

        return sourceText;
    }

    public void Dispose()
    {
    }
}

public abstract class NsxModuleLocator
{
    internal virtual string NormalizeModuleName(string name)
    {
        name = name.Replace('\\', '/');
        return Path.GetExtension(name) is ".nss" or ".nsx" ? name[..^4] : name;
    }

    internal virtual CompiledModuleSet? CompileModuleIfMissing(
        string name, GlobalsLookupTable globals, IReadOnlyDictionary<string, NsxModule> loadedModules,
        string[]? callerScopes = null) => null;

    /// <param name="name">Name without extension.</param>
    public abstract Stream OpenModule(string name);

    protected internal abstract SourceText LoadSourceText(string moduleName);
    public SourceMappingScope BeginSourceMapping() => new(this);
}

internal sealed record CompiledModuleSet(GlobalsLookupTable Globals, Dictionary<string, byte[]> Modules);

public sealed class FileSystemNsxModuleLocator(string bytecodeDirectory, string sourceDirectory, Encoding? sourceEncoding = null,
    SourceTextDecodingOptions? decodingOptions = null)
    : NsxModuleLocator
{
    private readonly SourceReferenceResolver _sourceResolver = new DefaultSourceReferenceResolver(sourceDirectory);

    internal override string NormalizeModuleName(string name)
    {
        name = base.NormalizeModuleName(name);
        string prefix = new DirectoryInfo(sourceDirectory).Name + "/";
        if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
        return _sourceResolver.TryResolvePath(name + ".nss") is { } source
            ? Path.ChangeExtension(Path.GetRelativePath(sourceDirectory, source.Value), null).Replace('\\', '/')
            : name;
    }

    internal override CompiledModuleSet? CompileModuleIfMissing(
        string name, GlobalsLookupTable globals, IReadOnlyDictionary<string, NsxModule> loadedModules,
        string[]? callerScopes = null)
    {
        if (File.Exists(Path.Combine(bytecodeDirectory, name) + ".nsx"))
        {
            if (callerScopes is null || !hasSource(name)) return null;
            foreach (string scope in callerScopes)
                if (!hasSource(scope)) return null;
        }
        var compilation = new Compilation(_sourceResolver, sourceEncoding, decodingOptions);
        SourceModuleSymbol? fallback = null;
        if (callerScopes is not null)
        {
            for (int i = callerScopes.Length - 1; i >= 0; i--)
                fallback = compilation.GetSourceModule(callerScopes[i] + ".nss", fallback);
        }
        SourceModuleSymbol root = compilation.GetSourceModule(name + ".nss", fallback);
        var modules = new Dictionary<string, MemoryStream>();
        using var globalBytes = new MemoryStream();
        _ = compilation.EmitIncremental(root, createOutput, globalBytes, globals);
        // Keep incremental bytecode with this VM's global table, rather than
        // caching files whose new slots are absent from the startup globals.
        var bytecode = new Dictionary<string, byte[]>();
        foreach ((string module, MemoryStream bytes) in modules) bytecode.Add(module, bytes.ToArray());
        return new CompiledModuleSet(GlobalsLookupTable.Load(new MemoryStream(globalBytes.ToArray())), bytecode);

        bool hasSource(string module)
            => _sourceResolver.TryResolvePath(module + ".nss") is { } path && File.Exists(path.Value);

        Stream createOutput(NsxModuleBuilder builder)
        {
            string module = NormalizeModuleName(builder.SourceFile.Name);
            if (callerScopes is null && (loadedModules.ContainsKey(module) || File.Exists(Path.Combine(bytecodeDirectory, module) + ".nsx"))) return Stream.Null;
            var bytes = new MemoryStream();
            modules.Add(module, bytes);
            return bytes;
        }
    }

    public override Stream OpenModule(string name)
    {
        string path = Path.Combine(bytecodeDirectory, name) + ".nsx";
        return File.OpenRead(path);
    }

    protected internal override SourceText LoadSourceText(string moduleName)
    {
        string path = Path.Combine(sourceDirectory, moduleName) + ".nss";
        using FileStream stream = File.OpenRead(path);
        return SourceText.From(stream, new ResolvedPath(path), sourceEncoding, decodingOptions);
    }
}
