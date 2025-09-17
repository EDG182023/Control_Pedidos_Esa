using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using EsaLogistica.Api.Dtos;
using OfficeOpenXml;

namespace EsaLogistica.Api.Services
{
    public class CargaService : ICargaService
    {
        private readonly IValidationService _validation;
        private readonly IApiService _apiService;
        private readonly ITxtParserService _txtParser;
        private readonly IMuelleService _muelleService;
        private readonly ILogger<CargaService> _logger;
        private readonly string? _connectionString;
        private readonly string? _servidor4ConnectionString;

        // Variables de instancia (no estáticas)
        private readonly List<CabeceraDto> _cabecerasTemp = new();
        private readonly List<DetalleDto> _detallesTemp = new();
        private readonly List<PedidoDto> _pedidosTemp = new();

        public CargaService(
            IValidationService validation,
            IApiService apiService,
            ITxtParserService txtParser,
            IMuelleService muelleService,
            ILogger<CargaService> logger,
            IConfiguration configuration)
        {
            _validation = validation;
            _apiService = apiService;
            _txtParser = txtParser;
            _muelleService = muelleService;
            _logger = logger;
            _connectionString = configuration.GetConnectionString("SaadDb");
            _servidor4ConnectionString = configuration.GetConnectionString("Servidor4");
        }

        public async Task CargarExcelAsync(IFormFile file)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("Archivo Excel vacío o nulo");

            // Limpiar datos temporales
            _cabecerasTemp.Clear();
            _detallesTemp.Clear();
            _pedidosTemp.Clear();

            _logger.LogInformation("Iniciando carga Excel - Estado limpio");

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);

            using var package = new ExcelPackage(stream);

            var cabeceraSheet = package.Workbook.Worksheets.FirstOrDefault(w =>
                w.Name.Contains("Cabecera", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("Header", StringComparison.OrdinalIgnoreCase)) ??
                package.Workbook.Worksheets.First();

            await ProcesarCabecerasExcel(cabeceraSheet);

            var detalleSheet = package.Workbook.Worksheets.FirstOrDefault(w =>
                w.Name.Contains("Detalle", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("Detail", StringComparison.OrdinalIgnoreCase)) ??
                package.Workbook.Worksheets.Skip(1).FirstOrDefault();

            if (detalleSheet != null)
                await ProcesarDetallesExcel(detalleSheet);

            _logger.LogInformation("Excel cargado: {CabecerasCount} cabeceras, {DetallesCount} detalles",
                _cabecerasTemp.Count, _detallesTemp.Count);
        }

        public async Task CargarTxtAsync(IFormFile cabecera, IFormFile detalle)
        {
            if (cabecera == null || detalle == null)
                throw new ArgumentException("Archivos TXT nulos");

            // Limpiar datos temporales
            _cabecerasTemp.Clear();
            _detallesTemp.Clear();
            _pedidosTemp.Clear();

            _logger.LogInformation("Iniciando carga TXT - Estado limpio");

            using var cabeceraStream = cabecera.OpenReadStream();
            using var detalleStream = detalle.OpenReadStream();

            var pedidos = await _txtParser.ParseTxtAsync(cabeceraStream, detalleStream);
            _pedidosTemp.AddRange(pedidos);

            _logger.LogInformation("TXT cargado: {PedidosCount} pedidos", _pedidosTemp.Count);
        }

        public async Task<(int procesados, int etapasInsertadas, List<string> errores)> ProcesarDesdeTempAsync()
        {
            var errores = new List<string>();
            int procesados = 0;
            int etapasInsertadas = 0;

            _logger.LogInformation("Iniciando procesamiento optimizado - Excel: {ExcelCount}, TXT: {TxtCount}",
                _cabecerasTemp.Count, _pedidosTemp.Count);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // OPTIMIZACIÓN: Pre-validar en lote si hay datos TXT
                if (_pedidosTemp.Any())
                {
                    _logger.LogInformation("Pre-cargando cache de validación para {Count} pedidos TXT", _pedidosTemp.Count);
                    var cacheStopwatch = System.Diagnostics.Stopwatch.StartNew();

                    // Cast necesario para el método optimizado
                    var validationService = _validation as ValidationService;
                    if (validationService != null)
                    {
                        await validationService.ValidarLoteAsync(_pedidosTemp);
                        _logger.LogInformation("Cache precargado en {CacheMs}ms", cacheStopwatch.ElapsedMilliseconds);
                    }
                }

                // Procesar datos de Excel
                if (_cabecerasTemp.Any())
                {
                    _logger.LogInformation("Procesando {Count} cabeceras de Excel", _cabecerasTemp.Count);
                    var resultado = await ProcesarExcelDataAsync();
                    procesados += resultado.procesados;
                    etapasInsertadas += resultado.etapasInsertadas;
                    errores.AddRange(resultado.errores);
                }

                // Procesar datos de TXT (con validación optimizada)
                if (_pedidosTemp.Any())
                {
                    _logger.LogInformation("Procesando {Count} pedidos de TXT con cache", _pedidosTemp.Count);
                    var resultado = await ProcesarTxtDataOptimizadoAsync();
                    procesados += resultado.procesados;
                    etapasInsertadas += resultado.etapasInsertadas;
                    errores.AddRange(resultado.errores);
                }

                if (!_cabecerasTemp.Any() && !_pedidosTemp.Any())
                {
                    errores.Add("No se encontraron datos para procesar. Verifique que el archivo sea válido.");
                }

                stopwatch.Stop();
                _logger.LogInformation("Procesamiento completado en {TotalMs}ms: {Procesados} procesados, {Insertadas} insertadas, {ErroresCount} errores",
                    stopwatch.ElapsedMilliseconds, procesados, etapasInsertadas, errores.Count);

                return (procesados, etapasInsertadas, errores);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Error general en procesamiento después de {Ms}ms", stopwatch.ElapsedMilliseconds);
                errores.Add($"Error general en procesamiento: {ex.Message}");
                return (procesados, etapasInsertadas, errores);
            }
        }

