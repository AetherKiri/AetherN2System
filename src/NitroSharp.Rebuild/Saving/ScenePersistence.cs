using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Text;
using MessagePack;
using NitroSharp.Content;
using NitroSharp.Graphics;
using NitroSharp.Media;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;
using NitroSharp.Saving;
using NitroSharp.Text;
using Veldrid;

namespace NitroSharp.Saving
{
    // The enclosing slot owns format versioning and VM globals. Entity references are
    // indices into this parent-before-child table, never process-local handles or paths.
    internal static class ScenePersistence
    {
        private enum Kind : byte
        {
            Process, Blank, CallScope, Thread, Image, Sprite, SolidColor, DialogueBox,
            DialoguePage, TextBlock, FrameSequence, Audio, Video, Scrollbar, Choice,
            MouseState, PlainEffect
        }

        public static byte[] Capture(GameContext ctx, Process process)
        {
            var entities = new List<Entity>();
            var ids = new Dictionary<Entity, int>(ReferenceEqualityComparer.Instance);
            Collect(process);
            int Id(Entity entity) => ids.TryGetValue(entity, out int id) ? id
                : throw new InvalidOperationException($"Save references an entity outside the scene: {entity.GetAbsolutePath()}");
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(-2);
            writer.Write(entities.Count);
            foreach (Entity entity in entities)
            {
                Kind kind = GetKind(entity);
                writer.Write((byte)kind);
                writer.Write(entity.Name.Value);
                writer.Write(entity.Parent is null ? -1 : Id(entity.Parent));
                writer.Write(entity.IsEnabled);
                writer.Write(entity.IsLocked);
                writer.Write(entity is RenderItem renderItem ? renderItem.Priority : 0);
                switch (entity)
                {
                    case Process p: SaveIO.WriteFont(writer, p.FontSettings); break;
                    case CallScopeEntity call: writer.Write(call.SaveRetired); break;
                    case Thread thread: thread.WriteSave(writer, ctx.VM, Id, ctx.Clock.Elapsed); break;
                    case Image image: image.Texture.WriteSave(writer, ctx); break;
                    case Scrollbar scrollbar: scrollbar.WriteScrollbarSave(writer, ctx, Id); break;
                    case Sprite sprite:
                        sprite.Texture.WriteSave(writer, ctx);
                        writer.Write(sprite.SaveBlurPasses);
                        writer.Write((byte)sprite.Tone);
                        writer.Write(sprite.RetireWhenIdle);
                        break;
                    case SolidColorRect solid: SaveIO.WriteSize(writer, solid.GetSize(ctx.RenderContext)); break;
                    case DialogueBox box:
                        SaveIO.WriteSize(writer, box.GetSize(ctx.RenderContext)); writer.Write(box.IsContainer); break;
                    case DialoguePage page:
                        writer.Write(page.ScriptScope is { IsPendingDestruction: false } scope ? Id(scope) : -1);
                        page.WritePageSave(writer, ctx, Id);
                        break;
                    case TextBlock text: text.WriteTextSave(writer); break;
                    case FrameSequence sequence: sequence.WriteSequenceSave(writer); break;
                    case AudioEntity audio: audio.WriteAudioSave(writer); break;
                    case Video video: video.WriteVideoSave(writer); break;
                    case ChoiceEntity choice: choice.WriteChoiceSave(writer, Id); break;
                    case MouseStateEntity mouse: writer.Write(mouse.IsVisible); break;
                    case PlainEffect plain: plain.WritePlainSave(writer, ctx); break;
                }
                if (entity is RenderItem render) render.WriteRenderSave(writer);
            }
            writer.Write(process.Aliases.SaveEntries.Count);
            foreach (var entry in process.Aliases.SaveEntries)
            {
                writer.Write(entry.Key.Value);
                writer.Write(Id(entry.Value));
            }
            writer.Write(Id(process.CurrentThread));
            writer.Flush();
            return output.ToArray();

            void Collect(Entity entity)
            {
                if (entity.IsPendingDestruction) return;
                ids.Add(entity, entities.Count);
                entities.Add(entity);
                foreach (Entity child in entity.Children) Collect(child);
            }
        }

