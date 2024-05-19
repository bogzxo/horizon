using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Box2D.NetStandard.Common;

using CodeKneading.Voxel;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Primitives;

using Silk.NET.Input;

namespace CodeKneading.Player.Behaviour;

internal interface IPlayerBehaviour : IUpdateable
{
    protected GamePlayer Player { get; init; }
    void Activate(in IPlayerBehaviour previous);
}

internal class PlayerBehaviourManagerComponent : IGameComponent
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "player Behaviour Manager";
    public Entity Parent { get; set; }

    public GamePlayer Player { get; private set; }

    public IPlayerBehaviour Behavior { get; private set; }

    public Vector3 CalculatedPosition { get; private set; }

    public void Initialize()
    {
        Player = (GamePlayer)Parent;

        // Default behaviour
        Behavior = new MovementBehaviour
        {
            Player = Player,
        };
    }

    public void Render(float dt, object? obj = null)
    {

    }

    public void UpdatePhysics(float dt)
    {
        Behavior.UpdatePhysics(dt);
    }

    public void UpdateState(float dt)
    {
        Behavior.UpdateState(dt);
    }
}
