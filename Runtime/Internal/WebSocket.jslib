
var LibraryWebSocket =
{
    $webSocketState:
    {
        instances: {},

        lastId: 0,

        onOpen: null,
        onBinaryMessage: null,
        onTextMessage: null,
        onError: null,
        onClose: null
    },

    WebSocketSetOnOpen: function(callback)
    {
        webSocketState.onOpen = callback;
    },

    WebSocketSetOnBinaryMessage: function(callback)
    {
        webSocketState.onBinaryMessage = callback;
    },

    WebSocketSetOnTextMessage: function(callback)
    {
        webSocketState.onTextMessage = callback;
    },

    WebSocketSetOnError: function(callback)
    {
        webSocketState.onError = callback;
    },

    WebSocketSetOnClose: function(callback)
    {
        webSocketState.onClose = callback;
    },

    WebSocketAllocate: function(url, debugLogging)
    {
        var urlStr = UTF8ToString(url);
        var instanceId = ++webSocketState.lastId;

        webSocketState.instances[instanceId] = {
            subprotocols: [],
            url: urlStr,
            ws: null,
            debugLogging: debugLogging
        };

        if (debugLogging)
            console.log("[JSLIB WebSocket] Instance " + instanceId + ": allocated");

        return instanceId;
    },

    WebSocketAddSubprotocol: function(instanceId, subprotocol)
    {
        var subprotocolStr = UTF8ToString(subprotocol);
        webSocketState.instances[instanceId].subprotocols.push(subprotocolStr);
    },

    WebSocketFree: function(instanceId)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return 0;

        if (instance.debugLogging)
            console.log("[JSLIB WebSocket] Instance " + instanceId + ": freeing");

        if (instance.ws && instance.ws.readyState < 2)
            instance.ws.close();

        delete webSocketState.instances[instanceId];

        return 0;
    },

    WebSocketConnect: function(instanceId)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return -1;

        if (instance.ws !== null)
            return -2;

        instance.ws = new WebSocket(instance.url, instance.subprotocols);

        instance.ws.binaryType = 'arraybuffer';

        instance.ws.onopen = function()
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + "}: connected");

            if (webSocketState.onOpen)
                Module.dynCall_vi(webSocketState.onOpen, instanceId);
        };

        instance.ws.onmessage = function(ev)
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + ": received " + ev.data);

            if (ev.data instanceof ArrayBuffer)
            {
                if (webSocketState.onBinaryMessage === null)
                    return;

                var dataBuffer = new Uint8Array(ev.data);

                var buffer = _malloc(dataBuffer.length);
                HEAPU8.set(dataBuffer, buffer);

                try
                {
                    Module.dynCall_viii(webSocketState.onBinaryMessage, instanceId, buffer, dataBuffer.length);
                }
                finally
                {
                    _free(buffer);
                }
            }
            else
            {
                if (webSocketState.onTextMessage === null)
                    return;

                var length = lengthBytesUTF8(ev.data) + 1;
                var buffer = _malloc(length);
                stringToUTF8(ev.data, buffer, length);

                try
                {
                    Module.dynCall_vii(webSocketState.onTextMessage, instanceId, buffer);
                }
                finally
                {
                    _free(buffer);
                }
            }
        };

        instance.ws.onerror = function(ev)
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + ": error occured");

            if (webSocketState.onError)
            {
                var message = "WebSocket error";
                var length = lengthBytesUTF8(message) + 1;
                var buffer = _malloc(length);
                stringToUTF8(message, buffer, length);

                try
                {
                    Module.dynCall_vii(webSocketState.onError, instanceId, buffer);
                }
                finally
                {
                    _free(buffer);
                }
            }
        };

        instance.ws.onclose = function(ev)
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + ": closed with code " + ev.code);

            if (webSocketState.onClose)
                Module.dynCall_vii(webSocketState.onClose, instanceId, ev.code);

            delete instance.ws;
        };

        return 0;
    },

    WebSocketClose: function(instanceId, code)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return -1;

        if (!instance.ws)
            return -3;

        if (instance.ws.readyState === 2)
            return -4;

        if (instance.ws.readyState === 3)
            return -5;

        try
        {
            instance.ws.close(code);
        }
        catch (err)
        {
            return -7;
        }

        return 0;
    },

    WebSocketSendBinary: function(instanceId, bufferPtr, bufferLength)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return -1;

        if (!instance.ws)
            return -3;

        if (instance.ws.readyState !== 1)
            return -6;

        instance.ws.send(HEAPU8.buffer.slice(bufferPtr, bufferPtr + bufferLength));

        return 0;
    },

    WebSocketSendText: function(instanceId, text)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return -1;

        if (!instance.ws)
            return -3;

        if (instance.ws.readyState !== 1)
            return -6;

        var textStr = UTF8ToString(text);
        instance.ws.send(textStr);

        return 0;
    },

    WebSocketGetState: function(instanceId)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return -1;

        if (instance.ws)
            return instance.ws.readyState;
        else
            return 3;
    }
};

autoAddDeps(LibraryWebSocket, '$webSocketState');
mergeInto(LibraryManager.library, LibraryWebSocket);
