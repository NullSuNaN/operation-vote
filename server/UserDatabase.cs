using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using operation_vote.Server;
using operation_vote.Shared.Extensions;

namespace operation_vote.Interface.Server
{
  /// <summary>
  /// A user database.
  /// Anonymous is stored separately and cannot be access with <see cref="IDictionary"> methods.
  /// </summary>
  public class UserDatabase : IUserContainer, IDisposable
  {
    private const string ConnectionString = "Data Source=users.db;Pooling=True;";
    private readonly object _dbLock = new(); // Ensures serialized execution for SQLite operations

    // High-performance ConcurrentDictionary guarantees thread safety across concurrent connection tasks
    private readonly ConcurrentDictionary<string, User> _cache = new(StringComparer.OrdinalIgnoreCase);

    public User AnonymousUser { get; private set; } = null!;
    public event EventHandler<User>? OnUserRegistered;
    public event EventHandler<User>? OnUserDeleted;

    public UserDatabase(EventHandler<User>? OnUserRegistered = null)
    {
      this.OnUserRegistered = OnUserRegistered;
      InitializeDatabase();
      LoadOrCreateAnonymousUser();
      PreloadUserCache();
    }

    private SqliteConnection CreateOpenConnection()
    {
      var connection = new SqliteConnection(ConnectionString);
      connection.Open();
      return connection;
    }

    private void InitializeDatabase()
    {
      lock (_dbLock)
      {
        using var connection = CreateOpenConnection();
        var createTableQuery = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Name TEXT PRIMARY KEY,
                    ApiKey TEXT NOT NULL,
                    VoteMultiplier INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS SystemSettings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT
                );";

        using var command = new SqliteCommand(createTableQuery, connection);
        command.ExecuteNonQuery();
      }
    }

    private void LoadOrCreateAnonymousUser()
    {
      var apiKey = "42";
      var multiplierStr = GetSetting("AnonymousVoteMultiplier") ?? "1";
      if (!int.TryParse(multiplierStr, out var multiplier)) multiplier = 1;

      AnonymousUser = new("Anonymous", apiKey, multiplier);
      OnUserRegistered?.Invoke(this, AnonymousUser);
    }

