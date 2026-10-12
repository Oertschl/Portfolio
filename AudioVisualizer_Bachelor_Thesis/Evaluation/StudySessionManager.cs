using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Persistent (DontDestroyOnLoad) study controller for the old-vs-new
/// visual system A/B comparison.
///
/// Confirmed from the actual project (ToneSortingLvlGenerator,
/// PhraseSequenceManager): there is only ONE MusicWave_Sorting particle
/// system per ToneBlock. "Old" and "new" are two different configurations
/// of that SAME particle system (a few scalar MainModule fields --
/// duration, startSpeed, startSize, startColor, simulationSpeed) plus
/// whether NoteAudioVisualizer is enabled to actively modulate it on top
/// of that baseline. There is no second GameObject or prefab to toggle.
///
/// NAVIGATION IS HUB-BASED, not linear: confirmed the player picks each
/// level individually from the inventory screen and returns there after
/// solving, rather than levels auto-chaining to the next one. Because of
/// that, this does NOT try to auto-detect "level completed" via
/// SolutionChecker (an earlier version did -- dropped because there's no
/// reliable signal for "the participant is done with all levels" in a
/// hub-based flow). Instead:
///
///   - RegisterLevelStart() is called once per level PICK (wired into
///     ToneGameManager.StartNewPuzzleSequence(), which fires every time a
///     level icon is clicked in the inventory, hub-based or not). It
///     alternates the condition each time it's called, counterbalanced by
///     participant ID parity so the starting condition is always exactly
///     even across participants.
///   - OnSceneLoaded() just applies whatever condition RegisterLevelStart()
///     most recently determined to every ToneBlock in the newly loaded
///     scene -- it doesn't do any counting itself.
///   - There is no automatic "session complete" detection. Wire
///     TakeSurveyButtonPressed() to a button on the inventory/home screen
///     instead -- the participant (or you) presses it once they've played
///     through however many levels you intend, and it writes the session
///     log, submits it remotely if configured, and opens the pre-filled
///     survey in one tap.
///
/// ASSUMPTION -- please confirm: MusicWave_Sorting is reachable via
/// GetComponentInChildren&lt;ParticleSystem&gt;() from the ToneBlock's own
/// GameObject, and NoteAudioVisualizer sits on that same GameObject
/// (reachable via GetComponent). If either lives somewhere else in the
/// hierarchy, tell me and I'll adjust how they're found.
/// </summary>
public class StudySessionManager : MonoBehaviour
{
    public static StudySessionManager Instance { get; private set; }

    [Serializable]
    public struct ParticlePreset
    {
        public float duration;
        public float startSpeed;
        public float startSize;
        public Color startColor;
        public float simulationSpeed;
    }

    [Header("Presets -- from your two MusicWave_Sorting screenshots")]
    [SerializeField]
    private ParticlePreset oldPreset = new ParticlePreset
    {
        duration = 1f, startSpeed = 2f, startSize = 5f, startColor = Color.black, simulationSpeed = 1f
    };
    [SerializeField]
    private ParticlePreset newPreset = new ParticlePreset
    {
        duration = 3f, startSpeed = 11f, startSize = 1f, startColor = Color.white, simulationSpeed = 3f
    };

    [Header("Session length (hard gate)")]
    [Tooltip("How many levels a participant must start before the Take Survey button becomes " +
             "visible. The button is hidden entirely (GameObject.SetActive(false)) until this " +
             "many levels have been started, and a direct press before then is also rejected as " +
             "a safety net -- so this blocks both accidental early taps and any path that finds " +
             "the button despite it being hidden.")]
    [SerializeField] private int targetLevelCount = 4;

    [Header("Participant")]
    [Tooltip("Leave at 0 to auto-generate a random ID when a session starts -- the participant " +
             "never has to think one up themselves. Only set this manually (via SetParticipantId()) " +
             "if you want guaranteed exact 50/50 counterbalancing via a sequential 1, 2, 3... " +
             "assignment, e.g. for in-person sessions where you control the order yourself.")]
    [SerializeField] private int participantId = 0;

