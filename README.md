# Another World

A Unity 2022.3 office simulation in which workers choose activities, navigate,
socialize, remember events, and react to a physical/virtual vending system.

## Runtime Design

The simulation does not depend on an LLM to keep running:

- `AIWorkerAgent` uses local utility scores, needs, availability, path cost, and
  per-character action preferences for physical decisions.
- `AgentConversationController` always has local conversation plans and replies.
- The LLM is an optional narrative layer for conversation continuations.
- LLM activity planning and generated conversation planning are off by default.
- Invalid, timed-out, rate-limited, or over-budget generations fall back locally.

Character identity is defined in `Assets/AgentProfiles`. Each profile contains
biography, traits, interests, speech style, relationships, memory seeds,
conversation starters, and token-free activity preferences.

## Hybrid Narrative

`OfficeEventDirector` schedules state-driven story arcs. Unity selects the
premise, stage, participants, current needs, goals, schedule, memories, and
relationship. One LLM request writes the complete five-turn scene, including
the opening and ending. It is played locally without a second generation.

Story definitions live in `Assets/Resources/OfficeStories` and require no code
changes. They contain facts, weights, privacy, timing, and consequences, not
authored dialogue. Initial premises include printer trouble, missing-snack
gossip, a suspicious shared-drive folder, a lunch plan, and a small office
victory.

Every arc has an introduction, follow-through, and resolution. The same
characters later gather again, and the next model prompt receives the factual
outcome of the earlier stage. Arc progress survives restarts. If generation is
unavailable, the director postpones the scene instead of repeating authored
story dialogue.

Physical vending events also contain a data-driven `characterReaction`. After a
purchase, an affected character immediately displays that reaction and tries to
find a coworker. Vending scenes have high LLM priority and receive a complete
prepared script when the budget and provider are available.

Numeric affinity, trust, tension, interaction count, and the last shared event
now influence partner selection and prompts. Explicit secrets, gossip, promises,
plans, favors, and conflicts change those values rather than merely adding a
line of text to memory.

## Token Controls

The application does not impose an hourly or daily request limit. The sample
scene runs locally, so it has no hosted token bill or provider token quota.
Prompts are still bounded because local context consumes RAM and CPU time.

Only one local inference request runs at a time. Foreground scenes wait briefly
for that slot; background planning immediately falls back to utility AI instead
of freezing an agent in a request queue. Conversations are generated in one
compact request and activity plans contain three actions.

Conversation and phone-call lines have no authored fallback. The model chooses
the subject and opening from personality, relationships, current needs, memory,
world events, and time. If generation is unavailable, the character abandons
that speech attempt rather than repeating a canned line.

## API Setup

The sample scene uses Ollama's OpenAI-compatible endpoint:

- `Base Url`: `http://localhost:11434/v1`
- `Model`: `qwen2.5:1.5b`
- `Api Key Environment Variable`: empty

Install/pull the model once with `ollama pull qwen2.5:1.5b`, then keep Ollama
running while the Unity simulation runs. The model is intentionally small for
this machine's available memory.

Other OpenAI-compatible providers can be configured on `LLMBrainService`:

- `Base Url`: endpoint root without `/chat/completions`
- `Model`: provider model identifier
- `Api Key Environment Variable`: environment variable containing the key

Remote endpoints that require a key still read it from the configured
environment variable. If that key is absent, the backend is not created and
local utility behavior remains active.

### Multiple Character Brains

`LLMBrainService.Agent Brains` is an optional routing list. Each entry contains:

- `Label`: readable brain name used in logs
- `Agent Ids`: stable ids routed to this brain
- `Base Url`: any OpenAI-compatible endpoint root
- `Api Key Environment Variable`: this provider's key name
- `Model`: model identifier for this brain

For example, Mingyun and Linli can share one Groq model, DianMei and Xiaomei can
share a second provider, while the remaining characters use local Ollama.
Agents sharing one entry also share its request gate. Different entries can run
at the same time.

Do not place literal API keys in the scene. Create environment variables such
as `WORLD_BRAIN_A_KEY` and `WORLD_BRAIN_B_KEY`, then enter those variable names
in the corresponding slots. Several keys governed by the same provider account
may still share one provider quota; separate brain slots improve capacity only
when their endpoints or effective quotas are independent.
Use the `LLMBrainService` component context menu:

- `Test Connection` verifies endpoint, key, model, and response parsing.

Log interpretation:

- `missing API key`: a remote endpoint was configured but Unity did not inherit
  its environment variable.
- HTTP `429`: the provider rejected the request; wait for its quota window or
  use another/local endpoint.

## Persistence

The versioned snapshot now saves every 60 seconds and on pause or quit:

- agent position, needs, productivity, and active buffs;
- current goal and last activity;
- episodic/social memory and recent dialogue;
- numeric relationships;
- active multi-stage story arcs;
- persistent simulated date and time.

The file is:

`Application.persistentDataPath/another-world-state.json`

On Windows with the current project settings this is normally under:

`%USERPROFILE%\AppData\LocalLow\DefaultCompany\ZhipuOffice`

Writes use a temporary file before replacing the current snapshot. Lists are
bounded to prevent unbounded prompt, memory, and disk growth.

## 24/7 Operation

`Run In Background` is enabled and the brain service persists across scene
loads. For unattended use, run a standalone build rather than the Unity Editor
and use an operating-system service or process supervisor to restart it after a
crash. Keep the API key in the supervisor's environment.

A production deployment still needs:

1. Furniture/cosmetic unlock persistence and restoration. Character runtime
   state and story state are saved, but spawned furniture is not yet rebuilt.
2. Dedicated home/sleep locations and sprites. The persistent clock already
   provides morning, work, lunch, afternoon, evening, and night priorities with
   bounded offline catch-up, but the current scene has no bedroom/home area.
3. Health checks, structured metrics, log rotation, crash reporting, and alerts.
4. Edit-mode tests for parsers/budgets and a multi-day play-mode soak test using
   a fake LLM backend.
5. A deterministic event journal or database if state must be recoverable after
   a machine failure rather than only a normal application restart.
6. The actual IoT/serverless transport. `PhysicalVirtualInteractionBridge`
   currently provides the local event contract, bounded event-id deduplication,
   accepted-event callback, and mock triggers, but there is no authenticated
   webhook receiver, durable event queue, WebSocket/SSE distribution, client
   acknowledgement, or server-side deduplication yet.

## Validation

Compile the scripts with:

```powershell
dotnet build Another-World.sln --no-restore
```

Unity regenerates `Assembly-CSharp.csproj` when new scripts are imported. Open
`Assets/Scenes/SampleScene.unity` and test both with and without an API key.