        public static Process Restore(GameContext ctx, byte[] data)
        {
            using var input = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(input, Encoding.UTF8);
            int count = reader.ReadInt32();
            bool extended = count == -2;
            if (extended) count = SaveIO.ReadCount(reader, 100_000);
            if (count is <= 0 or > 100_000) throw new InvalidDataException("The saved scene is empty.");
            var entities = new List<Entity>(count);
            var resolve = new List<Action<IReadOnlyList<Entity>>>();
            Process? process = null;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var kind = (Kind)reader.ReadByte();
                    EntityName name = EntityName.Parse(reader.ReadString());
                    int parentId = reader.ReadInt32();
                    if (i == 0 ? parentId != -1 || kind != Kind.Process : parentId < 0 || parentId >= i || kind == Kind.Process)
                        throw new InvalidDataException("Invalid saved entity hierarchy.");
                    Entity? parent = parentId == -1 ? null : entities[parentId];
                    if (parent?.TryGetChild(name) is not null) throw new InvalidDataException("Duplicate saved sibling name.");
                    bool enabled = reader.ReadBoolean();
                    bool locked = reader.ReadBoolean();
                    int priority = reader.ReadInt32();
                    Entity entity = kind switch
                    {
                        Kind.Process => new Process(name, null, SaveIO.ReadFont(reader)),
                        Kind.Blank => new BlankEntity(name, parent),
                        Kind.CallScope => CallScopeEntity.ReadCallSave(name, parent!, reader),
                        Kind.Thread => ReadThread(),
                        Kind.Image => new Image(name, parent, SpriteTexture.ReadSave(reader, ctx)),
                        Kind.Sprite => ReadSprite(),
                        Kind.SolidColor => new SolidColorRect(name, parent, priority, RgbaFloat.White, SaveIO.ReadSize(reader)),
                        Kind.DialogueBox => new DialogueBox(name, parent, priority, SaveIO.ReadSize(reader), reader.ReadBoolean()),
                        Kind.DialoguePage => ReadPage(),
                        Kind.TextBlock => TextBlock.ReadTextSave(name, parent!, priority, reader, ctx),
                        Kind.FrameSequence => FrameSequence.ReadSequenceSave(name, parent, priority, reader, ctx),
                        Kind.Audio => AudioEntity.ReadAudioSave(name, parent, reader, ctx),
                        Kind.Video => Video.ReadVideoSave(name, parent, priority, reader, ctx),
                        Kind.Scrollbar => Scrollbar.ReadScrollbarSave(name, parent, priority, reader, ctx, resolve),
                        Kind.Choice => ChoiceEntity.ReadChoiceSave(name, parent, reader, resolve),
                        Kind.MouseState when parent is ChoiceEntity choice => new MouseStateEntity(name, choice) { IsVisible = reader.ReadBoolean() },
                        Kind.PlainEffect => PlainEffect.ReadPlainSave(name, parent, priority, reader, ctx),
                        _ => throw new InvalidDataException($"Unknown saved entity type: {kind}.")
                    };
                    entities.Add(entity);
                    if (entity is Process p) process = p;
                    ctx.World.AddEntity(entity, enableNextFrame: false);
                    if (entity is RenderItem visual) visual.ReadRenderSave(reader, ctx);
                    if (enabled) entity.Enable(); else entity.Disable();
                    if (locked) entity.Request(NsEntityAction.Lock);

                    Thread ReadThread()
                    {
                        Thread thread = Thread.ReadSave(name, parent!, reader, ctx.VM, ctx.Clock.Elapsed, out var references);
                        resolve.Add(references);
                        return thread;
                    }
                    DialoguePage ReadPage()
                    {
                        int owner = extended ? reader.ReadInt32() : -1;
                        var page = DialoguePage.ReadPageSave(name, parent!, priority, reader, ctx, resolve);
                        if (owner >= 0) resolve.Add(list => page.ScriptScope = SaveIO.EntityAt(list, owner));
                        return page;
                    }
                    Sprite ReadSprite()
                    {
                        var sprite = new Sprite(name, parent, priority, SpriteTexture.ReadSave(reader, ctx));
                        try
                        {
                            sprite.SetBlur(reader.ReadUInt32());
                            if (extended)
                            {
                                var tone = (NsTone)reader.ReadByte();
                                if (!Enum.IsDefined(tone)) throw new InvalidDataException("Invalid saved sprite tone.");
                                sprite.SetTone(tone);
                                sprite.RetireWhenIdle = reader.ReadBoolean();
                            }
                            return sprite;
                        }
                        catch { sprite.Dispose(); throw; }
                    }
                }
                int aliases = SaveIO.ReadCount(reader, 100_000);
                for (int i = 0; i < aliases; i++)
                {
                    var alias = EntityAlias.CreateUnsafe(reader.ReadString());
                    process!.Aliases.Set(SaveIO.EntityAt(entities, reader.ReadInt32()), alias);
                }
                int currentThread = reader.ReadInt32();
                process!.RestoreCurrentThread((Thread)SaveIO.EntityAt(entities, currentThread));
                if (input.Position != input.Length) throw new InvalidDataException("Unexpected data after the scene.");
                foreach (var references in resolve) references(entities);
                _ = process!.MainThread;
                return process;
            }
            catch
            {
                if (process is not null) ctx.World.DiscardRestoredProcess(process);
                throw;
            }
        }

        private static Kind GetKind(Entity entity) => entity switch
        {
            Process => Kind.Process, BlankEntity => Kind.Blank, CallScopeEntity => Kind.CallScope,
            Thread => Kind.Thread, Image => Kind.Image, Scrollbar => Kind.Scrollbar,
            Sprite when entity.GetType() == typeof(Sprite) => Kind.Sprite, SolidColorRect => Kind.SolidColor, DialogueBox => Kind.DialogueBox,
            DialoguePage => Kind.DialoguePage, TextBlock => Kind.TextBlock, FrameSequence => Kind.FrameSequence,
            AudioEntity => Kind.Audio, Video => Kind.Video, ChoiceEntity => Kind.Choice,
            MouseStateEntity => Kind.MouseState, PlainEffect => Kind.PlainEffect,
            LegacySceneTransition => throw new NotSupportedException("Saving during a scene transition is not supported; wait until the transition completes."),
            _ => throw new NotSupportedException($"Cannot save entity type {entity.GetType().Name}.")
        };
    }

    internal static class SaveIO
    {
        public static int ReadCount(BinaryReader reader, int maximum = 1_000_000)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum) throw new InvalidDataException("Invalid saved collection length.");
            return count;
        }
        public static Entity EntityAt(IReadOnlyList<Entity> entities, int id)
            => id >= 0 && id < entities.Count ? entities[id] : throw new InvalidDataException("Invalid saved entity reference.");
        public static void WriteVector(BinaryWriter w, Vector2 v) { w.Write(v.X); w.Write(v.Y); }
        public static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        public static void WriteVector(BinaryWriter w, Vector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
        public static Vector2 ReadVector2(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
        public static Vector3 ReadVector3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        public static Vector4 ReadVector4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        public static void WriteColor(BinaryWriter w, RgbaFloat color) => WriteVector(w, color.ToVector4());
        public static RgbaFloat ReadColor(BinaryReader r) => new(ReadVector4(r));
        public static void WriteSize(BinaryWriter w, DesignSize size) { w.Write(size.Width); w.Write(size.Height); }
        public static DesignSize ReadSize(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
        public static void WriteRectangle(BinaryWriter w, DesignRectU? rectangle)
        {
            w.Write(rectangle.HasValue);
            if (rectangle is { } rect) { w.Write(rect.Left); w.Write(rect.Top); w.Write(rect.Width); w.Write(rect.Height); }
        }
        public static DesignRectU? ReadRectangle(BinaryReader r)
            => r.ReadBoolean() ? new DesignRectU(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()) : null;
        public static void WriteFont(BinaryWriter w, FontSettings font)
        {
            var buffer = new ArrayBufferWriter<byte>();
            var mp = new MessagePackWriter(buffer);
            font.Serialize(ref mp); mp.Flush();
            w.Write(buffer.WrittenCount); w.Write(buffer.WrittenSpan);
        }
        public static FontSettings ReadFont(BinaryReader r)
        {
            int count = ReadCount(r, 1024 * 1024);
            byte[] bytes = r.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            var mp = new MessagePackReader(bytes);
            return new FontSettings(ref mp);
        }
        public static void WriteRuns(BinaryWriter w, IEnumerable<TextRun> runs)
        {
            var list = new List<TextRun>(runs); w.Write(list.Count);
            foreach (TextRun run in list)
            {
                w.Write(run.Text.ToString()); w.Write(run.RubyText.ToString());
                w.Write(run.Font.FamilyName); w.Write((int)run.Font.Style); w.Write(run.FontSize.Value.Value);
                WriteColor(w, run.Color); w.Write(run.DrawOutline); if (run.DrawOutline) WriteColor(w, run.OutlineColor);
            }
        }
        public static ImmutableArray<TextRun> ReadRuns(BinaryReader r)
        {
            int count = ReadCount(r); var runs = ImmutableArray.CreateBuilder<TextRun>(count);
            for (int i = 0; i < count; i++)
            {
                string text = r.ReadString(), ruby = r.ReadString();
                var font = new FontFaceKey(r.ReadString(), (FontStyle)r.ReadInt32());
                var size = new PtFontSize(FreeTypeBindings.Fixed26Dot6.FromRawValue(r.ReadInt32()));
                RgbaFloat color = ReadColor(r); RgbaFloat? outline = r.ReadBoolean() ? ReadColor(r) : null;
                runs.Add(ruby.Length == 0 ? TextRun.Regular(text.AsMemory(), font, size, color, outline)
                    : TextRun.WithRubyText(text.AsMemory(), ruby.AsMemory(), font, size, color, outline));
            }
            return runs.MoveToImmutable();
        }
    }
}

namespace NitroSharp
{
    internal sealed partial class Process
    {
        internal void RestoreCurrentThread(Thread thread) => CurrentThread = thread;
    }
    internal sealed partial class AliasMap
    {
        internal IReadOnlyDictionary<EntityAlias, Entity> SaveEntries => _aliases;
    }
    internal sealed partial class CallScopeEntity
    {
        internal bool SaveRetired => _retired;
        internal static CallScopeEntity ReadCallSave(EntityName name, Entity parent, BinaryReader r)
            => new(name, parent) { _retired = r.ReadBoolean() };
    }
    internal sealed partial class ChoiceEntity
    {
        internal void WriteChoiceSave(BinaryWriter w, Func<Entity, int> id)
        {
            w.Write(PreserveOpacity); w.Write(IsClickable); w.Write(PointerInside);
            w.Write(_nextFocus.Count);
            foreach (var next in _nextFocus) { w.Write((int)next.Key); w.Write(id(next.Value)); }
        }
        internal static ChoiceEntity ReadChoiceSave(EntityName name, Entity? parent, BinaryReader r,
            List<Action<IReadOnlyList<Entity>>> resolve)
        {
            var choice = new ChoiceEntity(name, parent, r.ReadBoolean()) { IsClickable = r.ReadBoolean(), PointerInside = r.ReadBoolean() };
            int count = SaveIO.ReadCount(r, 32);
            for (int i = 0; i < count; i++)
            {
                var direction = (NsFocusDirection)r.ReadInt32(); int target = r.ReadInt32();
                resolve.Add(entities => choice.SetNextFocus(direction, (ChoiceEntity)SaveIO.EntityAt(entities, target)));
            }
            return choice;
        }
    }
}

namespace NitroSharp.Graphics
{
    internal readonly partial struct SpriteTexture
    {
        internal unsafe void WriteSave(BinaryWriter w, GameContext ctx)
        {
            w.Write((byte)Kind);
            SaveIO.WriteRectangle(w, SourceRectangle);
            SaveIO.WriteColor(w, Color);
            if (Kind == SpriteTextureKind.Asset) { w.Write(_assetRef!.Value.Path); return; }
            if (Kind == SpriteTextureKind.SolidColor) return;
            // Snapshot textures contain GPU resources, so store normalized RGBA bytes
            // rather than a platform handle, pixel layout, or process-local lease.
            Texture texture = Resolve(ctx.RenderContext);
            if (texture.Format is not (PixelFormat.R8_G8_B8_A8_UNorm or PixelFormat.B8_G8_R8_A8_UNorm
                or PixelFormat.R8_G8_B8_A8_UNorm_SRgb or PixelFormat.B8_G8_R8_A8_UNorm_SRgb))
                throw new NotSupportedException($"Cannot save snapshot pixel format {texture.Format}.");
            GraphicsDevice device = ctx.RenderContext.GraphicsDevice;
            using CommandList commands = device.ResourceFactory.CreateCommandList();
            using Texture staging = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                texture.Width, texture.Height, 1, 1, texture.Format, TextureUsage.Staging));
            commands.Begin(); commands.CopyTexture(texture, staging); commands.End();
            device.SubmitCommands(commands); device.WaitForIdle();
            w.Write(texture.Width); w.Write(texture.Height);
            w.Write(texture.Format is PixelFormat.R8_G8_B8_A8_UNorm_SRgb or PixelFormat.B8_G8_R8_A8_UNorm_SRgb);
            w.Write(_textureSize.HasValue);
            if (_textureSize is { } size) { w.Write(size.Width); w.Write(size.Height); }
            SaveIO.WriteVector(w, _uvOrigin); SaveIO.WriteVector(w, _uvExtent); w.Write(_sourceInset);
            MappedResource map = device.Map(staging, MapMode.Read);
            try
            {
                var row = new byte[checked((int)texture.Width * 4)];
                for (int y = 0; y < texture.Height; y++)
                {
                    new ReadOnlySpan<byte>((byte*)map.Data + y * map.RowPitch, row.Length).CopyTo(row);
                    if (texture.Format is PixelFormat.B8_G8_R8_A8_UNorm or PixelFormat.B8_G8_R8_A8_UNorm_SRgb)
                        for (int x = 0; x < row.Length; x += 4) (row[x], row[x + 2]) = (row[x + 2], row[x]);
                    w.Write(row);
                }
            }
            finally { device.Unmap(staging); }
        }

        internal static SpriteTexture ReadSave(BinaryReader r, GameContext ctx)
        {
            var kind = (SpriteTextureKind)r.ReadByte();
            DesignRectU? rectangle = SaveIO.ReadRectangle(r);
            RgbaFloat color = SaveIO.ReadColor(r);
            if (kind == SpriteTextureKind.Asset)
            {
                string path = r.ReadString();
                return FromAsset(ctx.Content.RequestTexture(path)
                    ?? throw new FileNotFoundException("Saved texture is missing.", path), rectangle);
            }
            if (kind == SpriteTextureKind.SolidColor)
                return SolidColor(color, rectangle is { } rect ? new DesignSizeU(rect.Width, rect.Height)
                    : throw new InvalidDataException("A solid texture has no size."));
            if (kind is not (SpriteTextureKind.Owned or SpriteTextureKind.Pooled or SpriteTextureKind.Borrowed))
                throw new InvalidDataException("Unknown saved texture kind.");
            uint width = r.ReadUInt32(), height = r.ReadUInt32();
            if (width == 0 || height == 0 || width > 16384 || height > 16384 || (ulong)width * height > 67_108_864)
                throw new InvalidDataException("Invalid saved texture size.");
            bool srgb = r.ReadBoolean();
            DesignSizeU? size = r.ReadBoolean() ? new DesignSizeU(r.ReadUInt32(), r.ReadUInt32()) : null;
            Vector2 origin = SaveIO.ReadVector2(r), extent = SaveIO.ReadVector2(r); float inset = r.ReadSingle();
            int count = checked((int)(width * height * 4)); byte[] pixels = r.ReadBytes(count);
            if (pixels.Length != count) throw new EndOfStreamException();
            GraphicsDevice device = ctx.RenderContext.GraphicsDevice;
            Texture texture = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D(width, height,
                1, 1, srgb ? PixelFormat.R8_G8_B8_A8_UNorm_SRgb : PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
            try
            {
                device.UpdateTexture(texture, pixels, 0, 0, 0, width, height, 1, 0, 0);
                return new SpriteTexture(SpriteTextureKind.Owned, null, rectangle, null,
                    new TextureLease(texture, texture), color, size, origin, extent, inset);
            }
            catch { texture.Dispose(); throw; }
        }
    }

    internal partial class Sprite
    {
        internal uint SaveBlurPasses => _blurPasses;
    }

    internal abstract partial class RenderItem
    {
        internal void WriteRenderSave(BinaryWriter w)
        {
            w.Write(_key.Id);
            SaveIO.WriteVector(w, Transform.Position); SaveIO.WriteVector(w, Transform.Scale);
            SaveIO.WriteVector(w, Transform.Rotation); w.Write(Transform.Pivot.HasValue);
            if (Transform.Pivot is { } pivot) SaveIO.WriteVector(w, pivot);
            SaveIO.WriteColor(w, Color); w.Write(IsHidden); w.Write((int)BlendMode); w.Write((int)FilterMode);
            AnimationPersistence.Write(w, _fadeAnimation); AnimationPersistence.Write(w, _moveAnimation);
            AnimationPersistence.Write(w, _bezierMoveAnimation); AnimationPersistence.Write(w, _scaleAnimation);
            AnimationPersistence.Write(w, _rotationAnimation);
            w.Write(_transition is not null);
            if (_transition is { } transition) transition.WriteMaskSave(w);
            w.Write(_transitionActive);
        }
        internal void ReadRenderSave(BinaryReader r, GameContext ctx)
        {
            int drawId = r.ReadInt32();
            if (drawId < 0 || drawId == int.MaxValue) throw new InvalidDataException("Invalid saved draw order.");
            _key = new RenderItemKey(Priority, drawId); s_lastId = Math.Max(s_lastId, drawId + 1);
            Transform.Position = SaveIO.ReadVector3(r); Transform.Scale = SaveIO.ReadVector3(r);
            Transform.Rotation = SaveIO.ReadVector3(r); Transform.Pivot = r.ReadBoolean() ? SaveIO.ReadVector2(r) : null;
            Color = SaveIO.ReadColor(r); IsHidden = r.ReadBoolean();
            BlendMode = (BlendMode)r.ReadInt32(); FilterMode = (FilterMode)r.ReadInt32();
            _fadeAnimation = (OpacityAnimation?)AnimationPersistence.Read(r, this);
            _moveAnimation = (MoveAnimation?)AnimationPersistence.Read(r, this);
            _bezierMoveAnimation = (BezierMoveAnimation?)AnimationPersistence.Read(r, this);
            _scaleAnimation = (ScaleAnimation?)AnimationPersistence.Read(r, this);
            _rotationAnimation = (RotationAnimation?)AnimationPersistence.Read(r, this);
            _transition = r.ReadBoolean() ? MaskTransition.ReadMaskSave(r, ctx) : null;
            _transitionActive = r.ReadBoolean();
        }
    }

    internal sealed partial class MaskTransition
    {
        internal void WriteMaskSave(BinaryWriter w)
        {
            w.Write(_mask.HasValue); if (_mask is { } mask) w.Write(mask.Path);
            w.Write(_source); w.Write(_destination); w.Write(Feather); SaveAnimationHeader(w);
            w.Write(ProgressValue);
        }
        internal static MaskTransition ReadMaskSave(BinaryReader r, GameContext ctx)
        {
            AssetRef<Texture>? mask = null;
            if (r.ReadBoolean())
            {
                string path = r.ReadString(); mask = ctx.Content.RequestTexture(path)
                    ?? throw new FileNotFoundException("Saved transition mask is missing.", path);
            }
            try
            {
                float source = r.ReadSingle(), destination = r.ReadSingle(), feather = r.ReadSingle();
                var header = AnimationPersistence.ReadHeader(r);
                float progress = r.ReadSingle();
                var transition = new MaskTransition(mask, source, destination, feather, header.Duration, header.Ease);
                transition.LoadAnimationHeader(header); transition.ProgressValue = progress;
                return transition;
            }
            catch { mask?.Dispose(); throw; }
        }
    }

    internal sealed partial class TextBlock
    {
        private string SaveMarkup { get; }
        private FontSettings SaveFontSettings { get; }
        private DesignSizeU SaveMaxBounds { get; }
        internal void WriteTextSave(BinaryWriter w)
        {
            w.Write(SaveMarkup); SaveIO.WriteFont(w, SaveFontSettings);
            w.Write(SaveMaxBounds.Width); w.Write(SaveMaxBounds.Height); SaveIO.WriteVector(w, _margin);
        }
        internal static TextBlock ReadTextSave(EntityName name, Entity parent, int priority, BinaryReader r, GameContext ctx)
        {
            string markup = r.ReadString(); FontSettings font = SaveIO.ReadFont(r);
            var bounds = new DesignSizeU(r.ReadUInt32(), r.ReadUInt32()); Vector4 margin = SaveIO.ReadVector4(r);
            return new TextBlock(name, parent, priority, markup, ctx.RenderContext.Text, bounds, font, margin);
        }
    }

    internal sealed partial class Scrollbar
    {
        internal void WriteScrollbarSave(BinaryWriter w, GameContext ctx, Func<Entity, int> id)
        {
            Texture.WriteSave(w, ctx); w.Write(SaveBlurPasses);
            SaveIO.WriteVector(w, _start); SaveIO.WriteVector(w, _end); w.Write(_vertical);
            w.Write(Value); w.Write(ScrollSpeed); w.Write(ScrollTarget is { } target ? id(target) : -1);
            // Pointer capture belongs to the current input gesture, not to a saved scene.
        }
        internal static Scrollbar ReadScrollbarSave(EntityName name, Entity? parent, int priority,
            BinaryReader r, GameContext ctx, List<Action<IReadOnlyList<Entity>>> resolve)
        {
            SpriteTexture texture = SpriteTexture.ReadSave(r, ctx);
            try
            {
                uint blur = r.ReadUInt32();
                Vector2 start = SaveIO.ReadVector2(r), end = SaveIO.ReadVector2(r); bool vertical = r.ReadBoolean();
                var scrollbar = new Scrollbar(name, parent, priority, texture, start, end,
                    vertical ? NsScrollDirection.Vertical : NsScrollDirection.Horizontal, r.ReadSingle());
                scrollbar.SetBlur(blur); scrollbar.ScrollSpeed = r.ReadSingle(); int target = r.ReadInt32();
                if (target >= 0) resolve.Add(entities => scrollbar.ScrollTarget = SaveIO.EntityAt(entities, target));
                return scrollbar;
            }
            catch { texture.Dispose(); throw; }
        }
    }

    internal sealed partial class PlainEffect
    {
        internal void WritePlainSave(BinaryWriter w, GameContext ctx)
        {
            w.Write(_size.Width); w.Write(_size.Height); w.Write(IsPassive); w.Write(_blurPasses);
            SaveIO.WriteRectangle(w, _sourceRectangle); w.Write(_snapshot.HasValue);
            if (_snapshot is { } snapshot) snapshot.WriteSave(w, ctx);
        }
        internal static PlainEffect ReadPlainSave(EntityName name, Entity? parent, int priority, BinaryReader r, GameContext ctx)
        {
            var size = new DesignSizeU(r.ReadUInt32(), r.ReadUInt32());
            var effect = new PlainEffect(name, parent, priority, size, rect => ctx.RenderContext.CapturePresentedFrame(rect));
            effect.IsPassive = r.ReadBoolean(); effect._blurPasses = r.ReadUInt32();
            effect._sourceRectangle = SaveIO.ReadRectangle(r);
            effect._snapshot = r.ReadBoolean() ? SpriteTexture.ReadSave(r, ctx) : null;
            effect._blurDirty = true;
            return effect;
        }
    }
}

