using System;
using UnityEngine;

/// <summary>
/// Responder 6/6 -- combined, timbre-robust responder intended for actual gameplay/study use. The
/// other five (RMS/FFT/Chroma/Onset/STFT) stay selectable in the Inspector purely for isolated
/// single-extractor evaluation/testing -- this one fuses them into a single visual language:
///
///   - Color: ground-truth symbolicPitchClass on the same 12-tone hue wheel ChromaResponder uses --
///     always correct regardless of analysis noise, since it's read directly from the note the game
///     is actually playing rather than detected from the audio signal.
///
///   - Size / density / speed: driven by a per-note NORMALIZED envelope ("drive") -- frame.envelopeValue
///     divided by that note's own AudioClip peak amplitude (frame.clipPeakAmplitude) -- rather than raw
///     amplitude. This is the actual fix for "guitar huge, marimba normal": raw RMS/envelope reflects
///     how loud each instrument's sample happens to be recorded/mixed, not how full THIS note is within
///     its own dynamic range. Dividing by the clip's own peak puts every instrument on the same 0-1
///     scale, so the visual PEAKS comparably across instruments; each still DECAYS at its own natural
///     rate afterward (marimba fast, guitar slow) -- which is the actual audible/musical difference you
///     want the visuals to carry, rather than an artifact of sample loudness.
///
///   - Extra density from STFT mid/high spectral balance, using the same ratio-based approach as
///     STFTResponder (each band normalized against this frame's own rmsValue) -- already timbre-relative
///     since it's a proportion, not an absolute magnitude.
///
///   - Onset burst: a brief, fixed-ratio kick to size/speed on the exact frame an onset fires,
///     independent of drive -- so an instrument with a very soft/gradual envelope still reads a clear,
///     visible attack moment.
///
///   - Small optional manual per-instrument multiplier (Settings.instrumentGain) on top of the automatic
///     normalization, for any residual art-direction difference that doesn't fully wash out -- e.g. a
///     sustained note still occupies more visual "screen time" near its peak than a percussive hit, even
///     at equal relative loudness. Defaults to 1 (no adjustment) for every instrument -- start there and
///     only nudge a specific instrument after watching it play.
/// </summary>
public class CombinedResponder : IVisualResponder
{
    [Serializable]
    public class Settings
    {
        [Header("Color")]
        [Tooltip("Saturation/brightness for the ground-truth pitch-class hue wheel.")]
        public float colorSaturation = 0.9f;
        public float colorBrightness = 1f;

        [Header("Ripple size (radius)")]
        [Tooltip("Multiplies the prefab's own base startSize before any drive is applied.")]
        public float baseSizeMultiplier = 1f;
        [Tooltip("How much the normalized per-note envelope (0-1) grows size on top of base.")]
        public float sizeDriveScale = 6f;

        [Header("Ripple density (emission rate)")]
        public float baseRateMultiplier = 1f;
        [Tooltip("How much the normalized envelope adds to emission rate.")]
        public float densityDriveScale = 900f;
        [Tooltip("How much STFT mid/high spectral energy (relative, not absolute) adds extra density " +
                 "-- brighter transients read as denser ripples.")]
        public float densitySpectralScale = 700f;

        [Header("Ripple speed (outward movement)")]
        public float baseSpeedMultiplier = 1f;
        public float speedDriveScale = 4f;

        // Loudness-driven scaling alone barely varies between different notes of the SAME instrument,
        // since they're pitch-shifted copies of one source recording sharing nearly the same envelope
        // shape -- color is the only thing that currently tells them apart. This adds an explicit
        // pitch-based push on top, scaled per SEMITONE (not normalized against the full 5-octave
        // theoretical range, which made real note-to-note differences within a typical melody -- a
        // handful of semitones -- barely register). Lower notes get bigger/slower ripples, higher
        // notes smaller/faster.
        [Header("Pitch influence on size/speed")]
        [Tooltip("Absolute semitone (MusicPhrase.NoteToAbsoluteStep value) at which size/speed = 1x, " +
                 "no adjustment. Notes below this get bigger/slower, above get smaller/faster. Set " +
                 "this near the middle of the pitch range your levels actually use.")]
        public float referenceSemitone = 36f;
        [Tooltip("Size multiplier change per semitone away from referenceSemitone. 0.035 = ~3.5% per " +
                 "semitone, ~42% per octave. Raise this if the effect still isn't visible enough.")]
        [Range(0f, 0.15f)] public float pitchSizePerSemitone = 0.035f;
        [Range(0f, 0.15f)] public float pitchSpeedPerSemitone = 0.035f;

        [Header("Onset burst (note attack)")]
        [Tooltip("Fixed multiplier applied to size/speed on the exact frame an onset fires -- " +
                 "independent of drive, so soft-envelope instruments still get a clear visible attack.")]
        public float onsetSizeBurstMultiplier = 2.5f;
        public float onsetSpeedBurstMultiplier = 2f;

