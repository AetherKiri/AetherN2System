using System;
using System.Numerics;
using NitroSharp.Content;
using NitroSharp.Graphics.Core;
using NitroSharp.NsScript.VM;
using NitroSharp.Text;
using NitroSharp.Utilities;
using Veldrid;

namespace NitroSharp.Graphics
{
    internal enum BlendMode : byte
    {
        Alpha,
        Additive,
        ReverseSubtractive,
        Multiplicative
    }

    internal enum FilterMode : byte
    {
        Point,
        Linear
    }

    internal sealed class RenderContext : IDisposable
    {
        private readonly ShaderLibrary _shaderLibrary;

        private readonly CommandList _drawCommands;
        private readonly CommandList _secondaryCommandList;
        private readonly CommandList _captureCommands;

        private readonly Swapchain? _mainSwapchain;
        private readonly RenderTarget _swapchainTarget;
        private readonly RenderTarget _presentedTarget;
        private readonly DrawBatch _offscreenBatch;
        private bool _captureFrame;

        private readonly TextureCache _textureCache;

        public RenderContext(
            GameWindow window,
            Config config,
            GameProfile gameProfile,
            GraphicsDevice graphicsDevice,
            Swapchain? swapchain,
            ContentManager contentManager,
            GlyphRasterizer glyphRasterizer,
            SystemVariableLookup systemVariables)
        {
            DesignResolution = gameProfile.DesignResolution;
            RenderResolution = window.Size;
            Window = window;
            GraphicsDevice = graphicsDevice;
            ResourceFactory = graphicsDevice.ResourceFactory;
            Content = contentManager;
            GlyphRasterizer = glyphRasterizer;
            SystemVariables = systemVariables;
            _mainSwapchain = swapchain;
            _shaderLibrary = new ShaderLibrary(graphicsDevice);

            _swapchainTarget = swapchain is null ? new RenderTarget(graphicsDevice, window.Size) : RenderTarget.Swapchain(graphicsDevice, swapchain.Framebuffer);
            _presentedTarget = new RenderTarget(graphicsDevice, _swapchainTarget.Size, _swapchainTarget.ColorTarget.Format);
            OffscreenTarget = new RenderTarget(graphicsDevice, _swapchainTarget.Size);
            OffscreenTexturePool = new ResourcePool<Texture>(CreateOffscreenTexture, initialSize: 4);

            TransferCommands = ResourceFactory.CreateCommandList();
            TransferCommands.Name = "Transfer commands";
            _drawCommands = ResourceFactory.CreateCommandList();
            _drawCommands.Name = "Draw commands (primary)";
            _secondaryCommandList = ResourceFactory.CreateCommandList();
            _secondaryCommandList.Name = "Secondary";
            _captureCommands = ResourceFactory.CreateCommandList();
            _captureCommands.Name = "Screen capture";
            _captureCommands.Begin();
            _captureCommands.SetFramebuffer(_presentedTarget.Framebuffer);
            _captureCommands.ClearColorTarget(0, RgbaFloat.Black);
            _captureCommands.End();
            graphicsDevice.SubmitCommands(_captureCommands);
            CommandListPool = new ResourcePool<CommandList>(ResourceFactory.CreateCommandList, initialSize: 2);

            double scaledDesignArea = Math.Min(
                (double)RenderResolution.Width * DesignResolution.Height,
                (double)RenderResolution.Height * DesignResolution.Width
            );
            double designArea = (double)DesignResolution.Width * DesignResolution.Height;
            double viewportWidth = scaledDesignArea / DesignResolution.Height;
            double viewportHeight = scaledDesignArea / DesignResolution.Width;
            RenderScale = (float)(scaledDesignArea / designArea);
            ViewportLeft = (float)((RenderResolution.Width - viewportWidth) / 2);
            ViewportTop = (float)((RenderResolution.Height - viewportHeight) / 2);
            ViewportRight = (float)((RenderResolution.Width + viewportWidth) / 2);
            ViewportBottom = (float)((RenderResolution.Height + viewportHeight) / 2);

            OrthoProjection = ViewProjection.CreateOrtho(
                graphicsDevice,
                new ScreenRectU(PointU<ScreenPixel>.Zero, RenderResolution)
            );

            PerspectiveViewProjection = ViewProjection.CreatePerspective(
                graphicsDevice,
                fov: MathF.PI / 3,
                (float)RenderResolution.Width / RenderResolution.Height
            );

            ShaderResources = new ShaderResources(
                graphicsDevice,
                _shaderLibrary,
                _swapchainTarget.OutputDescription,
                OrthoProjection.ResourceLayout
            );

            ResourceSetCache = new ResourceSetCache(ResourceFactory);
            _textureCache = new TextureCache(GraphicsDevice);
            WhiteTexture = CreateWhiteTexture();

            Quads = new PrimitiveBatch<QuadVertex>(
                graphicsDevice,
                new PrimitiveTemplate(QuadPrimitive.IndexPattern, QuadPrimitive.VertexCount),
                initialCapacity: 512
            );
            QuadsUV3 = new PrimitiveBatch<QuadVertexUV3>(
                graphicsDevice,
                new PrimitiveTemplate(QuadPrimitive.IndexPattern, QuadPrimitiveUV3.VertexCount),
                initialCapacity: 4
            );
            Cubes = new PrimitiveBatch<CubeVertex>(
                graphicsDevice,
                new PrimitiveTemplate(CubePrimitive.IndexPattern, CubePrimitive.VertexCount),
                initialCapacity: 1
            );

            Text = new TextRenderContext(GraphicsDevice, GlyphRasterizer, _textureCache);
            _mainBatch = new DrawBatch(this);
            _offscreenBatch = new DrawBatch(this);

            Icons = LoadIcons(gameProfile);

            CustomSampler = ResourceFactory.CreateSampler(CustomSamplerDesc);
        }

