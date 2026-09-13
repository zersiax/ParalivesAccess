using System;

namespace ParalivesAccess.Handlers
{
    /// <summary>
    /// Acquires the game's native interaction menu through the same creating path used by a
    /// physical world click.
    /// </summary>
    /// <remarks>
    /// <c>UI.GetOrNull&lt;UIInteractions&gt;</c> defaults to <c>createIfNotExists: false</c>.
    /// A fresh game can therefore have a valid interaction target before the interaction-window
    /// prefab has ever been instantiated. The game's <c>UpdateSelect</c> click path uses
    /// <c>UI.Get&lt;UIInteractions&gt;</c>, so accessibility handoffs must do the same.
    /// </remarks>
    internal static class NativeInteractionMenu
    {
        internal static UIInteractions GetOrCreate(int playerIndex, string source)
        {
            try
            {
                if (UI.Instance == null)
                {
                    Plugin.Log.LogError(
                        $"[InteractionUI] source={source}, player={playerIndex}, UI.Instance is null.");
                    return null;
                }

                bool alreadyExisted = UI.GetOrNull<UIInteractions>(playerIndex) != null;
                UIInteractions interactions = UI.Get<UIInteractions>(playerIndex);
                Plugin.Log.LogInfo(
                    $"[InteractionUI] source={source}, player={playerIndex}, "
                  + $"alreadyExisted={alreadyExisted}, ready={interactions != null}.");
                return interactions;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    $"[InteractionUI] source={source}, player={playerIndex}, creation threw: {ex}");
                return null;
            }
        }
    }
}
