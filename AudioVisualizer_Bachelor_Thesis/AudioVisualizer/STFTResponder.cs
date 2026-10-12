using UnityEngine;

/// <summary>
/// Responder 5/5 — STFT Spectral Bands
/// Splits spectrum into bass/mid/high bands and maps each to
/// size, emission rate, and hue respectively.
/// </summary>
public class STFTResponder : IVisualResponder
{
    public string MethodName => "STFT spectral bands (bass/mid/high → size/rate/hue)";

    private readonly ParticleSystem ps;
    private readonly ParticleSystemRenderer psRenderer;
    private readonly Material instanceMaterial;
    private readonly float baseSize;
    private readonly float baseRate;

    private const float BASS_MAX_HZ = 250f;
    private const float MID_MAX_HZ  = 2000f;
    private const float SIZE_SCALE  = 70f;
    private const float RATE_SCALE  = 400f;
    private const float HUE_SHIFT   = 0.25f;
    private const float ATTACK      = 0.7f;
    private const float RELEASE     = 0.2f;

    private float smoothBass, smoothMid, smoothHigh;

    public STFTResponder(ParticleSystem ps)
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
            smoothBass = smoothMid = smoothHigh = 0f;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        // Fallback to RMS if STFT not yet available
        if (frame.stftMagnitudeFrames == null)
        {
            float drive = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;
            var mainFallback = ps.main;
            mainFallback.startSize = baseSize * (1f + drive * SIZE_SCALE * 0.1f);
            if (instanceMaterial != null)
                instanceMaterial.SetColor("_BaseColor",
                    Color.HSVToRGB((frame.symbolicPitchClass % 12) / 12f, 0.85f, 1f));
            return;
        }

        int nF      = frame.stftMagnitudeFrames.GetLength(0);
        int nB      = frame.stftMagnitudeFrames.GetLength(1);
        int last    = nF - 1;
        float sr    = frame.sampleRate > 0 ? frame.sampleRate : 44100f;
        float binHz = sr / (nB * 2f);

        float bassSum = 0f; int bassN = 0;
        float midSum  = 0f; int midN  = 0;
        float highSum = 0f; int highN = 0;

        for (int k = 0; k < nB; k++)
        {
            float hz  = k * binHz;
            float mag = frame.stftMagnitudeFrames[last, k];
            if      (hz < BASS_MAX_HZ) { bassSum += mag; bassN++; }
            else if (hz < MID_MAX_HZ)  { midSum  += mag; midN++;  }
            else                       { highSum += mag; highN++; }
        }

        float norm    = frame.rmsValue > 0.0001f ? frame.rmsValue : 1f;
        float bassAvg = Mathf.Clamp01(bassN > 0 ? bassSum / bassN / norm : 0f);
        float midAvg  = Mathf.Clamp01(midN  > 0 ? midSum  / midN  / norm : 0f);
        float highAvg = Mathf.Clamp01(highN > 0 ? highSum / highN / norm : 0f);

        smoothBass = Smooth(smoothBass, bassAvg);
        smoothMid  = Smooth(smoothMid,  midAvg);
        smoothHigh = Smooth(smoothHigh, highAvg);

        var main = ps.main;
        main.startSize = Mathf.Max(0.01f, baseSize * (1f + smoothBass * SIZE_SCALE));

        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = Mathf.Max(0f, baseRate + smoothMid * RATE_SCALE);
        emission.rateOverTime = rate;

        float baseHue  = (frame.symbolicPitchClass % 12) / 12f;
        float finalHue = (baseHue + smoothHigh * HUE_SHIFT) % 1f;
        if (instanceMaterial != null)
            instanceMaterial.SetColor("_BaseColor",
                Color.HSVToRGB(finalHue, 0.85f, 1.0f));
    }

    public void Reset()
    {
        smoothBass = smoothMid = smoothHigh = 0f;
        if (ps == null) return;
        var main = ps.main;
        main.startSize = baseSize;
        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = baseRate;
        emission.rateOverTime = rate;
    }

    private float Smooth(float current, float target)
    {
        float c = target > current ? ATTACK : RELEASE;
        return current + (target - current) * c;
    }
}
