# LLM runtime setup

The office episode director uses remote OpenAI-compatible providers. It does not
require Ollama or any other local model process.

Configure keys once for the current Windows user:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Configure-LLMKeys.ps1
```

Configure both keys for provider rotation and failover:

- `GROQ_API_KEY` powers `openai/gpt-oss-20b`.
- `GEMINI_API_KEY` powers `gemini-3.5-flash-lite`.

The script stores the values in the Windows user environment, never in this
repository. Exit and reopen Unity after changing keys so an older key inherited
by the Editor process cannot take precedence.

## Entering a key at runtime (private deployed builds)

A browser or a bare Windows player may have no environment variables to configure.
Hover the **WORLD CONTROL** corner panel in the game, paste a Groq or Gemini
key into the `LLM API KEY` field, and press **Apply**. The key is kept in
memory for the current process and takes precedence over the environment
variable. It is cleared when the player reloads or exits and is not written to
`PlayerPrefs`. Use **Test** to verify the connection. **Stop** freezes
the simulation, **Start** resumes it, and **Restart world** wipes the save and
returns every worker to a fresh day one.

Do not use this runtime key field for a public WebGL deployment. Browser users
can inspect outbound requests and recover the key. See `Docs/DEPLOYMENT.md` for
the required server-side proxy boundary.

If no key is reachable by either path at startup, the runtime logs
`[LLMBrainService] API key is empty` and the director shows a panel hint until
a key is applied.

No hosted API is unlimited. Using both providers distributes requests and
provides failover, but keys on the same provider account can still share one
quota. The runtime minimizes usage by generating ten reusable beats per episode
request and by allowing only one new routine conversation every 12 seconds.
It does not run one planning request per character; physical action sequences
are included in the shared episode pack.

At runtime, look for:

```text
[LLMBrainService] ... remoteEpisodeProviders=2
[Episode director] buffered ... beats from ...
[Episode started] conversation: ...
```

If neither key is available, utility movement and physical interactions continue
without model-authored scenes. The director retries after a cooldown instead of
blocking agents or repeatedly consuming requests.

On Windows, the game uses the operating system HTTP/certificate stack for
remote LLM calls. A certificate or SSL failure opens a five-minute provider
cooldown rather than producing repeated retries. HTTP `429` still means the
provider's own rate or token quota was reached.

The Windows HTTP client follows the current system proxy. If Windows points to
a local proxy application such as Clash Verge, start that application and
enable its System Proxy option before launching Unity. A stopped local proxy
will make Gemini time out, while bypassing the proxy can cause Groq to return
`403 Forbidden` on restricted networks.

Routine dialogue and the episode provider pool can run two requests
concurrently, matching the maximum of two simultaneous conversations.
Agent-specific endpoints are checked first; otherwise Groq and Gemini rotate.
