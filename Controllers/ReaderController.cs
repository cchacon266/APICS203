using Microsoft.AspNetCore.Mvc;
using CSLibrary;
using CSLibrary.Events;
using CS203XAPI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Sockets;

namespace CS203XAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ReaderController : ControllerBase
    {
        private static List<HighLevelInterface> ReaderList = new List<HighLevelInterface>();
        private static Dictionary<string, DateTime> TagsDict = new Dictionary<string, DateTime>();
        private static object StateChangedLock = new object();
        private static object TagInventoryLock = new object();
        private readonly ILogger<ReaderController> _logger;

        public ReaderController(ILogger<ReaderController> logger)
        {
            _logger = logger;
        }

        [HttpPost("start")]
        public IActionResult StartReading([FromBody] StartReadingRequest request)
        {
            if (request.ReaderIPs == null || request.ReaderIPs.Count == 0)
            {
                _logger.LogError("Reader IPs are required");
                return BadRequest(new { error = "Reader IPs are required" });
            }

            foreach (var ip in request.ReaderIPs)
            {
                try
                {
                    _logger.LogInformation($"Attempting to connect to reader at IP: {ip}");
                    if (IsSocketConnected(ip, 1515, 3, 2000))
                    {
                        HighLevelInterface reader = new HighLevelInterface();
                        var ret = reader.Connect(ip, 30000);
                        _logger.LogInformation($"Connection result for IP {ip}: {ret}");
                        if (ret != CSLibrary.Constants.Result.OK)
                        {
                            _logger.LogError($"Cannot connect to reader with IP: {ip}. Error code: {ret}");
                            continue;
                        }

                        reader.OnStateChanged += ReaderXP_StateChangedEvent;
                        reader.OnAsyncCallback += ReaderXP_TagInventoryEvent;
                        InventorySetting(reader);
                        reader.StartOperation(CSLibrary.Constants.Operation.TAG_RANGING, false);
                        ReaderList.Add(reader);
                        _logger.LogInformation($"Reader connected and started at IP: {ip}");
                    }
                    else
                    {
                        _logger.LogWarning($"Cannot connect to IP {ip} on port 1515");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Exception during connection to IP {ip} on port 1515");
                }
            }
            return Ok("Readers started successfully");
        }

        [HttpPost("stop")]
        public IActionResult StopReading()
        {
            _logger.LogInformation("Stopping all readers");
            foreach (var reader in ReaderList)
            {
                reader.StopOperation(true);
                reader.Disconnect();
            }
            ReaderList.Clear();
            return Ok("Readers stopped successfully");
        }

        [HttpGet("tags")]
        public IActionResult GetTags()
        {
            lock (TagInventoryLock)
            {
                var tagsWithTimestamp = new List<object>();
                foreach (var tag in TagsDict)
                {
                    tagsWithTimestamp.Add(new
                    {
                        Tag = tag.Key,
                        LastReadTime = tag.Value.ToString("dd-MM-yyyy HH:mm") // Formatear la fecha y hora
                    });
                }
                return Ok(tagsWithTimestamp);
            }
        }

        [HttpGet("list")]
        public IActionResult GetAntennas()
        {
            var connectedAntennas = new List<string>();
            foreach (var reader in ReaderList)
            {
                connectedAntennas.Add(reader.IPAddress);
            }
            return Ok(connectedAntennas);
        }

        [HttpPost("gpio")]
        public IActionResult TriggerGPIO([FromBody] GpioRequest request)
        {
            if (request.Action == "trigger")
            {
                if (request.Gpio == 0)
                {
                    SetGPO0(request.State);
                }
                else if (request.Gpio == 1)
                {
                    if (request.State)
                    {
                        SetGPO1(true);
                        Task.Delay(20000).ContinueWith(t => SetGPO1(false));
                    }
                    else
                    {
                        SetGPO1(false);
                    }
                }
                return Ok("GPIO triggered");
            }
            return BadRequest("Invalid action or GPIO");
        }

        private void SetGPO0(bool state)
        {
            foreach (var reader in ReaderList)
            {
                reader.SetGPO0Async(state);
            }
        }

        private void SetGPO1(bool state)
        {
            foreach (var reader in ReaderList)
            {
                reader.SetGPO1Async(state);
            }
        }

        private void ReaderXP_StateChangedEvent(object sender, OnStateChangedEventArgs e)
        {
            lock (StateChangedLock)
            {
                var reader = (HighLevelInterface)sender;
                if (e.state == CSLibrary.Constants.RFState.ANT_CYCLE_END)
                {
                    return; // Ignorar el estado ANT_CYCLE_END
                }

                _logger.LogInformation($"State changed for reader at IP: {reader.IPAddress}, new state: {e.state}");
                switch (e.state)
                {
                    case CSLibrary.Constants.RFState.IDLE:
                        HandleIdleState(reader);
                        break;
                    case CSLibrary.Constants.RFState.BUSY:
                        break;
                    case CSLibrary.Constants.RFState.RESET:
                        HandleResetState(reader);
                        break;
                    case CSLibrary.Constants.RFState.ABORT:
                        break;
                }
            }
        }

        private void HandleIdleState(HighLevelInterface reader)
        {
            switch (reader.LastMacErrorCode)
            {
                case 0x000:
                    break;
                case 0x306:
                    RestartReader(reader, "Reader too hot", 180);
                    break;
                case 0x309:
                    RestartReader(reader, "Reflected power too high", 3);
                    break;
                default:
                    _logger.LogError($"Mac Error: 0x{reader.LastMacErrorCode:X}, please report to CSL technical support.");
                    break;
            }
        }

        private void HandleResetState(HighLevelInterface reader)
        {
            Thread service = new Thread(() =>
            {
                DateTime reconnTimer = DateTime.Now;
                int retryCount = 0;
                int maxRetries = 10;

                _logger.LogInformation($"Attempting to reconnect reader at IP: {reader.IPAddress}");

                while (retryCount < maxRetries)
                {
                    retryCount++;

                    try
                    {
                        if (!IsSocketConnected(reader.IPAddress, 1515, 3, 2000))
                        {
                            _logger.LogWarning($"Socket is not connected for IP: {reader.IPAddress}, retrying ({retryCount}/{maxRetries})...");
                            Thread.Sleep(1000);
                            continue;
                        }

                        var result = reader.Reconnect(1);
                        if (result == CSLibrary.Constants.Result.OK)
                        {
                            _logger.LogInformation($"Successfully reconnected reader at IP: {reader.IPAddress}");
                            InventorySetting(reader);
                            reader.StartOperation(CSLibrary.Constants.Operation.TAG_RANGING, false);
                            break;
                        }
                        else
                        {
                            _logger.LogWarning($"Reconnection attempt failed for reader at IP: {reader.IPAddress}, result: {result} ({retryCount}/{maxRetries})");
                        }
                    }
                    catch (ObjectDisposedException ex)
                    {
                        _logger.LogError(ex, $"ObjectDisposedException caught during reconnection for reader at IP: {reader.IPAddress}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Exception caught during reconnection for reader at IP: {reader.IPAddress}");
                    }

                    Thread.Sleep(1000); // Esperar un momento antes de intentar reconectar nuevamente
                }

                _logger.LogInformation($"Reconnection attempts for reader at IP: {reader.IPAddress} completed with {retryCount} attempts.");
            });

            service.IsBackground = true; // Asegura que el thread no bloquee la salida del programa
            try
            {
                service.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Exception caught while starting reconnection thread for reader at IP: {reader.IPAddress}");
            }
        }

        private void RestartReader(HighLevelInterface reader, string message, int delayInSeconds)
        {
            Thread reconnect = new Thread(() =>
            {
                _logger.LogInformation($"{message} - Restarting in {delayInSeconds} seconds...");
                Thread.Sleep(delayInSeconds * 1000);
                reader.StartOperation(CSLibrary.Constants.Operation.TAG_RANGING, false);
            });
            reconnect.Start();
        }

        private void ReaderXP_TagInventoryEvent(object sender, OnAsyncCallbackEventArgs e)
        {
            lock (TagInventoryLock)
            {
                var reader = (HighLevelInterface)sender;
                string tag = $"{reader.IPAddress}: {e.info.epc.ToString()}";
                TagsDict[tag] = DateTime.Now; // Actualiza la hora de lectura de la etiqueta

                // Enviar la etiqueta a través de WebSocket
                WebSocketController.SendTag(tag);
            }
        }

        private void InventorySetting(HighLevelInterface reader)
        {
            reader.SetAntennaPortState(0, CSLibrary.Constants.AntennaPortState.DISABLED);
            reader.SetAntennaPortState(1, CSLibrary.Constants.AntennaPortState.DISABLED);
            reader.SetAntennaPortState(2, CSLibrary.Constants.AntennaPortState.DISABLED);
            reader.SetAntennaPortState(3, CSLibrary.Constants.AntennaPortState.ENABLED);

            var QParms = new CSLibrary.Structures.DynamicQParms
            {
                maxQValue = 15,
                minQValue = 0,
                retryCount = 7,
                startQValue = 7,
                thresholdMultiplier = 1,
                toggleTarget = 1
            };

            reader.SetTagGroup(CSLibrary.Constants.Selected.ALL, CSLibrary.Constants.Session.S0, CSLibrary.Constants.SessionTarget.A);
            reader.SetOperationMode(CSLibrary.Constants.RadioOperationMode.CONTINUOUS);
            reader.SetSingulationAlgorithmParms(CSLibrary.Constants.SingulationAlgorithm.DYNAMICQ, QParms);
            reader.Options.TagRanging.multibanks = 0;
            reader.Options.TagRanging.bank1 = CSLibrary.Constants.MemoryBank.TID;
            reader.Options.TagRanging.offset1 = 0;
            reader.Options.TagRanging.count1 = 2;
            reader.Options.TagRanging.bank2 = CSLibrary.Constants.MemoryBank.USER;
            reader.Options.TagRanging.offset2 = 0;
            reader.Options.TagRanging.count2 = 2;
            reader.Options.TagRanging.flags = CSLibrary.Constants.SelectFlags.ZERO;
        }

        private bool IsSocketConnected(string ip, int port, int maxRetries, int retryDelayMilliseconds)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        var result = client.BeginConnect(ip, port, null, null);
                        bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

                        if (!success)
                        {
                            _logger.LogWarning($"Cannot connect to IP {ip} on port {port}");
                            Thread.Sleep(retryDelayMilliseconds);
                            continue;
                        }

                        client.EndConnect(result);
                        _logger.LogInformation($"Successfully connected to IP {ip} on port {port}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Exception during connection to IP {ip} on port {port}");
                    Thread.Sleep(retryDelayMilliseconds);
                }
            }

            return false;
        }
    }
}
