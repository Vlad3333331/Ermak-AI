using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ии
{
    // === ЛОКАЛЬНЫЙ СЕРВИС ЕРМАКА ДЛЯ БРАУЗЕРА ===
    // Слушает 127.0.0.1:8765. Браузер «Ермакъ Browser» дёргает:
    //   GET /read?url=<адрес страницы>
    // Ермак скачивает страницу, учится на ней и возвращает краткую выжимку.
    // Всё локально: текст не покидает компьютер.
    internal static class ErmakServer
    {
        private const int Port = 8765;

        public static void Start(ChatBot bot)
        {
            Task.Run(() =>
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, Port);
                    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Start();
                    Out.Learn($"[🌐 Сервис] Ермак слушает http://127.0.0.1:{Port} — браузер может просить прочитать страницы.");
                    while (true)
                    {
                        TcpClient client;
                        try { client = listener.AcceptTcpClient(); }
                        catch { break; }
                        Task.Run(() => Handle(client, bot));
                    }
                }
                catch (Exception ex)
                {
                    Out.Learn("[🌐 Сервис] Не смог занять порт 8765: " + ex.Message);
                }
            });
        }

        private static void Handle(TcpClient client, ChatBot bot)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 120000;
                var buffer = new byte[8192];
                int read = stream.Read(buffer, 0, buffer.Length);
                string request = Encoding.UTF8.GetString(buffer, 0, read);
                string firstLine = request.Split('\n')[0].Trim();

                // только чтение страниц
                if (!firstLine.StartsWith("GET /read?url=", StringComparison.OrdinalIgnoreCase))
                {
                    Respond(stream, 404, "{\"ok\":false,\"error\":\"unknown\"}");
                    return;
                }
                string rawUrl = firstLine.Substring("GET /read?url=".Length).Split(' ')[0];
                string url = Uri.UnescapeDataString(rawUrl.Replace("+", "%20").Replace("%20", " "));

                string summary = bot.ReadWebPageForBrowser(url);
                string json = JsonSerializer.Serialize(new { ok = true, summary });
                Respond(stream, 200, json);
            }
            catch
            {
                try { Respond(client.GetStream(), 500, "{\"ok\":false,\"error\":\"server error\"}"); } catch { }
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static void Respond(NetworkStream stream, int code, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 " + code + " OK\r\nContent-Type: application/json; charset=utf-8\r\n" +
                          "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            byte[] headBytes = Encoding.UTF8.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
