using System;
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
        #region Desired State Properties
        public WebSocketConfig DesiredConfig { get; set; } = new WebSocketConfig();
        public WebSocketDesiredState DesiredState { get; private set; }
        #endregion
        
        #region Current State Properties
        public string Url => Config?.Url;
        public WebSocketConfig Config { get; private set; }
        public WebSocketState State { get; private set; }
        public string ErrorMessage { get; private set; }
        
        // You probably don't need these and should use methods and events instead. These are
        // here if you really want to manipulate the underlying collections directly.
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
            {
                if (DesiredConfig == null)
                    DesiredConfig = new WebSocketConfig { Url = url };
                else
                    DesiredConfig.Url = url;
            }

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

        public void AddOutgoingMessage(string data)
        {
            AddOutgoingMessage(new WebSocketMessage(data));
        }

        public void AddOutgoingMessage(byte[] data)
        {
            AddOutgoingMessage(new WebSocketMessage(data));
        }

        public void AddOutgoingMessage(WebSocketMessage message)
        {
            if (State != WebSocketState.Connected)
            {
                OnError($"State is {State}. Must be {WebSocketState.Connected} to add outgoing messages.");
                return;
            }

            var size = message.Bytes.Length;
            if (size > Config.MaxSendBytes)
            {
                OnError($"Outgoing message size {size} exceeded max size {Config.MaxSendBytes}");
                return;
            }

            _outgoingMessages.AddLast(message);
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
            _cts.Cancel();
            
            if (State == WebSocketState.Connecting || State == WebSocketState.Connected)
                ChangeState(WebSocketState.Disconnecting);
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
                    ChangeState(WebSocketState.Disconnected);
                    ClearBuffers();
                }

                if (_cts.IsCancellationRequested)
                    break;
                
                // process desired states second
                if (DesiredState == WebSocketDesiredState.Connect)
                {
                    DesiredState = WebSocketDesiredState.None;
                    ErrorMessage = null;
                    Config = DeepCopy(DesiredConfig);
                    ClearBuffers();
                    ChangeState(WebSocketState.Connecting);
                    
                    await ShutdownWebSocketAsync();
                    try
                    {
                        InitializeWebSocket();
                    }
                    catch (Exception e)
                    {
                        OnError(e.Message);
                        ChangeState(WebSocketState.Invalid);
                    }
                }
                else if (DesiredState == WebSocketDesiredState.Disconnect)
                {
                    DesiredState = WebSocketDesiredState.None;
                    
                    if (State == WebSocketState.Connecting || State == WebSocketState.Connected)
                        ChangeState(WebSocketState.Disconnecting);
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
                    _webSocket.AddOutgoingMessage(message);
                }
                    
                await Task.Yield();
            }
        }
        #endregion

        #region Internal WebSocket Management
        private void InitializeWebSocket()
        {
            _webSocket = WebSocketHelpers.CreateWebSocket(
                Config.Url,
                Config.Subprotocols,
                Config.Headers,
                Config.MaxReceiveBytes);
            _webSocket.Opened += OnOpened;
            _webSocket.MessageReceived += OnMessageReceived;
            _webSocket.Closed += OnClosed;
            _webSocket.Error += OnError;
            
            _connectTask = _webSocket.ConnectAsync();
        }

        private async Task ShutdownWebSocketAsync()
        {
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

        private void OnOpened()
        {
            ChangeState(WebSocketState.Connected);
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
            ChangeState(WebSocketState.Disconnecting);
        }

        private void OnError(string errorMessage)
        {
            ErrorMessage = errorMessage;
            ErrorMessageReceived?.Invoke(this, errorMessage);
        }
        #endregion

        #region Internal State Management
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

        private void ClearBuffers()
        {
            _incomingMessages.Clear();
            _outgoingMessages.Clear();
        }

        private void ChangeState(WebSocketState newState)
        {
            if (State == newState)
                return;
            
            var oldState = State;
            State = newState;
            StateChanged?.Invoke(this, oldState, newState);
        }
        #endregion
    }
}
