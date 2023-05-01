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
    internal delegate void WebSocketOpenEventHandler();
    internal delegate void WebSocketMessageEventHandler(byte[] data);
    internal delegate void WebSocketErrorEventHandler(string errorMsg);
    internal delegate void WebSocketCloseEventHandler(WebSocketCloseCode closeCode);

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

    internal enum WebSocketState
    {
        Connecting,
        Open,
        Closing,
        Closed
    }

    internal static partial class WebSocketHelpers
    {
        public static WebSocketCloseCode ConvertCloseCode(int closeCode)
        {
            if (Enum.IsDefined(typeof(WebSocketCloseCode), closeCode))
                return (WebSocketCloseCode) closeCode;
            return WebSocketCloseCode.Undefined;
        }
    }
    
    internal interface IWebSocket
    {
        event WebSocketOpenEventHandler OnOpen;
        event WebSocketMessageEventHandler OnMessage;
        event WebSocketErrorEventHandler OnError;
        event WebSocketCloseEventHandler OnClose;

        WebSocketState State { get; }
    }

#if false //!UNITY_WEBGL || UNITY_EDITOR
    internal class WebSocket : IWebSocket
    {
        public event WebSocketOpenEventHandler OnOpen;
        public event WebSocketMessageEventHandler OnMessage;
        public event WebSocketErrorEventHandler OnError;
        public event WebSocketCloseEventHandler OnClose;

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

        public WebSocket(string url, Dictionary<string, string> headers = null)
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

            subprotocols = new List<string>();

            string protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);
        }

        public WebSocket(string url, string subprotocol, Dictionary<string, string> headers = null)
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

            subprotocols = new List<string> {subprotocol};

            string protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);
        }

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
                OnOpen?.Invoke();

                await Receive();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
                OnClose?.Invoke(WebSocketCloseCode.Abnormal);
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

        // simple dispatcher for queued messages.
        public void DispatchMessageQueue()
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
                OnMessage?.Invoke(messageListCopy[i]);
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
                            closeCode = WebSocketHelpers.ParseCloseCodeEnum((int)result.CloseStatus);
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
                OnClose?.Invoke(closeCode);
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
    internal static partial class WebSocketHelpers
    {
        public static WebSocketException GetExceptionFromErrorCode(int errorCode)
        {
            switch (errorCode)
            {
                case -1:
                    return new WebSocketUnexpectedException("WebSocket instance not found.");
                case -2:
                    return new WebSocketInvalidStateException("WebSocket is already connected or in connecting state.");
                case -3:
                    return new WebSocketInvalidStateException("WebSocket is not connected.");
                case -4:
                    return new WebSocketInvalidStateException("WebSocket is already closing.");
                case -5:
                    return new WebSocketInvalidStateException("WebSocket is already closed.");
                case -6:
                    return new WebSocketInvalidStateException("WebSocket is not in open state.");
                case -7:
                    return new WebSocketInvalidArgumentException("Cannot close WebSocket. An invalid code was specified or reason is too long.");
                default:
                    return new WebSocketUnexpectedException("Unknown error.");
            }
        }

        public class WebSocketException : Exception
        {
            public WebSocketException(string message) : base(message)
            {
            }
        }

        public class WebSocketUnexpectedException : WebSocketException
        {
            public WebSocketUnexpectedException(string message) : base(message)
            {
            }
        }

        public class WebSocketInvalidArgumentException : WebSocketException
        {
            public WebSocketInvalidArgumentException(string message) : base(message)
            {
            }
        }

        public class WebSocketInvalidStateException : WebSocketException
        {
            public WebSocketInvalidStateException(string message) : base(message)
            {
            }
        }
    }

    internal class WebSocket : IWebSocket
    {
        #region JSLib Interop
        [DllImport("__Internal")]
        public static extern int WebSocketConnect(int instanceId);
        [DllImport("__Internal")]
        public static extern int WebSocketClose(int instanceId, int code, string reason);
        [DllImport("__Internal")]
        public static extern int WebSocketSend(int instanceId, byte[] dataPtr, int dataLength);
        [DllImport("__Internal")]
        public static extern int WebSocketSendText(int instanceId, string message);
        [DllImport("__Internal")]
        public static extern int WebSocketGetState(int instanceId);
        #endregion

        #region IWebSocket Events
        public event WebSocketOpenEventHandler OnOpen;
        public event WebSocketMessageEventHandler OnMessage;
        public event WebSocketErrorEventHandler OnError;
        public event WebSocketCloseEventHandler OnClose;
        #endregion

        #region Public Properties
        public int InstanceId { get; }

        public WebSocketState State
        {
            get 
            {
                var state = WebSocketGetState(InstanceId);

                if (state < 0) 
                    throw WebSocketHelpers.GetExceptionFromErrorCode(state);

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

        #region Ctor/Dtor
        public WebSocket(string url, List<string> subprotocols, Dictionary<string, string> headers = null)
        {
            JsLibBridge.Initialize();

            InstanceId = JsLibBridge.AddInstance(url, this);

            foreach (var subprotocol in subprotocols)
                JsLibBridge.WebSocketAddSubProtocol(InstanceId, subprotocol);
        }

        ~WebSocket()
        {
            JsLibBridge.RemoveInstance(InstanceId);
        }
        #endregion

        #region Public API
        public Task Connect()
        {
            var ret = WebSocketConnect(InstanceId);

            if (ret < 0)
                throw WebSocketHelpers.GetExceptionFromErrorCode(ret);

            return Task.CompletedTask;
        }

        public void CancelConnection()
        {
            if (State == WebSocketState.Open)
                Close(WebSocketCloseCode.Abnormal);
        }

        public Task Close(WebSocketCloseCode code = WebSocketCloseCode.Normal, string reason = null)
        {
            var ret = WebSocketClose(InstanceId, (int)code, reason);

            if (ret < 0)
                throw WebSocketHelpers.GetExceptionFromErrorCode(ret);

            return Task.CompletedTask;
        }

        public Task Send(byte[] data)
        {
            var ret = WebSocketSend(InstanceId, data, data.Length);

            if (ret < 0)
                throw WebSocketHelpers.GetExceptionFromErrorCode(ret);

            return Task.CompletedTask;
        }

        public Task SendText(string message)
        {
            var ret = WebSocketSendText(InstanceId, message);

            if (ret < 0)
                throw WebSocketHelpers.GetExceptionFromErrorCode(ret);

            return Task.CompletedTask;
        }
        #endregion

        #region JSLib Events
        public void TriggerOnOpen()
        {
            OnOpen?.Invoke();
        }

        public void TriggerOnMessage(byte[] data)
        {
            OnMessage?.Invoke(data);
        }

        public void TriggerOnError(string errorMsg)
        {
            OnError?.Invoke(errorMsg);
        }

        public void TriggerOnClose(int closeCode)
        {
            OnClose?.Invoke(WebSocketHelpers.ConvertCloseCode(closeCode));
        }
        #endregion
    }

    internal static class JsLibBridge
    {
        #region JSLib Interop
        public delegate void OpenedCallback(int instanceId);
        public delegate void MessageReceivedCallback(int instanceId, IntPtr msgPtr, int msgSize);
        public delegate void ErrorCallback(int instanceId, IntPtr errorPtr);
        public delegate void ClosedCallback(int instanceId, int closeCode);

        [DllImport ("__Internal")]
        public static extern int WebSocketAllocate(string url);
        [DllImport ("__Internal")]
        public static extern void WebSocketFree(int instanceId);
        [DllImport ("__Internal")]
        public static extern int WebSocketAddSubProtocol(int instanceId, string subprotocol);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnOpen(OpenedCallback callback);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnMessage(MessageReceivedCallback receivedCallback);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnError(ErrorCallback callback);
        [DllImport ("__Internal")]
        public static extern void WebSocketSetOnClose(ClosedCallback callback);
        #endregion

        public static bool IsInitialized { get; private set; }

        private static Dictionary<Int32, WebSocket> Instances { get; } = new Dictionary<Int32, WebSocket> ();
        
        public static void Initialize()
        {
            if (IsInitialized)
                return;
            
            IsInitialized = true;
            
            WebSocketSetOnOpen(OnOpened);
            WebSocketSetOnMessage(OnMessageReceived);
            WebSocketSetOnError(OnError);
            WebSocketSetOnClose(OnClosed);
        }

        public static int AddInstance(string url, WebSocket webSocket)
        {
            var instanceId = WebSocketAllocate(url);
            Instances.Add(instanceId, webSocket);
            return instanceId;
        }

        public static void RemoveInstance(int instanceId)
        {
            Instances.Remove(instanceId);
            WebSocketFree(instanceId);
        }

        [MonoPInvokeCallback(typeof(OpenedCallback))]
        public static void OnOpened(int instanceId)
        {
            if (Instances.TryGetValue(instanceId, out var instance))
                instance.TriggerOnOpen();
        }

        [MonoPInvokeCallback(typeof(MessageReceivedCallback))]
        public static void OnMessageReceived(int instanceId, IntPtr msgPtr, int msgSize)
        {
            if (Instances.TryGetValue(instanceId, out var instance))
            {
                var msg = new byte[msgSize];
                Marshal.Copy(msgPtr, msg, 0, msgSize);

                instance.TriggerOnMessage(msg);
            }
        }

        [MonoPInvokeCallback(typeof(ErrorCallback))]
        public static void OnError(int instanceId, IntPtr errorPtr)
        {
            if (Instances.TryGetValue(instanceId, out var instance))
            {
                var errorMsg = Marshal.PtrToStringAuto(errorPtr);
                instance.TriggerOnError(errorMsg);
            }
        }

        [MonoPInvokeCallback(typeof(ClosedCallback))]
        public static void OnClosed(int instanceId, int closeCode)
        {
            if (Instances.TryGetValue (instanceId, out var instance)) {
                instance.TriggerOnClose(closeCode);
            }
        }
    }
#endif
}
