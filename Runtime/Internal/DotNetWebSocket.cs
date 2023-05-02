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
        private readonly int _maxSendBytes;
        private readonly int _maxReceiveBytes;
        
        private CancellationTokenSource _cancellationTokenSource;
        private CancellationToken _cancellationToken;
        private ClientWebSocket _socket = new ClientWebSocket();

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
            int maxSendBytes = 4096,
            int maxReceiveBytes = 4096)
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException($"Unsupported protocol: {protocol}");

            _uri = uri;
            _subprotocols = subprotocols?.ToList();
            _headers = headers?.ToDictionary(pair => pair.Key, pair => pair.Value);
            _maxSendBytes = maxSendBytes;
            _maxReceiveBytes = maxReceiveBytes;
        }
        #endregion

        #region IWebSocket Methods
        public void ProcessReceivedMessages()
        {
            if (_incomingMessages.Count == 0)
                return;

            List<byte[]> messages;
            lock (_incomingMessages)
            {
                messages = new List<byte[]>(_incomingMessages);
                _incomingMessages.Clear();
            }

            foreach (var message in messages)
                MessageReceived?.Invoke(message);
        }

        public async Task ConnectAsync()
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationToken = _cancellationTokenSource.Token;

                _socket = new ClientWebSocket();

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

                await ReceiveAsync();
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
                Closed?.Invoke(WebSocketCloseCode.Abnormal);
            }
            finally
            {
                _cancellationTokenSource.Cancel();
                _socket = null;
            }
        }

        public async Task SendAsync(byte[] bytes)
        {
            if (bytes.Length == 0)
                return;
            
            if (bytes.Length > _maxSendBytes)
                throw new ArgumentException($"Tried to send {bytes.Length} bytes (max {_maxSendBytes})");

            if (_socket == null)
                return;

            var buffer = new ArraySegment<byte>(bytes);
            await _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage:true, _cancellationToken);
        }

        public Task CloseAsync()
        {
            switch (State)
            {
                case WebSocketState.Closed:
                case WebSocketState.Closing:
                    break;
                
                default:
                    _cancellationTokenSource?.Cancel();
                    break;
            }
            return Task.CompletedTask;
        }
        #endregion

        #region Internal Methods
        private async Task ReceiveAsync()
        {
            await new WaitForBackgroundThread();

            var closeCode = WebSocketCloseCode.Normal;
            var buffer = new ArraySegment<byte>(new byte[_maxReceiveBytes]);
            try
            {
                while (_socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    using (var ms = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        var bytes = 0;
                        do
                        {
                            result = await _socket.ReceiveAsync(buffer, _cancellationToken);
                            if (_cancellationToken.IsCancellationRequested)
                                break;
                            
                            bytes += result.Count;
                            if (bytes >= _maxReceiveBytes)
                            {
                                Error?.Invoke($"Received {bytes} bytes (max {_maxReceiveBytes}");
                                while (!result.EndOfMessage)
                                    result = await _socket.ReceiveAsync(buffer, _cancellationToken);
                                continue;
                            }
                            
                            ms.Write(buffer.Array, buffer.Offset, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseAsync();
                            closeCode = WebSocketHelpers.ConvertCloseCode((int)result.CloseStatus);
                            break;
                        }
                        
                        ms.Seek(0, SeekOrigin.Begin);
                        lock (_incomingMessages)
                        {
                            _incomingMessages.Enqueue(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception)
            {
                _cancellationTokenSource.Cancel();
                closeCode = WebSocketCloseCode.Abnormal;
            }
            finally
            {
                await new WaitForMainThread();
                Closed?.Invoke(closeCode);
            }
        }
        #endregion
    }
    
    internal class MainThreadUtil : MonoBehaviour
    {
        public static MainThreadUtil Instance { get; private set; }
        public static SynchronizationContext synchronizationContext { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Setup()
        {
            Instance = new GameObject("MainThreadUtil")
                .AddComponent<MainThreadUtil>();
            synchronizationContext = SynchronizationContext.Current;
        }

        public static void Run(IEnumerator waitForUpdate)
        {
            synchronizationContext.Post(_ => Instance.StartCoroutine(
                waitForUpdate), null);
        }

        void Awake()
        {
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);
        }
    }

    internal class WaitForMainThread : CustomYieldInstruction
    {
        public override bool keepWaiting
        {
            get { return false; }
        }

        public MainThreadAwaiter GetAwaiter()
        {
            var awaiter = new MainThreadAwaiter();
            MainThreadUtil.Run(CoroutineWrapper(this, awaiter));
            return awaiter;
        }

        public class MainThreadAwaiter : INotifyCompletion
        {
            Action continuation;

            public bool IsCompleted { get; set; }

            public void GetResult() { }

            public void Complete()
            {
                IsCompleted = true;
                continuation?.Invoke();
            }

            void INotifyCompletion.OnCompleted(Action continuation)
            {
                this.continuation = continuation;
            }
        }

        public static IEnumerator CoroutineWrapper(IEnumerator theWorker, MainThreadAwaiter awaiter)
        {
            yield return theWorker;
            awaiter.Complete();
        }
    }

    internal class WaitForBackgroundThread
    {
        public ConfiguredTaskAwaitable.ConfiguredTaskAwaiter GetAwaiter()
        {
            return Task.Run(() => {}).ConfigureAwait(false).GetAwaiter();
        }
    }
}