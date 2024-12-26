# WebSocket Client for Unity

[![Unity Version](https://img.shields.io/badge/Unity-2019.4%2B-blueviolet?logo=unity)](https://unity.com/releases/editor/archive)

`WebSocketConnection` is an easy-to-use WebSocket client for Unity that Just Works

# Features

* Easy to use
   * `WebSocketConnection` is just a `MonoBehaviour`
   * Using `async/await` is optional: event listeners, coroutines, and polling are supported
   * Doesn't force `#if` for WebGL: no conditional-compilation required
   * Public API prevents you from corrupting an active connection
   * Reusable: connect, disconnect, change URL, connect again from one `WebSocketConnection`
* Wide platform support
   * No external install requirements or dependencies
   * `string` is treated as text, `byte[]` as binary (some servers enforce this)
   * Custom ping-pong support, write once for Web and non-Web
   * Web uses a `.jslib` JavaScript library, non-Web builds use the built-in `System.Net.WebSockets`
   * Includes support for `WebAssembly.Table` (Unity 6+)
* Flexible config
   * URL is the only required config
   * Sane defaults
   * Optionally set subprotocols, max send, and max receive bytes
   * Optionally configure ping-pongs to happen one after another, enabling RTT tracking

# Install

See official instructions for how to [Install a Package from a Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html). The URL is

`https://github.com/mikerochip/unity-websocket.git`

# ⚠️ Known Limitations ⚠️

* Headers aren't supported in WebGL because the JavaScript [WebSocket API](https://developer.mozilla.org/en-US/docs/Web/API/WebSocket) doesn't support them
   * See [this StackOverflow issue](https://stackoverflow.com/questions/4361173/http-headers-in-websockets-client-api) for more.
* You can't bypass server certificate validation when connecting to a secure websocket endpoint (`wss`). That means the endpoint must have a CA-verifiable SSL certificate, it can't have no certs installed or only self-signed certs.
   * For WebGL, this is due to a limitation in the JavaScript WebSocket API
   * For .NET, this is due to a bug in Unity's mono runtime
   * There is an [active issue](https://github.com/mikerochip/unity-websocket/issues/7) to address this, but no timeframe for resolution, currently.

# Samples

Assume we have a class like this for the following samples:

```CSharp
using MikeSchweitzer.WebSocket;

public class Tester : MonoBehaviour
{
    public WebSocketConnection _Connection;
    public string _Url = "wss://ws.postman-echo.com/raw";
}
```

## Connect
```CSharp
// inline style
public void Connect()
{
    _Connection.Connect(_Url);
}

// property style
public void Connect()
{
    _Connection.DesiredConfig = new WebSocketConfig
    {
        Url = _Url,
    };
    _Connection.Connect();
}
```

## Disconnect
```CSharp
public void Disconnect()
{
    _Connection.Disconnect();
}
```

## State Querying

### Update Style
```CSharp
private WebSocketState _oldState;

private void Update()
{
    var newState = WebSocketConnection.State;
    if (_oldState != newState)
    {
        Debug.Log($"OnStateChanged oldState={_oldState}|newState={newState}");
        _oldState = newState;
    }
}
```

### Event Style
```CSharp
private void Awake()
{
    _Connection.StateChanged += OnStateChanged
}

private void OnDestroy()
{
    _Connection.StateChanged -= OnStateChanged;
}

private void OnStateChanged(WebSocketConnection connection, WebSocketState oldState, WebSocketState newState)
{
    Debug.Log($"OnStateChanged oldState={oldState}|newState={newState}");
}
```

## Reconnect

### Coroutine Style
```CSharp
public IEnumerator Reconnect()
{
   Disconnect();
   yield return new WaitUntil(_Connection.State == WebSocketState.Disconnected);

   // you may change the desired url now, if you want
   Connect();
}
```

### Event Style
```CSharp
private void OnStateChanged(WebSocketConnection connection, WebSocketState oldState, WebSocketState newState)
{
    switch (newState == WebSocketState.Disconnected)
    {
        // you may change the desired url now, if you want
        _Connection.Connect();
    }
}
```

## Error Messages

**NOTE: These are just error messages, not states. See the State Querying section.**

Error messages are generally derived from platform-specific WebSocket errors.

```CSharp
private void Awake()
{
    _Connection.ErrorMessageReceived += OnErrorMessageReceived;
}

private void OnDestroy()
{
    _Connection.ErrorMessageReceived -= OnErrorMessageReceived;
}

private void OnErrorMessageReceived(WebSocketConnection connection, string errorMessage)
{
    Debug.LogError(errorMessage);
    // you can also use _Connection.ErrorMessage
}
```

## Send Messages

⚠️ You must be `Connected` to send messages, otherwise you will get an error

```CSharp
public void SendString()
{
    _Connection.AddOutgoingMessage("hello");
}

public void SendBinary()
{
    var bytes = Encoding.UTF8.GetBytes("hello");
    _Connection.AddOutgoingMessage(bytes);
}
```

## Receive Messages

### Update Style
```CSharp
private void Update()
{
    while (_Connection.TryRemoveIncomingMessage(out string message))
        Debug.Log(message);
}
```

### Event Style
```CSharp
private void Awake()
{
    _Connection.MessageReceived += OnMessageReceived;
}

private void OnDestroy()
{
    _Connection.MessageReceived -= OnMessageReceived;
}

private void OnMessageReceived(WebSocketConnection connection, WebSocketMessage message)
{
    Debug.Log(message.String);
}
```

### Coroutine Style
```CSharp
private void Awake()
{
    StartCoroutine(ReceiveMessages());
}

private IEnumerator ReceiveMessages()
{
    while (true)
    {
        if (_Connection.TryRemoveIncomingMessage(out string message))
            Debug.Log(message);
        yield return null;
    }
}
```

### Async/Await Style
```CSharp
private CancellationTokenSource _cts;

private async void Awake()
{
    _cts = new CancellationTokenSource();
    await ReceiveMessagesAsync();
}

private void OnDestroy()
{
    _cts.Cancel();
}

private async Task ReceiveMessagesAsync()
{
    while (!_cts.IsCancellationRequested)
    {
        if (_Connection.TryRemoveIncomingMessage(out string message))
            Debug.Log(message);

        await Task.Yield();
    }
}
```

## Custom Ping-Pong Support

This package has a custom ping-pong that you can use on both Web and non-Web builds.

⚠️ Your server must be configured to echo messages of the same message type (text or binary) and content.\
⚠️ This package has custom ping-pong support because the default browser JavaScript WebSocket does not implement [the WebSocket Ping Pong spec](https://datatracker.ietf.org/doc/html/rfc6455#section-5.5.2) even though .NET's `WebSocketClient` does implement the spec.

### Enable Text Ping-Pongs
```CSharp
private void ConfigureStringPings()
{
    _Connection.DesiredConfig = new WebSocketConfig
    {
        Url = _Url,
        PingInterval = TimeSpan.FromSeconds(30),
        PingMessage = new WebSocketMessage("ping"),
    };
}
```

### Enable Binary Ping-Pongs
```CSharp
private byte[] _pingBytes = Encoding.UTF8.GetBytes("ping");
private void ConfigureBinaryPings()
{
    _Connection.DesiredConfig = new WebSocketConfig
    {
        Url = _Url,
        PingInterval = TimeSpan.FromSeconds(30),
        PingMessage = new WebSocketMessage(_pingBytes),
    };
}
```

### Enable Round Trip Time (RTT) Tracking
```CSharp
private void Awake()
{
    _Connection.DesiredConfig = new WebSocketConfig
    {
        Url = _Url,
        PingInterval = TimeSpan.FromSeconds(3),
        PingMessage = new WebSocketMessage("ping"),
        ShouldPingWaitForPong = true,
    };
    _Connection.PingSent += OnPingSent;
    _Connection.PongReceived += OnPongReceived;
}

private void OnDestroy()
{
    _Connection.PingSent -= OnPingSent;
    _Connection.PongReceived -= OnPongReceived;
}

private void OnPingSent(WebSocketConnection connection, DateTime timestamp)
{
    Debug.Log($"OnPingSent timestamp={timestamp:HH:mm:ss.ffff}");
}

private void OnPongReceived(WebSocketConnection connection, DateTime timestamp)
{
    Debug.Log($"OnPongReceived timestamp={timestamp:HH:mm:ss.ffff}");
    Debug.Log($"OnPongReceived RTT={connection.LastPingPongInterval:ss\\.ffff}");
}
```

# Attribution

Based on [this repo](https://github.com/endel/NativeWebSocket) by Endel Dreyer, which was\
Based on [this repo](https://github.com/jirihybek/unity-websocket-webgl) by Jiri Hybek

See [license](./LICENSE.md) and [third party notices](./THIRD%20PARTY%20NOTICES.md) for full attribution.
