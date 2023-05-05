namespace Mikerochip.WebSocket
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
        // client or server requested close messages will result in this state
        Closed,
        // errors will disconnect and result in this state - see ErrorMessage property
        Error,
    }
}
