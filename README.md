# Mikerochip's Unity WebSocket

Easy-to-use, flexible WebSocket client as a MonoBehaviour called `WebSocketConnection`

* Add `WebSocketConnection` to a GameObject in the editor, or add one programmatically
* Flexible config
   * URL is the only requirement
   * Sane defaults
* It Just Works
   * Does not force you into using `async/await` or `Coroutine`
   * Buffers state changes and messages, sends simple events
   * Public API prevents altering active connections
* Supports WebGL and any platform that implements `System.Net.WebSockets`
   * WebGL uses a bundled JavaScript lib `WebSocket.jslib`
   * Other platforms use `ClientWebSocket`, implementation complexities are abstracted away for you

# Install

*Requires Unity 2019.1 with .NET 4.x Runtime or higher*

See official instructions for how to [Install a Package from a Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html)

The URL is https://github.com/mikerochip/unity-websocket.git

# Attribution

Based on [this repo](https://github.com/endel/NativeWebSocket) by Endel Dreyer, which was\
Based on [this repo](https://github.com/jirihybek/unity-websocket-webgl) by Jiri Hybek

See [license](./LICENSE.md) and [third party notices](./THIRD%20PARTY%20NOTICES.md) for full attribution.
