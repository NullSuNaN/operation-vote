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
    public static async Task<(bool success, string reason)> AuthenticateAsync(
    AuthenticationData data,
    Func<string, Task<string>> sendRequest,
    Func<byte[], Task<(bool success, string reason)>> sendSignatureResponse
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

      return await sendSignatureResponse(ms.ToArray()).ConfigureAwait(false);
    }
    public record AuthenticationData(string Username, string ApiKey = "42");
  }
}