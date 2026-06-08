using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using operation_vote.Server;

namespace operation_vote.Interface.Server
{
  /// <summary>
  /// A user database.
  /// Anonymous is stored separately and cannot be access with <see cref="IDictionary"> methods.
  /// </summary>
  public class UserDatabase : IUserContainer, IDisposable
  {
    private readonly SqliteConnection _connection;
    private const string ConnectionString = "Data Source=users.db;Pooling=False;";

    // High-performance ConcurrentDictionary guarantees thread safety across concurrent connection tasks
    private readonly ConcurrentDictionary<string, User> _cache = new(StringComparer.OrdinalIgnoreCase);

    public User AnonymousUser { get; private set; } = null!;
    public event EventHandler<User>? OnUserRegistered;
    public event EventHandler<User>? OnUserDeleted;

    public UserDatabase(EventHandler<User>? OnUserRegistered = null)
    {
      _connection = new SqliteConnection(ConnectionString);
      _connection.Open();
      this.OnUserRegistered = OnUserRegistered;
      InitializeDatabase();
      LoadOrCreateAnonymousUser();
      PreloadUserCache();
    }

    private void InitializeDatabase()
    {
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

      using var command = new SqliteCommand(createTableQuery, _connection);
      command.ExecuteNonQuery();
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
      var selectQuery = "SELECT Name, ApiKey, VoteMultiplier FROM Users;";
      using var command = new SqliteCommand(selectQuery, _connection);
      using var reader = command.ExecuteReader();
      while (reader.Read())
      {
        var user = new User(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
        RegisterUserMonitor(user);
        _cache[user.Name] = user;
        OnUserRegistered?.Invoke(this, user);
      }
    }

    private void HandleUserPropertyChange<T>(object? sender, (T Original, T New) e)
    {
      User? user = (User?)sender;
      if(user!=null && TryGetValue(user.Name, out var _user) && ReferenceEquals(user, _user))
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
      var query = "SELECT Value FROM SystemSettings WHERE Key = $key;";
      using var command = new SqliteCommand(query, _connection);
      command.Parameters.AddWithValue("$key", key);
      return command.ExecuteScalar()?.ToString();
    }

    private void SaveSetting(string key, string value)
    {
      var query = @"
                INSERT INTO SystemSettings (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = EXCLUDED.Value;";
      using var command = new SqliteCommand(query, _connection);
      command.Parameters.AddWithValue("$key", key);
      command.Parameters.AddWithValue("$value", value);
      command.ExecuteNonQuery();
    }

    private void SqlUpsert(User user)
    {
      var insertQuery = @"
                INSERT INTO Users (Name, ApiKey, VoteMultiplier) 
                VALUES ($name, $apiKey, $voteMultiplier)
                ON CONFLICT(Name) DO UPDATE SET 
                    ApiKey = EXCLUDED.ApiKey, 
                    VoteMultiplier = EXCLUDED.VoteMultiplier;";

      using var command = new SqliteCommand(insertQuery, _connection);
      command.Parameters.AddWithValue("$name", user.Name);
      command.Parameters.AddWithValue("$apiKey", user.ApiKey);
      command.Parameters.AddWithValue("$voteMultiplier", user.VoteMultiplier);
      command.ExecuteNonQuery();
    }

    private bool SqlDelete(string username)
    {
      var deleteQuery = "DELETE FROM Users WHERE Name = $name;";
      using var command = new SqliteCommand(deleteQuery, _connection);
      command.Parameters.AddWithValue("$name", username);
      return command.ExecuteNonQuery() > 0;
    }

    #endregion

    #region IDictionary<string, User> Implementation (Keyed by Username)

    public User this[string key]
    {
      get
      {
        return _cache[key];
      }
      set
      {
        if (key != value.Name)
          throw new ArgumentException("The dictionary key must exactly match the User object's Name property.");

        SqlUpsert(value);
        bool isAdd = false;
        User user = _cache.GetOrAdd(key, str=>{
          isAdd=true;
          return value;
        });
        if (isAdd)
        {
          RegisterUserMonitor(user);
          OnUserRegistered?.Invoke(this, value);
        }
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

      if (_cache.TryAdd(key, value))
      {
        RegisterUserMonitor(value);
        SqlUpsert(value);
        OnUserRegistered?.Invoke(this, value);
      }
      else
      {
        throw new ArgumentException($"An element with the key '{key}' already exists.");
      }
    }

    public bool ContainsKey(string key)
    {
      return _cache.ContainsKey(key);
    }

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

    public bool TryGetValue(string key, out User value)
    {
      return _cache.TryGetValue(key, out value!);
    }

    public void Add(KeyValuePair<string, User> item) => Add(item.Key, item.Value);

    public void Clear()
    {
      using var command = new SqliteCommand("DELETE FROM Users;", _connection);
      command.ExecuteNonQuery();

      foreach (var item in _cache)
      {
        UnregisterUserMonitor(item.Value);
        OnUserDeleted?.Invoke(this, item.Value);
      }
      _cache.Clear();

    }

    public bool Contains(KeyValuePair<string, User> item)
    {
      return ((ICollection<KeyValuePair<string, User>>)_cache).Contains(item);
    }

    public void CopyTo(KeyValuePair<string, User>[] array, int arrayIndex)
    {
      // Safe enumeration over an atomic snapshot layout array
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
      _connection?.Dispose();
    }

    #region Console Manager Tool

    public static void RunConsoleManager(UserDatabase? userDB = null) =>
        ConsoleManager.Run(userDB);

    private static class ConsoleManager
    {
      private static bool _strictMode = false;

      public static void Run(UserDatabase? userDB = null)
      {
        using var db = userDB ?? new UserDatabase();
        bool running = true;

        while (running)
        {
          Console.WriteLine("\n=== User Database Manager ===");
          Console.WriteLine($"[Current Mode: {(_strictMode ? "STRICT (No trimming)" : "NORMAL (Trims input)")}]");
          Console.WriteLine("1. List Users");
          Console.WriteLine("2. Show Anonymous");
          Console.WriteLine("3. Add/Update User");
          Console.WriteLine("4. Configure Anonymous");
          Console.WriteLine("5. Remove User");
          Console.WriteLine("6. Toggle Strict Mode");
          Console.WriteLine("7. Exit Manager");
          Console.Write("Select operation: ");

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
              AddOrUpdateUser(db);
              break;
            case "4":
              ConfigureAnonymous(db);
              break;
            case "5":
              DeleteUser(db);
              break;
            case "6":
              _strictMode = !_strictMode;
              Console.WriteLine($"Strict mode {(_strictMode ? "ENABLED" : "DISABLED")}.");
              break;
            case "7":
              running = false;
              break;
            default:
              Console.WriteLine("Unknown choice. Re-enter selection.");
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
            Console.WriteLine($"Validation Error: '{fieldName}' cannot be empty or match whitespace pattern.");
            isValid = false;
          }
        }
        else
        {
          if (input.Length == 0)
          {
            Console.WriteLine($"Validation Error: String sequence length for '{fieldName}' cannot be zero.");
            isValid = false;
          }
        }

        return input;
      }

      private static void ListUsers(UserDatabase db)
      {
        Console.WriteLine("--- All Users ---");
        if (db.Count == 0) Console.WriteLine("(No Users Registered)");

        foreach (KeyValuePair<string, User> kvp in db)
        {
          Console.WriteLine($"Username: \"{kvp.Value.Name}\", ApiKey: \"{kvp.Value.ApiKey}\", Multiplier: {kvp.Value.VoteMultiplier}");
        }
      }

      private static void ViewAnonymous(UserDatabase db)
      {
        Console.WriteLine("--- Anonymous ---");
        var anon = db.AnonymousUser;
        Console.WriteLine($"Username: \"{anon.Name}\", ApiKey: \"{anon.ApiKey}\", Multiplier: {anon.VoteMultiplier}");
      }

      private static void AddOrUpdateUser(UserDatabase db)
      {
        Console.Write("Enter Username: ");
        string username = GetInput(out bool nameValid, "Username");
        if (!nameValid) return;

        Console.Write("Enter ApiKey(Password): ");
        string apiKey = GetInput(out bool keyValid, "ApiKey");
        if (!keyValid) return;

        Console.Write("Enter VoteMultiplier (default 1): ");
        if (!int.TryParse(Console.ReadLine(), out int multiplier)) multiplier = 1;

        try
        {
          if (db.TryGetValue(username, out User? user))
          {
            user.Set(ApiKey: apiKey, VoteMultiplier: multiplier);
          }
          else
          {
            db[username] = new User(Name: username, ApiKey: apiKey, VoteMultiplier: multiplier);
          }
          Console.WriteLine($"Operation complete: Add/Updated '{username}'.");
        }
        catch (Exception ex)
        {
          Console.WriteLine($"Error: {ex.Message}");
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
        Console.Write("Enter target Username: ");
        string username = GetInput(out bool keyValid, "Username");
        if (!keyValid) return;

        if (db.Remove(username))
        {
          Console.WriteLine($"Success: User '{username}' is removed.");
        }
        else
        {
          Console.WriteLine($"Failed: '{username}' is absent.");
        }
      }
    }

    #endregion
  }
}