        public Sampler CustomSampler { get; set; }

        public static readonly SamplerDescription CustomSamplerDesc = new()
        {
            AddressModeU = SamplerAddressMode.Clamp,
            AddressModeV = SamplerAddressMode.Clamp,
            AddressModeW = SamplerAddressMode.Clamp,
            Filter = SamplerFilter.MinLinear_MagLinear_MipLinear,
            LodBias = 0,
            MinimumLod = 0,
            MaximumLod = uint.MaxValue,
            MaximumAnisotropy = 0,
        };

        public GameWindow Window { get; }
        public DesignSizeU DesignResolution { get; }
        public ScreenSizeU RenderResolution { get; }
        public ViewProjection OrthoProjection { get; }
        public ViewProjection PerspectiveViewProjection { get; }
        public PrimitiveBatch<QuadVertex> Quads { get; }
        public PrimitiveBatch<QuadVertexUV3> QuadsUV3 { get; }
        public PrimitiveBatch<CubeVertex> Cubes { get; }

        public float RenderScale { get; }
        public float ViewportLeft { get; }
        public float ViewportTop { get; }
        public float ViewportRight { get; }
        public float ViewportBottom { get; }

        public GraphicsDevice GraphicsDevice { get; }
        public ResourceFactory ResourceFactory { get; }

        public CommandList TransferCommands { get; }
        public ResourcePool<CommandList> CommandListPool { get; }

        public ContentManager Content { get; }
        public GlyphRasterizer GlyphRasterizer { get; }
        public ShaderResources ShaderResources { get; }

        public ResourceSetCache ResourceSetCache { get; }
        public Texture WhiteTexture { get; }

        public TextRenderContext Text { get; }

        private readonly DrawBatch _mainBatch;
        private DrawBatch? _captureBatch;
        public DrawBatch MainBatch => _captureBatch ?? _mainBatch;

        public RenderTarget OffscreenTarget { get; }
        public ResourcePool<Texture> OffscreenTexturePool { get; }

        public AnimatedIcons Icons { get; }

        public SystemVariableLookup SystemVariables { get; }

        public static GraphicsBackend GetDefaultBackend()
        {
            if (GraphicsDevice.IsBackendSupported(GraphicsBackend.Direct3D11))
            {
                return GraphicsBackend.Direct3D11;
            }
            if (GraphicsDevice.IsBackendSupported(GraphicsBackend.Metal))
            {
                return GraphicsBackend.Metal;
            }
            if (GraphicsDevice.IsBackendSupported(GraphicsBackend.Vulkan))
            {
                return GraphicsBackend.Vulkan;
            }

            return GraphicsBackend.OpenGL;
        }

