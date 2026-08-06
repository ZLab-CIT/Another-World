using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(SortingGroup))]
[ExecuteAlways]
public class AgentPresentation2D : MonoBehaviour
{
    [Header("Character Visuals")]
    [SerializeField] private Animator animator;
    [Tooltip("Base of the character group order. Child SpriteRenderer order changes made by animations are preserved inside the group.")]
    [SerializeField] private int characterGroupBaseOrder = 10000;
    [SerializeField, Min(1f)] private float sortingUnitsPerWorldUnit = 100f;
    [SerializeField] private Transform handAnchor;

    [Header("Thought Bubble")]
    [SerializeField] private bool thoughtBubblesEnabled = true;
    [SerializeField] private AgentThoughtBubble thoughtBubblePrefab;
    [SerializeField] private AgentThoughtBubble thoughtBubble;
    [SerializeField] private AgentThoughtBubble speechBubble;

    [Header("Emotion Sprites")]
    [SerializeField] private Sprite[] emotionSprites;

    private Vector2 lastFacing = Vector2.down;
    private GameObject currentHat;
    private Vector3 currentHatStandingOffset;
    private Vector3 currentHatSittingOffset;
    private bool lastHatSittingState;
    private GameObject heldItem;
    private SpriteRenderer heldItemRenderer;
    private float heldItemExpiry;
    private SortingGroup sortingGroup;

    public bool ThoughtBubblesEnabled => thoughtBubblesEnabled;
    public bool IsHolding => heldItem != null;
    public Sprite GetEmotionSprite(AgentEmotion emotion)
    {
        int index = emotion switch
        {
            AgentEmotion.Happy => 0,
            AgentEmotion.Lol => 1,
            AgentEmotion.Romantic => 2,
            AgentEmotion.Angry => 3,
            AgentEmotion.Sad => 4,
            AgentEmotion.Shocked => 5,
            AgentEmotion.Crying => 6,
            AgentEmotion.Surprised => 7,
            AgentEmotion.Cool => 8,
            AgentEmotion.Confused => 9,
            AgentEmotion.Sleepy => 10,
            AgentEmotion.FacePalm => 11,
            _ => -1
        };
        return index >= 0 && emotionSprites != null && index < emotionSprites.Length
            ? emotionSprites[index] : null;
    }

    public void SetFacing(Vector2 direction)
    {
        if (direction.sqrMagnitude <= 0.0001f)
            return;

        lastFacing = Mathf.Abs(direction.x) > Mathf.Abs(direction.y)
            ? new Vector2(Mathf.Sign(direction.x), 0f)
            : new Vector2(0f, Mathf.Sign(direction.y));
    }

    private void Awake()
    {
        InitializeVisuals();
    }

    private void Update()
    {
        if (heldItem != null && heldItemExpiry > 0f && Time.time >= heldItemExpiry)
            ClearHeldItem();
    }

    private void OnEnable()
    {
        InitializeVisuals();
        UpdateSortingOrder();
    }

    private void OnValidate()
    {
        InitializeVisuals();
        UpdateSortingOrder();
    }

    private void InitializeVisuals()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        sortingGroup = GetComponent<SortingGroup>();
        if (sortingGroup == null)
            sortingGroup = gameObject.AddComponent<SortingGroup>();

        sortingGroup.sortingLayerName = "Default";

