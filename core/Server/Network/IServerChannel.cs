using System.Runtime.Versioning;

namespace operation_vote.Server.Network
{
    [UnsupportedOSPlatform("browser")]
    public interface IServerChannel : IDisposable
    {
        /// <summary> Fires when a fresh client joins via this specific network protocol. </summary>
        event EventHandler<ClientInfo> OnChannelClientConnected;

        /// <summary> Fires when a connection breaks or drops. </summary>
        event EventHandler<(ClientInfo Client, string Reason)> OnChannelClientDisconnected;

        /// <summary> Fires when a fully framed payload block is correctly collected off the wire. </summary>
        event EventHandler<(ClientInfo Client, byte[] Payload)> OnChannelDataReceived;

        /// <summary> Fires when a message is sent to the client. </summary>
        event EventHandler<(ClientInfo Client, byte[] Payload)> OnChannelDataSent;

        /// <summary> Starts listening for connections on the designated endpoint configuration. </summary>
        Task StartAsync(User unauthorizedUser);

        /// <summary> Abort the connection and let the client reconnect. </summary>
        Task ResetAsync(ClientInfo Client);

        /// <summary> Sends raw bytes down to a specific client channel session. </summary>
        Task SendToClientAsync(ClientInfo Client, byte[] data);

        /// <summary> Broadcasts a raw payload to all alive clients linked inside this channel protocol layer. </summary>
        Task BroadcastAsync(byte[] data);

        /// <summary> Terminates network channels immediately. </summary>
        void Stop();
    }
}