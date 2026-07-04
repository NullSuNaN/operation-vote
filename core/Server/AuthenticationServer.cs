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
    public static async Task<string?> AuthenticateClientAsync(IUserContainer users, ClientInfo client, Func<string, Task<byte[]>> sendRequest, Func<string, User?, Task> onUserReceived, Func<bool, string, Task> sendResult)
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
      users.TryGetValue(username, out var user);
      await onUserReceived(username, user).ConfigureAwait(false);

      string? expectedApiKey = user?.ApiKey;
      if (expectedApiKey == null || user == null)
      {
        await sendResult(false, "User does not exist.").ConfigureAwait(false);
        return null; // User doesn't exist
      }

      // Compute what the signature *should* be using the local API key copy
      string expectedSignature = ComputeHmacSignature(serverToken, expectedApiKey);

      bool success=false;
      string reason = "Unknown Exception.";
      try
      {
        // Constant-time comparison to prevent timing attacks
        if (CryptographicOperations.FixedTimeEquals(
          Encoding.UTF8.GetBytes(expectedSignature),
          Encoding.UTF8.GetBytes(clientSignature)))
        {
          if(user.TryAddClient(client))
          {
            success=true;
            reason = "Success.";
            return username; // Authentication successful
          }
          else
          {
            reason = "Sessions limit exceed.";
          }
        }
        else
          reason = "Password is incorrect.";

        return null; // Signature mismatch
      }
      catch(Exception e)
      {
        reason = $"Unknown Exception: {e.GetType().Name}";
        reason = $"Unknown Exception: {e.Message}";
        throw;
      }
      finally
      {
        await sendResult(success, reason).ConfigureAwait(false);
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