        if (handAnchor == null)
            handAnchor = GetComponentInChildren<Transform>();
    }

    public void UpdateState(Vector2 velocity, bool isSitting)
    {
        if (animator != null)
        {
            bool isMoving = velocity.sqrMagnitude > 0.0001f;
            if (isMoving)
            {
                Vector2 direction = velocity.normalized;
                lastFacing = Mathf.Abs(direction.x) > Mathf.Abs(direction.y)
                    ? new Vector2(Mathf.Sign(direction.x), 0f)
                    : new Vector2(0f, Mathf.Sign(direction.y));
            }

            animator.SetBool("IsMoving", isMoving);
            animator.SetFloat("MoveX", lastFacing.x);
            animator.SetFloat("MoveY", lastFacing.y);
            animator.SetBool("IsSitting", isSitting);
        }

        UpdateHatPlacement(isSitting);
        UpdateHeldItemSorting();
        bool automaticSortingEnabled = !isSitting;
        if (sortingGroup.enabled != automaticSortingEnabled)
            sortingGroup.enabled = automaticSortingEnabled;

        if (isSitting)
            return;

        UpdateSortingOrder();
    }

    public void ShowThought(string content)
    {
        if (!thoughtBubblesEnabled)
            return;

        if (thoughtBubble == null)
        {
            if (thoughtBubblePrefab == null)
            {
                Debug.LogWarning($"{name}: no thought bubble prefab is assigned.", this);
                return;
            }

            thoughtBubble = Instantiate(thoughtBubblePrefab, transform);
        }

        thoughtBubble.Show(content);
    }

    public void HideThought()
    {
        if (thoughtBubble != null)
            thoughtBubble.Hide();
    }

    public void ShowSpeech(string speakerName, string content, Color speakerColor)
    {
        AgentThoughtBubble bubble = EnsureSpeechBubble();
        if (bubble != null)
            bubble.ShowDialogue(speakerName, content, speakerColor);
    }

    public void HideSpeech()
    {
        if (speechBubble != null)
            speechBubble.Hide();
    }

    private AgentThoughtBubble EnsureSpeechBubble()
    {
        if (!thoughtBubblesEnabled)
            return null;
        if (speechBubble != null)
            return speechBubble;
        if (thoughtBubblePrefab == null)
        {
            Debug.LogWarning($"{name}: no bubble prefab is assigned.", this);
            return null;
        }

        speechBubble = Instantiate(thoughtBubblePrefab, transform);
        speechBubble.name = "SpeechBubble";
        return speechBubble;
    }

    public void AttachItemToHand(SceneItem item, Vector3 localOffset = default, Vector3 localScale = default, Quaternion localRot = default, float holdDuration = 0f)
    {
        ClearHeldItem();
        if (item == null) return;

        heldItem = item.gameObject;
        heldItem.transform.SetParent(handAnchor, false);
        heldItem.transform.localPosition = localOffset;
        if (localScale != default)
        {
            Vector3 parentLossy = handAnchor.lossyScale;
            heldItem.transform.localScale = new Vector3(
                parentLossy.x > 0 ? localScale.x / parentLossy.x : 1f,
                parentLossy.y > 0 ? localScale.y / parentLossy.y : 1f,
                parentLossy.z > 0 ? localScale.z / parentLossy.z : 1f);
        }
        heldItem.transform.localRotation = localRot;
        heldItemExpiry = holdDuration > 0f ? Time.time + holdDuration : 0f;

        if (item.TryGetComponent<SpriteRenderer>(out var sr))
        {
            heldItemRenderer = sr;
            ApplyHeldItemSorting(sr);
        }
        else
        {
            heldItemRenderer = null;
        }
    }

    public void PlaceHeldItemAt(Transform parent, Vector3 localPosition, float destroyAfter = 0f)
    {
        if (heldItem == null || parent == null)
            return;

        Vector3 worldScale = heldItem.transform.lossyScale;
        heldItem.transform.SetParent(parent, false);
        heldItem.transform.localPosition = localPosition;
        Vector3 parentLossy = parent.lossyScale;
        heldItem.transform.localScale = new Vector3(
            parentLossy.x > 0 ? worldScale.x / parentLossy.x : 1f,
            parentLossy.y > 0 ? worldScale.y / parentLossy.y : 1f,
            parentLossy.z > 0 ? worldScale.z / parentLossy.z : 1f);
        heldItem.transform.localRotation = Quaternion.identity;

        if (heldItem.TryGetComponent<SpriteRenderer>(out var sr))
        {
            sr.sortingOrder = 15;
        }

        if (heldItem.TryGetComponent<SceneItem>(out var sceneItem))
            sceneItem.ScheduleDestroy(destroyAfter);

        heldItem = null;
        heldItemRenderer = null;
        heldItemExpiry = 0f;
    }

    public void ClearHeldItem()
    {
        if (heldItem != null)
        {
            Destroy(heldItem);
            heldItem = null;
        }

        heldItemRenderer = null;
        heldItemExpiry = 0f;
    }

    public SceneItem ReleaseHeldItem()
    {
        if (heldItem == null)
            return null;
        GameObject released = heldItem;
        heldItem = null;
        heldItemRenderer = null;
        heldItemExpiry = 0f;
        released.transform.SetParent(null, true);
        return released.GetComponent<SceneItem>();
    }

    public SceneItem HeldItem => heldItem != null ? heldItem.GetComponent<SceneItem>() : null;

    private void ApplyHeldItemSorting(SpriteRenderer sr)
    {
        SpriteRenderer bodyRenderer = VisualRoot.GetComponentInChildren<SpriteRenderer>();
        if (bodyRenderer != null)
        {
            sr.sortingLayerID = bodyRenderer.sortingLayerID;
            sr.sortingOrder = lastFacing.y > 0
                ? bodyRenderer.sortingOrder - 10
                : bodyRenderer.sortingOrder + 10;
        }
        else
        {
            sr.sortingOrder = 50;
        }
    }

    private void UpdateHeldItemSorting()
    {
        if (heldItemRenderer != null)
            ApplyHeldItemSorting(heldItemRenderer);
    }

    public void ApplyHat(Sprite hatSprite, Vector3 standingOffset, Vector3 sittingOffset, Vector3 localScale)
    {
        if (hatSprite == null)
            return;

        if (currentHat != null)
            Destroy(currentHat);

        currentHatStandingOffset = standingOffset;
        currentHatSittingOffset = sittingOffset;

        currentHat = new GameObject("Hat");
        currentHat.transform.SetParent(VisualRoot, false);
        currentHat.transform.localPosition = lastHatSittingState ? sittingOffset : standingOffset;
        currentHat.transform.localScale = localScale;

        GameObject visual = new("Visual");
        visual.transform.SetParent(currentHat.transform, false);
        visual.transform.localPosition = new Vector3(0f, -hatSprite.bounds.min.y, 0f);

        SpriteRenderer hatRenderer = visual.AddComponent<SpriteRenderer>();
        hatRenderer.sprite = hatSprite;

        SpriteRenderer bodyRenderer = VisualRoot.GetComponentInChildren<SpriteRenderer>();
        if (bodyRenderer != null)
        {
            hatRenderer.sortingLayerID = bodyRenderer.sortingLayerID;
            hatRenderer.sortingOrder = bodyRenderer.sortingOrder + 20;
        }
        else
        {
            hatRenderer.sortingOrder = 100;
        }
    }

    public string InferAgentType()
    {
        SpriteRenderer renderer = VisualRoot.GetComponentInChildren<SpriteRenderer>();
        if (renderer == null || renderer.sprite == null)
            return "";

        string spriteName = renderer.sprite.name;
        int separator = spriteName.IndexOf('_');
        return separator > 0 ? spriteName[..separator] : spriteName;
    }

    private Transform VisualRoot => animator != null ? animator.transform : transform;

    private void UpdateSortingOrder()
    {
        if (sortingGroup == null)
            return;

        int order = characterGroupBaseOrder
            - Mathf.RoundToInt(transform.position.y * sortingUnitsPerWorldUnit);
        sortingGroup.sortingOrder = order;
    }

    private void UpdateHatPlacement(bool isSitting)
    {
        if (isSitting == lastHatSittingState)
            return;

        lastHatSittingState = isSitting;
        if (currentHat != null)
            currentHat.transform.localPosition = isSitting ? currentHatSittingOffset : currentHatStandingOffset;
    }

}

