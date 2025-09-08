using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EsaLogistica.Api.Dtos;

namespace EsaLogistica.Api.Services
{
    public interface ITxtParserService
    {
        Task<IEnumerable<PedidoDto>> ParseTxtAsync(Stream cabeceraStream, Stream detalleStream);
    }
}