namespace NitroSharp.Saving
{
    internal static class AnimationPersistence
    {
        internal readonly record struct Header(TimeSpan Duration, NsEaseFunction Ease, bool Repeat,
            float Elapsed, bool Initialized, bool Completed);
        internal static Header ReadHeader(BinaryReader r) => new(TimeSpan.FromTicks(r.ReadInt64()),
            (NsEaseFunction)r.ReadInt32(), r.ReadBoolean(), r.ReadSingle(), r.ReadBoolean(), r.ReadBoolean());
        internal static void Write(BinaryWriter w, AnimationWithDuration? animation)
        {
            if (animation is null) { w.Write((byte)0); return; }
            byte kind = animation switch
            {
                OpacityAnimation => 1, MoveAnimation => 2, RotationAnimation => 3,
                ScaleAnimation => 4, BezierMoveAnimation => 5,
                _ => throw new NotSupportedException($"Cannot save animation {animation.GetType().Name}.")
            };
            w.Write(kind); animation.SaveAnimationHeader(w);
            switch (animation)
            {
                case OpacityAnimation opacity: opacity.WriteOpacitySave(w); break;
                case Vector3Animation<RenderItem> vector: vector.WriteVectorSave(w); break;
                case BezierMoveAnimation bezier:
                    w.Write(bezier.SaveCurve.Segments.Length);
                    foreach (var segment in bezier.SaveCurve.Segments)
                    {
                        SaveIO.WriteVector(w, segment.P0); SaveIO.WriteVector(w, segment.P1);
                        SaveIO.WriteVector(w, segment.P2); SaveIO.WriteVector(w, segment.P3);
                    }
                    break;
            }
        }
        internal static AnimationWithDuration? Read(BinaryReader r, RenderItem entity)
        {
            byte kind = r.ReadByte(); if (kind == 0) return null;
            Header header = ReadHeader(r);
            AnimationWithDuration animation;
            if (kind == 1)
                animation = new OpacityAnimation(entity, r.ReadSingle(), r.ReadSingle(), header.Duration, header.Ease, header.Repeat);
            else if (kind == 5)
            {
                int count = SaveIO.ReadCount(r, 100_000); if (count == 0) throw new InvalidDataException("Empty saved curve.");
                var segments = ImmutableArray.CreateBuilder<ProcessedBezierSegment>(count);
                for (int i = 0; i < count; i++) segments.Add(new ProcessedBezierSegment(
                    SaveIO.ReadVector2(r), SaveIO.ReadVector2(r), SaveIO.ReadVector2(r), SaveIO.ReadVector2(r)));
                animation = new BezierMoveAnimation(entity, new ProcessedBezierCurve(segments.MoveToImmutable()), header.Duration, header.Ease);
            }
            else
            {
                Vector3 start = SaveIO.ReadVector3(r), end = SaveIO.ReadVector3(r);
                animation = kind switch
                {
                    2 => new MoveAnimation(entity, start, end, header.Duration, header.Ease, header.Repeat),
                    3 => new RotationAnimation(entity, start, end, header.Duration, header.Ease),
                    4 => new ScaleAnimation(entity, start, end, header.Duration, header.Ease, header.Repeat),
                    _ => throw new InvalidDataException("Unknown saved animation.")
                };
            }
            animation.LoadAnimationHeader(header);
            return animation;
        }
    }
}

