#if UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;

namespace MikeSchweitzer.WebSocket.Internal
{
    internal class WebGLWebSocket : IWebSocket
    {
        #region Private Fields
        private readonly int _maxReceiveBytes;
        private readonly int _instanceId;
        // incoming message buffering isn't strictly necessary, it's for API consistency with
        // the System.Net.WebSockets path
        private readonly Queue<WebSocketMessage> _incomingMessages = new Queue<WebSocketMessage>();
        #endregion

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
                var state = JsLibBridge.GetState(_instanceId);

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

        #region Ctor/Dtor
        public WebGLWebSocket(
            Uri uri,
            IEnumerable<string> subprotocols,
            int maxReceiveBytes,
            bool canDebugLog)
        {
            _maxReceiveBytes = maxReceiveBytes;

            JsLibBridge.Initialize();

            _instanceId = JsLibBridge.Allocate(this, uri.AbsoluteUri, subprotocols, canDebugLog);
        }

        ~WebGLWebSocket()
        {
            JsLibBridge.Free(_instanceId);
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
            var ret = JsLibBridge.Connect(_instanceId);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }

        public void AddOutgoingMessage(WebSocketMessage message)
        {
            var ret = message.Type == WebSocketDataType.Binary
                ? JsLibBridge.SendBinary(_instanceId, message.Bytes)
                : JsLibBridge.SendText(_instanceId, message.String);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));
        }

        public Task CloseAsync()
        {
            switch (State)
            {
                case WebSocketState.Closed:
                case WebSocketState.Closing:
                    return Task.CompletedTask;
            }

            var ret = JsLibBridge.Close(_instanceId, (int)WebSocketCloseCode.Normal);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));

            return Task.CompletedTask;
        }

        public void Cancel()
        {
            switch (State)
            {
                case WebSocketState.Closed:
                case WebSocketState.Closing:
                    return;
            }

            var ret = JsLibBridge.Close(_instanceId, (int)WebSocketCloseCode.Normal);

            if (ret < 0)
                Error?.Invoke(ErrorCodeToMessage(ret));
        }
        #endregion

        #region Internal Methods
        private static string ErrorCodeToMessage(int errorCode)
        {
            switch (errorCode)
            {
                case -1:
                    return "WebSocket not created";
                case -2:
                    return "Already connected or in connecting state";
                case -3:
                    return "Not connected";
                case -4:
                    return "Already closing";
                case -5:
                    return "Already closed";
                case -6:
                    return "WebSocket not opened";
                case -7:
                    return "Cannot close, invalid code specified or reason too long";
                default:
                    return "Unknown error";
            }
        }
        #endregion

        #region JsLibBridge Event Helpers
        public void OnOpen()
        {
            Opened?.Invoke();
        }

        public void OnBinaryMessage(byte[] bytes)
        {
            if (bytes.Length > _maxReceiveBytes)
                return;

            var message = new WebSocketMessage(bytes);
            _incomingMessages.Enqueue(message);
        }

        public void OnTextMessage(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (bytes.Length > _maxReceiveBytes)
                return;

            var message = new WebSocketMessage(text);
            _incomingMessages.Enqueue(message);
        }

        public void OnError(string errorMsg)
        {
            Error?.Invoke(errorMsg);
        }

        public void OnClose(int closeCode)
        {
            Closed?.Invoke(WebSocketHelpers.ConvertCloseCode(closeCode));
        }
        #endregion
    }

    internal static class JsLibBridge
    {
        #region Marshaled Callback Types
        private delegate void OpenCallback(int instanceId);
        private delegate void BinaryMessageCallback(int instanceId, IntPtr messagePtr, int messageLength);
        private delegate void TextMessageCallback(int instanceId, IntPtr messagePtr);
        private delegate void ErrorCallback(int instanceId, IntPtr errorPtr);
        private delegate void CloseCallback(int instanceId, int closeCode);
        #endregion

        #region Marshaled Methods
        [DllImport ("__Internal")]
        private static extern int WebSocketAllocate(string url, bool debugLogging);
        [DllImport ("__Internal")]
        private static extern void WebSocketAddSubprotocol(int instanceId, string subprotocol);
        [DllImport ("__Internal")]
        private static extern void WebSocketFree(int instanceId);

        [DllImport("__Internal")]
        private static extern int WebSocketConnect(int instanceId);
        [DllImport("__Internal")]
        private static extern int WebSocketClose(int instanceId, int code);
        [DllImport("__Internal")]
        private static extern int WebSocketSendBinary(int instanceId, byte[] bytes, int length);
        [DllImport("__Internal")]
        private static extern int WebSocketSendText(int instanceId, string message);
        [DllImport("__Internal")]
        private static extern int WebSocketGetState(int instanceId);
        #endregion

        #region Marshaled Callback Setters
        [DllImport ("__Internal")]
        private static extern void WebSocketSetOnOpen(OpenCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetOnBinaryMessage(BinaryMessageCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetOnTextMessage(TextMessageCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetOnError(ErrorCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetOnClose(CloseCallback callback);
        #endregion

        #region Public Properties
        public static bool IsInitialized { get; private set; }
        #endregion

        #region Private Properties
        private static Dictionary<int, WebGLWebSocket> Instances { get; } = new Dictionary<int, WebGLWebSocket>();
        #endregion

        #region Public Methods
        public static void Initialize()
        {
            if (IsInitialized)
                return;

            IsInitialized = true;

            WebSocketSetOnOpen(OnOpen);
            WebSocketSetOnBinaryMessage(OnBinaryMessage);
            WebSocketSetOnTextMessage(OnTextMessage);
            WebSocketSetOnError(OnError);
            WebSocketSetOnClose(OnClose);
        }
        #endregion

        #region Wrappers for Marshaled Methods
        public static int Allocate(WebGLWebSocket instance, string url, IEnumerable<string> subprotocols, bool debugLogging)
        {
            var instanceId = WebSocketAllocate(url, debugLogging);

            if (subprotocols != null)
            {
                foreach (var subprotocol in subprotocols)
                    WebSocketAddSubprotocol(instanceId, subprotocol);
            }

            Instances.Add(instanceId, instance);
            return instanceId;
        }

        public static void Free(int instanceId)
        {
            Instances.Remove(instanceId);
            WebSocketFree(instanceId);
        }

        public static int Connect(int instanceId)
        {
            return WebSocketConnect(instanceId);
        }

        public static int Close(int instanceId, int code)
        {
            return WebSocketClose(instanceId, code);
        }

        public static int SendBinary(int instanceId, byte[] bytes)
        {
            return WebSocketSendBinary(instanceId, bytes, bytes.Length);
        }

        public static int SendText(int instanceId, string text)
        {
            return WebSocketSendText(instanceId, text);
        }

        public static int GetState(int instanceId)
        {
            return WebSocketGetState(instanceId);
        }
        #endregion

        #region Handlers for Marshaled Callbacks
        [MonoPInvokeCallback(typeof(OpenCallback))]
        private static void OnOpen(int instanceId)
        {
            if (!Instances.TryGetValue(instanceId, out var instance))
                return;

            instance.OnOpen();
        }

        [MonoPInvokeCallback(typeof(BinaryMessageCallback))]
        private static void OnBinaryMessage(int instanceId, IntPtr messagePtr, int messageLength)
        {
            if (!Instances.TryGetValue(instanceId, out var instance))
                return;

            var bytes = new byte[messageLength];
            Marshal.Copy(messagePtr, bytes, 0, messageLength);
            instance.OnBinaryMessage(bytes);
        }

        [MonoPInvokeCallback(typeof(TextMessageCallback))]
        private static void OnTextMessage(int instanceId, IntPtr messagePtr)
        {
            if (!Instances.TryGetValue(instanceId, out var instance))
                return;

            var text = Marshal.PtrToStringAuto(messagePtr);
            instance.OnTextMessage(text);
        }

        [MonoPInvokeCallback(typeof(ErrorCallback))]
        private static void OnError(int instanceId, IntPtr errorPtr)
        {
            if (!Instances.TryGetValue(instanceId, out var instance))
                return;

            var errorMsg = Marshal.PtrToStringAuto(errorPtr);
            instance.OnError(errorMsg);
        }

        [MonoPInvokeCallback(typeof(CloseCallback))]
        private static void OnClose(int instanceId, int closeCode)
        {
            if (!Instances.TryGetValue(instanceId, out var instance))
                return;

            instance.OnClose(closeCode);
        }
        #endregion
    }
}
#endif
