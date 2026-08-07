using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public sealed class WorldSimulationPanel : MonoBehaviour
{
    public static bool IsPaused { get; private set; }
    public static WorldSimulationPanel Instance { get; private set; }

    [Header("Panel Toggle")]
    [Tooltip("Tab button that shows and hides the settings panel.")]
    [SerializeField] private Button tabButton;
    [Tooltip("CanvasGroup of the settings panel. Alpha is flipped by the tab button.")]
    [SerializeField] private CanvasGroup panelGroup;

    [Header("Status")]
    [Tooltip("One-line status: simulation state, world time, LLM key state.")]
    [SerializeField] private TMP_Text statusText;

    [Header("API Key")]
    [Tooltip("Parent GameObject that holds the API key fields and buttons. Hidden until the admin password unlocks it.")]
    [SerializeField] private GameObject apiKeySection;
    [Tooltip("Masked input for the main LLM API key (only visible after admin unlock).")]
    [SerializeField] private TMP_InputField apiKeyField;
    [Tooltip("Optional: override the main provider's model. Empty keeps the configured one.")]
    [SerializeField] private TMP_InputField modelField;
    [Tooltip("Optional second provider (e.g. Gemini). Empty key keeps the environment-variable key.")]
    [SerializeField] private TMP_InputField secondaryApiKeyField;
    [Tooltip("Optional: override the second provider's model. Empty keeps the configured one.")]
    [SerializeField] private TMP_InputField secondaryModelField;
    [Tooltip("Environment variable name of the second provider (default: GEMINI_API_KEY).")]
    [SerializeField] private string secondaryApiKeyEnvironmentVariable = "GEMINI_API_KEY";
    [SerializeField] private Button applyKeyButton;
    [SerializeField] private Button clearKeyButton;
    [SerializeField] private Button testKeyButton;
    [Tooltip("Password required to reveal and edit the API key. Leave empty to keep the panel open.")]
    [SerializeField] private string adminPassword;
    [Tooltip("Masked input where the admin password is typed.")]
    [SerializeField] private TMP_InputField adminPasswordField;
    [SerializeField] private Button adminUnlockButton;
    [Tooltip("Optional text that reports a wrong password.")]
    [SerializeField] private TMP_Text adminStatusText;

    [Header("Simulation")]
    [Tooltip("Single toggle button: starts (play) and pauses (freeze) the simulation. Shows the play sprite while frozen, the pause sprite while running.")]
    [SerializeField] private Button startStopButton;
    [SerializeField] private Image startStopImage;
    [SerializeField] private Sprite startSprite;
    [SerializeField] private Sprite stopSprite;
    [Tooltip("Restart button. First click arms a confirm state, second click wipes the world.")]
    [SerializeField] private Button restartButton;
    [SerializeField] private TMP_Text restartButtonLabel;

    private float restartConfirmUntil;
    private float nextStatusRefreshTime;
    private readonly Dictionary<Animator, float> animatorSpeeds = new();
    private Animator[] sceneAnimators = System.Array.Empty<Animator>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        Wire(applyKeyButton, ApplyApiKey);
        Wire(clearKeyButton, ClearApiKey);
        Wire(testKeyButton, TestConnection);
        Wire(tabButton, TogglePanel);
        Wire(startStopButton, ToggleStartStop);
        Wire(restartButton, OnRestartClick);
        Wire(adminUnlockButton, TryUnlockAdmin);

        IsPaused = true;
        ApplyFreezeToAnimators(true);
        ApplyAdminLock();
        SetPanelVisible(false);

        if (apiKeyField == null)
            Debug.LogError("[World control] Api Key Field is not assigned on " + name + ".", this);
        if (apiKeyField != null)
            apiKeyField.contentType = TMP_InputField.ContentType.Password;
        if (adminPasswordField != null)
            adminPasswordField.contentType = TMP_InputField.ContentType.Password;
        RefreshStatus();
    }

    private void Update()
    {
        if (Time.unscaledTime < nextStatusRefreshTime)
            return;
        nextStatusRefreshTime = Time.unscaledTime + 0.5f;
        RefreshStatus();

        if (restartConfirmUntil > 0f && Time.unscaledTime >= restartConfirmUntil)
        {
            restartConfirmUntil = 0f;
            SetRestartLabel("RESTART");
        }
    }

    private static void Wire(Button button, UnityAction action)
    {
        if (button != null)
            button.onClick.AddListener(action);
    }

    public static void SetPaused(bool paused)
    {
        if (IsPaused == paused)
            return;
        IsPaused = paused;
        Debug.Log("[World control] simulation " + (paused ? "frozen" : "resumed"));
        Instance?.ApplyFreezeToAnimators(paused);
    }

    private void ApplyFreezeToAnimators(bool freeze)
    {
        sceneAnimators = FindObjectsOfType<Animator>();
        if (freeze)
        {
            animatorSpeeds.Clear();
            foreach (Animator anim in sceneAnimators)
            {
                if (anim == null)
                    continue;
                animatorSpeeds[anim] = anim.speed;
                anim.speed = 0f;
            }
            return;
        }

        foreach (Animator anim in sceneAnimators)
        {
            if (anim != null && animatorSpeeds.TryGetValue(anim, out float speed))
                anim.speed = speed;
        }
        animatorSpeeds.Clear();
    }

    public void TogglePanel()
    {
        if (panelGroup == null)
        {
            Debug.LogError("[World control] Panel Group is not assigned on " + name + ".", this);
            return;
        }

        SetPanelVisible(panelGroup.alpha <= 0.5f);
    }

    private void SetPanelVisible(bool visible)
    {
        if (panelGroup == null)
            return;
        panelGroup.alpha = visible ? 1f : 0f;
        panelGroup.interactable = visible;
        panelGroup.blocksRaycasts = visible;
    }

    public void TryUnlockAdmin()
    {
        if (string.IsNullOrEmpty(adminPassword))
        {
            ApplyAdminLock();
            return;
        }
        string entered = adminPasswordField != null ? adminPasswordField.text.Trim() : "";
        if (entered == adminPassword)
        {
            if (adminStatusText != null)
                adminStatusText.text = "";
            ApplyAdminLock();
        }
        else if (adminStatusText != null)
        {
            adminStatusText.text = "Wrong password";
        }
    }

    private void ApplyAdminLock()
    {
        bool locked = !string.IsNullOrEmpty(adminPassword);
        if (apiKeySection != null)
            apiKeySection.SetActive(!locked);
    }

    public void ApplyApiKey()
    {
        if (apiKeyField == null && modelField == null
            && secondaryApiKeyField == null && secondaryModelField == null)
        {
            Debug.LogError("[World control] No API key/provider fields assigned on " + name + ".", this);
            return;
        }

        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return;

        brain.ApplyRuntimeProviders(
            new RuntimeProviderOverride
            {
                apiKeyEnvironmentVariable = brain.DefaultApiKeyEnvironmentVariable,
                apiKey = apiKeyField != null ? apiKeyField.text : "",
                model = modelField != null ? modelField.text : ""
            },
            new RuntimeProviderOverride
            {
                apiKeyEnvironmentVariable = secondaryApiKeyEnvironmentVariable,
                apiKey = secondaryApiKeyField != null ? secondaryApiKeyField.text : "",
                model = secondaryModelField != null ? secondaryModelField.text : ""
            });
        RefreshStatus();
    }

    public void ClearApiKey()
    {
        if (apiKeyField != null)
            apiKeyField.text = "";
        if (modelField != null)
            modelField.text = "";
        if (secondaryApiKeyField != null)
            secondaryApiKeyField.text = "";
        if (secondaryModelField != null)
            secondaryModelField.text = "";
        ApplyApiKey();
    }

    public void TestConnection()
    {
        LLMBrainService.Instance?.TestConnection();
    }

    public void ToggleStartStop()
    {
        SetPaused(!IsPaused);
        RefreshStatus();
    }

    private void OnRestartClick()
    {
        if (restartConfirmUntil > 0f)
        {
            restartConfirmUntil = 0f;
            SetRestartLabel("RESTART");
            RestartWorld();
            return;
        }

        restartConfirmUntil = Time.unscaledTime + 4f;
        SetRestartLabel("RESTART - CLICK AGAIN TO CONFIRM");
    }

    public void RestartWorld()
    {
        SetPaused(false);
        LLMBrainService.Instance?.RestartWorld();
        RefreshStatus();
    }

    private void SetRestartLabel(string text)
    {
        if (restartButtonLabel != null)
            restartButtonLabel.text = text;
    }

    private void RefreshStatus()
    {
        LLMBrainService brain = LLMBrainService.Instance;
        string world = brain != null ? brain.WorldDateTime.ToString("ddd HH:mm") : "--:--";
        string llm = brain != null && brain.HasUsableApiKey
            ? "LLM key active" : "LLM key missing";
        if (statusText != null)
            statusText.text = (IsPaused ? "SIMULATION FROZEN" : "SIMULATION RUNNING")
                + "   " + world + "   " + llm;

        if (startStopImage != null)
            startStopImage.sprite = IsPaused ? startSprite : stopSprite;
    }
}