        // NUEVO: Método optimizado para TXT con cache
        private async Task<(int procesados, int etapasInsertadas, List<string> errores)> ProcesarTxtDataOptimizadoAsync()
        {
            var errores = new List<string>();
            int procesados = 0;
            int etapasInsertadas = 0;

            // Pre-obtener áreas de muelle en lote
            var areasCache = new Dictionary<string, string>();
            await PrecargarAreasMuelle(areasCache);

            // OPTIMIZACIÓN: Preparar datos para bulk insert
            var cabecerasParaInsertar = new List<CabeceraDto>();
            var detallesParaInsertar = new List<(CabeceraDto cabecera, List<DetalleDto> detalles)>();

            foreach (var pedido in _pedidosTemp)
            {
                try
                {
                    // Validación rápida con cache
                    await _validation.ValidatePedidoTxtAsync(pedido);

                    // Convertir pedido TXT a formato cabecera/detalle
                    var cabecera = ConvertirPedidoACabecera(pedido);
                    var detalles = ConvertirPedidoADetalles(pedido);

                    // Asignar área de muelle desde cache
                    var cacheKey = $"{cabecera.Direccion}|{cabecera.SubClienteCodigo}|{cabecera.LocalidadNombre}|{cabecera.ClienteCodigo}";
                    cabecera.AreaMuelle = areasCache.GetValueOrDefault(cacheKey, "GENERAL");

                    cabecerasParaInsertar.Add(cabecera);
                    detallesParaInsertar.Add((cabecera, detalles));

                    procesados++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error validando pedido TXT {Numero}: {Error}", pedido.numero, ex.Message);
                    errores.Add($"Error procesando pedido TXT {pedido.numero}: {ex.Message}");
                }
            }

            // BULK INSERT optimizado
            if (cabecerasParaInsertar.Any())
            {
                etapasInsertadas = await BulkInsertCabecerasDetallesAsync(detallesParaInsertar);
            }

            return (procesados, etapasInsertadas, errores);
        }

