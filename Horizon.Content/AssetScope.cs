namespace Horizon.Content;

/// <summary>
/// Says who the assets that are made belong to. While a scope is entered (see <see cref="Enter"/>) every asset an
/// <see cref="Managers.AssetManager{AssetType, AssetFactoryType, AssetDescriptionType, AssetDisposerType}"/> makes is
/// noted as that scope's, and every named asset that is asked for as one the scope uses. Releasing the scope frees
/// all of it in one go, whatever made it and whether or not that cleaned up after itself: what has no name is
/// freed outright, what has one is freed once no scope uses it any more.
/// <para>
/// A scene has one, which is what makes leaving a scene leave nothing behind. With no scope entered assets belong
/// to nobody and stay until they are removed by hand or the engine shuts down, which is right for what the engine
/// itself makes.
/// </para>
/// Assets are only ever made on the GL thread, and that is the only thread a scope means anything on.
/// </summary>
public sealed class AssetScope
{
    [ThreadStatic]
    private static AssetScope? current;

    /// <summary>The scope that is entered on this thread right now, null if assets belong to nobody.</summary>
    public static AssetScope? Current => current;

    /// <summary>What the scope is called, for the log.</summary>
    public string Name { get; }

    /// <summary>Whether the scope has been released. What is made in it after that is nobody's.</summary>
    public bool IsReleased { get; private set; }

    public AssetScope(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Has everything that is made from here on belong to this scope, until what is returned is disposed of:
    /// <code>
    /// using (scope.Enter())
    ///     scene.Initialize();
    /// </code>
    /// Scopes nest, leaving one goes back to whichever was entered before it.
    /// </summary>
    public Guard Enter()
    {
        var guard = new Guard(current);
        current = IsReleased ? null : this;
        return guard;
    }

    /// <summary>
    /// Has everything that is made from here on belong to nobody, whatever scope is entered: for what is made
    /// once and shared by everything that comes after (a buffer every renderer draws with), which must not go
    /// with whichever scene happened to need it first.
    /// </summary>
    public static Guard EnterGlobal()
    {
        var guard = new Guard(current);
        current = null;
        return guard;
    }

    /// <summary>Marks the scope as released. For whoever frees what it had, see ObjectManager.Release.</summary>
    public void MarkReleased() => IsReleased = true;

    /// <summary>Puts back the scope that was entered before, when it is disposed of.</summary>
    public readonly struct Guard : IDisposable
    {
        private readonly AssetScope? previous;

        internal Guard(AssetScope? previous)
        {
            this.previous = previous;
        }

        public void Dispose() => current = previous;
    }
}
