using System;
using System.Collections.Generic;
using System.Numerics;
using Horizon.HIDL.Runtime;
using ValueType = Horizon.HIDL.Runtime.ValueType;
using Horizon.Rendering.Text;
using Horizon.Rendering.UI;

namespace Horizon.Rendering.UIX.Components
{
    public class ProgressBar : UIComponent
    {
        public ObjectValue Object { get; init; }
        public NativeValue ProgressValue { get; init; }
        
        private float _progress = 1.0f; // 0.0 to 1.0
        
        public float Progress
        {
            get => _progress;
            set 
            {
                _progress = Math.Clamp(value, 0f, 1f);
            }
        }

        private UISprite _backgroundSprite;
        private UISprite _fillSprite;
        private TextLabel _textLabel;
        private string _labelId;

        public ProgressBar()
        {
            _labelId = Guid.NewGuid().ToString();
            
            this.ProgressValue = new NativeValue(
                () => new NumberValue(_progress), 
                (val) =>
                {
                    if (val is NumberValue numVal) 
                    {
                        Progress = numVal.Value;
                        if (_textLabel != null)
                            _textLabel.Text = $"{(_progress * 100):0}%";
                    }
                    else throw new Exception("Progress value has to be a number!");
                }
            );
            
            this.Object = new ObjectValue(new Dictionary<string, IRuntimeValue>()
            {
                {"progress", this.ProgressValue}
            });
        }

        public override void Initialize(UICompositor compositor)
        {
            _backgroundSprite = new UISprite(new Vector2(200, 30));
            _fillSprite = new UISprite(new Vector2(200, 30));
            
            if (compositor.SharedSheet != null)
            {
                _backgroundSprite.ConfigureSpriteSheet(compositor.SharedSheet, "orange_square");
                _fillSprite.ConfigureSpriteSheet(compositor.SharedSheet, "red_square");
            }

            compositor.SpriteBatch.Add(_backgroundSprite);
            compositor.SpriteBatch.Add(_fillSprite);
            
            _textLabel = new TextLabel() { Text = "100%" };
            compositor.GlyphRenderer.AddLabel(_labelId, _textLabel);
        }

        public override void UpdateState(float dt)
        {
            if (_fillSprite != null)
            {
                // Scale the fill sprite based on progress
                _fillSprite.Transform.Size = new Vector2(200 * _progress, 30);
                
                // Keep text aligned
                _textLabel.Transform.Position = _backgroundSprite.Transform.Position + new Vector2(100, 15);
            }
        }
    }
}
