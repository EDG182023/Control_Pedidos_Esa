using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using EsaLogistica.Api.Services;

namespace EsaLogistica.Api.Controllers
{
    [ApiController]
    [Route("api/upload")]
    public class UploadController : ControllerBase
    {
        private readonly ICargaService _carga;

        public UploadController(ICargaService carga) => _carga = carga;

        [HttpPost("import")]
        public async Task<IActionResult> Import([FromForm] IFormFile file)
        {
            await _carga.CargarExcelAsync(file);
            var result = await _carga.ProcesarDesdeTempAsync();
            return Ok(result);
        }

        [HttpPost("import-txt")]
        public async Task<IActionResult> ImportTxt([FromForm] IFormFile cabecera,
                                                   [FromForm] IFormFile detalle)
        {
            await _carga.CargarTxtAsync(cabecera, detalle);
            var result = await _carga.ProcesarDesdeTempAsync();
            return Ok(result);
        }
    }
}