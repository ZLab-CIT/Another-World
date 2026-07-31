using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class VendingEventDispatcher : MonoBehaviour
{
    public static VendingEventDispatcher Instance { get; private set; }

    [Header("Event Database")]
    [Tooltip("Runtime event database loaded from Resources/VendingEvents.")]
    [SerializeField] private List<VendingEventSO> events = new();

    [Header("References")]
    [SerializeField] private VendingEventAnnouncer announcer;

    [Header("Drop Visual")]
    [SerializeField] private int dropSortingOrder = 50;
    [SerializeField] private float dropSpawnJitter = 0.3f;

    [Header("Hats")]
    [Tooltip("Hat catalog mapping agentType -> hat sprites, offsets, and scales.")]
    [SerializeField] private HatCatalogSO hatCatalog;

    [Header("Gacha Weights (mixed-roll per press)")]
    [Tooltip("Base weight for buff (Category A) events. Higher = buffs appear more often.")]
    [SerializeField] private float buffEventWeight = 100f;
    [SerializeField] private float commonGachaWeight = 8f;
    [SerializeField] private float rareGachaWeight = 3f;
    [SerializeField] private float epicGachaWeight = 1f;
    [SerializeField] private float legendaryGachaWeight = 0.3f;
    [SerializeField] private bool includeHatsInGacha;

    private static Sprite cachedWhiteSprite;

    private VendingMachine vendingMachine;
    private readonly VendingEventCatalog catalog = new();

    public IReadOnlyList<VendingEventSO> Events => events;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        events ??= new List<VendingEventSO>();

        catalog.LoadFromResources("VendingEvents");
        events.Clear();
        events.AddRange(catalog.Events);

        if (hatCatalog == null)
            hatCatalog = Resources.Load<HatCatalogSO>("VendingEvents/HatCatalog");

        if (hatCatalog == null)
        {
            HatCatalogSO[] catalogs = Resources.LoadAll<HatCatalogSO>("");
            if (catalogs != null && catalogs.Length > 0)
                hatCatalog = catalogs[0];
        }

        if (announcer == null)
            announcer = FindFirstObjectByType<VendingEventAnnouncer>();
    }

    private IEnumerator Start()
    {
        yield return null;
        RestoreFurniture();
    }

    public void TriggerEvent(VendingEventSO evt)
    {
        if (evt == null)
            return;

        StartCoroutine(RunEvent(evt));
    }

    public VendingEventSO FindEventByProductId(string productId)
    {
        return catalog.FindByProductId(productId);
    }

    public VendingEventSO PickEvent(bool cosmetic)
    {
        return catalog.PickWeighted(evt =>
                (evt is IVendingGachaEvent) == cosmetic
                && (includeHatsInGacha || evt is not VendingHatEventSO),
            GetEventWeight);
    }

    public void ShowOfflineCoupon(string userId, OfflineCouponReward reward)
    {
        if (announcer == null)
            return;

        string title = string.IsNullOrEmpty(reward.displayName)
            ? "Offline Coupon"
            : reward.displayName;
        string subtitle = "User " + userId + " receives " + reward.couponId;
        if (!string.IsNullOrEmpty(reward.description))
            subtitle += ": " + reward.description;

        announcer.Show(title, subtitle, null, 2f);
    }

    public void ShowWorldAnnouncement(string title, string subtitle, Sprite icon = null,
        float holdSeconds = 3f)
    {
        if (announcer == null)
            announcer = FindFirstObjectByType<VendingEventAnnouncer>();
        if (announcer == null)
            return;

        announcer.Show(title, subtitle, icon, holdSeconds);
    }

    private IEnumerator RunEvent(VendingEventSO evt)
    {
        LLMBrainService.Instance?.RememberWorldEvent(evt.displayName + ": " + evt.description);
        string subtitle = evt.description;
        if (evt is IVendingGachaEvent gacha)
            subtitle = gacha.Rarity.ToString().ToUpperInvariant() + " \u2014 " + evt.description;

        if (announcer != null)
            announcer.Show(evt.displayName, subtitle, evt.icon, evt.announceDuration);

        if (evt.announceDuration > 0f)
            yield return new WaitForSeconds(evt.announceDuration);

        List<AIWorkerAgent> targets = VendingTargetResolver.Resolve(evt, hatCatalog);

        if (evt is VendingBuffEventSO buff)
            PlayMachineReaction(buff, targets);
        ApplyCosmetic(evt, targets);
        OfficeEventDirector.Instance?.NotifyVendingEvent(evt, targets);

        if (targets.Count == 0)
            yield break;

        if (evt is VendingBuffEventSO buffEvent)
        {
            foreach (AIWorkerAgent agent in targets)
            {
                if (agent == null)
                    continue;

                agent.ApplyEffects(buffEvent.energyChange, buffEvent.focusChange,
                    buffEvent.socialChange, buffEvent.productivityChange);
                if (buffEvent.speedMultiplier != 1f && buffEvent.speedBuffDuration > 0f)
                    agent.ApplySpeedBuff(buffEvent.speedMultiplier, buffEvent.speedBuffDuration);
                if (buffEvent.decayOverrideDuration > 0f &&
                    (buffEvent.energyDecayMultiplier != 1f || buffEvent.focusDecayMultiplier != 1f ||
                     buffEvent.socialDecayMultiplier != 1f))
                    agent.ApplyDecayOverride(buffEvent.energyDecayMultiplier, buffEvent.focusDecayMultiplier,
                        buffEvent.socialDecayMultiplier, buffEvent.decayOverrideDuration);
            }

            if (buffEvent.speedBuffDuration > 0f &&
                (buffEvent.postEnergyChange != 0f || buffEvent.postFocusChange != 0f ||
                 buffEvent.postSocialChange != 0f || buffEvent.postProductivityChange != 0f))
            {
                yield return new WaitForSeconds(buffEvent.speedBuffDuration);
                foreach (AIWorkerAgent agent in targets)
                    if (agent != null)
                        agent.ApplyEffects(buffEvent.postEnergyChange, buffEvent.postFocusChange,
                            buffEvent.postSocialChange, buffEvent.postProductivityChange);
            }
        }
    }

    private float GetCosmeticRarityWeight(VendingRarity rarity)
    {
        switch (rarity)
        {
            case VendingRarity.Rare: return rareGachaWeight;
            case VendingRarity.Epic: return epicGachaWeight;
            case VendingRarity.Legendary: return legendaryGachaWeight;
            default: return commonGachaWeight;
        }
    }

    private float GetEventWeight(VendingEventSO evt)
    {
        if (evt == null)
            return 0f;
        if (!includeHatsInGacha && evt is VendingHatEventSO)
            return 0f;

        return evt is IVendingGachaEvent gacha
            ? GetCosmeticRarityWeight(gacha.Rarity)
            : buffEventWeight;
    }

    private void ApplyCosmetic(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        switch (evt)
        {
            case VendingFurnitureEventSO furniture:
                SpawnFurniture(furniture, targets);
                break;

            case VendingConfettiEventSO confetti:
                StartCoroutine(ConfettiRain(Mathf.Max(confetti.count, 160), Mathf.Max(confetti.duration, 2f), confetti.sprite));
                break;

            case VendingHatEventSO:
                if (targets.Count > 0 && targets[0] != null && hatCatalog != null)
                {
                    HatCatalogSO.HatEntry hat = hatCatalog.PickRandomHat(targets[0].AgentType);
                    if (hat != null)
                    {
                        HatCatalogSO.HatPool pool = hatCatalog.GetPool(targets[0].AgentType);
                        if (pool != null && targets[0].TryGetComponent(
                                out AgentPresentation2D presentation))
                        {
                            presentation.ApplyHat(
                                hat.sprite, pool.standingOffset, pool.sittingOffset,
                                hat.localScale);
                        }
                        Sprite face = GetAgentIcon(targets[0]);
                        HighlightTransform(targets[0].transform, 3f);
                        if (announcer != null)
                            announcer.Show("Hat Equipped", targets[0].name + " got a new hat.", face != null ? face : hat.sprite, 2f);
                    }
                }
                break;

        }
    }

    private void SpawnFurniture(VendingFurnitureEventSO evt,
        List<AIWorkerAgent> targets,
        PersistedFurnitureState restored = null,
        bool showPresentation = true,
        bool remember = true)
    {
        OfficeGrid2D grid = FindFirstObjectByType<OfficeGrid2D>();
        Vector3 position = restored != null
            ? new Vector3(restored.positionX, restored.positionY, restored.positionZ)
            : ResolveFurniturePosition(evt, targets, grid);
        string socketId = ResolveSocketId(evt);
        ClearFurnitureSocket(socketId);

        GameObject furniture;
        Sprite pickedSprite = null;
        if (evt.furniturePrefab != null)
        {
            furniture = Instantiate(evt.furniturePrefab, position, Quaternion.identity);
        }
        else
        {
            furniture = new GameObject("Furniture_" + evt.displayName);
            furniture.transform.position = position;
            SpriteRenderer sr = furniture.AddComponent<SpriteRenderer>();
            Sprite furnitureSprite = restored != null
                ? FindSprite(evt.furnitureSprites, restored.spriteName)
                : PickSprite(evt.furnitureSprites);
            if (furnitureSprite == null)
                furnitureSprite = evt.icon;
            pickedSprite = furnitureSprite;
            sr.sprite = furnitureSprite != null ? furnitureSprite : GetWhiteSprite();
            sr.color = furnitureSprite != null
                ? Color.white
                : evt.placeholderColor == Color.white
                    ? new Color(0.3f, 0.6f, 0.35f)
                    : evt.placeholderColor;
            sr.sortingOrder = dropSortingOrder + 5;
        }

        furniture.transform.localScale *= Mathf.Max(0.01f, evt.furnitureScale);

        MakeDecorationOnly(furniture);
        furniture.name = "Furniture_" + socketId + "_" + evt.displayName;

        if (evt.furnitureKind == UpgradeableFurnitureKind.Plant)
            AddPlantActionPoint(furniture);

        FurnitureSocketItem marker = furniture.GetComponent<FurnitureSocketItem>();
        if (marker == null)
            marker = furniture.AddComponent<FurnitureSocketItem>();
        marker.Initialize(socketId);

        if (showPresentation && announcer != null)
        {
            string objectName = pickedSprite != null ? pickedSprite.name : evt.displayName;
            announcer.Show("Appeared: " + objectName, "Placed at " + socketId, pickedSprite != null ? pickedSprite : evt.icon, 1.6f);
        }

        if (showPresentation)
            HighlightWorldPosition(position, 3.5f);

        if (remember)
            LLMBrainService.Instance?.RememberFurniture(new PersistedFurnitureState
            {
                socketId = socketId,
                eventId = evt.eventId,
                spriteName = pickedSprite != null ? pickedSprite.name : "",
                positionX = position.x,
                positionY = position.y,
                positionZ = position.z
            });

        if (evt.furnitureKind == UpgradeableFurnitureKind.Plant)
        {
            AIWorkerAgent[] agents = FindObjectsByType<AIWorkerAgent>(FindObjectsSortMode.None);
            foreach (AIWorkerAgent agent in agents)
                agent.RefreshActionPoints();
        }
    }

    private void RestoreFurniture()
    {
        LLMBrainService brain = LLMBrainService.Instance;
        if (brain == null)
            return;
        foreach (PersistedFurnitureState state in brain.RestoreFurniture())
        {
            if (state == null)
                continue;
            VendingFurnitureEventSO evt = events.Find(candidate =>
                candidate is VendingFurnitureEventSO
                && string.Equals(candidate.eventId, state.eventId,
                    System.StringComparison.OrdinalIgnoreCase))
                as VendingFurnitureEventSO;
            if (evt != null)
                SpawnFurniture(evt, new List<AIWorkerAgent>(), state, false, false);
        }
    }

    private static Sprite FindSprite(Sprite[] sprites, string spriteName)
    {
        if (sprites == null || string.IsNullOrWhiteSpace(spriteName))
            return null;
        foreach (Sprite sprite in sprites)
            if (sprite != null && string.Equals(
                    sprite.name, spriteName,
                    System.StringComparison.OrdinalIgnoreCase))
                return sprite;
        return null;
    }

    private Vector3 ResolveFurniturePosition(VendingFurnitureEventSO evt, List<AIWorkerAgent> targets, OfficeGrid2D grid)
    {
        if (TryFindSocketPosition(evt, grid, out Vector3 socketPosition))
            return socketPosition;

        if (TryFindDecorationPosition(grid, out Vector3 position))
            return position;

        Vector3 basePos = targets.Count > 0 && targets[0] != null
            ? targets[0].GetPosition()
            : GetViewportWorldPoint(Random.Range(0.2f, 0.8f), Random.Range(0.2f, 0.8f));
        basePos += new Vector3(Random.Range(-0.8f, 0.8f), Random.Range(-0.8f, 0.8f), 0f);

        return basePos;
    }

    private bool TryFindSocketPosition(VendingFurnitureEventSO evt, OfficeGrid2D grid, out Vector3 result)
    {
        string socketId = ResolveSocketId(evt);
        if (TryFindNamedSocket(socketId, out result))
            return true;

        Vector3 preferred = DefaultSocketPosition(evt.furnitureKind);
        if (grid == null)
        {
            result = preferred;
            return true;
        }

        if (grid.TryFindNearestWalkable(preferred, 0.45f, out Vector2 walkable))
            preferred = new Vector3(walkable.x, walkable.y, 0f);

        if (IsGoodDecorationPosition(preferred, grid))
        {
            result = preferred;
            return true;
        }

        result = preferred;
        return true;
    }

    private static bool TryFindNamedSocket(string socketId, out Vector3 result)
    {
        result = Vector3.zero;
        if (string.IsNullOrEmpty(socketId))
            return false;

        GameObject socket = GameObject.Find("FurnitureSocket_" + socketId);
        if (socket == null)
            socket = GameObject.Find(socketId);

        if (socket == null)
            return false;

        result = socket.transform.position;
        result.z = 0f;
        return true;
    }

    private static string ResolveSocketId(VendingFurnitureEventSO evt)
    {
        if (!string.IsNullOrEmpty(evt.furnitureSocketId))
            return evt.furnitureSocketId;

        return evt.furnitureKind.ToString();
    }

    private static Vector3 DefaultSocketPosition(UpgradeableFurnitureKind kind)
    {
        switch (kind)
        {
            case UpgradeableFurnitureKind.CoffeeMachine:
                return new Vector3(5.08f, 3.25f, 0f);
            case UpgradeableFurnitureKind.Lounge:
                return new Vector3(-5.2f, -3.35f, 0f);
            default:
                return new Vector3(-5.6f, 3.3f, 0f);
        }
    }

    private bool TryFindDecorationPosition(OfficeGrid2D grid, out Vector3 result)
    {
        result = Vector3.zero;
        if (grid == null)
            return false;

        List<Vector3> candidates = new();
        for (int i = 0; i < 48; i++)
        {
            Vector3 candidate = GetViewportWorldPoint(Random.Range(0.12f, 0.88f), Random.Range(0.12f, 0.82f));
            if (grid.TryFindNearestWalkable(candidate, 0.45f, out Vector2 walkable))
                candidate = new Vector3(walkable.x, walkable.y, 0f);

            if (IsGoodDecorationPosition(candidate, grid))
                candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return false;

        result = candidates[Random.Range(0, candidates.Count)];
        return true;
    }

    private bool IsGoodDecorationPosition(Vector3 position, OfficeGrid2D grid)
    {
        if (grid == null || !grid.IsBodyPositionClear(position, 0.35f))
            return false;

        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 viewport = cam.WorldToViewportPoint(position);
            if (viewport.x < 0.08f || viewport.x > 0.92f || viewport.y < 0.08f || viewport.y > 0.88f)
                return false;
        }

        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd != null)
        {
            foreach (AIWorkerAgent worker in crowd.Workers)
            {
                if (worker != null && Vector2.Distance(worker.GetPosition(), position) < 1.2f)
                    return false;
            }
        }

        OfficeActionPoint[] actionPoints;
        actionPoints = FindObjectsOfType<OfficeActionPoint>();
        foreach (OfficeActionPoint actionPoint in actionPoints)
        {
            if (actionPoint != null && Vector2.Distance(actionPoint.transform.position, position) < 0.9f)
                return false;
        }

        return true;
    }

    private static void MakeDecorationOnly(GameObject obj)
    {
        if (obj == null)
            return;

        Collider2D[] colliders = obj.GetComponentsInChildren<Collider2D>();
        foreach (Collider2D collider in colliders)
            collider.enabled = false;

        OfficeActionPoint[] actionPoints = obj.GetComponentsInChildren<OfficeActionPoint>();
        foreach (OfficeActionPoint actionPoint in actionPoints)
            actionPoint.enabled = false;
    }

    private static void AddPlantActionPoint(GameObject furniture)
    {
        OfficeActionPoint point = furniture.AddComponent<OfficeActionPoint>();
        point.actionType = OfficeActionType.PlantCare;
        point.useTime = 4f;
        point.baseScore = 8f;
        point.energyChange = 5f;
        point.focusChange = 0f;
        point.socialChange = 3f;
        point.productivityChange = 0f;
        point.slots = new List<OfficeActionSlot>
        {
            new OfficeActionSlot { offset = new Vector2(0f, -0.6f), facing = OfficeFacingDirection.Up }
        };
    }

    private static void ClearFurnitureSocket(string socketId)
    {
        if (string.IsNullOrEmpty(socketId))
            return;

        FurnitureSocketItem[] items = FindObjectsByType<FurnitureSocketItem>(FindObjectsSortMode.None);
        foreach (FurnitureSocketItem item in items)
        {
            if (item != null && item.SocketId == socketId)
                Destroy(item.gameObject);
        }
    }

    private IEnumerator ConfettiRain(int count, float duration, Sprite sprite)
    {
        if (count <= 0 || duration <= 0f)
            yield break;

        if (sprite == null)
            sprite = GetWhiteSprite();

        GameObject root = new("Screen Confetti Overlay");
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 2200;
        root.AddComponent<CanvasScaler>();
        root.AddComponent<GraphicRaycaster>();

        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.one;
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        RectTransform[] pieces = new RectTransform[count];
        Vector2[] velocities = new Vector2[count];
        float width = Mathf.Max(640f, Screen.width);
        float height = Mathf.Max(360f, Screen.height);

        for (int i = 0; i < count; i++)
        {
            GameObject piece = new("Confetti Piece");
            piece.transform.SetParent(root.transform, false);

            Image image = piece.AddComponent<Image>();
            image.sprite = sprite;
            image.color = Color.HSVToRGB(Random.value, 0.95f, 1f);
            image.raycastTarget = false;

            RectTransform rect = image.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(Random.Range(10f, 22f), Random.Range(5f, 12f));
            rect.anchoredPosition = new Vector2(Random.Range(-width * 0.52f, width * 0.52f), Random.Range(height * 0.38f, height * 0.68f));
            rect.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            pieces[i] = rect;
            velocities[i] = new Vector2(Random.Range(-120f, 120f), Random.Range(-420f, -220f));
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float alpha = Mathf.Clamp01(1f - (elapsed / duration));
            for (int i = 0; i < pieces.Length; i++)
            {
                RectTransform piece = pieces[i];
                if (piece == null)
                    continue;

                velocities[i].x += Mathf.Sin((elapsed * 4f) + i) * 4f;
                piece.anchoredPosition += velocities[i] * Time.deltaTime;
                piece.Rotate(0f, 0f, 420f * Time.deltaTime * (i % 2 == 0 ? 1f : -1f));

                Image image = piece.GetComponent<Image>();
                if (image != null)
                {
                    Color color = image.color;
                    color.a = alpha;
                    image.color = color;
                }
            }

            yield return null;
        }

        if (root != null)
            Destroy(root);
    }

    private static Vector3 GetViewportWorldPoint(float x, float y)
    {
        Camera cam = Camera.main;
        if (cam == null)
            return new Vector3(Random.Range(-3f, 3f), Random.Range(-2f, 2f), 0f);

        float depth = Mathf.Abs(cam.transform.position.z);
        Vector3 world = cam.ViewportToWorldPoint(new Vector3(x, y, depth));
        world.z = 0f;
        return world;
    }

    private VendingMachine GetVendingMachine()
    {
        if (vendingMachine != null)
            return vendingMachine;

        vendingMachine = FindFirstObjectByType<VendingMachine>();
        return vendingMachine;
    }

    private void PlayMachineReaction(VendingBuffEventSO evt, List<AIWorkerAgent> targets)
    {
        VendingMachine machine = GetVendingMachine();
        if (machine == null || machine.SpawnSnackExternal() == null)
        {
            SpawnDrop(evt, targets);
            return;
        }

        if (targets.Count > 0 && targets[0] != null)
            HighlightTransform(targets[0].transform, Mathf.Min(Mathf.Max(1.6f, evt.dropLifetime), 4f));
    }

    private void SpawnDrop(VendingBuffEventSO evt, List<AIWorkerAgent> targets)
    {
        Sprite sprite = PickDropSprite(evt);
        if (sprite == null)
            return;

        Vector3 position = ResolveDropPosition(targets);

        GameObject drop = new("Drop " + sprite.name);
        drop.transform.position = position;
        drop.transform.localScale = Vector3.one * Mathf.Max(0.01f, evt.dropScale);

        SpriteRenderer renderer = drop.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.color = Color.white;
        renderer.sortingOrder = dropSortingOrder;

        StartCoroutine(DestroyAfter(drop, Mathf.Max(0.1f, evt.dropLifetime)));

        if (announcer != null)
            announcer.Show("Appeared: " + sprite.name, "Visible for " + Mathf.Max(0.1f, evt.dropLifetime).ToString("0.#") + "s", sprite, 1.6f);

        HighlightWorldPosition(position, Mathf.Min(Mathf.Max(1.6f, evt.dropLifetime), 4f));
    }

    private Vector3 ResolveDropPosition(List<AIWorkerAgent> targets)
    {
        if (targets.Count > 0 && targets[0] != null)
        {
            Vector3 p = targets[0].GetPosition();
            p.x += Random.Range(-dropSpawnJitter, dropSpawnJitter);
            p.y += Random.Range(-dropSpawnJitter, dropSpawnJitter);
            p.z = 0f;
            return p;
        }

        Camera cam = Camera.main;
        if (cam != null)
            return new Vector3(Random.Range(-2f, 2f), Random.Range(-1f, 1f), 0f);

        return Vector3.zero;
    }

    private IEnumerator DestroyAfter(GameObject obj, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (obj != null)
            Destroy(obj);
    }

    private void HighlightWorldPosition(Vector3 position, float duration)
    {
        StartCoroutine(HighlightRoutine(null, position, duration));
    }

    private void HighlightTransform(Transform target, float duration)
    {
        if (target == null)
            return;

        StartCoroutine(HighlightRoutine(target, target.position, duration));
    }

    private IEnumerator HighlightRoutine(Transform target, Vector3 position, float duration)
    {
        GameObject marker = new("Event Highlight");
        LineRenderer line = marker.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = true;
        line.positionCount = 48;
        line.widthMultiplier = 0.045f;
        line.sortingOrder = dropSortingOrder + 180;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null)
            line.material = new Material(shader);

        Color color = new(1f, 0.92f, 0.25f, 1f);
        line.startColor = color;
        line.endColor = color;

        for (int i = 0; i < line.positionCount; i++)
        {
            float angle = (i / (float)line.positionCount) * Mathf.PI * 2f;
            line.SetPosition(i, new Vector3(Mathf.Cos(angle) * 0.55f, Mathf.Sin(angle) * 0.55f, 0f));
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            Vector3 center = target != null ? target.position : position;
            center.z = 0f;
            marker.transform.position = center;

            float pulse = 1f + Mathf.Sin(elapsed * 8f) * 0.12f;
            marker.transform.localScale = Vector3.one * pulse;
            Color pulseColor = color;
            pulseColor.a = Mathf.Clamp01(1f - (elapsed / duration)) * (0.65f + Mathf.Abs(Mathf.Sin(elapsed * 7f)) * 0.35f);
            line.startColor = pulseColor;
            line.endColor = pulseColor;

            yield return null;
        }

        if (marker != null)
            Destroy(marker);
    }

    private static Sprite GetAgentIcon(AIWorkerAgent agent)
    {
        if (agent == null)
            return null;

        SpriteRenderer renderer = agent.GetComponentInChildren<SpriteRenderer>();
        return renderer != null ? renderer.sprite : null;
    }

    private static Sprite GetWhiteSprite()
    {
        if (cachedWhiteSprite == null)
        {
            Texture2D tex = Texture2D.whiteTexture;
            cachedWhiteSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }
        return cachedWhiteSprite;
    }

    private static Sprite PickDropSprite(VendingBuffEventSO evt)
    {
        if (evt == null)
            return null;

        Sprite sprite = PickSprite(evt.dropSprites);
        return sprite != null ? sprite : evt.dropSprite;
    }

    private static Sprite PickSprite(Sprite[] sprites)
    {
        if (sprites == null || sprites.Length == 0)
            return null;

        List<Sprite> available = new();
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] != null)
                available.Add(sprites[i]);
        }

        if (available.Count == 0)
            return null;

        return available[Random.Range(0, available.Count)];
    }

}
