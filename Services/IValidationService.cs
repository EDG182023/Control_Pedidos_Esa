using EsaLogistica.Api.Dtos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EsaLogistica.Api.Services
{
    /// <summary>
    /// Servicio de validación optimizado para cabeceras, detalles y pedidos TXT.
    /// Incluye validaciones de negocio, stock local y cache para lotes grandes.
    /// </summary>
    public interface IValidationService
    {
        /// <summary>
        /// Valida una cabecera de pedido/documento según las reglas de negocio.
        /// </summary>
        Task ValidateCabeceraAsync(CabeceraDto cab);
        
        /// <summary>
        /// Valida un detalle de pedido/documento.
        /// Para compañía 05, valida stock contra la base de datos local.
        /// </summary>
        Task ValidateDetalleAsync(DetalleDto det);
        
        /// <summary>
        /// Valida un pedido completo en formato TXT (cabecera + detalles).
        /// </summary>
        Task ValidatePedidoTxtAsync(PedidoDto pedido);
        
        /// <summary>
        /// NUEVO: Pre-carga cache de validación para procesar lotes grandes eficientemente.
        /// Reduce queries repetidas consultando clientes, productos y stock en lote.
        /// </summary>
        Task ValidarLoteAsync(List<PedidoDto> pedidos);
    }
}