using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Unity.AI.Navigation;

public class ProjectBootstrapper : MonoBehaviour
{
    void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;

        SetupNavMesh();
    }

    void SetupNavMesh()
    {
        var surface = FindObjectOfType<NavMeshSurface>();
        if (surface == null) return;

        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;

        var rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (var root in rootObjects)
        {
            Add3DCollidersRecursive(root.transform);
        }

        surface.BuildNavMesh();

        Debug.Log("NavMesh baked with 3D colliders mapped from 2D colliders.");
    }

    static void Add3DCollidersRecursive(Transform t)
    {
        var col2D = t.GetComponent<BoxCollider2D>();
        var sr = t.GetComponent<SpriteRenderer>();

        if (col2D != null && sr != null && t.GetComponent<BoxCollider>() == null)
        {
            var col3D = t.gameObject.AddComponent<BoxCollider>();
            col3D.size = new Vector3(col2D.size.x, col2D.size.y, 0.1f);
            col3D.center = new Vector3(col2D.offset.x, col2D.offset.y, 0);
            col3D.isTrigger = col2D.isTrigger;
        }

        foreach (Transform child in t)
        {
            Add3DCollidersRecursive(child);
        }
    }
}
