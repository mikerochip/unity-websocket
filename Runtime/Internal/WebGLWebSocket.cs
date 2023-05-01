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
        private readonly int _maxSendBytes;
        private readonly int _maxReceiveBytes;
        private readonly int _instanceId;
        // incoming message buffering isn't strictly necessary, it's for API consistency with
        // the System.Net.WebSockets path
        private readonly Queue<byte[]> _incomingMessages = new Queue<byte[]>();
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
            int maxSendBytes = 4096,
            int maxReceiveBytes = 4096)
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException("Unsupported protocol: " + protocol);

            _maxSendBytes = maxSendBytes;
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
        public void ProcessReceivedMessages()
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
            if (bytes.Length > _maxSendBytes)
                throw new ArgumentException($"Tried to send {bytes.Length} bytes (max {_maxSendBytes})");
            
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
        public void OnMessage(byte[] bytes)
        {
            if (bytes.Length > _maxReceiveBytes)
            {
                Error?.Invoke($"Received {bytes.Length} bytes (max {_maxReceiveBytes}");
                return;
            }
            
            _incomingMessages.Enqueue(bytes);
        }
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
        private static Dictionary<int, WebGLWebSocket> Instances { get; } = new Dictionary<int, WebGLWebSocket>();
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

        public static int AddInstance(WebGLWebSocket instance, string url, IEnumerable<string> subprotocols)
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
                var bytes = new byte[msgSize];
                Marshal.Copy(msgPtr, bytes, 0, msgSize);

                instance.OnMessage(bytes);
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
}