        private Viewport CalculateViewport()
        {
            float designAspect = (float)DesignResolution.Width / DesignResolution.Height;
            float windowAspect = (float)RenderResolution.Width / RenderResolution.Height;

            float viewportWidth, viewportHeight;
            if (windowAspect > designAspect)
            {
                // Window is wider - letterbox sides
                viewportHeight = RenderResolution.Height;
                viewportWidth = MathF.Round(viewportHeight * designAspect);
            }
            else
            {
                // Window is taller - letterbox top/bottom
                viewportWidth = RenderResolution.Width;
                viewportHeight = MathF.Round(viewportWidth / designAspect);
            }

            float viewportLeft = MathF.Round((RenderResolution.Width - viewportWidth) / 2.0f);
            float viewportTop = MathF.Round((RenderResolution.Height - viewportHeight) / 2.0f);

            return new Viewport(
                viewportLeft,
                viewportTop,
                viewportWidth,
                viewportHeight,
                minDepth: 0f,
                maxDepth: 1f
            );
        }

        private Texture CreateWhiteTexture()
        {
            var textureDesc = TextureDescription.Texture2D(
                width: 1, height: 1, mipLevels: 1, arrayLayers: 1,
                PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging
            );
            Texture stagingWhite = ResourceFactory.CreateTexture(in textureDesc);
            MappedResourceView<RgbaByte> pixels = GraphicsDevice.Map<RgbaByte>(
                stagingWhite, MapMode.Write
            );
            pixels[0] = RgbaByte.White;
            GraphicsDevice.Unmap(stagingWhite);

            textureDesc.Usage = TextureUsage.Sampled;
            Texture texture = ResourceFactory.CreateTexture(in textureDesc);

            TransferCommands.Begin();
            TransferCommands.CopyTexture(stagingWhite, texture);
            TransferCommands.End();
            GraphicsDevice.SubmitCommands(TransferCommands);
            stagingWhite.Dispose();
            return texture;
        }

        private Texture CreateOffscreenTexture()
        {
            ScreenSizeU size = _swapchainTarget.Size;
            var desc = TextureDescription.Texture2D(
                size.Width, size.Height,
                mipLevels: 1, arrayLayers: 1,
                PixelFormat.B8_G8_R8_A8_UNorm,
                TextureUsage.Sampled
            );
            return ResourceFactory.CreateTexture(in desc);
        }

        private AnimatedIcons LoadIcons(GameProfile gameProfile)
        {
            Icon? waitLine = null;
            if (Icon.Exists(Content, gameProfile.IconPathPatterns.WaitLine))
            {
                waitLine = Icon.Load(this, gameProfile.IconPathPatterns.WaitLine);
            }
            return new AnimatedIcons(waitLine);
        }

        public void BeginFrame(in FrameStamp frameStamp)
        {
            BeginFrame(frameStamp, _swapchainTarget);
        }

        public void BeginFrame(in FrameStamp frameStamp, RenderTarget renderTarget)
        {
            _captureFrame = ReferenceEquals(renderTarget, _swapchainTarget);
            _drawCommands.Begin();
            MainBatch.Begin(_drawCommands, renderTarget);

            _secondaryCommandList.Begin();

            Quads.Begin();
            QuadsUV3.Begin();
            Cubes.Begin();
            _textureCache.BeginFrame(frameStamp);
            ResourceSetCache.BeginFrame(frameStamp);
            Text.BeginFrame();
            TransferCommands.Begin();
        }

        public DrawBatch BeginOffscreenBatch(RenderTarget renderTarget)
        {
            _offscreenBatch.Begin(_secondaryCommandList, renderTarget);
            return _offscreenBatch;
        }

        public void ResolveGlyphs()
        {
            Text.ResolveGlyphs();
            _textureCache.EndFrame(TransferCommands);
        }

        public Texture ReadbackTexture(CommandList cl, Texture texture)
        {
            Texture staging = ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                texture.Width, texture.Height,texture.MipLevels, texture.ArrayLayers,
                texture.Format, TextureUsage.Staging
            ));

