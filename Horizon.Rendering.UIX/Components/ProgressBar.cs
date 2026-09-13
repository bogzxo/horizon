using System;
using System.Collections.Generic;
using System.Numerics;
using Horizon.Engine;
using Horizon.HIDL.Runtime;
using ValueType = Horizon.HIDL.Runtime.ValueType;
using Horizon.Rendering.Text;
using Silk.NET.OpenGL;

namespace Horizon.Rendering.UIX.Components
{
    public class ProgressBar : UIComponent
    {
        public NativeValue ProgressValue { get; init; }
        
        private float _progress = 0.0f; // 0.0 to 1.0
        
        public float Progress
        {
            get => _progress;
            set 
            {
                _progress = Math.Clamp(value, 0f, 1f);

                _fillSprite.StencilTransform.Size = new Vector2(_fillSprite.GetSize().X * _progress, _fillSprite.GetSize().Y) * Scale;
                _backgroundSprite.Transform.Size = _fillSprite.Transform.Size = _backgroundSprite.GetSize() * Scale;
                _textLabel.Text = $"{(_progress * 100):0}%";
                comp.GlyphRenderer.MarkDirty();
            }
        }

        private UISprite _backgroundSprite;
        private UISprite _fillSprite;
        private TextLabel _textLabel;
        private string _labelId;

        public Vector2 Position { get;
            set
            {
                _backgroundSprite.Transform.Position = _textLabel.Transform.Position = value;
                _fillSprite.Transform.Position = _fillSprite.StencilTransform.Position = new Vector2(value.X - (_fillSprite.GetSize().X * Scale.X) / 2, value.Y);
            }
        }

        public Vector2 Scale { get;
            set => this._fillSprite.Transform.Size =
                this._fillSprite.StencilTransform.Size = this._backgroundSprite.Transform.Size = value;
        } = new Vector2(4, 2);

        private Vector2 spawnPos;
        public ProgressBar(in Vector2 position)
        {
            this.spawnPos = position;
            _labelId = Guid.NewGuid().ToString();
            
            this.ProgressValue = new NativeValue(
                () => new NumberValue(_progress), 
                (val) =>
                {
                    if (val is NumberValue numVal) 
                    {
                        Progress = numVal.Value;
                        if (_textLabel != null)
                        {
                            _textLabel.Text = $"{(_progress * 100):0}%";
                            comp.GlyphRenderer.MarkDirty();
                        }
                    }
                    else throw new Exception("Progress value has to be a number!");
                }
            );
            
            this.Object = new ObjectValue(new Dictionary<string, IRuntimeValue>()
            {
                {"progress", this.ProgressValue}
            });
        }

        private UICompositor comp;
        public override void Initialize(UICompositor compositor)
        {
            Engine.GameEngine.Instance.GL.Enable(EnableCap.DepthTest);
            this.comp = compositor;
            
            _backgroundSprite = new UISprite(compositor);
            _backgroundSprite.SetAnimation("progress_frame");
            
            _fillSprite = new UISprite(compositor);
            _fillSprite.UseStencilBuffer = true;
            _fillSprite.Transform.Origin = _fillSprite.Transform.Origin = Origin.Left;
            _fillSprite.StencilTransform.Origin = _fillSprite.Transform.Origin = Origin.Left;
            _fillSprite.SetAnimation("progress_fill");
            
            compositor.SpriteBatch.Add(_backgroundSprite);
            compositor.SpriteBatch.Add(_fillSprite);
            
            _textLabel = new TextLabel() { Text = "100%", Origin = Origin.Center};
            compositor.GlyphRenderer.AddLabel(_labelId, _textLabel);

            this.Position = spawnPos;
        }

        public override void UpdateState(float dt, in UICompositor compositor, Vector2 mousePos, bool clicked)
        {
            Progress += dt / 10.0f;
        }
    }
}
