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
   * Set subprotocols, max send, and max receive bytes
* Wide platform support
   * No external install requirements or dependencies
   * `string` is treated as text and `byte[]` as binary (some servers care)
   * WebGL uses a bundled JavaScript lib `WebSocket.jslib`
   * Other platforms use the built-in `System.Net.WebSockets`
 
⚠️ Headers aren't supported for WebGL because the JavaScript [WebSocket API](https://developer.mozilla.org/en-US/docs/Web/API/WebSocket) doesn't support them. See [this StackOverflow issue](https://stackoverflow.com/questions/4361173/http-headers-in-websockets-client-api) for more on that.

# Install

See official instructions for how to [Install a Package from a Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html)

The URL is https://github.com/mikerochip/unity-websocket.git

# Samples

Assume we have a class like this for the following samples:

```CSharp
using Mikerochip.WebSocket;

public class Tester : MonoBehaviour
{
    public WebSocketConnection _Connection;
    public string _Url = "wss://ws.postman-echo.com/raw";
}
```

## Connect
```CSharp
// inline style
void Connect()
{
    _Connection.Connect(_Url);
}

// property style
void Connect()
{
    _Connection.DesiredUrl = _Url;
    _Connection.Connect();
}
```

## Disconnect
```CSharp
void Disconnect()
{
    _Connection.Disconnect();
}
```

## Connection Management
```CSharp
private void Awake()
{
    _Connection.StateChanged += OnStateChanged
}

private void OnDestroy()
{
   _Connection.Disconnect();
}

public void Connect()
{
    _Connection.Connect(_Url);
}

private void OnStateChanged(WebSocketConnection connection, WebSocketState oldState, WebSocketState newState)
{
    Debug.Log($"OnStateChanged oldState={oldState}|newState={newState}");

    if (newState == WebSocketState.Error)
        Debug.LogError($"OnStateChanged Error={_Connection.ErrorMessage}");
}
```

## Reconnect
```CSharp
IEnumerator Reconnect()
{
   _Connection.Disconnect();
   yield return new WaitWhile(_Connection.State == WebSocketState.Disconnecting);
   _Connection.Connect();
}

void Reconnect()
{
    _Connection.StateChanged += OnStateChanged;
    _Connection.Disconnect();

    void OnStateChanged(WebSocketConnection connection, WebSocketState oldState, WebSocketState newState)
    {
        if (newState == WebSocketState.Disconnecting)
            return;
        _Connection.Connect();
        _Connection.StateChanged -= OnStateChanged;
    }
}
```

## Error Handling

### Update Style
```CSharp
private bool _handledError;

private void Update()
{
    if (_Connection.State == WebSocketState.Error)
    {
        if (!_handledError)
        {
            Debug.LogError(_Connection.ErrorMessage);
            _handledError = true;
        }
    }
    else
    {
        _handledError = false;
    }
}
```

### Event Style
```CSharp
private void Awake()
{
    _Connection.Error += OnError;
}

void OnError(WebSocketConnection connection, string error)
{
    Debug.LogError(error);
}
```

## Send Messages
```CSharp
void SendString()
{
    _Connection.AddOutgoingMessage("hello");
}

void SendBinary()
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

private void OnMessageReceived(WebSocketConnection connection, WebSocketMessage message)
{
    Debug.Log(message.String);

    // NOTE: the message in the parameter is retained by the connection, so you have to remove it
    _Connection.TryRemoveIncomingMessage(out string _);
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
    }
}
```

# Attribution

Based on [this repo](https://github.com/endel/NativeWebSocket) by Endel Dreyer, which was\
Based on [this repo](https://github.com/jirihybek/unity-websocket-webgl) by Jiri Hybek

See [license](./LICENSE.md) and [third party notices](./THIRD%20PARTY%20NOTICES.md) for full attribution.
