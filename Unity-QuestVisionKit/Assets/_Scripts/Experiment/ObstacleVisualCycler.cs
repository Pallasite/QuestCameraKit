using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages a list of visual variant GameObjects for the obstacle.
/// Cycles through them, deactivating all and activating the current one.
/// Not wired to any input — just exposes public methods for external event wiring.
/// </summary>
public class ObstacleVisualCycler : MonoBehaviour
{
    [SerializeField] private List<GameObject> visualVariants = new List<GameObject>();

    public int CurrentIndex { get; private set; } = 0;
    public int VariantCount => visualVariants.Count;

    public void CycleNext()
    {
        if (visualVariants.Count == 0) return;
        CurrentIndex = (CurrentIndex + 1) % visualVariants.Count;
        ApplyVariant();
    }

    public void SetVariant(int index)
    {
        if (visualVariants.Count == 0) return;
        CurrentIndex = Mathf.Clamp(index, 0, visualVariants.Count - 1);
        ApplyVariant();
    }

    private void ApplyVariant()
    {
        for (int i = 0; i < visualVariants.Count; i++)
        {
            if (visualVariants[i] != null)
                visualVariants[i].SetActive(i == CurrentIndex);
        }
    }
}
