using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        // see LastError property for the error message
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
        // set these before you connect
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
        
        // read-only properties to query current state
        #region Active Properties
        public WebSocketPendingState PendingState { get; private set; }
        public WebSocketConfig Config { get; private set; }
        public WebSocketState State { get; private set; }
        
        public string LastError { get; private set; }
        public byte[] LastIncomingMessage { get; private set; }
        public string LastIncomingMessageString => BytesToString(LastIncomingMessage);
        
        // Don't use these unless you really need to manipulate the message Queues directly.
        // I recommend casting if you want to alter them.
        public IEnumerable<byte[]> IncomingMessages => _incomingMessages;
        public IEnumerable<byte[]> OutgoingMessages => _outgoingMessages;
        #endregion
        
        // for your convenience
        #region Public Events
        public event Action<WebSocketConnection> Connected;
        public event Action<WebSocketConnection> Disconnected;
        // LastIncomingMessage if you need to know what was received
        public event Action<WebSocketConnection> MessageReceived;
        public event Action<WebSocketConnection> Error;
        #endregion

        #region Private Fields
        private NativeWebSocket.WebSocket _webSocket;
        private readonly Queue<byte[]> _incomingMessages = new Queue<byte[]>();
        private readonly Queue<byte[]> _outgoingMessages = new Queue<byte[]>();
        #endregion

        #region Public API
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

        public void AddOutgoingMessage(string str)
        {
            var bytes = StringToBytes(str);
            _outgoingMessages.Enqueue(bytes);
        }

        public void AddOutgoingMessage(byte[] bytes)
        {
            _outgoingMessages.Enqueue(bytes);
        }

        public bool TryRemoveIncomingMessage(out string str)
        {
            str = null;
            if (!TryRemoveIncomingMessage(out byte[] bytes))
                return false;
            
            str = BytesToString(bytes);
            return true;
        }

        public bool TryRemoveIncomingMessage(out byte[] bytes)
        {
            if (!_incomingMessages.TryDequeue(out bytes))
                return false;

            if (_incomingMessages.Count == 0)
                LastIncomingMessage = null;
            return true;
        }

        public static byte[] StringToBytes(string str) =>
            str == null ? null : Encoding.UTF8.GetBytes(str);
        
        public static string BytesToString(byte[] bytes) =>
            bytes == null ? null : Encoding.UTF8.GetString(bytes);
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
