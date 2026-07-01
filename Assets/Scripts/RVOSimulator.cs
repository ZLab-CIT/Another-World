using System.Collections.Generic;
using UnityEngine;

public struct RVOLine
{
    public Vector2 point;
    public Vector2 direction;
}

public class RVOAgent
{
    public AIWorkerAgent owner;
    public Collider2D collider;
    public Vector2 position;
    public Vector2 velocity;
    public Vector2 prefVelocity;
    public Vector2 newVelocity;
    public float radius;
    public float maxSpeed;
    public bool active;
}

public class RVOSimulator : MonoBehaviour
{
    public static RVOSimulator Instance { get; private set; }

    [Header("Avoidance")]
    public float neighborDist = 2.5f;
    public int maxNeighbors = 12;
    public float timeHorizon = 1.5f;

    [Header("Static Obstacles (walls)")]
    [Tooltip("LayerMask of walls/furniture. Left empty -> copied from OfficeGrid2D at runtime. Must NOT include the agent layer.")]
    public LayerMask obstacleMask;
    [Tooltip("How far around each agent to sample walls each step.")]
    public float obstacleLookahead = 0.8f;
    [Tooltip("Time horizon for wall avoidance (usually larger than agent timeHorizon for smoother steering).")]
    public float obstacleTimeHorizon = 2f;
    [Tooltip("Number of radial raycasts used to sample walls per agent per step.")]
    public int obstacleRays = 16;

    private const float RVO_EPSILON = 1e-5f;

    private readonly List<RVOAgent> agents = new List<RVOAgent>();
    private readonly List<(float distSq, RVOAgent agent)> neighborBuf = new List<(float, RVOAgent)>();
    private readonly List<RVOLine> orcaLines = new List<RVOLine>();

    public IReadOnlyList<RVOAgent> Agents => agents;

    private void Awake()
    {
        Instance = this;

        if (obstacleMask.value == 0)
        {
#if UNITY_2023_1_OR_NEWER
            OfficeGrid2D grid = FindFirstObjectByType<OfficeGrid2D>();
#else
            OfficeGrid2D grid = FindObjectOfType<OfficeGrid2D>();
#endif
            if (grid != null)
                obstacleMask = grid.obstacleMask;
        }
    }