namespace NitroSharp
{
    internal abstract partial class Animation
    {
        internal void SaveTiming(BinaryWriter w)
        {
            w.Write((int)EaseFunction); w.Write(_repeat); w.Write(_elapsed); w.Write(_initialized); w.Write(_completed);
        }
        internal void LoadTiming(AnimationPersistence.Header header)
        {
            _elapsed = header.Elapsed; _initialized = header.Initialized; _completed = header.Completed;
        }
    }
    internal abstract partial class AnimationWithDuration
    {
        internal void SaveAnimationHeader(BinaryWriter w) { w.Write(_duration.Ticks); SaveTiming(w); }
        internal void LoadAnimationHeader(AnimationPersistence.Header header) => LoadTiming(header);
    }
    internal sealed partial class OpacityAnimation
    {
        internal void WriteOpacitySave(BinaryWriter w) { w.Write(_startOpacity); w.Write(_endOpacity); }
    }
    internal abstract partial class Vector3Animation<TEntity>
    {
        internal void WriteVectorSave(BinaryWriter w) { SaveIO.WriteVector(w, _startValue); SaveIO.WriteVector(w, _endValue); }
    }
    internal sealed partial class BezierMoveAnimation
    {
        internal ProcessedBezierCurve SaveCurve => curve;
    }
}

