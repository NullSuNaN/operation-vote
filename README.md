# Operation Voting

have real-time votes on keyboard/mouse actions.

## Note

You can make your own servers and clients with the `core/` stuff.

The `client-window`, `client-browser`, `server` are all just an implementation to process generic voting.

If the server port is not open for WLAN, you need to use `0.0.0.0` or your WLAN IP as the host.

For the detail of the server configuration

## Authentication

There is an anonymous user by default, which can be configured to be disabled(vote does not count) by the server.

You can change the multiplier of each account while keeping the server on.

Every user have a username and a password, they both have no limitations on the characters and is *CASE SENSITIVE*.

You can only login with your client, and only the server host can register.

The protocol allows the client to change account mid-connection, but currently cannot change back to Anonymous and it is not implemented to the clients.

The account with the name `Anonymous` is separate from the default Anonymous user.

The users on the server-side is stored in `users.db`.

## Examples

+ Commands
```bash
server # uses config.json
server gd.json # uses gd.json
server -- gd.json # uses gd.json
server --manager gd.json # opens the user manager with gd.json
server --help # show help
dotnet serve -d:dist/wwwroot -p8080 -a 0.0.0.0 # serve the browser client, require dotnet-serve tool
client-window localhost:9055 # connect to localhost:9055 with raw TCP
client-window localhost:9055 ws # connect to localhost:9055 with WS
client-window localhost:9055 wss NullSuNaN 123456 # connect to localhost:9055 with WS and attempt to login as NullSuNaN
                                                  # or continue as Anonymous if the login failed
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
    },
  ],
  "Alert": false,
  "Logging": {
    "LogLevel": "debug",
    "LogNetworkTrace": false
  }
}
```