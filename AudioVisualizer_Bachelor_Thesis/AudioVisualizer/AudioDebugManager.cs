using UnityEngine;

/// <summary>
/// Attach to TONE_GAME_MANAGER.
/// Toggles ALL AudioDebugVisualizer instances simultaneously,
/// including ones spawned mid-game when new tasks load.
/// </summary>
public class AudioDebugManager : MonoBehaviour
{
    [Header("Global Debug Visualizer Toggle")]
    [SerializeField]
    [Tooltip("Toggle ALL AudioDebugVisualizer instances in the scene at once.")]
    private bool showAllDebugVisualizers = false;

    private bool lastValue = false;
    private int lastCount = 0;

    private void Update()
    {
        var all = FindObjectsOfType<AudioDebugVisualizer>();

        // Apply when toggle changes OR when new instances appear mid-game
        bool valueChanged = showAllDebugVisualizers != lastValue;
        bool countChanged = all.Length != lastCount;

        if (!valueChanged && !countChanged) return;

        int prevCount = lastCount;
        lastValue = showAllDebugVisualizers;
        lastCount = all.Length;

        foreach (var viz in all)
            viz.SetShowDebugVisualizer(showAllDebugVisualizers);

        Debug.Log($"[AudioDebugManager] {all.Length} visualizers " +
                  $"(was {prevCount}), set to {showAllDebugVisualizers}");
    }
}