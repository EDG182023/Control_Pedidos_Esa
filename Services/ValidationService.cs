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

        // Caches por lote
        private readonly Dictionary<string, bool> _clientesCache = new();
        private readonly Dictionary<(string, string), bool> _productosCache = new();
        private readonly Dictionary<(string, string), decimal?> _stockCache = new();

        // Chunking para no superar 2100 parámetros
        private const int ClientesChunkSize = 900;   // 900 * 1 param
        private const int ProductosChunkSize = 800;  // 800 * 2 params ~= 1600

        public ValidationService(ILogger<ValidationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _connectionString = configuration.GetConnectionString("SaadDb");
        }

        public async Task ValidarLoteAsync(List<PedidoDto> pedidos)
        {
            if (pedidos == null || pedidos.Count == 0) return;

            // Limpiar caches para el nuevo lote
            _clientesCache.Clear();
            _productosCache.Clear();
            _stockCache.Clear();

            var clientesCodigos = pedidos
                .Select(p => p.clienteCodigo)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();

            // Tuplas tipadas (código, compañía)
            var productos = pedidos
                .SelectMany(p => p.detalle ?? Enumerable.Empty<PedidoDetalleDto>())
                .Select(d => (productoCodigo: d.productoCodigo, productoCompaniaCodigo: d.productoCompaniaCodigo))
                .Where(t => !string.IsNullOrWhiteSpace(t.productoCodigo) && !string.IsNullOrWhiteSpace(t.productoCompaniaCodigo))
                .Distinct()
                .ToList();

            using var connection = new SqlConnection(_connectionString);

            await PrecargarClientesAsync(connection, clientesCodigos);
            await PrecargarProductosAsync(connection, productos);

            var productos05 = productos.Where(p => p.productoCompaniaCodigo == "05").ToList();
            if (productos05.Count > 0)
            {
                await PrecargarStockAsync(connection, productos05);
            }

            _logger.LogInformation(
                "Cache precargado: {ClientesCount} clientes, {ProductosCount} productos, {StockCount} stocks",
                _clientesCache.Count, _productosCache.Count, _stockCache.Count);
        }

        private async Task PrecargarClientesAsync(SqlConnection connection, List<string> clientesCodigos)
        {
            if (clientesCodigos == null || clientesCodigos.Count == 0) return;

            foreach (var chunk in Chunk(clientesCodigos, ClientesChunkSize))
            {
                // Particionar: numéricos vs alfanuméricos
                var numericos = new List<(string original, int valor)>();
                var alfas = new List<string>();

                foreach (var c in chunk)
                {
                    if (IsDigitsOnly(c))
                    {
                        var norm = NormalizeZeros(c);
                        // int.Parse es seguro porque norm es solo dígitos (incluye "0" si estaba vacío)
                        numericos.Add((c, int.Parse(norm)));
                    }
                    else
                    {
                        alfas.Add(c);
                    }
                }

                // --- Query para numéricos: compara por valor int (ignora ceros a la izquierda) ---
                var existentesNum = new HashSet<int>();
                if (numericos.Count > 0)
                {
                    var dparams = new DynamicParameters();
                    var placeholders = new List<string>();
                    for (int i = 0; i < numericos.Count; i++)
                    {
                        placeholders.Add($"@n{i}");
                        dparams.Add($"@n{i}", numericos[i].valor, DbType.Int32);
                    }

                    var sqlNum = $@"
                        SELECT DISTINCT TRY_CONVERT(int, Codigo) AS CodigoInt
                        FROM Clientes
                        WHERE TRY_CONVERT(int, Codigo) IN ({string.Join(",", placeholders)})";

                    var rowsNum = await connection.QueryAsync<int?>(sqlNum, dparams);
                    foreach (var v in rowsNum)
                        if (v.HasValue) existentesNum.Add(v.Value);
                }

                // --- Query para alfanuméricos: igualdad exacta ---
                var existentesAlpha = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (alfas.Count > 0)
                {
                    var dparams = new DynamicParameters();
                    var placeholders = new List<string>();
                    for (int i = 0; i < alfas.Count; i++)
                    {
                        placeholders.Add($"@a{i}");
                        dparams.Add($"@a{i}", alfas[i], DbType.String);
                    }

                    var sqlAlpha = $@"
                        SELECT DISTINCT Codigo
                        FROM Clientes
                        WHERE Codigo IN ({string.Join(",", placeholders)})";

                    var rowsAlpha = await connection.QueryAsync<string>(sqlAlpha, dparams);
                    foreach (var s in rowsAlpha)
                        if (!string.IsNullOrWhiteSpace(s))
                            existentesAlpha.Add(s);
                }

                // Poblar cache con la clave ORIGINAL del pedido
                foreach (var c in chunk)
                {
                    bool existe;
                    if (IsDigitsOnly(c))
                    {
                        var norm = int.Parse(NormalizeZeros(c));
                        existe = existentesNum.Contains(norm);
                    }
                    else
                    {
                        existe = existentesAlpha.Contains(c);
                    }
                    _clientesCache[c] = existe;
                }
            }
        }

        private async Task PrecargarProductosAsync(SqlConnection connection,
            List<(string productoCodigo, string productoCompaniaCodigo)> productos)
        {
            if (productos == null || productos.Count == 0) return;

            foreach (var chunk in Chunk(productos, ProductosChunkSize))
            {
                var where = new List<string>();
                var dparams = new DynamicParameters();

                for (int i = 0; i < chunk.Count; i++)
                {
                    where.Add($"(itprod = @p{i} AND itcia = @c{i})");
                    dparams.Add($"@p{i}", chunk[i].productoCodigo);
                    dparams.Add($"@c{i}", chunk[i].productoCompaniaCodigo);
                }

                var sql = $"SELECT itprod, itcia FROM Matitec WHERE {string.Join(" OR ", where)}";
                var rows = (await connection.QueryAsync<(string itprod, string itcia)>(sql, dparams)).ToList();
                var hs = rows.Select(r => (r.itprod, r.itcia)).ToHashSet();

                foreach (var prod in chunk)
                {
                    var key = (prod.productoCodigo, prod.productoCompaniaCodigo);
                    _productosCache[key] = hs.Contains((prod.productoCodigo, prod.productoCompaniaCodigo));
                }
            }
        }

        private async Task PrecargarStockAsync(SqlConnection connection,
            List<(string productoCodigo, string productoCompaniaCodigo)> productos05)
        {
            if (productos05 == null || productos05.Count == 0) return;

            foreach (var chunk in Chunk(productos05, ProductosChunkSize))
            {
                var where = new List<string>();
                var dparams = new DynamicParameters();

                for (int i = 0; i < chunk.Count; i++)
                {
                    where.Add($"(producto = @p{i} AND cia = @c{i})");
                    dparams.Add($"@p{i}", chunk[i].productoCodigo);
                    dparams.Add($"@c{i}", chunk[i].productoCompaniaCodigo);
                }

                var sql = $@"
                    SELECT producto, cia, stockDisponible
                    FROM [SAAD].[dbo].[_stock_disponible]
                    WHERE {string.Join(" OR ", where)}";

                var rows = (await connection.QueryAsync<(string producto, string cia, decimal stockDisponible)>(sql, dparams)).ToList();
                var dict = new Dictionary<(string, string), decimal?>();
                foreach (var r in rows)
                    dict[(r.producto, r.cia)] = r.stockDisponible;

                foreach (var prod in chunk)
                {
                    var key = (prod.productoCodigo, prod.productoCompaniaCodigo);
                    if (!dict.TryGetValue(key, out var val))
                        val = null; // sin registro de stock
                    _stockCache[key] = val;
                }
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

        // No async => evita CS1998
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

            bool existe;
            if (IsDigitsOnly(clienteCodigo))
            {
                var norm = int.Parse(NormalizeZeros(clienteCodigo));
                existe = await connection.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM Clientes WHERE TRY_CONVERT(int, Codigo) = @v",
                    new { v = norm }) == 1;
            }
            else
            {
                existe = await connection.QuerySingleOrDefaultAsync<int>(
                    "SELECT 1 FROM Clientes WHERE Codigo = @Codigo",
                    new { Codigo = clienteCodigo }) == 1;
            }

            if (!existe)
                throw new Exception($"Cliente {clienteCodigo} no existe");
        }

        private async Task ValidarProductoExisteAsync(string productoCodigo, string companiaCodigo)
        {
            using var connection = new SqlConnection(_connectionString);
            var existe = await connection.QuerySingleOrDefaultAsync<int>(
                "SELECT 1 FROM Matitec WHERE Itprod = @Codigo AND Itcia = @Compania",
                new { Codigo = productoCodigo, Compania = companiaCodigo }) == 1;

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

        // ----------------- Helpers -----------------
        private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
        {
            var list = new List<T>(size);
            foreach (var item in source)
            {
                list.Add(item);
                if (list.Count == size)
                {
                    yield return list;
                    list = new List<T>(size);
                }
            }
            if (list.Count > 0) yield return list;
        }

        private static bool IsDigitsOnly(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        private static string NormalizeZeros(string s)
        {
            // Solo para numéricos: quita ceros a la izquierda; si queda vacío => "0"
            var i = 0;
            while (i < s.Length && s[i] == '0') i++;
            var res = s.Substring(i);
            return res.Length == 0 ? "0" : res;
        }
    }
}
