using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MikeSchweitzer.WebSocket.Internal
{
    internal class DotNetWebSocket : IWebSocket
    {
        #region Private Fields
        private readonly Uri _uri;
        private readonly List<string> _subprotocols;
        private readonly Dictionary<string, string> _headers;
        private readonly int _maxReceiveBytes;
        private readonly bool _suppressKeepAlive;

        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _cancellationToken;
        private ClientWebSocket _socket;
        private bool _closeRequested;

        private readonly Queue<WebSocketMessage> _outgoingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<WebSocketMessage> _incomingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<string> _incomingErrorMessages = new Queue<string>();
        // temp lists are used to reduce garbage when copying from the queues above
        private readonly Queue<WebSocketMessage> _workingOutgoingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<WebSocketMessage> _workingIncomingMessages = new Queue<WebSocketMessage>();
        private readonly Queue<string> _workingIncomingErrorMessages = new Queue<string>();
        #endregion

        #region IWebSocket Events
        public event OpenedHandler Opened;
        public event MessageSentHandler MessageSent;
        public event MessageReceivedHandler MessageReceived;
        public event ErrorHandler Error;
        public event ClosedHandler Closed;
        #endregion

        #region IWebSocket Properties
        public WebSocketState State
        {
            get
            {
                switch (_socket?.State)
                {
                    case System.Net.WebSockets.WebSocketState.Connecting:
                        return WebSocketState.Connecting;

                    case System.Net.WebSockets.WebSocketState.Open:
                        return WebSocketState.Open;

                    case System.Net.WebSockets.WebSocketState.CloseSent:
                    case System.Net.WebSockets.WebSocketState.CloseReceived:
                        return WebSocketState.Closing;

                    case System.Net.WebSockets.WebSocketState.Closed:
                        return WebSocketState.Closed;

                    default:
                        return WebSocketState.Closed;
                }
            }
        }
        #endregion

        #region Ctor/Dtor
        public DotNetWebSocket(
            Uri uri,
            IEnumerable<string> subprotocols,
            Dictionary<string, string> headers,
            int maxReceiveBytes,
            bool suppressKeepAlive)
        {
            _uri = uri;
            _subprotocols = subprotocols?.ToList();
            _headers = headers?.ToDictionary(pair => pair.Key, pair => pair.Value);
            _maxReceiveBytes = maxReceiveBytes;
            _suppressKeepAlive = suppressKeepAlive;

            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationToken = _cancellationTokenSource.Token;
        }

        public void Dispose()
        {
            _socket?.Dispose();
        }
        #endregion

        #region IWebSocket Methods
        public async Task ConnectAsync()
        {
            _socket = new ClientWebSocket();

            if (_suppressKeepAlive)
                _socket.Options.KeepAliveInterval = TimeSpan.Zero;

            try
            {
                if (_subprotocols != null)
                {
                    foreach (var subprotocol in _subprotocols)
                        _socket.Options.AddSubProtocol(subprotocol);
                }

                if (_headers != null)
                {
                    foreach (var header in _headers)
                        _socket.Options.SetRequestHeader(header.Key, header.Value);
                }

                await _socket.ConnectAsync(_uri, _cancellationToken);
                Opened?.Invoke();

                await ThreadPoolReceiveLoopAsync();
            }
            catch (Exception e)
            {
                if (!_cancellationToken.IsCancellationRequested)
                    Error?.Invoke(e.Message);
            }
            finally
            {
                var closeCode = _socket.CloseStatus == null
                    ? WebSocketCloseCode.Abnormal
                    : WebSocketHelpers.ConvertCloseCode((int)_socket.CloseStatus);
                Closed?.Invoke(closeCode);

                ClearMessages();
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationToken = _cancellationTokenSource.Token;
                _socket?.Dispose();
                _socket = null;
                _closeRequested = false;
            }
        }

        public void AddOutgoingMessage(WebSocketMessage message)
        {
            _outgoingMessages.Enqueue(message);
        }

        public async Task ProcessMessagesAsync()
        {
            await ProcessOutgoingMessagesAsync();
            // If we have _tempOutgoingMessages, that means we're currently processing outgoing
            // messages. We always want to process outstanding outgoing before incoming messages,
            // so we want to wait for that to finish first.
            if (_workingOutgoingMessages.Count == 0)
                ProcessIncomingMessages();
        }

        public async Task CloseAsync()
        {
            if (_socket == null)
                return;
            if (_socket.CloseStatus != null)
                return;

            switch (_socket.State)
            {
                case System.Net.WebSockets.WebSocketState.Open:
                    // We have to handle a case where the socket state can be open AND the server
                    // closes the socket unexpectedly (crash, API bug, etc).
                    //
                    // Reference exception:
                    //
                    // System.Net.WebSockets.WebSocketException (0x80004005): The remote party closed the WebSocket connection without completing the close handshake.
                    // ---> System.IO.IOException: Unable to read data from the transport connection: interrupted.
                    // ---> System.Net.Sockets.SocketException: interrupted
                    // --- End of inner exception stack trace ---
                    // at System.Net.Sockets.Socket+AwaitableSocketAsyncEventArgs.ThrowException (System.Net.Sockets.SocketError error) [0x00007] in <14b82fe9461f4a63a5f031a62c71f4f3>:0
                    // at System.Net.Sockets.Socket+AwaitableSocketAsyncEventArgs.GetResult (System.Int16 token) [0x00022] in <14b82fe9461f4a63a5f031a62c71f4f3>:0
                    // at System.Threading.Tasks.ValueTask`1[TResult].get_Result () [0x0002e] in <c816f303bdad4e9a8d8dabcc4fd172eb>:0
                    // at System.Net.WebSockets.ManagedWebSocket.EnsureBufferContainsAsync (System.Int32 minimumRequiredBytes, System.Threading.CancellationToken cancellationToken, System.Boolean throwOnPrematureClosure) [0x000ff] in <14b82fe9461f4a63a5f031a62c71f4f3>:0
                    // at System.Net.WebSockets.ManagedWebSocket.ReceiveAsyncPrivate[TWebSocketReceiveResultGetter,TWebSocketReceiveResult] (System.Memory`1[T] payloadBuffer, System.Threading.CancellationToken cancellationToken, TWebSocketReceiveResultGetter resultGetter) [0x0010d] in <14b82fe9461f4a63a5f031a62c71f4f3>:0
                    // at System.Net.WebSockets.ManagedWebSocket.ReceiveAsyncPrivate[TWebSocketReceiveResultGetter,TWebSocketReceiveResult] (System.Memory`1[T] payloadBuffer, System.Threading.CancellationToken cancellationToken, TWebSocketReceiveResultGetter resultGetter) [0x00788] in <14b82fe9461f4a63a5f031a62c71f4f3>:0
                    // at System.Net.WebSockets.ManagedWebSocket.CloseAsyncPrivate (System.Net.WebSockets.WebSocketCloseStatus closeStatus, System.String statusDescription, System.Threading.CancellationToken cancellationToken) [0x00169] in <14b82fe9461f4a63a5f031a62c71f4f3>:0
                    // at MikeSchweitzer.WebSocket.Internal.DotNetWebSocket.CloseAsync () [0x00080] in ...
                    try
                    {
                        _closeRequested = true;
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        Error?.Invoke(e.Message);
                        _cancellationTokenSource.Cancel();
                    }
                    break;

                case System.Net.WebSockets.WebSocketState.Connecting:
                    _cancellationTokenSource.Cancel();
                    break;
            }
        }

        public void Cancel()
        {
            if (_socket == null)
                return;

            _cancellationTokenSource.Cancel();
        }
        #endregion

        #region Message Processing Methods
        private async Task ProcessOutgoingMessagesAsync()
        {
            while (_outgoingMessages.Count > 0)
                _workingOutgoingMessages.Enqueue(_outgoingMessages.Dequeue());

            while (_workingOutgoingMessages.Count > 0)
            {
                var message = _workingOutgoingMessages.Dequeue();

                if (_socket.State != System.Net.WebSockets.WebSocketState.Open)
                    continue;

                var segment = new ArraySegment<byte>(message.Bytes);
                var type = message.Type == WebSocketDataType.Binary
                    ? WebSocketMessageType.Binary
                    : WebSocketMessageType.Text;
                await _socket.SendAsync(segment, type, endOfMessage: true, _cancellationToken);

                MessageSent?.Invoke(message);
            }
        }

        private void ProcessIncomingMessages()
        {
            lock (_incomingErrorMessages)
            {
                while (_incomingErrorMessages.Count > 0)
                    _workingIncomingErrorMessages.Enqueue(_incomingErrorMessages.Dequeue());
            }
            while (_workingIncomingErrorMessages.Count > 0)
            {
                var message = _workingIncomingErrorMessages.Dequeue();
                Error?.Invoke(message);
            }

            lock (_incomingMessages)
            {
                while (_incomingMessages.Count > 0)
                    _workingIncomingMessages.Enqueue(_incomingMessages.Dequeue());
            }
            while (_workingIncomingMessages.Count > 0)
            {
                var message = _workingIncomingMessages.Dequeue();
                MessageReceived?.Invoke(message);
            }
        }

        // _socket.ReceiveAsync() is a blocking call, and we don't want to block the main
        // thread, so we need to put it on a ThreadPool thread
        private async Task ThreadPoolReceiveLoopAsync()
        {
            await new WaitForThreadPoolRun();

            try
            {
                await ReceiveLoopAsync();
            }
            finally
            {
                // return to the main thread before leaving to guarantee we can make
                // Unity API calls from the call site, once again
                await new WaitForMainThreadUpdate();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new ArraySegment<byte>(new byte[_maxReceiveBytes]);

            WebSocketReceiveResult result = null;
            while (_socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                using (var memoryStream = new MemoryStream())
                {
                    string errorMessage = null;
                    var byteCount = 0;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _cancellationToken);
                        byteCount += result.Count;

                        if (byteCount > _maxReceiveBytes)
                        {
                            // drain message since we're discarding it
                            while (!result.EndOfMessage && !result.CloseStatus.HasValue)
                                result = await _socket.ReceiveAsync(buffer, _cancellationToken);

                            errorMessage = WebSocketHelpers.GetReceiveSizeExceededErrorMessage(byteCount, _maxReceiveBytes);
                            lock (_incomingErrorMessages)
                                _incomingErrorMessages.Enqueue(errorMessage);
                            break;
                        }

                        if (result.CloseStatus.HasValue)
                            goto close;

                        memoryStream.Write(buffer.Array, buffer.Offset, result.Count);
                    } while (!result.EndOfMessage);

                    if (!string.IsNullOrEmpty(errorMessage))
                        continue;

                    memoryStream.Seek(0, SeekOrigin.Begin);
                    var bytes = memoryStream.ToArray();
                    var message = result.MessageType == WebSocketMessageType.Binary
                        ? new WebSocketMessage(bytes)
                        : new WebSocketMessage(System.Text.Encoding.UTF8.GetString(bytes));
                    lock (_incomingMessages)
                        _incomingMessages.Enqueue(message);
                }
            }

            close:
            if (!_closeRequested && result?.CloseStatus.HasValue == true)
                await _socket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, _cancellationToken);
        }

        private void ClearMessages()
        {
            _outgoingMessages.Clear();
            lock (_incomingMessages)
                _incomingMessages.Clear();
            lock (_incomingErrorMessages)
                _incomingErrorMessages.Clear();
            _workingOutgoingMessages.Clear();
            _workingIncomingMessages.Clear();
            _workingIncomingErrorMessages.Clear();
        }
        #endregion
    }

    #region Thread Management
    // this completes as soon as a ThreadPool thread starts
    internal class WaitForThreadPoolRun
    {
        public ConfiguredTaskAwaitable.ConfiguredTaskAwaiter GetAwaiter()
        {
            return Task.Run(() => {}).ConfigureAwait(false).GetAwaiter();
        }
    }

    // this completes as soon as the main thread returns from a coroutine yield
    internal class WaitForMainThreadUpdate : INotifyCompletion
    {
        #region Private Fields
        private Action _continuation;
        #endregion

        #region INotifyCompletion Overrides
        public void OnCompleted(Action continuation)
        {
            _continuation = continuation;
        }
        #endregion

        #region await Support
        public bool IsCompleted { get; private set; }
        public void GetResult() { }
        public WaitForMainThreadUpdate GetAwaiter()
        {
            MainThreadCoroutineRunner.Run(CompleteAfterYieldAsync());
            return this;
        }
        #endregion

        #region Helper Methods
        private IEnumerator CompleteAfterYieldAsync()
        {
            yield return null;
            IsCompleted = true;
            _continuation?.Invoke();
        }
        #endregion
    }

    // this enables running a coroutine on the main thread regardless of the caller's thread
    internal class MainThreadCoroutineRunner : MonoBehaviour
    {
        private static MainThreadCoroutineRunner Instance { get; set; }
        private static SynchronizationContext MainThreadSyncContext { get; set; }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            // this behavior is supposed to be an invisible helper utility to
            // enable awaited tasks on threads to return to the main thread, so
            // we really do not want it to clutter the hierarchy
            gameObject.hideFlags = HideFlags.HideAndDontSave;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            var go = new GameObject(nameof(MainThreadCoroutineRunner));
            Instance = go.AddComponent<MainThreadCoroutineRunner>();
            MainThreadSyncContext = SynchronizationContext.Current;
        }

        internal static void Run(IEnumerator coroutine)
        {
            MainThreadSyncContext.Post(_ => Instance.StartCoroutine(coroutine), null);
        }
    }
    #endregion
}
