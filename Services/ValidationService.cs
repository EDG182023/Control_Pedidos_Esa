using System;
using System.Threading.Tasks;
using EsaLogistica.Api.Dtos;
using Microsoft.Extensions.Logging;

namespace EsaLogistica.Api.Services
{
    public class ValidationService : IValidationService
    {
        private readonly ILogger<ValidationService> _logger;
        private readonly IApiService _apiService;

        public ValidationService(ILogger<ValidationService> logger, IApiService apiService)
        {
            _logger = logger;
            _apiService = apiService;
        }

        public async Task ValidateCabeceraAsync(CabeceraDto cab)
        {
            // Validaciones básicas para todos los tipos
            if (string.IsNullOrWhiteSpace(cab.Numero))
                throw new Exception("Número de cabecera es obligatorio");

            if (string.IsNullOrWhiteSpace(cab.TipoCodigo))
                throw new Exception("Tipo de código es obligatorio");

            if (string.IsNullOrWhiteSpace(cab.Categoria))
                throw new Exception("Categoría es obligatoria");

            if (string.IsNullOrWhiteSpace(cab.Sucursal))
                throw new Exception("Sucursal es obligatoria");

            if (cab.FechaEmision == default)
                throw new Exception("Fecha de emisión es obligatoria");

            if (cab.FechaEntrega == default)
                throw new Exception("Fecha de entrega es obligatoria");

            if (string.IsNullOrWhiteSpace(cab.ClienteCodigo))
                throw new Exception("Código de cliente es obligatorio");

            if (string.IsNullOrWhiteSpace(cab.RazonSocial))
                throw new Exception("Razón social es obligatoria");

            // Validar fechas lógicas
            if (cab.FechaEntrega < cab.FechaEmision)
                throw new Exception("La fecha de entrega no puede ser anterior a la fecha de emisión");

            // Validaciones específicas por tipo de comprobante
            await ValidarPorTipoComprobanteAsync(cab);

            _logger.LogDebug("Cabecera {Numero} validada correctamente", cab.Numero);
        }

        public async Task ValidateDetalleAsync(DetalleDto det)
        {
            // Validaciones básicas
            if (string.IsNullOrWhiteSpace(det.Numero))
                throw new Exception("Número de detalle es obligatorio");

            if (det.Linea <= 0)
                throw new Exception("Línea debe ser mayor a cero");

            if (string.IsNullOrWhiteSpace(det.ProductoCodigo))
                throw new Exception("Código de producto es obligatorio");

            if (string.IsNullOrWhiteSpace(det.ProductoCompaniaCodigo))
                throw new Exception("Código de compañía del producto es obligatorio");

            if (det.Cantidad <= 0)
                throw new Exception("Cantidad debe ser mayor a cero");

            // Validar fecha de vencimiento si tiene lote
            if (!string.IsNullOrWhiteSpace(det.LoteCodigo) && det.LoteVencimiento <= DateTime.Now)
                throw new Exception($"Lote {det.LoteCodigo} está vencido");

            // Validación de stock para compañía 05
            if (det.ProductoCompaniaCodigo == "05")
            {
                await ValidarStockProductoAsync(det);
            }

            _logger.LogDebug("Detalle {Numero}-{Linea} validado correctamente", det.Numero, det.Linea);
        }

        private async Task ValidarPorTipoComprobanteAsync(CabeceraDto cab)
        {
            switch (cab.TipoCodigo)
            {
                case "05": // Comprobantes tipo 05 - Validar stock
                    await ValidarComprobanteTipo05Async(cab);
                    break;
                
                case "10": // Comprobantes tipo 10 - Validar logística
                    ValidarComprobanteTipo10(cab);
                    break;
                
                default:
                    // Validaciones generales para otros tipos
                    ValidarComprobantesGenerales(cab);
                    break;
            }
        }