    public static RVOSimulator Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new GameObject(nameof(RVOSimulator));
        return go.AddComponent<RVOSimulator>();
    }

    public RVOAgent Register(AIWorkerAgent owner, float radius)
    {
        RVOAgent agent = new RVOAgent
        {
            owner = owner,
            radius = radius
        };

        // RVO is the sole authority for agent-to-agent avoidance, so disable
        // physical collisions between agents. They still collide with walls
        // (static colliders on other objects).
        Collider2D col = owner.GetComponent<Collider2D>();
        agent.collider = col;
        if (col != null)
        {
            foreach (RVOAgent existing in agents)
            {
                Collider2D existingCol = existing.owner.GetComponent<Collider2D>();
                if (existingCol != null)
                    Physics2D.IgnoreCollision(col, existingCol, true);
            }
        }

        agents.Add(agent);
        return agent;
    }

    public void Unregister(RVOAgent agent)
    {
        if (agent != null)
            agents.Remove(agent);
    }

    private void FixedUpdate()
    {
        if (agents.Count == 0)
            return;

        float dt = Time.fixedDeltaTime;

        // Sync live positions; stationary agents report zero velocity so moving
        // agents treat them as static obstacles (full responsibility).
        for (int i = 0; i < agents.Count; i++)
        {
            RVOAgent a = agents[i];
            a.position = a.owner.GetPosition();
            if (!a.active)
                a.velocity = Vector2.zero;
        }

        // Solve preferred -> collision-free velocity for every moving agent.
        for (int i = 0; i < agents.Count; i++)
        {
            RVOAgent a = agents[i];
            if (!a.active)
            {
                a.newVelocity = Vector2.zero;
                continue;
            }
            ComputeNewVelocity(a, dt);
        }

        // Integrate active agents through their owner rigidbody.
        for (int i = 0; i < agents.Count; i++)
        {
            RVOAgent a = agents[i];
            if (!a.active)
                continue;
            a.velocity = a.newVelocity;
            a.owner.ApplyRVOVelocity(a.newVelocity, dt);
        }
    }

    private void ComputeNewVelocity(RVOAgent a, float dt)
    {
        orcaLines.Clear();

        // Static obstacle (wall) lines first: these are hard, non-negotiable
        // constraints. They make wall-constrained agents unable to swerve into
        // a wall, so the LP forces the agent with room to yield instead.
        int obstLineCount = AddObstacleLines(a);

        float invTimeHorizon = 1f / timeHorizon;
        float neighborDistSq = neighborDist * neighborDist;

        neighborBuf.Clear();
        for (int i = 0; i < agents.Count; i++)
        {
            RVOAgent b = agents[i];
            if (b == a)
                continue;

            Vector2 relPos = b.position - a.position;
            float distSq = relPos.x * relPos.x + relPos.y * relPos.y;
            if (distSq < neighborDistSq)
                neighborBuf.Add((distSq, b));
        }

        neighborBuf.Sort((x, y) => x.distSq.CompareTo(y.distSq));
        int count = Mathf.Min(neighborBuf.Count, maxNeighbors);

        for (int i = 0; i < count; i++)
        {
            RVOAgent b = neighborBuf[i].agent;
            Vector2 relPos = b.position - a.position;
            float distSq = neighborBuf[i].distSq;
            float combinedRadius = a.radius + b.radius;
            float combinedRadiusSq = combinedRadius * combinedRadius;

            // Reciprocal (shared) responsibility vs. full avoidance of a
            // stationary agent that will not cooperate this step.
            float responsibility = b.active ? 0.5f : 1f;

            RVOLine line;
            Vector2 u;

            if (distSq > combinedRadiusSq)
            {
                // No collision: build the ORCA half-plane for this pair.
                Vector2 relVel = a.velocity - b.velocity;
                Vector2 w = relVel - invTimeHorizon * relPos;
                float wLenSq = w.x * w.x + w.y * w.y;
                float dot1 = w.x * relPos.x + w.y * relPos.y;

                if (dot1 < 0f && dot1 * dot1 > combinedRadiusSq * wLenSq)
                {
                    // Project on the cut-off circle.
                    float wLen = Mathf.Sqrt(wLenSq);
                    Vector2 unitW = wLen > RVO_EPSILON ? w / wLen : new Vector2(1f, 0f);
                    line.direction = new Vector2(unitW.y, -unitW.x);
                    u = (combinedRadius * invTimeHorizon - wLen) * unitW;
                }
                else
                {
                    // Project on the left or right leg of the cone.
                    float leg = Mathf.Sqrt(distSq - combinedRadiusSq);
                    if (Det(relPos, w) > 0f)
                    {
                        line.direction = new Vector2(
                            relPos.x * leg - relPos.y * combinedRadius,
                            relPos.x * combinedRadius + relPos.y * leg) / distSq;
                    }
                    else
                    {
                        line.direction = -new Vector2(
                            relPos.x * leg + relPos.y * combinedRadius,
                            -relPos.x * combinedRadius + relPos.y * leg) / distSq;
                    }

                    float dot2 = relVel.x * line.direction.x + relVel.y * line.direction.y;
                    u = dot2 * line.direction - relVel;
                }

                line.point = a.velocity + u * responsibility;
            }
            else
            {
                // Already colliding: use the time step to push apart this frame.
                float invTimeStep = 1f / dt;
                Vector2 relVel = a.velocity - b.velocity;
                Vector2 w = relVel - invTimeStep * relPos;
                float wLen = w.magnitude;
                Vector2 unitW = wLen > RVO_EPSILON ? w / wLen : new Vector2(1f, 0f);
                line.direction = new Vector2(unitW.y, -unitW.x);
                u = (combinedRadius * invTimeStep - wLen) * unitW;
                line.point = a.velocity + u * responsibility;
            }

            orcaLines.Add(line);
        }

        // Find the velocity closest to the preferred one that satisfies all
        // ORCA half-planes, clamped to max speed. Obstacle lines are hard.
        Vector2 newVel = a.prefVelocity;
        int lineFail = LinearProgram2(orcaLines, a.maxSpeed, a.prefVelocity, false, ref newVel);
        if (lineFail < orcaLines.Count)
            LinearProgram3(orcaLines, obstLineCount, lineFail, a.maxSpeed, ref newVel);

        // Avoidance can box an agent in (e.g. pressed against / just inside a
        // desk corner where rays hit several faces and the LP collapses to
        // ~zero). A dead stall freezes the agent until the stuck timer aborts
        // it. Instead, creep along the preferred direction so the grid-cleared
        // path leads it out instead of leaving it frozen on the collider.
        if (a.prefVelocity.sqrMagnitude > 1e-4f && newVel.sqrMagnitude < 1e-4f)
            newVel = a.prefVelocity.normalized * Mathf.Min(a.maxSpeed, 0.5f);

        a.newVelocity = newVel;
    }

    private int AddObstacleLines(RVOAgent a)
    {
        if (obstacleMask.value == 0 || a.collider == null || obstacleRays <= 0)
            return 0;

        int startCount = orcaLines.Count;
        float reach = obstacleLookahead;

        for (int i = 0; i < obstacleRays; i++)
        {
            float angle = (i / (float)obstacleRays) * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

            RaycastHit2D hit = Physics2D.Raycast(a.position, dir, reach, obstacleMask);
            if (hit.collider == null || hit.collider == a.collider)
                continue;

            Vector2 n = hit.normal;
            if (n.sqrMagnitude < RVO_EPSILON)
                continue;
            n.Normalize();

            // Half-plane: velocity into the wall is bounded so the agent's
            // radius never penetrates within obstacleTimeHorizon.
            float bound = (a.radius - hit.distance) / obstacleTimeHorizon;
            RVOLine line;
            line.direction = new Vector2(n.y, -n.x);
            line.point = n * bound;
            orcaLines.Add(line);
        }

        return orcaLines.Count - startCount;
    }

    private static float Det(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static float AbsSq(Vector2 v)
    {
        return v.x * v.x + v.y * v.y;
    }

    private static bool LinearProgram1(
        List<RVOLine> lines, int lineNo, float radius,
        Vector2 optVelocity, bool directionOpt, ref Vector2 result)
    {
        float dotProduct = lines[lineNo].point.x * lines[lineNo].direction.x
                         + lines[lineNo].point.y * lines[lineNo].direction.y;

        float discriminant = dotProduct * dotProduct + radius * radius - AbsSq(lines[lineNo].point);
        if (discriminant < 0f)
        {
            result = Vector2.zero;
            return false;
        }

        float sqrtDisc = Mathf.Sqrt(discriminant);
        float tLeft = -dotProduct - sqrtDisc;
        float tRight = -dotProduct + sqrtDisc;

        for (int i = 0; i < lineNo; i++)
        {
            float denominator = Det(lines[lineNo].direction, lines[i].direction);
            float numerator = Det(lines[i].direction, lines[i].point - lines[lineNo].point);

            if (Mathf.Abs(denominator) <= RVO_EPSILON)
            {
                // Lines are (almost) parallel.
                if (numerator < 0f)
                {
                    result = Vector2.zero;
                    return false;
                }
                continue;
            }

            float t = numerator / denominator;
            if (denominator >= 0f)
            {
                if (t < tRight)
                    tRight = t;
            }
            else
            {
                if (t > tLeft)
                    tLeft = t;
            }

            if (tLeft > tRight)
            {
                result = Vector2.zero;
                return false;
            }
        }

        if (directionOpt)
        {
            if (optVelocity.x * lines[lineNo].direction.x + optVelocity.y * lines[lineNo].direction.y > 0f)
                result = lines[lineNo].point + tRight * lines[lineNo].direction;
            else
                result = lines[lineNo].point + tLeft * lines[lineNo].direction;
        }
        else
        {
            float t = lines[lineNo].direction.x * (optVelocity.x - lines[lineNo].point.x)
                    + lines[lineNo].direction.y * (optVelocity.y - lines[lineNo].point.y);

            if (t < tLeft)
                result = lines[lineNo].point + tLeft * lines[lineNo].direction;
            else if (t > tRight)
                result = lines[lineNo].point + tRight * lines[lineNo].direction;
            else
                result = lines[lineNo].point + t * lines[lineNo].direction;
        }

        return true;
    }

    private static int LinearProgram2(
        List<RVOLine> lines, float radius, Vector2 optVelocity,
        bool directionOpt, ref Vector2 result)
    {
        if (directionOpt)
        {
            result = optVelocity * radius;
        }
        else if (AbsSq(optVelocity) > radius * radius)
        {
            result = optVelocity.normalized * radius;
        }
        else
        {
            result = optVelocity;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (Det(lines[i].direction, lines[i].point - result) > 0f)
            {
                // Result violates constraint i; recompute against lines [0..i].
                Vector2 tempResult = result;
                if (!LinearProgram1(lines, i, radius, optVelocity, directionOpt, ref result))
                {
                    result = tempResult;
                    return i;
                }
            }
        }

        return lines.Count;
    }

    private static void LinearProgram3(
        List<RVOLine> lines, int numObstLines, int beginLine,
        float radius, ref Vector2 result)
    {
        float distance = 0f;

        for (int i = beginLine; i < lines.Count; i++)
        {
            if (Det(lines[i].direction, lines[i].point - result) > distance)
            {
                var projLines = new List<RVOLine>(numObstLines + i);
                for (int j = 0; j < numObstLines; j++)
                    projLines.Add(lines[j]);

                for (int j = numObstLines; j < i; j++)
                {
                    RVOLine line;
                    float determinant = Det(lines[i].direction, lines[j].direction);

                    if (Mathf.Abs(determinant) <= RVO_EPSILON)
                    {
                        if (lines[i].direction.x * lines[j].direction.x
                            + lines[i].direction.y * lines[j].direction.y > 0f)
                            continue;

                        line.point = 0.5f * (lines[i].point + lines[j].point);
                    }
                    else
                    {
                        line.point = lines[i].point
                            + (Det(lines[j].direction, lines[i].point - lines[j].point) / determinant)
                            * lines[i].direction;
                    }

                    Vector2 dir = lines[j].direction - lines[i].direction;
                    line.direction = dir.sqrMagnitude > RVO_EPSILON ? dir.normalized : Vector2.right;
                    projLines.Add(line);
                }

                Vector2 tempResult = result;
                if (LinearProgram2(projLines, radius,
                        new Vector2(-lines[i].direction.y, lines[i].direction.x),
                        true, ref result) < projLines.Count)
                {
                    result = tempResult;
                }

                distance = Det(lines[i].direction, lines[i].point - result);
            }
        }
    }
}
