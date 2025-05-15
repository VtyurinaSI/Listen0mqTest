using System;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Listen0mqTest
{
    class Program
    {
        private static readonly string[] Commands = new[]
        {
            "CONNECT", "DISCONNECT", "IS_CONNECTED",
            "APPLY_CONFIG", "GET_CONFIG",
            "START_STREAM", "STOP_STREAM", "IS_STREAMING",
            "SW_TRIGGER", "SHUTDOWN"
        };

        static void Main(string[] args)
        {
            string cmdAddress = args.Length > 0
                ? args[0]
                : "tcp://localhost:5555";
            string dataAddress = args.Length > 1
                ? args[1]
                : "tcp://localhost:5556";

            Console.WriteLine($"✨ Подключаемся к {cmdAddress}...");
            using var req = new RequestSocket();
            req.Connect(cmdAddress);
            Console.WriteLine("✔ Подключено к командному каналу!\nВведите 'help' для списка команд, 'exit' для выхода.\n");

            SubscriberSocket? sub = null;
            Thread? streamThread = null;
            var cts = new CancellationTokenSource();

            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0].ToUpperInvariant();

                if (cmd == "EXIT" || cmd == "QUIT")
                    break;
                if (cmd == "HELP")
                {
                    Console.WriteLine("Доступные команды:");
                    Console.WriteLine(string.Join(", ", Commands));
                    Console.WriteLine("Чтобы послать параметры, наберите: COMMAND {\"param1\":value1, ...}");
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
                    try
                    {
                        request["params"] = JObject.Parse(parts[1]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Ошибка разбора JSON-параметров: {ex.Message}");
                        continue;
                    }
                }

                req.SendFrame(request.ToString(Formatting.None));
                var reply = req.ReceiveFrameString();
                try
                {
                    var jo = JObject.Parse(reply);
                    Console.WriteLine(jo.ToString(Formatting.Indented));
                }
                catch
                {
                    Console.WriteLine(reply);
                }

                if (cmd == "START_STREAM" && streamThread == null)
                {
                    sub = new SubscriberSocket();
                    sub.Connect(dataAddress);
                    sub.SubscribeToAnyTopic();
                    Console.WriteLine($"🔔 Подписались на поток данных {dataAddress}...");

                    streamThread = new Thread(() => StreamLoop(sub, cts.Token));
                    streamThread.IsBackground = true;
                    streamThread.Start();
                }
                else if (cmd == "STOP_STREAM" && streamThread != null)
                {
                    cts.Cancel();
                    streamThread.Join();
                    sub?.Disconnect(dataAddress);
                    sub?.Close();
                    streamThread = null;
                    cts = new CancellationTokenSource();
                    Console.WriteLine("🛑 Стрим остановлен.");
                }
            }
            Console.WriteLine("👋 Пока-пока!");
        }

        static void StreamLoop(SubscriberSocket sub, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (sub.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(500), out var msg))
                {
                    try
                    {
                        int offset = 0;
                        uint count = BitConverter.ToUInt32(msg, offset);
                        offset += 4;

                        long sumX = 0, sumY = 0, sumZ = 0;
                        for (int i = 0; i < count; i++)
                        {
                            short x = BitConverter.ToInt16(msg, offset); offset += 2;
                            short y = BitConverter.ToInt16(msg, offset); offset += 2;
                            short z = BitConverter.ToInt16(msg, offset); offset += 2;
                            sumX += x;
                            sumY += y;
                            sumZ += z;
                        }
                        double avgX = sumX / (double)count;
                        double avgY = sumY / (double)count;
                        double avgZ = sumZ / (double)count;

                        Console.WriteLine($"[Stream AVG] X: {avgX:F2}, Y: {avgY:F2}, Z: {avgZ:F2}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Ошибка обработки стрима: {ex.Message}");
                    }
                }
            }
        }
    }
}
/*
using System;
using System.Threading;
using System.Runtime.InteropServices;
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
            // Базовая директория IPC-сокетов для Windows
            string windowsBase = @"C:/Users/Булка с мясом/Desktop/детекрирование сосков/MAIN/win64_ifm3d_camera_middleware";

            // Определяем адреса IPC по умолчанию в зависимости от ОС
            string defaultCmdAddress;
            string defaultDataAddress;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Используем абсолютные пути из директории win64_ifm3d_camera_middleware
                defaultCmdAddress = $"ipc:///{windowsBase}/ifm_commands.ipc";
                defaultDataAddress = $"ipc:///{windowsBase}/ifm_pointcloud.ipc";
            }
            else
            {
                defaultCmdAddress = "ipc:///tmp/ifm-cmd.ipc";
                defaultDataAddress = "ipc:///tmp/ifm-stream.ipc";
            }

            // Адрес командного IPC-сокета (первый аргумент или по умолчанию)
            string cmdAddress = args.Length > 0 ? args[0] : defaultCmdAddress;
            // Адрес IPC-сокета для стриминга данных (второй аргумент или по умолчанию)
            string dataAddress = args.Length > 1 ? args[1] : defaultDataAddress;

            Console.WriteLine($"✨ Подключаемся к IPC командному сокету {cmdAddress}...");
            using var req = new RequestSocket();
            req.Options.Linger = TimeSpan.Zero;
            req.Connect(cmdAddress);
            Console.WriteLine("✔ Подключено к командному каналу!\nВведите 'help' для списка команд, 'exit' для выхода.\n");

            SubscriberSocket? sub = null;
            Thread? streamThread = null;
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
                    Console.WriteLine("Для передачи параметров: COMMAND {\"param\":value}");
                    continue;
                }

                if (Array.IndexOf(Commands, cmd) < 0)
                {
                    Console.WriteLine($"❌ Неизвестная команда '{cmd}'. Введите 'help'.");
                    continue;
                }

                // Формируем JSON-запрос
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

                // Отправляем команду и ждём ответ
                req.SendFrame(request.ToString(Formatting.None));
                var reply = req.ReceiveFrameString();
                try
                {
                    Console.WriteLine(JObject.Parse(reply).ToString(Formatting.Indented));
                }
                catch
                {
                    Console.WriteLine(reply);
                }

                // START_STREAM - инициируем подписку на IPC-стрим
                if (cmd == "START_STREAM" && streamThread == null)
                {
                    sub = new SubscriberSocket();
                    sub.Options.Linger = TimeSpan.Zero;
                    sub.Connect(dataAddress);
                    sub.SubscribeToAnyTopic();
                    Console.WriteLine($"🔔 Подписались на IPC поток {dataAddress}...");

                    streamThread = new Thread(() => StreamLoop(sub, cts.Token)) { IsBackground = true };
                    streamThread.Start();
                }
                else if (cmd == "STOP_STREAM" && streamThread != null)
                {
                    cts.Cancel();
                    streamThread.Join();
                    sub?.Disconnect(dataAddress);
                    sub?.Close();

                    streamThread = null;
                    cts = new CancellationTokenSource();
                    Console.WriteLine("🛑 Стрим остановлен.");
                }
            }

            Console.WriteLine("👋 Пока-пока!");
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
                        }}*/