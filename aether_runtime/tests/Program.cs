using System;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NitroSharp.Media;

NativeLibrary.SetDllImportResolver(typeof(OpenAL.NativeLib).Assembly, (name, assembly, path) =>
    NativeLibrary.Load(args[0])
);
await using var device = new OpenALAudioDevice(AudioParameters.Default);
await using var source = device.CreateAudioSource(4096, 4);
var pipe = new Pipe(
    new PipeOptions(pauseWriterThreshold: long.MaxValue, resumeWriterThreshold: long.MaxValue / 2)
);
byte[] pcm = new byte[44100 * 4];
for (int i = 0; i < 44100; i++)
{
    short sample = (short)(Math.Sin(i * 2 * Math.PI * 440 / 44100) * 2000);
    for (int channel = 0; channel < 2; channel++)
    {
        int offset = i * 4 + channel * 2;
        pcm[offset] = (byte)sample;
        pcm[offset + 1] = (byte)(sample >> 8);
    }
}
source.Play(pipe.Reader);
await pipe.Writer.WriteAsync(pcm);
await pipe.Writer.CompleteAsync();
await Task.Delay(250);
if (!source.IsPlaying || source.SecondsElapsed <= 0 || source.GetCurrentBuffer().IsEmpty)
    throw new Exception("OpenAL PCM queue did not advance.");
source.Pause();
double paused = source.SecondsElapsed;
await Task.Delay(80);
if (Math.Abs(source.SecondsElapsed - paused) > 0.03)
    throw new Exception("OpenAL pause did not freeze playback.");
source.Resume();
await Task.Delay(100);
if (source.SecondsElapsed <= paused)
    throw new Exception("OpenAL resume did not advance playback.");
Console.WriteLine(
    $"OpenAL PCM playback advanced to {source.SecondsElapsed:F3}s; pause/resume passed."
);
for (int attempt = 0; attempt < 60 && source.IsPlaying; attempt++)
    await Task.Delay(25);
if (source.IsPlaying)
    throw new Exception("OpenAL did not finish after PCM EOF.");
Console.WriteLine("OpenAL EOF completed without remaining playback.");
source.Stop();

if (args.Length > 2)
{
    NativeLibrary.SetDllImportResolver(
        typeof(FreeTypeBindings.FT).Assembly,
        (name, assembly, path) => NativeLibrary.Load(args[1])
    );
    using var fonts = new NitroSharp.Text.FontContext();
    if (!fonts.AddFont(args[2], out var faces) || faces.IsEmpty)
        throw new Exception("Font did not load.");
    var face = faces[0].Item2;
    uint index = fonts.GetGlyphIndex(face, 'A');
    var glyph = fonts.RasterizeGlyph(face, new NitroSharp.Text.PtFontSize(24), index);
    if (
        glyph.Width == 0
        || glyph.Height == 0
        || !System.Linq.Enumerable.Any(glyph.Bytes, value => value != 0)
    )
        throw new Exception("Glyph rasterizer returned an empty bitmap.");
    Console.WriteLine($"FreeType rasterized 'A' to {glyph.Width}x{glyph.Height} coverage bitmap.");
}
