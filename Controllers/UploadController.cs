using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using EsaLogistica.Api.Services;
using Microsoft.Extensions.Logging;
using System;

namespace EsaLogistica.Api.Controllers
{
    [ApiController]
    [Route("api/upload")]
    public class UploadController : ControllerBase
    {
        private readonly ICargaService _carga;
        private readonly ILogger<UploadController> _logger;

        public UploadController(ICargaService carga, ILogger<UploadController> logger)
        {
            _carga = carga;
            _logger = logger;
        }

        [HttpPost("import")]
        public async Task<IActionResult> Import([FromForm] IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "No se proporcionó un archivo válido",
                        Errors = new[] { "El archivo Excel es obligatorio" }
                    });
                }

                _logger.LogInformation("Iniciando carga de archivo Excel: {FileName}", file.FileName);

                // Cargar archivo en tablas temporales
                await _carga.CargarExcelAsync(file);
                _logger.LogInformation("Archivo Excel cargado exitosamente");

                // Procesar y validar datos
                var (procesados, etapasInsertadas, errores) = await _carga.ProcesarDesdeTempAsync();
                
                _logger.LogInformation("Procesamiento completado: {Procesados} procesados, {Insertadas} insertadas, {ErrorCount} errores", 
                    procesados, etapasInsertadas, errores.Count);

                if (errores.Any())
                {
                    return Ok(new
                    {
                        Success = false,
                        Message = $"Procesamiento completado con errores: {procesados} registros procesados, {etapasInsertadas} insertados correctamente",
                        ProcessedCount = procesados,
                        InsertedCount = etapasInsertadas,
                        ErrorCount = errores.Count,
                        Errors = errores,
                        Details = "Revise los errores de validación. Los registros sin errores fueron procesados correctamente."
                    });
                }

                return Ok(new
                {
                    Success = true,
                    Message = "Archivo procesado exitosamente",
                    ProcessedCount = procesados,
                    InsertedCount = etapasInsertadas,
                    ErrorCount = 0,
                    Details = "Todos los registros fueron validados y procesados correctamente"
                });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("Error de validación en carga de Excel: {Error}", ex.Message);
                return BadRequest(new
                {
                    Success = false,
                    Message = "Error de validación",
                    Errors = new[] { ex.Message }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error general procesando archivo Excel");
                return StatusCode(500, new
                {
                    Success = false,
                    Message = "Error interno del servidor",
                    Errors = new[] { $"Error procesando archivo: {ex.Message}" }
                });
            }
        }

        [HttpPost("import-txt")]
        public async Task<IActionResult> ImportTxt([FromForm] IFormFile cabecera,
                                                   [FromForm] IFormFile detalle)
        {
            try
            {
                if (cabecera == null || detalle == null)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Archivos TXT incompletos",
                        Errors = new[] { "Se requieren ambos archivos: cabecera y detalle" }
                    });
                }

                if (cabecera.Length == 0 || detalle.Length == 0)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Archivos TXT vacíos",
                        Errors = new[] { "Los archivos de cabecera y detalle no pueden estar vacíos" }
                    });
                }

                _logger.LogInformation("Iniciando carga de archivos TXT: Cabecera={CabeceraFileName}, Detalle={DetalleFileName}", 
                    cabecera.FileName, detalle.FileName);

                // Cargar archivos TXT en tablas temporales
                await _carga.CargarTxtAsync(cabecera, detalle);
                _logger.LogInformation("Archivos TXT cargados exitosamente");

                // Procesar y validar datos
                var (procesados, etapasInsertadas, errores) = await _carga.ProcesarDesdeTempAsync();

                _logger.LogInformation("Procesamiento TXT completado: {Procesados} procesados, {Insertadas} insertadas, {ErrorCount} errores", 
                    procesados, etapasInsertadas, errores.Count);

                if (errores.Any())
                {
                    return Ok(new
                    {
                        Success = false,
                        Message = $"Procesamiento completado con errores: {procesados} pedidos procesados, {etapasInsertadas} insertados correctamente",
                        ProcessedCount = procesados,
                        InsertedCount = etapasInsertadas,
                        ErrorCount = errores.Count,
                        Errors = errores,
                        Details = "Revise los errores de validación (especialmente stock). Los pedidos sin errores fueron procesados correctamente."
                    });
                }

                return Ok(new
                {
                    Success = true,
                    Message = "Archivos TXT procesados exitosamente",
                    ProcessedCount = procesados,
                    InsertedCount = etapasInsertadas,
                    ErrorCount = 0,
                    Details = "Todos los pedidos fueron validados y procesados correctamente"
                });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning("Error de validación en carga de TXT: {Error}", ex.Message);
                return BadRequest(new
                {
                    Success = false,
                    Message = "Error de validación",
                    Errors = new[] { ex.Message }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error general procesando archivos TXT");
                return StatusCode(500, new
                {
                    Success = false,
                    Message = "Error interno del servidor",
                    Errors = new[] { $"Error procesando archivos TXT: {ex.Message}" }
                });
            }
        }

        [HttpPost("validate-only")]
        public async Task<IActionResult> ValidateOnly([FromForm] IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "No se proporcionó un archivo válido",
                        Errors = new[] { "El archivo Excel es obligatorio" }
                    });
                }

                _logger.LogInformation("Iniciando validación de archivo Excel: {FileName}", file.FileName);

                // Solo cargar y validar, no procesar
                await _carga.CargarExcelAsync(file);
                
                return Ok(new
                {
                    Success = true,
                    Message = "Archivo validado exitosamente",
                    Details = "El archivo fue cargado y validado. Use el endpoint /import para procesarlo definitivamente."
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error en validación de archivo: {Error}", ex.Message);
                return BadRequest(new
                {
                    Success = false,
                    Message = "Error de validación",
                    Errors = new[] { ex.Message }
                });
            }
        }
    }
}