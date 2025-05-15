using System;
using System.Threading;
using System.Diagnostics;
using System.IO;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IfmConsoleClient
{
    class Program
    {
        // Список поддерживаемых команд
        private static readonly string[] Commands = new[]
        {
            "CONNECT", "DISCONNECT", "IS_CONNECTED",
            "APPLY_CONFIG", "GET_CONFIG",
            "START_STREAM", "STOP_STREAM", "IS_STREAMING",
            "SW_TRIGGER", "SHUTDOWN"
        };

        static void Main(string[] args)
        {
            // Выбор варианта подключения: true - TCP, false - IPC
            bool useTcp = true; // установите false для IPC

            // Определяем путь к ifm3d_camera_middleware.exe
            string defaultServerDir = @"C:\Users\Булка с мясом\Desktop\детекрирование сосков\MAIN\win64_ifm3d_camera_middleware";
            string defaultServerExe = Path.Combine(defaultServerDir, "ifm3d_camera_middleware.exe");
            string serverPath = args.Length > 2 ? args[2] : defaultServerExe;

            // Запускаем сервер
            TryStartServer(serverPath, useTcp);

            // Адреса для TCP
            string tcpCmdAddress = args.Length > 0 ? args[0] : "tcp://localhost:5555";
            string tcpDataAddress = args.Length > 1 ? args[1] : "tcp://localhost:5556";

            // Адреса для IPC
            string ipcCmdAddress = "ipc:///tmp/ifm-cmd.ipc";
            string ipcDataAddress = "ipc:///tmp/ifm-stream.ipc";

            // Инициализация сокетов по выбранному варианту
            RequestSocket req;
            string streamAddress;
            if (useTcp)
            {
                req = SetupTcp(tcpCmdAddress);
                streamAddress = tcpDataAddress;
            }
            else
            {
                req = SetupIpc(ipcCmdAddress);
                streamAddress = ipcDataAddress;
            }

            using (req)
            {
                Console.WriteLine("✔ Готов к работе! Введите 'help' для списка команд, 'exit' для выхода.\n");

                SubscriberSocket sub = null;
                Thread streamThread = null;
                var cts = new CancellationTokenSource();

                while (true)
                {
                    Console.Write("> ");
                    var line = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var cmd = parts[0].ToUpperInvariant();
                    if (cmd == "EXIT" || cmd == "QUIT") break;

                    if (cmd == "HELP")
                    {
                        Console.WriteLine("Доступные команды:");
                        Console.WriteLine(string.Join(", ", Commands));
                        Console.WriteLine("COMMAND [params]  - отправить команду");
                        continue;
                    }

                    if (Array.IndexOf(Commands, cmd) < 0)
                    {
                        Console.WriteLine($"❌ Неизвестная команда '{cmd}'. Введите 'help'.");
                        continue;
                    }

                    var request = new JObject { ["command"] = cmd, ["params"] = new JObject() };
                    if (parts.Length > 1)
                    {
                        try { request["params"] = JObject.Parse(parts[1]); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Ошибка JSON: {ex.Message}");
                            continue;
                        }
                    }

                    req.SendFrame(request.ToString(Formatting.None));
                    var reply = req.ReceiveFrameString();
                    try { Console.WriteLine(JObject.Parse(reply).ToString(Formatting.Indented)); }
                    catch { Console.WriteLine(reply); }

                    if (cmd == "START_STREAM" && streamThread == null)
                    {
                        sub = SetupSubscriber(streamAddress);
                        Console.WriteLine($"🔔 Подписались на поток {streamAddress}...");

                        streamThread = new Thread(() => StreamLoop(sub, cts.Token)) { IsBackground = true };
                        streamThread.Start();
                    }
                    else if (cmd == "STOP_STREAM" && streamThread != null)
                    {
                        cts.Cancel();
                        streamThread.Join();
                        sub?.Disconnect(streamAddress);
                        sub?.Close();
                        streamThread = null;
                        cts = new CancellationTokenSource();
                        Console.WriteLine("🛑 Стрим остановлен.");
                    }
                }

                Console.WriteLine("👋 Пока-пока!");
            }
        }

        // Методы подключения
        static RequestSocket SetupTcp(string cmdAddress)
        {
            Console.WriteLine($"✨ TCP: подключаемся к {cmdAddress}...");
            var req = new RequestSocket();
            req.Options.Linger = TimeSpan.Zero;
            req.Connect(cmdAddress);
            return req;
        }

        static RequestSocket SetupIpc(string cmdAddress)
        {
            Console.WriteLine($"✨ IPC: подключаемся к {cmdAddress}...");
            var req = new RequestSocket();
            req.Options.Linger = TimeSpan.Zero;
            req.Connect(cmdAddress);
            return req;
        }

        static SubscriberSocket SetupSubscriber(string dataAddress)
        {
            var sub = new SubscriberSocket();
            sub.Options.Linger = TimeSpan.Zero;
            sub.Connect(dataAddress);
            sub.SubscribeToAnyTopic();
            return sub;
        }

        // Запуск middleware из указанного пути и передачи --mode
        static void TryStartServer(string exePath, bool useTcp)
        {
            if (!File.Exists(exePath))
            {
                Console.WriteLine($"❌ Middleware не найден по пути: {exePath}");
                return;
            }
            string mode = useTcp ? "tcp" : "ipc";
            Console.WriteLine($"🚀 Запускаем middleware: {exePath} --mode {mode}...");
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--mode {mode}",
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = true,
                CreateNoWindow = false
            };
            try
            {
                Process.Start(startInfo);
                Thread.Sleep(1000);
                Console.WriteLine("✔ Middleware запущен.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка запуска middleware: {ex.Message}");
            }
        }

        // Обработка стрима: вычисление среднего XYZ
        static void StreamLoop(SubscriberSocket sub, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(500), out var msg))
                {
                    try
                    {
                        int offset = 0;
                        uint count = BitConverter.ToUInt32(msg, offset); offset += 4;
                        long sx = 0, sy = 0, sz = 0;
                        for (int i = 0; i < count; i++)
                        {
                            sx += BitConverter.ToInt16(msg, offset); offset += 2;
                            sy += BitConverter.ToInt16(msg, offset); offset += 2;
                            sz += BitConverter.ToInt16(msg, offset); offset += 2;
                        }
                        Console.WriteLine($"[Stream AVG] X: {sx / (double)count:F2}, Y: {sy / (double)count:F2}, Z: {sz / (double)count:F2}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Ошибка стрима: {ex.Message}");
                    }
                }
            }
        }
    }
}
