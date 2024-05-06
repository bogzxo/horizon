// Helper class to manage websocket as well as aggregate all packet handlers and dispatch their respective events
class PacketEventDispatcher {
    constructor() {
        this.packetHandlers = [];
        this.socket = new WebSocket("ws://127.0.0.1:8080/dash");

        window.onbeforeunload = this.closeSocket.bind(this);

        this.socket.onmessage = this.handleMessage.bind(this);
    }

    closeSocket() {
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

    handleMessage(event) {
        const csObj = JSON.parse(event.data);

        if (this.packetHandlers[csObj.PacketID]!= undefined)
            this.packetHandlers[csObj.PacketID](csObj);
    }
}

eventDispatcher = new PacketEventDispatcher();