    [Tooltip("Optional -- purely for your own reference during in-person sessions. Put this on a " +
             "Canvas that's a CHILD of this same GameObject so it survives scene loads along with it.")]
    [SerializeField] private TMP_Text participantIdDisplayText;

    [Header("End-of-session screen (optional -- TextMeshPro)")]
    [SerializeField] private GameObject endScreenPanel;
    [SerializeField] private TMP_Text endScreenText;

    [Header("Survey")]
    [Tooltip("Pre-filled Google Form link (Form's three-dot menu -> 'Get pre-filled link'). " +
             "Whatever value you used for the Participant ID field in that pre-filled response gets " +
             "swapped out for the real participantId when the survey opens -- the participant never " +
             "types or copies anything.")]
    [SerializeField]
    private string surveyUrlTemplate =
        "https://docs.google.com/forms/d/e/1FAIpQLSfD3oGNmN0ANwXlnRXYiMUn9kKb4sDflCAZJN_62VBBBN1sxQ/viewform?usp=pp_url&entry.282899285={PARTICIPANT_ID}";
    [Tooltip("Name of the 'Take Survey' button GameObject (exactly as it appears in the Hierarchy), " +
             "reachable from the inventory/home screen. One tap: logs the session, submits it " +
             "remotely if configured, and opens the survey.\n\n" +
             "This is looked up by NAME and re-wired every time the MainMenu scene (re)loads, rather " +
             "than using a fixed Inspector reference -- because only this manager survives scene " +
             "loads (DontDestroyOnLoad); the button itself is destroyed and recreated as a fresh " +
             "instance every time the player returns to the menu, so a manually-set reference (or an " +
             "OnClick() entry pointing at this component) would go stale (\"Missing (Object)\") after " +
             "the very next reload.")]
    [SerializeField] private string takeSurveyButtonName = "TakeSurvey";

    [Header("Remote submission (optional -- leave empty to skip)")]
    [Tooltip("Google Apps Script Web App URL (see StudySessionReceiver.gs). Needed for remote " +
             "participants you never get physical/ADB access to -- the local CSV alone can't be " +
             "retrieved from their device.")]
    [SerializeField] private string submissionEndpointUrl;
    [SerializeField] private TMP_Text submissionStatusText;

    public UnityEvent onSessionComplete;

