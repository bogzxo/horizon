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
        public readonly TextureParameter[] Parameters { get; init; }

        public static TextureDefinition RgbaUnsignedByte { get; } =
            new TextureDefinition
            {
                InternalFormat = InternalFormat.Rgba8,
                PixelFormat = PixelFormat.Rgba,
                PixelType = PixelType.UnsignedByte,
                TextureTarget = TextureTarget.Texture2D,
                Parameters = [
                    new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
                    new () { Name = TextureParameterName.TextureMaxLevel, Value = 0 },
                    ]
            };

        public static TextureDefinition RgbaUnsignedByteCubeMap { get; } =
           new TextureDefinition
           {
               InternalFormat = InternalFormat.Rgba8,
               PixelFormat = PixelFormat.Rgba,
               PixelType = PixelType.UnsignedByte,
               TextureTarget = TextureTarget.TextureCubeMap,
               Parameters = [
                    new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
                    new () { Name = TextureParameterName.TextureMaxLevel, Value = 0 },
                    ]
           };

        public static TextureDefinition RgbaUnsignedInt { get; } =
           new TextureDefinition
           {
               InternalFormat = InternalFormat.Rgba32ui,
               PixelFormat = PixelFormat.RgbaInteger,
               PixelType = PixelType.UnsignedInt,
               TextureTarget = TextureTarget.Texture2D,
               Parameters = [
                    new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
                    new () { Name = TextureParameterName.TextureMaxLevel, Value = 0 },
                    ]
           };

        public static TextureDefinition RgbaFloat { get; } =
            new TextureDefinition
            {
                InternalFormat = InternalFormat.Rgba,
                PixelFormat = PixelFormat.Rgba,
                PixelType = PixelType.Float,
                TextureTarget = TextureTarget.Texture2D,
                Parameters = [
                    new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
                    new () { Name = TextureParameterName.TextureMaxLevel, Value = 0 },
                    ]
            };
        public static TextureDefinition DepthComponent { get; } =
           new TextureDefinition
           {
               InternalFormat = InternalFormat.DepthComponent,
               PixelFormat = PixelFormat.DepthComponent,
               PixelType = PixelType.Float,
               TextureTarget = TextureTarget.Texture2D,
               Parameters = [
                    new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Linear },
                    new () { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
                    new () { Name = TextureParameterName.TextureMaxLevel, Value = 0 },
                    ]
           };

        public static TextureDefinition DepthStencil { get; } =
            new TextureDefinition
            {
                InternalFormat = InternalFormat.Depth24Stencil8,
                PixelFormat = PixelFormat.DepthStencil,
                PixelType = PixelType.UnsignedInt248,
                TextureTarget = TextureTarget.Texture2D,
                Parameters = [
                    new () { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
                    new () { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Nearest},
                    new () { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Nearest },
                    new () { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
                    new () { Name = TextureParameterName.TextureMaxLevel, Value = 0 },
                    ]
            };

    }
}