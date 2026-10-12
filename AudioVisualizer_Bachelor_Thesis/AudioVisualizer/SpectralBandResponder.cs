using UnityEngine;

/// <summary>
/// Splits the FFT magnitude spectrum into 3 frequency bands (bass/mid/high)
/// and computes a smoothed average per band every frame.
///
/// Original design intent (why 3 bands rather than 1 RMS value): a single
/// amplitude value rises and falls symmetrically -- it can't capture the
/// fact that a marimba note's fundamental decays faster than its overtones.
/// 3 independently-moving bands were meant to give the visual more life:
/// SIZE pulsing at attack (bass spike), RATE sustaining while the note
/// sounds (mid presence), HUE shifting as the note ages into its brighter
/// harmonics (high band growing relatively stronger as the fundamental
/// decays).
///
/// Current behavior: none of the three band averages (smoothedBass/Mid/High)
/// are actually read anywhere below -- they're computed and smoothed every
/// frame but never wired to a visual parameter. Size and lifetime are
/// instead driven by the locked, ground-truth symbolic pitch class, and
/// emission rate by RMS/envelope (see the hybrid-approach comment further
/// down). The band computation is left in place rather than removed, in
/// case per-band mapping is revisited later.
///
/// Requires FFTExtractor to be registered in NoteAudioVisualizer --
/// frame.spectrumMagnitudes must be non-null for this responder to act.
/// Falls back to an RMS-only size/color response if spectrum data isn't
/// available yet, rather than leaving the particle system visually dead.
/// </summary>
[System.Serializable]
public class SpectralBandResponder : IVisualResponder
{
    public string MethodName => "Spectral band responder (bass/mid/high → size/rate/hue)";

    private readonly ParticleSystem targetParticles;
    private readonly ParticleSystemRenderer targetRenderer;

    // Frequency band boundaries in Hz -- these are standard audio engineering
    // divisions, not arbitrary. Bass covers the fundamental range of most
    // instruments including marimba (lowest note ~65Hz, highest ~1047Hz),
    // Mid covers the bulk of harmonic content, High covers upper partials
    // and the brightness that characterizes timbre aging in percussion.
    private const float BASS_MAX_HZ  = 250f;
    private const float MID_MAX_HZ   = 2000f;
    // High band: MID_MAX_HZ to Nyquist (sampleRate/2)

    // How strongly each band drives its parameter.
    // These are starting points -- tune in the Inspector via
    // NoteAudioVisualizer's exposed fields once wired up.
    private readonly float sizeScale;
    private readonly float rateScale;
    private readonly float hueShiftScale;

    // Baseline particle system values captured once at construction,
    // so audio-reactive modulation is relative to the artist-authored
    // defaults rather than fighting them.
    private readonly float baseStartSize;
    private readonly float baseEmissionRate;

    // Despite the name, this is normalized symbolic pitch class, not an
    // FFT-derived spectral centroid -- see the hybrid-approach comment in
    // Apply() for why. Locked once per note a few frames after onset and
    // held for the note's duration rather than recomputed every frame.
    private float lockedCentroidNorm = 0f;
    // Delay the lock by a few frames after note start: on frame 0
    // (noteJustStarted), the audio buffer may still contain leftover
    // energy from the previous note's decay. By CENTROID_LOCK_FRAME the
    // buffer is genuinely fresh, though since the locked value is now
    // symbolic pitch class rather than anything read from that buffer,
    // this delay is a precaution carried over from an earlier
    // FFT-centroid-based version rather than a strict requirement.
    private int framessinceNoteStart = 0;
    private const int CENTROID_LOCK_FRAME = 3;
    // Separate smoothed values per band since they have different
    // temporal characteristics (bass transient vs high sustained).
    private float smoothedBass;
    private float smoothedMid;
    private float smoothedHigh;
    private const float SMOOTH_ATTACK  = 0.7f;
    private const float SMOOTH_RELEASE = 0.2f;

