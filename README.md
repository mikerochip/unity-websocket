# WebSocket Client for Unity

`WebSocketConnection` is an easy-to-use WebSocket client that Just Works

# Features

* `WebSocketConnection` is just a `MonoBehaviour`. You can:
   * Add it to a `GameObject` in the editor
   * Create one programmatically
* Flexible config
   * URL is the only requirement
   * Has sane defaults
* Flexible platform support
   * No external install requirements or dependencies
   * WebGL uses a bundled JavaScript lib `WebSocket.jslib`
   * Otherwise uses built-in `System.Net.WebSockets`
* Flexible runtime support
   * Use `async/await`, `Coroutine`s, `Update()`, whatever you want
   * Reusable: connect, disconnect, change URL, connect again, etc
* Simple to understand API
   * Only 2 (optional!) events: `StateChanged`, `MessageReceived`
   * Public API prevents you from corrupting an active connection

# Install

*Requires Unity 2019.1 with .NET 4.x Runtime or higher*

See official instructions for how to [Install a Package from a Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html)

The URL is https://github.com/mikerochip/unity-websocket.git

# Attribution

Based on [this repo](https://github.com/endel/NativeWebSocket) by Endel Dreyer, which was\
Based on [this repo](https://github.com/jirihybek/unity-websocket-webgl) by Jiri Hybek

See [license](./LICENSE.md) and [third party notices](./THIRD%20PARTY%20NOTICES.md) for full attribution.
