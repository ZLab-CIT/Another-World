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
repository. Restart Play Mode after changing keys.

At runtime, look for:

```text
[LLMBrainService] ... remoteEpisodeProviders=2
[Episode director] buffered ... beats from ...
[Episode started] conversation: ...
```

If neither key is available, utility movement and physical interactions continue
without model-authored scenes. The director retries after a cooldown instead of
blocking agents or repeatedly consuming requests.
