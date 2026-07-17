using UnityEngine;

public class AgentEffects : MonoBehaviour
{
    private AIWorkerAgent owner;
    private float speedMultiplier = 1f;
    private float speedUntil = -1f;
    private float energyDecayMultiplier = 1f;
    private float focusDecayMultiplier = 1f;
    private float socialDecayMultiplier = 1f;
    private float decayOverrideUntil = -1f;

    public float EffectiveSpeedMultiplier => Time.time < speedUntil ? speedMultiplier : 1f;

    private void Awake()
    {
        owner = GetComponent<AIWorkerAgent>();
    }

    public void TickNeeds(float deltaTime)
    {
        bool overridden = Time.time < decayOverrideUntil;
        owner.energy = Mathf.Clamp(owner.energy - owner.energyDecay * (overridden ? energyDecayMultiplier : 1f) * deltaTime, 0f, 100f);
        owner.focus = Mathf.Clamp(owner.focus - owner.focusDecay * (overridden ? focusDecayMultiplier : 1f) * deltaTime, 0f, 100f);
        owner.social = Mathf.Clamp(owner.social - owner.socialDecay * (overridden ? socialDecayMultiplier : 1f) * deltaTime, 0f, 100f);
    }

    public void Apply(float energy, float focus, float social, float productivity)
    {
        owner.energy = Mathf.Clamp(owner.energy + energy, 0f, 100f);
        owner.focus = Mathf.Clamp(owner.focus + focus, 0f, 100f);
        owner.social = Mathf.Clamp(owner.social + social, 0f, 100f);
        owner.productivity = Mathf.Max(0f, owner.productivity + productivity);
        PhysicalVirtualInteractionBridge.Instance?.EvaluateProductivityMilestone();
    }

    public void ApplySpeed(float multiplier, float duration)
    {
        if (duration <= 0f)
            return;
        speedMultiplier = Mathf.Max(0f, multiplier);
        speedUntil = Time.time + duration;
    }

    public void ApplyDecayOverride(float energy, float focus, float social, float duration)
    {
        if (duration <= 0f)
            return;
        energyDecayMultiplier = Mathf.Max(0f, energy);
        focusDecayMultiplier = Mathf.Max(0f, focus);
        socialDecayMultiplier = Mathf.Max(0f, social);
        decayOverrideUntil = Time.time + duration;
    }
}
