using UnityEngine;

/// <summary>
/// Generic feature output container. Each extractor fills in only the
/// fields it's responsible for and leaves the rest at defaults --
/// visualizers (IVisualResponder implementations) read whichever fields
/// they care about, decoupled from which extractor produced them.
///
/// Why one shared struct instead of per-method structs (FFTResult,
/// ChromaResult, ...): it lets a single AudioFeatureFrame instance flow
/// through your whole pipeline per frame, with N extractors writing into
/// different parts of it, and it lets a responder mix features from
/// different methods (e.g. "scale ripple by RMS, color by chroma") without
/// needing to know about N different result types.
/// </summary>
public struct AudioFeatureFrame
{
    public double dspTime;

    // --- FFT / spectrum ---
    public float[] spectrumMagnitudes;   // linear magnitude per bin
    public float spectralCentroid;       // brightness, useful for mapping to color/size
    public float dominantFrequencyHz;    // peak bin frequency, useful for pitch sanity-check

    // --- STFT (if you keep it distinct from a single FFT frame) ---
    public float[,] stftMagnitudeFrames; // [time, freq], only if buffering multiple frames

    // --- Chroma ---
    public float[] chromaVector;         // length 12, one bin per pitch class (C, C#, D, ...)
    public int estimatedPitchClass;      // argmax of chromaVector, 0-11

    // --- Onset / beat ---
    public bool onsetDetected;           // true only on the frame an onset fires
    public float onsetStrength;          // continuous "how confident" value, for smoother visual mapping

    // --- RMS / envelope ---
    public float rmsValue;               // current RMS amplitude
    public float envelopeValue;          // smoothed/followed version of rmsValue (attack/release)

    /// <summary>Sample rate propagated from AudioBuffer, needed for bin-to-Hz conversion in responders.</summary>
    public int sampleRate;

    /// <summary>
    /// Symbolic pitch class (0=C, 1=D, 2=E, 3=F, 4=G, 5=A, 6=B) sourced
    /// directly from ToneBlock.blockNote -- ground-truth, not derived from
    /// audio analysis. Set by NoteAudioVisualizer from the ToneBlock
    /// component on the same GameObject.
    /// </summary>
    public int symbolicPitchClass;

    /// <summary>
    /// True only on the first frame after a note transitions from
    /// not-playing to playing. Responders should use this -- not their
    /// own target's internal state (e.g. ParticleSystem.isEmitting) -- to
    /// decide whether to (re)trigger, since a previous emission cycle may
    /// still be finishing when a new note starts.
    /// </summary>
    public bool noteJustStarted;

    /// <summary>Which note/clip this frame describes, propagated from AudioBuffer.sourceId.</summary>
    public string sourceId;

    /// <summary>
    /// Peak absolute sample amplitude of the CURRENT note's AudioClip (computed once via
    /// AudioClip.GetData() when the note starts, cached per-clip by NoteAudioVisualizer since the
    /// same clip gets replayed across many ToneBlocks). 0 if not yet computed.
    ///
    /// Exists so responders can normalize rmsValue/envelopeValue against "how loud is this note
    /// relative to its OWN true peak" instead of an absolute amplitude -- different instrument
    /// samples (guitar, marimba, bass, percussion) are recorded/mixed at very different absolute
    /// loudness, so raw envelopeValue alone makes guitar visuals balloon relative to marimba even
    /// though both are just "playing at full volume" for their own sample. Dividing by this peak
    /// converts the raw envelope into a 0-1 "how far into this note's own dynamic range are we"
    /// value that's comparable across every instrument.
    /// </summary>
    public float clipPeakAmplitude;

    /// <summary>
    /// Ground-truth instrument for the current note, cast from ToneGameManager.INSTRUMENTS
    /// (Marimba=0, Guitar=1, Percussion=2, Bass=3). Sourced the same way ToneBlock.Start() gets it
    /// (PhraseSequenceManager.GetCurrentPhrase().instrument) -- lets a responder apply a small manual
    /// per-instrument fine-tuning multiplier on top of the automatic clipPeakAmplitude normalization,
    /// for art-direction adjustments the automatic normalization alone doesn't fully capture (e.g. a
    /// sustained guitar note still "reads" bigger than a percussive marimba hit even at the same
    /// relative loudness, just because it stays near its peak for longer).
    /// </summary>
    public int instrumentIndex;

    /// <summary>
    /// Timestamp (seconds from clip start) after which this clip's audio never rises back above a
    /// small fraction of its own peak again -- i.e. where the real musical content ends and only a
    /// quiet decay/reverb/silence tail remains. Computed once per distinct clip (same offline
    /// AudioClip.GetData() pass as clipPeakAmplitude, cached) by scanning backward from the end of
    /// the clip. 0 if not yet computed or no such point was found (whole clip stays "live").
    ///
    /// Exists because a live per-frame envelope threshold can't reliably tell "the note is still
    /// genuinely decaying, just quietly" apart from "we're now in the clip's dead tail" -- both can
    /// sit at a similarly low envelope level for a while. Comparing clipPlaybackPositionSeconds
    /// against this ground-truth, precomputed-from-the-whole-clip timestamp instead is unambiguous.
    /// </summary>
    public float clipTailStartSeconds;

    /// <summary>Current playback position within the clip (AudioSource.timeSamples / clip frequency), for comparison against clipTailStartSeconds.</summary>
    public float clipPlaybackPositionSeconds;
}
