using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Generic;
using System;

namespace CS203XAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WebSocketController : ControllerBase
    {
        private static List<WebSocket> _sockets = new List<WebSocket>();
        private static object _lock = new object();

        [HttpGet("/ws")]
        public async Task Get()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                lock (_lock)
                {
                    _sockets.Add(webSocket);
                }

                await Echo(webSocket);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
            }
        }

        private async Task Echo(WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            WebSocketReceiveResult result = null;
            try
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                while (!result.CloseStatus.HasValue)
                {
                    await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                // Log the error
                Console.WriteLine($"WebSocket Echo error: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _sockets.Remove(webSocket);
                }

                if (result != null)
                {
                    await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
                }
                else
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Internal server error", CancellationToken.None);
                }
            }
        }

        // Método para enviar lecturas de tags
        public static async void SendTag(string tag)
        {
            var buffer = Encoding.UTF8.GetBytes(tag);
            var tasks = new List<Task>();

            lock (_lock)
            {
                foreach (var socket in _sockets)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        tasks.Add(socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None));
                    }
                }
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                // Log the error
                Console.WriteLine($"Error sending tag via WebSocket: {ex.Message}");
            }
        }

        // Método para enviar logs
        public static async void SendLog(string logMessage)
        {
            var buffer = Encoding.UTF8.GetBytes(logMessage);
            var tasks = new List<Task>();

            lock (_lock)
            {
                foreach (var socket in _sockets)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        tasks.Add(socket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None));
                    }
                }
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                // Log the error
                Console.WriteLine($"Error sending log via WebSocket: {ex.Message}");
            }
        }
    }
}
