
namespace MikeSchweitzer.WebSocket
{
    public enum WebSocketDataType
    {
        Binary,
        Text,
    }

    public class WebSocketMessage
    {
        public WebSocketDataType Type { get; }
        public byte[] Bytes => _bytes ?? (_bytes = WebSocketConnection.StringToBytes(_string));
        public string String => _string ?? (_string = WebSocketConnection.BytesToString(_bytes));
 
        private byte[] _bytes;
        private string _string;
       
        public WebSocketMessage(byte[] data)
        {
            Type = WebSocketDataType.Binary;
            _bytes = data;
        }
        public WebSocketMessage(string data)
        {
            Type = WebSocketDataType.Text;
            _string = data;
        }
    }
}
