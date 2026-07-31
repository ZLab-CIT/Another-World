# Digital Terrarium Presentation

Use three status labels consistently:

- **Implemented**: present and active in the current Unity prototype.
- **Prototype**: Unity-side contract or logic exists, but no production integration.
- **Roadmap**: proposed work that is not currently implemented.

Do not use code line numbers in the slides. They change whenever the project is
refactored. Use component names and architecture diagrams instead.

## Slide 1: Title

**Digital Terrarium**

**A Persistent 2D Autonomous Office Connected to Physical Events**

Unity Prototype, AI Agent Architecture, IoT Integration Roadmap

Speaker note: Say "persistent prototype", not "production-ready 24/7 system."

## Slide 2: Project Vision

**Goal**

Create a persistent virtual office that continues to evolve without direct
player control and reacts visibly to ordinary physical-office activity.

**Experience**

- Six AI researcher characters work, rest, socialize, remember events, and form relationships.
- A real vending purchase becomes an immediate event inside the 2D office.
- The vending display and office display eventually show the same synchronized event.
- Returning users can observe consequences, ongoing social threads, and environmental changes.

**Research Question**

Can passive real-world actions create meaningful engagement with a persistent
digital environment?

## Slide 3: Core Interaction Loop

1. A user buys a physical drink or snack.
2. The vending system emits a transaction event containing event ID, product ID, user ID, and time.
3. A backend validates and publishes the event to connected clients.
4. Unity maps the product ID to a data-driven virtual event.
5. The world shows a drop, buff, cosmetic, furniture upgrade, or celebration.
6. AI characters react through thoughts, actions, conversations, and memory.
7. The resulting state is saved and remains visible in later sessions.

**Current status**

- **Implemented**: Steps 4-6 and partial state persistence in Unity.
- **Prototype**: Local transaction event contract, mock triggers, deduplication, and callbacks.
- **Roadmap**: Physical hardware ingestion, backend distribution, two-client synchronization, and acknowledgements.

## Slide 4: Current and Target Architecture

**Current Unity Prototype**

```text
Utility AI + FSM ----> movement, needs, action selection
         |
Remote LLM pool -----> buffered narrative episode pack
         |
Episode Director ----> validated thoughts, actions, calls, conversations
         |
World State Store ---> JSON snapshot and restart recovery
         |
Vending Dispatcher --> ScriptableObject event mapping and visual effects
```

**Target Production Architecture**

```text
Vending IoT
    |
Authenticated webhook
    |
Durable queue + idempotency store
    |
Authoritative event/state service
    |
WebSocket or SSE fan-out
    +--------------------+
    |                    |
Vending display     Office display
```

Speaker note: "Serverless" and "dual-client synchronization" describe the target
architecture, not the current Unity implementation.

## Slide 5: Implemented Autonomous Agent System

- Six data-driven character profiles: DianMei, Linli, Mingyun, Pengpeng, Xiaomei, and XiaoXia.
- Local utility AI evaluates energy, focus, social need, productivity, schedule phase, personality preference, occupancy, and route cost.
- FSM execution separates thinking, movement, and action states.
- Custom grid pathfinding uses A*-style search, obstacle clearance, turn cost, path smoothing, and destination replanning.
- Engine support includes desks, coffee, vending, breaks, chat, meetings, printer, whiteboard, plant care, phone calls, walking, thinking, phone checking, colleague approach, and custom actions.
- The sample scene currently configures desks, coffee, vending, break, chat, and printer points; other action types require additional scene objects.
- Slot reservation, multi-person social spots, item pickup/placement, movement recovery, and 2-4-person conversations are implemented.

## Slide 6: LLM Narrative Architecture

**Active approach: one request creates multiple reusable scenes**

- The episode planner requests a pack of 6-10 executable beats.
- At least 70% are conversations; a pack also includes one external phone call.
- Beats may include private thoughts and physical actions before or after dialogue.
- Context is compact: personality, current visible state, relationships, recent memory, established facts, and recent topics.
- Validation rejects unknown agents, unavailable scene actions, unsupported objects, numeric stat dialogue, repeated snack mysteries, and promises without matching actions.
- The episode director buffers valid beats and executes them locally over time.
- Groq and Gemini rotate as remote providers with retries, output limits, cooldowns, and circuit breakers.

**Important design choice**

