using EsaLogistica.Api.Dtos;
using System.Threading.Tasks;

namespace EsaLogistica.Api.Services
{
    /// <summary>
    /// Servicio de validación para cabeceras, detalles y pedidos TXT.
    /// Incluye validaciones de negocio, stock local y reglas específicas por tipo de comprobante.
    /// </summary>
    public interface IValidationService
    {
        /// <summary>
        /// Valida una cabecera de pedido/documento según las reglas de negocio.
        /// Incluye validaciones específicas por tipo de comprobante.
        /// </summary>
        /// <param name="cab">Cabecera a validar</param>
        /// <returns>Task que completa si la validación es exitosa, o lanza excepción con el error</returns>
        Task ValidateCabeceraAsync(CabeceraDto cab);
        
        /// <summary>
        /// Valida un detalle de pedido/documento.
        /// Para compañía 05, valida stock contra la base de datos local.
        /// </summary>
        /// <param name="det">Detalle a validar</param>
        /// <returns>Task que completa si la validación es exitosa, o lanza excepción con el error</returns>
        Task ValidateDetalleAsync(DetalleDto det);
        
        /// <summary>
        /// Valida un pedido completo en formato TXT (cabecera + detalles).
        /// Incluye validación de stock para productos de compañía 05.
        /// </summary>
        /// <param name="pedido">Pedido TXT a validar</param>
        /// <returns>Task que completa si la validación es exitosa, o lanza excepción con el error</returns>
        Task ValidatePedidoTxtAsync(PedidoDto pedido);
    }
}