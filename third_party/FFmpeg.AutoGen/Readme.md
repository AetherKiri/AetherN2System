FFmpeg bindings are derived from Ruslan-B/FFmpeg.AutoGen tag v8.1.0.0 (commit 444925cd53d3611fd4c8c295873fb631be56ab21), under the accompanying MIT license. Generated structures, enums, arrays, delegates, and constants retain the upstream layouts for FFmpeg 8.1. The namespace is kept as FFmpeg.AutoGen for the existing runtime.

FFmpeg.functions.g.cs contains only the direct P/Invoke declarations used by NitroSharp. The declarations are adapted from the upstream DynamicallyLinkedBindings, with platform-specific library names, built-in UTF-8 string marshaling, and pointer parameters for sws_scale. These adaptations avoid runtime binding generation and custom marshaling under NativeAOT. NativeLibs.cs pins library major versions; substituting a different FFmpeg ABI is unsupported.

The Aether host may resolve the versioned library names to its bundled FFmpeg media library, built from the same FFmpeg 8 ABI. The bindings do not package native binaries.
