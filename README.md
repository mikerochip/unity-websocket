# Mikerochip's Unity WebSocket

Easy-to-use, flexible WebSocket client as a simple MonoBehaviour.

* Easy-to-use, configurable `WebSocketConnection` MonoBehaviour
* Flexible config. URL is the only requirement, sane defaults otherwise.
* Does not force you into using `async/await` or Coroutines - code however you want
* It Just Works - public API prevents bad internal states
* Works with WebGL using bundled JavaScript lib `WebSocket.jslib`
* Works on other platforms using built-in `System.Net.WebSockets`

# Install

*Requires Unity 2019.1 with .NET 4.x Runtime or higher*

See official instructions for how to [Install a Package from a Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html)

The URL is https://github.com/mikerochip/unity-websocket.git

# Attribution

Based on (this repo)[https://github.com/endel/NativeWebSocket] by Endel Dreyer which was based on (this repo)[https://github.com/jirihybek/unity-websocket-webgl] by Jiri Hybek. See [license](./LICENSE.md) and [third party notices](./THIRD%20PARTY%20NOTICES.md) for full attribution.