    private bool oldConditionFirst;
    private int levelsStarted = 0;
    private bool sessionActive = false;
    private bool currentLevelIsOld;
    private readonly List<double> levelStartTimes = new List<double>();
    private readonly List<string> conditionPerLevel = new List<string>(); // "Old"/"New" per level, for the log only

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void Start()
    {
        // Covers the very first scene active when Play begins -- OnSceneLoaded() handles every
        // reload after that. Safe to call twice for the same scene (RewireTakeSurveyButton just
        // re-establishes a single listener, doesn't stack duplicates).
        RewireTakeSurveyButton(SceneManager.GetActiveScene());
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    public int ParticipantId => participantId;

    /// <summary>Only call this if you want to assign IDs yourself (e.g. sequential 1, 2, 3... for
    /// guaranteed exact counterbalancing during in-person sessions). Otherwise leave participantId at
    /// 0 and the session will auto-generate one and display it for you.</summary>
    public void SetParticipantId(int id) => participantId = id;

    /// <summary>
    /// Safe to call every time a level is picked (it's idempotent past the first call) -- sets up
    /// participant ID and counterbalancing exactly once per session. Call RegisterLevelStart()
    /// alongside it every time, which does the actual per-level bookkeeping.
    /// </summary>
    public void BeginSession()
    {
        if (sessionActive) return; // already running -- don't reset progress on a later level pick

        if (participantId == 0)
        {
            // Random 6-digit ID -- large enough that two remote participants
            // colliding is very unlikely, and still short enough to
            // comfortably read off screen if you want to note it down.
            participantId = UnityEngine.Random.Range(100000, 999999);
        }

        oldConditionFirst = (participantId % 2 == 0);
        levelsStarted = 0;
        sessionActive = true;
        levelStartTimes.Clear();
        conditionPerLevel.Clear();

        UpdateParticipantIdDisplay();

        Debug.Log($"[StudySessionManager] Participant {participantId}: starting condition = " +
                  $"{(oldConditionFirst ? "Old" : "New")}. Alternating every level.");
    }

    /// <summary>
    /// Call every time a level is started (wired into ToneGameManager.StartNewPuzzleSequence(), which
    /// fires on every inventory click regardless of hub navigation). Determines and stores the
    /// condition for the level about to load; OnSceneLoaded() applies it once the blocks exist.
    /// </summary>
    public void RegisterLevelStart()
    {
        if (!sessionActive)
        {
            Debug.LogWarning("[StudySessionManager] RegisterLevelStart() called before BeginSession() -- " +
                              "call BeginSession() first (it's idempotent, safe to call every time too).");
            return;
        }

        bool isEvenLevel = (levelsStarted % 2 == 0);
        currentLevelIsOld = oldConditionFirst ? isEvenLevel : !isEvenLevel;
        levelsStarted++;

        conditionPerLevel.Add(currentLevelIsOld ? "Old" : "New");
        levelStartTimes.Add(Time.realtimeSinceStartupAsDouble);

        Debug.Log($"[StudySessionManager] Level #{levelsStarted} started, condition = " +
                  $"{(currentLevelIsOld ? "Old" : "New")}.");
    }

    private void UpdateParticipantIdDisplay()
    {
        if (participantIdDisplayText != null)
            participantIdDisplayText.text = $"Participant ID: {participantId}";
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RewireTakeSurveyButton(scene);

        if (!sessionActive) return;

        ParticlePreset preset = currentLevelIsOld ? oldPreset : newPreset;

        ToneBlock[] blocks = FindObjectsOfType<ToneBlock>();
        if (blocks.Length == 0) return; // not a level scene (e.g. inventory/home) -- nothing to do

        foreach (var block in blocks)
        {
            ApplyPresetToBlock(block, preset, useNewSystem: !currentLevelIsOld);
        }

        Debug.Log($"[StudySessionManager] Scene loaded, applied condition = " +
                  $"{(currentLevelIsOld ? "Old" : "New")} to {blocks.Length} block(s).");
    }

    /// <summary>
    /// Finds the "Take Survey" button by name in the scene that just loaded and (re)wires its click
    /// listener. Called on every scene load (see OnSceneLoaded/Start above) so this never depends on
    /// a reference that's guaranteed to go stale once the button's own scene reloads -- if the button
    /// isn't present in this particular scene (e.g. a level/puzzle scene rather than the menu),
    /// nothing happens.
    /// </summary>
    private void RewireTakeSurveyButton(Scene scene)
    {
        if (string.IsNullOrEmpty(takeSurveyButtonName)) return;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform found = FindDeep(root.transform, takeSurveyButtonName);
            if (found == null) continue;

            Button button = found.GetComponent<Button>();
            if (button == null) continue;

            button.onClick.RemoveListener(TakeSurveyButtonPressed); // avoid stacking duplicates
            button.onClick.AddListener(TakeSurveyButtonPressed);
            UpdateTakeSurveyButtonInteractable(button);
            return;
        }
    }

    /// <summary>
    /// Hides the Take Survey button entirely until targetLevelCount levels have been started --
    /// participants can't see it, let alone tap it, before then. Called every time the button is
    /// (re)wired -- i.e. every time the participant lands back on the home/inventory screen -- so
    /// visibility always reflects current progress without needing its own Update() loop.
    ///
    /// SetActive(false) doesn't stop FindDeep() from finding it again on a later scene load --
    /// Transform traversal doesn't care about activeSelf -- so this is safe to toggle back and
    /// forth freely as levelsStarted changes.
    /// </summary>
    private void UpdateTakeSurveyButtonInteractable(Button button)
    {
        bool enoughLevelsPlayed = levelsStarted >= targetLevelCount;
        button.gameObject.SetActive(enoughLevelsPlayed);
    }

