using UnityEditor;
using UnityEngine;

public static class InteractionHubDebugMenu
{
    [MenuItem("Another World/Interaction Hub/Publish Test Decision _F8")]
    private static void PublishTestDecision()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Enter Play mode before publishing a test decision.");
            return;
        }
        OfficeInteractionHubClient client = OfficeInteractionHubClient.Instance;
        if (client == null)
        {
            Debug.LogWarning("The InteractionHub client is not running yet.");
            return;
        }
        client.PublishDecision(new OfficeAudienceDecision
        {
            decisionId = "editor-test-" + System.Guid.NewGuid().ToString("N"),
            authorAgentId = "mingyun",
            authorDisplayName = "Mingyun",
            question = "Which direction should the next tiny prototype explore?",
            durationSeconds = 180,
            options = new[]
            {
                new OfficeAudienceDecisionOption
                {
                    optionId = "quiet",
                    label = "A quiet mystery",
                    reaction = "That could reward careful attention.",
                    consequence = "Mingyun explores a quiet mystery prototype."
                },
                new OfficeAudienceDecisionOption
                {
                    optionId = "playful",
                    label = "A playful challenge",
                    reaction = "That sounds delightfully chaotic.",
                    consequence = "Mingyun explores a playful challenge prototype."
                }
            }
        });
        Debug.Log("Published a test-only visitor decision. Refresh the phone page.");
    }
}
