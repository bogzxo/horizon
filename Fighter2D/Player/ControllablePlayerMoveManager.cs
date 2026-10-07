using System;
using System.Linq;
using System.Numerics;

using Bogz.Logging.Loggers;

using Fighter2D.Logic;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Particles;



namespace Fighter2D.Player;

internal class ControllablePlayerMoveManager : IGameComponent
{
    public bool Enabled { get; set; }
    public string Name { get; set; } = string.Empty;
    public Entity Parent { get; set; }

    public FightingMove CurrentMove { get; private set; }

    private readonly PlayerInputTracker _input = new();
    private readonly PlayerStateTracker _state = new();
    private readonly MoveList _moveList = new();
    private readonly Random _random = new();

    private IntervalRunner _particleRunner;

    private float _frameStepTimer = 0.0f;
    private float _totalEngineTime = 0.0f;
    private float _jumpForce = 2000f;
    private float _speed = 2500f;

    public void Initialize()
    {
        CurrentMove = _moveList.Idle;
        Player.Instance.AnimationManager.Enabled = false;

        SetupHIDLRuntime();
        SetupAmbienceParticleRunner();
    }

    #region Callbacks & Particles
    private void SetupAmbienceParticleRunner()
    {
        _particleRunner = new IntervalRunner(1 / 15.0f, () =>
        {
            for (int diagonal = 0; diagonal < 4; diagonal++)
            {
                float x = _random.NextSingle() * GameEngine.Instance.WindowManager.WindowSize.X + diagonal / 2.0f;
                float y = _random.NextSingle() * GameEngine.Instance.WindowManager.WindowSize.Y + diagonal / 2.0f;

                var position = GameEngine.Instance.ActiveCamera.ScreenToWorld(new Vector2(x, y));
                SpawnParticle(position, -Vector2.One, 0.2f);
            }
        });
    }