public sealed class AgentEmotionDisplay2D : MonoBehaviour
{
    private AIWorkerAgent owner;
    private AgentPresentation2D presentation;
    private SpriteRenderer icon;
    private Renderer characterRenderer;
    private AgentEmotion displayedEmotion = (AgentEmotion)(-1);
    private float pulseUntil;

    public void Bind(AIWorkerAgent agent, AgentPresentation2D agentPresentation)
    {
        owner = agent;
        presentation = agentPresentation;
        EnsureDisplay();
        SetEmotion(owner != null ? owner.CurrentEmotion : AgentEmotion.Neutral);
    }

    private void LateUpdate()
    {
        if (owner == null)
            return;

        EnsureDisplay();
        PositionDisplay();
        float pulse = Time.time < pulseUntil
            ? 1f + Mathf.Sin(Time.time * 12f) * 0.08f : 1f;
        icon.transform.localScale = Vector3.one * 0.3f * pulse;
    }

    public void SetEmotion(AgentEmotion emotion)
    {
        EnsureDisplay();
        if (displayedEmotion == emotion)
            return;
        displayedEmotion = emotion;
        icon.sprite = presentation != null
            ? presentation.GetEmotionSprite(emotion) : null;
        icon.enabled = icon.sprite != null;
        pulseUntil = Time.time + 0.7f;
    }

    private void EnsureDisplay()
    {
        if (icon != null)
            return;

        characterRenderer = GetComponentInChildren<Renderer>();
        GameObject display = new(name + " Emotion");
        display.transform.SetParent(transform, false);
        icon = display.AddComponent<SpriteRenderer>();
        icon.sortingOrder = 29900;
    }

    private void PositionDisplay()
    {
        float localTop = 0.8f;
        if (characterRenderer != null)
            localTop = transform.InverseTransformPoint(
                new Vector3(transform.position.x,
                    characterRenderer.bounds.max.y + 0.14f,
                    transform.position.z)).y;
        // Keep reactions beside the face, below world-space speech bubbles.
        icon.transform.localPosition = new Vector3(0.48f, localTop - 0.24f, 0f);
        icon.transform.rotation = Quaternion.identity;
    }
}
