using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Shared;

namespace operation_vote.Interface.ClientWindow
{
	public class VotingWindow<T> : Window where T : ISocketRequestHandler
	{
		private readonly TextBlock _statusLabel;
		private readonly TextBlock _userLabel;
		private readonly Dictionary<Key, KeyEventArgs> _pressedKeysState = [];
		private readonly HashSet<KeyMappingExtensions.MouseButton> _pressedMouseButtonState = [];
		public readonly VotingClient<T> Client;
		private readonly CancellationTokenSource DisconnectCts = new();
		private readonly CancellationTokenSource PressToCloseCts = new();
		private bool _readyToForceClose = false;
		private bool isBanned = false;

		public VotingWindow(VotingClient<T> client, ConcurrentDictionary<string, Operation.OperationType> keyOp, Action trackActivity)
		{
			Title = "Operation Voting Client";
			Width = 400;
			Height = 200;
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			Client = client;

			Background = Brushes.DimGray;
			TransparencyLevelHint = [WindowTransparencyLevel.None];

			_statusLabel = new TextBlock
			{
				Text = "Focus this window and operate.\nYour actions will be sent to the server.",
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				TextAlignment = TextAlignment.Center,
				FontSize = 16,
				FontWeight = FontWeight.Bold,
				Foreground = Brushes.Lime,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 10) // Adds 10px spacing below this row
			};

			_userLabel = new TextBlock
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				TextAlignment = TextAlignment.Center,
				FontSize = 12,
				FontWeight = FontWeight.DemiBold,
				Foreground = Brushes.Gray,
				TextWrapping = TextWrapping.Wrap
			};
			(_userLabel.Text, _userLabel.Foreground) = GetUserLabel();
			var mainContainer = new StackPanel
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};

			client.OnVoteMultiplierChange += (sender, e) =>
			{
				bool _isBanned = e.New == 0;
				Console.WriteLine($"OnVoteMultiplierChange: {e.Original} -> {e.New}");
				if (Interlocked.Exchange(ref isBanned, _isBanned) != _isBanned || true)
				{
					Avalonia.Threading.Dispatcher.UIThread.Post(() =>
						(_userLabel.Text, _userLabel.Foreground) = GetUserLabel()
					);
				}
			};
			client.OnUserChanged += (sender, user) =>
			{
				Console.WriteLine($"OnUserChanged: {GetUserLabel().Content}");
				Avalonia.Threading.Dispatcher.UIThread.Post(() =>
					(_userLabel.Text, _userLabel.Foreground) = GetUserLabel()
				);
			};
			isBanned = client.VoteMultiplier == 0;

			mainContainer.Children.Add(_statusLabel);
			mainContainer.Children.Add(_userLabel);

			Content = mainContainer;

			client.OnDisconnect += (sender, e) => Avalonia.Threading.Dispatcher.UIThread.Post(HandleDisconnect);

			// KEY DOWN
			KeyDown += async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested)
				{
					if (_readyToForceClose)
					{
						Close();
					}
					return;
				}

				trackActivity();
				string keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				var key = e.Key;
				if (keyOp.TryGetValue(keyStr, out var targetedOpType))
					if (_pressedKeysState.TryAdd(key, e))
					{
						var supportOp = new Operation(targetedOpType, VoteType.Support, []);
						var sendTask = client.SendOperationAsync(supportOp, default);
						_statusLabel.Text = GetStatusLabel();
						await sendTask;
					}
			};

			// KEY UP
			KeyUp += async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				trackActivity();
				string keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				var key = e.Key;
				if (keyOp.TryGetValue(keyStr, out var targetedOpType))
					if (_pressedKeysState.Remove(key))
					{
						var againstOp = new Operation(targetedOpType, VoteType.Against, []);
						var sendTask = client.SendOperationAsync(againstOp, default);
						_statusLabel.Text = GetStatusLabel();
						await sendTask;
					}
			};

			PointerPressed += async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				trackActivity();
				var button = KeyMappingExtensions.GetMouseButtonName(e.Properties);
				if (keyOp.TryGetValue(button.ToString(), out var targetedOpType))
					if (_pressedMouseButtonState.Add(button))
					{
						var supportOp = new Operation(targetedOpType, VoteType.Support, []);
						var sendTask = client.SendOperationAsync(supportOp, default);
						_statusLabel.Text = GetStatusLabel();
						await sendTask;
					}
			};

			PointerReleased += async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				trackActivity();
				var button = KeyMappingExtensions.GetMouseButtonName(e.Properties);
				if (keyOp.TryGetValue(button.ToString(), out var targetedOpType))
					if (_pressedMouseButtonState.Remove(button))
					{
						var against = new Operation(targetedOpType, VoteType.Against, []);
						var sendTask = client.SendOperationAsync(against, default);
						_statusLabel.Text = GetStatusLabel();
						await sendTask;
					}
			};
		}

		public string GetStatusLabel()
		{
			if (_pressedKeysState.Count == 0 && _pressedMouseButtonState.Count == 0) return "No Input";
			StringBuilder builder = new("Active Input: ");
			bool gotFirst = false;
			foreach (var item in _pressedKeysState)
			{
				if (gotFirst) builder.Append(", ");
				else gotFirst = true;
				string name = KeyMappingExtensions.GetJsStyleKeyName(item.Value);
				if (name == " ") name = "Space";
				builder.Append(name);
			}
			foreach (var item in _pressedMouseButtonState)
			{
				if (gotFirst) builder.Append(", ");
				else gotFirst = true;
				builder.Append(item);
			}
			return builder.ToString();
		}
		public (string Content, IBrush Color) GetUserLabel()
		{
			StringBuilder builder = new("");
			IBrush color;

			string? user = Client.User;
			if (user != null)
			{
				builder.Append($"Logged in as {user}");
				if (isBanned)
				{
					builder.Append($", BANNED!");
					color = Brushes.Red;
				}
				else
					color = Brushes.Lime;
			}
			else
			{
				if (isBanned)
				{
					builder.Append("Unauthorized!");
					color = Brushes.Red;
				}
				else
				{
					builder.Append("Anonymous");
					color = Brushes.Lime;
				}
			}

			return (builder.ToString(), color);
		}

		private void HandleDisconnect()
		{
			if (DisconnectCts.IsCancellationRequested) return;
			DisconnectCts.Cancel();

			_statusLabel.Text = "Disconnected and failed to reconnect to the server!";
			_statusLabel.Foreground = Brushes.Olive;

			// Fire off a non-blocking background wait operation
			_ = WaitForKeyCloseAsync();
		}

		private async Task WaitForKeyCloseAsync()
		{
			try
			{
				await Task.Delay(2000, PressToCloseCts.Token);
			}
			catch (TaskCanceledException)
			{
				// Catch safely if window is shut down early by other means
			}

			_statusLabel.Text = "Disconnected and failed to reconnect to the server!\nPress any key to close the client.";
			_readyToForceClose = true;
		}

		protected override void OnClosed(EventArgs e)
		{
			// Force cancellations to unleash any blocked await threads on exit
			DisconnectCts.Cancel();
			PressToCloseCts.Cancel();
			base.OnClosed(e);
		}
	}
}