        private async Task PrecargarAreasMuelle(Dictionary<string, string> cache)
        {
            // Extraer combinaciones únicas de criterios de área de muelle
            var criterios = _pedidosTemp.Select(p => new
            {
                Direccion = ConvertirPedidoACabecera(p).Direccion,
                SubClienteCodigo = ConvertirPedidoACabecera(p).SubClienteCodigo,
                LocalidadNombre = ConvertirPedidoACabecera(p).LocalidadNombre,
                ClienteCodigo = ConvertirPedidoACabecera(p).ClienteCodigo
            }).Distinct().ToList();

            foreach (var criterio in criterios)
            {
                try
                {
                    var area = await _muelleService.ObtenerAreaMuelleAsync(
                        criterio.Direccion ?? string.Empty,
                        criterio.SubClienteCodigo,
                        criterio.LocalidadNombre,
                        criterio.ClienteCodigo);

                    var cacheKey = $"{criterio.Direccion}|{criterio.SubClienteCodigo}|{criterio.LocalidadNombre}|{criterio.ClienteCodigo}";
                    cache[cacheKey] = area ?? "GENERAL";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error obteniendo área muelle: {Error}", ex.Message);
                    var cacheKey = $"{criterio.Direccion}|{criterio.SubClienteCodigo}|{criterio.LocalidadNombre}|{criterio.ClienteCodigo}";
                    cache[cacheKey] = "GENERAL";
                }
            }

            _logger.LogInformation("Cache de áreas de muelle precargado: {Count} entradas", cache.Count);
        }

