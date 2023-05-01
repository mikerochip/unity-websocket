using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using UnityEngine;

internal class MainThreadUtil : MonoBehaviour
{
    public static MainThreadUtil Instance { get; private set; }
    public static SynchronizationContext synchronizationContext { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void Setup()
    {
        Instance = new GameObject("MainThreadUtil")
            .AddComponent<MainThreadUtil>();
        synchronizationContext = SynchronizationContext.Current;
    }

    public static void Run(IEnumerator waitForUpdate)
    {
        synchronizationContext.Post(_ => Instance.StartCoroutine(
                    waitForUpdate), null);
    }

    void Awake()
    {
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(gameObject);
    }
}

internal class WaitForUpdate : CustomYieldInstruction
{
    public override bool keepWaiting
    {
        get { return false; }
    }

    public MainThreadAwaiter GetAwaiter()
    {
        var awaiter = new MainThreadAwaiter();
        MainThreadUtil.Run(CoroutineWrapper(this, awaiter));
        return awaiter;
    }

    public class MainThreadAwaiter : INotifyCompletion
    {
        Action continuation;

        public bool IsCompleted { get; set; }

        public void GetResult() { }

        public void Complete()
        {
            IsCompleted = true;
            continuation?.Invoke();
        }

        void INotifyCompletion.OnCompleted(Action continuation)
        {
            this.continuation = continuation;
        }
    }

    public static IEnumerator CoroutineWrapper(IEnumerator theWorker, MainThreadAwaiter awaiter)
    {
        yield return theWorker;
        awaiter.Complete();
    }
}

internal class WaitForBackgroundThread
{
    public ConfiguredTaskAwaitable.ConfiguredTaskAwaiter GetAwaiter()
    {
        return Task.Run(() => { }).ConfigureAwait(false).GetAwaiter();
    }
}

namespace NativeWebSocket
{
    // see https://developer.mozilla.org/en-US/docs/Web/API/CloseEvent/code
    internal enum WebSocketCloseCode
    {
        NotSet = 0,
        Normal = 1000,
        Away = 1001,
        ProtocolError = 1002,
        UnsupportedData = 1003,
        Undefined = 1004,
        NoStatus = 1005,
        Abnormal = 1006,
        InvalidData = 1007,
        PolicyViolation = 1008,
        TooBig = 1009,
        MandatoryExtension = 1010,
        ServerError = 1011,
        TlsHandshakeFailure = 1015
    }

    internal static class WebSocketHelpers
    {
        public static WebSocketCloseCode ConvertCloseCode(int closeCode)
        {
            if (Enum.IsDefined(typeof(WebSocketCloseCode), closeCode))
                return (WebSocketCloseCode)closeCode;
            return WebSocketCloseCode.Undefined;
        }
    }
    
    internal delegate void OpenedHandler();
    internal delegate void MessageReceivedHandler(byte[] data);
    internal delegate void ErrorHandler(string errorMsg);
    internal delegate void ClosedHandler(WebSocketCloseCode closeCode);

    internal enum WebSocketState
    {
        Connecting,
        Open,
        Closing,
        Closed
    }

    internal interface IWebSocket
    {
        event OpenedHandler Opened;
        event MessageReceivedHandler MessageReceived;
        event ErrorHandler Error;
        event ClosedHandler Closed;

        WebSocketState State { get; }