namespace NitroSharp.Graphics
{
    internal sealed partial class FrameSequence
    {
        internal void WriteSequenceSave(BinaryWriter w)
        {
            w.Write(_frameSources.Length); foreach (string source in _frameSources) w.Write(source);
            w.Write((int)(_interval / 33)); w.Write(_finalFrameSource ?? "");
            w.Write(RemoveOnCompletion); w.Write(_loop); w.Write(FrameIndex); w.Write(_elapsed);
            w.Write(_initialized); w.Write(_stopped); w.Write(_removeRequested); w.Write(IsComplete);
        }
        internal static FrameSequence ReadSequenceSave(EntityName name, Entity? parent, int priority, BinaryReader r, GameContext ctx)
        {
            int count = SaveIO.ReadCount(r, 100_000); var frames = new string[count];
            for (int i = 0; i < count; i++) frames[i] = r.ReadString();
            int ticks = r.ReadInt32(); string final = r.ReadString(); bool remove = r.ReadBoolean(), loop = r.ReadBoolean();
            var assets = new List<AssetRef<Texture>>();
            var sequence = new FrameSequence(name, parent, priority, frames, ticks, final, remove, loop,
                path =>
                {
                    if (ctx.Content.RequestTexture(path) is not { } asset) throw new FileNotFoundException("Saved animation frame is missing.", path);
                    assets.Add(asset); return SpriteTexture.FromAsset(asset);
                },
                completed => { if (completed.RemoveOnCompletion) ctx.World.DestroyEntity(completed); },
                ready: () => { foreach (var asset in assets) if (ctx.Content.TryGet(asset) is null) return false; return true; });
            try
            {
                sequence.FrameIndex = r.ReadInt32();
                if (sequence.FrameIndex < 0 || sequence.FrameIndex >= count) throw new InvalidDataException("Invalid saved frame index.");
                sequence._elapsed = r.ReadDouble(); sequence._initialized = r.ReadBoolean(); sequence._stopped = r.ReadBoolean();
                sequence._removeRequested = r.ReadBoolean(); sequence.IsComplete = r.ReadBoolean();
                sequence._current = sequence.IsComplete ? sequence._finalFrame ?? sequence._frames[sequence.FrameIndex] : sequence._frames[sequence.FrameIndex];
                return sequence;
            }
            catch { sequence.Dispose(); throw; }
        }
    }

