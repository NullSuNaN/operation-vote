using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
		private readonly Dictionary<Key, KeyEventArgs> _pressedKeysState = [];
		private readonly HashSet<KeyMappingExtensions.MouseButton> _pressedMouseButtonState = [];
		public readonly VotingClient<T> Client;
		private readonly CancellationTokenSource DisconnectCts = new();
		private readonly CancellationTokenSource PressToCloseCts = new();
		private bool _readyToForceClose = false;

		public VotingWindow(VotingClient<T> client, Dictionary<string, Operation.OperationType> keyOp)
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
				TextWrapping = TextWrapping.Wrap
			};
			Content = _statusLabel;

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

				string keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				var key = e.Key;
				if (keyOp.TryGetValue(keyStr, out var targetedOpType) && !_pressedKeysState.ContainsKey(key))
				{
					var supportOp = new Operation(targetedOpType, VoteType.Support, []);
					var sendTask = client.SendOperationAsync(supportOp, default);

					_pressedKeysState.Add(key, e);
					_statusLabel.Text = GetStatusLabel();

					await sendTask;
				}
				else
				{
					_pressedKeysState.Remove(key);
					_statusLabel.Text = GetStatusLabel();
				}
			};

			// KEY UP
			KeyUp += async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				string keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				var key = e.Key;
				if (keyOp.TryGetValue(keyStr, out var targetedOpType) && _pressedKeysState.ContainsKey(key))
				{
					var againstOp = new Operation(targetedOpType, VoteType.Against, []);
					var sendTask = client.SendOperationAsync(againstOp, default);

					_pressedKeysState.Remove(key);
					_statusLabel.Text = GetStatusLabel();

					await sendTask;
				}
				else
				{
					_pressedKeysState.Remove(key);
					_statusLabel.Text = GetStatusLabel();
				}
			};

			PointerPressed += async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				var button = KeyMappingExtensions.GetMouseButtonName(e.Properties);
				if (keyOp.TryGetValue(button.ToString(), out var targetedOpType) && !_pressedMouseButtonState.Contains(button))
				{
					var supportOp = new Operation(targetedOpType, VoteType.Support, []);
					var sendTask = client.SendOperationAsync(supportOp, default);

					_pressedMouseButtonState.Add(button);
					_statusLabel.Text = GetStatusLabel();

					await sendTask;
				}
				else
				{
					_pressedMouseButtonState.Remove(button);
					_statusLabel.Text = GetStatusLabel();
				}
			};

			PointerReleased += async (sender, e) =>
			{
				if (DisconnectCts.IsCancellationRequested) return;
				var button = KeyMappingExtensions.GetMouseButtonName(e.Properties);
				if (keyOp.TryGetValue(button.ToString(), out var targetedOpType) && !_pressedMouseButtonState.Contains(button))
				{
					var against = new Operation(targetedOpType, VoteType.Against, []);
					var sendTask = client.SendOperationAsync(against, default);

					_pressedMouseButtonState.Add(button);
					_statusLabel.Text = GetStatusLabel();

					await sendTask;
				}
				else
				{
					_pressedMouseButtonState.Remove(button);
					_statusLabel.Text = GetStatusLabel();
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