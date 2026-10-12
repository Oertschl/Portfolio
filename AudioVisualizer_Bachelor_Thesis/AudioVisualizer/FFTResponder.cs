using UnityEngine;

/// <summary>
/// Responder 2/5 — FFT Dominant Frequency
/// Maps dominant frequency bin to color hue. Demonstrates FFT
/// reliability problem — C maps to wrong hue due to harmonic dominance.
/// </summary>
public class FFTResponder : IVisualResponder
{
    public string MethodName => "FFT dominant frequency (Hz → color hue)";

    private readonly ParticleSystem ps;
    private readonly ParticleSystemRenderer psRenderer;
    private readonly Material instanceMaterial;
    private readonly float baseSize;
    private readonly float baseRate;

    private const float MIN_HZ    = 300f;
    private const float MAX_HZ    = 550f;
    private const float SIZE_SCALE = 18f;
    private const float RATE_SCALE = 1500f;
    private const int   LOCK_FRAME = 3;

    private float lockedHue = -1f;
    private int framesSinceStart = 0;

    public FFTResponder(ParticleSystem ps)
    {
        this.ps = ps;
        this.psRenderer = ps.GetComponent<ParticleSystemRenderer>();

        if (psRenderer != null && psRenderer.sharedMaterial != null)
        {
            instanceMaterial = new Material(psRenderer.sharedMaterial);
            psRenderer.material = instanceMaterial;
        }

        var main = ps.main;
        baseSize = main.startSize.constant;
        baseRate = ps.emission.rateOverTime.constant;
    }

    public void Apply(in AudioFeatureFrame frame)
    {
        if (ps == null) return;

        if (frame.noteJustStarted)
        {
            lockedHue = -1f;
            framesSinceStart = 0;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        framesSinceStart++;

        if (lockedHue < 0f && framesSinceStart >= LOCK_FRAME
            && frame.dominantFrequencyHz > 0f)
            lockedHue = Mathf.Clamp01(
                Mathf.InverseLerp(MIN_HZ, MAX_HZ, frame.dominantFrequencyHz));

        float drive = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;
        var main = ps.main;
        main.startSize = Mathf.Max(0.01f, baseSize * (1f + drive * SIZE_SCALE));
        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = Mathf.Max(0f, baseRate + drive * RATE_SCALE);
        emission.rateOverTime = rate;

        float hue = lockedHue >= 0f ? lockedHue : 0.5f;
        Color c = Color.HSVToRGB(hue, 0.9f, 1.0f);
        if (instanceMaterial != null)
            instanceMaterial.SetColor("_BaseColor", c);
    }

    public void Reset()
    {
        lockedHue = -1f;
        framesSinceStart = 0;
        if (ps == null) return;
        var main = ps.main;
        main.startSize = baseSize;
        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = baseRate;
        emission.rateOverTime = rate;
    }
}
