using UnityEngine;

/// <summary>
/// Drives the existing MusicWave_Sorting ParticleSystem using audio
/// features instead of the static hardcoded values it currently has.
///
/// Built against frame.rmsValue/envelopeValue rather than
/// frame.spectrumMagnitudes -- this was the first responder implemented,
/// specifically because RmsEnvelopeFollower is trivial (much simpler than
/// FFT), letting the FULL pipeline (capture -> extract -> respond ->
/// visible change) be validated end-to-end before the other extractors
/// existed. Kept as the RMS_Envelope responder mode alongside the other,
/// later responders (FFTResponder, ChromaResponder, etc.) rather than
/// being replaced by them.
///
/// Usage: attach to the same GameObject as ToneBlock (alongside
/// NoteAudioVisualizer), assign the `targetParticles` field to the
/// existing MusicWave_Sorting ParticleSystem in the Inspector.
/// </summary>
[System.Serializable]
public class ParticleRippleResponder : IVisualResponder
{
    public string MethodName => "Particle ripple (RMS/envelope-driven)";

    private readonly ParticleSystem targetParticles;
    private readonly ParticleSystemRenderer targetRenderer;

    // Baseline values captured once at startup, so "audio-reactive" means
    // "modulated around the artist-authored defaults", not hardcoded
    // magic numbers that fight whatever was tuned in the Inspector.
    private readonly float baseStartSize;
    private readonly float baseStartSpeed;
    private readonly ParticleSystem.MinMaxCurve baseStartColorAlpha;

    // Tuning knobs -- how strongly envelope/rms affects each parameter.
    // Exposed here as constructor params rather than buried as magic
    // numbers, so NoteAudioVisualizer (or an Inspector-exposed wrapper)
    // can adjust these without touching this class.
    private readonly float sizeResponseScale;
    private readonly float speedResponseScale;

    public ParticleRippleResponder(ParticleSystem targetParticles, float sizeResponseScale = 2.0f, float speedResponseScale = 1.5f)
    {
        this.targetParticles = targetParticles;
        this.targetRenderer = targetParticles.GetComponent<ParticleSystemRenderer>();
        this.sizeResponseScale = sizeResponseScale;
        this.speedResponseScale = speedResponseScale;

        var main = targetParticles.main;
        baseStartSize = main.startSize.constant;
        baseStartSpeed = main.startSpeed.constant;
    }

    public void Apply(in AudioFeatureFrame frame)
    {
        if (targetParticles == null)
            return;

        float driveValue = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;

        var main = targetParticles.main;
        main.startSize = baseStartSize * (1f + driveValue * sizeResponseScale);
        main.startSpeed = baseStartSpeed * (1f + driveValue * speedResponseScale);

        // Color mapping: use symbolic pitch class (ground truth from ToneBlock)
        // rather than any audio-detected pitch value. dominantFrequencyHz
        // proved unreliable for marimba samples because overtones dominate
        // the FFT spectrum -- the loudest bin is often a harmonic rather
        // than the fundamental. Chroma features fold all octaves together
        // and are far more robust, but this responder predates
        // ChromaExtractor and keeps using ground-truth pitch class since it
        // is simpler and, being ground truth, cannot be wrong.
        // 7 diatonic notes (C=0..B=6) mapped evenly across the hue wheel.
        float hue = frame.symbolicPitchClass / 7f;
        Color noteColor = Color.HSVToRGB(hue, 0.85f, 1.0f);

        // Set color directly on the material's _BaseColor property rather
        // than via ParticleSystem.main.startColor. The URP particle shader
        // uses shader_feature (not multi_compile) for Color Mode keywords,
        // meaning _COLORCOLOR_ON (Multiply) may not be compiled into the
        // active shader variant -- causing startColor to be silently ignored.
        // _BaseColor is always respected regardless of Color Mode.
        if (targetRenderer != null && targetRenderer.material != null)
        {
            targetRenderer.material.SetColor("_BaseColor", noteColor);
        }

        if (frame.noteJustStarted)
        {
            targetParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            targetParticles.Play();
            // Set color explicitly AFTER Play() so it isn't overwritten
            // by Unity resetting module values at the moment of Play().
            main.startColor = new ParticleSystem.MinMaxGradient(noteColor);
            Debug.Log($"[ParticleRipple] noteJustStarted=True: pitchClass={frame.symbolicPitchClass}, " +
                      $"hue={hue:F3}, noteColor={noteColor}, isPlaying after Play()={targetParticles.isPlaying}");
        }
        else
        {
            // Keep updating color every frame so it stays correct even if
            // the pitch-class changes mid-note (e.g. rapid note switching).
            main.startColor = new ParticleSystem.MinMaxGradient(noteColor);
        }
    }

    public void Reset()
    {
        if (targetParticles == null)
            return;

        // Return to the artist-authored baseline rather than leaving the
        // particle system sized/sped at whatever the last loud frame was.
        var main = targetParticles.main;
        main.startSize = baseStartSize;
        main.startSpeed = baseStartSpeed;
    }
}