using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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
                StartCoroutine(ConfettiBurst(ResolveDropPosition(targets), evt.confettiCount, evt.confettiDuration, evt.confettiSprite));
                break;

            case VendingCosmeticType.AgentHat:
                if (targets.Count > 0 && targets[0] != null && hatCatalog != null)
                {
                    HatCatalogSO.HatEntry hat = hatCatalog.PickRandomHat(targets[0].agentType);
                    if (hat != null)
                        targets[0].ApplyHat(hat.sprite, hat.localOffset, hat.sittingLocalOffset, hat.localScale);
                }
                break;
        }
    }

    private void SpawnFurniture(VendingEventSO evt, List<AIWorkerAgent> targets)
    {
        OfficeGrid2D grid = FindFirstObjectByType<OfficeGrid2D>();
        Vector3 position = ResolveFurniturePosition(targets, grid);

        GameObject furniture;
        if (evt.furniturePrefab != null)
        {
            furniture = Instantiate(evt.furniturePrefab, position, Quaternion.identity);
        }
        else
        {
            furniture = new GameObject("Furniture_" + evt.displayName);
            furniture.transform.position = position;
            SpriteRenderer sr = furniture.AddComponent<SpriteRenderer>();
            Sprite furnitureSprite = evt.icon != null ? evt.icon : evt.dropSprite;
            sr.sprite = furnitureSprite != null ? furnitureSprite : GetWhiteSprite();
            sr.color = furnitureSprite != null
                ? Color.white
                : evt.agentTint == Color.white
                    ? new Color(0.3f, 0.6f, 0.35f)
                    : evt.agentTint;
            sr.sortingOrder = 10;
            furniture.AddComponent<BoxCollider2D>();
        }

        SetObstacleLayer(furniture);

        OfficeActionPoint actionPoint = furniture.GetComponent<OfficeActionPoint>();
        if (actionPoint == null)
        {
            actionPoint = furniture.AddComponent<OfficeActionPoint>();
            actionPoint.actionType = OfficeActionType.BreakSpot;
            actionPoint.useTime = 4f;
            actionPoint.baseScore = 12f;
            actionPoint.energyChange = 8f;
            actionPoint.socialChange = 12f;
            actionPoint.facing = OfficeFacingDirection.Down;
            actionPoint.singleUserTargetOffset = new Vector2(0f, -0.6f);
        }

        if (grid != null)
            grid.Rebuild();

        OfficeCrowdCoordinator2D crowd = OfficeCrowdCoordinator2D.Instance;
        if (crowd != null)
        {
            foreach (AIWorkerAgent worker in crowd.Workers)
            {
                if (worker != null)
                    worker.RefreshActionPoints();
            }
        }
    }

    private Vector3 ResolveFurniturePosition(List<AIWorkerAgent> targets, OfficeGrid2D grid)
    {
        Vector3 basePos = targets.Count > 0 && targets[0] != null
            ? targets[0].GetPosition()
            : Vector3.zero;
        basePos += new Vector3(Random.Range(-1f, 1f), -1.2f, 0f);

        if (grid != null && grid.TryFindNearestWalkable(basePos, 0.4f, out Vector2 walkable))
            return new Vector3(walkable.x, walkable.y, 0f);

        return basePos;
    }

    private static void SetObstacleLayer(GameObject obj)
    {
        OfficeGrid2D grid = FindFirstObjectByType<OfficeGrid2D>();
        if (grid == null)
            return;

        int mask = grid.obstacleMask.value;
        for (int i = 0; i < 32; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                obj.layer = i;
                return;
            }
        }
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
            piece.transform.position = center + (Vector3)Random.insideUnitCircle * 0.15f;
            piece.transform.localScale = Vector3.one * Random.Range(0.08f, 0.16f);
            velocities[i] = new Vector3(Random.Range(-2.5f, 2.5f), Random.Range(2f, 5f), 0f);

            SpriteRenderer sr = piece.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = Color.HSVToRGB(Random.value, 0.9f, 1f);
            sr.sortingOrder = dropSortingOrder + 10;
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

                velocities[i].y += -9f * Time.deltaTime;
                piece.transform.position += velocities[i] * Time.deltaTime;
                piece.transform.Rotate(0f, 0f, 360f * Time.deltaTime * (i % 2 == 0 ? 1f : -1f));

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
        if (evt.dropSprite == null)
            return;

        Vector3 position = ResolveDropPosition(targets);

        GameObject drop = new GameObject("Drop " + evt.dropSprite.name);
        drop.transform.position = position;
        drop.transform.localScale = Vector3.one * Mathf.Max(0.01f, evt.dropScale);

        SpriteRenderer renderer = drop.AddComponent<SpriteRenderer>();
        renderer.sprite = evt.dropSprite;
        renderer.color = Color.white;
        renderer.sortingOrder = dropSortingOrder;

        StartCoroutine(DestroyAfter(drop, Mathf.Max(0.1f, evt.dropLifetime)));
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

    private static Sprite GetWhiteSprite()
    {
        if (cachedWhiteSprite == null)
        {
            Texture2D tex = Texture2D.whiteTexture;
            cachedWhiteSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }
        return cachedWhiteSprite;
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

        list.Add(NewEvent("healthy_lunch", "Healthy Lunch", "Office-wide wellness \u2014 needs decay slower.",
            VendingEventScope.AllAgents,
            energyDecayMult: 0.7f, focusDecayMult: 0.7f, socialDecayMult: 0.7f, decayDur: 90f, announce: 1.6f));

        list.Add(NewCosmetic("confetti", "Confetti Burst", "A little celebration!",
            VendingRarity.Common, VendingCosmeticType.ConfettiBurst, confettiCount: 28, announce: 1.2f));

        list.Add(NewCosmetic("tint_blue", "Cool Blue Skin", "An agent gets a cool new look.",
            VendingRarity.Rare, VendingCosmeticType.AgentTint,
            tint: new Color(0.4f, 0.6f, 1f), focus: 10f, announce: 1.4f));

        list.Add(NewCosmetic("plant", "Office Plant", "A plant sprouts nearby \u2014 agents can relax at it.",
            VendingRarity.Epic, VendingCosmeticType.FurnitureUnlock,
            tint: new Color(0.3f, 0.6f, 0.35f), announce: 1.6f));

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
        return evt;
    }
}