    public SpectralBandResponder(
        ParticleSystem targetParticles,
        float sizeScale     = 1.5f,   // centroid 0-1 * 1.5 → size up to 2.5x base at high notes
        float rateScale     = 750f,   // RMS * 750 → emission burst at onset
        float hueShiftScale = 5.0f)   // unused now but kept for future use
    {
        this.targetParticles = targetParticles;
        this.targetRenderer  = targetParticles.GetComponent<ParticleSystemRenderer>();
        this.sizeScale       = sizeScale;
        this.rateScale       = rateScale;
        this.hueShiftScale   = hueShiftScale;

        var main     = targetParticles.main;
        var emission = targetParticles.emission;
        baseStartSize    = main.startSize.constant;
        baseEmissionRate = emission.rateOverTime.constant;
    }

    public void Apply(in AudioFeatureFrame frame)
    {
        if (targetParticles == null)
            return;

        // Trigger Play() on note onset -- same pattern as before.
        if (frame.noteJustStarted)
        {
            targetParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            targetParticles.Play();
        }

        // Always set base color from symbolic pitch class so notes are
        // visually distinct regardless of spectrum data availability.
        // The high-band hue shift is applied on top of this base hue below.
        float baseHue  = frame.symbolicPitchClass / 7f;

        // If no spectrum data yet (FFTExtractor hasn't produced output),
        // fall back to RMS-only behavior so the visual isn't dead silent.
        if (frame.spectrumMagnitudes == null || frame.spectrumMagnitudes.Length == 0)
        {
            float drive = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;
            var mainFallback = targetParticles.main;
            mainFallback.startSize = baseStartSize * (1f + drive * sizeScale * 0.5f);
            SetColor(Color.HSVToRGB(baseHue, 0.85f, 1.0f));
            return;
        }

        // --- Compute per-band average magnitude ---
        // bin width = sampleRate / windowSize; spectrumMagnitudes.Length is
        // windowSize/2, so binToHz = sampleRate / (Length * 2). Falls back
        // to 48000Hz only if frame.sampleRate wasn't populated (e.g. an
        // older/partial AudioFeatureFrame), which should not normally happen.
        float binToHz = frame.sampleRate > 0
            ? frame.sampleRate / (frame.spectrumMagnitudes.Length * 2f)
            : 48000f / (frame.spectrumMagnitudes.Length * 2f);

        float bassSum = 0f;  int bassCount = 0;
        float midSum  = 0f;  int midCount  = 0;
        float highSum = 0f;  int highCount = 0;

        for (int k = 0; k < frame.spectrumMagnitudes.Length; k++)
        {
            float freqHz = k * binToHz;
            float mag    = frame.spectrumMagnitudes[k];

            if (freqHz < BASS_MAX_HZ)
            { bassSum += mag; bassCount++; }
            else if (freqHz < MID_MAX_HZ)
            { midSum  += mag; midCount++;  }
            else
            { highSum += mag; highCount++; }
        }

        float bassAvg = bassCount > 0 ? bassSum / bassCount : 0f;
        float midAvg  = midCount  > 0 ? midSum  / midCount  : 0f;
        float highAvg = highCount > 0 ? highSum / highCount : 0f;

        // Normalize by overall RMS so band values live in a 0-1 range
        // rather than the raw 0.0001-0.005 FFT magnitude range.
        // This makes scale parameters intuitive: sizeScale=3 means
        // "at peak loudness, size is 3x the base" rather than requiring
        // scale values in the hundreds to produce a visible effect.
        // Falls back to raw values if RMS is zero (silence).
        float normFactor = frame.rmsValue > 0.0001f ? frame.rmsValue : 1f;
        bassAvg /= normFactor;
        midAvg  /= normFactor;
        highAvg /= normFactor;

        // Clamp to [0,1] so a single loud frame doesn't explode the visual
        bassAvg = Mathf.Clamp01(bassAvg);
        midAvg  = Mathf.Clamp01(midAvg);
        highAvg = Mathf.Clamp01(highAvg);

        // --- Smooth each band independently ---
        smoothedBass = Smooth(smoothedBass, bassAvg);
        smoothedMid  = Smooth(smoothedMid,  midAvg);
        smoothedHigh = Smooth(smoothedHigh, highAvg);

        // --- Map features to particle parameters ---
        // Key insight: all four notes (C/G/A/B) have harmonics in the
        // same MID band, so band averages don't differ meaningfully between
        // notes. spectralCentroid DOES differ: C~300Hz, G~500Hz, A~550Hz,
        // B~600Hz -- it encodes pitch in the signal, not just loudness.

        // Count frames since note started to delay centroid lock.
        if (frame.noteJustStarted)
        {
            framessinceNoteStart = 0;
            lockedCentroidNorm = 0f; // clear old lock immediately on new note
        }
        else if (framessinceNoteStart < CENTROID_LOCK_FRAME + 1)
        {
            framessinceNoteStart++;
        }

        // Lock centroid on frame CENTROID_LOCK_FRAME after onset.
        // Not on frame 0 (noteJustStarted) since the buffer may still
        // contain the previous note's elevated centroid from decay.
        // Use symbolic pitch class for size/lifetime rather than FFT-derived
        // dominantFrequencyHz. The audio buffer at lock time is frequently
        // contaminated by the previous note's fading tail -- on rapid retaps
        // the FFT sees a mix of old-note decay and new-note attack, causing
        // dominantHz to report the wrong note's frequency.
        //
        // symbolicPitchClass (from ToneBlock.blockNote) is ground truth:
        // always correct, zero latency, immune to buffer contamination.
        // This is a legitimate hybrid approach for your thesis -- use signal
        // analysis (RMS, spectrum shape) for dynamic/temporal parameters,
        // use symbolic data for stable pitch identity parameters.
        //
        // Note ordering by pitch: C=0(262Hz) D=1 E=2 F=3 G=4(392Hz) A=5(440Hz) B=6(494Hz)
        // Divide by 6 (not 7) so B maps to 1.0 and C maps to 0.0 exactly.
        if (framessinceNoteStart == CENTROID_LOCK_FRAME)
        {
            lockedCentroidNorm = Mathf.Clamp01(frame.symbolicPitchClass / 6f);

            var mainOnset = targetParticles.main;
            mainOnset.startSize = Mathf.Max(0.01f,
                baseStartSize * (1f + lockedCentroidNorm * sizeScale));

            var lifetimeOnset = mainOnset.startLifetime;
            lifetimeOnset.constant = Mathf.Lerp(1.5f, 0.3f, lockedCentroidNorm);
            mainOnset.startLifetime = lifetimeOnset;

            UnityEngine.Debug.Log($"[SpectralBand] LOCKED at frame {framessinceNoteStart}: " +
                                  $"pitchClass={frame.symbolicPitchClass}, " +
                                  $"norm={lockedCentroidNorm:F3}, " +
                                  $"size={mainOnset.startSize.constant:F3}, " +
                                  $"lifetime={mainOnset.startLifetime.constant:F3}");
        }

        // RMS/envelope → emission rate: onset burst, same for all notes
        // but gives the impact feel at attack regardless of pitch.
        var main = targetParticles.main;
        float envelopeDrive = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;
        var emission = targetParticles.emission;
        var rateOverTime = emission.rateOverTime;
        rateOverTime.constant = Mathf.Max(0f, baseEmissionRate + envelopeDrive * rateScale);
        emission.rateOverTime = rateOverTime;

        // Symbolic pitch → color (unchanged -- already working well)
        Color noteColor = Color.HSVToRGB(baseHue, 0.85f, 1.0f);
        SetColor(noteColor);
    }

    public void Reset()
    {
        if (targetParticles == null)
            return;

        smoothedBass = 0f;
        smoothedMid  = 0f;
        smoothedHigh = 0f;
        lockedCentroidNorm = 0f;
        framessinceNoteStart = 0;

        var main = targetParticles.main;
        main.startSize = baseStartSize;

        var lifetime = main.startLifetime;
        lifetime.constant = 1.0f;
        main.startLifetime = lifetime;

        var emission     = targetParticles.emission;
        var rateOverTime = emission.rateOverTime;
        rateOverTime.constant = baseEmissionRate;
        emission.rateOverTime = rateOverTime;
    }

    private void SetColor(Color color)
    {
        if (targetRenderer != null && targetRenderer.material != null)
            targetRenderer.material.SetColor("_BaseColor", color);
    }

    private float Smooth(float current, float target)
    {
        float coeff = target > current ? SMOOTH_ATTACK : SMOOTH_RELEASE;
        return current + (target - current) * coeff;
    }
}