    internal sealed partial class DialoguePage
    {
        private int SaveLineSpacing { get; }
        internal void WritePageSave(BinaryWriter w, GameContext ctx, Func<Entity, int> id)
        {
            w.Write(_token.BoxName); w.Write(_token.BlockName); ctx.VM.WriteSaveModule(w, _token.Module);
            w.Write(_token.SubroutineIndex); w.Write(_token.Offset);
            w.Write(checked((uint)_layout.MaxBounds.Width)); w.Write(checked((uint)_layout.MaxBounds.Height));
            w.Write(SaveLineSpacing); w.Write(_verticalGlyphs);
            w.Write(_partIndex); w.Write(_waitingForInput); w.Write(_unreadText); w.Write(_clearBeforeMarkup);
            w.Write(_disableTextAnimation); w.Write(_entered); w.Write(_destroyWhenIdle); w.Write(IsIdle);
            w.Write(_charactersSinceInput); w.Write(_waitElapsed); w.Write(_codeThread is { } thread ? id(thread) : -1);
            SaveIO.WriteRuns(w, _layout.SavedRuns);
            w.Write(_layout.SavedAppendLengths.Count); foreach (int length in _layout.SavedAppendLengths) w.Write(length);
            w.Write(_layout.OpacityValues.Length); foreach (float opacity in _layout.OpacityValues) w.Write(opacity);
            w.Write(_textAnimation is not null); _textAnimation?.WriteSave(w);
            w.Write(_segments.Count);
            foreach (DialogueSegment segment in _segments)
            {
                w.Write((byte)segment.SegmentKind);
                switch (segment)
                {
                    case TextSegment text: SaveIO.WriteRuns(w, text.TextRuns); break;
                    case MarkerSegment marker: w.Write((int)marker.MarkerKind); break;
                    case VoiceSegment voice: w.Write(voice.CharacterName); w.Write(voice.FileName); w.Write((int)voice.Action); break;
                    default: throw new NotSupportedException("Cannot save dialogue segment.");
                }
            }
        }
        internal static DialoguePage ReadPageSave(EntityName name, Entity parent, int priority,
            BinaryReader r, GameContext ctx, List<Action<IReadOnlyList<Entity>>> resolve)
        {
            string box = r.ReadString(), block = r.ReadString(); NsxModule module = ctx.VM.ReadSaveModule(r);
            var token = new DialogueBlockToken(box, block, module, r.ReadInt32(), r.ReadInt32());
            var page = new DialoguePage(name, parent, priority, token, r.ReadUInt32(), r.ReadUInt32(), r.ReadInt32());
            page._verticalGlyphs = r.ReadBoolean();
            page._partIndex = r.ReadInt32();
            if (page._partIndex < 0 || page._partIndex > page._block.Parts.Length) throw new InvalidDataException("Invalid dialogue position.");
            page._waitingForInput = r.ReadBoolean(); page._unreadText = r.ReadBoolean(); page._clearBeforeMarkup = r.ReadBoolean();
            page._disableTextAnimation = r.ReadBoolean(); page._entered = r.ReadBoolean(); page._destroyWhenIdle = r.ReadBoolean();
            page.IsIdle = r.ReadBoolean(); page._charactersSinceInput = r.ReadInt32(); page._waitElapsed = r.ReadSingle();
            int thread = r.ReadInt32();
            if (thread >= 0) resolve.Add(entities => page._codeThread = (Thread)SaveIO.EntityAt(entities, thread));
            var runs = SaveIO.ReadRuns(r);
            int appends = SaveIO.ReadCount(r); int runOffset = 0;
            for (int i = 0; i < appends; i++)
            {
                int length = SaveIO.ReadCount(r);
                if (length > runs.Length - runOffset) throw new InvalidDataException("Invalid saved dialogue runs.");
                page._layout.Append(ctx.GlyphRasterizer, runs.AsSpan().Slice(runOffset, length)); runOffset += length;
            }
            if (runOffset != runs.Length) throw new InvalidDataException("Incomplete saved dialogue runs.");
            int opacityCount = SaveIO.ReadCount(r);
            if (opacityCount != page._layout.Glyphs.Length) throw new InvalidDataException("Saved dialogue glyph layout does not match installed fonts.");
            for (int i = 0; i < opacityCount; i++) page._layout.GetOpacityValuesMut(i..(i + 1))[0] = r.ReadSingle();
            page._textAnimation = r.ReadBoolean() ? DialogueTextAnimation.ReadSave(r, page._layout) : null;
            int segments = SaveIO.ReadCount(r);
            for (int i = 0; i < segments; i++)
            {
                DialogueSegment segment = (DialogueSegmentKind)r.ReadByte() switch
                {
                    DialogueSegmentKind.Text => new TextSegment(SaveIO.ReadRuns(r)),
                    DialogueSegmentKind.Marker => new MarkerSegment((MarkerKind)r.ReadInt32()),
                    DialogueSegmentKind.Voice => new VoiceSegment(r.ReadString(), r.ReadString(), (NsVoiceAction)r.ReadInt32()),
                    _ => throw new InvalidDataException("Unknown saved dialogue segment.")
                };
                page._segments.Enqueue(segment);
            }
            return page;
        }
    }
}

