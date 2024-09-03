using Microsoft.AspNetCore.Mvc;
using CS203XAPI.Services;
using System.Collections.Generic;

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

        // Endpoint para obtener todos los logs
        [HttpGet]
        public IActionResult GetAllLogs()
        {
            var logs = _logService.GetAllLogs();
            return Ok(logs);
        }

        // Endpoint para eliminar todos los logs
        [HttpDelete]
        public IActionResult ClearLogs()
        {
            _logService.ClearLogs();
            return NoContent();
        }
    }
}
