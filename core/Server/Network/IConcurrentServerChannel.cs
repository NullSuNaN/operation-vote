using System.Runtime.Versioning;

namespace operation_vote.Server.Network
{
    /// <summary>
    /// Concurrent tag.<br/>
    /// If your custom channel is not concurrent, you can use <see cref="ConcurrentChannelWrapper{T}" />
    /// </summary>
    [UnsupportedOSPlatform("browser")]
    public interface IConcurrentServerChannel : IServerChannel;
}