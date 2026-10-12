using UnityEngine;

/// <summary>
/// RMS (root-mean-square) amplitude + attack/release envelope follower.
///
/// The math is simple (root-mean-square of the buffer, plus a one-pole
/// smoothing filter for attack/release), which is why this extractor was
/// the first one implemented in the pipeline -- it let the full
/// capture -> extract -> respond -> visible change chain be validated
/// end-to-end before the more involved FFT/chroma/onset extractors existed.
/// </summary>
public class RmsEnvelopeFollower : IAudioFeatureExtractor
{
    public string MethodName => "RMS / envelope follower";

    // RMS itself needs very few samples to be meaningful (unlike FFT,
    // which needs a full window for frequency resolution), so we only
    // require a small minimum to avoid acting on a near-empty buffer.
    public int RequiredSampleCount => 512;

    // Attack/release coefficients for the envelope smoothing filter.
    // Higher = faster response. These are deliberately simple constants
    // for now -- exposing them as tunable fields is a natural next step
    // once you're comparing how responsiveness affects user perception.
    private readonly float attackCoefficient;
    private readonly float releaseCoefficient;

    private float currentEnvelope;

    public RmsEnvelopeFollower(float attackCoefficient = 0.9f, float releaseCoefficient = 0.3f)
    {
        this.attackCoefficient = attackCoefficient;
        this.releaseCoefficient = releaseCoefficient;
    }

    public void Process(in AudioBuffer buffer, ref AudioFeatureFrame frame)
    {
        if (buffer.samples.Length < RequiredSampleCount)
            return;

        // RMS = sqrt(mean(samples^2)) -- the standard measure of a
        // signal's "average loudness" over the buffer, as opposed to a
        // single instantaneous sample value which would be noisy.
        float sumOfSquares = 0f;
        for (int i = 0; i < buffer.samples.Length; i++)
        {
            float s = buffer.samples[i];
            sumOfSquares += s * s;
        }
        float rms = Mathf.Sqrt(sumOfSquares / buffer.samples.Length);

        // One-pole smoothing filter, with separate attack/release speeds
        // (standard envelope-follower technique): rises quickly on
        // transients, falls more slowly on decay, which avoids visual
        // flicker from frame-to-frame RMS jitter while still feeling
        // responsive to note onsets.
        float coefficient = rms > currentEnvelope ? attackCoefficient : releaseCoefficient;
        currentEnvelope += (rms - currentEnvelope) * coefficient;

        frame.rmsValue = rms;
        frame.envelopeValue = currentEnvelope;
        frame.sourceId = buffer.sourceId;
        frame.dspTime = buffer.dspTime;
    }
}
