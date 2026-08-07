# Deployment

## Recommended demo: Windows + InteractionHub

This is the only current deployment that preserves the complete prototype:
LLM conversations, persistent world state, visitor QR pages, voting, coupons,
and newspapers.

Prerequisites:

- Close the Unity Editor before running a command-line Unity build. Unity does
  not allow the same project to be opened by the Editor and batch mode.
- Configure `GROQ_API_KEY` and/or `GEMINI_API_KEY` on the machine running the
  simulation. API keys are not included in build output.

Build the player and publish the hub:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Build-Unity.ps1 -Target Windows
powershell -ExecutionPolicy Bypass -File .\Tools\Publish-InteractionHub.ps1
```

Run `Builds\InteractionHub\Start-Hub.ps1`, then run
`Builds\Windows\AnotherWorld.exe`. Keep the hub terminal open while the game is
running. For source-based development, use `Tools\Start-InteractionHub.ps1`.

The published hub is self-contained for Windows x64. Its SQLite data is
created beside the deployed hub process according to the hub's current data
configuration, so preserve that data directory between launches.

## WebGL status

WebGL Build Support is not installed in the current Unity 2022.3.62f3c1
installation. Add that module in Unity Hub before running:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\Build-Unity.ps1 -Target WebGL
```

A WebGL build must be served over HTTP(S); opening `index.html` directly is not
supported. The current browser build should be treated as a local/private
prototype only. It is not ready for public hosting for these reasons:

- A key entered in WebGL is sent directly from the browser to the LLM provider.
  It is visible to the browser user and provider CORS rules may reject requests.
- `INTERACTION_HUB_URL` and `INTERACTION_HUB_SECRET` environment variables do
  not exist in a browser. Embedding the Unity secret in JavaScript would expose it.
- The default hub address is `127.0.0.1`, which means the visitor's own device
  when the game is hosted remotely.

For a public WebGL release, put the WebGL files, InteractionHub, and an LLM
proxy behind one HTTPS origin. The proxy must own the provider keys, authorize
Unity endpoints server-side, enforce per-user rate limits, and forward only the
specific operations the game needs. Do not enable broad unauthenticated CORS on
the existing Unity endpoints.

## Release verification

Before distributing a build:

1. Run `dotnet build Another-World.sln --no-restore`.
2. Run `Tools\Test-InteractionHub.ps1` against a fresh test hub.
3. Build the Windows player with the script above.
4. Play for at least one simulated workday and verify movement, simultaneous
   conversations, vending, save/reload, QR scan, reward claim/use, and voting.
5. Review `Player.log` for exceptions, HTTP failures, and overlapping dialogue.

There are currently no automated Unity EditMode or PlayMode tests, so a passing
build is not proof that every simulation behavior is correct.
