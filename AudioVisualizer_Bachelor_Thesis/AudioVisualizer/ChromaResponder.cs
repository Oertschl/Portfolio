using UnityEngine;

/// <summary>
/// Responder 3/5 — Chroma Features (Pitch Class Profile)
/// Maps symbolic pitch class to color on 12-tone wheel.
/// Chroma confidence drives particle lifetime.
/// </summary>
public class ChromaResponder : IVisualResponder
{
    public string MethodName => "Chroma (pitch class → color, confidence → lifetime)";

    private readonly ParticleSystem ps;
    private readonly ParticleSystemRenderer psRenderer;
    private readonly Material instanceMaterial;
    private readonly float baseSize;
    private readonly float baseRate;
    private readonly float baseLifetime;

    private const float SIZE_SCALE = 18f;
    private const float RATE_SCALE = 1500f;

    public ChromaResponder(ParticleSystem ps)
    {
        this.ps = ps;
        this.psRenderer = ps.GetComponent<ParticleSystemRenderer>();

        if (psRenderer != null && psRenderer.sharedMaterial != null)
        {
            instanceMaterial = new Material(psRenderer.sharedMaterial);
            psRenderer.material = instanceMaterial;
        }

        var main = ps.main;
        baseSize     = main.startSize.constant;
        baseRate     = ps.emission.rateOverTime.constant;
        baseLifetime = main.startLifetime.constant;
    }

    public void Apply(in AudioFeatureFrame frame)
    {
        if (ps == null) return;

        if (frame.noteJustStarted)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        float drive = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;
        var main = ps.main;
        main.startSize = Mathf.Max(0.01f, baseSize * (1f + drive * SIZE_SCALE));
        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = Mathf.Max(0f, baseRate + drive * RATE_SCALE);
        emission.rateOverTime = rate;

        // Color from symbolic pitch class on 12-tone chromatic hue wheel
        float hue = (frame.symbolicPitchClass % 12) / 12f;
        Color noteColor = Color.HSVToRGB(hue, 0.9f, 1.0f);

        // Lifetime from chroma confidence (max bin value after normalisation)
        float confidence = 0.5f;
        if (frame.chromaVector != null)
        {
            float maxBin = 0f;
            foreach (float v in frame.chromaVector)
                if (v > maxBin) maxBin = v;
            confidence = maxBin;
        }
        var lifetime = main.startLifetime;
        lifetime.constant = Mathf.Lerp(0.2f, 1.5f, confidence);
        main.startLifetime = lifetime;

        if (instanceMaterial != null)
            instanceMaterial.SetColor("_BaseColor", noteColor);
    }

    public void Reset()
    {
        if (ps == null) return;
        var main = ps.main;
        main.startSize = baseSize;
        main.startLifetime = new ParticleSystem.MinMaxCurve(baseLifetime);
        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = baseRate;
        emission.rateOverTime = rate;
    }
}
