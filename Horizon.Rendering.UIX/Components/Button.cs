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
    public class Button : UIComponent
    {
        public NativeValue TextValue { get; protected set; }
        public NativeValue ScaleValue { get; protected set; }
        private string _text;
        private float _scale;
        
        public Action OnPressed { get; set; }
        private IRuntimeValue _hidlCallback;
        
        public string Text
        {
            get => TextValue.AccessorCallback.Invoke().ToString() ?? "NO_STR";
            set => TextValue.MutatorCallback.Invoke(new StringValue(value));
        }
        public float Scale
        {
            get => float.TryParse(TextValue.AccessorCallback.Invoke().ToString(), out var val) ? val : 0.0f;
            set => TextValue.MutatorCallback.Invoke(new NumberValue(value));
        }

        private UISprite _backgroundSprite;
        private TextLabel _textLabel;
        private string _labelId;

        public Button(UICompositor compositor, in string label, float scale=1.0f)
        {
            _text = label;
            _scale = scale;
            _labelId = Guid.NewGuid().ToString();
        }

        public override void Initialize(UICompositor compositor)
        {
            // Initialize the background sprite
            _backgroundSprite = new UISprite(compositor.SharedSheet.SpriteSize);
            _backgroundSprite.ConfigureSpriteSheet(compositor.SharedSheet, "btn_normal");
            _backgroundSprite.IsAnimated = false;

            compositor.SpriteBatch.Add(_backgroundSprite);

            // Initialize the text label
            _textLabel = new TextLabel
            {
                Text = _text,
                Origin = Origin.Center,
                Transform =
                {
                    Size = Vector2.One * 0.4f
                }
            };
            compositor.GlyphRenderer.AddLabel(_labelId, _textLabel);

            this.TextValue = new NativeValue(() => new StringValue(_text), (val) =>
            {
                if (val is StringValue strVal)
                {
                    _text = strVal.Value;
                    _textLabel.Text = _text;
                }
                else throw new Exception("Button text label has to be a string!");
            });
            this.ScaleValue = new NativeValue(() => new NumberValue(_scale), (val) =>
            {
                if (val is NumberValue fltVal)
                {
                    _scale = fltVal.Value;
                    _backgroundSprite.Transform.Size = compositor.SharedSheet.SpriteSize * _scale;
                }
                else throw new Exception("Button scale has to be a number!");
            });

            var onPressedValue = new NativeValue(
                () => _hidlCallback ?? new NullValue(),
                (val) =>
                {
                    _hidlCallback = val;
                }
            );

            this.Object = new ObjectValue(new Dictionary<string, IRuntimeValue>()
            {
                {"label", this.TextValue},
                {"scale", this.ScaleValue},
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
