using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;

namespace Mikerochip.WebSocket.Internal
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
        
        #region Ctor/Dtor
        public WebGLWebSocket(
            string url,
            IEnumerable<string> subprotocols,
            Dictionary<string, string> headers = null,
            int maxReceiveBytes = 4096)
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);

            _maxReceiveBytes = maxReceiveBytes;

            JsLibBridge.Initialize();

            _instanceId = JsLibBridge.AddInstance(this, url, subprotocols);
        }

        ~WebGLWebSocket()
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

        public void AddOutgoingMessage(WebSocketMessage message)
        {
            var ret = message.Type == WebSocketDataType.Binary
                ? WebSocketSendBinary(_instanceId, message.Bytes, message.Bytes.Length)
                : WebSocketSendText(_instanceId, message.String);

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

            var ret = WebSocketClose(_instanceId, (int)WebSocketCloseCode.Normal);

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
        private static extern int WebSocketClose(int instanceId, int code);
        [DllImport("__Internal")]
        private static extern int WebSocketSendBinary(int instanceId, byte[] bytes, int length);
        [DllImport("__Internal")]
        private static extern int WebSocketSendText(int instanceId, string message);
        [DllImport("__Internal")]
        private static extern int WebSocketGetState(int instanceId);
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
        #region Marshalled Types
        private delegate void OpenCallback(int instanceId);
        private delegate void BinaryMessageCallback(int instanceId, IntPtr messagePtr, int messageLength);
        private delegate void TextMessageCallback(int instanceId, IntPtr messagePtr);
        private delegate void ErrorCallback(int instanceId, IntPtr errorPtr);
        private delegate void CloseCallback(int instanceId, int closeCode);

        [DllImport ("__Internal")]
        private static extern int WebSocketAllocate(string url);
        [DllImport ("__Internal")]
        private static extern void WebSocketAddSubprotocol(int instanceId, string subprotocol);
        [DllImport ("__Internal")]
        private static extern void WebSocketFree(int instanceId);
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

        #region External Properties
        public static bool IsInitialized { get; private set; }
        #endregion

        #region Internal Properties
        private static Dictionary<int, WebGLWebSocket> Instances { get; } = new Dictionary<int, WebGLWebSocket>();
        #endregion
        
        #region External Methods
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

        public static int AddInstance(WebGLWebSocket instance, string url, IEnumerable<string> subprotocols)
        {
            var instanceId = WebSocketAllocate(url);

            if (subprotocols != null)
            {
                foreach (var subprotocol in subprotocols)
                    WebSocketAddSubprotocol(instanceId, subprotocol);
            }
            
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