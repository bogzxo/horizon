namespace Horizon.Rendering.Spriting
{
    public struct SpriteAnimationDefinition
    {
        public float FrameTime { get; set; }
        public float Timer { get; set; }
        public uint Index { get; set; }
        public uint Length { get; set; }

        public void ResetIndex() => Index = 0;

        public SpriteDefinition FirstFrame { get; set; }
        public float Fuzz { get; set; }
    }
}