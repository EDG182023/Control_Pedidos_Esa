using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using EsaLogistica.Api.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Dapper;

namespace EsaLogistica.Api.Services
{
    public class ValidationService : IValidationService
    {
        private readonly ILogger<ValidationService> _logger;
        private readonly string? _connectionString;

        private readonly Dictionary<string, bool> _clientesCache = new();
        private readonly Dictionary<(string, string), bool> _productosCache = new();
        private readonly Dictionary<(string, string), decimal?> _stockCache = new();

        public ValidationService(ILogger<ValidationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _connectionString = configuration.GetConnectionString("SaadDb");
        }

        public async Task ValidarLoteAsync(List<PedidoDto> pedidos)
        {
            if (!pedidos.Any()) return;

            _clientesCache.Clear();
            _productosCache.Clear();
            _stockCache.Clear();

            var clientesCodigos = pedidos
                .Select(p => p.clienteCodigo)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();

            // AHORA: tuplas tipadas (no anonymous + dynamic)
            var productos = pedidos
                .SelectMany(p => p.detalle)
                .Select(d => (productoCodigo: d.productoCodigo, productoCompaniaCodigo: d.productoCompaniaCodigo))
                .Distinct()
                .ToList();

            using var connection = new SqlConnection(_connectionString);

            await PrecargarClientesAsync(connection, clientesCodigos);
            await PrecargarProductosAsync(connection, productos);

            var productos05 = productos.Where(p => p.productoCompaniaCodigo == "05").ToList();
            if (productos05.Any())
            {
                await PrecargarStockAsync(connection, productos05);
            }

            _logger.LogInformation("Cache precargado: {ClientesCount} clientes, {ProductosCount} productos, {StockCount} stocks",
                _clientesCache.Count, _productosCache.Count, _stockCache.Count);
        }

        private async Task PrecargarClientesAsync(SqlConnection connection, List<string> clientesCodigos)
        {
            if (!clientesCodigos.Any()) return;

            var parameters = string.Join(",", clientesCodigos.Select((_, i) => $"@c{i}"));
            var dynamicParams = new DynamicParameters();

            for (int i = 0; i < clientesCodigos.Count; i++)
                dynamicParams.Add($"@c{i}", clientesCodigos[i]);

            var query = $"SELECT Codigo FROM Clientes WHERE Codigo IN ({parameters})";
            var clientesExistentes = (await connection.QueryAsync<string>(query, dynamicParams)).ToHashSet();

            foreach (var codigo in clientesCodigos)
                _clientesCache[codigo] = clientesExistentes.Contains(codigo);
        }

        // AHORA: tipado fuerte con tuplas
        private async Task PrecargarProductosAsync(SqlConnection connection, List<(string productoCodigo, string productoCompaniaCodigo)> productos)
        {
            if (!productos.Any()) return;

            var whereConditions = new List<string>();
            var dynamicParams = new DynamicParameters();

            for (int i = 0; i < productos.Count; i++)
            {
                whereConditions.Add($"(itprod = @p{i} AND itcia = @c{i})");
                dynamicParams.Add($"@p{i}", productos[i].productoCodigo);
                dynamicParams.Add($"@c{i}", productos[i].productoCompaniaCodigo);
            }

            var query = $"SELECT itprod, itcia FROM Matitec WHERE ({string.Join(" OR ", whereConditions)}) ";
            var productosExistentes = await connection.QueryAsync<(string Codigo, string Compania)>(query, dynamicParams);

            foreach (var prod in productos)
            {
                var key = (prod.productoCodigo, prod.productoCompaniaCodigo);
                _productosCache[key] = productosExistentes.Any(p => p.Codigo == prod.productoCodigo && p.Compania == prod.productoCompaniaCodigo);
            }
        }

        // AHORA: tipado fuerte con tuplas
        private async Task PrecargarStockAsync(SqlConnection connection, List<(string productoCodigo, string productoCompaniaCodigo)> productos05)
        {
            if (!productos05.Any()) return;

            var whereConditions = new List<string>();
            var dynamicParams = new DynamicParameters();

            for (int i = 0; i < productos05.Count; i++)
            {
                whereConditions.Add($"(producto = @p{i} AND cia = @c{i})");
                dynamicParams.Add($"@p{i}", productos05[i].productoCodigo);
                dynamicParams.Add($"@c{i}", productos05[i].productoCompaniaCodigo);
            }

            var query = $"SELECT producto, cia, stockDisponible FROM [SAAD].[dbo].[_stock_disponible] WHERE {string.Join(" OR ", whereConditions)}";
            var stockData = await connection.QueryAsync<(string producto, string cia, decimal stock)>(query, dynamicParams);
            var stockList = stockData.ToList();

            foreach (var prod in productos05)
            {
                var key = (prod.productoCodigo, prod.productoCompaniaCodigo);
                var found = stockList.FirstOrDefault(s => s.producto == prod.productoCodigo && s.cia == prod.productoCompaniaCodigo);
                var exists = stockList.Any(s => s.producto == prod.productoCodigo && s.cia == prod.productoCompaniaCodigo);
                _stockCache[key] = exists ? found.stock : (decimal?)null;
            }
        }

