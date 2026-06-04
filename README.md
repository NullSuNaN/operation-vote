# Operation Voting

have real-time votes on keyboard/mouse actions.

## Note

You can make your own servers and clients with the `core/` stuff.

The `client-window`, `client-browser`, `server` are all just an implementation to process generic voting.

If the server port is not open for WLAN, you need to use `0.0.0.0` or your WLAN IP as the host

## Examples

+ Commands
```bash
server # uses config.json
server gd.json # uses gd.json
dotnet serve -ddist/wwwroot -p8080 -a 0.0.0.0 # serve the browser client, require dotnet-serve
client-window localhost:9055 # connect to localhost:9055 with raw TCP
client-window localhost:9055 ws # connect to localhost:9055 with WS
client-window localhost:9055 wss # connect to localhost:9055 with WSS
```
+ Server Configuration
```json
{
  "$schema": "config.schema.json",
  "Network": {
    "TcpHost": "0.0.0.0",
    "TcpPort": 9055,
    "WsUriPrefix": "http://+:9056/"
  },
  "Profiles": [
    {
      "Name": "jump",
      "Keys": ["w", "W", " ", "ArrowUp", "MouseLeft"],
      "AfkLimit": "00:00:07",
      "VoteResults": [
        {
          "type": "PressKey",
          "key": " ",
          "requireSupportRate": 0.50
        },
        {
          "type": "Output",
          "id": "antiJump",
          "fd": 2,
          "requireSupportRate": -0.2
        }
      ]
    }
  ]
}
```