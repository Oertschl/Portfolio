using UnityEngine;

[RequireComponent(typeof(NoteAudioVisualizer))]
public class AudioDebugVisualizer : MonoBehaviour
{
    [Header("Debug Visualizer — Developer Only")]
    [SerializeField] private bool showDebugVisualizer = false;
    [SerializeField] private Vector3 offset = new Vector3(-1.5f, -4f, 0f);
    [SerializeField] private float displayWidth = 3f;
    [SerializeField] private float displayHeight = 3f;
    [SerializeField] private float lineWidth = 0.05f;
    [SerializeField] private string sortingLayerName = "Default";
    [SerializeField] private int sortingOrder = 200;

    private const int MAX_BARS = 100;

    private LineRenderer[] bars;
    private LineRenderer baselineLR;
    private GameObject visualizerRoot;

    private AudioFeatureFrame latestFrame;
    private NoteAudioVisualizer.ResponderMode latestMode;
    private bool isBuilt = false;

    public void UpdateFrame(in AudioFeatureFrame frame,
                            NoteAudioVisualizer.ResponderMode mode)
    {
        latestFrame = frame;
        latestMode  = mode;

        if (!isBuilt) return;
        if (showDebugVisualizer)
            RefreshBars();
    }

    private void Awake()
    {
        BuildLineRenderers();
    }

    private void Start()
    {
        // Set visibility on Start (not just Awake) so it's correct at play time
        SetVisible(showDebugVisualizer);
        Debug.Log($"[AudioDebugVisualizer] Start on '{gameObject.name}': " +
                  $"showDebugVisualizer={showDebugVisualizer}, " +
                  $"root={visualizerRoot != null}, bars={bars?.Length}, " +
                  $"rootActive={visualizerRoot?.activeSelf}, " +
                  $"position={transform.position + offset}");
    }

    /// <summary>Called by AudioDebugManager to toggle all instances at once.</summary>
    public void SetShowDebugVisualizer(bool value)
    {
        showDebugVisualizer = value;
        SetVisible(value);
    }

    private void OnValidate()
    {
        if (visualizerRoot != null)
            SetVisible(showDebugVisualizer);
    }

    private void Update()
    {
        if (!showDebugVisualizer || !isBuilt) return;

        Vector3 origin = transform.position + offset;

        // Always keep baseline visible so position is always apparent
        baselineLR.gameObject.SetActive(true);
        baselineLR.SetPosition(0, new Vector3(origin.x, origin.y, origin.z));
        baselineLR.SetPosition(1, new Vector3(origin.x + displayWidth,
                                               origin.y, origin.z));
        RefreshBars();
    }

    // ── Setup ────────────────────────────────────────────────────────

    private void BuildLineRenderers()
    {
        visualizerRoot = new GameObject("__AudioDebugViz");
        visualizerRoot.transform.SetParent(transform, false);
        visualizerRoot.transform.localPosition = Vector3.zero;

        baselineLR = CreateLR(visualizerRoot, "baseline",
                              new Color(1f, 1f, 1f, 0.4f), lineWidth * 0.5f);

        bars = new LineRenderer[MAX_BARS];
        for (int i = 0; i < MAX_BARS; i++)
            bars[i] = CreateLR(visualizerRoot, $"bar_{i}", Color.cyan, lineWidth);

        isBuilt = true;
    }

    private LineRenderer CreateLR(GameObject parent, string name,
                                   Color color, float width)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace  = true;
        lr.positionCount  = 2;
        lr.startWidth     = width;
        lr.endWidth       = width;
        lr.startColor     = color;
        lr.endColor       = color;
        lr.sortingLayerName = sortingLayerName;
        lr.sortingOrder     = sortingOrder;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        lr.numCapVertices    = 0;

        // Use Sprites/Default — works in both Built-in and URP
        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = color;
        lr.material = mat;

        // Place off-screen initially
        lr.SetPosition(0, Vector3.zero);
        lr.SetPosition(1, Vector3.zero);