    private void PreloadUserCache()
    {
      _cache.Clear();
      lock (_dbLock)
      {
        using var connection = CreateOpenConnection();
        var selectQuery = "SELECT Name, ApiKey, VoteMultiplier FROM Users;";
        using var command = new SqliteCommand(selectQuery, connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
          var user = new User(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
          RegisterUserMonitor(user);
          _cache[user.Name] = user;
          OnUserRegistered?.Invoke(this, user);
        }
      }
    }

    private void HandleUserPropertyChange<T>(object? sender, (T Original, T New) e)
    {
      if (sender is User user && TryGetValue(user.Name, out var cachedUser) && ReferenceEquals(user, cachedUser))
      {
        SqlUpsert(user);
      }
    }

    private void RegisterUserMonitor(User user)
    {
      user.OnApiKeyChange += HandleUserPropertyChange;
      user.OnVoteMultiplierChange += HandleUserPropertyChange;
    }

    private void UnregisterUserMonitor(User user)
    {
      user.OnApiKeyChange -= HandleUserPropertyChange;
      user.OnVoteMultiplierChange -= HandleUserPropertyChange;
    }

    public void UpdateAnonymousUser(int voteMultiplier)
    {
      AnonymousUser.Set("42", voteMultiplier);
      SaveSetting("AnonymousVoteMultiplier", voteMultiplier.ToString());
    }

    #region DB Helper Routines

    private string? GetSetting(string key)
    {
      lock (_dbLock)
      {
        using var connection = CreateOpenConnection();
        var query = "SELECT Value FROM SystemSettings WHERE Key = $key;";
        using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString();
      }
    }

    private void SaveSetting(string key, string value)
    {
      lock (_dbLock)
      {
        using var connection = CreateOpenConnection();
        var query = @"
                INSERT INTO SystemSettings (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = EXCLUDED.Value;";
        using var command = new SqliteCommand(query, connection);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
      }
    }

    private void SqlUpsert(User user)
    {
      lock (_dbLock)
      {
        using var connection = CreateOpenConnection();
        var insertQuery = @"
                INSERT INTO Users (Name, ApiKey, VoteMultiplier) 
                VALUES ($name, $apiKey, $voteMultiplier)
                ON CONFLICT(Name) DO UPDATE SET 
                    ApiKey = EXCLUDED.ApiKey, 
                    VoteMultiplier = EXCLUDED.VoteMultiplier;";

        using var command = new SqliteCommand(insertQuery, connection);
        command.Parameters.AddWithValue("$name", user.Name);
        command.Parameters.AddWithValue("$apiKey", user.ApiKey);
        command.Parameters.AddWithValue("$voteMultiplier", user.VoteMultiplier);
        command.ExecuteNonQuery();
      }
    }

    private bool SqlDelete(string username)
    {
      lock (_dbLock)
      {
        using var connection = CreateOpenConnection();
        var deleteQuery = "DELETE FROM Users WHERE Name = $name;";
        using var command = new SqliteCommand(deleteQuery, connection);
        command.Parameters.AddWithValue("$name", username);
        return command.ExecuteNonQuery() > 0;
      }
    }

    #endregion

    #region IDictionary<string, User> Implementation (Keyed by Username)

    public User this[string key]
    {
      get => _cache[key];
      set
      {
        if (key != value.Name)
          throw new ArgumentException("The dictionary key must exactly match the User object's Name property.");

        SqlUpsert(value);

        // Capture the true baseline reference used by the application cache
        bool newUser = false;
        User actualTrackedUser = _cache.AddOrUpdate(key,
          newValue =>
          {
            RegisterUserMonitor(value);
            newUser=true;
            return value;
          },
          (keyStr, existingUser) =>
          {
            // Mutate the properties on the original live reference
            existingUser.Set(value.ApiKey, value.VoteMultiplier);
            return existingUser;
          });
        if(newUser)
          OnUserRegistered?.Invoke(this, actualTrackedUser);
      }
    }

    public ICollection<string> Keys => _cache.Keys;
    public ICollection<User> Values => _cache.Values;
    public int Count => _cache.Count;
    public bool IsReadOnly => false;

    public void Add(string key, User value)
    {
      if (key != value.Name)
        throw new ArgumentException("The dictionary key must match the User's Name.");

      RegisterUserMonitor(value);
      if (_cache.TryAdd(key, value))
      {
        SqlUpsert(value);
        OnUserRegistered?.Invoke(this, value);
      }
      else
      {
        UnregisterUserMonitor(value); // Rollback tracking if add failed
        throw new ArgumentException($"An element with the key '{key}' already exists.");
      }
    }

    public bool ContainsKey(string key) => _cache.ContainsKey(key);

    public bool Remove(string key)
    {
      if (_cache.TryRemove(key, out var user))
      {
        UnregisterUserMonitor(user);
        SqlDelete(key);
        OnUserDeleted?.Invoke(this, user);
        return true;
      }
      return false;
    }

    public bool TryGetValue(string key, out User value) => _cache.TryGetValue(key, out value!);

    public void Add(KeyValuePair<string, User> item) => Add(item.Key, item.Value);

    public void Clear()
    {
      lock (_dbLock)
      {
        using var connection = CreateOpenConnection();
        using var command = new SqliteCommand("DELETE FROM Users;", connection);
        command.ExecuteNonQuery();
      }

      var items = _cache.ToArray();
      _cache.Clear();

      foreach (var item in items)
      {
        UnregisterUserMonitor(item.Value);
        OnUserDeleted?.Invoke(this, item.Value);
      }
    }

    public bool Contains(KeyValuePair<string, User> item) => ((ICollection<KeyValuePair<string, User>>)_cache).Contains(item);

    public void CopyTo(KeyValuePair<string, User>[] array, int arrayIndex)
    {
      var snapshot = _cache.ToArray();
      ((ICollection<KeyValuePair<string, User>>)snapshot).CopyTo(array, arrayIndex);
    }

    public bool Remove(KeyValuePair<string, User> item)
    {
      if (Contains(item))
      {
        return Remove(item.Key);
      }
      return false;
    }

    public IEnumerator<KeyValuePair<string, User>> GetEnumerator() => _cache.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

    public void Dispose()
    {
      GC.SuppressFinalize(this);
      // Connections are now self-disposing within individual methods, minimizing leak potential.
    }
    #region Console Manager Tool

    public static void RunConsoleManager(UserDatabase? userDB = null, VotingServer? server = null) =>
        ConsoleManager.Run(userDB, server);

    private static class ConsoleManager
    {
      private static bool _strictMode = false;

      public static void Run(UserDatabase? userDB = null, VotingServer? server = null)
      {
        var db = userDB ?? [];
        using var _ = userDB == null ? db : null;
        bool running = true;

        while (running)
        {
          Console.WriteLine("\n=================== User Database Manager ===================");
          Console.WriteLine($"[Current Mode: {(_strictMode ? "STRICT (Allows pure spaces)" : "NORMAL (Trims input & blocks empty values)")}]");
          Console.WriteLine("1. List All Registered Users");
          Console.WriteLine("2. Show Anonymous User Details");
          Console.WriteLine("3. Add New User Profile");
          Console.WriteLine("4. Update Existing User (Separate Properties)");
          Console.WriteLine("5. Query Specific User Details");
          Console.WriteLine("6. List Active Connections of a User");
          Console.WriteLine("7. Unauthorize All Connections of a User");
          Console.WriteLine("8. Configure Anonymous Settings");
          Console.WriteLine("9. Remove User Profile");
          Console.WriteLine("10. List Active Connections of Anonymous");
          Console.WriteLine("11. Toggle Strict Mode Configuration");
          Console.WriteLine("!1. Reset Active Connections of a User and let the client reconnect");
          Console.WriteLine("12. Exit Manager");
          Console.Write("Select operation (1-12): ");

          var choice = Console.ReadLine() ?? "";
          Console.WriteLine();

          switch (choice.Trim())
          {
            case "1":
              ListUsers(db);
              break;
            case "2":
              ViewAnonymous(db);
              break;
            case "3":
              AddNewUser(db);
              break;
            case "4":
              UpdateExistingUser(db);
              break;
            case "5":
              QuerySpecificUser(db);
              break;
            case "6":
              ListUserConnections(db);
              break;
            case "7":
              UnauthorizeUserConnections(db, server);
              break;
            case "8":
              ConfigureAnonymous(db);
              break;
            case "9":
              DeleteUser(db);
              break;
            case "10":
              ListAnonymousConnections(db);
              break;
            case "11":
              _strictMode = !_strictMode;
              Console.WriteLine($"Strict mode successfully altered to: {(_strictMode ? "ENABLED" : "DISABLED")}.");
              break;
            case "12":
              running = false;
              break;
            case "!1":
              ResetUserConnections(db);
              break;
            default:
              Console.WriteLine("Unknown choice. Please re-enter selection.");
              break;
          }
        }
      }

      private static string GetInput(out bool isValid, string fieldName)
      {
        string input = Console.ReadLine() ?? "";
        isValid = true;

        if (!_strictMode)
        {
          input = input.Trim();
          if (string.IsNullOrWhiteSpace(input))
          {
            Console.WriteLine($"Validation Error: '{fieldName}' cannot be empty or match a whitespace pattern.");
            isValid = false;
          }
        }
        else
        {
          // Strict mode requirement: allows pure empty spaces ("   "), only rejects absolute zero length strings
          if (input.Length == 0)
          {
            Console.WriteLine($"Validation Error: String sequence length for '{fieldName}' cannot be completely empty (zero-length).");
            isValid = false;
          }
        }

        return input;
      }

      private static void ListUsers(UserDatabase db)
      {
        Console.WriteLine("--- All Registered Users ---");
        if (db.Count == 0) Console.WriteLine("(No Users Registered)");

        foreach (KeyValuePair<string, User> kvp in db)
        {
          Console.WriteLine($"Username: \"{kvp.Value.Name}\", ApiKey: \"{kvp.Value.ApiKey}\", Multiplier: {kvp.Value.VoteMultiplier}, Connections: {kvp.Value.ConnectedClients.Count}");
        }
      }

      private static void ViewAnonymous(UserDatabase db)
      {
        Console.WriteLine("--- Anonymous User Configuration ---");
        var anon = db.AnonymousUser;
        Console.WriteLine($"Username: \"{anon.Name}\", ApiKey: \"{anon.ApiKey}\", Multiplier: {anon.VoteMultiplier}");
      }

      private static void AddNewUser(UserDatabase db)
      {
        Console.WriteLine("--- Add New User Profile ---");
        Console.Write("Enter New Username: ");
        string username = GetInput(out bool nameValid, "Username");
        if (!nameValid) return;

        if (db.ContainsKey(username))
        {
          Console.WriteLine($"Error: User '{username}' already exists. Use the update option instead.");
          return;
        }

        Console.Write("Enter ApiKey (Password): ");
        string apiKey = GetInput(out bool keyValid, "ApiKey");
        if (!keyValid) return;

        Console.Write("Enter VoteMultiplier (default 1): ");
        if (!int.TryParse(Console.ReadLine(), out int multiplier)) multiplier = 1;

        try
        {
          db.Add(username, new User(Name: username, ApiKey: apiKey, VoteMultiplier: multiplier));
          Console.WriteLine($"Success: Account initialized for '{username}'.");
        }
        catch (Exception ex)
        {
          Console.WriteLine($"Insertion Error: {ex.Message}");
        }
      }

      private static void UpdateExistingUser(UserDatabase db)
      {
        Console.WriteLine("--- Update Existing User ---");
        Console.Write("Enter Targeted Username: ");
        string username = GetInput(out bool nameValid, "Username");
        if (!nameValid) return;

        if (!db.TryGetValue(username, out User? user))
        {
          Console.WriteLine($"Error: User '{username}' was not found in database registry.");
          return;
        }

        Console.WriteLine("\nSelect attribute parameter to mutate:");
        Console.WriteLine("1. Modify ApiKey exclusively");
        Console.WriteLine("2. Modify VoteMultiplier exclusively");
        Console.WriteLine("3. Modify both properties concurrently");
        Console.Write("Choice: ");
        string choice = Console.ReadLine() ?? "";

        string targetApiKey = user.ApiKey;
        int targetMultiplier = user.VoteMultiplier;

        switch (choice.Trim())
        {
          case "1":
            Console.Write("Enter New ApiKey: ");
            targetApiKey = GetInput(out bool keyOk, "ApiKey");
            if (!keyOk) return;
            break;

          case "2":
            Console.Write("Enter New VoteMultiplier: ");
            if (!int.TryParse(Console.ReadLine(), out targetMultiplier))
            {
              Console.WriteLine("Invalid entry. Multiplier fallback to default.");
              return;
            }
            break;

          case "3":
            Console.Write("Enter New ApiKey: ");
            targetApiKey = GetInput(out bool aggregateKeyOk, "ApiKey");
            if (!aggregateKeyOk) return;

            Console.Write("Enter New VoteMultiplier: ");
            if (!int.TryParse(Console.ReadLine(), out targetMultiplier)) return;
            break;

          default:
            Console.WriteLine("Invalid selection. Aborting execution routine.");
            return;
        }

        try
        {
          // Atomically apply parameters to existing live reference safely via the indexer update flow
          if (db.TryGetValue(username, out User? modifiedUser))
            modifiedUser.Set(ApiKey: targetApiKey, VoteMultiplier: targetMultiplier);
          Console.WriteLine($"Success: User parameters updated for '{username}'.");
        }
        catch (Exception ex)
        {
          Console.WriteLine($"Database Mutator Error: {ex.Message}");
        }
      }

      private static void QuerySpecificUser(UserDatabase db)
      {
        Console.WriteLine("--- Query User Profile Details ---");
        Console.Write("Enter Username: ");
        string username = GetInput(out bool nameValid, "Username");
        if (!nameValid) return;

        if (db.TryGetValue(username, out User? user))
        {
          Console.WriteLine($"\n[Match Found]");
          Console.WriteLine($" -> Username:       \"{user.Name}\"");
          Console.WriteLine($" -> ApiKey:         \"{user.ApiKey}\"");
          Console.WriteLine($" -> VoteMultiplier: {user.VoteMultiplier}");
          Console.WriteLine($" -> Active Handles: {user.ConnectedClients.Count} instances connected.");
        }
        else
        {
          Console.WriteLine($"No matching criteria found for user key: \"{username}\".");
        }
      }

      private static void ListUserConnections(UserDatabase db)
      {
        Console.WriteLine("--- Active Connection Log Inspector ---");
        Console.Write("Enter Target Username: ");
        string username = GetInput(out bool nameValid, "Username");
        if (!nameValid) return;

        if (db.TryGetValue(username, out User? user))
        {
          var connectionSnapshot = user.ConnectedClients.Keys.ToList();
          Console.WriteLine($"\nUser '{username}' currently has {connectionSnapshot.Count} active connections:");

          int counter = 1;
          foreach (var client in connectionSnapshot)
          {
            Console.WriteLine($" [{counter++}] ClientId: {client.ClientId} | Protocol Channel: {client.Channel?.GetType().Name ?? "N/A"}");
          }
        }
        else
        {
          Console.WriteLine($"User '{username}' does not exist inside active memory.");
        }
      }

      private static void UnauthorizeUserConnections(UserDatabase db, VotingServer? server)
      {
        Console.WriteLine("--- Unauthorize All Live Client Connections ---");
        Console.Write("Enter Target Username: ");
        string username = GetInput(out bool nameValid, "Username");
        if (!nameValid) return;

        if (db.TryGetValue(username, out User? user))
        {
          int totalDisconnected = 0;

          // Safely snapshot using ReaderWriterLockSlim or loop atomically
          using (user.ConnectedClientsLock.EnterWriteLockAsToken())
          {
            var activeClients = user.ConnectedClients.Keys.ToList();
            totalDisconnected = activeClients.Count;

            List<Task<User>> ExchangeUserTasks = [];
            foreach (var client in activeClients)
            {
              Task<User>? currentTask = server?.ExchangeUser(client, db.AnonymousUser);
              if(currentTask != null)
                ExchangeUserTasks.Add(currentTask);
            }
            Task.WaitAll(ExchangeUserTasks);
          }

          Console.WriteLine($"Action Completed: Displaced {totalDisconnected} active tracking connection links from user '{username}'.");
        }
        else
        {
          Console.WriteLine($"User '{username}' does not exist inside database registry.");
        }
      }

      private static void ConfigureAnonymous(UserDatabase db)
      {
        Console.Write("Enter Anonymous VoteMultiplier (default 1): ");
        if (!int.TryParse(Console.ReadLine(), out int multiplier)) multiplier = 1;

        db.UpdateAnonymousUser(multiplier);
        Console.WriteLine("Anonymous user property successfully reassigned.");
      }

      private static void DeleteUser(UserDatabase db)
      {
        Console.Write("Enter target Username to remove: ");
        string username = GetInput(out bool keyValid, "Username");
        if (!keyValid) return;

        if (db.Remove(username))
        {
          Console.WriteLine($"Success: User '{username}' is completely removed.");
        }
        else
        {
          Console.WriteLine($"Failed: Target user '{username}' is absent.");
        }
      }
      private static void ListAnonymousConnections(UserDatabase db)
      {
        Console.WriteLine("--- Active Connection Log Inspector ---");
        User user = db.AnonymousUser;
        var connectionSnapshot = user.ConnectedClients.Keys.ToList();
        Console.WriteLine($"\nAnonymous currently has {connectionSnapshot.Count} active connections:");

        int counter = 1;
        foreach (var client in connectionSnapshot)
        {
          Console.WriteLine($" [{counter++}] ClientId: {client.ClientId} | Protocol Channel: {client.Channel?.GetType().Name ?? "N/A"}");
        }
      }

      private static void ResetUserConnections(UserDatabase db)
      {
        Console.WriteLine("--- Reset All Live Client Connections ---");
        Console.Write("Enter Target Username: ");
        string username = GetInput(out bool nameValid, "Username");
        if (!nameValid) return;

        if (db.TryGetValue(username, out User? user))
        {
          int totalDisconnected = 0;

          // Safely snapshot using ReaderWriterLockSlim or loop atomically
          using (user.ConnectedClientsLock.EnterWriteLockAsToken())
          {
            var activeClients = user.ConnectedClients.Keys.ToList();
            totalDisconnected = activeClients.Count;

            foreach (var client in activeClients)
            {
              client.Channel.ResetAsync(client);
            }
          }

          Console.WriteLine($"Action Completed: Displaced {totalDisconnected} active tracking connection links from user '{username}'.");
        }
        else
        {
          Console.WriteLine($"User '{username}' does not exist inside database registry.");
        }
      }
    }

    #endregion
  }
}