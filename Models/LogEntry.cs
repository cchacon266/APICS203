using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace CS203XAPI.Models
{
    public class LogEntry
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("timestamp")]
    public DateTime Timestamp { get; set; }

    [BsonElement("level")]
    public string Level { get; set; }

    [BsonElement("message")]
    public string Message { get; set; }

    [BsonElement("source")]
    public string Source { get; set; }

    public override string ToString()
    {
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level}] {Source}: {Message}";
    }
}
}