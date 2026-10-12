using UnityEngine;

/// <summary>
/// Responder 1/5 — RMS / Envelope Follower
/// Maps amplitude envelope to particle size and emission rate.
/// Color is neutral white — demonstrates RMS carries no pitch information.
/// </summary>
public class RmsResponder : IVisualResponder
{
    public string MethodName => "RMS / Envelope (amplitude → size + emission rate)";

    private readonly ParticleSystem ps;
    private readonly ParticleSystemRenderer psRenderer;
    private readonly Material instanceMaterial;
    private readonly float baseSize;
    private readonly float baseRate;

    private const float SIZE_SCALE = 20f;
    private const float RATE_SCALE = 2000f;

    public RmsResponder(ParticleSystem ps)
    {
        this.ps = ps;
        this.psRenderer = ps.GetComponent<ParticleSystemRenderer>();

        // Create per-instance material to avoid prefab/shared material errors
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

        if (instanceMaterial != null)
            instanceMaterial.SetColor("_BaseColor", Color.white);
    }

    public void Reset()
    {
        if (ps == null) return;
        var main = ps.main;
        main.startSize = baseSize;
        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = baseRate;
        emission.rateOverTime = rate;
    }
}
