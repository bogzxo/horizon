// Helper class to manage websocket as well as aggregate all packet handlers and dispatch their respective events
class PacketEventDispatcher {
    constructor() {
        this.packetHandlers = [];
        this.isConnected = false;

        window.onbeforeunload = this.closeSocket.bind(this);
        
        this.socket = new WebSocket("ws://127.0.0.1:8080/dash");
        this.socket.onopen = this.onOpenSocket.bind(this);

        this.socket.onmessage = this.handleMessage.bind(this);
        this.socket.onclose = this.onCloseSocket.bind(this);
    }

    onOpenSocket() {
        this.isConnected = true;
    }

    onCloseSocket() {
        this.isConnected = false;
    }

    closeSocket() {
        if (!this.isConnected)
            throw new Error("Cannot close a closed socket.");
        
        this.socket.close();
    }

    registerPacketHandler(packetID, handler) {
        if (typeof handler !== "function")
            throw new Error("Packet handler must be a function");
        if (typeof packetID !== "number")
            throw new Error("Packet ID must be a number");
        if (this.packetHandlers[packetID]!== undefined)
            throw new Error("Packet ID already registered");

        this.packetHandlers[packetID] = handler;
    }

    sendPacket(payload) {
        if (!this.isConnected)
            throw new Error("Cannot send a packet to a closed socket.");
        if (typeof payload !== "object")
            throw new Error("Packet payload must be an object");
        this.socket.send(JSON.stringify(payload));
    }

    handleMessage(event) {
        const csObj = JSON.parse(event.data);

        if (this.packetHandlers[csObj.PacketID]!= undefined)
            this.packetHandlers[csObj.PacketID](csObj);
    }
}

eventDispatcher = new PacketEventDispatcher();