using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class OfficeWorkerMotor2D : MonoBehaviour
{
    [Tooltip("How quickly the worker reaches walking speed.")]
    public float acceleration = 8f;
    [Tooltip("How quickly the worker stops.")]
    public float braking = 10f;

    private Rigidbody2D rb;
    private Vector2 velocity;

    public Vector2 Velocity => velocity;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        Collider2D bodyCollider = GetComponent<Collider2D>();
        bodyCollider.isTrigger = true;

        rb.bodyType = RigidbodyType2D.Kinematic;
        rb.gravityScale = 0f;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
    }

    public void Stop()
    {
        velocity = Vector2.zero;
    }

    public bool MoveToward(
        OfficeGrid2D grid,
        Vector2 target,
        float radius,
        float maxSpeed,
        float deltaTime)
    {
        if (grid == null || deltaTime <= 0f)
            return false;

        Vector2 position = rb.position;
        Vector2 offset = target - position;
        float distance = offset.magnitude;
        if (distance <= 0.001f)
        {
            Stop();
            return true;
        }

        float desiredSpeed = Mathf.Min(maxSpeed, distance / Mathf.Max(deltaTime, 0.001f));
        Vector2 desiredVelocity = offset / distance * desiredSpeed;
        float response = desiredVelocity.sqrMagnitude > velocity.sqrMagnitude ? acceleration : braking;
        velocity = Vector2.MoveTowards(velocity, desiredVelocity, response * deltaTime);

        Vector2 next = position + velocity * deltaTime;
        if (!grid.CanMoveBodyPhysically(position, next, radius)
            && !grid.CanRecoverBody(position, next, radius))
        {
            Stop();
            return false;
        }

        rb.MovePosition(next);
        return true;
    }
}
