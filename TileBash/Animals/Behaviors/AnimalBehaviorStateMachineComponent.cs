﻿using Horizon.Core;
using Horizon.Core.Components;
using Horizon.GameEntity;
using Horizon.GameEntity.Components;
using Horizon.Rendering;

namespace TileBash.Animals.Behaviors;

internal class AnimalBehaviorStateMachineComponent : IGameComponent
{
    public AnimalState CurrentState { get; protected set; }
    public Dictionary<AnimalBehavior, AnimalState> States { get; init; } = new();

    public string Name { get; set; } = "Animal Behavior State Machine";
    public Entity Parent { get; set; }
    public bool Enabled { get; set; }

    public void Initialize() { }

    public void AddState(AnimalBehavior behavior, AnimalState state)
    {
        States.Add(behavior, state);
        CurrentState ??= state;
    }

    public void Transition(AnimalBehavior behavior)
    {
        CurrentState?.Exit();
        CurrentState = States[behavior];
        CurrentState.Enter();
    }

    public void UpdateState(float dt)
    {
        CurrentState?.UpdateState(dt);
    }

    public void Render(float dt, object? obj = null) { }

    public void UpdatePhysics(float dt) { }
}
