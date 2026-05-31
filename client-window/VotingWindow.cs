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
		private readonly HashSet<Key> _pressedKeysState = new();

		public VotingWindow(VotingClient<T> client, Dictionary<string, Operation.OperationType> keyOp)
		{
			Title = "Operation Voting Client";
			Width = 400;
			Height = 200;
			WindowStartupLocation = WindowStartupLocation.CenterScreen;

			// FIX 1: Explicitly force the window background to a solid color 
			// instead of leaving it to the system default transparent alpha mix
			Background = Brushes.DimGray;

			// FIX 2: Explicitly tell Avalonia to shut off alpha window compositing
			TransparencyLevelHint = [WindowTransparencyLevel.None];

			_statusLabel = new TextBlock
			{
				Text = "Focus this window and operate.\nYour actions will be sent to the server.",
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				TextAlignment = TextAlignment.Center,
				FontSize = 16,
				FontWeight = FontWeight.Bold,
				Foreground = Brushes.White // White text so it contrasts perfectly with the background
			};
			Content = _statusLabel;

			// KEY DOWN
			KeyDown += async (sender, e) =>
			{
				string keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				if (keyOp.TryGetValue(keyStr, out var targetedOpType) && !_pressedKeysState.Contains(e.Key))
				{
					_pressedKeysState.Add(e.Key);
					_statusLabel.Text = $"Active Input: '{keyStr}'";
					_statusLabel.Foreground = Brushes.Lime; // Bright lime green for high visibility

					var supportOp = new Operation(targetedOpType, VoteType.Support, []);
					await client.SendOperationAsync(supportOp, default);
				}
			};

			// KEY UP
			KeyUp += async (sender, e) =>
			{
				string? keyStr = KeyMappingExtensions.GetJsStyleKeyName(e);
				if (keyOp.TryGetValue(keyStr, out var targetedOpType) && _pressedKeysState.Contains(e.Key))
				{
					_pressedKeysState.Remove(e.Key);
					_statusLabel.Text = $"Active Input: '{keyStr}'";
					_statusLabel.Foreground = Brushes.OrangeRed; // Opaque reddish orange

					var againstOp = new Operation(targetedOpType, VoteType.Against, []);
					await client.SendOperationAsync(againstOp, default);
				}
			};
		}
	}
}