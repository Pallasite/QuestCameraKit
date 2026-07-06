using UnityEngine;

/// <summary>
/// Minimal surface for pushing transient messages to whatever HUD a scene has.
/// Implemented by <see cref="PipelineStatusHUD"/> (old constellation scenes) and
/// <c>SessionHUD</c> (single/double-tag scenes), so shared components like
/// <see cref="ObstacleFinesseController"/> can post feedback without caring
/// which HUD variant is present.
/// </summary>
public interface IHudTransientSink
{
    /// <summary>Show a transient message. durationSeconds &lt;= 0 = implementation default.</summary>
    void ShowTransient(string message, float durationSeconds = -1f);
}

/// <summary>Scene-scan locator for the active <see cref="IHudTransientSink"/>.</summary>
public static class HudSink
{
    /// <summary>
    /// Find any active MonoBehaviour implementing <see cref="IHudTransientSink"/>.
    /// Scenes carry exactly one HUD, so first match wins. Call once (Awake) and cache.
    /// </summary>
    public static IHudTransientSink Find()
    {
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (mb is IHudTransientSink sink) return sink;
        }
        return null;
    }
}
