using System.Numerics;

using Horizon.Core.Components;
using Horizon.Engine;

namespace CodeKneading.Player.Behaviour;

internal class MovementBehaviour : IPlayerBehaviour
{
    internal const float MOVEMENT_SPEED = 10.0f;

    public GamePlayer Player { get; init; }


    public void Activate(in IPlayerBehaviour previous)
    {

    }

    public void UpdatePhysics(float dt)
    {
        GamePlayer.Transform.Position += GetMovementDirection(dt);
    }

    public void UpdateState(float dt)
    {

    }

    /// <summary>
    /// Helper method to calculate how far the player moves based on their direction and momentum.
    /// </summary>
    internal Vector3 GetMovementDirection(in float dt)
    {
        // store old position
        Vector3 oldPos = GamePlayer.Transform.Position;

        // calculate new position
        Vector3 newPos = oldPos + CalculatePhysics(dt);

        //// test collisions on x axis
        //if ((int)Player.World[(int)newPos.X, (int)(newPos.Y - 1), (int)oldPos.Z].Type > 0 ||
        //    (int)Player.World[(int)newPos.X, (int)newPos.Y, (int)oldPos.Z].Type > 0)
        //{
        //    newPos.X = oldPos.X;
        //}
        //// test collisions on z axis
        //if ((int)Player.World[(int)oldPos.X, (int)(newPos.Y - 1), (int)newPos.Z].Type > 0 ||
        //    (int)Player.World[(int)oldPos.X, (int)newPos.Y, (int)newPos.Z].Type > 0)
        //{
        //    newPos.Z = oldPos.Z;
        //}
        //// test collisions on the y axis
        //if ((int)Player.World[(int)newPos.X, (int)newPos.Y - 1, (int)newPos.Z].Type > 0 ||
        //    (int)Player.World[(int)newPos.X, (int)(newPos.Y - 1.9f), (int)newPos.Z].Type > 0)
        //{
        //    newPos.Y = oldPos.Y;
        //}

        // return the difference
        return newPos - oldPos;
    }

    private float massCoeff = 8.0f;
    private Vector3 momentum = Vector3.Zero;

    private Vector3 CalculatePhysics(in float dt)
    {
        // get movement axis from the virtual controller
        Vector2 axis = GameEngine.Instance.InputManager.GetVirtualController().MovementAxis;

        // calculate target movement speed
        float movementSpeed = MOVEMENT_SPEED * (GameEngine.Instance.InputManager.KeyboardManager.IsKeyDown(Silk.NET.Input.Key.ShiftLeft) ? 2.0f : 1.0f);

        // get camera front with no pitch
        Vector3 cameraFrontNoPitch = Vector3.Normalize(new Vector3(Player.Camera.Front.X, 0, Player.Camera.Front.Z));
        cameraFrontNoPitch = Player.Camera.Front;

        // get travel direction vector with respect to camera, removing the Y direction
        Vector3 movement = ((Vector3.Normalize(Vector3.Cross(cameraFrontNoPitch, Vector3.UnitY)) * movementSpeed * axis.X * dt) +
                            (movementSpeed * cameraFrontNoPitch * axis.Y * dt)) * new Vector3(1, 1, 1);

        // apply jumping logic
        movement += JumpLogic(dt);

        // apply gravity logic
        //movement += GravityLogic(dt);

        // modify momentum with weight coefficient
        momentum = Vector3.Lerp(momentum, movement, dt * massCoeff);

        return momentum;
    }

    private Vector3 GravityLogic(in float dt)
    {
        // check if we are grounded
        if (Player.IsGrounded) return Vector3.Zero;

        return new Vector3(0, -10 * dt, 0);
    }

    private float timeSinceLastJump = 0;
    private Vector3 JumpLogic(in float dt)
    {
        timeSinceLastJump += dt;

        // limit the user to jumping every 100ms
        if (timeSinceLastJump < 0.1) return Vector3.Zero;

        // check if we are grounded
        //if (!Player.IsGrounded) return Vector3.Zero;

        // check that a jump is requested
        if (!GameEngine.Instance.InputManager.GetVirtualController().IsPressed(Horizon.Input.VirtualAction.MoveJump)) return Vector3.Zero;

        timeSinceLastJump = 0;
        return new Vector3(0, 200 * dt, 0);
    }
}
