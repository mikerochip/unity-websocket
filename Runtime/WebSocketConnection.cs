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
        #region Read/Write Desired State Properties
        public WebSocketConfig DesiredConfig { get; set; } = new WebSocketConfig();
        public WebSocketDesiredState DesiredState { get; private set; }
        #endregion

        #region Read-Only Current State Properties
        public string Url => Config?.Url;
        public WebSocketConfig Config { get; private set; }
        public WebSocketState State { get; private set; }
        public string ErrorMessage { get; private set; }
        public bool IsPinging => Config?.PingMessage != null && Config?.PingInterval != TimeSpan.Zero;
        public TimeSpan LastPingPongInterval { get; private set; }

        // You probably don't need these and should use methods and events instead. These are
        // here if you really want to manipulate the underlying collections directly.
        public IEnumerable<WebSocketMessage> IncomingMessages => _incomingMessages;
        public IEnumerable<WebSocketMessage> OutgoingMessages => _outgoingMessages;
        #endregion

        #region Public Events
        public delegate void StateChangedHandler(WebSocketConnection connection, WebSocketState oldState, WebSocketState newState);
        public delegate void MessageReceivedHandler(WebSocketConnection connection, WebSocketMessage message);
        public delegate void ErrorMessageReceivedHandler(WebSocketConnection connection, string errorMessage);
        public delegate void PingSentHandler(WebSocketConnection connection, DateTime timestamp);
        public delegate void PongReceivedHandler(WebSocketConnection connection, DateTime timestamp);

        public event StateChangedHandler StateChanged;
        public event MessageReceivedHandler MessageReceived;
        public event ErrorMessageReceivedHandler ErrorMessageReceived;
        public event PingSentHandler PingSent;
        public event PongReceivedHandler PongReceived;
        #endregion

        #region Private Fields
        private CancellationTokenSource _cancellationTokenSource;
        private IWebSocket _webSocket;
        private Task _connectTask;
        private DateTime _lastPingSentTimestamp;
        private DateTime _lastPongReceivedTimestamp;

        private readonly Queue<WebSocketMessage> _incomingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<WebSocketMessage> _outgoingMessages = new Queue<WebSocketMessage>();
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

            _outgoingMessages.Enqueue(message);
        }

        public bool TryRemoveIncomingMessage(out string message)
        {
            message = null;
            if (_incomingMessages.Count == 0)
                return false;

            var result = _incomingMessages.Dequeue();
            message = result.String;
            return true;
        }

        public bool TryRemoveIncomingMessage(out byte[] message)
        {
            message = null;
            if (_incomingMessages.Count == 0)
                return false;

            var result = _incomingMessages.Dequeue();
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
            _cancellationTokenSource = new CancellationTokenSource();
            await Task.WhenAll(ManageStateAsync(), ConnectAsync());
        }

        private void Update()
        {
            SendOutgoingMessages();
            ReceiveIncomingMessages();
        }

        private void OnDestroy()
        {
            _cancellationTokenSource.Cancel();

            if (State == WebSocketState.Connecting || State == WebSocketState.Connected)
                ChangeState(WebSocketState.Disconnecting);
        }

        private void OnApplicationQuit()
        {
            _cancellationTokenSource.Cancel();

            if (State != WebSocketState.Connecting && State != WebSocketState.Connected)
                return;

            ForceShutdownWebSocket();
            // clear messages after state change so messages are available to event listeners
            ChangeState(WebSocketState.DisconnectedFromAppQuit);
            ClearMessageBuffers();
        }
        #endregion

        #region Internal Async Management
        private async Task ManageStateAsync()
        {
            while (true)
            {
                // process Disconnecting before desired state so we can ensure Disconnecting
                // always leads to Disconnected
                if (State == WebSocketState.Disconnecting)
                {
                    await ShutdownWebSocketAsync();
                    // clear messages after state change so messages are available to event listeners
                    ChangeState(WebSocketState.Disconnected);
                    ClearMessageBuffers();
                }

                // cancellations only happen when destroying or shutting down, so we don't need to
                // process desired states afterward
                if (_cancellationTokenSource.IsCancellationRequested)
                    break;

                if (DesiredState == WebSocketDesiredState.Connect)
                {
                    DesiredState = WebSocketDesiredState.None;
                    ErrorMessage = null;
                    Config = DeepCopy(DesiredConfig);
                    ClearMessageBuffers();
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
                if (_cancellationTokenSource.IsCancellationRequested)
                    break;

                if (_connectTask != null)
                    await _connectTask;

                await Task.Yield();
            }
        }

        private void ReceiveIncomingMessages()
        {
            if (_webSocket?.State == Internal.WebSocketState.Open)
                _webSocket.ProcessIncomingMessages();
        }

        private void SendOutgoingMessages()
        {
            if (_webSocket?.State == Internal.WebSocketState.Open && IsPinging)
            {
                if (ShouldSendPing())
                    _webSocket.AddOutgoingMessage(Config.PingMessage);
            }

            while (_webSocket?.State == Internal.WebSocketState.Open && _outgoingMessages.Count > 0)
            {
                var message = _outgoingMessages.Dequeue();
                _webSocket.AddOutgoingMessage(message);
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
                Config.MaxReceiveBytes,
                Config.CanDebugLog,
                IsPinging);
            _webSocket.Opened += OnOpened;
            _webSocket.MessageSent += OnMessageSent;
            _webSocket.MessageReceived += OnMessageReceived;
            _webSocket.Closed += OnClosed;
            _webSocket.Error += OnError;

            _connectTask = _webSocket.ConnectAsync();
        }

        private async Task ShutdownWebSocketAsync()
        {
            if (_webSocket == null)
                return;

            if (_cancellationTokenSource.IsCancellationRequested)
                _webSocket.Cancel();
            else
                await _webSocket.CloseAsync();

            await _connectTask;

            OnWebSocketShutdown();
        }

        private void ForceShutdownWebSocket()
        {
            if (_webSocket == null)
                return;

            _webSocket.Cancel();

            OnWebSocketShutdown();
        }
        #endregion

        #region Internal WebSocket Events
        private void OnWebSocketShutdown()
        {
            _connectTask = null;

            _webSocket.Opened -= OnOpened;
            _webSocket.MessageSent -= OnMessageSent;
            _webSocket.MessageReceived -= OnMessageReceived;
            _webSocket.Closed -= OnClosed;
            _webSocket.Error -= OnError;
            _webSocket = null;
        }

        private void OnOpened()
        {
            _lastPingSentTimestamp = DateTime.Now;
            _lastPongReceivedTimestamp = _lastPingSentTimestamp;
            LastPingPongInterval = TimeSpan.Zero;

            ChangeState(WebSocketState.Connected);
        }

        private void OnMessageSent(WebSocketMessage message)
        {
            if (IsPinging && ReferenceEquals(message, Config.PingMessage))
            {
                _lastPingSentTimestamp = DateTime.Now;
                PingSent?.Invoke(this, _lastPingSentTimestamp);
            }
        }

        private void OnMessageReceived(WebSocketMessage message)
        {
            if (IsPinging && message.Equals(Config.PingMessage))
            {
                _lastPongReceivedTimestamp = DateTime.Now;
                LastPingPongInterval = _lastPongReceivedTimestamp - _lastPingSentTimestamp;
                PongReceived?.Invoke(this, _lastPongReceivedTimestamp);
                return;
            }

            // we have to always enqueue the message to ensure the public property IncomingMessages
            // is accurate, even though we want to remove the message if there is at least one
            // event listener
            _incomingMessages.Enqueue(message);
            if (MessageReceived == null)
                return;

            MessageReceived.Invoke(this, message);

            // it's possible the message list was manipulated by a listener, so sanity check
            // that the message list isn't empty now
            if (_incomingMessages.Count == 0)
                return;

            // remove the last element of the queue by re-queueing all but the last one, which is
            // not the most efficient use of CPU, but:
            // 1. does not generate as much garbage over time as a LinkedList
            // 2. saves memory vs having another queue just to store the last element
            // 3. maintains API ergonomics for the public property IncomingMessages
            while (true)
            {
                var firstMessage = _incomingMessages.Dequeue();
                if (ReferenceEquals(firstMessage, message))
                    break;
                _incomingMessages.Enqueue(firstMessage);
            }
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
                PingInterval = src.PingInterval,
                PingMessage = src.PingMessage?.Clone(),
                CanDebugLog = src.CanDebugLog,
            };
        }

        private bool ShouldSendPing()
        {
            if (Config.ShouldPingWaitForPong)
            {
                if (_lastPongReceivedTimestamp <= _lastPingSentTimestamp)
                    return false;
            }

            var now = DateTime.Now;
            var lastPingInterval = now - _lastPingSentTimestamp;
            if (lastPingInterval < Config.PingInterval)
                return false;

            return true;
        }

        private void ClearMessageBuffers()
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