        void ProcessReceivedMessages();
        Task ConnectAsync();
        Task SendAsync(byte[] bytes);
        Task CloseAsync();
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    internal class WebSocket : IWebSocket
    {
        #region IWebSocket Events
        public event OpenedHandler Opened;
        public event MessageReceivedHandler MessageReceived;
        public event ErrorHandler Error;
        public event ClosedHandler Closed;
        #endregion

        #region Private Fields
        private readonly Uri _uri;
        private readonly Dictionary<string, string> _headers;
        private readonly List<string> _subprotocols;
        private ClientWebSocket _socket = new ClientWebSocket();

        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _cancellationToken;

        private bool isSending = false;
        private readonly Queue<byte[]> _incomingMessages = new Queue<byte[]>();
        private readonly Queue<ArraySegment<byte>> _outgoingMessages = new Queue<ArraySegment<byte>>();
        #endregion

        #region IWebSocket Properties
        public WebSocketState State
        {
            get
            {
                switch (_socket.State)
                {
                    case System.Net.WebSockets.WebSocketState.Connecting:
                        return WebSocketState.Connecting;

                    case System.Net.WebSockets.WebSocketState.Open:
                        return WebSocketState.Open;

                    case System.Net.WebSockets.WebSocketState.CloseSent:
                    case System.Net.WebSockets.WebSocketState.CloseReceived:
                        return WebSocketState.Closing;

                    case System.Net.WebSockets.WebSocketState.Closed:
                        return WebSocketState.Closed;

                    default:
                        return WebSocketState.Closed;
                }
            }
        }
        #endregion

        #region Ctor/Dtor
        public WebSocket(string url, IEnumerable<string> subprotocols, Dictionary<string, string> headers = null)
        {
            _uri = new Uri(url);

            _headers = headers == null
                ? new Dictionary<string, string>()
                : headers.ToDictionary(pair => pair.Key, pair => pair.Value);

            _subprotocols = subprotocols.ToList();

            string protocol = _uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);
        }
        #endregion

        #region IWebSocket Methods
        public void ProcessReceivedMessages()
        {
            if (_incomingMessages.Count == 0)
                return;

            List<byte[]> messages;
            lock (_incomingMessages)
            {
                messages = new List<byte[]>(_incomingMessages);
                _incomingMessages.Clear();
            }

            foreach (var message in messages)
                MessageReceived?.Invoke(message);
        }

        public async Task ConnectAsync()
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationToken = _cancellationTokenSource.Token;

                _socket = new ClientWebSocket();

                foreach (var header in _headers)
                {
                    _socket.Options.SetRequestHeader(header.Key, header.Value);
                }

                foreach (string subprotocol in _subprotocols) {
                    _socket.Options.AddSubProtocol(subprotocol);
                }

                await _socket.ConnectAsync(_uri, _cancellationToken);
                Opened?.Invoke();

                await ReceiveAsync();
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
                Closed?.Invoke(WebSocketCloseCode.Abnormal);
            }
            finally
            {
                if (_socket != null)
                {
                    _cancellationTokenSource.Cancel();
                    _socket.Dispose();
                }
            }
        }

        public Task SendAsync(byte[] bytes)
        {
            if (bytes.Length == 0)
                return Task.CompletedTask;

            return SendMessage(new ArraySegment<byte>(bytes));
        }

        public Task CloseAsync()
        {
            switch (State)
            {
                case WebSocketState.Closed:
                case WebSocketState.Closing:
                    break;
                
                default:
                    _cancellationTokenSource.Cancel();
                    break;
            }
            return Task.CompletedTask;
        }

        private async Task SendMessage(ArraySegment<byte> buffer)
        {
            bool sending;
            lock (_outgoingMessages)
            {
                sending = isSending;
                if (!isSending)
                    isSending = true;
            }

            if (_cancellationToken.IsCancellationRequested)
                return;

            if (!sending)
            {
                // Lock with a timeout, just in case.
                if (!Monitor.TryEnter(_socket, 1000))
                {
                    // If we couldn't obtain exclusive access to the socket in one second, something is wrong.
                    await _socket.CloseAsync(WebSocketCloseStatus.InternalServerError, string.Empty, _cancellationToken);
                    return;
                }

                try
                {
                    // Send the message synchronously.
                    var t = _socket.SendAsync(buffer, WebSocketMessageType.Binary, true, _cancellationToken);
                    t.Wait(_cancellationToken);
                }
                finally
                {
                    Monitor.Exit(_socket);
                }

                lock (_outgoingMessages)
                    isSending = false;

                if (_cancellationToken.IsCancellationRequested)
                    return;

                await HandleQueue();
            }
            else
            {
                lock (_outgoingMessages)
                {
                    _outgoingMessages.Enqueue(buffer);
                }
            }
        }

