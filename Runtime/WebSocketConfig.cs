using System.Collections.Generic;

namespace Mikerochip.WebSocket
{
    public class WebSocketConfig
    {
        public string Url { get; set; }
        public List<string> Subprotocols { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public int MaxReceiveBytes { get; set; } = 4096;
        public int MaxSendBytes { get; set; } = 4096;
    }
}
