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

    private Vector2 lastFacing = Vector2.down;
    private GameObject currentHat;
    private Vector3 currentHatStandingOffset;
    private Vector3 currentHatSittingOffset;
    private bool lastHatSittingState;
    private GameObject heldItem;
    private float heldItemExpiry;
    private SortingGroup sortingGroup;

    public bool ThoughtBubblesEnabled => thoughtBubblesEnabled;
    public bool IsHolding => heldItem != null;

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
        {
            Transform visualRoot = animator != null ? animator.transform : transform;
            handAnchor = new GameObject("HandAnchor").transform;
            handAnchor.SetParent(visualRoot, false);
            handAnchor.localPosition = new Vector3(0.3f, -0.15f, 0f);
        }
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
        // Sitting clips control the SpriteRenderer sorting themselves so the body
        // can move behind/in front of an external chair. A SortingGroup would make
        // the whole character atomic and prevent those animation curves from
        // interacting with the chair renderer.
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

    public AgentThoughtBubble CreateSharedDialogueBubble(Transform anchor)
    {
        if (!thoughtBubblesEnabled || thoughtBubblePrefab == null || anchor == null)
            return null;

        AgentThoughtBubble dialogueBubble = Instantiate(thoughtBubblePrefab, anchor);
        dialogueBubble.name = anchor.name + " Dialogue";
        return dialogueBubble;
    }

    public void AttachItemToHand(SceneItem item, Vector3 localOffset = default, Vector3 localScale = default, Quaternion localRot = default, float holdDuration = 0f)
    {
        ClearHeldItem();
        if (item == null) return;

        heldItem = item.gameObject;
        heldItem.transform.SetParent(handAnchor, false);
        heldItem.transform.localPosition = localOffset;
        heldItem.transform.localScale = localScale == default ? Vector3.one : localScale;
        heldItem.transform.localRotation = localRot;
        heldItemExpiry = holdDuration > 0f ? Time.time + holdDuration : 0f;

        if (item.TryGetComponent<SpriteRenderer>(out var sr)) ApplyHeldItemSorting(sr);
    }

    public void PlaceHeldItemAt(Transform parent, Vector3 localPosition)
    {
        if (heldItem == null)
            return;

        heldItem.transform.SetParent(parent, false);
        heldItem.transform.localPosition = localPosition;
        heldItem.transform.localScale = Vector3.one;
        heldItem.transform.localRotation = Quaternion.identity;

        if (heldItem.TryGetComponent<SpriteRenderer>(out var sr))
        {
            sr.sortingOrder = 15;
        }

        heldItem = null;
        heldItemExpiry = 0f;
    }

    public void ClearHeldItem()
    {
        if (heldItem != null)
        {
            Destroy(heldItem);
            heldItem = null;
        }
    }

    private void ApplyHeldItemSorting(SpriteRenderer sr)
    {
        SpriteRenderer bodyRenderer = VisualRoot.GetComponentInChildren<SpriteRenderer>();
        if (bodyRenderer != null)
        {
            sr.sortingLayerID = bodyRenderer.sortingLayerID;
            sr.sortingOrder = bodyRenderer.sortingOrder + 10;
        }
        else
        {
            sr.sortingOrder = 50;
        }
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
