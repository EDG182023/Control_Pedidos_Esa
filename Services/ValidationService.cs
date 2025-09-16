using System;
using System.Data;
using System.Threading.Tasks;
using EsaLogistica.Api.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Linq;

namespace EsaLogistica.Api.Services
{
    public class ValidationService : IValidationService
    {
        private readonly ILogger<ValidationService> _logger;
        private readonly string? _connectionString;

        public ValidationService(ILogger<ValidationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _connectionString = configuration.GetConnectionString("SaadDb");
        }

        public async Task ValidateCabeceraAsync(CabeceraDto cab)
        {
            // 1. VALIDACIONES BÁSICAS - CAMPOS OBLIGATORIOS
            if (string.IsNullOrWhiteSpace(cab.Numero))
                throw new Exception("Número de cabecera es obligatorio");
            
            if (string.IsNullOrWhiteSpace(cab.ClienteCodigo))
                throw new Exception("Código de cliente es obligatorio");
            
            if (string.IsNullOrWhiteSpace(cab.RazonSocial))
                throw new Exception("Razón social es obligatoria");
            
            if (string.IsNullOrWhiteSpace(cab.Direccion))
                throw new Exception("Dirección es obligatoria");
            
            if (cab.FechaEmision == default)
                throw new Exception("Fecha de emisión es obligatoria");
            
            if (cab.FechaEntrega == default)
                throw new Exception("Fecha de entrega es obligatoria");

            // 2. VALIDACIONES LÓGICAS
            if (cab.FechaEntrega < cab.FechaEmision)
                throw new Exception("La fecha de entrega no puede ser anterior a la fecha de emisión");

            // 3. VALIDAR CLIENTE EXISTE
            await ValidarClienteExisteAsync(cab.ClienteCodigo);

            _logger.LogDebug("Cabecera {Numero} validada correctamente", cab.Numero);
        }

        public async Task ValidateDetalleAsync(DetalleDto det)
        {
            // 1. VALIDACIONES BÁSICAS
            if (string.IsNullOrWhiteSpace(det.ProductoCodigo))
                throw new Exception("Código de producto es obligatorio");
            
            if (det.Cantidad <= 0)
                throw new Exception("Cantidad debe ser mayor a cero");

            // 2. VALIDAR PRODUCTO EXISTE
            await ValidarProductoExisteAsync(det.ProductoCodigo, det.ProductoCompaniaCodigo);

            // 3. VALIDAR LOTE (si aplica)
            if (!string.IsNullOrWhiteSpace(det.LoteCodigo))
            {
                await ValidarLoteAsync(det.ProductoCodigo, det.LoteCodigo, det.LoteVencimiento);
            }

            // 4. VALIDAR STOCK para compañía 05
            if (det.ProductoCompaniaCodigo == "05")
            {
                await ValidarStockDisponibleAsync(det);
            }

            _logger.LogDebug("Detalle {Numero}-{Linea} validado", det.Numero, det.Linea);
        }

        // MÉTODOS DE VALIDACIÓN ESPECÍFICOS

        private async Task ValidarClienteExisteAsync(string clienteCodigo)
        {
            using var connection = new SqlConnection(_connectionString);
            var existe = await connection.QuerySingleOrDefaultAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM Clientes WHERE Codigo = @Codigo) THEN 1 ELSE 0 END AS BIT)",
                new { Codigo = clienteCodigo });

            if (!existe)
                throw new Exception($"Cliente {clienteCodigo} no existe");
        }

        private async Task ValidarProductoExisteAsync(string productoCodigo, string companiaCodigo)
        {
            using var connection = new SqlConnection(_connectionString);
            var existe = await connection.QuerySingleOrDefaultAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM Productos WHERE Codigo = @Codigo AND Compania = @Compania) THEN 1 ELSE 0 END AS BIT)",
                new { Codigo = productoCodigo, Compania = companiaCodigo });

            if (!existe)
                throw new Exception($"Producto {productoCodigo} no existe para compañía {companiaCodigo}");
        }

        private async Task ValidarLoteAsync(string productoCodigo, string loteCodigo, DateTime loteVencimiento)
        {
            // Validar formato del lote
            if (string.IsNullOrWhiteSpace(loteCodigo))
                throw new Exception("Código de lote no puede estar vacío");

            // Validar fecha de vencimiento
            if (loteVencimiento <= DateTime.Now)
                throw new Exception($"Lote {loteCodigo} está vencido");

            // Opcional: Validar lote existe en sistema
            using var connection = new SqlConnection(_connectionString);
            var existeLote = await connection.QuerySingleOrDefaultAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM Lotes WHERE ProductoCodigo = @Producto AND LoteCodigo = @Lote) THEN 1 ELSE 0 END AS BIT)",
                new { Producto = productoCodigo, Lote = loteCodigo });

            if (!existeLote)
                throw new Exception($"Lote {loteCodigo} no existe para producto {productoCodigo}");
        }

        private async Task ValidarStockDisponibleAsync(DetalleDto det)
        {
            using var connection = new SqlConnection(_connectionString);
            
            var stockDisponible = await connection.QuerySingleOrDefaultAsync<decimal?>(
                "SELECT stockDisponible FROM [SAAD].[dbo].[_stock_disponible] WHERE cia = @Cia AND producto = @Producto",
                new { Cia = det.ProductoCompaniaCodigo, Producto = det.ProductoCodigo });

            if (!stockDisponible.HasValue)
                throw new Exception($"Producto {det.ProductoCodigo} sin stock registrado");

            if (stockDisponible.Value < det.Cantidad)
                throw new Exception($"Stock insuficiente para {det.ProductoCodigo}: Disponible {stockDisponible.Value}, Solicitado {det.Cantidad}");
        }

        public async Task ValidatePedidoTxtAsync(PedidoDto pedido)
        {
            // Validaciones específicas para formato TXT
            if (string.IsNullOrWhiteSpace(pedido.numero))
                throw new Exception("Número de pedido es obligatorio");

            if (!pedido.detalle.Any())
                throw new Exception("El pedido debe contener al menos un detalle");

            // Validar cada detalle del pedido
            foreach (var det in pedido.detalle)
            {
                if (det.cantidad <= 0)
                    throw new Exception($"Línea {det.linea}: Cantidad debe ser mayor a cero");

                await ValidarProductoExisteAsync(det.productoCodigo, det.productoCompaniaCodigo);
                
                if (det.productoCompaniaCodigo == "05")
                {
                    var detalleDto = new DetalleDto
                    {
                        ProductoCodigo = det.productoCodigo,
                        ProductoCompaniaCodigo = det.productoCompaniaCodigo,
                        Cantidad = det.cantidad
                    };
                    await ValidarStockDisponibleAsync(detalleDto);
                }
            }
        }
    }
}