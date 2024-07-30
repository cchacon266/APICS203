using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Generic;
using System.Linq;

namespace CS203XAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class DatabaseController : ControllerBase
    {
        private readonly IMongoDatabase _antennasDatabase;

        public DatabaseController(IMongoClient mongoClient)
        {
            _antennasDatabase = mongoClient.GetDatabase("assets-app-antenas");
        }

        [HttpGet("antennas")]
        public IActionResult GetAntennas()
        {
            var antennasCollection = _antennasDatabase.GetCollection<BsonDocument>("Antennas");
            var antennas = antennasCollection.Find(new BsonDocument()).ToList();
            var result = antennas.Select(a => new
            {
                Id = a["_id"].ToString(),
                IP = a["IP"].AsString,
                GPIO = a.Contains("GPIO") ? a["GPIO"].AsBoolean : false,
                Location = a.Contains("location") ? a["location"].AsString : "Unknown"
            }).ToList();
            return Ok(result);
        }

        [HttpGet("antennas/{ip}")]
        public IActionResult GetAntennaByIp(string ip)
        {
            var antennasCollection = _antennasDatabase.GetCollection<BsonDocument>("Antennas");
            var filter = Builders<BsonDocument>.Filter.Eq("IP", ip);
            var antenna = antennasCollection.Find(filter).FirstOrDefault();

            if (antenna == null)
            {
                return NotFound();
            }

            var result = new
            {
                Id = antenna["_id"].ToString(),
                IP = antenna["IP"].AsString,
                GPIO = antenna.Contains("GPIO") ? antenna["GPIO"].AsBoolean : false,
                Location = antenna.Contains("location") ? antenna["location"].AsString : "Unknown"
            };

            return Ok(result);
        }

        [HttpPost("antennas")]
        public IActionResult CreateAntenna([FromBody] AntennaModel model)
        {
            var antennasCollection = _antennasDatabase.GetCollection<BsonDocument>("Antennas");
            var antenna = new BsonDocument
            {
                { "IP", model.IP },
                { "GPIO", model.GPIO },
                { "location", model.Location }
            };
            antennasCollection.InsertOne(antenna);
            return CreatedAtAction(nameof(GetAntennaByIp), new { ip = model.IP }, new
            {
                Id = antenna["_id"].ToString(),
                IP = model.IP,
                GPIO = model.GPIO,
                Location = model.Location
            });
        }

        [HttpPut("antennas/{id}")]
        public IActionResult UpdateAntenna(string id, [FromBody] AntennaModel model)
        {
            var antennasCollection = _antennasDatabase.GetCollection<BsonDocument>("Antennas");
            var filter = Builders<BsonDocument>.Filter.Eq("_id", new ObjectId(id));
            var update = Builders<BsonDocument>.Update
                .Set("IP", model.IP)
                .Set("GPIO", model.GPIO)
                .Set("location", model.Location);
            var result = antennasCollection.UpdateOne(filter, update);

            if (result.MatchedCount == 0)
            {
                return NotFound();
            }

            return NoContent();
        }

        [HttpDelete("antennas/{id}")]
        public IActionResult DeleteAntenna(string id)
        {
            var antennasCollection = _antennasDatabase.GetCollection<BsonDocument>("Antennas");
            var filter = Builders<BsonDocument>.Filter.Eq("_id", new ObjectId(id));
            var result = antennasCollection.DeleteOne(filter);

            if (result.DeletedCount == 0)
            {
                return NotFound();
            }

            return NoContent();
        }

        [HttpGet("exceptions")]
        public IActionResult GetExceptions()
        {
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");
            var exceptions = exceptionsCollection.Find(new BsonDocument()).ToList();
            var result = exceptions.Select(e => new
            {
                Id = e["_id"].ToString(),
                EPC = e.Contains("EPC") ? e["EPC"].AsString : null,
                Category = e.Contains("category") ? e["category"].AsString : null
            }).ToList();
            return Ok(result);
        }

        [HttpGet("exceptions/{id}")]
        public IActionResult GetExceptionById(string id)
        {
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");
            var filter = Builders<BsonDocument>.Filter.Eq("_id", new ObjectId(id));
            var exception = exceptionsCollection.Find(filter).FirstOrDefault();

            if (exception == null)
            {
                return NotFound();
            }

            var result = new
            {
                Id = exception["_id"].ToString(),
                EPC = exception.Contains("EPC") ? exception["EPC"].AsString : null,
                Category = exception.Contains("category") ? exception["category"].AsString : null
            };

            return Ok(result);
        }

        [HttpGet("exceptions/epc/{epc}")]
        public IActionResult GetExceptionByEPC(string epc)
        {
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");
            var filter = Builders<BsonDocument>.Filter.Eq("EPC", epc);
            var exception = exceptionsCollection.Find(filter).FirstOrDefault();

            if (exception == null)
            {
                return NotFound();
            }

            var result = new
            {
                Id = exception["_id"].ToString(),
                EPC = exception.Contains("EPC") ? exception["EPC"].AsString : null,
                Category = exception.Contains("category") ? exception["category"].AsString : null
            };

            return Ok(result);
        }

        [HttpGet("exceptions/category/{category}")]
        public IActionResult GetExceptionByCategory(string category)
        {
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");
            var filter = Builders<BsonDocument>.Filter.Eq("category", category);
            var exception = exceptionsCollection.Find(filter).FirstOrDefault();

            if (exception == null)
            {
                return NotFound();
            }

            var result = new
            {
                Id = exception["_id"].ToString(),
                EPC = exception.Contains("EPC") ? exception["EPC"].AsString : null,
                Category = exception.Contains("category") ? exception["category"].AsString : null
            };

            return Ok(result);
        }

        [HttpPost("exceptions")]
        public IActionResult CreateException([FromBody] ExceptionModel model)
        {
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");
            var exception = new BsonDocument();
            if (!string.IsNullOrEmpty(model.EPC))
            {
                exception.Add("EPC", model.EPC);
            }
            if (!string.IsNullOrEmpty(model.Category))
            {
                exception.Add("category", model.Category);
            }
            exceptionsCollection.InsertOne(exception);
            return CreatedAtAction(nameof(GetExceptionById), new { id = exception["_id"].ToString() }, new
            {
                Id = exception["_id"].ToString(),
                EPC = model.EPC,
                Category = model.Category
            });
        }

        [HttpPut("exceptions/{id}")]
        public IActionResult UpdateException(string id, [FromBody] ExceptionModel model)
        {
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");
            var filter = Builders<BsonDocument>.Filter.Eq("_id", new ObjectId(id));

            var updateDefinition = new List<UpdateDefinition<BsonDocument>>();
            if (!string.IsNullOrEmpty(model.EPC))
            {
                updateDefinition.Add(Builders<BsonDocument>.Update.Set("EPC", model.EPC));
            }
            if (!string.IsNullOrEmpty(model.Category))
            {
                updateDefinition.Add(Builders<BsonDocument>.Update.Set("category", model.Category));
            }

            var update = Builders<BsonDocument>.Update.Combine(updateDefinition);
            var result = exceptionsCollection.UpdateOne(filter, update);

            if (result.MatchedCount == 0)
            {
                return NotFound();
            }

            return NoContent();
        }

        [HttpDelete("exceptions/{id}")]
        public IActionResult DeleteException(string id)
        {
            var exceptionsCollection = _antennasDatabase.GetCollection<BsonDocument>("Exceptions");
            var filter = Builders<BsonDocument>.Filter.Eq("_id", new ObjectId(id));
            var result = exceptionsCollection.DeleteOne(filter);

            if (result.DeletedCount == 0)
            {
                return NotFound();
            }

            return NoContent();
        }
    }

    public class AntennaModel
    {
        public string IP { get; set; }
        public bool GPIO { get; set; }
        public string Location { get; set; }
    }

    public class ExceptionModel
    {
        public string EPC { get; set; }
        public string Category { get; set; }
    }
}
