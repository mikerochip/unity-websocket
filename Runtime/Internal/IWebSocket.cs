using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MikeSchweitzer.WebSocket.Internal
{
    internal interface IWebSocket
    {
        event OpenedHandler Opened;
        event MessageSentHandler MessageSent;
        event MessageReceivedHandler MessageReceived;
        event ErrorHandler Error;
        event ClosedHandler Closed;

        WebSocketState State { get; }

        Task ConnectAsync();
        void AddOutgoingMessage(WebSocketMessage message);
        Task ProcessMessagesAsync();
        Task CloseAsync();
        void Cancel();
    }

    internal delegate void OpenedHandler();
    internal delegate void MessageSentHandler(WebSocketMessage message);
    internal delegate void MessageReceivedHandler(WebSocketMessage message);
    internal delegate void ErrorHandler(string errorMessage);
    internal delegate void ClosedHandler(WebSocketCloseCode code, string reason);

    internal enum WebSocketState
    {
        Connecting,
        Open,
        Closing,
        Closed,
    }

    internal static class WebSocketHelpers
    {
        public static WebSocketCloseCode ConvertCloseCode(int code)
        {
            if (Enum.IsDefined(typeof(WebSocketCloseCode), code))
                return (WebSocketCloseCode)code;
            return WebSocketCloseCode.NoCodeProvided;
        }

        public static string GetReceiveSizeExceededErrorMessage(int bytes, int maxBytes)
        {
            return $"Incoming message size {bytes} exceeded max size {maxBytes}";
        }

        public static IWebSocket CreateWebSocket(
            string url,
            IEnumerable<string> subprotocols,
            Dictionary<string, string> headers,
            int maxReceiveBytes,
            bool debugLogging,
            bool dotNetSuppressKeepAlive,
            byte[] dotNetSelfSignedCert,
            char[] dotNetSelfSignedCertPassword)
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme;
            if (!protocol.Equals("ws") && !protocol.Equals("wss"))
                throw new ArgumentException($"Unsupported protocol: {protocol}");

            #if !UNITY_WEBGL || UNITY_EDITOR
                return new DotNetWebSocket(uri,
                    subprotocols,
                    headers,
                    maxReceiveBytes,
                    dotNetSuppressKeepAlive,
                    dotNetSelfSignedCert,
                    dotNetSelfSignedCertPassword);
            #else
                return new WebGLWebSocket(uri,
                    subprotocols,
                    maxReceiveBytes,
                    debugLogging);
            #endif
        }
    }
}