Utility AI keeps the office moving when the LLM is unavailable. Model-authored
dialogue pauses instead of repeating hardcoded fallback conversations.

## Slide 7: Character, Memory, and Social Life

- Profiles store biography, role, birthday, traits, interests, speech style, relationship notes, memory seeds, and action preferences.
- Social memory supports secrets, gossip, promises, favour requests, completed favours, plans, invitations, and conflicts.
- Pair relationships track affinity, trust, tension, interaction count, last event, and last interaction time.
- Recent topics, openings, and utterances reduce obvious repetition.
- Conversations support two to four participants and allow late participants at shared social points.
- Commitments can become later physical actions, including obtaining or delivering coffee and snacks.
- Speech bubbles, thought bubbles, twelve contextual emotion sprites, sitting states, and cosmetic hats provide visible feedback.

Speaker note: There is no visible "affinity meter" yet. Relationship values are
internal simulation data.

## Slide 8: Physical-Virtual Vending Prototype

**Implemented in Unity**

- ScriptableObject event database maps `physicalProductId` values to virtual effects without hardcoded SKU branches.
- Events include coffee, snacks, hydration, healthy lunch, sugar rush, confetti, hats, plants, and lounge upgrades.
- Weighted gacha rarity supports Common, Rare, Epic, and Legendary outcomes.
- Event targeting supports nearest agent, nearby agents, or the whole office.
- Vending events can trigger drops, buffs, need-decay changes, furniture, hats, announcements, character reactions, and follow-up narrative context.

**Prototype boundary**

- `PhysicalInteractionEvent` provides an event envelope and local duplicate-event protection.
- Coupon reward data, productivity milestone rules, local logs, and announcement UI exist.
- No real vending hardware, webhook receiver, durable queue, client synchronization, QR code, receipt, email, or mini-program coupon delivery exists yet.

## Slide 9: Persistence and 24/7 Readiness

**Implemented**

- `Run In Background` is enabled.
- Versioned JSON snapshots save every 60 seconds and on pause or quit.
- Temporary-file replacement reduces the risk of partial snapshot writes.
- Saved data includes position, needs, productivity, buffs, goals, memories, relationships, story arcs, episode reserve, world events, and simulated time.
- A persistent clock defines Night, Morning, Work, Lunch, Afternoon, and Evening phases.
- Restart recovery includes bounded offline catch-up of up to eight simulated hours.

**Not production-ready**

- No dedicated headless/service build packaging or automated deployment pipeline.
- No watchdog, health endpoint, structured metrics, log rotation, crash reporting, or alerting.
- No durable server-side event journal or database recovery.
- Furniture and cosmetic world changes are not fully restored.
- No automated parser tests, integration tests, or multi-day soak test.

## Slide 10: Research and Commercial Evaluation Plan

These are evaluation hypotheses, not currently implemented analytics.

- **Engagement**: purchase-to-reaction latency, post-purchase dwell time, and repeated viewing.
- **Retention**: repeat purchase frequency and return rate for users who unlock persistent digital items.
- **Conversion**: A/B comparison of purchase rate and SKU mix with the experience enabled or disabled.
- **World impact**: which events create conversations, relationship changes, or later actions.
- **Reliability and cost**: successful event delivery, LLM success rate, fallback time, token use per displayed scene, and uptime.

**Required before a real study**

- Consent, pseudonymous user IDs, retention policy, access control, and privacy review.
- A shared analytics event schema and authoritative timestamping.
- A dashboard that joins vending transactions, display acknowledgements, and simulation outcomes.

## Slide 11: Technical Debt and Refactoring Priorities

1. **AIWorkerAgent, approximately 2,850 lines**

   Split decision policy, activity execution, social orchestration, item handling,
   emotion handling, and runtime persistence adapters.

2. **OfficeEventDirector, approximately 1,680 lines**

   Split episode buffering/execution, ambient story arcs, vending reactions, and
   birthday coordination.

3. **LLMActivityPlanner, approximately 1,655 lines**

   Move `OfficeEpisodePlanner` into its own file. Decide whether the disabled
   per-agent batch planner is still part of the product; remove it if the central
   episode architecture is now definitive.

4. **LLMBrainService, approximately 1,238 lines**

   It is no longer the original god class, but still combines provider routing,
   profile registry, relationships, simulated time, persistence coordination,
   and episode failover.

