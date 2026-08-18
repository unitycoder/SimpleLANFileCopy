using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanFileCopy
{
    internal static class Program
    {
        // Protocol opcodes (written by the SENDER, read by the RECEIVER on each connection)
        private const byte OP_END = 0;
        private const byte OP_SEND_DIRECT = 1;
        private const byte OP_CHECK = 2;

        private static readonly object LogLock = new();
        private static string _logPath;

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "sender":
                        return RunSender(args);
                    case "receiver":
                        return RunReceiver(args);
                    default:
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"[FATAL] {ex}");
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("LanFileCopy - copy huge folder trees between PCs over a plain TCP port (no SMB needed)");
            Console.WriteLine();
            Console.WriteLine("Two independent choices:");
            Console.WriteLine("  1) Who has the files?      sender (has the source files)  /  receiver (gets written files)");
            Console.WriteLine("  2) Who accepts the connection?  --listen (this PC accepts inbound)  /  --connect --host X (this PC dials out)");
            Console.WriteLine();
            Console.WriteLine("Pick whichever side can actually accept inbound connections through your firewall to --listen;");
            Console.WriteLine("the other side always --connect's to it. This works no matter which PC has the source files.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine();
            Console.WriteLine("  Old PC has the files and CAN accept inbound connections:");
            Console.WriteLine("    Old PC:  LanFileCopy.exe sender   --source \"D:\\OldFiles\" --listen --port 6129 [--resume]");
            Console.WriteLine("    New PC:  LanFileCopy.exe receiver --root \"D:\\Target\"    --connect --host <old-pc-ip> --port 6129 --threads 8");
            Console.WriteLine();
            Console.WriteLine("  New PC CAN accept inbound connections instead (old PC still has the files):");
            Console.WriteLine("    New PC:  LanFileCopy.exe receiver --root \"D:\\Target\"    --listen --port 6129");
            Console.WriteLine("    Old PC:  LanFileCopy.exe sender   --source \"D:\\OldFiles\" --connect --host <new-pc-ip> --port 6129 --threads 8 [--resume]");
            Console.WriteLine();
            Console.WriteLine("Other options:");
            Console.WriteLine("  --resume    (sender only) ask the receiver before sending each file, skip if already present with matching size");
            Console.WriteLine("  --threads   (only matters on whichever side uses --connect) parallel connections, default 8");
            Console.WriteLine("  --allow-ip  (only with --listen) accept connections only from this IP");
            Console.WriteLine("  --log FILE  write warnings/errors to a log file instead of only the console");
        }

        // ---------------------------------------------------------------
        // Argument helpers
        // ---------------------------------------------------------------

        private static string GetArg(string[] args, string name, string def = null)
        {
            for (int i = 1; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return def;
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (var a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static void Log(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {message}";
            lock (LogLock)
            {
                if (!string.IsNullOrEmpty(_logPath))
                {
                    try { File.AppendAllText(_logPath, line + Environment.NewLine); }
                    catch { /* best effort */ }
                }
            }
        }

        private static void ConfigureSocket(TcpClient client)
        {
            client.NoDelay = true;
            client.SendBufferSize = 1 << 20;
            client.ReceiveBufferSize = 1 << 20;
        }

        // ---------------------------------------------------------------
        // SENDER  (has the source files; pushes them over whichever connections it gets)
        // ---------------------------------------------------------------

        private static long _filesSent;
        private static long _filesSkipped;
        private static long _bytesSent;
        private static long _sendErrors;
        private static long _filesScanned;
        private static long _bytesPlanned;

        private static int RunSender(string[] args)
        {
            bool listen = HasFlag(args, "--listen");
            bool connect = HasFlag(args, "--connect");
            if (listen == connect)
            {
                Console.WriteLine("[ERROR] Specify exactly one of --listen or --connect --host <ip>");
                return 1;
            }

            int port = int.Parse(GetArg(args, "--port", "9000"));
            string source = GetArg(args, "--source");
            bool resume = HasFlag(args, "--resume");
            int threads = int.Parse(GetArg(args, "--threads", "8"));
            string allowedIpText = GetArg(args, "--allow-ip");
            if (!TryParseAllowIp(allowedIpText, out IPAddress allowedIp))
            {
                Console.WriteLine($"[ERROR] Invalid --allow-ip value: {allowedIpText}");
                return 1;
            }
            _logPath = GetArg(args, "--log");

            if (string.IsNullOrWhiteSpace(source))
            {
                Console.WriteLine("[ERROR] --source is required (folder to send files from)");
                return 1;
            }
            if (!Directory.Exists(source))
            {
                Console.WriteLine($"[ERROR] Source folder not found: {source}");
                return 1;
            }
            source = Path.GetFullPath(source);

            var queue = new BlockingCollection<string>(boundedCapacity: 20000);
            var producer = new Thread(() =>
            {
                foreach (string file in EnumerateFilesRobust(source))
                {
                    queue.Add(file);
                    Interlocked.Increment(ref _filesScanned);
                    try
                    {
                        long fileLen = new FileInfo(file).Length;
                        Interlocked.Add(ref _bytesPlanned, fileLen);
                    }
                    catch { /* best effort for ETA */ }
                }
                queue.CompleteAdding();
            });
            producer.IsBackground = true;
            producer.Start();

            DateTime senderStartUtc = DateTime.UtcNow;
            StartProgressPrinter(() =>
            {
                long sentBytes = Interlocked.Read(ref _bytesSent);
                long plannedBytes = Interlocked.Read(ref _bytesPlanned);
                bool scanComplete = queue.IsAddingCompleted;
                string etaText = scanComplete ? FormatEta(sentBytes, plannedBytes, senderStartUtc) : "scanning...";
                return $"Scanned: {_filesScanned:N0}   Sent: {_filesSent:N0}   Skipped: {_filesSkipped:N0}   " +
                       $"{FormatBytes(sentBytes)} @ {FormatRate(sentBytes, senderStartUtc)}   Elapsed: {FormatDuration(DateTime.UtcNow - senderStartUtc)}   ETA: {etaText}   Errors: {_sendErrors:N0}";
            });

            Console.WriteLine(resume ? "Resume mode: will ask the receiver before sending each file." : "Direct mode: will send every file (use --resume to skip files already on the target).");

            if (listen)
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                Console.WriteLine($"Sender listening on port {port}, serving files from: {source}");
                if (allowedIp != null)
                    Console.WriteLine($"Only allowing incoming connections from: {allowedIp}");
                Console.WriteLine("Waiting for the receiver to connect... (Ctrl+C to stop once transfer is complete)");

                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    if (!IsAllowedClient(client, allowedIp))
                    {
                        string remote = client.Client.RemoteEndPoint?.ToString() ?? "(unknown)";
                        Log($"[WARN] Rejected connection from {remote} (not in --allow-ip)");
                        client.Close();
                        continue;
                    }

                    ConfigureSocket(client);
                    var t = new Thread(() => SenderPump(client, source, queue, resume));
                    t.IsBackground = true;
                    t.Start();
                }
            }
            else
            {
                string host = GetArg(args, "--host");
                if (string.IsNullOrWhiteSpace(host))
                {
                    Console.WriteLine("[ERROR] --host is required with --connect");
                    return 1;
                }

                Console.WriteLine($"Sender connecting to {host}:{port} with {threads} connection(s), serving files from: {source}");

                var workers = new Thread[threads];
                for (int i = 0; i < threads; i++)
                {
                    workers[i] = new Thread(() =>
                    {
                        TcpClient client;
                        try
                        {
                            client = new TcpClient();
                            client.Connect(host, port);
                            ConfigureSocket(client);
                        }
                        catch (Exception ex)
                        {
                            Log($"[ERROR] Could not connect to {host}:{port}: {ex.Message}");
                            Interlocked.Increment(ref _sendErrors);
                            foreach (var _ in queue.GetConsumingEnumerable()) { }
                            return;
                        }
                        SenderPump(client, source, queue, resume);
                    });
                    workers[i].Start();
                }
                foreach (var w in workers) w.Join();
                producer.Join();

                Console.WriteLine();
                Console.WriteLine("Done.");
                Console.WriteLine($"Files sent: {_filesSent:N0}, skipped (already on target): {_filesSkipped:N0}, errors: {_sendErrors:N0}");
                Console.WriteLine($"Total transferred: {FormatBytes(_bytesSent)}");
                if (_sendErrors > 0 && !string.IsNullOrEmpty(_logPath))
                    Console.WriteLine($"See {_logPath} for error details.");
                return 0;
            }
        }

        // Sends files pulled from the shared queue over one already-connected/accepted TcpClient.
        private static void SenderPump(TcpClient client, string sourceRoot, BlockingCollection<string> queue, bool resume)
        {
            byte[] copyBuffer = new byte[1 << 16];

            using (client)
            using (NetworkStream stream = client.GetStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                foreach (string fullPath in queue.GetConsumingEnumerable())
                {
                    string relPath = Path.GetRelativePath(sourceRoot, fullPath);
                    if (!IsSafeRelativePath(relPath))
                    {
                        Interlocked.Increment(ref _sendErrors);
                        Log($"[WARN] Skipping file outside source root '{sourceRoot}': '{fullPath}'");
                        continue;
                    }

                    relPath = relPath.Replace(Path.DirectorySeparatorChar, '/');
                    byte[] pathBytes = Encoding.UTF8.GetBytes(relPath);

                    long fileLen;
                    try { fileLen = new FileInfo(fullPath).Length; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _sendErrors);
                        Log($"[ERROR] Cannot stat '{fullPath}': {ex.Message}");
                        continue;
                    }

                    try
                    {
                        writer.Write(resume ? OP_CHECK : OP_SEND_DIRECT);
                        writer.Write(pathBytes.Length);
                        writer.Write(pathBytes);
                        writer.Write(fileLen);
                        writer.Flush();

                        if (resume)
                        {
                            byte resp = reader.ReadByte();
                            if (resp == 0)
                            {
                                Interlocked.Increment(ref _filesSkipped);
                                continue;
                            }
                        }

                        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                        {
                            CopyExactly(fs, stream, fileLen, copyBuffer);
                        }

                        Interlocked.Increment(ref _filesSent);
                        Interlocked.Add(ref _bytesSent, fileLen);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _sendErrors);
                        Log($"[ERROR] Sending '{fullPath}': {ex.Message}");
                    }
                }

                try
                {
                    writer.Write(OP_END);
                    writer.Flush();
                }
                catch { /* connection may already be broken, nothing more to do */ }
            }
        }

        // ---------------------------------------------------------------
        // RECEIVER  (writes incoming files to disk on whichever connections it gets)
        // ---------------------------------------------------------------

        private static long _filesReceived;
        private static long _bytesReceived;
        private static long _receiveErrors;

        private static int RunReceiver(string[] args)
        {
            bool listen = HasFlag(args, "--listen");
            bool connect = HasFlag(args, "--connect");
            if (listen == connect)
            {
                Console.WriteLine("[ERROR] Specify exactly one of --listen or --connect --host <ip>");
                return 1;
            }

            int port = int.Parse(GetArg(args, "--port", "9000"));
            string root = GetArg(args, "--root");
            int threads = int.Parse(GetArg(args, "--threads", "8"));
            string allowedIpText = GetArg(args, "--allow-ip");
            if (!TryParseAllowIp(allowedIpText, out IPAddress allowedIp))
            {
                Console.WriteLine($"[ERROR] Invalid --allow-ip value: {allowedIpText}");
                return 1;
            }
            _logPath = GetArg(args, "--log");

            if (string.IsNullOrWhiteSpace(root))
            {
                Console.WriteLine("[ERROR] --root is required (folder to write files into)");
                return 1;
            }
            Directory.CreateDirectory(root);
            root = Path.GetFullPath(root);

            DateTime receiverStartUtc = DateTime.UtcNow;
            StartProgressPrinter(() =>
            {
                long receivedBytes = Interlocked.Read(ref _bytesReceived);
                return $"Received: {_filesReceived:N0} files, {FormatBytes(receivedBytes)} @ {FormatRate(receivedBytes, receiverStartUtc)}   Elapsed: {FormatDuration(DateTime.UtcNow - receiverStartUtc)}   ETA: n/a   Errors: {_receiveErrors:N0}";
            });

            if (listen)
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                Console.WriteLine($"Receiver listening on port {port}, writing into: {root}");
                if (allowedIp != null)
                    Console.WriteLine($"Only allowing incoming connections from: {allowedIp}");
                Console.WriteLine("Waiting for the sender to connect... (Ctrl+C to stop)");

                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    if (!IsAllowedClient(client, allowedIp))
                    {
                        string remote = client.Client.RemoteEndPoint?.ToString() ?? "(unknown)";
                        Log($"[WARN] Rejected connection from {remote} (not in --allow-ip)");
                        client.Close();
                        continue;
                    }

                    ConfigureSocket(client);
                    var t = new Thread(() => ReceiverPump(client, root));
                    t.IsBackground = true;
                    t.Start();
                }
            }
            else
            {
                string host = GetArg(args, "--host");
                if (string.IsNullOrWhiteSpace(host))
                {
                    Console.WriteLine("[ERROR] --host is required with --connect");
                    return 1;
                }

                Console.WriteLine($"Receiver connecting to {host}:{port} with {threads} connection(s), writing into: {root}");

                var workers = new Thread[threads];
                for (int i = 0; i < threads; i++)
                {
                    workers[i] = new Thread(() =>
                    {
                        TcpClient client;
                        try
                        {
                            client = new TcpClient();
                            client.Connect(host, port);
                            ConfigureSocket(client);
                        }
                        catch (Exception ex)
                        {
                            Log($"[ERROR] Could not connect to {host}:{port}: {ex.Message}");
                            Interlocked.Increment(ref _receiveErrors);
                            return;
                        }
                        ReceiverPump(client, root);
                    });
                    workers[i].Start();
                }
                foreach (var w in workers) w.Join();

                Console.WriteLine();
                Console.WriteLine("Done.");
                Console.WriteLine($"Files received: {_filesReceived:N0}, errors: {_receiveErrors:N0}");
                Console.WriteLine($"Total received: {FormatBytes(_bytesReceived)}");
                if (_receiveErrors > 0 && !string.IsNullOrEmpty(_logPath))
                    Console.WriteLine($"See {_logPath} for error details.");
                return 0;
            }
        }

        // Receives files over one already-connected/accepted TcpClient and writes them under root.
        private static void ReceiverPump(TcpClient client, string root)
        {
            string remote = client.Client.RemoteEndPoint?.ToString();
            Console.WriteLine();
            Console.WriteLine($"[INFO] Connected: {remote}");
            try
            {
                using NetworkStream stream = client.GetStream();
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                byte[] copyBuffer = new byte[1 << 16];

                while (true)
                {
                    byte op;
                    try { op = reader.ReadByte(); }
                    catch (EndOfStreamException) { break; }
                    catch (IOException) { break; }

                    if (op == OP_END) break;

                    int pathLen = reader.ReadInt32();
                    byte[] pathBytes = reader.ReadBytes(pathLen);
                    string relPath = Encoding.UTF8.GetString(pathBytes);
                    long fileLen = reader.ReadInt64();

                    if (!TryResolveSafeTargetPath(root, relPath, out string fullPath))
                    {
                        Interlocked.Increment(ref _receiveErrors);
                        Log($"[WARN] Rejected unsafe path from {remote}: '{relPath}'");

                        if (op == OP_CHECK)
                        {
                            writer.Write((byte)0);
                            writer.Flush();
                        }
                        else
                        {
                            try { DrainExactly(stream, fileLen, copyBuffer); } catch { break; }
                        }

                        continue;
                    }

                    if (op == OP_CHECK)
                    {
                        bool exists = false;
                        try { exists = File.Exists(fullPath) && new FileInfo(fullPath).Length == fileLen; }
                        catch { /* treat as not existing */ }

                        writer.Write((byte)(exists ? 0 : 1));
                        writer.Flush();

                        if (exists)
                            continue; // sender will not transmit data for this file
                    }

                    try
                    {
                        string dir = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);

                        using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
                        {
                            CopyExactly(stream, fs, fileLen, copyBuffer);
                        }

                        Interlocked.Increment(ref _filesReceived);
                        Interlocked.Add(ref _bytesReceived, fileLen);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _receiveErrors);
                        Log($"[ERROR] Writing '{fullPath}': {ex.Message}");
                        try { DrainExactly(stream, fileLen, copyBuffer); } catch { break; }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Connection {remote}: {ex.Message}");
            }
            finally
            {
                client.Close();
                Console.WriteLine($"[INFO] Disconnected: {remote}");
            }
        }

        // ---------------------------------------------------------------
        // Shared helpers
        // ---------------------------------------------------------------

        private static IEnumerable<string> EnumerateFilesRobust(string root)
        {
            var dirs = new Stack<string>();
            dirs.Push(root);

            while (dirs.Count > 0)
            {
                string dir = dirs.Pop();

                IEnumerable<string> subDirs = Array.Empty<string>();
                try { subDirs = Directory.EnumerateDirectories(dir); }
                catch (Exception ex) { Log($"[WARN] Cannot list subfolders of '{dir}': {ex.Message}"); }

                foreach (var d in subDirs) dirs.Push(d);

                IEnumerable<string> files = Array.Empty<string>();
                try { files = Directory.EnumerateFiles(dir); }
                catch (Exception ex) { Log($"[WARN] Cannot list files in '{dir}': {ex.Message}"); continue; }

                foreach (var f in files) yield return f;
            }
        }

        private static void CopyExactly(Stream src, Stream dst, long length, byte[] buffer)
        {
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = src.Read(buffer, 0, toRead);
                if (read <= 0)
                    throw new IOException($"Stream ended prematurely, {remaining} bytes remaining");
                dst.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static void DrainExactly(Stream src, long length, byte[] buffer)
        {
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = src.Read(buffer, 0, toRead);
                if (read <= 0) throw new IOException("Stream ended while draining");
                remaining -= read;
            }
        }

        private static bool TryParseAllowIp(string ipText, out IPAddress ip)
        {
            ip = null;
            if (string.IsNullOrWhiteSpace(ipText))
                return true;

            return IPAddress.TryParse(ipText, out ip);
        }

        private static bool IsSafeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            if (Path.IsPathRooted(relativePath))
                return false;

            string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string[] parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (part == "." || part == "..")
                    return false;
            }

            return true;
        }

        private static bool TryResolveSafeTargetPath(string root, string relativePath, out string fullPath)
        {
            fullPath = null;
            if (!IsSafeRelativePath(relativePath))
                return false;

            string normalizedRoot = Path.GetFullPath(root);
            string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSep = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                return false;

            fullPath = candidate;
            return true;
        }

        private static bool IsAllowedClient(TcpClient client, IPAddress allowedIp)
        {
            if (allowedIp == null)
                return true;

            if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndPoint)
                return false;

            IPAddress remoteIp = remoteEndPoint.Address;
            if (remoteIp.IsIPv4MappedToIPv6)
                remoteIp = remoteIp.MapToIPv4();

            IPAddress normalizedAllowedIp = allowedIp;
            if (normalizedAllowedIp.IsIPv4MappedToIPv6)
                normalizedAllowedIp = normalizedAllowedIp.MapToIPv4();

            return remoteIp.Equals(normalizedAllowedIp);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double val = bytes;
            int i = 0;
            while (val >= 1024 && i < units.Length - 1) { val /= 1024; i++; }
            return $"{val:0.##} {units[i]}";
        }

        private static string FormatRate(long bytes, DateTime startedAtUtc)
        {
            double elapsedSeconds = (DateTime.UtcNow - startedAtUtc).TotalSeconds;
            if (elapsedSeconds <= 0.001)
                return "0 B/s";

            long bytesPerSecond = (long)(bytes / elapsedSeconds);
            return $"{FormatBytes(bytesPerSecond)}/s";
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";

            return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        private static string FormatEta(long transferredBytes, long totalBytes, DateTime startedAtUtc)
        {
            if (transferredBytes <= 0)
                return "--";

            if (totalBytes <= transferredBytes)
                return "00:00";

            double elapsedSeconds = (DateTime.UtcNow - startedAtUtc).TotalSeconds;
            if (elapsedSeconds <= 0.001)
                return "--";

            double bytesPerSecond = transferredBytes / elapsedSeconds;
            if (bytesPerSecond <= 0.001)
                return "--";

            TimeSpan remaining = TimeSpan.FromSeconds((totalBytes - transferredBytes) / bytesPerSecond);
            return FormatDuration(remaining);
        }

        private static void StartProgressPrinter(Func<string> statusFn)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    Console.Write($"\r{statusFn()}    ");
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
