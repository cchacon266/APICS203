using CS203XAPI.Models;
using MongoDB.Driver;
using MongoDB.Bson;
using System;
using System.Collections.Generic;
using CS203XAPI.Controllers;
using System.Text.Json;

namespace CS203XAPI.Services
{
    public interface ILogService
    {
        void Log(string level, string message, string source);
        List<LogEntry> GetAllLogs();
        void ClearLogs();
    }

    public class LogService : ILogService
    {
        private readonly IMongoCollection<LogEntry> _logsCollection;

        public LogService(IMongoClient mongoClient)
        {
            var database = mongoClient.GetDatabase("assets-app-antenas");
            _logsCollection = database.GetCollection<LogEntry>("Logs");
        }

        public void Log(string level, string message, string source)
        {
            var logEntry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message,
                Source = source
            };

            _logsCollection.InsertOne(logEntry);

            // Enviar el log a través de WebSocket
            var logMessageJson = JsonSerializer.Serialize(logEntry);
            WebSocketController.SendLog(logMessageJson);
        }

        public List<LogEntry> GetAllLogs()
        {
            return _logsCollection.Find(new BsonDocument()).SortByDescending(log => log.Timestamp).ToList();
        }

        public void ClearLogs()
        {
            _logsCollection.DeleteMany(new BsonDocument());
        }
    }
}