        // OPTIMIZACIÓN: Bulk insert con helper para no superar 2100 parámetros
        private async Task<int> BulkInsertCabecerasDetallesAsync(List<(CabeceraDto cabecera, List<DetalleDto> detalles)> datos)
        {
            if (string.IsNullOrWhiteSpace(_servidor4ConnectionString))
                throw new InvalidOperationException("Cadena de conexión 'Servidor4' no configurada");

            using var connection = new SqlConnection(_servidor4ConnectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                int totalCabecerasInsertadas = 0;

                // === 1) INSERT CABECERAS EN LOTES SEGUROS ===
                // Cada fila de Cabecera inserta 23 columnas -> 23 parámetros por fila
                const int CabeceraParamsPorFila = 23;
                int cabeceraBatchSize = SqlBatchHelper.CalcBatchSize(CabeceraParamsPorFila, safetyMargin: 200);

                foreach (var loteCab in SqlBatchHelper.ChunkBy(datos, cabeceraBatchSize))
                {
                    var insertCabQueryHead = @"
                        INSERT INTO dbo.CabeceraTemp (
                            TipoCodigo, Categoria, Sucursal, Numero, 
                            FechaEmision, FechaEntrega, ClienteCodigo, Bultos, 
                            Kilos, M3, SubClienteCodigo, RazonSocial, 
                            DepositoCodigo, LocalidadNombre, CodigoPostal, Direccion,
                            ValorDeclarado, ReferenciaA, ReferenciaB, Observaciones,
                            Telefono, Email, AreaMuelle
                        ) VALUES ";

                    var values = new List<string>();
                    var dp = new DynamicParameters();

                    for (int i = 0; i < loteCab.Count; i++)
                    {
                        var (cab, _) = loteCab[i];

                        string idx = i.ToString();
                        values.Add($"(@tc{idx}, @cat{idx}, @suc{idx}, @num{idx}, @fe{idx}, @fent{idx}, @cli{idx}, @bul{idx}, @kil{idx}, @m3{idx}, @sub{idx}, @rs{idx}, @dep{idx}, @loc{idx}, @cp{idx}, @dir{idx}, @vd{idx}, @ra{idx}, @rb{idx}, @obs{idx}, @tel{idx}, @mail{idx}, @area{idx})");

                        SqlBatchHelper.Add(dp, $"@tc{idx}", cab.TipoCodigo);
                        SqlBatchHelper.Add(dp, $"@cat{idx}", cab.Categoria);
                        SqlBatchHelper.Add(dp, $"@suc{idx}", cab.Sucursal);
                        SqlBatchHelper.Add(dp, $"@num{idx}", cab.Numero);
                        SqlBatchHelper.Add(dp, $"@fe{idx}", cab.FechaEmision);
                        SqlBatchHelper.Add(dp, $"@fent{idx}", cab.FechaEntrega);
                        SqlBatchHelper.Add(dp, $"@cli{idx}", cab.ClienteCodigo);
                        SqlBatchHelper.Add(dp, $"@bul{idx}", cab.Bultos);
                        SqlBatchHelper.Add(dp, $"@kil{idx}", cab.Kilos);
                        SqlBatchHelper.Add(dp, $"@m3{idx}", cab.M3);
                        SqlBatchHelper.Add(dp, $"@sub{idx}", cab.SubClienteCodigo);
                        SqlBatchHelper.Add(dp, $"@rs{idx}", cab.RazonSocial);
                        SqlBatchHelper.Add(dp, $"@dep{idx}", cab.DepositoCodigo);
                        SqlBatchHelper.Add(dp, $"@loc{idx}", cab.LocalidadNombre);
                        SqlBatchHelper.Add(dp, $"@cp{idx}", cab.CodigoPostal);
                        SqlBatchHelper.Add(dp, $"@dir{idx}", cab.Direccion);
                        SqlBatchHelper.Add(dp, $"@vd{idx}", cab.ValorDeclarado);
                        SqlBatchHelper.Add(dp, $"@ra{idx}", cab.ReferenciaA);
                        SqlBatchHelper.Add(dp, $"@rb{idx}", cab.ReferenciaB);
                        SqlBatchHelper.Add(dp, $"@obs{idx}", cab.Observaciones);
                        SqlBatchHelper.Add(dp, $"@tel{idx}", cab.Telefono);
                        SqlBatchHelper.Add(dp, $"@mail{idx}", cab.Email);
                        SqlBatchHelper.Add(dp, $"@area{idx}", cab.AreaMuelle);
                    }

                    var sql = insertCabQueryHead + string.Join(",", values);
                    await connection.ExecuteAsync(sql, dp, transaction);
                    totalCabecerasInsertadas += loteCab.Count;

                    // === 2) INSERT DETALLES PARA ESTE LOTE, TAMBIÉN EN LOTES SEGUROS ===
                    // Cada fila de Detalle pasa 9 parámetros (Numero, Linea, ProductoCodigo, ProductoCompaniaCodigo, LoteCodigo, LoteVencimiento, Serie, Cantidad, DespachoParcial)
                    const int DetalleParamsPorFila = 9;
                    int detalleBatchSize = SqlBatchHelper.CalcBatchSize(DetalleParamsPorFila, safetyMargin: 200);

                    var detallesDelLote = new List<(string Numero, DetalleDto Detalle)>();
                    foreach (var (cab, dets) in loteCab)
                    {
                        foreach (var d in dets)
                            detallesDelLote.Add((cab.Numero!, d));
                    }

                    foreach (var loteDet in SqlBatchHelper.ChunkBy(detallesDelLote, detalleBatchSize))
                    {
                        var valuesDet = new List<string>();
                        var dpDet = new DynamicParameters();

                        for (int j = 0; j < loteDet.Count; j++)
                        {
                            var item = loteDet[j];
                            string idx = j.ToString();

                            valuesDet.Add($"(@num{idx}, @lin{idx}, @prod{idx}, @comp{idx}, @lote{idx}, @venc{idx}, @serie{idx}, @cant{idx}, @desp{idx})");

                            SqlBatchHelper.Add(dpDet, $"@num{idx}", item.Numero);
                            SqlBatchHelper.Add(dpDet, $"@lin{idx}", item.Detalle.Linea);
                            SqlBatchHelper.Add(dpDet, $"@prod{idx}", item.Detalle.ProductoCodigo);
                            SqlBatchHelper.Add(dpDet, $"@comp{idx}", item.Detalle.ProductoCompaniaCodigo);
                            SqlBatchHelper.Add(dpDet, $"@lote{idx}", item.Detalle.LoteCodigo);
                            SqlBatchHelper.Add(dpDet, $"@venc{idx}", item.Detalle.LoteVencimiento);
                            SqlBatchHelper.Add(dpDet, $"@serie{idx}", item.Detalle.Serie);
                            SqlBatchHelper.Add(dpDet, $"@cant{idx}", item.Detalle.Cantidad);
                            SqlBatchHelper.Add(dpDet, $"@desp{idx}", false);
                        }

                        if (valuesDet.Any())
                        {
                            var insertDetSql = @"
                                INSERT INTO dbo.DetalleTemp (
                                    IdCabecera, Linea, ProductoCodigo, ProductoCompaniaCodigo,
                                    LoteCodigo, LoteVencimiento, Serie, Cantidad, DespachoParcial
                                ) 
                                SELECT ct.IdCabecera, d.Linea, d.ProductoCodigo, d.ProductoCompaniaCodigo,
                                       d.LoteCodigo, d.LoteVencimiento, d.Serie, d.Cantidad, d.DespachoParcial
                                FROM (VALUES " + string.Join(",", valuesDet) + @") AS d(Numero, Linea, ProductoCodigo, ProductoCompaniaCodigo, LoteCodigo, LoteVencimiento, Serie, Cantidad, DespachoParcial)
                                INNER JOIN dbo.CabeceraTemp ct ON ct.Numero = d.Numero";

                            await connection.ExecuteAsync(insertDetSql, dpDet, transaction);
                        }
                    }
                }

                transaction.Commit();

                _logger.LogInformation("Bulk insert completado: {Insertadas} cabeceras con sus detalles (en lotes seguros)", totalCabecerasInsertadas);
                return totalCabecerasInsertadas;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Error en bulk insert");
                throw;
            }
        }

