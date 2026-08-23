using System;
using System.Collections.Generic;
using System.Text;
using Horizon.HIDL.Runtime;
using ValueType = Horizon.HIDL.Runtime.ValueType;
using Horizon.Rendering.Text;
using Horizon.Rendering.UI;
using System.Numerics;

namespace Horizon.Rendering.UIX.Components
{
    public class Button : IUIComponent
    {
        public ObjectValue Object { get; init; }
        public NativeValue Value { get; init; }
        private string _text;
        
        public Action OnPressed { get; set; }
        private IRuntimeValue _hidlCallback;
        private UICompositor _compositor;
        
        public string Text
        {
            get => Value.AccessorCallback.Invoke().ToString() ?? "NO_STR";
            set => Value.MutatorCallback.Invoke(new StringValue(value));
        }

        private UISprite _backgroundSprite;
        private TextLabel _textLabel;
        private string _labelId;

        public Button(in string label)
        {
            _text = label;
            _labelId = Guid.NewGuid().ToString();
            
            this.Value = new NativeValue(() => new StringValue(_text), (val) =>
            {
                if (val is StringValue strVal) 
                {
                    _text = strVal.Value;
                    if (_textLabel != null)
                        _textLabel.Text = _text;
                }
                else throw new Exception("Button text label has to be a string!");
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
                {"label", this.Value},
                {"on_pressed", onPressedValue}
            });
            
            this.OnPressed = () => 
            {
                if (_hidlCallback != null && _compositor != null)
                {
                    if (_hidlCallback is NativeFunctionValue nfv)
                    {
                        nfv.Callback.Invoke(Array.Empty<IRuntimeValue>(), _compositor.Runtime.GlobalScope);
                    }
                    else if (_hidlCallback is FunctionValue fv)
                    {
                        var scope = new Horizon.HIDL.Runtime.Environment(fv.Environment);
                        foreach (var stmt in fv.Body)
                            _compositor.Runtime.Interpreter.Evaluate(stmt, scope);
                    }
                    else if (_hidlCallback is AnonymousFunctionValue afv)
                    {
                        var scope = new Horizon.HIDL.Runtime.Environment(afv.Environment);
                        foreach (var stmt in afv.Body)
                            _compositor.Runtime.Interpreter.Evaluate(stmt, scope);
                    }
                }
            };
        }

        public void Initialize(UICompositor compositor)
        {
            _compositor = compositor;

            // Initialize the background sprite
            _backgroundSprite = new UISprite(new Vector2(200, 50)); 
            if (compositor.SharedSheet != null)
            {
                _backgroundSprite.ConfigureSpriteSheet(compositor.SharedSheet, "btn_normal");
            }
            
            compositor.SpriteBatch.Add(_backgroundSprite);
            
            // Initialize the text label
            _textLabel = new TextLabel() { Text = _text };
            compositor.GlyphRenderer.AddLabel(_labelId, _textLabel);
        }

        public void UpdateState(float dt)
        {
            // Here you would normally do collision detection against mouse cursor
            // For now, we will just sync transform (as an example, fixed at 100, 100)
            // Ideally, position comes from a layout system or parent transform.
            if (_backgroundSprite != null)
            {
                // Align text label to button background
                _textLabel.Transform.Position = _backgroundSprite.Transform.Position + new Vector2(10, 10);
            }
        }
    }
}
