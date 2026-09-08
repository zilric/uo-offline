# Playing with friends

UO Offline is a real server under the hood, so friends can play in your world. Your PC runs the world, their PCs run the game client and connect to yours. Their characters live in your save.

Every time you click **UO Offline** it asks how you want to play:

- **Play by myself.** Server and game on this PC. Nobody else can connect. This is the old behaviour.
- **Host for friends.** The same, but friends can join your world while your game is running.
- **Join a friend.** No server here. The game connects to a friend's PC.

Tick "don't ask again" if you always want the same one. `friends.bat ask` in the install folder brings the question back, and `friends.bat default host` (or `solo`, `join`) sets one without asking. On Linux and the Steam Deck it is `./friends.sh`.

Two things are always true:

- Friends can only play while your game is running. Clicking Play starts the server, and it stays up after you close the game (until you stop it or reboot).
- Everything runs on your own machines. There is no account with anyone and nothing to sign up for. The one optional extra is Tailscale, below.

## Hosting

Pick **Host for friends**. The first time, Windows asks (a normal admin prompt) to open port 2593 in its firewall; say yes. Then a window shows the address to give friends. `friends.bat` shows it again any time, along with whether the server is up and the firewall rule is there.

If the server was already running from a solo session, the launcher offers to restart it, because it only reads its listening setting at start. Yes saves the world first and takes about half a minute. No keeps playing by yourself this time.

On Linux, open TCP 2593 in your firewall if one is on (`sudo ufw allow 2593/tcp`, or the firewalld line at the top of `friends.sh`).

## Getting friends to your PC

**Same house or LAN.** Give them the LAN address the launcher shows (something like `192.168.1.20`). That is all.

**Anywhere else: Tailscale.** Install [Tailscale](https://tailscale.com) on your PC and on each friend's PC. Log in with the same account, or invite them to yours. Every PC then gets a private `100.x.y.z` address that reaches the others from anywhere, with no router setup and nothing open to the internet. The launcher shows your Tailscale address under "From anywhere". ZeroTier works the same way.

**Port forwarding.** Forwarding TCP 2593 on your router to your PC works too, and friends use your public IP. It exposes the port to everyone on the internet and home IPs change, so the two ways above are better. If you go this way, put your public IP or hostname in `ModernUO\Distribution\Configuration\modernuo.json` as `"serverListing.address": "your.public.ip"` so friends are sent to the right place after login. That setting also sends your own game there, so use it only if you really need it.

## Joining

Each friend installs UO Offline the normal way (the whole thing, so they can host their own world another day), clicks it, and picks **Join a friend**. It asks for your address, a name, and a password. The account is created on your world the first time they log in. If your game is not running they get a message saying so.

## Things to know

- **Party and guild.** Invite each other with the normal party window. Make a guild with a guild deed and recruit each other at the stone. The bots treat every connected player as a player: they answer you, take your party invites, join your guild, and answer guild chat.
- **Accounts.** Ten accounts per address. The first account on a fresh world is the owner (yours); friends are normal players.
- **Saves.** Friends' characters are in your save, so your backups cover them.
- **Versions.** Everyone needs a client the server accepts (7.0.23.1 or newer). The installer takes care of that.
- **Older installs.** Re-run the installer once to get the new launcher. It skips finished steps.

## If a friend cannot connect

1. Is your game running? The server starts when you click Play.
2. Did you pick Host for friends this session? `friends.bat` shows what the running server was started for. If it says solo, click UO Offline again, pick Host, and let it restart the server.
3. Firewall rule present? `friends.bat` shows it. `friends.bat firewall` adds it again.
4. Right address? LAN addresses only work on the same network. For Tailscale, both PCs need Tailscale running and logged into the same network.
5. On their side, picking Join a friend again lets them retype the address.
