using UnityEngine;

[CreateAssetMenu(menuName = "ZhipuOffice/Hat Catalog", fileName = "HatCatalog")]
public class HatCatalogSO : ScriptableObject
{
    [System.Serializable]
    public class HatEntry
    {
        public Sprite sprite;
        public Vector3 localOffset = Vector3.zero;
        public Vector3 sittingLocalOffset = Vector3.zero;
        public Vector3 localScale = new(0.5f, 0.5f, 1f);
    }

    [System.Serializable]
    public class HatPool
    {
        [Tooltip("Must match an AIWorkerAgent.agentType value.")]
        public string agentType = "";
        public HatEntry[] hats = new HatEntry[0];
    }

    [Header("Pools")]
    public HatPool[] pools = new HatPool[0];

    public HatPool GetPool(string agentType)
    {
        if (string.IsNullOrEmpty(agentType))
            return null;

        foreach (HatPool pool in pools)
        {
            if (pool != null && pool.agentType == agentType && HasUsableHat(pool))
                return pool;
        }

        return null;
    }

    public HatEntry PickRandomHat(string agentType)
    {
        HatPool pool = GetPool(agentType);
        if (pool == null)
            return null;

        for (int attempt = 0; attempt < pool.hats.Length; attempt++)
        {
            HatEntry hat = pool.hats[Random.Range(0, pool.hats.Length)];
            if (hat != null && hat.sprite != null)
                return hat;
        }

        foreach (HatEntry hat in pool.hats)
        {
            if (hat != null && hat.sprite != null)
                return hat;
        }

        return null;
    }

    private static bool HasUsableHat(HatPool pool)
    {
        if (pool.hats == null || pool.hats.Length == 0)
            return false;

        foreach (HatEntry hat in pool.hats)
        {
            if (hat != null && hat.sprite != null)
                return true;
        }

        return false;
    }
}