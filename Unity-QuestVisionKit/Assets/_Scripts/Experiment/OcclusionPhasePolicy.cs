using UnityEngine;

/// <summary>
/// Phase-aware occlusion for the obstacle. During placement (Setup/Ready) the
/// obstacle renders with the non-occluding material regardless of distance:
/// placement happens inside the OcclusionSwapper's swap radius, where the
/// soft-occlusion edge feathers against the real floor exactly where the
/// experimenter needs to judge the bottom edge. Once trials run, the normal
/// distance-based swap resumes (occlude when the participant is close, so
/// their legs read in front of the obstacle when stepping over).
/// </summary>
[DisallowMultipleComponent]
public sealed class OcclusionPhasePolicy : MonoBehaviour
{
    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private SessionFlowController flow;

    private void Awake()
    {
        if (!flow) flow = FindAnyObjectByType<SessionFlowController>();
    }

    private void OnEnable()
    {
        if (flow == null) return;
        flow.OnPhaseChanged += HandlePhaseChanged;
        Apply(flow.Phase);
    }

    private void OnDisable()
    {
        if (flow != null) flow.OnPhaseChanged -= HandlePhaseChanged;
    }

    private void HandlePhaseChanged(SessionPhase prev, SessionPhase next) => Apply(next);

    // A swapper that spawns after this runs (obstacle chain rebuilt on
    // recapture) starts in its own default state; the next phase transition
    // (placement fires Setup -> Ready) re-applies the policy to all instances.
    private void Apply(SessionPhase phase)
    {
        bool placing = phase == SessionPhase.Setup || phase == SessionPhase.Ready;
        if (placing)
        {
            OcclusionSwapper.SetAllAutoSwapEnabled(false);
            OcclusionSwapper.SetAllOcclusion(false);
        }
        else
        {
            OcclusionSwapper.SetAllAutoSwapEnabled(true);
        }
    }
}
