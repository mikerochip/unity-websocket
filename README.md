# WebSocket Client for Unity

[![Unity Version](https://img.shields.io/badge/Unity-2019.4%2B-blueviolet?logo=unity)](https://unity.com/releases/editor/archive)

This package provides a MonoBehaviour called `WebSocketConnection`.

`WebSocketConnection` is an easy-to-use WebSocket client.

# Features

* Easy to use
   * `WebSocketConnection` is just a `MonoBehaviour`
   * Using `async/await` is optional: event listeners, coroutines, and polling are supported
   * Doesn't force `#if` for WebGL: no conditional-compilation required
   * Public API prevents you from corrupting an active connection
   * Reusable: connect, disconnect, change URL, connect again from one `WebSocketConnection`
* Wide support
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

## .NET

* Self-signed certs require you to
   * Use Api Compatibility Level `.NET Framework`
   * Do a whole ton of fiddly setup, see [sample below](#self-signed-certificates)

## Web

There are limitations due to the underlying implementation using the default browser JavaScript [WebSocket API](https://developer.mozilla.org/en-US/docs/Web/API/WebSocket)

* No custom header support. See [this](https://stackoverflow.com/questions/4361173/http-headers-in-websockets-client-api) for more.
* No support for servers with self-signed certs
* No websocket spec-compliant ping-pong control frames
   * The custom ping-pong feature here is a non-spec-compliant alternative

# My Test Projects

If you want to see how I test this package, or you just don't want to roll your own:

* [Server test project](https://github.com/mikerochip/server-websocket-tester)
* [Client test project](https://github.com/mikerochip/unity-websocket-tester)

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

    // you may change the url here, if you want
    Connect();
}
```

### Event Style
```CSharp
private void OnStateChanged(WebSocketConnection connection, WebSocketState oldState, WebSocketState newState)
{
    if (newState == WebSocketState.Disconnected)
    {
        // you may change the url here, if you want
        _Connection.Connect();
    }
}
```

## Error Messages

> [!NOTE]
> These are just error messages, not states. See the State Querying section.

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
    // you can also use _Connection.ErrorMessage
    Debug.LogError(errorMessage);
}
```

## Send Messages

> [!WARNING]
> You must be `Connected` to send messages, otherwise you will get an error

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

This package has a custom ping-pong feature that you can write once for Web and non-Web builds.

> [!WARNING]
> * Your server must be configured to echo messages of the same message type (text or binary) and content.
> * This package has custom ping-pong support because the default browser JavaScript WebSocket client does not implement [the WebSocket Ping Pong spec](https://datatracker.ietf.org/doc/html/rfc6455#section-5.5.2) even though .NET's `WebSocketClient` does implement the spec.

### Enable Text Ping-Pongs
```CSharp
private void ConfigureStringPings()
{
    _Connection.DesiredConfig = new WebSocketConfig
    {
        Url = _Url,
        PingInterval = TimeSpan.FromSeconds(30),
        PingMessage = new WebSocketMessage("hi"),
    };
}
```

### Enable Binary Ping-Pongs
```CSharp
private byte[] _pingBytes = Encoding.UTF8.GetBytes("hi");
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
        PingMessage = new WebSocketMessage("hi"),
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

## Self-Signed Certificates

If you must use self-signed certificates, then here is a way to make that work.

> ![WARNING]
> **I highly recommend against self-signed certs.** These steps are easy to mess up and overly complicated.
>
> I highly recommend instead:
> * Trusted CA certs
> * CA certs pre-installed on your servers and devices
> * Just using insecure `ws:`

1. Create a certificate with e.g. `openssl`
   * Example:\
     `openssl req -x509 -newkey rsa:2048 -nodes -out cert.pem -keyout key.pem -days 365 -subj "/CN=example.local" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,DNS:my-example-domain.com"`
   * In the above example, replace `my-example-domain.com` with your domain name (if you have one, otherwise leave out the `DNS:` SAN)
2. Export a pfx file from your cert
   * Example:\
     `openssl pkcs12 -export -in cert.pem -inkey key.pem -out cert.pfx -password pass:mypass -macalg SHA1 -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES`
   * In the above example, replace `mypass` with a password of your choosing
   * ⚠️**NOTE**: You MUST use these algorithm options or Unity will fail to load your cert
3. Set your Unity project's `Api Compatibility Level` to `.NET Framework`
4. Create a class like this somewhere in your project
   ```CSharp
   using System.Net;
   using System.Security.Cryptography.X509Certificates;

   private class SelfSignedCertTrustPolicy : ICertificatePolicy
   {
       public bool CheckValidationResult(ServicePoint servicePoint, X509Certificate certificate, WebRequest request, int certificateProblem)
       {
           return true;
       }
   }
   ```
5. In some `Awake()` method somewhere, add this line:\
   ```CSharp
   ServicePointManager.CertificatePolicy = new SelfSignedCertTrustPolicy();
   ```
6. Configure your server to load the certs
   * This totally depends on your server
   * You can see an example from my test server [here](https://github.com/mikerochip/server-websocket-tester/blob/ba2fcde8be7f1cc37fa69319d030012ebb31c931/WebSocketEchoServer/Program.cs#L39)
7. Configure this Unity package to load the certs
   * Put your `cert.pfx` in your Unity project as `cert.pfx.bytes`
   * Do similar with the password you made earlier: put your password in a text file like `cert.pfx.pass.bytes`
   * Load both of those in your code as `TextAsset`
   * Then do `MyWebSocketConfig.DotNetSelfSignedCert = MyCert.bytes`
   * And `MyWebSocketConfig.DotNetSelfSignedCertPassword = MyCertPassword.text.ToCharArray()`

`WebSocketConnection` should now be able to use `wss:` to connect to your server.

# Attribution

Based on [this repo](https://github.com/endel/NativeWebSocket) by Endel Dreyer, which was\
Based on [this repo](https://github.com/jirihybek/unity-websocket-webgl) by Jiri Hybek

See [license](./LICENSE.md) and [third party notices](./THIRD%20PARTY%20NOTICES.md) for full attribution.
