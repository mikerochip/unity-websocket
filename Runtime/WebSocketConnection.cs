using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Mikerochip.WebSocket
{
    public enum WebSocketPendingState
    {
        None,
        Connect,
        Disconnect,
    }

    public enum WebSocketState
    {
        Invalid,
        Connecting,
        Connected,
        Disconnecting,
        Disconnected,
        Error,
    }

    public class WebSocketConfig
    {
        public string Url { get; set; }
        public List<string> Subprotocols { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public int MaxReceiveBytes { get; set; } = 4096;
        public int MaxSendBytes { get; set; } = 4096;
    }

    public class WebSocketConnection : MonoBehaviour
    {
        #region Desired Properties
        public string DesiredUrl
        {
            get => DesiredConfig?.Url;
            set
            {
                DesiredConfig ??= new WebSocketConfig();
                DesiredConfig.Url = value;
            }
        }
        public WebSocketConfig DesiredConfig { get; set; } = new WebSocketConfig();
        #endregion
        
        #region Active Properties
        public WebSocketPendingState PendingState { get; private set; }
        public WebSocketConfig Config { get; private set; }
        public WebSocketState State { get; private set; }
        public Queue<byte[]> IncomingMessages { get; private set; }
        #endregion
        
        #region Public Events
        public event Action<WebSocketConnection> Connected;
        public event Action<WebSocketConnection> Disconnected;
        public event Action<WebSocketConnection> MessageReceived;
        public event Action<WebSocketConnection> Error;
        #endregion

        #region Private Fields
        private NativeWebSocket.WebSocket _webSocket;
        private Queue<byte[]> _outgoingMessages = new Queue<byte[]>();
        #endregion

        #region Public Methods
        public void Connect(string url = null)
        {
            if (url != null)
                DesiredUrl = url;

            PendingState = WebSocketPendingState.Connect;
        }

        public void Disconnect()
        {
            PendingState = WebSocketPendingState.Disconnect;
        }
        
        public void AddOutgoingMessage(string message)
        {}
        public void AddOutgoingMessage(byte[] message)
        {}

        public bool TryTakeReceivedMessage(out string message)
        {
            message = null;
            return true;
        }

        public bool TryTakeReceivedMessage(out byte[] message)
        {
            message = null;
            return true;
        }
        #endregion

        #region Private Methods
        private WebSocketConfig DeepCopy(WebSocketConfig src)
        {
            var dst = new WebSocketConfig
            {
                Url = src.Url,
                Subprotocols = src.Subprotocols?.ToList(),
                Headers = src.Headers?.ToDictionary(pair => pair.Key, pair => pair.Value),
                MaxReceiveBytes = src.MaxReceiveBytes,
                MaxSendBytes = src.MaxSendBytes,
            };
            return dst;
        }
        #endregion
    }
}
