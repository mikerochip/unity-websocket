using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace Mikerochip.WebSocket
{
    public enum WebSocketDesiredState
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
        // clean shutdown will result in this state
        Closed,
        // error will result in this state
        // see LastErrorMessage property for the error message
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
        // set and check what state you requested 
        #region Desired State Properties
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
        public WebSocketDesiredState DesiredState { get; private set; }
        #endregion
        
        // check the current state of the connection
        #region Current State Properties
        public string Url => Config?.Url;
        public WebSocketConfig Config { get; private set; }
        public WebSocketState State { get; private set; }
        
        public bool IsConnecting => DesiredState == WebSocketDesiredState.Connect || State == WebSocketState.Connecting;
        public bool IsDisconnecting => DesiredState == WebSocketDesiredState.Disconnect || State == WebSocketState.Disconnecting;
        
        public string ErrorMessage { get; private set; }
        public byte[] LastIncomingMessageBytes => _incomingMessages.LastOrDefault();
        public string LastIncomingMessageString => BytesToString(LastIncomingMessageBytes);
        
        // You probably don't need these and should use the methods instead. These are only here
        // if you really want to manipulate the message Queues directly, for some reason.
        public IEnumerable<byte[]> IncomingMessages => _incomingMessages;
        public IEnumerable<byte[]> OutgoingMessages => _outgoingMessages;
        #endregion
        
        // optional events, raised when current state changes
        #region Public Events
        public event Action<WebSocketConnection> Connected;
        public event Action<WebSocketConnection> Disconnected;
        // check LastIncomingMessage* to know what was received
        public event Action<WebSocketConnection> MessageReceived;
        #endregion

        #region Private Fields
        private NativeWebSocket.WebSocket _webSocket;
        private Task _connectTask;
        private readonly Queue<byte[]> _incomingMessages = new Queue<byte[]>();
        private readonly Queue<byte[]> _outgoingMessages = new Queue<byte[]>();
        #endregion

        #region Public API
        public void Connect(string url = null)
        {
            if (url != null)
                DesiredUrl = url;

            DesiredState = WebSocketDesiredState.Connect;
        }

        public void Disconnect()
        {
            if (DesiredState == WebSocketDesiredState.Connect)
            {
                DesiredState = WebSocketDesiredState.None;
                return;
            }
            
            DesiredState = WebSocketDesiredState.Disconnect;
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
            return _incomingMessages.TryDequeue(out bytes);
        }

        public static byte[] StringToBytes(string str) =>
            str == null ? null : Encoding.UTF8.GetBytes(str);
        
        public static string BytesToString(byte[] bytes) =>
            bytes == null ? null : Encoding.UTF8.GetString(bytes);
        #endregion
        
        #region Unity Methods
        private async void Awake()
        {
            await Task.WhenAll(ManageStateAsync(), ConnectAsync(), ReceiveAsync(), SendAsync());
        }
        #endregion

        #region Internal Async Management
        private async Task ManageStateAsync()
        {
            while (true)
            {
                // handle active states first
                if (State == WebSocketState.Disconnecting)
                {
                    await ShutdownWebSocketAsync();
                    State = ErrorMessage == null ? WebSocketState.Closed : WebSocketState.Error;
                    Disconnected?.Invoke(this);
                }
                
                // process desired states now
                if (DesiredState == WebSocketDesiredState.Connect)
                {
                    DesiredState = WebSocketDesiredState.None;
                    
                    await ShutdownWebSocketAsync();
                    
                    State = WebSocketState.Connecting;
                    InitializeWebSocket();
                    _connectTask = _webSocket.Connect();
                }
                else if (DesiredState == WebSocketDesiredState.Disconnect)
                {
                    DesiredState = WebSocketDesiredState.None;

                    if (_webSocket != null)
                    {
                        _webSocket.CancelConnection();
                        await _webSocket.Close();
                    }
                }

                await Task.Yield();
            }
        }

        private async Task ConnectAsync()
        {
            while (true)
            {
                if (_connectTask != null)
                    await _connectTask;

                await Task.Yield();
            }
        }

        private async Task ReceiveAsync()
        {
            while (true)
            {
                _webSocket?.ProcessIncomingMessages();
                await Task.Yield();
            }
        }

        private async Task SendAsync()
        {
            while (true)
            {
                while (_webSocket?.State == NativeWebSocket.WebSocketState.Open &&
                       _outgoingMessages.TryDequeue(out var bytes))
                {
                    await _webSocket.Send(bytes);
                }

                await Task.Yield();
            }
        }
        #endregion

        #region Internal WebSocket Management
        private void InitializeWebSocket()
        {
            Config = DeepCopy(DesiredConfig);

            ErrorMessage = null;
            
            _webSocket = new NativeWebSocket.WebSocket(Config.Url, Config.Subprotocols, Config.Headers);
            _webSocket.Opened += WebSocketOnOpen;
            _webSocket.MessageReceived += WebSocketOnMessage;
            _webSocket.Closed += WebSocketOnClose;
            _webSocket.Error += WebSocketOnError;
        }

        private async Task ShutdownWebSocketAsync()
        {
            if (_webSocket == null)
                return;
            
            _webSocket.CancelConnection();
            await _connectTask;
            _connectTask = null;
            
            _incomingMessages.Clear();
            _outgoingMessages.Clear();
            
            _webSocket.Opened -= WebSocketOnOpen;
            _webSocket.MessageReceived -= WebSocketOnMessage;
            _webSocket.Closed -= WebSocketOnClose;
            _webSocket.Error -= WebSocketOnError;
            _webSocket = null;
        }

        private static WebSocketConfig DeepCopy(WebSocketConfig src)
        {
            if (src == null)
                return new WebSocketConfig();
            
            return new WebSocketConfig
            {
                Url = src.Url,
                Subprotocols = src.Subprotocols?.ToList(),
                Headers = src.Headers?.ToDictionary(pair => pair.Key, pair => pair.Value),
                MaxReceiveBytes = src.MaxReceiveBytes,
                MaxSendBytes = src.MaxSendBytes,
            };
        }

        private void WebSocketOnOpen()
        {
            State = WebSocketState.Connected;
            Connected?.Invoke(this);
        }

        private void WebSocketOnMessage(byte[] data)
        {
            _incomingMessages.Enqueue(data);
            MessageReceived?.Invoke(this);
        }

        private void WebSocketOnClose(WebSocketCloseCode closeCode)
        {
            State = WebSocketState.Disconnecting;
        }

        private void WebSocketOnError(string errorMsg)
        {
            State = WebSocketState.Disconnecting;
            ErrorMessage = errorMsg;
        }
        #endregion
    }
}