        go.SetActive(false); // hidden until RefreshBars activates it
        return lr;
    }

    private void SetVisible(bool visible)
    {
        if (visualizerRoot == null) return;
        visualizerRoot.SetActive(visible);

        // Always show baseline when visible
        if (baselineLR != null)
            baselineLR.gameObject.SetActive(visible);

        // Hide all bars when turning off
        if (!visible && bars != null)
            foreach (var b in bars)
                if (b != null) b.gameObject.SetActive(false);
    }

    // ── Rendering ────────────────────────────────────────────────────

    private void RefreshBars()
    {
        if (!isBuilt) return;

        Vector3 origin = transform.position + offset;

        // Hide all bars then redraw active ones
        foreach (var b in bars)
            b.gameObject.SetActive(false);

        switch (latestMode)
        {
            case NoteAudioVisualizer.ResponderMode.RMS_Envelope:
                DrawSingleBar(origin, 0,
                    Mathf.Clamp01(latestFrame.envelopeValue),
                    new Color(0.2f, 0.8f, 1f));
                break;

            case NoteAudioVisualizer.ResponderMode.FFT_DominantFreq:
                DrawSpectrum(origin, latestFrame.spectrumMagnitudes, 100);
                break;

            case NoteAudioVisualizer.ResponderMode.Chroma_PitchClass:
                DrawChroma(origin, latestFrame.chromaVector,
                           latestFrame.estimatedPitchClass);
                break;

            case NoteAudioVisualizer.ResponderMode.Onset_Flux:
                DrawSingleBar(origin, 0,
                    Mathf.Clamp01(latestFrame.onsetStrength * 2f),
                    latestFrame.onsetDetected ? Color.white
                                              : new Color(0.2f, 1f, 0.4f));
                break;

            case NoteAudioVisualizer.ResponderMode.STFT_Bands:
                if (latestFrame.stftMagnitudeFrames != null)
                {
                    int nF = latestFrame.stftMagnitudeFrames.GetLength(0);
                    int nB = latestFrame.stftMagnitudeFrames.GetLength(1);
                    int show = Mathf.Min(nB, MAX_BARS);
                    float[] slice = new float[show];
                    for (int k = 0; k < show; k++)
                        slice[k] = latestFrame.stftMagnitudeFrames[nF - 1, k];
                    DrawSpectrum(origin, slice, show);
                }
                break;
        }
    }

    private void DrawSingleBar(Vector3 origin, int idx,
                                float value, Color color)
    {
        float h  = Mathf.Clamp01(value) * displayHeight;
        float cx = origin.x + displayWidth * 0.5f;

        var lr = bars[idx];
        lr.gameObject.SetActive(true);
        lr.startColor = color;
        lr.endColor   = color;
        lr.startWidth = displayWidth * 0.6f;
        lr.endWidth   = displayWidth * 0.6f;
        lr.SetPosition(0, new Vector3(cx, origin.y,        origin.z));
        lr.SetPosition(1, new Vector3(cx, origin.y + h,    origin.z));

        var mat = lr.material;
        mat.color = color;
    }

    private void DrawSpectrum(Vector3 origin, float[] mags, int maxBins)
    {
        if (mags == null || mags.Length == 0) return;
        int bins = Mathf.Min(mags.Length, Mathf.Min(maxBins, MAX_BARS));

        float peak = 0f;
        for (int k = 0; k < bins; k++)
            if (mags[k] > peak) peak = mags[k];
        if (peak < 0.0001f) return;

        float bw = displayWidth / bins;

        for (int k = 0; k < bins; k++)
        {
            float h     = (mags[k] / peak) * displayHeight;
            float cx    = origin.x + k * bw + bw * 0.5f;
            Color color = Color.HSVToRGB((float)k / bins * 0.75f, 0.9f, 1f);

            var lr = bars[k];
            lr.gameObject.SetActive(true);
            lr.startColor = color;
            lr.endColor   = color;
            lr.startWidth = bw * 0.9f;
            lr.endWidth   = bw * 0.9f;
            lr.SetPosition(0, new Vector3(cx, origin.y,     origin.z));
            lr.SetPosition(1, new Vector3(cx, origin.y + h, origin.z));

            var mat = lr.material;
            mat.color = color;
        }
    }

    private void DrawChroma(Vector3 origin, float[] chroma, int peakBin)
    {
        if (chroma == null || chroma.Length != 12) return;
        float bw = displayWidth / 12f;

        for (int i = 0; i < 12; i++)
        {
            float h  = chroma[i] * displayHeight;
            float cx = origin.x + i * bw + bw * 0.5f;
            Color color = (i == peakBin)
                ? new Color(1f, 0.95f, 0f)
                : Color.HSVToRGB((float)i / 12f, 0.85f, 1f);

            var lr = bars[i];
            lr.gameObject.SetActive(true);
            lr.startColor = color;
            lr.endColor   = color;
            lr.startWidth = bw * 0.85f;
            lr.endWidth   = bw * 0.85f;
            lr.SetPosition(0, new Vector3(cx, origin.y,     origin.z));
            lr.SetPosition(1, new Vector3(cx, origin.y + h, origin.z));

            var mat = lr.material;
            mat.color = color;
        }
    }
}
