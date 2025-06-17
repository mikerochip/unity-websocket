namespace MikeSchweitzer.WebSocket
{
    // https://datatracker.ietf.org/doc/html/rfc6455#section-7.4
    // https://developer.mozilla.org/en-US/docs/Web/API/CloseEvent/code
    // https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.websocketclosestatus?view=netstandard-2.1
    public enum WebSocketCloseCode
    {
        Normal = 1000,
        Away = 1001,
        ProtocolError = 1002,
        UnsupportedMessageType = 1003,
        Reserved = 1004,
        NoCodeProvided = 1005,
        Abnormal = 1006,
        DataInconsistentWithMessageType = 1007,
        PolicyViolation = 1008,
        MessageTooBig = 1009,
        MandatoryExtension = 1010,
        InternalServerError = 1011,
        TlsHandshakeFailure = 1015,
    }
}
