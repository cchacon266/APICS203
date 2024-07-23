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
        // Lista de lectores conectados
        private static List<HighLevelInterface> ReaderList = new List<HighLevelInterface>();
        // Diccionario para almacenar etiquetas y su último tiempo de lectura
        private static object StateChangedLock = new object();
        private static object TagInventoryLock = new object();
        private readonly ILogger<ReaderController> _logger;
        private readonly IMongoDatabase _assetsDatabase;
        private readonly IMongoDatabase _antennasDatabase;
        private static int antCycleEndCount = 0;
        private const int AntCycleEndLogInterval = 100;
        private static Dictionary<string, DateTime> LastLoggedTimeDict = new Dictionary<string, DateTime>();
        private const int LogIntervalSeconds = 60; // Intervalo de tiempo en segundos para registrar el mismo mensaje



        // Constructor que inicializa las bases de datos y el logger
        public ReaderController(ILogger<ReaderController> logger, IMongoClient mongoClient)
        {
            _logger = logger;
            _assetsDatabase = mongoClient.GetDatabase("assets-app-doihi");
            _antennasDatabase = mongoClient.GetDatabase("assets-app-antenas");
        }

        // Método para iniciar la lectura de las antenas
        [HttpPost("start")]
        public IActionResult StartReading([FromBody] StartReadingRequest request)
        {
            // Verificar si la lista de IPs de los lectores está vacía
            if (request.ReaderIPs == null || request.ReaderIPs.Count == 0)
            {
                _logger.LogError("Se requieren las IPs de los lectores");
                return BadRequest(new { error = "Se requieren las IPs de los lectores" });
            }

            // Intentar conectar cada lector en la lista de IPs
            foreach (var ip in request.ReaderIPs)
            {
                _logger.LogInformation($"Intentando conectar al lector en IP: {ip}");
                HighLevelInterface reader = null;

                try
                {
                    // Verificar si el socket está conectado
                    bool isConnected;
                    try
                    {
                        isConnected = IsSocketConnected(ip, 1515);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error al verificar la conexión del socket a la IP {ip}");
                        continue;
                    }

                    if (!isConnected)
                    {
                        _logger.LogWarning($"No se puede conectar a la IP {ip} en el puerto 1515");
                        continue;
                    }

                    reader = new HighLevelInterface();
                    var ret = reader.Connect(ip, 40000);

                    _logger.LogInformation($"Resultado de la conexión para la IP {ip}: {ret}");
                    if (ret != CSLibrary.Constants.Result.OK)
                    {
                        _logger.LogError($"No se puede conectar al lector con IP: {ip}. Código de error: {ret}");
                        throw new Exception($"No se puede conectar al lector con IP: {ip}. Código de error: {ret}");
                    }

                    // Configurar eventos y comenzar la operación de lectura
                    reader.OnStateChanged += ReaderXP_StateChangedEvent;
                    reader.OnAsyncCallback += ReaderXP_TagInventoryEvent;
                    InventorySetting(reader);
                    reader.StartOperation(CSLibrary.Constants.Operation.TAG_RANGING, false);
                    ReaderList.Add(reader);
                    _logger.LogInformation($"Lector conectado y comenzado en IP: {ip}");

                    // Verificar y registrar si la antena tiene GPIO
                    if (HasGPIO(ip))
                    {
                        _logger.LogInformation($"La antena con IP {ip} tiene semáforo (GPIO).");
                        SetGPO0(reader, true); // Encender LED verde si tiene semaforo
                    }
                    else
                    {
                        _logger.LogInformation($"La antena con IP {ip} no tiene semáforo (GPIO).");

                    }
                }
                catch (Exception ex)
                {
                    if (reader != null)
                    {
                        try
                        {
                            reader.Disconnect();
                        }
                        catch (Exception disconnectEx)
                        {
                            _logger.LogError(disconnectEx, $"Error desconectando el lector en IP: {ip}");
                        }
                    }

                    _logger.LogError(ex, $"Error al conectar al lector en IP: {ip}");
                }
            }

            return Ok("Lectores iniciados con éxito");
        }


        // Método para detener la lectura de las antenas
        [HttpPost("stop")]
        public IActionResult StopReading([FromBody] StopReadingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ReaderIP))
            {
                _logger.LogError("Reader IP is required to stop reading");
                return BadRequest(new { error = "Reader IP is required" });
            }

            _logger.LogInformation($"Stopping reader with IP: {request.ReaderIP}");
            lock (StateChangedLock)
            {
                var reader = ReaderList.Find(r => r.IPAddress == request.ReaderIP);
                if (reader != null)
                {
                    try
                    {
                        // Apagar el semáforo verde (asumimos que el LED verde está controlado por GPO0)
                        if (HasGPIO(request.ReaderIP))
                        {
                            SetGPO0(reader, false);
                        }

                        reader.StopOperation(true);
                        reader.Disconnect();
                        ReaderList.Remove(reader);
                        _logger.LogInformation($"Reader with IP {request.ReaderIP} stopped successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error stopping reader with IP: {request.ReaderIP}");
                        return StatusCode(500, $"Error stopping reader with IP: {request.ReaderIP}");
                    }
                }
                else
                {
                    _logger.LogWarning($"Reader with IP {request.ReaderIP} not found");
                    return NotFound(new { error = $"Reader with IP {request.ReaderIP} not found" });
                }
            }
            return Ok($"Reader with IP {request.ReaderIP} stopped successfully");
        }


        // Método para obtener las etiquetas leídas
        [HttpGet("tags")]
        public IActionResult GetTags()
        {
            var readsCollection = _antennasDatabase.GetCollection<BsonDocument>("Reads");
            var allReads = readsCollection.Find(new BsonDocument()).ToList();
            var tagsWithTimestamp = new List<object>();

            foreach (var read in allReads)
            {
                tagsWithTimestamp.Add(new
                {
                    IP = read["IP"].AsString,
                    Tag = read["tag"].AsString,
                    LastReadTime = read["lastReadTime"].ToString() // Formatear la fecha y hora si es necesario
                });
            }

            return Ok(tagsWithTimestamp);
        }


        // Método para obtener la lista de antenas conectadas
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

        // Método para activar/desactivar GPIO
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

        // Método para configurar el estado del GPIO 0
        private void SetGPO0(HighLevelInterface reader, bool state)
        {
            reader.SetGPO0Async(state);
        }

        // Método para configurar el estado del GPIO 1
        private void SetGPO1(HighLevelInterface reader, bool state)
        {
            reader.SetGPO1Async(state);
        }

        // Evento que se dispara cuando cambia el estado de la antena
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
                        //_logger.LogInformation($"Estado cambiado para el lector en IP: {reader.IPAddress}, nuevo estado: FIN_CICLO_ANT (registrado cada {AntCycleEndLogInterval} ocurrencias)");
                    }
                }
                else
                {
                    _logger.LogInformation($"Estado cambiado para el lector en IP: {reader.IPAddress}, nuevo estado: {e.state}");
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

        // Método para manejar el estado IDLE
        private void HandleIdleState(HighLevelInterface reader)
        {
            switch (reader.LastMacErrorCode)
            {
                case 0x000:
                    break;
                case 0x306:
                    RestartReader(reader, "Lector demasiado caliente", 180);
                    break;
                case 0x309:
                    RestartReader(reader, "Potencia reflejada demasiado alta", 3);
                    break;
                default:
                    _logger.LogError($"Mac Error: 0x{reader.LastMacErrorCode:X}, please report to CSL technical support.");
                    break;
            }
        }

        // Método para manejar el estado RESET
        private void HandleResetState(HighLevelInterface reader)
        {
            Thread service = new Thread(() =>
            {
                DateTime reconnTimer = DateTime.Now;
                int retryCount = 0;
                int maxRetries = 5;

                _logger.LogInformation($"Intentando reconectar el lector en IP: {reader.IPAddress}");

                while (retryCount < maxRetries)
                {
                    retryCount++;

                    if (string.IsNullOrWhiteSpace(reader.IPAddress) || reader.IPAddress == "0.0.0.0")
                    {
                        _logger.LogError($"IDirección IP inválida para la reconexión: {reader.IPAddress}");
                        break;
                    }

                    try
                    {
                        if (!IsSocketConnected(reader.IPAddress, 1515))
                        {
                            _logger.LogWarning($"Socket is not connected for IP: {reader.IPAddress}, retrying ({retryCount}/{maxRetries})...");
                            Thread.Sleep(2000);
                            continue;
                        }

                        var result = reader.Reconnect(1);
                        if (result == CSLibrary.Constants.Result.OK)
                        {
                            _logger.LogInformation($"Reconexión exitosa del lector en IP: {reader.IPAddress}");
                            InventorySetting(reader);
                            reader.StartOperation(CSLibrary.Constants.Operation.TAG_RANGING, false);

                            // Encender semáforo verde después de la reconexión
                            if (HasGPIO(reader.IPAddress))
                            {
                                SetGPO0(reader, true); // Encender LED verde
                            }

                            break;
                        }
                        else
                        {
                            _logger.LogWarning($"Intento de reconexión fallido para el lector en IP: {reader.IPAddress}, result: {result} ({retryCount}/{maxRetries})");
                        }
                    }
                    catch (ObjectDisposedException ex)
                    {
                        _logger.LogError(ex, $"ObjectDisposedException capturada durante la reconexión para el lector en IP: {reader.IPAddress}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Excepción capturada durante la reconexión para el lector en IP: {reader.IPAddress}");
                    }

                    Thread.Sleep(2000);
                }

                _logger.LogInformation($" Intentos de reconexión para el lector en IP: {reader.IPAddress} completados con {retryCount} intentos.");
            })
            {
                IsBackground = true
            };
            try
            {
                service.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Excepción capturada al iniciar el hilo de reconexión para el lector en IP: {reader.IPAddress}");
            }
        }


        // Método para reiniciar el lector
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

        // Evento que se dispara cuando se lee una etiqueta
        private void ReaderXP_TagInventoryEvent(object sender, OnAsyncCallbackEventArgs e)
        {
            lock (TagInventoryLock)
            {
                var reader = (HighLevelInterface)sender;
                string tag = $"{reader.IPAddress}: {e.info.epc.ToString()}";

                if (!IsTagInDatabase(e.info.epc.ToString())) return;

                // Si la antena tiene GPIO habilitado, manejar el encendido de los LEDs antes de actualizar la hora
                if (HasGPIO(reader.IPAddress))
                {
                    HandleGpioActions(reader, e.info.epc.ToString());
                }

                // Actualiza la hora de lectura de la etiqueta después de manejar el GPIO
                UpdateReadsCollection(reader.IPAddress, e.info.epc.ToString());

                // Enviar la etiqueta a través de WebSocket
                try
                {
                    WebSocketController.SendTag(tag);
                }
                catch (ObjectDisposedException ex)
                {
                    _logger.LogError(ex, $"WebSocket object disposed exception for tag {tag}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error sending tag {tag} via WebSocket");
                }
            }
        }

        // Verificar si la etiqueta está en la base de datos
        private bool IsTagInDatabase(string epc)
        {
            var assetsCollection = _assetsDatabase.GetCollection<BsonDocument>("assets");
            var filter = Builders<BsonDocument>.Filter.Eq("EPC", epc);
            var result = assetsCollection.Find(filter).FirstOrDefault();
            return result != null;
        }

        // Método para actualizar la colección de lecturas
        private void UpdateReadsCollection(string ip, string epc)
        {
            var readsCollection = _antennasDatabase.GetCollection<BsonDocument>("Reads");
            var filter = Builders<BsonDocument>.Filter.Eq("tag", epc) & Builders<BsonDocument>.Filter.Eq("IP", ip);
            var existingRead = readsCollection.Find(filter).FirstOrDefault();

            DateTime currentTime = DateTime.Now;

            if (existingRead == null)
            {
                var newRead = new BsonDocument
                {
                    { "IP", ip },
                    { "tag", epc },
                    { "lastReadTime", currentTime.ToString("dd-MM-yyyy HH:mm") }
                };
                readsCollection.InsertOne(newRead);
            }
            else
            {
                var update = Builders<BsonDocument>.Update.Set("lastReadTime", currentTime.ToString("dd-MM-yyyy HH:mm"));
                readsCollection.UpdateOne(filter, update);
            }
        }

        // Configuración de inventario para la antena
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

        // Método para verificar si el socket está conectado con manejo de reintentos
        private bool IsSocketConnected(string ip, int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var result = client.BeginConnect(ip, port, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10000));

                    if (!success)
                    {
                        _logger.LogWarning($"No se puede conectar a la IP {ip} en el puerto {port}");
                        return false;
                    }

                    client.EndConnect(result);
                    _logger.LogInformation($"Conexión exitosa a la IP {ip} en el puerto {port}");
                    return true;
                }
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, $"ObjectDisposedException durante la conexión a la IP {ip} en el puerto {port}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Excepción durante la conexión a la IP {ip} en el puerto {port}");
                return false;
            }
        }


        // Método para verificar si la antena tiene GPIO habilitado
        private bool HasGPIO(string ip)
        {
            var antennasCollection = _antennasDatabase.GetCollection<BsonDocument>("Antennas");
            var filter = Builders<BsonDocument>.Filter.Eq("IP", ip);
            var antenna = antennasCollection.Find(filter).FirstOrDefault();
            return antenna != null && antenna.Contains("GPIO") && antenna["GPIO"].AsBoolean;
        }

        // Método para manejar las acciones del GPIO
        private void HandleGpioActions(HighLevelInterface reader, string epc)
        {
            var assetsCollection = _assetsDatabase.GetCollection<BsonDocument>("assets");
            var filter = Builders<BsonDocument>.Filter.Eq("EPC", epc);
            var asset = assetsCollection.Find(filter).FirstOrDefault();

            bool isException = false;

            // El LED rojo se enciende si la categoría del EPC o el EPC mismo se encuentran en la base de datos de excepciones
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");

            // Verificar si el EPC está en la base de datos de excepciones
            var epcFilter = Builders<BsonDocument>.Filter.Eq("EPC", epc);
            var epcException = exceptionsCollection.Find(epcFilter).FirstOrDefault();
            if (epcException != null)
            {
                isException = true;
            }

            // Verificar si la categoría está en la base de datos de excepciones
            if (asset != null)
            {
                var category = asset["category"]["label"].AsString;
                var categoryFilter = Builders<BsonDocument>.Filter.Eq("category", category);
                var categoryException = exceptionsCollection.Find(categoryFilter).FirstOrDefault();
                if (categoryException != null)
                {
                    isException = true;
                }
            }

            if (!isException)
            {
                // Consultar la última hora de lectura desde la base de datos
                var readsCollection = _antennasDatabase.GetCollection<BsonDocument>("Reads");
                var readsFilter = Builders<BsonDocument>.Filter.Eq("tag", epc) & Builders<BsonDocument>.Filter.Eq("IP", reader.IPAddress);
                var readEntry = readsCollection.Find(readsFilter).FirstOrDefault();

                DateTime lastReadTime = DateTime.MinValue;
                bool isNewTag = true;

                if (readEntry != null)
                {
                    isNewTag = false;
                    lastReadTime = DateTime.ParseExact(readEntry["lastReadTime"].AsString, "dd-MM-yyyy HH:mm", null);
                }

                // Actualizar la última hora de activación en la base de datos antes de encender el LED
                var update = Builders<BsonDocument>.Update.Set("lastReadTime", DateTime.Now.ToString("dd-MM-yyyy HH:mm"));
                readsCollection.UpdateOne(readsFilter, update, new UpdateOptions { IsUpsert = true });

                // Verificar si el EPC es nuevo o si han pasado al menos X minutos desde la última activación del LED rojo
                if (isNewTag || (DateTime.Now - lastReadTime).TotalMinutes >= 1)
                {
                    _logger.LogInformation($"Encendiendo LED rojo para EPC {epc} en IP {reader.IPAddress}.");

                    SetGPO1(reader, true);
                    Task.Delay(15000).ContinueWith(t =>
                    {
                        SetGPO1(reader, false);
                    });
                }
                else
                {
                    // Registrar el mensaje solo si ha pasado el intervalo de tiempo definido
                    DateTime lastLoggedTime;
                    if (!LastLoggedTimeDict.TryGetValue(epc, out lastLoggedTime) || (DateTime.Now - lastLoggedTime).TotalSeconds >= LogIntervalSeconds)
                    {
                        _logger.LogInformation($"El EPC {epc} fue leído en IP {reader.IPAddress} hace menos de 1 minutos.");
                        LastLoggedTimeDict[epc] = DateTime.Now;
                    }
                }
            }
        }
    }
}

