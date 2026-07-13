using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class VendingEventDispatcher : MonoBehaviour
{
    public static VendingEventDispatcher Instance { get; private set; }

    [Header("Event Database")]
    [Tooltip("Vending events to choose from. Leave empty to auto-generate defaults.")]
    [SerializeField] private List<VendingEventSO> events = new List<VendingEventSO>();

    [Header("References (auto-created if missing)")]
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
    [SerializeField] private float commonCosmeticWeight = 8f;
    [SerializeField] private float rareCosmeticWeight = 3f;
    [SerializeField] private float epicCosmeticWeight = 1f;
    [SerializeField] private float legendaryCosmeticWeight = 0.3f;

    private static Sprite cachedWhiteSprite;

    public IReadOnlyList<VendingEventSO> Events => events;

    public static VendingEventDispatcher Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new GameObject(nameof(VendingEventDispatcher));
        return go.AddComponent<VendingEventDispatcher>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (events == null)
            events = new List<VendingEventSO>();

        if (events.Count == 0)
            events = CreateDefaultEvents();

        LoadCustomEvents();

        if (hatCatalog == null)
            hatCatalog = Resources.Load<HatCatalogSO>("VendingEvents/HatCatalog");

        if (hatCatalog == null)
        {
            HatCatalogSO[] catalogs = Resources.LoadAll<HatCatalogSO>("");
            if (catalogs != null && catalogs.Length > 0)
                hatCatalog = catalogs[0];
        }

        if (announcer == null)
        {
            GameObject announcerObj = new GameObject(nameof(VendingEventAnnouncer));
            announcerObj.transform.SetParent(transform, false);
            announcer = announcerObj.AddComponent<VendingEventAnnouncer>();
        }

    }

    public void TriggerBuffEvent()
    {
        if (events == null || events.Count == 0)
            return;

        List<VendingEventSO> pool = new List<VendingEventSO>();
        foreach (VendingEventSO evt in events)
        {
            if (evt != null && evt.cosmeticType == VendingCosmeticType.None)
                pool.Add(evt);
        }

        if (pool.Count == 0)
        {
            TriggerRandomEvent();
            return;
        }

        TriggerEvent(PickWeighted(pool));
    }

    public void TriggerRandomEvent()
    {
        if (events == null || events.Count == 0)
            return;

        TriggerEvent(PickWeighted(events));
    }

    public void TriggerGachaEvent()
    {
        if (events == null || events.Count == 0)
            return;

        List<VendingEventSO> pool = new List<VendingEventSO>();
        foreach (VendingEventSO evt in events)
        {
            if (evt != null && evt.cosmeticType != VendingCosmeticType.None)
                pool.Add(evt);
        }

        if (pool.Count == 0)
        {
            TriggerRandomEvent();
            return;
        }

        TriggerEvent(PickWeighted(pool));
    }

    public void TriggerHatEvent()
    {
        if (events == null)
            events = new List<VendingEventSO>();

        foreach (VendingEventSO evt in events)
        {
            if (evt != null && evt.cosmeticType == VendingCosmeticType.AgentHat)
            {
                TriggerEvent(evt);
                return;
            }
        }

        TriggerEvent(NewCosmetic("hat", "Random Hat", "One agent gets a stylish new hat!",
            VendingRarity.Rare, VendingCosmeticType.AgentHat, announce: 1.4f));
    }

    public void TriggerEvent(VendingEventSO evt)
    {
        if (evt == null)
            return;

        StartCoroutine(RunEvent(evt));
    }

    public VendingEventSO FindEventById(string eventId)
    {
        if (string.IsNullOrEmpty(eventId) || events == null)
            return null;

        foreach (VendingEventSO evt in events)
        {
            if (evt != null && evt.eventId == eventId)
                return evt;
        }

        return null;
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

    private IEnumerator RunEvent(VendingEventSO evt)
    {
        string subtitle = evt.description;
        if (evt.cosmeticType != VendingCosmeticType.None)
            subtitle = evt.rarity.ToString().ToUpperInvariant() + " \u2014 " + evt.description;

        if (announcer != null)
            announcer.Show(evt.displayName, subtitle, evt.icon, evt.announceDuration);

        if (evt.announceDuration > 0f)
            yield return new WaitForSeconds(evt.announceDuration);

        List<AIWorkerAgent> targets = ResolveTargets(evt);

        SpawnDrop(evt, targets);
        ApplyCosmetic(evt, targets);

        if (targets.Count == 0)
            yield break;

        foreach (AIWorkerAgent agent in targets)
        {
            if (agent == null)
                continue;

            agent.ApplyEffects(
                evt.energyChange,
                evt.focusChange,
                evt.socialChange,
                evt.productivityChange);

            if (evt.speedMultiplier != 1f && evt.speedBuffDuration > 0f)
                agent.ApplySpeedBuff(evt.speedMultiplier, evt.speedBuffDuration);

            if (evt.decayOverrideDuration > 0f &&
                (evt.energyDecayMultiplier != 1f ||
                 evt.focusDecayMultiplier != 1f ||
                 evt.socialDecayMultiplier != 1f))
            {
                agent.ApplyDecayOverride(
                    evt.energyDecayMultiplier,
                    evt.focusDecayMultiplier,
                    evt.socialDecayMultiplier,
                    evt.decayOverrideDuration);
            }
        }

        if (evt.speedBuffDuration > 0f &&
            (evt.postEnergyChange != 0f || evt.postFocusChange != 0f ||
             evt.postSocialChange != 0f || evt.postProductivityChange != 0f))
        {
            yield return new WaitForSeconds(evt.speedBuffDuration);

            foreach (AIWorkerAgent agent in targets)
            {
                if (agent != null)
                    agent.ApplyEffects(
                        evt.postEnergyChange,
                        evt.postFocusChange,
                        evt.postSocialChange,
                        evt.postProductivityChange);
            }
        }
    }

    private float GetCosmeticRarityWeight(VendingRarity rarity)
    {
        switch (rarity)
        {
            case VendingRarity.Rare: return rareCosmeticWeight;
            case VendingRarity.Epic: return epicCosmeticWeight;
            case VendingRarity.Legendary: return legendaryCosmeticWeight;
            default: return commonCosmeticWeight;
        }
    }

    private float GetEventWeight(VendingEventSO evt)
    {
        if (evt == null)
            return 0f;

        return evt.cosmeticType == VendingCosmeticType.None
            ? buffEventWeight
            : GetCosmeticRarityWeight(evt.rarity);
    }

    private VendingEventSO PickWeighted(List<VendingEventSO> pool)
    {
        float total = 0f;
        foreach (VendingEventSO evt in pool)
            total += GetEventWeight(evt);

        if (total <= 0f)
            return pool.Count > 0 ? pool[Random.Range(0, pool.Count)] : null;

        float roll = Random.value * total;
        foreach (VendingEventSO evt in pool)
        {
            roll -= GetEventWeight(evt);
            if (roll <= 0f)
                return evt;
        }

        return pool[pool.Count - 1];
    }

    private void ApplyCosmetic(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        switch (evt.cosmeticType)
        {
            case VendingCosmeticType.AgentTint:
                if (targets.Count > 0 && targets[0] != null)
                    targets[0].ApplyCosmeticTint(evt.agentTint);
                break;

            case VendingCosmeticType.FurnitureUnlock:
                SpawnFurniture(evt, targets);
                break;

            case VendingCosmeticType.ConfettiBurst:
                StartCoroutine(ConfettiRain(Mathf.Max(evt.confettiCount, 160), Mathf.Max(evt.confettiDuration, 2f), evt.confettiSprite));
                break;

            case VendingCosmeticType.AgentHat:
                if (targets.Count > 0 && targets[0] != null && hatCatalog != null)
                {
                    HatCatalogSO.HatEntry hat = hatCatalog.PickRandomHat(targets[0].agentType);
                    if (hat != null)
                    {
                        targets[0].ApplyHat(hat.sprite, hat.localOffset, hat.sittingLocalOffset, hat.localScale);
                        Sprite face = GetAgentIcon(targets[0]);
                        HighlightTransform(targets[0].transform, 3f);
                        if (announcer != null)
                            announcer.Show("Hat Equipped", targets[0].name + " got a new hat.", face != null ? face : hat.sprite, 2f);
                    }
                }
                break;

            case VendingCosmeticType.DiscoDance:
                StartCoroutine(DiscoParty(evt, targets));
                break;
        }
    }

    private void SpawnFurniture(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        OfficeGrid2D grid = FindFirstObjectByType<OfficeGrid2D>();
        Vector3 position = ResolveFurniturePosition(evt, targets, grid);
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
            Sprite furnitureSprite = PickSprite(evt.furnitureSprites);
            if (furnitureSprite == null)
                furnitureSprite = evt.icon != null ? evt.icon : PickDropSprite(evt);
            pickedSprite = furnitureSprite;
            sr.sprite = furnitureSprite != null ? furnitureSprite : GetWhiteSprite();
            sr.color = furnitureSprite != null
                ? Color.white
                : evt.agentTint == Color.white
                    ? new Color(0.3f, 0.6f, 0.35f)
                    : evt.agentTint;
            sr.sortingOrder = dropSortingOrder + 5;
        }

        MakeDecorationOnly(furniture);
        furniture.name = "Furniture_" + socketId + "_" + evt.displayName;

        FurnitureSocketItem marker = furniture.GetComponent<FurnitureSocketItem>();
        if (marker == null)
            marker = furniture.AddComponent<FurnitureSocketItem>();
        marker.Initialize(socketId);

        if (announcer != null)
        {
            string objectName = pickedSprite != null ? pickedSprite.name : evt.displayName;
            announcer.Show("Appeared: " + objectName, "Placed at " + socketId, pickedSprite != null ? pickedSprite : evt.icon, 1.6f);
        }

        HighlightWorldPosition(position, 3.5f);
    }

    private Vector3 ResolveFurniturePosition(VendingEventSO evt, List<AIWorkerAgent> targets, OfficeGrid2D grid)
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

    private bool TryFindSocketPosition(VendingEventSO evt, OfficeGrid2D grid, out Vector3 result)
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

    private static string ResolveSocketId(VendingEventSO evt)
    {
        if (evt != null && !string.IsNullOrEmpty(evt.furnitureSocketId))
            return evt.furnitureSocketId;

        return evt != null ? evt.furnitureKind.ToString() : "";
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

        List<Vector3> candidates = new List<Vector3>();
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
#if UNITY_2023_1_OR_NEWER
        actionPoints = FindObjectsByType<OfficeActionPoint>(FindObjectsSortMode.None);
#else
        actionPoints = FindObjectsOfType<OfficeActionPoint>();
#endif
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

    private static void ClearFurnitureSocket(string socketId)
    {
        if (string.IsNullOrEmpty(socketId))
            return;

#if UNITY_2023_1_OR_NEWER
        FurnitureSocketItem[] items = FindObjectsByType<FurnitureSocketItem>(FindObjectsSortMode.None);
#else
        FurnitureSocketItem[] items = FindObjectsOfType<FurnitureSocketItem>();
#endif
        foreach (FurnitureSocketItem item in items)
        {
            if (item != null && item.SocketId == socketId)
                Destroy(item.gameObject);
        }
    }

    private IEnumerator DiscoParty(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        float duration = Mathf.Max(3f, evt.confettiDuration > 0f ? evt.confettiDuration : 4f);
        List<AIWorkerAgent> dancers = targets;
        if (dancers == null || dancers.Count == 0)
        {
            VendingEventSO allEvent = ScriptableObject.CreateInstance<VendingEventSO>();
            allEvent.scope = VendingEventScope.AllAgents;
            dancers = ResolveTargets(allEvent);
            Destroy(allEvent);
        }

        foreach (AIWorkerAgent agent in dancers)
        {
            if (agent != null)
                agent.StartDance(duration);
        }

        StartCoroutine(ConfettiRain(Mathf.Max(evt.confettiCount, 220), duration, evt.confettiSprite));
        StartCoroutine(DiscoLights(duration));
        StartCoroutine(DiscoScreenTint(duration));

        float interval = 0.45f;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += interval;
            yield return new WaitForSeconds(interval);

            StartCoroutine(ConfettiRain(36, 1.2f, evt.confettiSprite));
        }
    }

    private IEnumerator DiscoLights(float duration)
    {
        Camera cam = Camera.main;
        Vector3 center = cam != null ? cam.transform.position : Vector3.zero;
        center.z = 0f;

        List<GameObject> lights = new List<GameObject>();
        for (int i = 0; i < 5; i++)
        {
            GameObject lightObj = new GameObject("Disco Light");
            lightObj.transform.position = GetViewportWorldPoint(Random.Range(0.18f, 0.82f), Random.Range(0.2f, 0.8f));
            lightObj.transform.localScale = new Vector3(Random.Range(160f, 280f), Random.Range(18f, 34f), 1f);

            SpriteRenderer sr = lightObj.AddComponent<SpriteRenderer>();
            sr.sprite = GetWhiteSprite();
            sr.color = Color.HSVToRGB(Random.value, 0.85f, 1f);
            sr.sortingOrder = dropSortingOrder + 120;
            lights.Add(lightObj);
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            for (int i = 0; i < lights.Count; i++)
            {
                GameObject lightObj = lights[i];
                if (lightObj == null)
                    continue;

                lightObj.transform.Rotate(0f, 0f, (i % 2 == 0 ? 160f : -140f) * Time.deltaTime);
                SpriteRenderer sr = lightObj.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    Color color = Color.HSVToRGB(Mathf.Repeat((elapsed * 0.45f) + i * 0.18f, 1f), 0.9f, 1f);
                    color.a = 0.62f + Mathf.Sin(elapsed * 8f + i) * 0.22f;
                    sr.color = color;
                }
            }

            yield return null;
        }

        for (int i = 0; i < lights.Count; i++)
        {
            if (lights[i] != null)
                Destroy(lights[i]);
        }
    }

    private IEnumerator DiscoScreenTint(float duration)
    {
        GameObject tint = new GameObject("Disco Screen Tint");
        SpriteRenderer sr = tint.AddComponent<SpriteRenderer>();
        sr.sprite = GetWhiteSprite();
        sr.sortingOrder = dropSortingOrder + 100;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            FitToCamera(tint.transform);
            Color color = Color.HSVToRGB(Mathf.Repeat(elapsed * 0.25f, 1f), 0.75f, 1f);
            color.a = 0.12f + Mathf.Abs(Mathf.Sin(elapsed * 5.5f)) * 0.12f;
            sr.color = color;
            yield return null;
        }

        if (tint != null)
            Destroy(tint);
    }

    private IEnumerator ConfettiRain(int count, float duration, Sprite sprite)
    {
        if (count <= 0 || duration <= 0f)
            yield break;

        if (sprite == null)
            sprite = GetWhiteSprite();

        GameObject root = new GameObject("Screen Confetti Overlay");
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
            GameObject piece = new GameObject("Confetti Piece");
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

    private static void FitToCamera(Transform target)
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            target.position = Vector3.zero;
            target.localScale = new Vector3(900f, 600f, 1f);
            return;
        }

        float depth = Mathf.Abs(cam.transform.position.z);
        Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(0f, 0f, depth));
        Vector3 topRight = cam.ViewportToWorldPoint(new Vector3(1f, 1f, depth));
        Vector3 center = (bottomLeft + topRight) * 0.5f;
        center.z = 0f;

        target.position = center;
        target.localScale = new Vector3(Mathf.Abs(topRight.x - bottomLeft.x) * 100f, Mathf.Abs(topRight.y - bottomLeft.y) * 100f, 1f);
    }

    private IEnumerator ConfettiBurst(Vector3 center, int count, float duration, Sprite sprite)
    {
        if (count <= 0 || duration <= 0f)
            yield break;

        if (sprite == null)
            sprite = GetWhiteSprite();
        List<GameObject> pieces = new List<GameObject>(count);
        Vector3[] velocities = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            GameObject piece = new GameObject("Confetti");
            piece.transform.position = center + (Vector3)Random.insideUnitCircle * Random.Range(0.15f, 1.1f);
            piece.transform.localScale = new Vector3(Random.Range(0.08f, 0.2f), Random.Range(0.04f, 0.12f), 1f);
            velocities[i] = new Vector3(Random.Range(-4.5f, 4.5f), Random.Range(2.5f, 7f), 0f);

            SpriteRenderer sr = piece.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = Color.HSVToRGB(Random.value, 0.9f, 1f);
            sr.sortingOrder = dropSortingOrder + 20;
            pieces.Add(piece);
        }

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float alpha = 1f - (t / duration);
            for (int i = 0; i < pieces.Count; i++)
            {
                GameObject piece = pieces[i];
                if (piece == null)
                    continue;

                velocities[i].y += -8f * Time.deltaTime;
                piece.transform.position += velocities[i] * Time.deltaTime;
                piece.transform.Rotate(0f, 0f, 540f * Time.deltaTime * (i % 2 == 0 ? 1f : -1f));

                SpriteRenderer sr = piece.GetComponent<SpriteRenderer>();
                if (sr != null)
                {
                    Color c = sr.color;
                    c.a = alpha;
                    sr.color = c;
                }
            }
            yield return null;
        }

        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i] != null)
                Destroy(pieces[i]);
        }
    }

    private List<AIWorkerAgent> ResolveTargets(VendingEventSO evt)
    {
        List<AIWorkerAgent> targets = new List<AIWorkerAgent>();

        if (evt.cosmeticType == VendingCosmeticType.AgentHat)
            return ResolveHatTargets();

        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null || crowd.Workers.Count == 0)
            return targets;

        switch (evt.scope)
        {
            case VendingEventScope.AllAgents:
                foreach (AIWorkerAgent worker in crowd.Workers)
                    if (worker != null)
                        targets.Add(worker);
                break;

            case VendingEventScope.NearbyAgents:
                AIWorkerAgent center = PickRandomWorker(crowd);
                if (center != null)
                {
                    crowd.GetNearbyWorkers(center.GetPosition(), evt.nearbyRadius, center, targets);
                    targets.Add(center);
                }
                break;

            default:
                AIWorkerAgent single = PickRandomWorker(crowd);
                if (single != null)
                    targets.Add(single);
                break;
        }

        return targets;
    }

    private List<AIWorkerAgent> ResolveHatTargets()
    {
        List<AIWorkerAgent> targets = new List<AIWorkerAgent>();
        if (hatCatalog == null)
            return targets;

        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd == null || crowd.Workers.Count == 0)
            return targets;

        List<AIWorkerAgent> qualified = new List<AIWorkerAgent>();
        foreach (AIWorkerAgent worker in crowd.Workers)
        {
            if (worker != null && hatCatalog.GetPool(worker.agentType) != null)
                qualified.Add(worker);
        }

        if (qualified.Count > 0)
            targets.Add(qualified[Random.Range(0, qualified.Count)]);

        return targets;
    }

    private static AIWorkerAgent PickRandomWorker(OfficeCrowdCoordinator2D crowd)
    {
        int count = crowd.Workers.Count;
        for (int attempt = 0; attempt < count; attempt++)
        {
            AIWorkerAgent worker = crowd.Workers[Random.Range(0, count)];
            if (worker != null)
                return worker;
        }
        return null;
    }

    private void SpawnDrop(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        Sprite sprite = PickDropSprite(evt);
        if (sprite == null)
            return;

        Vector3 position = ResolveDropPosition(targets);

        GameObject drop = new GameObject("Drop " + sprite.name);
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
        GameObject marker = new GameObject("Event Highlight");
        LineRenderer line = marker.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.loop = true;
        line.positionCount = 48;
        line.widthMultiplier = 0.045f;
        line.sortingOrder = dropSortingOrder + 180;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null)
            line.material = new Material(shader);

        Color color = new Color(1f, 0.92f, 0.25f, 1f);
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

    private static Sprite PickDropSprite(VendingEventSO evt)
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

        List<Sprite> available = new List<Sprite>();
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] != null)
                available.Add(sprites[i]);
        }

        if (available.Count == 0)
            return null;

        return available[Random.Range(0, available.Count)];
    }

    private void LoadCustomEvents()
    {
        VendingEventSO[] custom = Resources.LoadAll<VendingEventSO>("VendingEvents");
        if (custom == null || custom.Length == 0)
            return;

        HashSet<string> customIds = new HashSet<string>();
        foreach (VendingEventSO evt in custom)
        {
            if (evt != null && !string.IsNullOrEmpty(evt.eventId))
                customIds.Add(evt.eventId);
        }

        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] != null && customIds.Contains(events[i].eventId))
                events.RemoveAt(i);
        }

        foreach (VendingEventSO evt in custom)
        {
            if (evt != null)
                events.Add(evt);
        }
    }

    private static List<VendingEventSO> CreateDefaultEvents()
    {
        List<VendingEventSO> list = new List<VendingEventSO>();

        list.Add(NewEvent("coffee", "Coffee Drop", "Caffeine hits the office \u2014 frenzy mode!",
            VendingEventScope.OneNearestAgent, energy: 30f,
            speedMult: 2f, speedDur: 30f, focusDecayMult: 0f, decayDur: 30f, announce: 1.5f));

        list.Add(NewEvent("sugar_rush", "Sugar Rush", "A quick focus spike \u2014 then a crash.",
            VendingEventScope.OneNearestAgent, focus: 25f,
            speedMult: 1.5f, speedDur: 20f, postEnergy: -15f, announce: 1.4f));

        list.Add(NewEvent("hydration", "Hydration", "Sustained calm focus for everyone.",
            VendingEventScope.AllAgents, focus: 15f,
            energyDecayMult: 0.5f, decayDur: 60f, announce: 1.4f));

        list.Add(NewEvent("snack", "Snack Break", "A nearby snack lifts the mood.",
            VendingEventScope.NearbyAgents, energy: 10f, social: 20f, announce: 1.2f));

        VendingEventSO vendingDrop = NewEvent("vending_drop", "Vending Treat", "A coffee or snack appears for a moment.",
            VendingEventScope.OneNearestAgent, energy: 8f, focus: 4f, announce: 1.1f);
        vendingDrop.dropLifetime = 4f;
        vendingDrop.dropScale = 0.65f;
        vendingDrop.physicalProductId = "vending_treat";
        list.Add(vendingDrop);

        list.Add(NewEvent("healthy_lunch", "Healthy Lunch", "Office-wide wellness \u2014 needs decay slower.",
            VendingEventScope.AllAgents,
            energyDecayMult: 0.7f, focusDecayMult: 0.7f, socialDecayMult: 0.7f, decayDur: 90f, announce: 1.6f));

        list.Add(NewCosmetic("confetti", "Confetti Burst", "A visible celebration erupts across the office!",
            VendingRarity.Common, VendingCosmeticType.ConfettiBurst, confettiCount: 140, confettiDuration: 2.4f, announce: 1.2f));

        VendingEventSO disco = NewCosmetic("disco", "Office Disco", "Everyone dances under flashing lights!",
            VendingRarity.Epic, VendingCosmeticType.DiscoDance, confettiCount: 220, confettiDuration: 5f, energy: 8f, social: 25f, announce: 1.4f);
        disco.scope = VendingEventScope.AllAgents;
        disco.physicalProductId = "disco_pass";
        list.Add(disco);

        list.Add(NewCosmetic("tint_blue", "Cool Blue Skin", "An agent gets a cool new look.",
            VendingRarity.Rare, VendingCosmeticType.AgentTint,
            tint: new Color(0.4f, 0.6f, 1f), focus: 10f, announce: 1.4f));

        VendingEventSO plant = NewCosmetic("plant", "Office Plant", "A plant grows in the office upgrade corner.",
            VendingRarity.Epic, VendingCosmeticType.FurnitureUnlock,
            tint: new Color(0.3f, 0.6f, 0.35f), announce: 1.6f);
        plant.furnitureKind = UpgradeableFurnitureKind.Plant;
        plant.furnitureSocketId = "Plant";
        plant.physicalProductId = "office_plant";
        list.Add(plant);

        VendingEventSO lounge = NewCosmetic("lounge", "Lounge Upgrade", "The break area gets a more comfortable upgrade.",
            VendingRarity.Epic, VendingCosmeticType.FurnitureUnlock,
            tint: new Color(0.35f, 0.65f, 0.95f), announce: 1.6f);
        lounge.furnitureKind = UpgradeableFurnitureKind.Lounge;
        lounge.furnitureSocketId = "Lounge";
        lounge.physicalProductId = "lounge_upgrade";
        list.Add(lounge);

        list.Add(NewCosmetic("tint_gold", "Legendary Gold Skin", "A golden aura descends on one agent!",
            VendingRarity.Legendary, VendingCosmeticType.AgentTint,
            tint: new Color(1f, 0.84f, 0.2f), energy: 20f, focus: 20f, announce: 2f));

        list.Add(NewCosmetic("hat", "Random Hat", "One agent gets a stylish new hat!",
            VendingRarity.Rare, VendingCosmeticType.AgentHat, announce: 1.4f));

        return list;
    }

    private static VendingEventSO NewEvent(
        string id, string name, string description, VendingEventScope scope,
        float energy = 0f, float focus = 0f, float social = 0f, float prod = 0f,
        float speedMult = 1f, float speedDur = 0f,
        float energyDecayMult = 1f, float focusDecayMult = 1f, float socialDecayMult = 1f, float decayDur = 0f,
        float postEnergy = 0f, float postFocus = 0f, float postSocial = 0f, float postProd = 0f,
        float announce = 1.5f)
    {
        VendingEventSO evt = ScriptableObject.CreateInstance<VendingEventSO>();
        evt.eventId = id;
        evt.displayName = name;
        evt.description = description;
        evt.scope = scope;
        evt.nearbyRadius = 2f;
        evt.energyChange = energy;
        evt.focusChange = focus;
        evt.socialChange = social;
        evt.productivityChange = prod;
        evt.speedMultiplier = speedMult;
        evt.speedBuffDuration = speedDur;
        evt.energyDecayMultiplier = energyDecayMult;
        evt.focusDecayMultiplier = focusDecayMult;
        evt.socialDecayMultiplier = socialDecayMult;
        evt.decayOverrideDuration = decayDur;
        evt.postEnergyChange = postEnergy;
        evt.postFocusChange = postFocus;
        evt.postSocialChange = postSocial;
        evt.postProductivityChange = postProd;
        evt.announceDuration = announce;
        evt.dropLifetime = 3f;
        evt.dropScale = 1f;
        evt.interactionDirection = InteractionDirection.PhysicalToVirtual;
        evt.physicalProductId = id;
        evt.targetUserId = "user_001";
        return evt;
    }

    private static VendingEventSO NewCosmetic(
        string id, string name, string description,
        VendingRarity rarity, VendingCosmeticType cosmeticType,
        Color? tint = null, int confettiCount = 24, float confettiDuration = 1.2f,
        float energy = 0f, float focus = 0f, float social = 0f, float prod = 0f,
        float announce = 1.5f)
    {
        VendingEventSO evt = NewEvent(id, name, description, VendingEventScope.OneNearestAgent,
            energy: energy, focus: focus, social: social, prod: prod, announce: announce);
        evt.rarity = rarity;
        evt.cosmeticType = cosmeticType;
        evt.agentTint = tint ?? Color.white;
        evt.confettiCount = confettiCount;
        evt.confettiDuration = confettiDuration;
        evt.interactionDirection = cosmeticType == VendingCosmeticType.AgentHat
            ? InteractionDirection.Bidirectional
            : InteractionDirection.PhysicalToVirtual;
        evt.offlineReward = new OfflineCouponReward
        {
            rewardType = OfflineRewardType.DiscountCoupon,
            couponId = "NEXT_DRINK_20",
            displayName = "20% Next Drink",
            description = "Reward issued from a virtual office event."
        };
        return evt;
    }
}
