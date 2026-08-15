namespace Horizon.Core;

public class IntervalRunnerSubStep(float timeInterval, Action action) : Entity
{
    public float TimeInterval { get; init; } = timeInterval;
    private float _timer = 0.0f;
    private Action _action = action;

    public void SetAction(Action action)
    {
        _action = action;
    }

    public override void UpdateState(float dt)
    {
        if (!Enabled)
        {
            _timer = 0.0f;
            return;
        }

        _timer += dt;

        while (_timer > TimeInterval)
        {
            _timer -= TimeInterval;
            _action.Invoke();
        }
    }
}