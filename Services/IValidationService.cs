using EsaLogistica.Api.Dtos;
using System.Threading.Tasks;

namespace EsaLogistica.Api.Services
{
    public interface IValidationService
    {
        Task ValidateCabeceraAsync(CabeceraDto cab);
        Task ValidateDetalleAsync(DetalleDto det);
        Task ValidatePedidoTxtAsync(PedidoDto pedido);
    }
}