using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MikeSchweitzer.WebSocket.Internal;
using UnityEngine;

namespace MikeSchweitzer.WebSocket
{
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
        // You probably don't need these and should use the methods instead. These are only here
        // if you really want to manipulate the message Queues directly, for some reason.
        public IEnumerable<WebSocketMessage> IncomingMessages => _incomingMessages;
        public IEnumerable<WebSocketMessage> OutgoingMessages => _outgoingMessages;
        #endregion
        
        #region Public Events
        public delegate void StateChangedHandler(WebSocketConnection connection,
            WebSocketState oldState, WebSocketState newState);
        public delegate void MessageReceivedHandler(WebSocketConnection connection, WebSocketMessage message);
        public delegate void ErrorMessageReceivedHandler(WebSocketConnection connection, string errorMessage);
        
        public event StateChangedHandler StateChanged;
        public event MessageReceivedHandler MessageReceived;
        public event ErrorMessageReceivedHandler ErrorMessageReceived;
        #endregion

        #region Private Properties
        private string OutgoingExceptionMessage => $"State is {State}. Must be {WebSocketState.Connected} to add outgoing messages.";
        #endregion

        #region Private Fields
        private CancellationTokenSource _cts;
        private IWebSocket _webSocket;
        private Task _connectTask;
        private readonly LinkedList<WebSocketMessage> _incomingMessages = new LinkedList<WebSocketMessage>();
        private readonly LinkedList<WebSocketMessage> _outgoingMessages = new LinkedList<WebSocketMessage>();
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
            if (State != WebSocketState.Connected)
            {
                OnError(OutgoingExceptionMessage);
                return;
            }
            
            _outgoingMessages.AddLast(new WebSocketMessage(message));
        }

        public void AddOutgoingMessage(byte[] message)
        {
            if (State != WebSocketState.Connected)
            {
                OnError(OutgoingExceptionMessage);
                return;
            }

            _outgoingMessages.AddLast(new WebSocketMessage(message));
        }

        public bool TryRemoveIncomingMessage(out string message)
        {
            message = null;
            if (_incomingMessages.First == null)
                return false;

            var result = _incomingMessages.First.Value;
            _incomingMessages.RemoveFirst();
            message = result.String;
            return true;
        }

        public bool TryRemoveIncomingMessage(out byte[] message)
        {
            message = null;
            if (_incomingMessages.First == null)
                return false;

            var result = _incomingMessages.First.Value;
            _incomingMessages.RemoveFirst();
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
            _cts = new CancellationTokenSource();
            await Task.WhenAll(ManageStateAsync(), ConnectAsync(), ReceiveAsync(), SendAsync());
        }

        private void OnDestroy()
        {
            if (State == WebSocketState.Connecting || State == WebSocketState.Connected)
            {
                var oldState = State;
                State = WebSocketState.Disconnecting;
                StateChanged?.Invoke(this, oldState, State);
            }
            
            _cts.Cancel();
        }
        #endregion

        #region Internal Async Management
        private async Task ManageStateAsync()
        {
            while (true)
            {
                // process active states first
                if (State == WebSocketState.Disconnecting)
                {
                    await ShutdownWebSocketAsync();
                    var oldState = State;
                    State = WebSocketState.Disconnected;
                    StateChanged?.Invoke(this, oldState, State);
                }

                if (_cts.IsCancellationRequested)
                    break;
                
                // process desired states second
                if (DesiredState == WebSocketDesiredState.Connect)
                {
                    DesiredState = WebSocketDesiredState.None;
                    var oldState = State;
                    State = WebSocketState.Connecting;
                    StateChanged?.Invoke(this, oldState, State);
                    
                    await ShutdownWebSocketAsync();
                    InitializeWebSocket();
                    
                    _connectTask = _webSocket!.ConnectAsync();
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
                if (_cts.IsCancellationRequested)
                    break;
                
                if (_connectTask != null)
                    await _connectTask;

                await Task.Yield();
            }
        }

        private async Task ReceiveAsync()
        {
            while (true)
            {
                if (_cts.IsCancellationRequested)
                    break;
                
                if (_webSocket?.State == Internal.WebSocketState.Open)
                    _webSocket.ProcessIncomingMessages();
                
                await Task.Yield();
            }
        }

        private async Task SendAsync()
        {
            while (true)
            {
                if (_cts.IsCancellationRequested)
                    break;
                
                while (_webSocket?.State == Internal.WebSocketState.Open && _outgoingMessages.First != null)
                {
                    var message = _outgoingMessages.First.Value;
                    _outgoingMessages.RemoveFirst();

                    var size = message.Bytes.Length;
                    if (size > Config.MaxSendBytes)
                    {
                        OnError($"Did not send message of size {size} (max {Config.MaxSendBytes})");
                        continue;
                    }
                    
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
            _incomingMessages.Clear();
            _outgoingMessages.Clear();
            
            if (_webSocket == null)
                return;
            
            if (_cts.IsCancellationRequested)
                _webSocket.Cancel();
            else
                await _webSocket.CloseAsync();
            
            await _connectTask;
            _connectTask = null;
            
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
            var oldState = State;
            State = WebSocketState.Connected;
            StateChanged?.Invoke(this, oldState, State);
        }

        private void OnMessageReceived(WebSocketMessage message)
        {
            _incomingMessages.AddLast(message);
            if (MessageReceived == null)
                return;
            MessageReceived.Invoke(this, message);
            _incomingMessages.RemoveLast();
        }

        private void OnClosed(WebSocketCloseCode closeCode)
        {
            var oldState = State;
            State = WebSocketState.Disconnecting;
            StateChanged?.Invoke(this, oldState, State);
        }

        private void OnError(string errorMessage)
        {
            ErrorMessage = errorMessage;
            ErrorMessageReceived?.Invoke(this, errorMessage);
        }
        #endregion
    }
}