        private async Task ValidarComprobanteTipo05Async(CabeceraDto cab)
        {
            _logger.LogInformation("Validando comprobante tipo 05: {Numero}", cab.Numero);
            
            // Para tipo 05, las validaciones específicas se harán a nivel de detalle
            // aquí solo validamos que tenga la información básica
            if (string.IsNullOrWhiteSpace(cab.DepositoCodigo))
                throw new Exception("Código de depósito es obligatorio para comprobantes tipo 05");

            await Task.CompletedTask; // Para evitar el warning CS1998
        }

        private void ValidarComprobanteTipo10(CabeceraDto cab)
        {
            _logger.LogInformation("Validando comprobante tipo 10: {Numero}", cab.Numero);

            // Validaciones específicas para tipo 10 (logística)
            if (!cab.Bultos.HasValue || cab.Bultos.Value <= 0)
                throw new Exception("Bultos es obligatorio y debe ser mayor a cero para comprobantes tipo 10");

            if (!cab.Kilos.HasValue || cab.Kilos.Value <= 0)
                throw new Exception("Kilos es obligatorio y debe ser mayor a cero para comprobantes tipo 10");

            if (!cab.M3.HasValue || cab.M3.Value <= 0)
                throw new Exception("M3 es obligatorio y debe ser mayor a cero para comprobantes tipo 10");

            if (string.IsNullOrWhiteSpace(cab.Direccion))
                throw new Exception("Dirección es obligatoria para comprobantes tipo 10");

            if (string.IsNullOrWhiteSpace(cab.CodigoPostal))
                throw new Exception("Código postal es obligatorio para comprobantes tipo 10");

            if (string.IsNullOrWhiteSpace(cab.LocalidadNombre))
                throw new Exception("Nombre de localidad es obligatorio para comprobantes tipo 10");

            // Validar formato de código postal argentino (opcional)
            if (!EsCodigoPostalValido(cab.CodigoPostal))
                throw new Exception($"Código postal {cab.CodigoPostal} no tiene formato válido");

            // Validar que tenga información de contacto
            if (string.IsNullOrWhiteSpace(cab.Telefono) && string.IsNullOrWhiteSpace(cab.Email))
                throw new Exception("Es obligatorio tener al menos teléfono o email para comprobantes tipo 10");
        }

        private void ValidarComprobantesGenerales(CabeceraDto cab)
        {
            // Validaciones para otros tipos de comprobantes
            if (string.IsNullOrWhiteSpace(cab.LocalidadNombre))
                throw new Exception("Nombre de localidad es obligatorio");

            if (string.IsNullOrWhiteSpace(cab.Direccion))
                throw new Exception("Dirección es obligatoria");

            if (string.IsNullOrWhiteSpace(cab.CodigoPostal))
                throw new Exception("Código postal es obligatorio");
        }

        private async Task ValidarStockProductoAsync(DetalleDto det)
        {
            try
            {
                bool hayStock = await _apiService.CheckStockAsync(det.ProductoCodigo);
                
                if (!hayStock)
                {
                    throw new Exception($"Sin stock disponible para producto {det.ProductoCodigo} (Compañía 05)");
                }

                _logger.LogDebug("Stock validado para producto {Producto}", det.ProductoCodigo);
            }
            catch (Exception ex) when (!(ex.Message.Contains("Sin stock")))
            {
                _logger.LogWarning("Error verificando stock para producto {Producto}: {Error}", 
                    det.ProductoCodigo, ex.Message);
                throw new Exception($"Error validando stock para producto {det.ProductoCodigo}: {ex.Message}");
            }
        }

        private bool EsCodigoPostalValido(string codigoPostal)
        {
            if (string.IsNullOrWhiteSpace(codigoPostal))
                return false;

            // Código postal argentino: 4 dígitos (viejo) o formato LETRA4DIGLETRAS (nuevo)
            codigoPostal = codigoPostal.Trim();
            
            // Formato viejo: 4 dígitos
            if (codigoPostal.Length == 4 && codigoPostal.All(char.IsDigit))
                return true;

            // Formato nuevo: C1234XXX (1 letra, 4 dígitos, 3 letras)
            if (codigoPostal.Length == 8 && 
                char.IsLetter(codigoPostal[0]) && 
                codigoPostal.Substring(1, 4).All(char.IsDigit) &&
                codigoPostal.Substring(5, 3).All(char.IsLetter))
                return true;

            return false;
        }

