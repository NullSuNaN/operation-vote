using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using operation_vote.Client;
using operation_vote.Client.Request;
using operation_vote.Interface.Shared;
using operation_vote.Shared;

namespace operation_vote.Interface.ClientWindow
{
	public class VotingWindow<T> : Window where T : ISocketRequestHandler
	{
		private readonly TextBlock _statusLabel;
		private readonly TextBlock _userLabel;
		private readonly Dictionary<Key, KeyEventArgs> _pressedKeysState = [];
		private readonly HashSet<MouseButton> _pressedMouseButtonState = [];
		public readonly VotingClient<T> Client;
		private readonly CancellationTokenSource DisconnectCts = new();
		private readonly CancellationTokenSource PressToCloseCts = new();
		private readonly ClientManager<T> ClientManager;
		private bool _readyToForceClose = false;
		private bool isBanned = false;
		private readonly ConcurrentDictionary<Key, object?> _currentlyHeldKeys = [];

		public VotingWindow(VotingClient<T> client, ClientManager<T> clientManager)
		{
			Title = "Operation Voting Client";
			Width = 400;
			Height = 200;
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			Client = client;
			ClientManager = clientManager;

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
			var mainContainer = new StackPanel
			{
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};

			client.OnVoteMultiplierChange += (sender, e) =>
			{
				bool _isBanned = e.New == 0;
				if (Interlocked.Exchange(ref isBanned, _isBanned) != _isBanned)
				{
					Avalonia.Threading.Dispatcher.UIThread.Post(() =>
						(_userLabel.Text, _userLabel.Foreground) = GetUserLabel()
					);
				}
			};
			client.OnUserChanged += (sender, user) =>
			{
				Avalonia.Threading.Dispatcher.UIThread.Post(() =>
					(_userLabel.Text, _userLabel.Foreground) = GetUserLabel()
				);
			};
			isBanned = client.VoteMultiplier == 0;
			Avalonia.Threading.Dispatcher.UIThread.Post(() =>
				(_userLabel.Text, _userLabel.Foreground) = GetUserLabel()
			);

			mainContainer.Children.Add(_statusLabel);
			mainContainer.Children.Add(_userLabel);

			Content = mainContainer;

			client.OnDisconnect += (sender, e) => Avalonia.Threading.Dispatcher.UIThread.Post(HandleDisconnect);

			// KEY DOWN
			AddHandler(KeyDownEvent, async (sender, e) =>
			{
				if (!_currentlyHeldKeys.TryAdd(e.Key, null)) return;
				if (DisconnectCts.IsCancellationRequested)
				{
					if (_readyToForceClose)
					{
						Close();
					}
					return;
				}

				string keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				var key = e.Key;
				await clientManager.RunWithOpType(async data =>
				{
					if (data != null && _pressedKeysState.TryAdd(key, e))
					{
						var supportOp = new Operation(data.Value.Type, VoteType.Support, []);
						Task<bool>? sendTask = null;
						ClientManager.LockClient(
							token => sendTask = client.SendOperationAsync(supportOp, token),
							data.Value.Type,
							VoteType.Support);
						Avalonia.Threading.Dispatcher.UIThread.Post(() => _statusLabel.Text = GetStatusLabel());
						if (!await sendTask!)
							await client.DisposeAsync();
					}
					else
						ClientManager.LockClient(null);
				}, keyStr);
			});

			// KEY UP
			AddHandler(KeyUpEvent, async (sender, e) =>
			{
				if (!_currentlyHeldKeys.TryRemove(e.Key, out _)) return;
				if (DisconnectCts.IsCancellationRequested) return;
				string keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				var key = e.Key;
				await ClientManager.RunWithOpType(async data =>
				{
					if (data != null && _pressedKeysState.Remove(key))
					{
						var againstOp = new Operation(data.Value.Type, VoteType.Against, []);
						Task<bool>? sendTask = null;
						ClientManager.LockClient(
							token => sendTask = client.SendOperationAsync(againstOp, token),
							data.Value.Type,
							VoteType.Support);
						Avalonia.Threading.Dispatcher.UIThread.Post(() => _statusLabel.Text = GetStatusLabel());
						try
						{
							if (!await sendTask!)
								await client.DisposeAsync();
						}
						catch (TaskCanceledException) {}
					}
					else
						ClientManager.LockClient(null);
				}, keyStr);
			});

			AddHandler(PointerPressedEvent, async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind.GetMouseButton();
				await ClientManager.RunWithOpType(async data =>
				{
					if (data != null && _pressedMouseButtonState.Add(button))
					{
						var supportOp = new Operation(data.Value.Type, VoteType.Support, []);
						Task<bool>? sendTask = null;
						ClientManager.LockClient(
							token => sendTask = client.SendOperationAsync(supportOp, token),
							data.Value.Type,
							VoteType.Support);
						Avalonia.Threading.Dispatcher.UIThread.Post(() => _statusLabel.Text = GetStatusLabel());
						if (!await sendTask!)
							await client.DisposeAsync();
					}
					else
						ClientManager.LockClient(null);
				}, KeyMappingExtensions.GetMouseButtonName(button));
			});

			AddHandler(PointerReleasedEvent, async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				var button = e.InitialPressMouseButton;
				await ClientManager.RunWithOpType(async data =>
				{
					if (data != null && _pressedMouseButtonState.Remove(button))
					{
						var againstOp = new Operation(data.Value.Type, VoteType.Against, []);
						Task<bool>? sendTask = null;
						ClientManager.LockClient(
							token => sendTask = client.SendOperationAsync(againstOp, token),
							data.Value.Type,
							VoteType.Support);
						Avalonia.Threading.Dispatcher.UIThread.Post(() => _statusLabel.Text = GetStatusLabel());
						if (!await sendTask!)
							await client.DisposeAsync();
					}
					else
						ClientManager.LockClient(null);
				}, KeyMappingExtensions.GetMouseButtonName(button));
			});
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