        public async Task ValidateCabeceraAsync(CabeceraDto cab)
        {
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
            if (cab.FechaEntrega < cab.FechaEmision)
                throw new Exception("La fecha de entrega no puede ser anterior a la fecha de emisión");

            if (_clientesCache.TryGetValue(cab.ClienteCodigo, out var clienteExiste))
            {
                if (!clienteExiste)
                    throw new Exception($"Cliente {cab.ClienteCodigo} no existe");
            }
            else
            {
                await ValidarClienteExisteAsync(cab.ClienteCodigo);
            }

            _logger.LogDebug("Cabecera {Numero} validada correctamente", cab.Numero);
        }

        public async Task ValidateDetalleAsync(DetalleDto det)
        {
            if (string.IsNullOrWhiteSpace(det.ProductoCodigo))
                throw new Exception("Código de producto es obligatorio");
            if (det.Cantidad <= 0)
                throw new Exception("Cantidad debe ser mayor a cero");

            var productoKey = (det.ProductoCodigo, det.ProductoCompaniaCodigo);

            if (_productosCache.TryGetValue(productoKey, out var productoExiste))
            {
                if (!productoExiste)
                    throw new Exception($"Producto {det.ProductoCodigo} no existe para compañía {det.ProductoCompaniaCodigo}");
            }
            else
            {
                await ValidarProductoExisteAsync(det.ProductoCodigo, det.ProductoCompaniaCodigo);
            }

            if (det.ProductoCompaniaCodigo == "05")
            {
                if (_stockCache.TryGetValue(productoKey, out var stock))
                {
                    if (!stock.HasValue)
                        throw new Exception($"Producto {det.ProductoCodigo} sin stock registrado");
                    if (stock.Value < det.Cantidad)
                        throw new Exception($"Stock insuficiente para {det.ProductoCodigo}: Disponible {stock.Value}, Solicitado {det.Cantidad}");
                }
                else
                {
                    await ValidarStockDisponibleAsync(det);
                }
            }

            _logger.LogDebug("Detalle {Numero}-{Linea} validado", det.Numero, det.Linea);
        }

        // AHORA: no es async; devuelve Task.CompletedTask para evitar CS1998
        public Task ValidatePedidoTxtAsync(PedidoDto pedido)
        {
            if (string.IsNullOrWhiteSpace(pedido.numero))
                throw new Exception("Número de pedido es obligatorio");
            if (pedido.detalle == null || !pedido.detalle.Any())
                throw new Exception("El pedido debe contener al menos un detalle");

            foreach (var det in pedido.detalle)
            {
                if (det.cantidad <= 0)
                    throw new Exception($"Línea {det.linea}: Cantidad debe ser mayor a cero");

                var productoKey = (det.productoCodigo, det.productoCompaniaCodigo);

                if (_productosCache.TryGetValue(productoKey, out var productoExiste))
                {
                    if (!productoExiste)
                        throw new Exception($"Producto {det.productoCodigo} no existe para compañía {det.productoCompaniaCodigo}");
                }

                if (det.productoCompaniaCodigo == "05")
                {
                    if (_stockCache.TryGetValue(productoKey, out var stock))
                    {
                        if (!stock.HasValue)
                            throw new Exception($"Línea {det.linea}: Producto {det.productoCodigo} sin stock registrado");
                        if (stock.Value < det.cantidad)
                            throw new Exception($"Línea {det.linea}: Stock insuficiente para {det.productoCodigo}: Disponible {stock.Value}, Solicitado {det.cantidad}");
                    }
                }
            }

            return Task.CompletedTask;
        }

        private async Task ValidarClienteExisteAsync(string clienteCodigo)
        {
            using var connection = new SqlConnection(_connectionString);
            var existe = await connection.QuerySingleOrDefaultAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM Clientes WHERE Codigo = @Codigo ) THEN 1 ELSE 0 END AS BIT)",
                new { Codigo = clienteCodigo });

            if (!existe)
                throw new Exception($"Cliente {clienteCodigo} no existe");
        }

        private async Task ValidarProductoExisteAsync(string productoCodigo, string companiaCodigo)
        {
            using var connection = new SqlConnection(_connectionString);
            var existe = await connection.QuerySingleOrDefaultAsync<bool>(
                "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM Matitec WHERE Itprod = @Codigo AND Itcia = @Compania ) THEN 1 ELSE 0 END AS BIT)",
                new { Codigo = productoCodigo, Compania = companiaCodigo });

            if (!existe)
                throw new Exception($"Producto {productoCodigo} no existe para compañía {companiaCodigo}");
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
    }
}
