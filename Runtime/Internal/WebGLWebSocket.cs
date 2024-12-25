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
        // message buffering isn't strictly necessary, it's for API consistency with DotNet path
        private readonly Queue<WebSocketMessage> _outgoingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<WebSocketMessage> _incomingMessages = new Queue<WebSocketMessage>();
        private readonly List<WebSocketMessage> _workingOutgoingMessages = new List<WebSocketMessage>();
        private readonly List<WebSocketMessage> _workingIncomingMessages = new List<WebSocketMessage>();

        private static bool _globalInitialized;
        private static Dictionary<int, WebGLWebSocket> _globalInstanceMap = new Dictionary<int, WebGLWebSocket>();
        #endregion

        #region IWebSocket Events
        public event OpenedHandler Opened;
        public event MessageSentHandler MessageSent;
        public event MessageReceivedHandler MessageReceived;
        public event ClosedHandler Closed;
        public event ErrorHandler Error;
        #endregion

        #region IWebSocket Properties
        public WebSocketState State
        {
            get
            {
                var state = JsLibBridge.GetState(_instanceId);

                if (state < 0)
                    Error?.Invoke(JsLibBridge.TranslateCustomErrorState(state));

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
            GlobalInitialize();

            _maxReceiveBytes = maxReceiveBytes;

            _instanceId = JsLibBridge.New(uri.AbsoluteUri, subprotocols, canDebugLog);
            _globalInstanceMap.Add(_instanceId, this);
        }

        ~WebGLWebSocket()
        {
            _globalInstanceMap.Remove(_instanceId);
            JsLibBridge.Delete(_instanceId);
        }
        #endregion

        #region IWebSocket Methods
        public Task ConnectAsync()
        {
            ClearMessages();

            var state = JsLibBridge.Connect(_instanceId);
            if (state < 0)
                Error?.Invoke(JsLibBridge.TranslateCustomErrorState(state));

            return Task.CompletedTask;
        }

        public void AddOutgoingMessage(WebSocketMessage message)
        {
            _outgoingMessages.Enqueue(message);
        }

        public Task ProcessMessagesAsync()
        {
            ProcessOutgoingMessages();
            ProcessIncomingMessages();
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

            var state = JsLibBridge.Close(_instanceId, (int)WebSocketCloseCode.Normal);
            if (state < 0)
                Error?.Invoke(JsLibBridge.TranslateCustomErrorState(state));

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

            var state = JsLibBridge.Close(_instanceId, (int)WebSocketCloseCode.Normal);
            if (state < 0)
                Error?.Invoke(JsLibBridge.TranslateCustomErrorState(state));
        }
        #endregion

        #region Message Processing Methods
        private void ProcessOutgoingMessages()
        {
            if (_outgoingMessages.Count == 0)
                return;

            _workingOutgoingMessages.AddRange(_outgoingMessages);
            _outgoingMessages.Clear();

            foreach (var message in _workingOutgoingMessages)
            {
                if (State != WebSocketState.Open)
                    break;

                var state = message.Type == WebSocketDataType.Binary
                    ? JsLibBridge.SendBinary(_instanceId, message.Bytes)
                    : JsLibBridge.SendText(_instanceId, message.String);

                if (state < 0)
                    Error?.Invoke(JsLibBridge.TranslateCustomErrorState(state));
                else
                    MessageSent?.Invoke(message);
            }
            _workingOutgoingMessages.Clear();
        }

        private void ProcessIncomingMessages()
        {
            if (_incomingMessages.Count == 0)
                return;

            _workingIncomingMessages.AddRange(_incomingMessages);
            _incomingMessages.Clear();

            foreach (var message in _workingIncomingMessages)
                MessageReceived?.Invoke(message);
            _workingIncomingMessages.Clear();
        }

        private void ClearMessages()
        {
            _outgoingMessages.Clear();
            _incomingMessages.Clear();
            _workingOutgoingMessages.Clear();
            _workingIncomingMessages.Clear();
        }
        #endregion

        #region Global Management
        private static void GlobalInitialize()
        {
            if (_globalInitialized)
                return;

            _globalInitialized = true;

            JsLibBridge.Initialize();

            JsLibBridge.Opened += OnOpened;
            JsLibBridge.BinaryMessageReceived += OnBinaryMessageReceived;
            JsLibBridge.TextMessageReceived += OnTextMessageReceived;
            JsLibBridge.Closed += OnClosed;
            JsLibBridge.Error += OnError;
        }
        #endregion

        #region Instance Events
        private static void OnOpened(int instanceId)
        {
            if (!_globalInstanceMap.TryGetValue(instanceId, out var instance))
                return;

            instance.Opened?.Invoke();
        }

        private static void OnBinaryMessageReceived(int instanceId, byte[] bytes)
        {
            if (!_globalInstanceMap.TryGetValue(instanceId, out var instance))
                return;

            if (bytes.Length > instance._maxReceiveBytes)
            {
                instance.Error?.Invoke(WebSocketHelpers.GetReceiveSizeExceededErrorMessage(bytes.Length, instance._maxReceiveBytes));
                return;
            }

            var message = new WebSocketMessage(bytes);
            instance._incomingMessages.Enqueue(message);
        }

        private static void OnTextMessageReceived(int instanceId, string text)
        {
            if (!_globalInstanceMap.TryGetValue(instanceId, out var instance))
                return;

            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (bytes.Length > instance._maxReceiveBytes)
            {
                instance.Error?.Invoke(WebSocketHelpers.GetReceiveSizeExceededErrorMessage(bytes.Length, instance._maxReceiveBytes));
                return;
            }

            var message = new WebSocketMessage(text);
            instance._incomingMessages.Enqueue(message);
        }

        private static void OnClosed(int instanceId, int closeCode)
        {
            if (!_globalInstanceMap.TryGetValue(instanceId, out var instance))
                return;

            instance.Closed?.Invoke(WebSocketHelpers.ConvertCloseCode(closeCode));
            instance.ClearMessages();
        }

        private static void OnError(int instanceId)
        {
            if (!_globalInstanceMap.TryGetValue(instanceId, out var instance))
                return;

            instance.Error?.Invoke("WebSocket error");
        }
        #endregion
    }

    internal static class JsLibBridge
    {
        #region Global Properties
        private static bool IsInitialized { get; set; }
        #endregion

        #region Global Methods
        public static void Initialize()
        {
            if (IsInitialized)
                return;

            IsInitialized = true;

            WebSocketInitialize();
            WebSocketSetOpenCallback(JsOnOpen);
            WebSocketSetBinaryMessageCallback(JsOnBinaryMessage);
            WebSocketSetTextMessageCallback(JsOnTextMessage);
            WebSocketSetErrorCallback(JsOnError);
            WebSocketSetCloseCallback(JsOnClose);
        }

        public static string TranslateCustomErrorState(int state)
        {
            switch (state)
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

        #region Instance Management
        public static int New(string url, IEnumerable<string> subprotocols, bool debugLogging)
        {
            var instanceId = WebSocketNew(url, debugLogging);

            if (subprotocols != null)
            {
                foreach (var subprotocol in subprotocols)
                    WebSocketAddSubprotocol(instanceId, subprotocol);
            }

            return instanceId;
        }

        public static void Delete(int instanceId)
        {
            WebSocketDelete(instanceId);
        }
        #endregion

        #region Instance API
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

        #region Instance Events
        public delegate void OpenedHandler(int instanceId);
        public delegate void BinaryMessageReceivedHandler(int instanceId, byte[] bytes);
        public delegate void TextMessageReceivedHandler(int instanceId, string text);
        public delegate void ClosedHandler(int instanceId, int closeCode);
        public delegate void ErrorHandler(int instanceId);

        public static event OpenedHandler Opened;
        public static event BinaryMessageReceivedHandler BinaryMessageReceived;
        public static event TextMessageReceivedHandler TextMessageReceived;
        public static event ClosedHandler Closed;
        public static event ErrorHandler Error;
        #endregion

        #region Marshaled Instance Management
        [DllImport("__Internal")]
        private static extern int WebSocketInitialize();
        [DllImport("__Internal")]
        private static extern int WebSocketNew(string url, bool debugLogging);
        [DllImport("__Internal")]
        private static extern void WebSocketAddSubprotocol(int instanceId, string subprotocol);
        [DllImport("__Internal")]
        private static extern void WebSocketDelete(int instanceId);
        #endregion

        #region Marshaled Instance API
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

        #region Marshaled Instance Events
        private delegate void JsOpenCallback(int instanceId);
        private delegate void JsBinaryMessageCallback(int instanceId, IntPtr messagePtr, int messageLength);
        private delegate void JsTextMessageCallback(int instanceId, IntPtr messagePtr);
        private delegate void JsCloseCallback(int instanceId, int closeCode);
        private delegate void JsErrorCallback(int instanceId);

        [DllImport ("__Internal")]
        private static extern void WebSocketSetOpenCallback(JsOpenCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetBinaryMessageCallback(JsBinaryMessageCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetTextMessageCallback(JsTextMessageCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetCloseCallback(JsCloseCallback callback);
        [DllImport ("__Internal")]
        private static extern void WebSocketSetErrorCallback(JsErrorCallback callback);

        [MonoPInvokeCallback(typeof(JsOpenCallback))]
        private static void JsOnOpen(int instanceId)
        {
            Opened?.Invoke(instanceId);
        }

        [MonoPInvokeCallback(typeof(JsBinaryMessageCallback))]
        private static void JsOnBinaryMessage(int instanceId, IntPtr messagePtr, int messageLength)
        {
            var bytes = new byte[messageLength];
            Marshal.Copy(messagePtr, bytes, 0, messageLength);
            BinaryMessageReceived?.Invoke(instanceId, bytes);
        }

        [MonoPInvokeCallback(typeof(JsTextMessageCallback))]
        private static void JsOnTextMessage(int instanceId, IntPtr messagePtr)
        {
            var text = Marshal.PtrToStringAuto(messagePtr);
            TextMessageReceived?.Invoke(instanceId, text);
        }

        [MonoPInvokeCallback(typeof(JsCloseCallback))]
        private static void JsOnClose(int instanceId, int closeCode)
        {
            Closed?.Invoke(instanceId, closeCode);
        }

        [MonoPInvokeCallback(typeof(JsErrorCallback))]
        private static void JsOnError(int instanceId)
        {
            Error?.Invoke(instanceId);
        }
        #endregion
    }
}
#endif