        private async Task HandleQueue()
        {
            ArraySegment<byte> buffer = null;
            
            lock (_outgoingMessages)
            {
                if (_outgoingMessages.Count > 0)
                    buffer = _outgoingMessages.Dequeue();
            }

            if (buffer == null)
                return;

            await SendMessage(buffer);
        }

        private async Task ReceiveAsync()
        {
            WebSocketCloseCode closeCode = WebSocketCloseCode.Normal;
            await new WaitForBackgroundThread();

            ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[8192]);
            try
            {
                while (_socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _socket.ReceiveAsync(buffer, _cancellationToken);
                            if (_cancellationToken.IsCancellationRequested)
                                break;
                            ms.Write(buffer.Array, buffer.Offset, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseAsync();
                            closeCode = WebSocketHelpers.ConvertCloseCode((int)result.CloseStatus);
                            break;
                        }
                        
                        ms.Seek(0, SeekOrigin.Begin);
                        lock (_incomingMessages)
                        {
                            _incomingMessages.Enqueue(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception)
            {
                _cancellationTokenSource.Cancel();
                closeCode = WebSocketCloseCode.Abnormal;
            }
            finally
            {
                await new WaitForUpdate();
                Closed?.Invoke(closeCode);
            }
        }
    }
    #endregion
#else
    internal class WebSocket : IWebSocket
    {
        #region IWebSocket Events
        public event OpenedHandler Opened;
        public event MessageReceivedHandler MessageReceived;
        public event ErrorHandler Error;
        public event ClosedHandler Closed;
        #endregion

        #region IWebSocket Properties
        public WebSocketState State
        {
            get 
            {
                var state = WebSocketGetState(_instanceId);

                if (state < 0) 
                    Error?.Invoke(ErrorCodeToMessage(state));

                // see https://developer.mozilla.org/en-US/docs/Web/API/WebSocket/readyState
                switch (state)
                {
                    case 0:
                        return WebSocketState.Connecting;
                    case 1:
                        return WebSocketState.Open;
                    case 2:
                        return WebSocketState.Closing;
                    case 3:
                        return WebSocketState.Closed;
                    default:
                        return WebSocketState.Closed;
                }
            }
        }
        #endregion
        
        #region Private Fields
        private readonly int _instanceId;
        // incoming message buffering isn't strictly necessary, it's for API consistency with
        // the System.Net.WebSockets path
        private readonly Queue<byte[]> _incomingMessages = new Queue<byte[]>();
        #endregion

        #region Ctor/Dtor
        public WebSocket(string url, IEnumerable<string> subprotocols, Dictionary<string, string> headers = null)
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);

            JsLibBridge.Initialize();

            _instanceId = JsLibBridge.AddInstance(this, url, subprotocols);
        }

        ~WebSocket()
        {
            JsLibBridge.RemoveInstance(_instanceId);
        }
        #endregion

        #region IWebSocket Methods
        public void ProcessIncomingMessages()
        {
            if (_incomingMessages.Count == 0)
                return;
            
            var messages = _incomingMessages.ToArray();
            _incomingMessages.Clear();
            
            foreach (var message in messages)
                MessageReceived?.Invoke(message);
        }
        
        public Task ConnectAsync()
        {
            var ret = WebSocketConnect(_instanceId);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }

        public Task SendAsync(byte[] bytes)
        {
            var ret = WebSocketSend(_instanceId, bytes, bytes.Length);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            switch (State)
            {
                case WebSocketState.Closed:
                case WebSocketState.Closing:
                    return Task.CompletedTask;
            }

            var ret = WebSocketClose(_instanceId, (int)WebSocketCloseCode.Normal, null);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }
        #endregion

        #region Internal Methods
        private static string ErrorCodeToMessage(int errorCode)
        {
            switch (errorCode)
            {
                case -1:
                    return "WebSocket instance not found.";
                case -2:
                    return "WebSocket is already connected or in connecting state.";
                case -3:
                    return "WebSocket is not connected.";
                case -4:
                    return "WebSocket is already closing.";
                case -5:
                    return "WebSocket is already closed.";
                case -6:
                    return "WebSocket is not in open state.";
                case -7:
                    return "Cannot close WebSocket. An invalid code was specified or reason is too long.";
                default:
                    return "Unknown error.";
            }
        }
        #endregion

