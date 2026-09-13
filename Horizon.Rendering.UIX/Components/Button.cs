using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Horizon.HIDL.Runtime;
using ValueType = Horizon.HIDL.Runtime.ValueType;
using Horizon.Rendering.Text;
using System.Numerics;
using Silk.NET.Maths;

namespace Horizon.Rendering.UIX.Components
{
    public class Button : UIComponent
    {
        private string _text;
        
        public Action OnPressed { get; set; }
        private IRuntimeValue _hidlCallback;
        
        private UISprite _backgroundSprite;
        private TextLabel _textLabel;
        private string _labelId = Guid.NewGuid().ToString();
        private readonly Vector2 position;
        private readonly float sprScale;
        private readonly float lblScale;
        private Rectangle<float> bounds;


        public Button(in string label, Vector2 position, float sprScale, float lblScale, IRuntimeValue callback)
        {
            this.position = position;
            this.sprScale = sprScale;
            this.lblScale = lblScale;
            _text = label;
            _hidlCallback = callback;


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
        }

        public override void Initialize(UICompositor compositor)
        {
            // Initialize the background sprite
            _backgroundSprite = new UISprite(compositor);
            _backgroundSprite.SetAnimation("btn_normal");
            _backgroundSprite.Transform.Position = position;
            _backgroundSprite.Transform.Size *= sprScale;

            compositor.SpriteBatch.Add(_backgroundSprite);
            bounds = new Rectangle<float>(position.X - _backgroundSprite.Transform.Size.X / 2, position.Y - _backgroundSprite.Transform.Size.Y / 2, _backgroundSprite.Transform.Size.X, _backgroundSprite.Transform.Size.Y);

            // Initialize the text label
            _textLabel = new TextLabel
            {
                Text = _text,
                Origin = Origin.Center,
                Transform =
                {
                    Size = Vector2.One * lblScale,
                    Position = position
                }
            };
            compositor.GlyphRenderer.AddLabel(_labelId, _textLabel);

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

        private float clickTimer = 0.0f;
        private bool holdoff = false;
        public override void UpdateState(float dt, in UICompositor compositor, Vector2 mousePos, bool clicked)
        {
            if (holdoff)
            {
                clickTimer += dt;
                if (clickTimer > 0.15f)
                {
                    clickTimer = 0;
                    holdoff = false;
                    _backgroundSprite.SetAnimation("btn_normal");
                }
            }

            if (clicked)
            {
                var testBounds = bounds.GetTranslated(new Vector2D<float>(compositor.Position.X, compositor.Position.Y))
                    .GetScaled(new Vector2D<float>(compositor.Scale.X, compositor.Scale.Y), new Vector2D<float>(0));

                if (testBounds.Contains(new Vector2D<float>(mousePos.X, mousePos.Y)))
                {
                    holdoff = true;
                    OnPressed?.Invoke();
                    _backgroundSprite.SetAnimation("btn_depressed");
                }
            }
        }
    }
}
