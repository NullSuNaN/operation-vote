using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace operation_vote.Server
{
  public static class AuthenticationServer
  {
    /// <summary>
    /// Initiates an authentication handshake by creating a random challenge string, sending it via a delegate, 
    /// and verifying the incoming response.
    /// </summary>
    /// <param name="sendRequest">Delegate responsible for transmitting the challenge to the client and returning the client's response payload bytes.</param>
    /// <param name="fetchApiKey">Delegate responsible for fetching the expected api key from the user, return null if the user does not exist, will be called exactly once if there is no exceptions.</param>
    /// <param name="sendResult">Delegate responsible for sending the result back to the client</param>
    /// <returns>The verified username if successful; null if authentication fails.</returns>
    /// <exception cref="ProtocolViolationException">Thrown when the response payload is structurally malformed or corrupted.</exception>
    public static async Task<string?> AuthenticateClientAsync(Func<string, Task<byte[]>> sendRequest, Func<string, Task<string?>> fetchApiKey, Func<bool, Task> sendResult)
    {
      // 1. Generate a cryptographically secure random token string (Base64 encoded)
      byte[] tokenBytes = new byte[32];
      RandomNumberGenerator.Fill(tokenBytes);
      string serverToken = Convert.ToBase64String(tokenBytes);

      // 2. Transmit challenge and await response payload bytes
      byte[] responseBytes = await sendRequest(serverToken).ConfigureAwait(false);

      if (responseBytes == null || responseBytes.Length == 0)
      {
        return null;
      }

      string username;
      string clientSignature;

      // 3. Unpack and Parse the payload
      try
      {
        using var ms = new MemoryStream(responseBytes);
        using var reader = new BinaryReader(ms, Encoding.UTF8);

        // Read structural components packed by the client
        username = reader.ReadString();
        clientSignature = reader.ReadString();
      }
      catch (Exception ex) when (ex is EndOfStreamException || ex is IOException)
      {
        // Throw ProtocolViolationException if framing/parsing fails
        throw new ProtocolViolationException("Failed to parse authentication response payload. Malformed stream framework.");
      }

      // 4. Verification Step: Look up the user's expected API Key

      string? expectedApiKey = await fetchApiKey(username).ConfigureAwait(false);
      if (expectedApiKey == null)
      {
        await sendResult(false).ConfigureAwait(false);
        return null; // User doesn't exist
      }

      // Compute what the signature *should* be using the local API key copy
      string expectedSignature = ComputeHmacSignature(serverToken, expectedApiKey);

      bool success=false;
      try
      {
        // Constant-time comparison to prevent timing attacks
        if (CryptographicOperations.FixedTimeEquals(
          Encoding.UTF8.GetBytes(expectedSignature),
          Encoding.UTF8.GetBytes(clientSignature)))
        {
          success=true;
          return username; // Authentication successful
        }

        return null; // Signature mismatch
      }
      finally
      {
        await sendResult(success).ConfigureAwait(false);
      }
    }

    private static string ComputeHmacSignature(string message, string key)
    {
      byte[] keyBytes = Encoding.UTF8.GetBytes(key);
      byte[] messageBytes = Encoding.UTF8.GetBytes(message);

      using var hmac = new HMACSHA256(keyBytes);
      byte[] hashBytes = hmac.ComputeHash(messageBytes);
      return Convert.ToBase64String(hashBytes);
    }
  }
}