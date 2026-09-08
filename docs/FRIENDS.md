# Playing with friends

UO Offline is a real server under the hood, so friends can play in your world. Your PC runs the world, their PCs run the game client and connect to yours. Their characters live in your save.

Two things are always true:

- Friends can only play while your game is running. Clicking Play starts the server, and it stays up after you close the game (until you stop it or reboot).
- Everything is your own machine and your friends'. There is no account with anyone and nothing to sign up for. The one optional extra is Tailscale, below.

## 1. You: turn on hosting

Pick **Host for friends** in the installer, or, on an install you already have, double-click `friends.bat` in the install folder and run:

```
friends.bat host
```

Windows will ask once (a normal admin prompt) to open port 2593 in its firewall. Say yes. If you say no, friends will not get through until you run `friends.bat firewall`.

Hosting changes take effect the next time the server starts. If it was already running, stop it (close its window, or reboot) and click Play again.

`friends.bat` on its own, or the **UO Offline Friends** desktop shortcut, shows the address to give people.

## 2. Getting friends to your PC

**Same house or LAN.** Give them the LAN address `friends.bat` shows (something like `192.168.1.20`). That is all.

**Anywhere else: Tailscale.** Install [Tailscale](https://tailscale.com) on your PC and on each friend's PC. Log in with the same account, or invite them to yours. Every PC then gets a private `100.x.y.z` address that reaches the others from anywhere, with no router setup and nothing open to the internet. `friends.bat` shows your Tailscale address under "From anywhere". ZeroTier works the same way.

**Port forwarding.** Forwarding TCP 2593 on your router to your PC works too, and friends use your public IP. It exposes the port to everyone on the internet and home IPs change, so the two ways above are better. If you go this way, put your public IP or hostname in `ModernUO\Distribution\Configuration\modernuo.json` as `"serverListing.address": "your.public.ip"` so friends are sent to the right place after login.

## 3. Friends: join

Each friend runs the UO Offline installer and picks **Join a friend's game**, types your address, and picks a name and password. That install is client only: no server is built, so it is quicker and smaller. Their account is created on your server the first time they log in.

On an install they already have:

```
friends.bat join 100.101.102.103 bob mypassword
```

Clicking Play then connects to you instead of starting a server. If your game is not running they get a message saying so.

To go back to their own world: `friends.bat solo`.

## Things to know

- **Party and guild.** Invite each other with the normal party window. Make a guild with a guild deed and recruit each other at the stone. The bots treat every connected player as a player: they answer you, take your party invites, join your guild, and answer guild chat.
- **Accounts.** Ten accounts per address. The first account on a fresh world is the owner (yours); friends are normal players.
- **Saves.** Friends' characters are in your save, so your backups cover them.
- **Versions.** Everyone needs a client the server accepts (7.0.23.1 or newer). The installer takes care of that.
- **Slow first join.** The first login of a new account on a busy world takes a few seconds while the client receives everything around it.

## If a friend cannot connect

1. Is your game running? The server starts when you click Play.
2. Is hosting on? `friends.bat` should say "This PC HOSTS". If it says "plays by itself", run `friends.bat host` and restart the server.
3. Firewall rule present? `friends.bat` shows it. `friends.bat firewall` adds it again.
4. Right address? LAN addresses only work on the same network. For Tailscale, both PCs need Tailscale running and logged into the same network.
5. On their side, `friends.bat` shows what address they are set to.