        // Métodos originales sin cambios (para Excel)
        private async Task<(int procesados, int etapasInsertadas, List<string> errores)> ProcesarExcelDataAsync()
        {
            var errores = new List<string>();
            int procesados = 0;
            int etapasInsertadas = 0;

            foreach (var cabecera in _cabecerasTemp)
            {
                try
                {
                    await _validation.ValidateCabeceraAsync(cabecera);

                    // Obtener área de muelle
                    try
                    {
                        var areaMuelle = await _muelleService.ObtenerAreaMuelleAsync(
                            cabecera.Direccion ?? string.Empty,
                            cabecera.SubClienteCodigo ?? string.Empty,
                            cabecera.CodigoPostal,
                            cabecera.ClienteCodigo);

                        cabecera.AreaMuelle = areaMuelle;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Error obteniendo área de muelle para {Numero}: {Error}", cabecera.Numero, ex.Message);
                        cabecera.AreaMuelle = "GENERAL";
                    }

                    var detallesAsociados = _detallesTemp.Where(d => d.Numero == cabecera.Numero).ToList();

                    if (!detallesAsociados.Any())
                    {
                        errores.Add($"No se encontraron detalles para cabecera {cabecera.Numero}");
                        continue;
                    }

                    await InsertarEnTablasTemporales(cabecera, detallesAsociados);
                    etapasInsertadas++;
                    procesados++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando cabecera {Numero}", cabecera.Numero);
                    errores.Add($"Error procesando cabecera {cabecera.Numero}: {ex.Message}");
                }
            }

            return (procesados, etapasInsertadas, errores);
        }