    private void SetupHIDLRuntime()
    {
        var globalScope = GameEngine.Instance.Debugger.Console.Runtime.GlobalScope;

        globalScope.DeclareSystem("_PLAYER_JUMP", new NativeFunctionValue((_, _) =>
        {
            if (_state.IsGrounded && _state.CurrentStatus == PlayerStatusType.Normal)
            {
                //Player.Instance.PlayerBody.ApplyLinearImpulseToCenter(new Vector2(0, _jumpForce));
                _state.ResetFallDuration();
            }
            return new NullValue();
        }));

        globalScope.DeclareSystem("_PLAYER_DASH", new NativeFunctionValue((_, _) =>
        {
            if (_state.IsGrounded && _state.CurrentStatus == PlayerStatusType.Normal)
            {
                float direction = Player.Instance.Flipped ? -1.0f : 1.0f;
                //Player.Instance.PlayerBody.ApplyLinearImpulseToCenter(new Vector2(direction * (_speed * 1.5f), 0));

                for (int i = 0; i < 32; i++)
                {
                    SpawnParticle(Player.Instance.Transform.Position - new Vector2(0, 32), new Vector2(-direction * 2f, 0.5f), 0.8f);
                }
            }
            return new NullValue();
        }));

        GameEngine.Instance.Debugger.Console.Runtime.Evaluate(@"
        let player = {
            jump: func() { _PLAYER_JUMP(); },
            dash: func() { _PLAYER_DASH(); }
        }", true);

        foreach (var (_, move) in _moveList.Moves)
        {
            if (move.Callback is not null)
            {
                globalScope.Assign(move.Name, move.Callback.Value with { Environment = globalScope });
            }
        }
    }
    #endregion

    #region Loops
    public void UpdatePhysics(float dt)
    {
        _totalEngineTime += dt;
        _input.Update(_totalEngineTime);
        _state.UpdatePhysicsState(dt, CurrentMove.Name.Equals("crouch", StringComparison.OrdinalIgnoreCase));
        _state.UpdateStatus(dt);

        CheckHeavyLanding();

        if (_state.CurrentStatus != PlayerStatusType.Normal)
        {
            ProcessAnimationFrames(dt);
            Player.Instance.SetAnimation(CurrentMove.Animation.Name);
            return;
        }

        if (!string.IsNullOrEmpty(CurrentMove.ReleaseMove) && !IsMoveHeld(CurrentMove))
        {
            if (_moveList.Moves.TryGetValue(CurrentMove.ReleaseMove, out var releaseMove))
            {
                ChangeToMove(releaseMove);
            }
        }

        TryProcessNewInputs();
        ProcessAnimationFrames(dt);
        TryResumeHeldMove();

        if (_state.CurrentStance != Stance.Standing && CurrentMove.Name == "standing")
        {
            Player.Instance.SetAnimation(_state.CurrentStance == Stance.Falling ? "fall" : "idle");
        }
        else
        {
            Player.Instance.SetAnimation(CurrentMove.Animation.Name);
        }
    }

    public void UpdateState(float dt)
    {
        _particleRunner?.UpdateState(dt);

        if (_state.CurrentStatus != PlayerStatusType.Normal) return;

        if (GameEngine.Instance.InputManager.KeyboardManager.IsKeyPressed(Silk.NET.Input.Key.R))
            _moveList.Reload();

        var movementDir = _input.GetMovementInput();
        if (movementDir.X != 0)
        {
            Player.Instance.Flipped = movementDir.X < 0;
        }

        // TODO: this will need to apply a force, not overide the position
        Player.Instance.PhysicsBody.ApplyForce(movementDir * _speed);
    }
    #endregion

    #region External Status API
    public void ApplyStun(float duration, string stunAnimationName = "stun")
    {
        _state.ApplyStun(duration);
        ForceMoveAnimation(stunAnimationName);
    }

    public void TrapInCombo(FightingMove comboMove, float lockDuration)
    {
        _state.ApplyComboTrap(lockDuration);
        CurrentMove = comboMove;
        Player.Instance.AnimationManager.Animations[CurrentMove.Animation.Name].ResetIndex();
    }

    private void ForceMoveAnimation(string animName)
    {
        if (_moveList.Moves.TryGetValue(animName, out var target))
        {
            CurrentMove = target;
        }
        Player.Instance.AnimationManager.Animations[CurrentMove.Animation.Name].ResetIndex();
    }
    #endregion

    #region Logic & Animation
    private void CheckHeavyLanding()
    {
        if (!_state.DeltaOnGround && _state.IsGrounded)
        {
            if (_state.FallDuration > 0.2f && _moveList.Moves.TryGetValue("heavy_land", out var landMove))
            {
                ChangeToMove(landMove);
            }

            for (int i = 0; i < 32; i++)
            {
                Vector2 randomDir = new Vector2((float)_random.NextDouble(), (float)_random.NextDouble());
                SpawnParticle(Player.Instance.Transform.Position + Vector2.UnitY * -64, randomDir);
            }
            _state.ResetFallDuration();
        }
    }

    private void ProcessAnimationFrames(float dt)
    {
        _frameStepTimer += dt;
        if (_frameStepTimer > 1.0f / 30f)
        {
            _frameStepTimer = 0;

            var (finished, index) = Player.Instance.AnimationManager.IncrementFrame(CurrentMove.Animation.Name);
            if (finished)
            {
                if (_state.CurrentStatus != PlayerStatusType.Normal) return;

                if (!string.IsNullOrEmpty(CurrentMove.NextMove) && _moveList.Moves.TryGetValue(CurrentMove.NextMove, out var next))
                {
                    ChangeToMove(next);
                }
                else if (CurrentMove.Loopable && IsMoveHeld(CurrentMove))
                {
                    Player.Instance.AnimationManager.Animations[CurrentMove.Animation.Name].ResetIndex();

                    if (CurrentMove.Callback is not null)
                    {
                        GameEngine.Instance.Debugger.Console.Runtime.Evaluate($"{CurrentMove.Name}(); playerJump();");
                    }
                }
                else
                {
                    CurrentMove = _moveList.Idle;
                }
            }
        }
    }

    private void TryProcessNewInputs()
    {
        var buttons = _input.ConsumeFramePresses();
        if (buttons.Length == 0 || !CurrentMove.Interuptable) return;

        foreach (var candidate in _moveList.Moves.Values)
        {
            if (candidate.Bindings.Length == 0) continue;

            bool matched = candidate.UseAnyBindings
                ? candidate.Bindings.Any(buttons.Contains)
                : candidate.Bindings.All(buttons.Contains);

            if (matched)
            {
                FightingMove moveToExecute = candidate;

                if (!IsStanceValid(candidate.Stances))
                {
                    if (candidate.StanceReroutes != null &&
                        candidate.StanceReroutes.TryGetValue(_state.CurrentStance, out string? reroutedName) &&
                        _moveList.Moves.TryGetValue(reroutedName, out var reroutedMove))
                    {
                        moveToExecute = reroutedMove;
                    }
                    else continue;
                }

                if (CurrentMove.Name == moveToExecute.Name) continue;

                if (candidate.DoubleTap || moveToExecute.DoubleTap)
                {
                    bool isDoubleTap = false;
                    foreach (var btn in candidate.Bindings.Where(buttons.Contains))
                    {
                        if (_input.HasDoubleTap(btn, _totalEngineTime, 0.3f))
                        {
                            isDoubleTap = true;
                            _input.ClearButtonHistory(btn);
                            break;
                        }
                    }
                    if (!isDoubleTap) continue;
                }

                ChangeToMove(moveToExecute);
                return;
            }
        }
    }

    private void TryResumeHeldMove()
    {
        if (CurrentMove.Name != _moveList.Idle.Name) return;

        foreach (var candidate in _moveList.Moves.Values)
        {
            if (candidate.Bindings.Length == 0 || !candidate.Loopable || !IsMoveHeld(candidate)) continue;

            FightingMove moveToExecute = candidate;
            if (!IsStanceValid(candidate.Stances))
            {
                if (candidate.StanceReroutes != null &&
                    candidate.StanceReroutes.TryGetValue(_state.CurrentStance, out string? reroutedName) &&
                    _moveList.Moves.TryGetValue(reroutedName, out var reroutedMove))
                {
                    moveToExecute = reroutedMove;
                }
                else continue;
            }

            CurrentMove = moveToExecute;
            Player.Instance.AnimationManager.Animations[CurrentMove.Animation.Name].ResetIndex();
            Player.Instance.SetAnimation(CurrentMove.Animation.Name);
            return;
        }
    }

    private bool IsStanceValid(Stance candidateStances)
    {
        switch (_state.CurrentStance)
        {
            case Stance.Jumping:
                return candidateStances.HasFlag(Stance.Jumping);
            case Stance.Falling:
                return candidateStances.HasFlag(Stance.Falling);
            case Stance.Crouching:
                return candidateStances.HasFlag(Stance.Crouching) || candidateStances == Stance.Standing;
            default:
                return candidateStances == Stance.Standing || candidateStances.HasFlag(Stance.Crouching);
        }
    }

    private bool IsMoveHeld(FightingMove candidate)
    {
        if (candidate.Bindings.Length == 0) return false;

        return candidate.UseAnyBindings
            ? candidate.Bindings.Any(_input.IsButtonHeld)
            : candidate.Bindings.All(_input.IsButtonHeld);
    }

    private void ChangeToMove(FightingMove newMove)
    {
        CurrentMove = newMove;
        Player.Instance.AnimationManager.Animations[CurrentMove.Animation.Name].ResetIndex();

        if (CurrentMove.Callback is not null)
        {
            var (succ, msg) = GameEngine.Instance.Debugger.Console.Runtime.Evaluate($"{CurrentMove.Name}();");
            if (!succ) ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, msg);
        }
    }

    private void SpawnParticle(Vector2 pos, Vector2 dir, float blend = 0.5f)
    {
        float val = (_random.NextSingle() * MathF.PI * 2.0f) - MathF.PI;

        Player.Instance.Particles.Add(new Particle2D(
            new Vector2(MathF.Sin(val), MathF.Cos(val)) * (1.0f - blend) + dir * blend,
            pos
        ));
    }
    #endregion

    public void Render(float dt, object? obj = null)
    {

    }
}