        [Header("Lifetime")]
        public float minLifetime = 0.2f;
        public float maxLifetime = 1.5f;

        [Header("Silence gate")]
        [Tooltip("Below this normalized drive (0-1), treat the note as effectively silent and stop " +
                 "emitting new particles entirely -- even though AudioSource.isPlaying may still be " +
                 "true. Clips have a quiet decay/resonance tail after their audible content ends " +
                 "(confirmed by your own analysisDetectedDuration vs. clip-length evaluation data), " +
                 "and without this gate that tail gets visualized as a few seconds of small, flat, " +
                 "repetitive particles once the real musical energy is already gone.")]
        [Range(0f, 0.3f)] public float silenceThreshold = 0.04f;
        [Tooltip("How long drive must stay continuously below silenceThreshold before existing " +
                 "particles get force-cleared (not just new emission stopped). A short grace period " +
                 "avoids killing particles on a brief dip mid-note (e.g. a quiet passage between two " +
                 "loud strums); once it's actually the end-of-clip silent tail, this is how long that " +
                 "tail is allowed to keep visibly fading before getting cut short.")]
        public float silenceHangoverSeconds = 0.15f;

        [Header("Per-instrument fine-tuning (optional, on top of automatic per-note normalization)")]
        [Tooltip("Index order matches ToneGameManager.INSTRUMENTS: Marimba, Guitar, Percussion, Bass. " +
                 "1 = no adjustment. Start at 1 for all and only change a value if that specific " +
                 "instrument still looks disproportionate after testing -- the automatic " +
                 "clipPeakAmplitude normalization should already handle most of the difference.")]
        public float[] instrumentGain = new float[] { 1f, 1f, 1f, 1f };

        [Header("Spectral band split (mirrors STFTResponder)")]
        public float bassMaxHz = 250f;
        public float midMaxHz = 2000f;
        [Range(0.01f, 1f)] public float smoothingAttack = 0.7f;
        [Range(0.01f, 1f)] public float smoothingRelease = 0.2f;

        [Header("Debug")]
        [Tooltip("Logs drive/silence-gate/onset state every frame while a note plays. Turn on " +
                 "temporarily to see exactly what's happening during an end-of-note tail instead of " +
                 "guessing -- turn back off once diagnosed, this will flood the console otherwise.")]
        public bool debugLogging = false;
    }

    public string MethodName => "Combined (ground-truth color + STFT density + onset burst, per-note normalized)";

    private readonly ParticleSystem ps;
    private readonly ParticleSystemRenderer psRenderer;
    private readonly Material instanceMaterial;
    private readonly Settings settings;

    private readonly float baseSize;
    private readonly float baseRate;
    private readonly float baseSpeed;
    private readonly float baseLifetime;

    private float smoothMid, smoothHigh;
    private float silenceTimer;
    private bool clearedForSilence;

    public CombinedResponder(ParticleSystem ps, Settings settings)
    {
        this.ps = ps;
        this.settings = settings ?? new Settings();
        this.psRenderer = ps.GetComponent<ParticleSystemRenderer>();

        // Per-instance material, same pattern as every other responder -- avoids mutating the
        // shared/prefab material.
        if (psRenderer != null && psRenderer.sharedMaterial != null)
        {
            instanceMaterial = new Material(psRenderer.sharedMaterial);
            psRenderer.material = instanceMaterial;
        }

        var main = ps.main;
        baseSize = main.startSize.constant;
        baseRate = ps.emission.rateOverTime.constant;
        baseSpeed = main.startSpeed.constant;
        baseLifetime = main.startLifetime.constant;
    }