5. **VendingEventDispatcher, approximately 736 lines**

   Separate event selection, effect application, runtime decoration spawning,
   confetti rendering, and announcement presentation.

6. **Runtime `Ensure()` singletons**

   Prefer explicit scene composition or injected service references for
   testability. Do not standardize on hidden runtime object creation merely for
   consistency.

**Already completed refactoring**

- Conversation planning is in `LLMConversationPlanner`.
- Activity and episode planning are outside `LLMBrainService`.
- Shared NLP helpers are consolidated in `TextUtils`.
- Large authored fallback-dialogue generators are no longer in the conversation controller.

## Slide 12: Prioritized Roadmap and Conclusion

**Phase 1: Make the prototype measurable and stable**

- Add fake-provider tests, parser tests, event mapping tests, and a 24-hour soak test.
- Add structured logs, LLM latency/success metrics, movement/activity metrics, and crash recovery.
- Persist furniture, cosmetics, unlock inventory, and processed event IDs.

**Phase 2: Connect the physical system**

- Build authenticated webhook ingestion, durable event storage, idempotency, and WebSocket/SSE fan-out.
- Define client snapshot, reconnect, acknowledgement, and replay behaviour.
- Connect a real vending transaction sandbox before connecting production hardware.

**Phase 3: Improve the audience experience**

- Add an event timeline, character detail panel, world clock, and observation camera.
- Add more configured action points such as a whiteboard, meeting table, window, bookshelf, and plant.
- Add visible long-term consequences and environmental progression.

**Conclusion**

The project is already a functioning autonomous-office and narrative prototype.
The next milestone is not "more AI." It is reliable event infrastructure,
measurable behaviour, persistent consequences, and a polished demonstration of
the physical-to-virtual loop.

## Recommended Live Demo

1. Start the scene and show that agents move using utility AI before an LLM scene begins.
2. Show a generated two-person or group conversation with thoughts and emotions.
3. Trigger a mock physical sale and show product mapping, announcement, visual drop or buff, and character reaction.
4. Trigger a gacha event and show a hat or furniture outcome.
5. Stop and restart Play mode, then show restored character state, memory, relationships, time, and episode reserve.
6. End with the architecture diagram and clearly identify the missing real webhook and dual-client layer.

## Visual Guidance

- **Slide 1**: Full-bleed office screenshot with the title over a dark translucent panel.
- **Slide 2**: Character montage on one side and a single-sentence vision statement on the other.
- **Slide 3**: Seven-step circular flow using vending, cloud, monitor, item, character, memory, and return icons.
- **Slide 4**: Use the two architecture diagrams; do not replace them with paragraphs.
- **Slide 5**: Show the office grid/path overlay and small icons for the configured action points.
- **Slide 6**: Show one episode pack as a timeline: conversation, action, phone call, group conversation.
- **Slide 7**: Show a relationship graph and one character profile card, not raw JSON.
- **Slide 8**: Show a real-product-to-virtual-event mapping example and a rarity strip.
- **Slide 9**: Use a two-column "Implemented / Production Gap" comparison.
- **Slide 10**: Use five metric cards and label the slide "Evaluation Plan."
- **Slide 11**: Use a horizontal bar chart of the five largest classes with proposed split labels.
- **Slide 12**: Use a three-phase roadmap and finish with a vending-triggered world screenshot.

For a 10-minute presentation, spend approximately 40-50 seconds per slide and
use the live demo instead of reading implementation bullet lists aloud.

## Claims Removed or Corrected

- Removed "fully implemented serverless architecture"; it is a roadmap design.
- Removed "near-zero latency"; no end-to-end hardware measurement exists.
- Removed "comprehensive analytics tools"; metrics are an evaluation plan.
- Removed "LLM long-term daily scheduling"; the active system uses short buffered narrative episodes.
- Removed "character-specific opening templates and fallback dialogue"; the current design deliberately avoids authored dialogue fallbacks.
- Replaced "affinity meter" with internal relationship state.
- Replaced "full-screen announcement" with event announcement card.
- Replaced "physical-to-virtual pipeline fully implemented" with Unity-side pipeline prototype.
- Replaced "coupon module fully coded" with reward contract and local presentation prototype.
- Removed "no save system", "no global time", "all state resets", and "README is empty"; all were false.
- Replaced the obsolete three-prompt slide with the active episode-pack architecture.
- Replaced the obsolete refactoring ranking with current file sizes and responsibilities.
