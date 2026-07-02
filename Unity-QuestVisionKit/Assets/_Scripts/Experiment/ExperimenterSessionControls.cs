using UnityEngine;

/// <summary>
/// In-headset experimenter control surface for the single/double-tag flow. Reads
/// controller chords (via <see cref="QuestControllerInput"/>) and calls the **public
/// API** of <see cref="ObstaclePlacementController"/>, <see cref="TrialSequencer"/>,
/// and <see cref="TrialLoopActivator"/>. Because every action is just a public-method
/// call, a future web/remote control surface can drive the exact same operations.
///
/// The controllers are free (no rig) in this flow, so the experimenter holds them
/// beside the participant during the run for redo / pause.
///
/// Default chord map (free buttons — finesse owns sticks/grips/A·B; tune on device):
///   L index trigger              → Place now (capture)
///   R grip + L index trigger     → Recapture (clear placement)
///   R index trigger              → Redo current trial
///   R grip + R index trigger     → Pause / Resume
///   Start (menu)                 → Cycle visual policy (Deferred→Smoothed→Raw)
///   R grip + Start               → Cycle tracking variant (Anchored↔WorldRoot)
///   L index + R index (together) → Cycle solver (Single↔TwoTagLine)
/// </summary>
[DisallowMultipleComponent]
public sealed class ExperimenterSessionControls : MonoBehaviour
{
    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private QuestControllerInput input;
    [SerializeField] private ObstaclePlacementController placement;
    [SerializeField] private TrialSequencer trialSequencer;
    [SerializeField] private TrialLoopActivator trialLoop;
    [SerializeField] private PipelineStatusHUD hud;

    [Header("Bindings (tune on device)")]
    [Tooltip("Held modifier that selects the secondary action for index/Start.")]
    [SerializeField] private OVRInput.Button modifier = OVRInput.Button.SecondaryHandTrigger; // R grip
    [SerializeField] private OVRInput.Button placeButton = OVRInput.Button.PrimaryIndexTrigger;   // L index
    [SerializeField] private OVRInput.Button redoButton = OVRInput.Button.SecondaryIndexTrigger;   // R index
    [SerializeField] private OVRInput.Button cyclePolicyButton = OVRInput.Button.Start;            // L menu

    [Header("Feedback")]
    [SerializeField] private bool haptics = true;

    private void Awake()
    {
        if (!input) input = FindAnyObjectByType<QuestControllerInput>();
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (!trialSequencer) trialSequencer = FindAnyObjectByType<TrialSequencer>();
        if (!trialLoop) trialLoop = FindAnyObjectByType<TrialLoopActivator>();
        if (!hud) hud = FindAnyObjectByType<PipelineStatusHUD>();
    }

    private void Update()
    {
        if (!input) return;

        bool mod = input.IsHeld(modifier);
        bool lHeld = input.IsHeld(placeButton);
        bool rHeld = input.IsHeld(redoButton);

        // Both index triggers -> cycle solver (checked before the single-index branches).
        if ((input.WasPressedThisFrame(placeButton) && rHeld) ||
            (input.WasPressedThisFrame(redoButton) && lHeld))
        {
            placement?.CycleSolverMode();
            Pulse(); ShowStatus();
            return;
        }

        if (input.WasPressedThisFrame(placeButton))
        {
            if (mod) placement?.Recapture(); else placement?.CapturePlacement();
            Pulse(); ShowStatus();
            return;
        }

        if (input.WasPressedThisFrame(redoButton))
        {
            if (mod) TogglePause(); else trialSequencer?.RedoCurrentTrial();
            Pulse(); ShowStatus();
            return;
        }

        if (input.WasPressedThisFrame(cyclePolicyButton))
        {
            if (mod) placement?.CycleTrackingVariant(); else placement?.CycleVisualPolicy();
            Pulse(); ShowStatus();
            return;
        }
    }

    private void TogglePause()
    {
        if (trialLoop == null) return;
        if (trialLoop.IsPaused) trialLoop.Resume(); else trialLoop.Pause();
    }

    private void ShowStatus()
    {
        if (hud == null || placement == null) return;
        string pause = (trialLoop != null && trialLoop.IsPaused) ? " · <color=#FFFF88>PAUSED</color>" : "";
        hud.ShowTransient($"<b>Session:</b> {placement.StatusLine()}{pause}", 3f);
    }

    private void Pulse()
    {
        if (!haptics) return;
        OVRInput.SetControllerVibration(1f, 0.4f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(1f, 0.4f, OVRInput.Controller.RTouch);
        CancelInvoke(nameof(StopHaptics));
        Invoke(nameof(StopHaptics), 0.04f);
    }

    private void StopHaptics()
    {
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
    }
}
