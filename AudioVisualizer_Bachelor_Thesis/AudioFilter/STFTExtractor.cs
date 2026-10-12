using UnityEngine;

/// <summary>
/// Short-Time Fourier Transform extractor.
///
/// Where FFTExtractor computes one spectrum from the most recent window
/// of samples, STFTExtractor maintains a rolling buffer and computes
/// multiple overlapping FFT frames over time, producing a 2D
/// time × frequency magnitude array (a spectrogram).
///
/// Why STFT rather than just FFT:
///   A single FFT frame is a snapshot -- it captures only what's happening
///   in the last ~46 ms. STFT gives you temporal context: how the spectrum
///   evolved over the past N frames. This is needed for onset detection
///   (which looks for changes between frames) and for stable pitch analysis
///   (averaging chroma over several frames reduces noise from transients).
///
/// Writes into AudioFeatureFrame:
///   stftMagnitudeFrames[numFrames, numBins]  -- spectrogram matrix
///                                               (time × frequency)
///   The most recent frame is at index [numFrames-1, :].
///
/// Parameters:
///   windowSize   -- FFT window size in samples (must be power of 2)
///   hopSize      -- samples between consecutive frames (overlap = window-hop)
///   numFrames    -- how many frames to retain in the rolling buffer
/// </summary>
public class STFTExtractor : IAudioFeatureExtractor
{
    public string MethodName => $"STFT (window={windowSize}, hop={hopSize}, frames={numFrames})";
    public int RequiredSampleCount => windowSize;

    private readonly int windowSize;
    private readonly int hopSize;
    private readonly int numFrames;
    private readonly int numBins;
    private readonly float[] hannWindow;
    private readonly float[] real;
    private readonly float[] imag;

    // Rolling spectrogram buffer -- oldest frame at [0], newest at [numFrames-1]
    private readonly float[,] spectrogram;

    // Internal rolling sample buffer for hop-based framing
    private readonly float[] sampleRingBuffer;
    private int ringWritePos;
    private int samplesSinceLastFrame;

    public STFTExtractor(int windowSize = 1024, int hopSize = 512, int numFrames = 8)
    {
        this.windowSize = Mathf.NextPowerOfTwo(windowSize);
        this.hopSize    = hopSize;
        this.numFrames  = numFrames;
        this.numBins    = this.windowSize / 2;

        hannWindow     = new float[this.windowSize];
        real           = new float[this.windowSize];
        imag           = new float[this.windowSize];
        spectrogram    = new float[numFrames, numBins];
        sampleRingBuffer = new float[this.windowSize];

        for (int n = 0; n < this.windowSize; n++)
            hannWindow[n] = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * n / (this.windowSize - 1)));
    }

    public void Process(in AudioBuffer buffer, ref AudioFeatureFrame frame)
    {
        if (buffer.samples.Length < RequiredSampleCount)
            return;

        float bestHopMaxMag = 0f;

        // Feed incoming samples into the ring buffer and compute a new
        // STFT frame every `hopSize` samples.
        foreach (float sample in buffer.samples)
        {
            sampleRingBuffer[ringWritePos % windowSize] = sample;
            ringWritePos++;
            samplesSinceLastFrame++;

            if (samplesSinceLastFrame >= hopSize)
            {
                ComputeFrame(buffer.sampleRate);
                samplesSinceLastFrame = 0;

                // Track which hop produced the most energy
                float hopMax = 0f;
                for (int k = 0; k < numBins; k++)
                    if (spectrogram[numFrames - 1, k] > hopMax)
                        hopMax = spectrogram[numFrames - 1, k];

                if (hopMax > bestHopMaxMag)
                {
                    bestHopMaxMag = hopMax;
                    // Copy this hop's spectrum into the output snapshot
                    if (outputSnapshot == null)
                        outputSnapshot = new float[numFrames, numBins];
                    for (int t = 0; t < numFrames; t++)
                        for (int k = 0; k < numBins; k++)
                            outputSnapshot[t, k] = spectrogram[t, k];
                }
            }
        }

        // Assign the snapshot (most energetic hop this buffer) to frame
        frame.stftMagnitudeFrames = outputSnapshot ?? spectrogram;
        frame.dspTime  = buffer.dspTime;
        frame.sourceId = buffer.sourceId;
    }

    // Snapshot of spectrogram at its most energetic hop this Process() call.
    // Separate from `spectrogram` so the logger always sees a stable copy
    // rather than whatever the last hop wrote (which may be near-silence).
    private float[,] outputSnapshot;

    private int computeFrameCallCount = 0;

    private void ComputeFrame(int sampleRate)
    {
        // Extract the most recent `windowSize` samples from the ring buffer
        for (int i = 0; i < windowSize; i++)
        {
            int idx = (ringWritePos - windowSize + i + windowSize) % windowSize;
            real[i] = sampleRingBuffer[idx] * hannWindow[i];
            imag[i] = 0f;
        }

        FFT(real, imag, windowSize);

        // Shift old frames back and write new frame at [numFrames-1]
        for (int t = 0; t < numFrames - 1; t++)
            for (int k = 0; k < numBins; k++)
                spectrogram[t, k] = spectrogram[t + 1, k];

        float norm = 1f / windowSize;
        for (int k = 0; k < numBins; k++)
        {
            float mag = Mathf.Sqrt(real[k] * real[k] + imag[k] * imag[k]);
            spectrogram[numFrames - 1, k] = mag * norm * 2f;
        }
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