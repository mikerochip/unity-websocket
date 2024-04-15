
var LibraryWebSocket =
{
    ///////////////////////////////////////////////////////////////////////////////////////////////
    // Instance Management
    ///////////////////////////////////////////////////////////////////////////////////////////////
    $webSocketState:
    {
        instances: {},
        nextInstanceId: 1,

        openCallback: null,
        binaryMessageCallback: null,
        textMessageCallback: null,
        errorCallback: null,
        closeCallback: null,

        haveDynCall: false
    },

    WebSocketInitialize: function()
    {
        webSocketState.haveDynCall = (typeof dynCall !== 'undefined');
    },

    WebSocketNew: function(url, debugLogging)
    {
        var urlStr = UTF8ToString(url);
        var instanceId = webSocketState.nextInstanceId++;

        webSocketState.instances[instanceId] =
        {
            subprotocols: [],
            url: urlStr,
            ws: null,
            debugLogging: debugLogging
        };

        if (debugLogging)
            console.log("[JSLIB WebSocket] Allocated instance " + instanceId);

        return instanceId;
    },

    WebSocketAddSubprotocol: function(instanceId, subprotocol)
    {
        var subprotocolStr = UTF8ToString(subprotocol);
        webSocketState.instances[instanceId].subprotocols.push(subprotocolStr);
    },

    WebSocketDelete: function(instanceId)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return 0;

        if (instance.debugLogging)
            console.log("[JSLIB WebSocket] Delete instance " + instanceId);

        if (instance.ws && instance.ws.readyState < 2)
            instance.ws.close();

        delete webSocketState.instances[instanceId];

        return 0;
    },

    ///////////////////////////////////////////////////////////////////////////////////////////////
    // Instance API
    ///////////////////////////////////////////////////////////////////////////////////////////////
    WebSocketConnect: function(instanceId)
    {
        var instance = webSocketState.instances[instanceId];
        if (!instance)
            return -1;

        if (instance.ws !== null)
            return -2;

        instance.ws = new WebSocket(instance.url, instance.subprotocols);

        instance.ws.binaryType = 'arraybuffer';

        instance.ws.onopen = function(event)
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + ": connected");

            if (webSocketState.openCallback)
            {
                if (webSocketState.haveDynCall)
                    Module.dynCall_vi(webSocketState.openCallback, instanceId);
                else
                    {{{ makeDynCall('vi', 'webSocketState.openCallback') }}}(instanceId);
            }
        };

        instance.ws.onmessage = function(event)
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + ": received " + event.data);

            if (event.data instanceof ArrayBuffer)
            {
                if (webSocketState.binaryMessageCallback === null)
                    return;

                var dataBuffer = new Uint8Array(event.data);

                var buffer = _malloc(dataBuffer.length);
                HEAPU8.set(dataBuffer, buffer);

                try
                {
                    if (webSocketState.haveDynCall)
                        Module.dynCall_viii(webSocketState.binaryMessageCallback, instanceId, buffer, dataBuffer.length);
                    else
                        {{{ makeDynCall('viii', 'webSocketState.binaryMessageCallback') }}}(instanceId, buffer, dataBuffer.length);
                }
                finally
                {
                    _free(buffer);
                }
            }
            else
            {
                if (webSocketState.textMessageCallback === null)
                    return;

                var length = lengthBytesUTF8(event.data) + 1;
                var buffer = _malloc(length);
                stringToUTF8(event.data, buffer, length);

                try
                {
                    if (webSocketState.haveDynCall)
                        Module.dynCall_vii(webSocketState.textMessageCallback, instanceId, buffer);
                    else
                        {{{ makeDynCall('vii', 'webSocketState.textMessageCallback') }}}(instanceId, buffer);
                }
                finally
                {
                    _free(buffer);
                }
            }
        };

        instance.ws.onerror = function(event)
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + ": error occured");

            if (webSocketState.errorCallback === null)
                return;

			if (webSocketState.haveDynCall)
				Module.dynCall_vi(webSocketState.errorCallback, instanceId);
			else
				{{{ makeDynCall('vi', 'webSocketState.errorCallback') }}}(instanceId);
            
        };

        instance.ws.onclose = function(event)
        {
            if (instance.debugLogging)
                console.log("[JSLIB WebSocket] Instance " + instanceId + ": closed with code " + event.code);

            if (webSocketState.closeCallback)
            {
                if (webSocketState.haveDynCall)
                    Module.dynCall_vii(webSocketState.closeCallback, instanceId, event.code);
                else
                    {{{ makeDynCall('vii', 'webSocketState.closeCallback') }}}(instanceId, event.code);
            }

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
            return -3;
    },

    ///////////////////////////////////////////////////////////////////////////////////////////////
    // Instance Events
    ///////////////////////////////////////////////////////////////////////////////////////////////
    WebSocketSetOpenCallback: function(callback)
    {
        webSocketState.openCallback = callback;
    },

    WebSocketSetBinaryMessageCallback: function(callback)
    {
        webSocketState.binaryMessageCallback = callback;
    },

    WebSocketSetTextMessageCallback: function(callback)
    {
        webSocketState.textMessageCallback = callback;
    },

    WebSocketSetCloseCallback: function(callback)
    {
        webSocketState.closeCallback = callback;
    },

    WebSocketSetErrorCallback: function(callback)
    {
        webSocketState.errorCallback = callback;
    }
};

autoAddDeps(LibraryWebSocket, '$webSocketState');
mergeInto(LibraryManager.library, LibraryWebSocket);
