using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Mikerochip.WebSocket.Internal
{
    internal interface IWebSocket
    {
        event OpenedHandler Opened;
        event MessageReceivedHandler MessageReceived;
        event ErrorHandler Error;
        event ClosedHandler Closed;

        WebSocketState State { get; }

        void ProcessReceivedMessages();
        Task ConnectAsync();
        Task SendAsync(byte[] bytes);
        Task CloseAsync();
    }
    
    internal delegate void OpenedHandler();
    internal delegate void MessageReceivedHandler(byte[] data);
    internal delegate void ErrorHandler(string errorMessage);
    internal delegate void ClosedHandler(WebSocketCloseCode closeCode);

    internal enum WebSocketState
    {
        Connecting,
        Open,
        Closing,
        Closed
    }

    // see https://developer.mozilla.org/en-US/docs/Web/API/CloseEvent/code
    internal enum WebSocketCloseCode
    {
        NotSet = 0,
        Normal = 1000,
        Away = 1001,
        ProtocolError = 1002,
        UnsupportedData = 1003,
        Undefined = 1004,
        NoStatus = 1005,
        Abnormal = 1006,
        InvalidData = 1007,
        PolicyViolation = 1008,
        TooBig = 1009,
        MandatoryExtension = 1010,
        ServerError = 1011,
        TlsHandshakeFailure = 1015
    }

    internal static class WebSocketHelpers
    {
        public static WebSocketCloseCode ConvertCloseCode(int closeCode)
        {
            if (Enum.IsDefined(typeof(WebSocketCloseCode), closeCode))
                return (WebSocketCloseCode)closeCode;
            return WebSocketCloseCode.Undefined;
        }

        public static IWebSocket CreateWebSocket(
            string url,
            IEnumerable<string> subprotocols,
            Dictionary<string, string> headers = null,
            int maxSendBytes = 4096,
            int maxReceiveBytes = 4096)
        {
            #if !UNITY_WEBGL || UNITY_EDITOR
                return new DotNetWebSocket(url, subprotocols, headers, maxSendBytes, maxReceiveBytes);
            #else
                return new WebGLWebSocket(url, subprotocols, headers, maxSendBytes, maxReceiveBytes);
            #endif
        }
    }

}
