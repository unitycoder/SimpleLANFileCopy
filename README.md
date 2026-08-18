# LANFileCopy

Simple way to copy files between two computers on the same LAN - IF your firewall blocks Shared Folders.

### Usage

Get list of open ports on your source or target pc:
```powershell
Get-NetUDPEndpoint | Sort-Object LocalPort | ForEach-Object { [PSCustomObject]@{Address=$_.LocalAddress;Port=$_.LocalPort;PID=$_.OwningProcess;Process=(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName} }
```

Then test which port on your source or target pc accepts connections (run in the other pc):
```powershell
Test-NetConnection 192.168.1.123 -Port 1234
```

### Command Structure

Two independent choices:
1. **Who has the files?** `sender` (has the source files) or `receiver` (gets written files)
2. **Who accepts the connection?** `--listen` (this PC accepts inbound) or `--connect --host X` (this PC dials out)

Pick whichever side can accept inbound connections through your firewall to `--listen`; the other side always `--connect`s to it.

### Example: Old PC CAN accept connections

**Run this first in the old PC (if it has port open):**
```
LanFileCopy.exe sender --source "D:\OldFiles" --listen --port 6129 --resume --log sender.log
```

**New PC (receiver):**
```
LanFileCopy.exe receiver --root "D:\Target" --connect --host 192.168.1.208 --port 6129 --threads 8 --log receiver.log
```

### Available Options

- `--resume` (sender only) - Ask the receiver before sending each file, skip if already present with matching size
- `--threads` (only on the side using --connect) - Number of parallel connections, default 8
- `--allow-ip` (only with --listen) - Accept connections only from this IP address
- `--log FILE` - Write warnings/errors to a log file instead of only the console

### Legacy Commands

For backward compatibility, `server` and `client` are still supported:
- `server` = `receiver --listen`
- `client` = `sender --connect`

### Troubleshooting

If Test-NetConnection, while your host is running, then it means your firewall only accepts specific executable path for that port.
You need to rename that existing executable as oldsomething.exe and rename LanFileCopy.exe to that filename, which is allowlisted in your firewall.

### Images
Client:<br>
<img width="787" height="103" alt="image" src="https://github.com/user-attachments/assets/1c6a038c-630b-443c-9f2f-72cb8322c3f4" />