namespace NitroSharp.Text
{
    internal sealed partial class DialogueTextAnimation
    {
        internal void WriteSave(BinaryWriter w)
        {
            w.Write(_start); w.Write(_interval); w.Write(_fadeDuration); w.Write(_elapsed); w.Write(IsActive);
        }
        internal static DialogueTextAnimation ReadSave(BinaryReader r, TextLayout layout)
        {
            int start = r.ReadInt32();
            if (start < 0 || start > layout.Glyphs.Length) throw new InvalidDataException("Invalid saved reveal position.");
            var animation = new DialogueTextAnimation(layout, start, r.ReadSingle(), r.ReadSingle());
            animation._elapsed = r.ReadSingle(); bool active = r.ReadBoolean();
            animation.Apply(); if (!active) animation.Complete();
            return animation;
        }
    }
}

namespace NitroSharp.Media
{
    internal sealed partial class AudioEntity
    {
        internal string? SourcePath { get; set; }
        internal void WriteAudioSave(BinaryWriter w)
        {
            w.Write(SourcePath ?? throw new NotSupportedException("Cannot save audio without a source asset path."));
            w.Write((int)_kind); w.Write(_looping); w.Write(_loopRegion is not null);
            if (_loopRegion is { } loop) { w.Write(loop.Start.Ticks); w.Write(loop.End.Ticks); }
            w.Write(_playbackStarted); w.Write(_hasPlayed); w.Write(_stream?.SavePaused ?? false);
            TimeSpan elapsed = Elapsed;
            if (_looping)
            {
                TimeSpan start = _loopRegion?.Start ?? TimeSpan.Zero, end = _loopRegion?.End ?? Duration;
                if (end > start && elapsed >= end) elapsed = start + TimeSpan.FromTicks((elapsed - start).Ticks % (end - start).Ticks);
            }
            elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > Duration ? Duration : elapsed;
            w.Write(elapsed.Ticks); w.Write(_volume); w.Write(_fadeStart); w.Write(_fadeTarget);
            w.Write(_fadeDuration); w.Write(_fadeElapsed);
        }
        internal static AudioEntity ReadAudioSave(EntityName name, Entity? parent, BinaryReader r, GameContext ctx)
        {
            string path = r.ReadString(); var kind = (NsAudioKind)r.ReadInt32();
            var audio = new AudioEntity(name, parent,
                () => ctx.Content.TryOpenStream(path) ?? throw new FileNotFoundException("Saved audio is missing.", path),
                ctx.AudioContext, kind, ctx.VM) { SourcePath = path };
            try
            {
                bool looping = r.ReadBoolean();
                if (r.ReadBoolean()) audio.SetLoopRegion(TimeSpan.FromTicks(r.ReadInt64()), TimeSpan.FromTicks(r.ReadInt64()));
                audio.SetLooping(looping);
                bool started = r.ReadBoolean(); audio._hasPlayed = r.ReadBoolean(); bool paused = r.ReadBoolean();
                TimeSpan position = TimeSpan.FromTicks(r.ReadInt64());
                audio._volume = r.ReadSingle(); audio._fadeStart = r.ReadSingle(); audio._fadeTarget = r.ReadSingle();
                audio._fadeDuration = r.ReadSingle(); audio._fadeElapsed = r.ReadSingle(); audio.ApplyVolume();
                if (started)
                {
                    audio._stream!.RestorePosition(position); audio.Play();
                    if (paused) audio._stream.Pause();
                }
                return audio;
            }
            catch { audio.Dispose(); throw; }
        }
    }
    internal sealed partial class MediaStream
    {
        private double _savedPositionOffset;
        internal bool SavePaused => !_unpauseSignal.IsSet;
        internal void RestorePosition(TimeSpan position)
        {
            if (_started) throw new InvalidOperationException("A saved playback position must be restored before decoding starts.");
            if (position < TimeSpan.Zero || position > Duration) throw new InvalidDataException("Invalid saved media position.");
            _savedPositionOffset = position.TotalSeconds;
            if (position > TimeSpan.Zero) Seek(position);
        }
    }
}

