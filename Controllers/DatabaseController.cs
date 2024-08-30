using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

          [HttpGet("report")]
public IActionResult GetReport()
{
    try
    {
        var collection = _antennasDatabase.GetCollection<BsonDocument>("Reads");
        var records = collection.Find(new BsonDocument()).ToList();

        var csv = new StringBuilder();
        csv.AppendLine("IP,Tag,LastReadTime,AssetName");

        foreach (var record in records)
        {
            string ip = record.GetValue("IP", "N/A").AsString;
            string tag = $"EPC: {record.GetValue("tag", "N/A").AsString}"; // Asegurando que el tag se trate como texto en Excel
            string lastReadTime = record.GetValue("lastReadTime", "N/A").AsString; // Dejando el formato de tiempo como está
            string assetName = record.GetValue("assetName", "N/A").AsString;

            csv.AppendLine($"{ip},{tag},{lastReadTime},{assetName}");
        }

        var csvBytes = Encoding.UTF8.GetBytes(csv.ToString());
        return File(csvBytes, "text/csv", "report.csv");
    }
    catch (Exception ex) // Aquí se declara el tipo de excepción que se captura
    {
        return BadRequest(new { message = "Error generating report", error = ex.Message });
    }
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

            var epcResults = exceptions
                .Where(e => e.Contains("EPC"))
                .Select(e => new
                {
                    Id = e["_id"].ToString(),
                    EPC = e["EPC"].AsString
                })
                .ToList();

            var categoryResults = exceptions
                .Where(e => e.Contains("category"))
                .Select(e => new
                {
                    Id = e["_id"].ToString(),
                    Category = e["category"].AsString
                })
                .ToList();

            return Ok(new
            {
                EPC = epcResults,
                Categories = categoryResults
            });
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
                updateDefinition.Add(Builders<BsonDocument>.Update.Unset("category")); // Unset category if EPC is set
            }
            if (!string.IsNullOrEmpty(model.Category))
            {
                updateDefinition.Add(Builders<BsonDocument>.Update.Set("category", model.Category));
                updateDefinition.Add(Builders<BsonDocument>.Update.Unset("EPC")); // Unset EPC if category is set
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
