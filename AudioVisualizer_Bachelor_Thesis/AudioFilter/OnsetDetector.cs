using UnityEngine;

/// <summary>
/// Beat/onset detector using spectral flux.
///
/// Onset detection finds the moment a new note or sound begins.
/// The standard approach: compute the "spectral flux" -- the sum of
/// positive magnitude differences between consecutive STFT frames.
/// When a new note strikes, energy appears suddenly across many
/// frequency bins simultaneously → large positive flux → onset.
///
/// Why this works well for marimba:
///   Marimba strikes have extremely sharp, clean transients. The
///   spectral flux jumps from near-zero (silence) to a large value
///   in a single hop interval, making detection reliable with a simple
///   threshold. More complex methods (complex-domain flux, phase-based)
///   are needed for softer onsets (bowed strings, vocals) but are
///   unnecessary here.
///
/// Writes into AudioFeatureFrame:
///   onsetDetected  -- true only on the frame an onset fires
///   onsetStrength  -- continuous flux value, useful for visual mapping
///                     even when no discrete onset threshold is crossed
///
/// Self-contained: runs its own STFT internally from the raw AudioBuffer.
/// </summary>
public class OnsetDetector : IAudioFeatureExtractor
{
    public string MethodName => $"Onset detection (spectral flux, threshold={threshold:F3})";
    public int RequiredSampleCount => windowSize;

    private readonly int windowSize;
    private readonly int hopSize;
    private readonly float threshold;
    private readonly float[] hannWindow;
    private readonly float[] real;
    private readonly float[] imag;

    // Previous frame's magnitude spectrum for flux computation
    private readonly float[] prevMagnitudes;
    private readonly float[] currMagnitudes;

    // Ring buffer for hop-based framing (same as STFT)
    private readonly float[] sampleRingBuffer;
    private int ringWritePos;
    private int samplesSinceLastFrame;

    // Smoothed onset strength for continuous visual mapping
    private float smoothedOnsetStrength;
    private const float ONSET_SMOOTH_DECAY = 0.85f; // per-frame decay

    // Cooldown: suppress repeated detections within this many frames
    // (avoids double-triggering on a single attack transient)
    private int cooldownFrames;
    private const int ONSET_COOLDOWN = 10;

    public OnsetDetector(int windowSize = 1024, int hopSize = 512, float threshold = 0.05f)
    {
        this.windowSize = Mathf.NextPowerOfTwo(windowSize);
        this.hopSize    = hopSize;
        this.threshold  = threshold;

        hannWindow       = new float[this.windowSize];
        real             = new float[this.windowSize];
        imag             = new float[this.windowSize];
        prevMagnitudes   = new float[this.windowSize / 2];
        currMagnitudes   = new float[this.windowSize / 2];
        sampleRingBuffer = new float[this.windowSize];

        for (int n = 0; n < this.windowSize; n++)
            hannWindow[n] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * n / (this.windowSize - 1)));
    }

    public void Process(in AudioBuffer buffer, ref AudioFeatureFrame frame)
    {
        if (buffer.samples.Length < RequiredSampleCount)
            return;

        bool onsetThisBuffer = false;
        float maxFluxThisBuffer = 0f;

        // Process samples hop-by-hop, same pattern as STFTExtractor
        foreach (float sample in buffer.samples)
        {
            sampleRingBuffer[ringWritePos % windowSize] = sample;
            ringWritePos++;
            samplesSinceLastFrame++;

            if (samplesSinceLastFrame >= hopSize)
            {
                float flux = ComputeHopFlux();
                if (flux > maxFluxThisBuffer) maxFluxThisBuffer = flux;

                if (cooldownFrames > 0)
                {
                    cooldownFrames--;
                }
                else if (flux > threshold)
                {
                    onsetThisBuffer = true;
                    cooldownFrames  = ONSET_COOLDOWN;
                }

                samplesSinceLastFrame = 0;
            }
        }

        // Smooth onset strength for continuous visual mapping --
        // the raw flux is bursty; smoothing gives a cleaner envelope
        // that responders can map to gradual visual parameters.
        smoothedOnsetStrength = onsetThisBuffer
            ? Mathf.Max(smoothedOnsetStrength, maxFluxThisBuffer)
            : smoothedOnsetStrength * ONSET_SMOOTH_DECAY;

        frame.onsetDetected  = onsetThisBuffer;
        frame.onsetStrength  = smoothedOnsetStrength;
        frame.dspTime        = buffer.dspTime;
        frame.sourceId       = buffer.sourceId;
    }

    private float ComputeHopFlux()
    {
        // Extract windowed frame from ring buffer into real[]
        for (int i = 0; i < windowSize; i++)
        {
            int idx = (ringWritePos - windowSize + i + windowSize) % windowSize;
            real[i] = sampleRingBuffer[idx] * hannWindow[i];
            imag[i] = 0f;
        }
        FFT(real, imag, windowSize);

        int numBins = windowSize / 2;
        float norm  = 1f / windowSize;
        float flux  = 0f;

        for (int k = 0; k < numBins; k++)
        {
            float mag = Mathf.Sqrt(real[k] * real[k] + imag[k] * imag[k]) * norm * 2f;
            currMagnitudes[k] = mag;

            // Half-wave rectified flux: only count positive differences.
            // Negative differences (energy decreasing) are ignored --
            // we only want to detect energy *appearing*, not disappearing.
            float diff = mag - prevMagnitudes[k];
            if (diff > 0f) flux += diff;
        }

        // Swap buffers: current becomes previous for next hop
        System.Array.Copy(currMagnitudes, prevMagnitudes, numBins);

        return flux;
    }

    private static void FFT(float[] re, float[] im, int n)
    {
        int bits = (int)Mathf.Log(n, 2);
        for (int i = 1; i < n; i++)
        {
            int rev = BitReverse(i, bits);
            if (rev > i)
            {
                float tmp = re[i]; re[i] = re[rev]; re[rev] = tmp;
                tmp = im[i]; im[i] = im[rev]; im[rev] = tmp;
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = -2f * Mathf.PI / len;
            float wRe = Mathf.Cos(angle), wIm = Mathf.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1f, curIm = 0f;
                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j, v = i + j + len / 2;
                    float tRe = curRe * re[v] - curIm * im[v];
                    float tIm = curRe * im[v] + curIm * re[v];
                    re[v] = re[u] - tRe; im[v] = im[u] - tIm;
                    re[u] = re[u] + tRe; im[u] = im[u] + tIm;
                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    private static int BitReverse(int n, int bits)
    {
        int rev = 0;
        for (int i = 0; i < bits; i++) { rev = (rev << 1) | (n & 1); n >>= 1; }
        return rev;
    }
}
