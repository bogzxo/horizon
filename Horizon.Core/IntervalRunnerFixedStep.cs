namespace Horizon.Core;

public class IntervalRunnerFixedStep(float timeInterval, Action action) : Entity
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
        if (!(_timer >= TimeInterval)) return;
        
        _timer = 0.0f;
        _action.Invoke();
    }
}