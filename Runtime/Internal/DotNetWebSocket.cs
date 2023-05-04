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

namespace Mikerochip.WebSocket.Internal
{
    internal class DotNetWebSocket : IWebSocket
    {
        #region Private Fields
        private readonly Uri _uri;
        private readonly List<string> _subprotocols;
        private readonly Dictionary<string, string> _headers;
        private readonly int _maxReceiveBytes;
        
        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _cancellationToken;
        private ClientWebSocket _socket;

        private readonly Queue<byte[]> _incomingMessages = new Queue<byte[]>();
        private readonly Queue<ArraySegment<byte>> _outgoingMessages = new Queue<ArraySegment<byte>>();
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
            string url,
            IEnumerable<string> subprotocols,
            Dictionary<string, string> headers = null,
            int maxReceiveBytes = 4096)
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException($"Unsupported protocol: {protocol}");

            _uri = uri;
            _subprotocols = subprotocols?.ToList();
            _headers = headers?.ToDictionary(pair => pair.Key, pair => pair.Value);
            _maxReceiveBytes = maxReceiveBytes;
            
            _cancellationTokenSource = new CancellationTokenSource();
            _cancellationToken = _cancellationTokenSource.Token;
        }

        public void Dispose()
        {
            _socket?.Dispose();
        }
        #endregion

        #region IWebSocket Methods
        public void ProcessIncomingMessages()
        {
            List<byte[]> messages;
            lock (_incomingMessages)
            {
                if (_incomingMessages.Count == 0)
                    return;

                messages = new List<byte[]>(_incomingMessages);
                _incomingMessages.Clear();
            }

            foreach (var message in messages)
                MessageReceived?.Invoke(message);
        }

        public void AddOutgoingMessage(byte[] bytes)
        {
            lock (_outgoingMessages)
            {
                _outgoingMessages.Enqueue(new ArraySegment<byte>(bytes));
            }
        }

        public async Task ConnectAsync()
        {
            _socket = new ClientWebSocket();
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

                // don't block the main thread while pumping messages
                await new WaitForBackgroundThreadStart();

                await Task.WhenAll(ReceiveAsync(), SendAsync());
                
                // return to the main thread before leaving
                await new WaitForMainThreadUpdate();
            }
            catch (Exception e)
            {
                // events should always be invoked on main thread so listeners don't need to
                // be trapped in a background thread
                await new WaitForMainThreadUpdate();
                
                if (!(e is OperationCanceledException))
                    Error?.Invoke(e.Message);
            }
            finally
            {
                var closeCode = _socket.CloseStatus == null
                    ? WebSocketCloseCode.Abnormal
                    : WebSocketHelpers.ConvertCloseCode((int)_socket.CloseStatus);
                Closed?.Invoke(closeCode);
                
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationToken = _cancellationTokenSource.Token;
                _socket?.Dispose();
                _socket = null;
            }
        }

        public async Task CloseAsync()
        {
            if (_socket == null)
                return;
            
            switch (_socket.State)
            {
                case System.Net.WebSockets.WebSocketState.Open:
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    break;
                
                default:
                    _cancellationTokenSource?.Cancel();
                    break;
            }
        }
        #endregion

        #region Internal Methods
        private async Task ReceiveAsync()
        {
            var buffer = new ArraySegment<byte>(new byte[_maxReceiveBytes]);
            while (_socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    var bytes = 0;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, _cancellationToken);
                        
                        bytes += result.Count;
                        if (bytes > _maxReceiveBytes)
                        {
                            while (!result.EndOfMessage)
                                result = await _socket.ReceiveAsync(buffer, _cancellationToken);
                            break;
                        }

                        if (result.CloseStatus != null)
                            break;
                        
                        ms.Write(buffer.Array, buffer.Offset, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.CloseStatus != null)
                    {
                        await _socket.CloseAsync(
                            result.CloseStatus.Value,
                            result.CloseStatusDescription,
                            CancellationToken.None);
                        break;
                    }

                    if (bytes > _maxReceiveBytes)
                        continue;

                    lock (_incomingMessages)
                    {
                        ms.Seek(0, SeekOrigin.Begin);
                        _incomingMessages.Enqueue(ms.ToArray());
                    }
                }
            }
        }

        private async Task SendAsync()
        {
            while (_socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                ArraySegment<byte> segment;
                lock (_outgoingMessages)
                {
                    segment = _outgoingMessages.Dequeue();
                }
                await _socket.SendAsync(segment, WebSocketMessageType.Binary, endOfMessage: true, _cancellationToken);
            }
        }
        #endregion
    }
    
    internal class MainThreadAsyncAwaitRunner : MonoBehaviour
    {
        private static MainThreadAsyncAwaitRunner Instance { get; set; }
        private static SynchronizationContext SynchronizationContext { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            var go = new GameObject(nameof(MainThreadAsyncAwaitRunner));
            Instance = go.AddComponent<MainThreadAsyncAwaitRunner>();
            SynchronizationContext = SynchronizationContext.Current;
        }

        public static void Run(IEnumerator routine)
        {
            SynchronizationContext.Post(_ => Instance.StartCoroutine(routine), null);
        }

        private void Awake()
        {
            // make the object persist, but don't let it clutter the hierarchy
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
        }
    }

    internal class WaitForMainThreadUpdate
    {
        // this completes as soon as we can return to the main thread
        public TaskAwaiter<bool> GetAwaiter()
        {
            var tcs = new TaskCompletionSource<bool>();
            MainThreadAsyncAwaitRunner.Run(Wait(tcs));
            return tcs.Task.GetAwaiter();
        }

        private static IEnumerator Wait(TaskCompletionSource<bool> tcs)
        {
            yield return new WaitUntil(() => true);
            tcs.SetResult(true);
        }
    }

    internal class WaitForBackgroundThreadStart
    {
        // this completes as soon as we can start a ThreadPool thread
        public ConfiguredTaskAwaitable.ConfiguredTaskAwaiter GetAwaiter()
        {
            return Task.Run(() => {}).ConfigureAwait(false).GetAwaiter();
        }
    }
}