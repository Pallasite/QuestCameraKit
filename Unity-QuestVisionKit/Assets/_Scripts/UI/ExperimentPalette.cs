using UnityEngine;

/// <summary>
/// THE indicator color vocabulary for the single/double-tag experiment — one
/// documented, colorblind-safe axis used identically by the HUD, the AprilTag
/// wireframe quality gradients, and the ghost preview:
///
///   CYAN    = good / ready / low-error        (#00E5FF)
///   YELLOW  = intermediate / in-progress      (#FFE566)
///   MAGENTA = bad / stale / high-error        (#FF2FB9)
///
/// Red↔green is banned: the two legacy wireframe gradients used it in OPPOSITE
/// directions (streaming: red=few obs, residual: red=high error), and red/green
/// is the most common colorblindness axis. Cyan↔magenta was established by the
/// on-device baseline (residual gradient) and is codified here.
/// </summary>
public static class ExperimentPalette
{
    /// <summary>Good / ready / low error.</summary>
    public static readonly Color Good = new Color(0f, 0.898f, 1f);        // #00E5FF

    /// <summary>Intermediate / in progress.</summary>
    public static readonly Color Mid = new Color(1f, 0.898f, 0.4f);       // #FFE566

    /// <summary>Bad / stale / high error.</summary>
    public static readonly Color Bad = new Color(1f, 0.184f, 0.725f);     // #FF2FB9

    /// <summary>Neutral informational text.</summary>
    public static readonly Color Neutral = new Color(0.85f, 0.85f, 0.85f);

    public const string GoodHex = "#00E5FF";
    public const string MidHex = "#FFE566";
    public const string BadHex = "#FF2FB9";

    /// <summary>Quality ramp where t=0 is BAD and t=1 is GOOD (e.g. observation count).</summary>
    public static Gradient BadToGood() => Make(Bad, Mid, Good);

    /// <summary>Quality ramp where t=0 is GOOD and t=1 is BAD (e.g. residual error).</summary>
    public static Gradient GoodToBad() => Make(Good, Mid, Bad);

    private static Gradient Make(Color a, Color b, Color c)
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(a, 0f),
                new GradientColorKey(b, 0.5f),
                new GradientColorKey(c, 1f),
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f),
            });
        return g;
    }
}
