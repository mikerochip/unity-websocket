using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AOT;
using System.Runtime.InteropServices;
using UnityEngine;
using System.Collections;

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
                return (WebSocketCloseCode) closeCode;
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

        void ProcessIncomingMessages();
    }

#if !UNITY_WEBGL || UNITY_EDITOR
    internal class WebSocket : IWebSocket
    {
        public event OpenedHandler Opened;
        public event MessageReceivedHandler MessageReceived;
        public event ErrorHandler Error;
        public event ClosedHandler Closed;

        private Uri uri;
        private Dictionary<string, string> headers;
        private List<string> subprotocols;
        private ClientWebSocket m_Socket = new ClientWebSocket();

        private CancellationTokenSource m_TokenSource;
        private CancellationToken m_CancellationToken;

        private readonly object OutgoingMessageLock = new object();
        private readonly object IncomingMessageLock = new object();

        private bool isSending = false;
        private List<ArraySegment<byte>> sendBytesQueue = new List<ArraySegment<byte>>();
        private List<ArraySegment<byte>> sendTextQueue = new List<ArraySegment<byte>>();

        public WebSocket(string url, List<string> subprotocols, Dictionary<string, string> headers = null)
        {
            uri = new Uri(url);

            if (headers == null)
            {
                this.headers = new Dictionary<string, string>();
            }
            else
            {
                this.headers = headers;
            }

            this.subprotocols = subprotocols;

            string protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);
        }

        public void CancelConnection()
        {
            m_TokenSource?.Cancel();
        }

        public async Task Connect()
        {
            try
            {
                m_TokenSource = new CancellationTokenSource();
                m_CancellationToken = m_TokenSource.Token;

                m_Socket = new ClientWebSocket();

                foreach (var header in headers)
                {
                    m_Socket.Options.SetRequestHeader(header.Key, header.Value);
                }

                foreach (string subprotocol in subprotocols) {
                    m_Socket.Options.AddSubProtocol(subprotocol);
                }

                await m_Socket.ConnectAsync(uri, m_CancellationToken);
                Opened?.Invoke();

                await Receive();
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
                Closed?.Invoke(WebSocketCloseCode.Abnormal);
            }
            finally
            {
                if (m_Socket != null)
                {
                    m_TokenSource.Cancel();
                    m_Socket.Dispose();
                }
            }
        }

        public WebSocketState State
        {
            get
            {
                switch (m_Socket.State)
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

        public Task Send(byte[] bytes)
        {
            // return m_Socket.SendAsync(buffer, WebSocketMessageType.Binary, true, CancellationToken.None);
            return SendMessage(sendBytesQueue, WebSocketMessageType.Binary, new ArraySegment<byte>(bytes));
        }

        public Task SendText(string message)
        {
            var encoded = Encoding.UTF8.GetBytes(message);

            // m_Socket.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
            return SendMessage(sendTextQueue, WebSocketMessageType.Text, new ArraySegment<byte>(encoded, 0, encoded.Length));
        }

        private async Task SendMessage(List<ArraySegment<byte>> queue, WebSocketMessageType messageType, ArraySegment<byte> buffer)
        {
            // Return control to the calling method immediately.
            // await Task.Yield ();

            // Make sure we have data.
            if (buffer.Count == 0)
            {
                return;
            }

            // The state of the connection is contained in the context Items dictionary.
            bool sending;

            lock (OutgoingMessageLock)
            {
                sending = isSending;

                // If not, we are now.
                if (!isSending)
                {
                    isSending = true;
                }
            }

            if (!sending)
            {
                // Lock with a timeout, just in case.
                if (!Monitor.TryEnter(m_Socket, 1000))
                {
                    // If we couldn't obtain exclusive access to the socket in one second, something is wrong.
                    await m_Socket.CloseAsync(WebSocketCloseStatus.InternalServerError, string.Empty, m_CancellationToken);
                    return;
                }

                try
                {
                    // Send the message synchronously.
                    var t = m_Socket.SendAsync(buffer, messageType, true, m_CancellationToken);
                    t.Wait(m_CancellationToken);
                }
                finally
                {
                    Monitor.Exit(m_Socket);
                }

                // Note that we've finished sending.
                lock (OutgoingMessageLock)
                {
                    isSending = false;
                }

                // Handle any queued messages.
                await HandleQueue(queue, messageType);
            }
            else
            {
                // Add the message to the queue.
                lock (OutgoingMessageLock)
                {
                    queue.Add(buffer);
                }
            }
        }

        private async Task HandleQueue(List<ArraySegment<byte>> queue, WebSocketMessageType messageType)
        {
            var buffer = new ArraySegment<byte>();
            lock (OutgoingMessageLock)
            {
                // Check for an item in the queue.
                if (queue.Count > 0)
                {
                    // Pull it off the top.
                    buffer = queue[0];
                    queue.RemoveAt(0);
                }
            }

            // Send that message.
            if (buffer.Count > 0)
            {
                await SendMessage(queue, messageType, buffer);
            }
        }

        private List<byte[]> m_MessageList = new List<byte[]>();

        public void ProcessIncomingMessages()
        {
            if (m_MessageList.Count == 0)
            {
                return;
            }

            List<byte[]> messageListCopy;

            lock (IncomingMessageLock)
            {
                messageListCopy = new List<byte[]>(m_MessageList);
                m_MessageList.Clear();
            }

            var len = messageListCopy.Count;
            for (int i = 0; i < len; i++)
            {
                MessageReceived?.Invoke(messageListCopy[i]);
            }
        }

        public async Task Receive()
        {
            WebSocketCloseCode closeCode = WebSocketCloseCode.Abnormal;
            await new WaitForBackgroundThread();

            ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[8192]);
            try
            {
                while (m_Socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    WebSocketReceiveResult result = null;

                    using (var ms = new MemoryStream())
                    {
                        do
                        {
                            result = await m_Socket.ReceiveAsync(buffer, m_CancellationToken);
                            ms.Write(buffer.Array, buffer.Offset, result.Count);
                        }
                        while (!result.EndOfMessage);

                        ms.Seek(0, SeekOrigin.Begin);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            lock (IncomingMessageLock)
                            {
                              m_MessageList.Add(ms.ToArray());
                            }

                            //using (var reader = new StreamReader(ms, Encoding.UTF8))
                            //{
                            //	string message = reader.ReadToEnd();
                            //	OnMessage?.Invoke(this, new MessageEventArgs(message));
                            //}
                        }
                        else if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            lock (IncomingMessageLock)
                            {
                              m_MessageList.Add(ms.ToArray());
                            }
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await Close();
                            closeCode = WebSocketHelpers.ConvertCloseCode((int)result.CloseStatus);
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                m_TokenSource.Cancel();
            }
            finally
            {
                await new WaitForUpdate();
                Closed?.Invoke(closeCode);
            }
        }

        public async Task Close()
        {
            if (State == WebSocketState.Open)
            {
                await m_Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, m_CancellationToken);
            }
        }
    }
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
        public WebSocket(string url, List<string> subprotocols, Dictionary<string, string> headers = null)
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);

            JsLibBridge.Initialize();

            _instanceId = JsLibBridge.AddInstance(url, this);

            foreach (var subprotocol in subprotocols)
                JsLibBridge.WebSocketAddSubProtocol(_instanceId, subprotocol);
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
        
        public Task Connect()
        {
            var ret = WebSocketConnect(_instanceId);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }

        public void CancelConnection()
        {
            if (State == WebSocketState.Open)
                Close(WebSocketCloseCode.Abnormal);
        }

        public Task Close(WebSocketCloseCode code = WebSocketCloseCode.Normal, string reason = null)
        {
            var ret = WebSocketClose(_instanceId, (int)code, reason);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }

        public Task Send(byte[] data)
        {
            var ret = WebSocketSend(_instanceId, data, data.Length);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }

        public Task SendText(string message)
        {
            var ret = WebSocketSendText(_instanceId, message);

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
        private static extern int WebSocketSendText(int instanceId, string message);
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
        public static extern void WebSocketFree(int instanceId);
        [DllImport ("__Internal")]
        public static extern int WebSocketAddSubProtocol(int instanceId, string subprotocol);
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

        public static int AddInstance(string url, WebSocket instance)
        {
            var instanceId = WebSocketAllocate(url);
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