    private static Transform FindDeep(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        foreach (Transform child in parent)
        {
            Transform result = FindDeep(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private void ApplyPresetToBlock(ToneBlock block, ParticlePreset preset, bool useNewSystem)
    {
        ParticleSystem ps = block.GetComponentInChildren<ParticleSystem>(true);
        if (ps != null)
        {
            var main = ps.main;
            main.duration = preset.duration;
            main.startSpeed = preset.startSpeed;
            main.startSize = preset.startSize;
            main.startColor = preset.startColor;
            main.simulationSpeed = preset.simulationSpeed;
        }
        else
        {
            Debug.LogWarning($"[StudySessionManager] No ParticleSystem found under '{block.gameObject.name}'.");
        }

        NoteAudioVisualizer visualizer = block.GetComponent<NoteAudioVisualizer>();
        if (visualizer != null)
        {
            visualizer.enabled = useNewSystem;
        }
        else
        {
            Debug.LogWarning($"[StudySessionManager] No NoteAudioVisualizer found on '{block.gameObject.name}'.");
        }
    }

    /// <summary>
    /// Wire to a "Take Survey" button reachable from the inventory/home screen. One tap: writes the
    /// session log, submits it remotely if configured, and opens the pre-filled survey.
    /// </summary>
    public void TakeSurveyButtonPressed()
    {
        if (levelsStarted < targetLevelCount)
        {
            // Belt-and-suspenders: the button should already be hidden at this point
            // (see UpdateTakeSurveyButtonInteractable), so reaching here means either that state
            // fell out of sync or something else invoked this method directly (e.g. FindDeep
            // locating the inactive button and calling this anyway). Either way, block it
            // rather than silently proceed.
            Debug.LogWarning($"[StudySessionManager] Survey requested after only {levelsStarted}/" +
                              $"{targetLevelCount} levels -- blocked.");
            if (submissionStatusText != null)
                submissionStatusText.text = $"Please play at least {targetLevelCount} levels before " +
                                             $"taking the survey ({levelsStarted}/{targetLevelCount} so far).";
            return;
        }

        sessionActive = false;

        if (endScreenText != null)
            endScreenText.text = "Thanks! Opening the survey now.";
        if (endScreenPanel != null)
            endScreenPanel.SetActive(true);

        WriteSessionLog();
        onSessionComplete?.Invoke();

        if (!string.IsNullOrEmpty(submissionEndpointUrl))
            StartCoroutine(SubmitSessionDataOverNetwork());
        else if (submissionStatusText != null)
            submissionStatusText.text = "Saved locally on this device only (no submission endpoint configured).";

        OpenSurvey();
    }

    /// <summary>Opens the pre-filled survey link with the real participant ID substituted in --
    /// nothing for the participant to type or copy.</summary>
    public void OpenSurvey()
    {
        if (string.IsNullOrEmpty(surveyUrlTemplate))
        {
            Debug.LogWarning("[StudySessionManager] surveyUrlTemplate not set -- can't open survey.");
            return;
        }
        string url = surveyUrlTemplate.Replace("{PARTICIPANT_ID}", participantId.ToString());
        Application.OpenURL(url);
        Debug.Log($"[StudySessionManager] Opened survey for participant {participantId}.");
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [Header("Debug (dev testing only -- compiled out of release builds, off by default even in dev builds)")]
    [Tooltip("Lets you cycle Old/New visuals on demand with a key press, independent of the study's " +
             "per-level condition assignment. For your own QA only -- never used by participants. " +
             "Compiled out entirely outside the Editor/Development Build, and off unless you tick this.")]
    [SerializeField] private bool enableDebugToggle = false;
    [SerializeField] private KeyCode debugToggleKey = KeyCode.F9;
    private bool debugCurrentIsOld = true;

    private void Update()
    {
        if (!enableDebugToggle) return;
        if (Input.GetKeyDown(debugToggleKey))
            DebugToggleVisualPreset();
    }

    /// <summary>
    /// Dev-only: reapplies presets to every ToneBlock currently in the scene, flipping Old/New each
    /// press. Deliberately does NOT touch session bookkeeping (levelsStarted, conditionPerLevel,
    /// currentLevelIsOld, sessionActive) -- it's a visual sanity-check tool, safe to mash during your
    /// own testing without corrupting a real study session's log.
    /// </summary>
    private void DebugToggleVisualPreset()
    {
        debugCurrentIsOld = !debugCurrentIsOld;
        ParticlePreset preset = debugCurrentIsOld ? oldPreset : newPreset;

        ToneBlock[] blocks = FindObjectsOfType<ToneBlock>();
        if (blocks.Length == 0)
        {
            Debug.Log("[StudySessionManager] Debug toggle: no ToneBlocks in the current scene.");
            return;
        }

        foreach (var block in blocks)
            ApplyPresetToBlock(block, preset, useNewSystem: !debugCurrentIsOld);

        Debug.Log($"[StudySessionManager] Debug toggle -> {(debugCurrentIsOld ? "Old" : "New")} " +
                  $"applied to {blocks.Length} block(s).");
    }
#endif

    // ── Local + remote logging ───────────────────────────────────────────
    // One row per participant: starting condition, the full old/new
    // sequence across all levels played (for your own records -- never
    // shown to the participant).
    private static readonly object fileLock = new object();
    private static string logPath;

    private (string startCondition, string sequence) BuildLogFields()
    {
        string startCondition = oldConditionFirst ? "Old" : "New";
        string sequence = string.Join("-", conditionPerLevel);
        return (startCondition, sequence);
    }

    private void WriteSessionLog()
    {
        if (logPath == null)
            logPath = Path.Combine(Application.persistentDataPath, "study_sessions.csv");

        bool needsHeader = !File.Exists(logPath);
        var fields = BuildLogFields();

        lock (fileLock)
        {
            try
            {
                using (var w = new StreamWriter(logPath, append: true))
                {
                    if (needsHeader)
                        w.WriteLine("participantId,startCondition,conditionSequence,levelsPlayed,timestamp");

                    w.WriteLine($"{participantId},{fields.startCondition},{fields.sequence}," +
                                $"{levelsStarted},{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                }
                Debug.Log($"[StudySessionManager] Session log written -> {logPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[StudySessionManager] Failed to write session log: {e.Message}");
            }
        }
    }

    private IEnumerator SubmitSessionDataOverNetwork()
    {
        var fields = BuildLogFields();

        var form = new WWWForm();
        form.AddField("participantId", participantId);
        form.AddField("startCondition", fields.startCondition);
        form.AddField("conditionSequence", fields.sequence);
        form.AddField("levelsPlayed", levelsStarted);
        form.AddField("timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        if (submissionStatusText != null) submissionStatusText.text = "Sending session data...";

        using (var req = UnityWebRequest.Post(submissionEndpointUrl, form))
        {
            req.timeout = 15; // seconds -- don't hang forever on a bad connection
            yield return req.SendWebRequest();

            bool ok = req.result == UnityWebRequest.Result.Success;
            if (submissionStatusText != null)
                submissionStatusText.text = ok
                    ? "Data sent. Thank you!"
                    : "Could not connect -- data saved locally only. Please let the researcher know.";

            if (ok)
                Debug.Log("[StudySessionManager] Session data submitted successfully.");
            else
                Debug.LogWarning($"[StudySessionManager] Remote submission failed ({req.error}). " +
                                  "Data is still safe in the local CSV on this device.");
        }
    }
}