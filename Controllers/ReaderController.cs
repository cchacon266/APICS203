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
using MongoDB.Bson;
using MongoDB.Driver;

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
        private readonly IMongoDatabase _assetsDatabase;
        private readonly IMongoDatabase _antennasDatabase;
        private static int antCycleEndCount = 0;
        private const int AntCycleEndLogInterval = 100;

        public ReaderController(ILogger<ReaderController> logger, IMongoClient mongoClient)
        {
            _logger = logger;
            _assetsDatabase = mongoClient.GetDatabase("assets-app-test");
            _antennasDatabase = mongoClient.GetDatabase("assets-app-antenas");
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
                if (string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0")
                {
                    _logger.LogError($"Invalid IP address: {ip}");
                    continue;
                }

                try
                {
                    _logger.LogInformation($"Attempting to connect to reader at IP: {ip}");
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Exception during connection to IP {ip}");
                }
            }
            return Ok("Readers started successfully");
        }

        [HttpPost("stop")]
        public IActionResult StopReading()
        {
            _logger.LogInformation("Stopping all readers");
            lock (StateChangedLock)
            {
                foreach (var reader in ReaderList)
                {
                    try
                    {
                        reader.StopOperation(true);
                        reader.Disconnect();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error stopping reader");
                    }
                }
                ReaderList.Clear();
            }
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
                        IP = tag.Key.Split(':')[0],
                        Tag = tag.Key.Split(':')[1].Trim(),
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
            lock (StateChangedLock)
            {
                foreach (var reader in ReaderList)
                {
                    connectedAntennas.Add(reader.IPAddress);
                }
            }
            return Ok(connectedAntennas);
        }

        [HttpPost("gpio")]
        public IActionResult TriggerGPIO([FromBody] GpioRequest request)
        {
            if (request.Action == "trigger")
            {
                var reader = ReaderList.Find(r => r.IPAddress == request.ReaderIP);
                if (reader == null)
                {
                    return BadRequest($"Reader with IP {request.ReaderIP} not found");
                }

                if (request.Gpio == 0)
                {
                    SetGPO0(reader, request.State);
                }
                else if (request.Gpio == 1)
                {
                    if (request.State)
                    {
                        SetGPO1(reader, true);
                        Task.Delay(20000).ContinueWith(t => SetGPO1(reader, false));
                    }
                    else
                    {
                        SetGPO1(reader, false);
                    }
                }
                return Ok("GPIO triggered");
            }
            return BadRequest("Invalid action or GPIO");
        }

        private void SetGPO0(HighLevelInterface reader, bool state)
        {
            reader.SetGPO0Async(state);
        }

        private void SetGPO1(HighLevelInterface reader, bool state)
        {
            reader.SetGPO1Async(state);
        }

        private void ReaderXP_StateChangedEvent(object sender, OnStateChangedEventArgs e)
        {
            lock (StateChangedLock)
            {
                var reader = (HighLevelInterface)sender;

                if (e.state == CSLibrary.Constants.RFState.ANT_CYCLE_END)
                {
                    antCycleEndCount++;
                    if (antCycleEndCount % AntCycleEndLogInterval == 0)
                    {
                        _logger.LogInformation($"State changed for reader at IP: {reader.IPAddress}, new state: ANT_CYCLE_END (logged every {AntCycleEndLogInterval} occurrences)");
                    }
                }
                else
                {
                    _logger.LogInformation($"State changed for reader at IP: {reader.IPAddress}, new state: {e.state}");
                }

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

                    if (string.IsNullOrWhiteSpace(reader.IPAddress) || reader.IPAddress == "0.0.0.0")
                    {
                        _logger.LogError($"Invalid IP address for reconnection: {reader.IPAddress}");
                        break;
                    }

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

                if (!IsTagInDatabase(e.info.epc.ToString())) return;

                TagsDict[tag] = DateTime.Now; // Actualiza la hora de lectura de la etiqueta
                UpdateReadsCollection(reader.IPAddress, e.info.epc.ToString());

                // Enviar la etiqueta a través de WebSocket
                WebSocketController.SendTag(tag);
            }
        }

        private bool IsTagInDatabase(string epc)
        {
            var assetsCollection = _assetsDatabase.GetCollection<BsonDocument>("assets");
            var filter = Builders<BsonDocument>.Filter.Eq("EPC", epc);
            var result = assetsCollection.Find(filter).FirstOrDefault();
            return result != null;
        }

        private void UpdateReadsCollection(string ip, string epc)
        {
            var readsCollection = _antennasDatabase.GetCollection<BsonDocument>("Reads");
            var filter = Builders<BsonDocument>.Filter.Eq("tag", epc) & Builders<BsonDocument>.Filter.Eq("IP", ip);
            var existingRead = readsCollection.Find(filter).FirstOrDefault();

            if (existingRead == null)
            {
                var newRead = new BsonDocument
                {
                    { "IP", ip },
                    { "tag", epc },
                    { "lastReadTime", DateTime.Now.ToString("dd-MM-yyyy HH:mm") }
                };
                readsCollection.InsertOne(newRead);
            }
            else
            {
                var lastReadTimeString = existingRead["lastReadTime"].AsString;
                if (DateTime.TryParseExact(lastReadTimeString, "dd-MM-yyyy HH:mm", null, System.Globalization.DateTimeStyles.None, out DateTime lastReadTime))
                {
                    if ((DateTime.Now - lastReadTime).TotalMinutes >= 5)
                    {
                        var update = Builders<BsonDocument>.Update.Set("lastReadTime", DateTime.Now.ToString("dd-MM-yyyy HH:mm"));
                        readsCollection.UpdateOne(filter, update);
                    }
                }
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
