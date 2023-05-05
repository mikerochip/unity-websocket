
namespace Mikerochip.WebSocket
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
       
        public WebSocketMessage(byte[] bytes)
        {
            Type = WebSocketDataType.Binary;
            _bytes = bytes;
        }
        public WebSocketMessage(string str)
        {
            Type = WebSocketDataType.Text;
            _string = str;
        }
    }
}