namespace NitroSharp.Graphics
{
    internal sealed partial class Video
    {
        internal string? SourcePath { get; set; }
        private bool SaveLoop { get; }
        private bool _restorePauseAfterFrame;
        internal void WriteVideoSave(BinaryWriter w)
        {
            w.Write(SourcePath ?? throw new NotSupportedException("Cannot save video without a source asset path."));
            w.Write(SaveLoop); w.Write(_alpha); w.Write(_stopped); w.Write(_media.SavePaused || _restorePauseAfterFrame);
            TimeSpan elapsed = Elapsed;
            if (SaveLoop && Duration > TimeSpan.Zero && elapsed >= Duration)
                elapsed = TimeSpan.FromTicks(elapsed.Ticks % Duration.Ticks);
            elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > Duration ? Duration : elapsed;
            w.Write(elapsed.Ticks);
        }
        internal static Video ReadVideoSave(EntityName name, Entity? parent, int priority, BinaryReader r, GameContext ctx)
        {
            string path = r.ReadString(); bool loop = r.ReadBoolean(), alpha = r.ReadBoolean();
            Stream stream = ctx.Content.TryOpenStream(path) ?? throw new FileNotFoundException("Saved movie is missing.", path);
            var video = new Video(name, parent, priority, stream, ctx.RenderContext.GraphicsDevice,
                ctx.AudioContext, loop, alpha) { SourcePath = path };
            try
            {
                video._stopped = r.ReadBoolean(); bool paused = r.ReadBoolean();
                video._media.RestorePosition(TimeSpan.FromTicks(r.ReadInt64()));
                video._restorePauseAfterFrame = paused && !video._stopped;
                video.Start(); if (video._stopped) video._media.Pause();
                return video;
            }
            catch { video.Dispose(); throw; }
        }
    }
}
