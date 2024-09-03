using Microsoft.AspNetCore.Mvc;
using CS203XAPI.Services;
using System.Globalization;
using System.Linq;

namespace CS203XAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class LogsController : ControllerBase
    {
        private readonly ILogService _logService;

        public LogsController(ILogService logService)
        {
            _logService = logService;
        }

        // Endpoint para obtener todos los logs con fechas formateadas
        [HttpGet("list")]
        public IActionResult GetAllLogs()
        {
            var logs = _logService.GetAllLogs();
            var formattedLogs = logs.Select(log => new
            {
                log.Level,
                Message = log.Message,
                Timestamp = log.Timestamp.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                log.Source
            }).ToList();

            return Ok(formattedLogs);
        }

        // Endpoint para eliminar todos los logs
        [HttpDelete("clear")]
        public IActionResult ClearLogs()
        {
            _logService.ClearLogs();
            return NoContent();
        }
    }
}
