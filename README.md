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

### If old PC accepts connections, then start source there:
LanFileCopy.exe sender --source "D:\OldFiles" --listen --port 1234 --resume --log sender.log

### New PC (receives the files, connects out):
LanFileCopy.exe receiver --root "D:\Target" --connect --host 192.168.1.123 --port 1234 --threads 8 --log receiver.log

### Troubleshooting

If Test-NetConnection, while your host is running, then it means your firewall only accepts specific executable path for that port.
You need to rename that existing executable as oldsomething.exe and rename LanFileCopy.exe to that filename, which is allowlisted in your firewall.