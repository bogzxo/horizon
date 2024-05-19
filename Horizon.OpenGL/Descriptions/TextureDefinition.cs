using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Descriptions
{
    /// <summary>
    /// Low level texture definition.
    /// </summary>
    public readonly struct TextureDefinition
    {
        public readonly InternalFormat InternalFormat { get; init; }
        public readonly PixelFormat PixelFormat { get; init; }
        public readonly PixelType PixelType { get; init; }
        public readonly TextureTarget TextureTarget { get; init; }

        public static TextureDefinition RgbaUnsignedByte { get; } =
            new TextureDefinition
            {
                InternalFormat = InternalFormat.Rgba8,
                PixelFormat = PixelFormat.Rgba,
                PixelType = PixelType.UnsignedByte,
                TextureTarget = TextureTarget.Texture2D
            };

        public static TextureDefinition RgbaUnsignedInt { get; } =
           new TextureDefinition
           {
               InternalFormat = InternalFormat.Rgba32ui,
               PixelFormat = PixelFormat.RgbaInteger,
               PixelType = PixelType.UnsignedInt,
               TextureTarget = TextureTarget.Texture2D
           };

        public static TextureDefinition RgbaFloat { get; } =
            new TextureDefinition
            {
                InternalFormat = InternalFormat.Rgba,
                PixelFormat = PixelFormat.Rgba,
                PixelType = PixelType.Float,
                TextureTarget = TextureTarget.Texture2D
            };
        public static TextureDefinition DepthComponent { get; } =
           new TextureDefinition
           {
               InternalFormat = InternalFormat.DepthComponent,
               PixelFormat = PixelFormat.DepthComponent,
               PixelType = PixelType.Float,
               TextureTarget = TextureTarget.Texture2D
           };

        public static TextureDefinition DepthComponentDefault { get; } =
            new TextureDefinition
            {
                InternalFormat = InternalFormat.DepthComponent,
                PixelFormat = PixelFormat.DepthComponent,
                PixelType = PixelType.Float,
                TextureTarget = TextureTarget.Texture2D
            };
        public static TextureDefinition DepthStencil { get; } =
            new TextureDefinition
            {
                InternalFormat = InternalFormat.Depth24Stencil8,
                PixelFormat = PixelFormat.DepthStencil,
                PixelType = PixelType.UnsignedInt248,
                TextureTarget = TextureTarget.Texture2D
            };

    }
}