// Program.cs
using System;
using System.Xml;
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
            // Адрес сервера, можно передать первым аргументом:
            string address = args.Length > 0
                ? args[0]
                : "tcp://localhost:5555"; // по умолчанию ждём на localhost:5555 :contentReference[oaicite:0]{index=0}:contentReference[oaicite:1]{index=1}

            Console.WriteLine($"✨ Подключаемся к {address}...");
            using var req = new RequestSocket();
            req.Connect(address);
            Console.WriteLine("✔ Подключено! Введите 'help' для списка команд, 'exit' для выхода.\n");

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

                // Формируем JSON-запрос
                var request = new JObject
                {
                    ["command"] = cmd
                };

                // Парсим параметры, если они есть
                var @params = new JObject();
                if (parts.Length > 1)
                {
                    try
                    {
                        @params = JObject.Parse(parts[1]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Ошибка разбора JSON-параметров: {ex.Message}");
                        continue;
                    }
                }
                request["params"] = @params;

                // Отправляем
                var reqText = request.ToString(Newtonsoft.Json.Formatting.None);
                req.SendFrame(reqText);

                // Ждём ответ
                string reply = req.ReceiveFrameString();
                try
                {
                    // Пытаемся красиво распечатать JSON-ответ
                    var jo = JObject.Parse(reply);
                    Console.WriteLine(jo.ToString(Newtonsoft.Json.Formatting.Indented));
                }
                catch
                {
                    // Если не JSON ― просто отобразить как строку
                    Console.WriteLine(reply);
                }
            }

            Console.WriteLine("👋 Пока-пока!");
        }
    }
}
