using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Mikerochip.WebSocket.Internal;
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
        // client or server requested close messages will result in this state
        Closed,
        // errors will disconnect and result in this state - see ErrorMessage property
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

    public enum WebSocketDataType
    {
        Binary,
        Text,
    }

    public class WebSocketMessage
    {
        private byte[] _bytes;
        private string _string;
       
        public WebSocketDataType Type { get; }
        public byte[] Bytes => _bytes ?? (_bytes = WebSocketConnection.StringToBytes(_string));
        public string String => _string ?? (_string = WebSocketConnection.BytesToString(_bytes));
 
        public WebSocketMessage(byte[] bytes)
        {
            Type = WebSocketDataType.Binary;
            _bytes = bytes;
        }
        public WebSocketMessage(string str)
        {
            Type = WebSocketDataType.Text;
            _string = str;
        }
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
        public string ErrorMessage { get; private set; }
        public WebSocketMessage LastIncomingMessage => _incomingMessages.LastOrDefault();
        // You probably don't need these and should use the methods instead. These are only here
        // if you really want to manipulate the message Queues directly, for some reason.
        public IEnumerable<WebSocketMessage> IncomingMessages => _incomingMessages;
        public IEnumerable<WebSocketMessage> OutgoingMessages => _outgoingMessages;
        #endregion
        
        #region Public Events
        public event Action<WebSocketConnection> StateChanged;
        // see LastIncomingMessage* to know what was received
        public event Action<WebSocketConnection> MessageReceived;
        #endregion

        #region Private Fields
        private IWebSocket _webSocket;
        private Task _connectTask;
        private readonly Queue<WebSocketMessage> _incomingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<WebSocketMessage> _outgoingMessages = new Queue<WebSocketMessage>();
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

        public void AddOutgoingMessage(string message)
        {
            _outgoingMessages.Enqueue(new WebSocketMessage(message));
        }

        public void AddOutgoingMessage(byte[] message)
        {
            _outgoingMessages.Enqueue(new WebSocketMessage(message));
        }

        public bool TryRemoveIncomingMessage(out string message)
        {
            message = null;
            if (!_incomingMessages.TryDequeue(out var result))
                return false;

            message = result.String;
            return true;
        }

        public bool TryRemoveIncomingMessage(out byte[] message)
        {
            message = null;
            if (!_incomingMessages.TryDequeue(out var result))
                return false;

            message = result.Bytes;
            return true;
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
                    StateChanged?.Invoke(this);
                }
                
                // process desired states now
                if (DesiredState == WebSocketDesiredState.Connect)
                {
                    DesiredState = WebSocketDesiredState.None;
                    
                    await ShutdownWebSocketAsync();
                    
                    State = WebSocketState.Connecting;
                    InitializeWebSocket();
                    StateChanged?.Invoke(this);
                    
                    _connectTask = _webSocket.ConnectAsync();
                }
                else if (DesiredState == WebSocketDesiredState.Disconnect)
                {
                    DesiredState = WebSocketDesiredState.None;

                    if (_webSocket != null)
                        await _webSocket.CloseAsync();
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
                if (_webSocket?.State == Internal.WebSocketState.Open)
                    _webSocket.ProcessIncomingMessages();
                
                await Task.Yield();
            }
        }

        private async Task SendAsync()
        {
            while (true)
            {
                while (_webSocket?.State == Internal.WebSocketState.Open &&
                       _outgoingMessages.TryDequeue(out var message))
                {
                    if (message.Bytes.Length > Config.MaxSendBytes)
                        continue;
                    
                    _webSocket.AddOutgoingMessage(message);
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
            
            _webSocket = WebSocketHelpers.CreateWebSocket(
                Config.Url,
                Config.Subprotocols,
                Config.Headers,
                Config.MaxReceiveBytes);
            _webSocket.Opened += OnOpened;
            _webSocket.MessageReceived += OnMessageReceived;
            _webSocket.Closed += OnClosed;
            _webSocket.Error += OnError;
        }

        private async Task ShutdownWebSocketAsync()
        {
            if (_webSocket == null)
                return;
            
            await _webSocket.CloseAsync();
            await _connectTask;
            _connectTask = null;
            
            _incomingMessages.Clear();
            _outgoingMessages.Clear();
            
            _webSocket.Opened -= OnOpened;
            _webSocket.MessageReceived -= OnMessageReceived;
            _webSocket.Closed -= OnClosed;
            _webSocket.Error -= OnError;
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

        private void OnOpened()
        {
            State = WebSocketState.Connected;
            StateChanged?.Invoke(this);
        }

        private void OnMessageReceived(WebSocketMessage message)
        {
            _incomingMessages.Enqueue(message);
            MessageReceived?.Invoke(this);
        }

        private void OnClosed(WebSocketCloseCode closeCode)
        {
            State = WebSocketState.Disconnecting;
            StateChanged?.Invoke(this);
        }

        private void OnError(string errorMessage)
        {
            State = WebSocketState.Disconnecting;
            ErrorMessage = errorMessage;
            StateChanged?.Invoke(this);
        }
        #endregion
    }
}
