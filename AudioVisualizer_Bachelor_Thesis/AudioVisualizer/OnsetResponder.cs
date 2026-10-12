using UnityEngine;

/// <summary>
/// Responder 4/5 — Onset Detection (Spectral Flux)
/// Fires a sharp visual burst at note attack, decays with onset strength.
/// White color — timing only, no pitch information encoded.
/// </summary>
public class OnsetResponder : IVisualResponder
{
    public string MethodName => "Onset detection (spectral flux → size burst)";

    private readonly ParticleSystem ps;
    private readonly ParticleSystemRenderer psRenderer;
    private readonly Material instanceMaterial;
    private readonly float baseSize;
    private readonly float baseRate;
    private readonly float baseSpeed;

    private const float BURST_SIZE_SCALE  = 8.0f;
    private const float DECAY_SIZE_SCALE  = 3.0f;
    private const float BURST_SPEED_SCALE = 3.0f;

    public OnsetResponder(ParticleSystem ps)
    {
        this.ps = ps;
        this.psRenderer = ps.GetComponent<ParticleSystemRenderer>();

        if (psRenderer != null && psRenderer.sharedMaterial != null)
        {
            instanceMaterial = new Material(psRenderer.sharedMaterial);
            psRenderer.material = instanceMaterial;
        }

        var main = ps.main;
        baseSize  = main.startSize.constant;
        baseRate  = ps.emission.rateOverTime.constant;
        baseSpeed = main.startSpeed.constant;
    }

    public void Apply(in AudioFeatureFrame frame)
    {
        if (ps == null) return;

        if (frame.noteJustStarted)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        var main = ps.main;

        if (frame.onsetDetected)
        {
            main.startSize  = baseSize  * BURST_SIZE_SCALE;
            main.startSpeed = baseSpeed * BURST_SPEED_SCALE;
        }
        else
        {
            float str = frame.onsetStrength;
            main.startSize  = Mathf.Max(baseSize,
                baseSize * (1f + str * DECAY_SIZE_SCALE));
            main.startSpeed = Mathf.Max(baseSpeed,
                baseSpeed * (1f + str * BURST_SPEED_SCALE * 0.5f));
        }

        if (instanceMaterial != null)
            instanceMaterial.SetColor("_BaseColor", Color.white);
    }

    public void Reset()
    {
        if (ps == null) return;
        var main = ps.main;
        main.startSize  = baseSize;
        main.startSpeed = baseSpeed;
    }
}