    public void Apply(in AudioFeatureFrame frame)
    {
        if (ps == null) return;

        if (frame.noteJustStarted)
        {
            smoothMid = smoothHigh = 0f;
            silenceTimer = 0f;
            clearedForSilence = false;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        // --- Core timbre-robustness fix: normalize against THIS note's own peak, not an absolute
        // amplitude, so a quiet marimba sample and a loud guitar sample both read as "drive = 1" at
        // their own respective loudest moment. ---
        float rawEnvelope = frame.envelopeValue > 0f ? frame.envelopeValue : frame.rmsValue;
        float drive = frame.clipPeakAmplitude > 0.0001f
            ? Mathf.Clamp01(rawEnvelope / frame.clipPeakAmplitude)
            : Mathf.Clamp01(rawEnvelope);

        float instrumentGain = 1f;
        if (settings.instrumentGain != null &&
            frame.instrumentIndex >= 0 && frame.instrumentIndex < settings.instrumentGain.Length)
        {
            instrumentGain = settings.instrumentGain[frame.instrumentIndex];
        }
        drive = Mathf.Clamp01(drive * instrumentGain);

        // --- Silence gate ---
        // A live per-frame threshold alone can't reliably tell "the note is still genuinely decaying,
        // just quietly" apart from "we're now in the clip's dead tail" -- both can sit at a similarly
        // low envelope level for a while (this is exactly what testing found: no single threshold/
        // hangover pair both killed the artifact tail AND let the real decay taper smoothly). So this
        // combines two signals:
        //   - the live threshold (catches genuinely near-zero audio in general), and
        //   - a ground-truth check against clipTailStartSeconds -- the timestamp (found once per clip
        //     by scanning the WHOLE clip's data, see NoteAudioVisualizer.GetOrComputeClipAnalysis)
        //     after which this specific clip never rises back above ~5% of its own peak again. Once
        //     actual playback position passes that point, we know for certain we're in the dead tail,
        //     regardless of what the live envelope happens to read at this exact instant.
        // This lets silenceThreshold stay low/lenient (won't cut the real decay short) while the
        // ground-truth check reliably catches the specific artifact tail once we're actually in it.
        bool pastKnownTail = frame.clipTailStartSeconds > 0f &&
                              frame.clipPlaybackPositionSeconds >= frame.clipTailStartSeconds;
        bool effectivelySilent = drive < settings.silenceThreshold || pastKnownTail;

        // Stopping new emission alone isn't enough -- particles already spawned during the loud part
        // of the note were assigned lifetimes up to maxLifetime (1.5s by default) and keep existing/
        // rendering for that long regardless of what happens afterward. That's what was still visible
        // even after the emission-only gate: a graceful natural fade-out, just one that legitimately
        // takes up to another second and a half. If a brief continuous silence has now persisted past
        // silenceHangoverSeconds, force-clear whatever's left so the tail actually ends when the note
        // does, rather than however long ago the last loud particle happened to spawn.
        if (effectivelySilent)
        {
            silenceTimer += Time.deltaTime;
            if (silenceTimer >= settings.silenceHangoverSeconds && !clearedForSilence)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                clearedForSilence = true;
            }
        }
        else
        {
            silenceTimer = 0f;
            clearedForSilence = false;
        }

        // --- Extra density from spectral balance -- same ratio approach as STFTResponder, already
        // timbre-relative since each band is normalized against this frame's own rmsValue. ---
        float midAvg = 0f, highAvg = 0f;
        if (frame.stftMagnitudeFrames != null)
        {
            int nF = frame.stftMagnitudeFrames.GetLength(0);
            int nB = frame.stftMagnitudeFrames.GetLength(1);
            int last = nF - 1;
            float sr = frame.sampleRate > 0 ? frame.sampleRate : 44100f;
            float binHz = sr / (nB * 2f);
            float norm = frame.rmsValue > 0.0001f ? frame.rmsValue : 1f;

            float midSum = 0f; int midN = 0;
            float highSum = 0f; int highN = 0;
            for (int k = 0; k < nB; k++)
            {
                float hz = k * binHz;
                if (hz < settings.bassMaxHz) continue; // bass excluded from density on purpose --
                                                        // low end doesn't read as "sparkle/density"
                float mag = frame.stftMagnitudeFrames[last, k];
                if (hz < settings.midMaxHz) { midSum += mag; midN++; }
                else { highSum += mag; highN++; }
            }
            midAvg = Mathf.Clamp01(midN > 0 ? midSum / midN / norm : 0f);
            highAvg = Mathf.Clamp01(highN > 0 ? highSum / highN / norm : 0f);
        }
        smoothMid = Smooth(smoothMid, midAvg);
        smoothHigh = Smooth(smoothHigh, highAvg);

        // --- Onset burst: fixed-ratio kick independent of drive, so even a soft-envelope instrument
        // still shows a clear attack moment. Explicitly gated by !effectivelySilent -- a spectral-flux
        // blip from decaying harmonics/resonance during the tail can still cross OnsetDetector's own
        // (unnormalized, fixed 0.03) threshold even when nothing audible/meaningful is happening, and
        // that should never count as a fresh "attack" worth bursting for.
        bool onsetCountsAsBurst = frame.onsetDetected && !effectivelySilent;
        float onsetSizeMul = onsetCountsAsBurst ? settings.onsetSizeBurstMultiplier : 1f;
        float onsetSpeedMul = onsetCountsAsBurst ? settings.onsetSpeedBurstMultiplier : 1f;

        // --- Pitch influence: an explicit, always-present note-to-note differentiator for
        // size/speed, since loudness-driven drive alone barely differs between notes of the same
        // instrument (pitch-shifted copies of one recording, nearly identical envelope shape).
        // symbolicPitchClass is MusicPhrase.NoteToAbsoluteStep() -- a full chromatic semitone count
        // across up to NUMBER_OF_OKTAVES (5) octaves (octave*12 + step), not a small 0-7 index.
        //
        // Deliberately NOT wrapped with % 12 here (unlike color's hue, a few lines below, which SHOULD
        // wrap -- C0 and C1 are meant to look the same color). Size/speed trend continuously across
        // the whole range instead -- C1 needs to read as smaller/faster than B0, not jump back to
        // "biggest" just because it crossed into a new octave.
        //
        // Scaled per-semitone from a reference point, NOT normalized against the full 0-60 theoretical
        // range (that was the previous bug: real test notes like G2/A2/C3 only span a few semitones
        // out of that 60-wide range, so their normalized "position" barely differed -- a ~5% size
        // spread, invisible next to the ~4x swing loudness/onset already produces). Per-semitone
        // scaling means ANY handful of semitones apart produces a consistent, visible difference
        // regardless of which octave the notes happen to sit in.
        float semitoneOffset = frame.symbolicPitchClass - settings.referenceSemitone;
        float pitchSizeFactor = Mathf.Clamp(1f - semitoneOffset * settings.pitchSizePerSemitone, 0.15f, 3f);
        float pitchSpeedFactor = Mathf.Clamp(1f + semitoneOffset * settings.pitchSpeedPerSemitone, 0.15f, 3f);

        var main = ps.main;
        main.startSize = Mathf.Max(0.01f,
            baseSize * settings.baseSizeMultiplier * (1f + drive * settings.sizeDriveScale)
            * onsetSizeMul * pitchSizeFactor);

        main.startSpeed = Mathf.Max(0.01f,
            baseSpeed * settings.baseSpeedMultiplier * (1f + drive * settings.speedDriveScale)
            * onsetSpeedMul * pitchSpeedFactor);

        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = effectivelySilent ? 0f : Mathf.Max(0f,
            baseRate * settings.baseRateMultiplier
            + drive * settings.densityDriveScale
            + (smoothMid + smoothHigh) * 0.5f * settings.densitySpectralScale);
        emission.rateOverTime = rate;

        var lifetime = main.startLifetime;
        lifetime.constant = Mathf.Lerp(settings.minLifetime, settings.maxLifetime, drive);
        main.startLifetime = lifetime;

        if (settings.debugLogging)
        {
            Debug.Log($"[CombinedResponder] drive={drive:F4} rawEnvelope={rawEnvelope:F4} " +
                      $"peak={frame.clipPeakAmplitude:F4} playbackPos={frame.clipPlaybackPositionSeconds:F3}s " +
                      $"tailStart={frame.clipTailStartSeconds:F3}s pastKnownTail={pastKnownTail} " +
                      $"effectivelySilent={effectivelySilent} silenceTimer={silenceTimer:F3} " +
                      $"clearedForSilence={clearedForSilence} onsetDetected={frame.onsetDetected} " +
                      $"onsetCountsAsBurst={onsetCountsAsBurst} symbolicPitchClass={frame.symbolicPitchClass} " +
                      $"semitoneOffset={semitoneOffset:F1} pitchSizeFactor={pitchSizeFactor:F3} " +
                      $"pitchSpeedFactor={pitchSpeedFactor:F3} appliedRate={rate.constant:F1} " +
                      $"appliedSize={main.startSize.constant:F3} appliedSpeed={main.startSpeed.constant:F3}");
        }

        // --- Ground-truth color -- always correct, never depends on audio analysis. ---
        float hue = (frame.symbolicPitchClass % 12) / 12f;
        Color noteColor = Color.HSVToRGB(hue, settings.colorSaturation, settings.colorBrightness);
        if (instanceMaterial != null)
            instanceMaterial.SetColor("_BaseColor", noteColor);
    }

    public void Reset()
    {
        smoothMid = smoothHigh = 0f;
        silenceTimer = 0f;
        clearedForSilence = false;
        if (ps == null) return;

        // Called the instant isPlaying goes false (note fully over). Particles spawned during the
        // loud part of the note can carry lifetimes up to maxLifetime (1.5s default) -- for a short
        // note (this one was ~0.5s total) that's longer than the note itself, so without an explicit
        // clear here they just keep existing/animating on their own schedule after the note has
        // already ended, which is the actual tail that was still showing up. Hard-clear immediately
        // instead of leaving it to natural per-particle fade-out.
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.startSize = baseSize;
        main.startSpeed = baseSpeed;
        main.startLifetime = new ParticleSystem.MinMaxCurve(baseLifetime);

        var emission = ps.emission;
        var rate = emission.rateOverTime;
        rate.constant = baseRate;
        emission.rateOverTime = rate;
    }

    private float Smooth(float current, float target)
    {
        float c = target > current ? settings.smoothingAttack : settings.smoothingRelease;
        return current + (target - current) * c;
    }
}
