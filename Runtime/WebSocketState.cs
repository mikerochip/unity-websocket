namespace MikeSchweitzer.WebSocket
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
        Disconnected,
        DisconnectedFromAppQuit,
    }
}
