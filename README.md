# WebSocket Client for Unity

[![Unity Version](https://img.shields.io/badge/Unity-2019.1%2B-blueviolet?logo=unity)](https://unity.com/releases/editor/archive)

`WebSocketConnection` is an easy-to-use WebSocket client for Unity that Just Works

# Features

* Easy to use
   * `WebSocketConnection` is just a `MonoBehaviour`
   * Doesn't force you to use `async/await` or `Coroutines` - use whatever you want
   * Add event listeners or poll the component, it's up to you
   * Public API prevents you from corrupting an active connection
   * Reusable: connect, disconnect, change URL, connect again, etc
* Flexible config
   * URL is the only required config
   * Sane defaults
   * Optionally set subprotocols, max send, and max receive bytes
* Wide platform support
   * No external install requirements or dependencies
   * `string` is treated as text and `byte[]` as binary (some servers care)
   * WebGL uses a bundled JavaScript lib `WebSocket.jslib`
   * Other platforms use the built-in `System.Net.WebSockets`

# Install

See official instructions for how to [Install a Package from a Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html)

The URL is https://github.com/mikerochip/unity-websocket.git

# ⚠️ Warnings ⚠️

* You may only add outgoing messages in the `Connected` state. An error will happen otherwise.
* Headers aren't supported for WebGL because the JavaScript [WebSocket API](https://developer.mozilla.org/en-US/docs/Web/API/WebSocket) doesn't support them. See [this StackOverflow issue](https://stackoverflow.com/questions/4361173/http-headers-in-websockets-client-api) for more on that.
* You cannot connect using `wss` to a server that does not have a valid SSL cert (no cert or self-signed cert). For WebGL, this is due to a limitation in the JavaScript WebSocket API. For non-WebGL, this is due to a bug in Unity's mono runtime.
   * There is an [active issue](https://github.com/mikerochip/unity-websocket/issues/7) to address this, but no timeframe for resolution currently.

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

## State Management

### Update Style
```CSharp
private WebSocketState _oldState;

private void Update()
{
    var newState = WebSocketConnection.State;
    if (_oldState != newState)
    {
        Debug.Log($"OnStateChanged oldState={_oldState}|newState={newState}");
        _oldState = state;
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

public void Connect()
{
    _Connection.Connect(_Url);
}

public void Disconnect()
{
    _Connection.Disconnect();
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
   // you can pass in a new url or change DesiredConfig.Url if you want
   Connect();
}
```

### Event Style
```CSharp
private void OnStateChanged(WebSocketConnection connection, WebSocketState oldState, WebSocketState newState)
{
    switch (newState == WebSocketState.Disconnected)
    {
       // you can pass in a new url or change DesiredConfig.Url if you want
        _Connection.Connect();
    }
}
```

## Error Messages

**NOTE: These are just error messages, not states. See the State Management section.**

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

# Attribution

Based on [this repo](https://github.com/endel/NativeWebSocket) by Endel Dreyer, which was\
Based on [this repo](https://github.com/jirihybek/unity-websocket-webgl) by Jiri Hybek

See [license](./LICENSE.md) and [third party notices](./THIRD%20PARTY%20NOTICES.md) for full attribution.
