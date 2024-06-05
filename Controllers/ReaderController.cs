using Microsoft.AspNetCore.Mvc;
using CSLibrary;
using CSLibrary.Events;
using CS203XAPI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using System;

namespace CS203XAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ReaderController : ControllerBase
    {
        private static List<HighLevelInterface> ReaderList = new List<HighLevelInterface>();
        private static List<string> TagsList = new List<string>();
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
                    HighLevelInterface reader = new HighLevelInterface();
                    _logger.LogInformation($"Attempting to connect to reader at IP: {ip}");
                    var ret = reader.Connect(ip, 30000);
                    if (ret != CSLibrary.Constants.Result.OK)
                    {
                        _logger.LogError($"Cannot connect to reader with IP: {ip}. Error code: {ret}");
                        throw new Exception($"Cannot connect to reader with IP: {ip}. Error code: {ret}");
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
                    _logger.LogError(ex, "Error connecting to reader");
                    return BadRequest(ex.Message);
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
                return Ok(TagsList);
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
                while (reader.Reconnect(1) != CSLibrary.Constants.Result.OK)
                {
                    Thread.Sleep(1000);
                }
                InventorySetting(reader);
                reader.StartOperation(CSLibrary.Constants.Operation.TAG_RANGING, false);
            });
            service.Start();
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
                if (!TagsList.Contains(tag))
                {
                    TagsList.Add(tag);
                }

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
    }
}
