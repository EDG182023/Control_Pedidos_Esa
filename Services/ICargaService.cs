using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace EsaLogistica.Api.Services
{
    public interface ICargaService
    {
        // 1) Solo guarda Excel/TXT en tablas temporales
        Task CargarExcelAsync(IFormFile file);
        Task CargarTxtAsync(IFormFile cabecera, IFormFile detalle);

        // 2) Procesa (valida + API/DB) desde tablas
        Task<(int procesados, int etapasInsertadas, List<string> errores)> ProcesarDesdeTempAsync();
    }
}