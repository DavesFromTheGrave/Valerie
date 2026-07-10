namespace Valerie.UI;

/// <summary>
/// Thread-safe bridge between the console/audio pipeline and the orb window.
/// Program.cs writes state; OrbWindow reads it on the UI thread.
/// </summary>
public sealed class OrbController
{
    public static readonly OrbController Instance = new();
    private OrbController() { }

    private volatile int _state = (int)OrbState.Idle;
    private float _amplitude;

    public OrbState State
    {
        get => (OrbState)_state;
        set => _state = (int)value;
    }

    /// <summary>0–1 RMS amplitude from the audio pipeline, updated per audio chunk.</summary>
    public float Amplitude
    {
        get => _amplitude;
        set => _amplitude = Math.Clamp(value, 0f, 1f);
    }
}
