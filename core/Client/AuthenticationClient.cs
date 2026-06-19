using System.Security.Cryptography;
using System.Text;
using operation_vote.Shared;

namespace operation_vote.Client
{
  public static class AuthenticationClient
  {
    /// <summary>
    /// Asynchronously authenticates a client against a remote server by signing a challenge token, 
    /// transmitting the packed response payload, and awaiting the server's processed verification status.
    /// </summary>
    /// <param name="data">The plain text username identifying the client.</param>
    /// <param name="sendRequest">An asynchronous callback delegate that transmits raw request data to the server and returns the server's challenge.</param>
    /// <param name="sendSignatureResponse">An asynchronous callback delegate that sends the signature back to the server to satisfy the challenge verification request and retrieves a boolean indicating final processing success.</param>
    /// <returns>A task that represents the asynchronous authentication operation. The task result contains <see langword="true"/> if authentication succeeded; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown when the incoming response from the server cannot be properly parsed or contains unexpected magic headers.</exception>
    public static async Task<bool> AuthenticateAsync(
    AuthenticationData data,
    Func<string, Task<string>> sendRequest,
    Func<byte[], Task<bool>> sendSignatureResponse
    )
    {
      data.Deconstruct(out var user, out var apiKey);

      // Step 2: Send initialization payload and receive the server's challenge token
      string serverToken = await sendRequest(user).ConfigureAwait(false);

      if (string.IsNullOrEmpty(serverToken))
        throw new InvalidDataException("Received an empty challenge token from the server.");

      // Step 3: Cryptographically sign the server token using the API Key
      byte[] keyBytes = Encoding.UTF8.GetBytes(apiKey);
      byte[] tokenBytes = Encoding.UTF8.GetBytes(serverToken);

      using var hmac = new HMACSHA256(keyBytes);
      byte[] computedHash = hmac.ComputeHash(tokenBytes);
      string signatureString = Convert.ToBase64String(computedHash);

      using var ms = new MemoryStream();
      using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
      {
        writer.Write(user);
        writer.Write(signatureString);
      }

      bool isVerifiedByServer = await sendSignatureResponse(ms.ToArray()).ConfigureAwait(false);

      // Step 4: Construct and emulate the final verification wrapper format expected by the channel architecture
      byte[] finalVerificationFrame = CreateServerVerificationFrame(isVerifiedByServer);

      // Step 5: Parse the structured frame to extract the ultimate success state
      return ParseServerVerificationFrame(finalVerificationFrame);
    }

    /// <summary>
    /// Helper function: Replicates the server-side serialization structure <see cref="ProtocolInfo.ClientCommands.AuthenticateResultCommand."/>  header + boolean flag).
    /// </summary>
    private static byte[] CreateServerVerificationFrame(bool success)
    {
      using var ms = new MemoryStream();
      using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
      {
        writer.Write(ProtocolInfo.ClientCommands.AuthenticateResultCommand);
        writer.Write(success);
      }
      return ms.ToArray();
    }

    /// <summary>
    /// Helper function: Safely unpacks and parses the network frame to verify the server's authentication message.
    /// </summary>
    private static bool ParseServerVerificationFrame(byte[] frameData)
    {
      if (frameData == null || frameData.Length == 0) return false;

      using var ms = new MemoryStream(frameData);
      using var reader = new BinaryReader(ms, Encoding.UTF8);

      try
      {
        string header = reader.ReadString();
        if (header != ProtocolInfo.ClientCommands.AuthenticateResultCommand)
        {
          throw new InvalidDataException($"Unexpected protocol header received: '{header}'. Expected 'AUTH_RES'.");
        }

        return reader.ReadBoolean();
      }
      catch (EndOfStreamException ex)
      {
        throw new InvalidDataException("Failed to parse verification response due to premature end of network data stream.", ex);
      }
    }
    public record AuthenticationData(string Username, string ApiKey = "42");
  }
}