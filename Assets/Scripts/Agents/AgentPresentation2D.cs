using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class AgentPresentation2D : MonoBehaviour
{
    [Header("Character Visuals")]
    [SerializeField] private Animator animator;
    [SerializeField] private Transform hatAnchor;
    [SerializeField] private Vector3 fallbackHatAnchorPosition = new(0f, 0.5f, 0f);
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
    private Coroutine danceRoutine;
    private Transform runtimeHatAnchor;
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
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        sortingGroup = GetComponent<SortingGroup>();
        if (sortingGroup == null)
            sortingGroup = gameObject.AddComponent<SortingGroup>();

        // Set the group layer once. Animations may still change sorting settings on
        // child SpriteRenderers; SortingGroup keeps those relative changes intact.
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

        int groupOrder = characterGroupBaseOrder
            - Mathf.RoundToInt(transform.position.y * sortingUnitsPerWorldUnit);
        if (sortingGroup.sortingOrder != groupOrder)
            sortingGroup.sortingOrder = groupOrder;
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

    public void StartDance(float duration)
    {
        if (duration <= 0f)
            return;

        if (danceRoutine != null)
            StopCoroutine(danceRoutine);

        danceRoutine = StartCoroutine(DanceRoutine(duration));
    }

    public void ApplyHat(Sprite hatSprite, Vector3 standingOffset, Vector3 sittingOffset, Vector3 localScale)
    {
        if (hatSprite == null)
            return;

        Transform resolvedHatAnchor = ResolveHatAnchor();
        if (resolvedHatAnchor == null)
        {
            Debug.LogWarning($"{name}: no hat anchor could be resolved.", this);
            return;
        }

        if (currentHat != null)
            Destroy(currentHat);

        currentHatStandingOffset = standingOffset;
        currentHatSittingOffset = sittingOffset;

        currentHat = new GameObject("Hat");
        currentHat.transform.SetParent(resolvedHatAnchor, false);
        currentHat.transform.localPosition = lastHatSittingState ? sittingOffset : standingOffset;
        currentHat.transform.localScale = localScale;

        SpriteRenderer hatRenderer = currentHat.AddComponent<SpriteRenderer>();
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
        return separator > 0 ? spriteName.Substring(0, separator) : spriteName;
    }

    private Transform VisualRoot => animator != null ? animator.transform : transform;

    private Transform ResolveHatAnchor()
    {
        if (hatAnchor != null)
            return hatAnchor;

        Transform found = FindChildRecursive(VisualRoot, "HatAnchor");
        if (found != null)
            return found;

        if (runtimeHatAnchor == null)
        {
            GameObject anchorObject = new("HatAnchor");
            runtimeHatAnchor = anchorObject.transform;
            runtimeHatAnchor.SetParent(VisualRoot, false);
            runtimeHatAnchor.localPosition = fallbackHatAnchorPosition;
        }

        return runtimeHatAnchor;
    }

    private static Transform FindChildRecursive(Transform root, string childName)
    {
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindChildRecursive(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void UpdateHatPlacement(bool isSitting)
    {
        if (isSitting == lastHatSittingState)
            return;

        lastHatSittingState = isSitting;
        if (currentHat != null)
            currentHat.transform.localPosition = isSitting ? currentHatSittingOffset : currentHatStandingOffset;
    }

    private IEnumerator DanceRoutine(float duration)
    {
        Transform visualRoot = VisualRoot;
        Vector3 baseLocalPosition = visualRoot.localPosition;
        Vector3 baseLocalScale = visualRoot.localScale;

        float elapsed = 0f;
        float phase = Random.Range(0f, Mathf.PI * 2f);
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float beat = Mathf.Sin((elapsed * 9f) + phase);
            float side = Mathf.Sin((elapsed * 5.5f) + phase);
            visualRoot.localPosition = baseLocalPosition + new Vector3(side * 0.045f, Mathf.Abs(beat) * 0.09f, 0f);
            visualRoot.localScale = baseLocalScale * (1f + Mathf.Abs(beat) * 0.08f);
            yield return null;
        }

        visualRoot.localPosition = baseLocalPosition;
        visualRoot.localScale = baseLocalScale;
        danceRoutine = null;
    }
}