        #region Marshalled Types
        [DllImport("__Internal")]
        private static extern int WebSocketConnect(int instanceId);
        [DllImport("__Internal")]
        private static extern int WebSocketClose(int instanceId, int code, string reason);
        [DllImport("__Internal")]
        private static extern int WebSocketSend(int instanceId, byte[] dataPtr, int dataLength);
        [DllImport("__Internal")]
        private static extern int WebSocketGetState(int instanceId);
        #endregion

        #region JsLibBridge Event Helpers
        public void OnOpen() => Opened?.Invoke();
        public void OnMessage(byte[] data) => _incomingMessages.Enqueue(data);
        public void OnError(string errorMsg) => Error?.Invoke(errorMsg);
        public void OnClose(int closeCode) => Closed?.Invoke(WebSocketHelpers.ConvertCloseCode(closeCode));
        #endregion
    }

    internal static class JsLibBridge
    {
        #region Marshalled Types
        public delegate void OpenCallback(int instanceId);
        public delegate void MessageCallback(int instanceId, IntPtr msgPtr, int msgSize);
        public delegate void ErrorCallback(int instanceId, IntPtr errorPtr);
        public delegate void CloseCallback(int instanceId, int closeCode);

        [DllImport ("__Internal")]
        public static extern int WebSocketAllocate(string url);
        [DllImport ("__Internal")]
        public static extern int WebSocketAddSubprotocol(int instanceId, string subprotocol);
        [DllImport ("__Internal")]
        public static extern void WebSocketFree(int instanceId);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnOpen(OpenCallback callback);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnMessage(MessageCallback callback);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnError(ErrorCallback callback);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnClose(CloseCallback callback);
        #endregion

        #region External Properties
        public static bool IsInitialized { get; private set; }
        #endregion

        #region Internal Properties
        private static Dictionary<int, WebSocket> Instances { get; } = new Dictionary<int, WebSocket> ();
        #endregion
        
        #region External Methods
        public static void Initialize()
        {
            if (IsInitialized)
                return;
            
            IsInitialized = true;
            
            WebSocketSetOnOpen(OnOpen);
            WebSocketSetOnMessage(OnMessage);
            WebSocketSetOnError(OnError);
            WebSocketSetOnClose(OnClose);
        }

        public static int AddInstance(WebSocket instance, string url, IEnumerable<string> subprotocols)
        {
            var instanceId = WebSocketAllocate(url);

            foreach (var subprotocol in subprotocols)
                WebSocketAddSubprotocol(instanceId, subprotocol);
            
            Instances.Add(instanceId, instance);
            return instanceId;
        }

        public static void RemoveInstance(int instanceId)
        {
            Instances.Remove(instanceId);
            WebSocketFree(instanceId);
        }
        #endregion

        #region Marshalled Callbacks
        [MonoPInvokeCallback(typeof(OpenCallback))]
        private static void OnOpen(int instanceId)
        {
            if (Instances.TryGetValue(instanceId, out var instance))
                instance.OnOpen();
        }

        [MonoPInvokeCallback(typeof(MessageCallback))]
        private static void OnMessage(int instanceId, IntPtr msgPtr, int msgSize)
        {
            if (Instances.TryGetValue(instanceId, out var instance))
            {
                var msg = new byte[msgSize];
                Marshal.Copy(msgPtr, msg, 0, msgSize);

                instance.OnMessage(msg);
            }
        }

        [MonoPInvokeCallback(typeof(ErrorCallback))]
        private static void OnError(int instanceId, IntPtr errorPtr)
        {
            if (Instances.TryGetValue(instanceId, out var instance))
            {
                var errorMsg = Marshal.PtrToStringAuto(errorPtr);
                instance.OnError(errorMsg);
            }
        }

        [MonoPInvokeCallback(typeof(CloseCallback))]
        private static void OnClose(int instanceId, int closeCode)
        {
            if (Instances.TryGetValue(instanceId, out var instance)) {
                instance.OnClose(closeCode);
            }
        }
        #endregion
    }
#endif
}