            cl.CopyTexture(texture, staging);
            return staging;
        }

        public void EndFrame()
        {
            MainBatch.End();
            if (_captureFrame)
                _drawCommands.CopyTexture(_swapchainTarget.ColorTarget, _presentedTarget.ColorTarget);

            _secondaryCommandList.End();
            _drawCommands.End();
            ResourceSetCache.EndFrame();

            Text.EndFrame(TransferCommands);
            if (_textureCache.IsActive)
            {
                _textureCache.EndFrame(TransferCommands);
            }
            Quads.End(TransferCommands);
            QuadsUV3.End(TransferCommands);
            Cubes.End(TransferCommands);
            TransferCommands.End();

            GraphicsDevice.SubmitCommands(TransferCommands);
            GraphicsDevice.SubmitCommands(_secondaryCommandList);
            GraphicsDevice.SubmitCommands(_drawCommands);
        }

        public SpriteTexture CapturePresentedFrame(DesignRectU? sourceRectangle = null)
        {
            Texture source = _presentedTarget.ColorTarget;
            // Keep only the game viewport. Preserve fractional viewport boundaries in
            // UVs so replaying a snapshot does not introduce bars or shift source crops.
            uint left = (uint)MathF.Floor(ViewportLeft);
            uint top = (uint)MathF.Floor(ViewportTop);
            uint width = (uint)MathF.Ceiling(ViewportRight) - left;
            uint height = (uint)MathF.Ceiling(ViewportBottom) - top;
            var uvOrigin = new Vector2((ViewportLeft - left) / width, (ViewportTop - top) / height);
            var uvExtent = new Vector2((ViewportRight - ViewportLeft) / width, (ViewportBottom - ViewportTop) / height);
            Texture snapshot = ResourceFactory.CreateTexture(TextureDescription.Texture2D(
                width, height, mipLevels: 1, arrayLayers: 1,
                source.Format, TextureUsage.Sampled
            ));
            try
            {
                // Script execution happens while the next draw list is being recorded.
                // Submit this copy separately so it observes the last completed frame.
                _captureCommands.Begin();
                _captureCommands.CopyTexture(
                    source, left, top, 0, 0, 0,
                    snapshot, 0, 0, 0, 0, 0,
                    width, height, 1, 1);
                _captureCommands.End();
                GraphicsDevice.SubmitCommands(_captureCommands);
                return SpriteTexture.FromOwnedTexture(snapshot, DesignResolution, sourceRectangle, uvOrigin, uvExtent);
            }
            catch
            {
                snapshot.Dispose();
                throw;
            }
        }

        internal SpriteTexture CaptureScene(GameContext context, ReadOnlySpan<RenderItem> items)
        {
            var target = CreateSceneTarget();
            try
            {
                CaptureScene(context, items, target);
                // Retain the entire target until the scene transition releases
                // its snapshot; its capture is submitted before the main scene.
                return SceneTexture(target);
            }
            catch
            {
                GraphicsDevice.DisposeWhenIdle(target);
                throw;
            }
        }

        internal RenderTarget CreateSceneTarget()
            => new(GraphicsDevice, RenderResolution, _swapchainTarget.ColorTarget.Format);

        internal SpriteTexture SceneTexture(RenderTarget target)
        {
            var origin = new Vector2(ViewportLeft / RenderResolution.Width, ViewportTop / RenderResolution.Height);
            var extent = new Vector2((ViewportRight - ViewportLeft) / RenderResolution.Width,
                (ViewportBottom - ViewportTop) / RenderResolution.Height);
            return SpriteTexture.FromOwnedTarget(target, DesignResolution, origin, extent);
        }

        internal void CaptureScene(GameContext context, ReadOnlySpan<RenderItem> items, RenderTarget target)
        {
            if (_captureBatch is not null) throw new InvalidOperationException("Nested scene capture is unsupported.");
            try
            {
                _captureBatch = BeginOffscreenBatch(target);
                _captureBatch.Clear(RgbaFloat.Black);
                foreach (RenderItem item in items)
                    if (!item.IsPendingDestruction) item.Render(context);
                _captureBatch.End();
            }
            finally { _captureBatch = null; }
        }

        internal void CompositeScene(SpriteTexture previous, SpriteTexture incoming, RenderTarget target, float coverage)
        {
            DrawBatch batch = BeginOffscreenBatch(target);
            batch.Clear(RgbaFloat.Black);
            Draw(previous, 1);
            Draw(incoming, coverage);
            batch.End();

            void Draw(SpriteTexture texture, float alpha)
            {
                (Vector2 topLeft, Vector2 bottomRight) = texture.GetTexCoords(this);
                var size = DesignResolution.ToFloatSize();
                var quad = QuadPrimitive.Create(size, GetTransformMatrix(Transform.Default, size, true, false),
                    topLeft, bottomRight, new Vector4(1, 1, 1, alpha));
                batch.PushQuad(quad, texture.Resolve(this), WhiteTexture, Vector2.Zero,
                    BlendMode.Alpha, FilterMode.Point, null);
            }
        }

        public unsafe void CopyRgba(Span<byte> destination)
        {
            using CommandList commands = ResourceFactory.CreateCommandList();
            commands.Begin();
            Texture staging = _swapchainTarget.ReadBack(commands, ResourceFactory);
            commands.End();
            GraphicsDevice.SubmitCommands(commands);
            GraphicsDevice.WaitForIdle();
            MappedResource map = GraphicsDevice.Map(staging, MapMode.Read);
            try
            {
                int width = (int)RenderResolution.Width;
                for (int y = 0; y < RenderResolution.Height; y++)
                {
                    var source = new ReadOnlySpan<byte>((byte*)map.Data + y * map.RowPitch, width * 4);
                    Span<byte> target = destination.Slice(y * width * 4, width * 4);
                    source.CopyTo(target);
                    if (staging.Format == PixelFormat.B8_G8_R8_A8_UNorm)
                        for (int x = 0; x < width * 4; x += 4)
                            (target[x], target[x + 2]) = (target[x + 2], target[x]);
                }
            }
            finally { GraphicsDevice.Unmap(staging); }
        }

        public void Present()
        {
            if (_mainSwapchain is not null) GraphicsDevice.SwapBuffers(_mainSwapchain);
        }

        public Sampler GetSampler(FilterMode filterMode)
        {
            return filterMode switch
            {
                FilterMode.Linear => CustomSampler,
                FilterMode.Point => GraphicsDevice.PointSampler,
                _ => throw ThrowHelper.UnexpectedValueOf<FilterMode>()
            };
        }

        public Matrix4x4 GetTransformMatrix(Transform transform, DesignSize size, bool useScaling, bool aligned)
            => CreateTransformMatrix(transform, size, RenderScale, new Vector2(ViewportLeft, ViewportTop), useScaling, aligned);

        internal static Matrix4x4 CreateTransformMatrix(Transform transform, DesignSize size, float renderScale,
            Vector2 viewportOffset, bool useScaling, bool aligned)
        {
            var bounds = size.ToVector2();
            var center = new Vector3((transform.Pivot ?? new Vector2(0.5f) * bounds) * (useScaling ? renderScale : 1), 0);
            var scale = Matrix4x4.CreateScale(transform.Scale, center);
            Matrix4x4 rot = Matrix4x4.CreateRotationZ(rad(transform.Rotation.Z), center)
                * Matrix4x4.CreateRotationY(rad(transform.Rotation.Y), center)
                * Matrix4x4.CreateRotationX(rad(transform.Rotation.X), center);
            Vector3 finalPosition = new Vector3(viewportOffset, 0) + transform.Position * renderScale;
            if (aligned)
            {
                finalPosition = new Vector3(
                    (float)Math.Floor(finalPosition.X),
                    (float)Math.Floor(finalPosition.Y),
                    (float)Math.Floor(finalPosition.Z)
                );
            }
            var translation = Matrix4x4.CreateTranslation(finalPosition);
            Matrix4x4 matrix = scale * rot * translation;
            if (useScaling)
            {
                matrix = Matrix4x4.CreateScale(renderScale) * matrix;
            }
            return matrix;

            static float rad(float deg) => deg / 180.0f * MathF.PI;
        }

        public void Dispose()
        {
            GraphicsDevice.WaitForIdle();
            OrthoProjection.Dispose();
            Icons.Dispose();
            TransferCommands.Dispose();
            _drawCommands.Dispose();
            _secondaryCommandList.Dispose();
            _captureCommands.Dispose();
            CommandListPool.Dispose();
            ShaderResources.Dispose();
            WhiteTexture.Dispose();
            Text.Dispose();
            Quads.Dispose();
            Cubes.Dispose();
            QuadsUV3.Dispose();
            PerspectiveViewProjection.Dispose();
            CustomSampler.Dispose();
            _textureCache.Dispose();
            ResourceSetCache.Dispose();
            _swapchainTarget.Dispose();
            _presentedTarget.Dispose();
            OffscreenTarget.Dispose();
            OffscreenTexturePool.Dispose();
            _shaderLibrary.Dispose();
            _mainSwapchain?.Dispose();
            GraphicsDevice.Dispose();
        }
    }
}
