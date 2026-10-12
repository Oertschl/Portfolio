/// <summary>
/// Contract for a single audio analysis method (FFT, STFT, chroma,
/// onset/beat detection, RMS/envelope, ...).
///
/// Design intent: every extractor takes the *same* AudioBuffer (raw PCM,
/// captured once via OnAudioFilterRead) and writes its results into a
/// shared AudioFeatureFrame. This is what lets you swap, combine, or
/// A/B-compare methods for your thesis without touching capture code or
/// visualization code -- only this layer changes.
///
/// Implementations should be pure/stateless where possible (no
/// MonoBehaviour, no GameObject references) so they can be unit-tested
/// and benchmarked in isolation -- relevant for the "performance" axis
/// of your research question.
/// </summary>
public interface IAudioFeatureExtractor
{
    /// <summary>
    /// Human-readable name for logging, UI toggles, and your evaluation
    /// write-up (e.g. "FFT (1024, Hann)", "Chroma (CQT-based)").
    /// </summary>
    string MethodName { get; }

    /// <summary>
    /// Analyze the given buffer and write results into `frame`.
    /// Implementations should only touch the fields they own (see
    /// AudioFeatureFrame's grouped comments) and leave others untouched,
    /// since multiple extractors may write into the same frame instance
    /// in sequence.
    /// </summary>
    void Process(in AudioBuffer buffer, ref AudioFeatureFrame frame);

    /// <summary>
    /// Optional: minimum number of samples this extractor needs to
    /// produce a meaningful result (e.g. FFT needs a full window size).
    /// Lets the pipeline skip calling Process() on buffers that are too
    /// short, e.g. right at note-start.
    /// </summary>
    int RequiredSampleCount { get; }
}