        public async Task ValidatePedidoTxtAsync(PedidoDto pedido)
        {
            // Validaciones para pedidos TXT
            if (string.IsNullOrWhiteSpace(pedido.almacenCodigo))
                throw new Exception("Código de almacén es obligatorio");

            if (string.IsNullOrWhiteSpace(pedido.almacenEmplazamientoCodigo))
                throw new Exception("Código de emplazamiento de almacén es obligatorio");

            if (string.IsNullOrWhiteSpace(pedido.tipoCodigo))
                throw new Exception("Tipo de código es obligatorio");

            if (string.IsNullOrWhiteSpace(pedido.categoria))
                throw new Exception("Categoría es obligatoria");

            if (string.IsNullOrWhiteSpace(pedido.sucursal))
                throw new Exception("Sucursal es obligatoria");

            if (string.IsNullOrWhiteSpace(pedido.numero))
                throw new Exception("Número es obligatorio");

            if (pedido.fechaEmision == default)
                throw new Exception("Fecha de emisión es obligatoria");

            if (pedido.fechaEntrega == default)
                throw new Exception("Fecha de entrega es obligatoria");

            if (string.IsNullOrWhiteSpace(pedido.clienteCodigo))
                throw new Exception("Código de cliente es obligatorio");

            if (string.IsNullOrWhiteSpace(pedido.razonSocial))
                throw new Exception("Razón social es obligatoria");

            if (string.IsNullOrWhiteSpace(pedido.domicilio))
                throw new Exception("Domicilio es obligatorio");

            if (string.IsNullOrWhiteSpace(pedido.localidadCodigo))
                throw new Exception("Código de localidad es obligatorio");

            if (pedido.fechaEntrega < pedido.fechaEmision)
                throw new Exception("La fecha de entrega no puede ser anterior a la fecha de emisión");

            if (pedido.detalle == null || !pedido.detalle.Any())
                throw new Exception("El pedido debe contener al menos un detalle");

            // Validar cada detalle
            foreach (var det in pedido.detalle)
            {
                await ValidatePedidoDetalleAsync(det);
            }

            _logger.LogDebug("Pedido TXT {Numero} validado correctamente", pedido.numero);
        }

        private async Task ValidatePedidoDetalleAsync(PedidoDetalleDto detalle)
        {
            if (string.IsNullOrWhiteSpace(detalle.productoCompaniaCodigo))
                throw new Exception($"Detalle línea {detalle.linea}: Código de compañía del producto es obligatorio");

            if (string.IsNullOrWhiteSpace(detalle.productoCodigo))
                throw new Exception($"Detalle línea {detalle.linea}: Código de producto es obligatorio");

            if (detalle.cantidad <= 0)
                throw new Exception($"Detalle línea {detalle.linea}: Cantidad debe ser mayor a cero");

            // Validar stock para compañía 05
            if (detalle.productoCompaniaCodigo == "05")
            {
                try
                {
                    bool hayStock = await _apiService.CheckStockAsync(detalle.productoCodigo);
                    
                    if (!hayStock)
                    {
                        throw new Exception($"Detalle línea {detalle.linea}: Sin stock disponible para producto {detalle.productoCodigo} (Compañía 05)");
                    }
                }
                catch (Exception ex) when (!(ex.Message.Contains("Sin stock")))
                {
                    _logger.LogWarning("Error verificando stock para producto {Producto} en línea {Linea}: {Error}", 
                        detalle.productoCodigo, detalle.linea, ex.Message);
                    throw new Exception($"Detalle línea {detalle.linea}: Error validando stock para producto {detalle.productoCodigo}: {ex.Message}");
                }
            }
        }
    }
}