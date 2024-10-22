using System;
using System.Linq;

namespace MikeSchweitzer.WebSocket
{
    public enum WebSocketDataType
    {
        Binary,
        Text,
    }

    public class WebSocketMessage
    {
        #region Public Properties
        public WebSocketDataType Type { get; }
        public byte[] Bytes => _bytes ?? (_stringAsBytes ?? (_stringAsBytes = WebSocketConnection.StringToBytes(_string)));
        public string String => _string ?? (_bytesAsString ?? (_bytesAsString = WebSocketConnection.BytesToString(_bytes)));
        #endregion

        #region Private Fields
        private readonly byte[] _bytes;
        private string _bytesAsString;

        private readonly string _string;
        private byte[] _stringAsBytes;
        #endregion

        #region Public Methods
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

        public WebSocketMessage Clone()
        {
            switch (Type)
            {
                case WebSocketDataType.Binary:
                    return new WebSocketMessage(_bytes.ToArray());

                case WebSocketDataType.Text:
                    return new WebSocketMessage(_string);

                default:
                    throw new NotImplementedException($"Unhandled WebSocketDataType {Type}");
            }
        }
        #endregion

        #region System.Object Overrides
        public override bool Equals(object obj)
        {
            if (!(obj is WebSocketMessage other))
                return false;

            if (this == obj)
                return true;

            if (Type != other.Type)
                return false;

            switch (Type)
            {
                case WebSocketDataType.Binary:
                    return _bytes.SequenceEqual(other._bytes);

                case WebSocketDataType.Text:
                    return _string == other._string;

                default:
                    throw new NotImplementedException($"Unhandled WebSocketDataType {Type}");
            }
        }

        public override int GetHashCode()
        {
            switch (Type)
            {
                case WebSocketDataType.Binary:
                    return (Type, _bytes).GetHashCode();

                case WebSocketDataType.Text:
                    return (Type, _string).GetHashCode();

                default:
                    throw new NotImplementedException($"Unhandled WebSocketDataType {Type}");
            }
        }
        #endregion
    }
}
