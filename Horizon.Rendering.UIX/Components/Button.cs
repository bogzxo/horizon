using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Horizon.HIDL.Runtime;
using ValueType = Horizon.HIDL.Runtime.ValueType;
using Horizon.Rendering.Text;
using Horizon.Rendering.UI;
using System.Numerics;

namespace Horizon.Rendering.UIX.Components
{
    public class Button(in string label, Vector2 position, float sprScale = 1.0f, float lblScale = 1.0f) : UIComponent
    {
        private string _text = label;
        
        public Action OnPressed { get; set; }
        private IRuntimeValue _hidlCallback;
        
        private UISprite _backgroundSprite;
        private TextLabel _textLabel;
        private string _labelId = Guid.NewGuid().ToString();

        public override void Initialize(UICompositor compositor)
        {
            // Initialize the background sprite
            _backgroundSprite = new UISprite(compositor.SharedSheet.SpriteSize);
            _backgroundSprite.ConfigureSpriteSheet(compositor.SharedSheet, "btn_normal");
            _backgroundSprite.IsAnimated = false;
            _backgroundSprite.Transform.Position = position;
            _backgroundSprite.Transform.Size *= sprScale;

            compositor.SpriteBatch.Add(_backgroundSprite);

            // Initialize the text label
            _textLabel = new TextLabel
            {
                Text = _text,
                Origin = Origin.Center,
                Transform =
                {
                    Size = Vector2.One * lblScale
                }
            };
            compositor.GlyphRenderer.AddLabel(_labelId, _textLabel);

            var onPressedValue = new NativeValue(
                () => _hidlCallback ?? new NullValue(),
                (val) =>
                {
                    _hidlCallback = val;
                }
            );

            this.Object = new ObjectValue(new Dictionary<string, IRuntimeValue>()
            {
                {"on_pressed", onPressedValue}
            });

            this.OnPressed = () =>
            {
                if (_hidlCallback == null) return;

                switch (_hidlCallback)
                {
                    case NativeFunctionValue nfv:
                        nfv.Callback.Invoke([], compositor.Runtime.GlobalScope);
                        break;
                    case FunctionValue fv:
                        {
                            var scope = new Horizon.HIDL.Runtime.Environment(fv.Environment);
                            foreach (var stmt in fv.Body)
                                compositor.Runtime.Interpreter.Evaluate(stmt, scope);
                            break;
                        }
                    case AnonymousFunctionValue afv:
                        {
                            var scope = new Horizon.HIDL.Runtime.Environment(afv.Environment);
                            foreach (var stmt in afv.Body)
                                compositor.Runtime.Interpreter.Evaluate(stmt, scope);
                            break;
                        }
                }
            };
        }


        public override void UpdateState(float dt)
        {

        }
    }
}
