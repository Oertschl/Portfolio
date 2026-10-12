using UnityEngine;

/// <summary>
/// Raw audio data snapshot, captured once per audio-thread callback (or per
/// polled frame), and handed identically to every IAudioFeatureExtractor.
///
/// Keeping this struct the single source of truth is what makes your
/// extractors comparable: FFT, chroma, onset, and RMS all read the exact
/// same samples, same window, same timestamp. Any difference in their
/// output is then attributable to the *method*, not to the input data.
/// </summary>
public struct AudioBuffer
{
    /// <summary>Raw PCM samples, mono-summed, range roughly [-1, 1].</summary>
    public float[] samples;

    /// <summary>Sample rate of the audio system (e.g. 44100).</summary>
    public int sampleRate;

    /// <summary>
    /// Unity's audio DSP clock at the moment this buffer was captured.
    /// Use this -- not Time.time -- to align visuals with audio, since
    /// dspTime is on the audio thread's own clock.
    /// </summary>
    public double dspTime;

    /// <summary>Which note/clip this buffer belongs to, for logging/eval.</summary>
    public string sourceId;
}
