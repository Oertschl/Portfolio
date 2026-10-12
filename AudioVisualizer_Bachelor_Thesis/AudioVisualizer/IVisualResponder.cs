using UnityEngine;

/// <summary>
/// Contract for something that turns an AudioFeatureFrame into a visible
/// change -- particle parameters, mesh deformation, shader properties,
/// color, whatever. Mirrors IAudioFeatureExtractor on the output side:
/// extractors fill in AudioFeatureFrame fields, responders read whichever
/// fields they care about and ignore the rest.
///
/// Multiple responders can run side by side (e.g. one drives a particle
/// system while another independently drives a mesh shader) since each
/// only reads from the frame -- it doesn't mutate or consume it. The
/// pipeline that calls these (NoteAudioVisualizer) can hold a list and
/// call Apply() on each, so going from one responder to several later is
/// a calling-code change, not an interface change.
/// </summary>
public interface IVisualResponder
{
    /// <summary>
    /// Human-readable name for logging/UI toggles/thesis write-up
    /// (e.g. "Particle ripple (RMS-driven)", "Mesh deform (FFT magnitude)").
    /// </summary>
    string MethodName { get; }

    /// <summary>
    /// Called once per frame while a note is playing (i.e. only when
    /// NoteAudioVisualizer's pipeline actually ran). Implementations
    /// should be defensive about missing data -- e.g. a responder reading
    /// frame.spectrumMagnitudes should null-check it, since not every
    /// extractor runs every frame (see IAudioFeatureExtractor.RequiredSampleCount).
    /// </summary>
    void Apply(in AudioFeatureFrame frame);

    /// <summary>
    /// Called when the note stops playing (isPlaying flips false), so the
    /// responder can reset to an idle/neutral visual state rather than
    /// freezing on its last audio-driven value. Without this, a
    /// mesh/particle system would stay deformed at whatever value the
    /// final frame produced.
    /// </summary>
    void Reset();
}
