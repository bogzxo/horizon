using System.Numerics;

using Horizon.Rendering.Spriting;

using TileBash.Animals.Behaviors;

namespace TileBash.Animals
{
    internal abstract class Animal : Sprite
    {
        public AnimalBehaviorStateMachineComponent StateMachine { get; init; }

        public Animal(in Vector2 spriteSize)
            : base(spriteSize)
        {
            StateMachine = AddComponent<AnimalBehaviorStateMachineComponent>();
        }
    }
}