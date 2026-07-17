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

    [Header("Thought Bubble")]
    [SerializeField] private bool thoughtBubblesEnabled = true;
    [SerializeField] private AgentThoughtBubble thoughtBubblePrefab;
    [SerializeField] private AgentThoughtBubble thoughtBubble;

    private Vector2 lastFacing = Vector2.down;
    private GameObject currentHat;
    private Vector3 currentHatStandingOffset;
    private Vector3 currentHatSittingOffset;
    private bool lastHatSittingState;
    private SortingGroup sortingGroup;

    public bool ThoughtBubblesEnabled => thoughtBubblesEnabled;

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