        // Métodos auxiliares sin cambios
        private async Task ProcesarCabecerasExcel(ExcelWorksheet sheet)
        {
            var rowCount = sheet.Dimension?.Rows ?? 0;

            for (int row = 2; row <= rowCount; row++)
            {
                try
                {
                    var cabecera = new CabeceraDto
                    {
                        TipoCodigo = sheet.Cells[row, 1].Text?.Trim(),
                        Categoria = sheet.Cells[row, 2].Text?.Trim(),
                        Sucursal = sheet.Cells[row, 3].Text?.Trim(),
                        Numero = sheet.Cells[row, 4].Text?.Trim(),
                        FechaEmision = DateTime.TryParse(sheet.Cells[row, 5].Text, out var fe) ? fe : DateTime.Now,
                        FechaEntrega = DateTime.TryParse(sheet.Cells[row, 6].Text, out var fent) ? fent : DateTime.Now.AddDays(1),
                        ClienteCodigo = sheet.Cells[row, 7].Text?.Trim(),
                        Bultos = int.TryParse(sheet.Cells[row, 8].Text, out var b) ? b : null,
                        Kilos = decimal.TryParse(sheet.Cells[row, 9].Text, out var k) ? k : null,
                        M3 = decimal.TryParse(sheet.Cells[row, 10].Text, out var m3) ? m3 : null,
                        SubClienteCodigo = string.IsNullOrWhiteSpace(sheet.Cells[row, 11].Text) ? null : sheet.Cells[row, 11].Text.Trim(),
                        RazonSocial = sheet.Cells[row, 12].Text?.Trim(),
                        DepositoCodigo = string.IsNullOrWhiteSpace(sheet.Cells[row, 13].Text) ? null : sheet.Cells[row, 13].Text.Trim(),
                        LocalidadNombre = sheet.Cells[row, 14].Text?.Trim(),
                        CodigoPostal = sheet.Cells[row, 15].Text?.Trim(),
                        Direccion = sheet.Cells[row, 16].Text?.Trim(),
                        ValorDeclarado = decimal.TryParse(sheet.Cells[row, 17].Text, out var vd) ? vd : null,
                        ReferenciaA = string.IsNullOrWhiteSpace(sheet.Cells[row, 18].Text) ? null : sheet.Cells[row, 18].Text.Trim(),
                        ReferenciaB = string.IsNullOrWhiteSpace(sheet.Cells[row, 19].Text) ? null : sheet.Cells[row, 19].Text.Trim(),
                        Observaciones = string.IsNullOrWhiteSpace(sheet.Cells[row, 20].Text) ? null : sheet.Cells[row, 20].Text.Trim(),
                        Telefono = string.IsNullOrWhiteSpace(sheet.Cells[row, 21].Text) ? null : sheet.Cells[row, 21].Text.Trim(),
                        Email = string.IsNullOrWhiteSpace(sheet.Cells[row, 22].Text) ? null : sheet.Cells[row, 22].Text.Trim()
                    };

                    try
                    {
                        await _validation.ValidateCabeceraAsync(cabecera);
                        _cabecerasTemp.Add(cabecera);
                    }
                    catch (Exception validationEx)
                    {
                        _logger.LogWarning("Error validando cabecera fila {Row}: {Error}", row, validationEx.Message);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error procesando cabecera fila {Row}: {Error}", row, ex.Message);
                }
            }
        }

        private async Task ProcesarDetallesExcel(ExcelWorksheet sheet)
        {
            var rowCount = sheet.Dimension?.Rows ?? 0;

            for (int row = 2; row <= rowCount; row++)
            {
                try
                {
                    var detalle = new DetalleDto
                    {
                        Numero = sheet.Cells[row, 1].Text?.Trim(),
                        Linea = int.TryParse(sheet.Cells[row, 2].Text, out var l) ? l : 0,
                        ProductoCodigo = sheet.Cells[row, 3].Text?.Trim(),
                        ProductoCompaniaCodigo = sheet.Cells[row, 4].Text?.Trim(),
                        LoteCodigo = string.IsNullOrWhiteSpace(sheet.Cells[row, 5].Text) ? null : sheet.Cells[row, 5].Text.Trim(),
                        LoteVencimiento = DateTime.TryParse(sheet.Cells[row, 6].Text, out var lv) ? lv : DateTime.Now.AddYears(1),
                        Serie = string.IsNullOrWhiteSpace(sheet.Cells[row, 7].Text) ? null : sheet.Cells[row, 7].Text.Trim(),
                        Cantidad = int.TryParse(sheet.Cells[row, 8].Text, out var c) ? c : 0
                    };

                    if (!_cabecerasTemp.Any(cab => cab.Numero == detalle.Numero))
                    {
                        _logger.LogWarning("Detalle fila {Row}: No se encontró cabecera para número {Numero}", row, detalle.Numero);
                        continue;
                    }

                    try
                    {
                        await _validation.ValidateDetalleAsync(detalle);
                        _detallesTemp.Add(detalle);
                    }
                    catch (Exception validationEx)
                    {
                        _logger.LogWarning("Error validando detalle fila {Row}: {Error}", row, validationEx.Message);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error procesando detalle fila {Row}: {Error}", row, ex.Message);
                }
            }
        }

        private CabeceraDto ConvertirPedidoACabecera(PedidoDto pedido)
        {
            return new CabeceraDto
            {
                TipoCodigo = "05",
                Categoria = pedido.categoria,
                Sucursal = pedido.sucursal,
                Numero = pedido.numero,
                FechaEmision = pedido.fechaEmision,
                FechaEntrega = pedido.fechaEntrega,
                ClienteCodigo = pedido.clienteCodigo,
                SubClienteCodigo = pedido.subClienteCodigo,
                RazonSocial = pedido.razonSocial,
                Direccion = pedido.domicilio,
                LocalidadNombre = pedido.localidadCodigo,
                CodigoPostal = "",
                ValorDeclarado = pedido.importeFactura,
                ReferenciaA = pedido.referenciaA,
                ReferenciaB = pedido.referenciaB,
                Observaciones = pedido.observaciones,
                Bultos = null,
                Kilos = null,
                M3 = null,
                DepositoCodigo = null,
                Telefono = null,
                Email = null
            };
        }

        private List<DetalleDto> ConvertirPedidoADetalles(PedidoDto pedido)
        {
            return pedido.detalle.Select(d => new DetalleDto
            {
                Numero = pedido.numero,
                Linea = d.linea,
                ProductoCodigo = d.productoCodigo,
                ProductoCompaniaCodigo = d.productoCompaniaCodigo,
                LoteCodigo = string.IsNullOrWhiteSpace(d.loteCodigo) ? null : d.loteCodigo,
                LoteVencimiento = DateTime.Now.AddYears(1),
                Serie = string.IsNullOrWhiteSpace(d.serie) ? null : d.serie,
                Cantidad = d.cantidad
            }).ToList();
        }

        private async Task InsertarEnTablasTemporales(CabeceraDto cabecera, List<DetalleDto> detalles)
        {
            if (string.IsNullOrWhiteSpace(_servidor4ConnectionString))
                throw new InvalidOperationException("Cadena de conexión 'Servidor4' no configurada");

            using var connection = new SqlConnection(_servidor4ConnectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                var insertCabeceraQuery = @"
                    INSERT INTO dbo.CabeceraTemp (
                        TipoCodigo, Categoria, Sucursal, Numero, 
                        FechaEmision, FechaEntrega, ClienteCodigo, Bultos, 
                        Kilos, M3, SubClienteCodigo, RazonSocial, 
                        DepositoCodigo, LocalidadNombre, CodigoPostal, Direccion,
                        ValorDeclarado, ReferenciaA, ReferenciaB, Observaciones,
                        Telefono, Email, AreaMuelle
                    ) VALUES (
                        @TipoCodigo, @Categoria, @Sucursal, @Numero,
                        @FechaEmision, @FechaEntrega, @ClienteCodigo, @Bultos,
                        @Kilos, @M3, @SubClienteCodigo, @RazonSocial,
                        @DepositoCodigo, @LocalidadNombre, @CodigoPostal, @Direccion,
                        @ValorDeclarado, @ReferenciaA, @ReferenciaB, @Observaciones,
                        @Telefono, @Email, @AreaMuelle
                    )";

                await connection.ExecuteAsync(insertCabeceraQuery, new
                {
                    cabecera.TipoCodigo,
                    cabecera.Categoria,
                    cabecera.Sucursal,
                    cabecera.Numero,
                    cabecera.FechaEmision,
                    cabecera.FechaEntrega,
                    cabecera.ClienteCodigo,
                    cabecera.Bultos,
                    cabecera.Kilos,
                    cabecera.M3,
                    cabecera.SubClienteCodigo,
                    cabecera.RazonSocial,
                    cabecera.DepositoCodigo,
                    cabecera.LocalidadNombre,
                    cabecera.CodigoPostal,
                    cabecera.Direccion,
                    cabecera.ValorDeclarado,
                    cabecera.ReferenciaA,
                    cabecera.ReferenciaB,
                    cabecera.Observaciones,
                    cabecera.Telefono,
                    cabecera.Email,
                    cabecera.AreaMuelle
                }, transaction);

                var insertDetalleQuery = @"
                    INSERT INTO dbo.DetalleTemp (
                        IdCabecera, Linea, ProductoCodigo, ProductoCompaniaCodigo,
                        LoteCodigo, LoteVencimiento, Serie, Cantidad, DespachoParcial
                    ) VALUES (
                        (SELECT TOP 1 IdCabecera FROM dbo.CabeceraTemp WHERE Numero = @Numero ORDER BY IdCabecera DESC),
                        @Linea, @ProductoCodigo, @ProductoCompaniaCodigo,
                        @LoteCodigo, @LoteVencimiento, @Serie, @Cantidad, @DespachoParcial
                    )";

                foreach (var detalle in detalles)
                {
                    await connection.ExecuteAsync(insertDetalleQuery, new
                    {
                        Numero = cabecera.Numero,
                        detalle.Linea,
                        detalle.ProductoCodigo,
                        detalle.ProductoCompaniaCodigo,
                        detalle.LoteCodigo,
                        detalle.LoteVencimiento,
                        detalle.Serie,
                        detalle.Cantidad,
                        DespachoParcial = false
                    }, transaction);
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Error en InsertarEnTablasTemporales");
                throw;
            }
        }

        // ===== Helper interno para lotes y nulabilidad =====
       // ===== Helper interno para lotes y nulabilidad (FIX) =====
    private static class SqlBatchHelper
    {
        public const int MaxParams = 2100;

        public static int CalcBatchSize(int paramsPerRow, int safetyMargin = 200)
        {
            var usable = Math.Max(1, MaxParams - Math.Max(0, safetyMargin));
            return Math.Max(1, usable / Math.Max(1, paramsPerRow));
        }

        // IMPORTANTE: no convertir a DBNull.Value; dejar null y Dapper se encarga
        public static void Add(Dapper.DynamicParameters p, string name, object? value, DbType? dbType = null)
        {
            // Si querés ser más defensivo con fechas “por defecto”, podés tratarlas como null:
            if (value is DateTime dt && dt == default) value = null;

            p.Add(name, value, dbType: dbType); // null -> DBNull.Value lo hace Dapper
        }

        public static IEnumerable<List<T>> ChunkBy<T>(IEnumerable<T> source, int size)
        {
            if (size <= 0) size = 1;
            var bucket = new List<T>(size);
            foreach (var item in source)
            {
                bucket.Add(item);
                if (bucket.Count == size)
                {
                    yield return bucket;
                    bucket = new List<T>(size);
                }
            }
            if (bucket.Count > 0)
                yield return bucket;
        }
    }

    }
}
