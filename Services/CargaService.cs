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

        // Almacenamiento temporal en memoria (en producción usar BD)
        private static readonly List<CabeceraDto> _cabecerasTemp = new();
        private static readonly List<DetalleDto> _detallesTemp = new();
        private static readonly List<PedidoDto> _pedidosTemp = new();

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

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            
            using var package = new ExcelPackage(stream);
            
            // Procesar hoja de cabeceras
            var cabeceraSheet = package.Workbook.Worksheets.FirstOrDefault(w => 
                w.Name.Contains("Cabecera", StringComparison.OrdinalIgnoreCase) ||
                w.Name.Contains("Header", StringComparison.OrdinalIgnoreCase)) ?? 
                package.Workbook.Worksheets.First();

            await ProcesarCabecerasExcel(cabeceraSheet);

            // Procesar hoja de detalles
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
            _pedidosTemp.Clear();

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

            try
            {
                // Procesar datos de Excel
                if (_cabecerasTemp.Any())
                {
                    var resultado = await ProcesarExcelDataAsync();
                    procesados += resultado.procesados;
                    etapasInsertadas += resultado.etapasInsertadas;
                    errores.AddRange(resultado.errores);
                }

                // Procesar datos de TXT
                if (_pedidosTemp.Any())
                {
                    var resultado = await ProcesarTxtDataAsync();
                    procesados += resultado.procesados;
                    etapasInsertadas += resultado.etapasInsertadas;
                    errores.AddRange(resultado.errores);
                }

                _logger.LogInformation("Procesamiento completado: {Procesados} procesados, {Insertadas} insertadas", 
                    procesados, etapasInsertadas);

                return (procesados, etapasInsertadas, errores);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error general en procesamiento");
                errores.Add($"Error general en procesamiento: {ex.Message}");
                return (procesados, etapasInsertadas, errores);
            }
        }

        private async Task ProcesarCabecerasExcel(ExcelWorksheet sheet)
        {
            var rowCount = sheet.Dimension?.Rows ?? 0;
            
            for (int row = 2; row <= rowCount; row++) // Asumiendo fila 1 son headers
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

                    // Validar la cabecera antes de agregarla
                    try
                    {
                        await _validation.ValidateCabeceraAsync(cabecera);
                        _cabecerasTemp.Add(cabecera);
                        _logger.LogDebug("Cabecera {Numero} agregada correctamente", cabecera.Numero);
                    }
                    catch (Exception validationEx)
                    {
                        _logger.LogWarning("Error validando cabecera fila {Row}: {Error}", row, validationEx.Message);
                        // Continuamos con la siguiente fila en lugar de fallar completamente
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

                    // Validar que el detalle corresponda a una cabecera existente
                    if (!_cabecerasTemp.Any(cab => cab.Numero == detalle.Numero))
                    {
                        _logger.LogWarning("Detalle fila {Row}: No se encontró cabecera para número {Numero}", row, detalle.Numero);
                        continue;
                    }

                    // Validar el detalle antes de agregarlo
                    try
                    {
                        await _validation.ValidateDetalleAsync(detalle);
                        _detallesTemp.Add(detalle);
                        _logger.LogDebug("Detalle {Numero}-{Linea} agregado correctamente", detalle.Numero, detalle.Linea);
                    }
                    catch (Exception validationEx)
                    {
                        _logger.LogWarning("Error validando detalle fila {Row}: {Error}", row, validationEx.Message);
                        // Continuamos con la siguiente fila
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error procesando detalle fila {Row}: {Error}", row, ex.Message);
                }
            }

            await Task.CompletedTask; // Para evitar el warning CS1998
        }

        private async Task<(int procesados, int etapasInsertadas, List<string> errores)> ProcesarExcelDataAsync()
        {
            var errores = new List<string>();
            int procesados = 0;
            int etapasInsertadas = 0;

            foreach (var cabecera in _cabecerasTemp)
            {
                try
                {
                    // Las validaciones ya se hicieron durante la carga, pero validamos nuevamente por seguridad
                    await _validation.ValidateCabeceraAsync(cabecera);

                    // OBTENER ÁREA DE MUELLE
                    try
                    {
                        var areaMuelle = await _muelleService.ObtenerAreaMuelleAsync(
                            cabecera.Direccion ?? string.Empty, 
                            cabecera.SubClienteCodigo ?? string.Empty, 
                            cabecera.CodigoPostal,
                            cabecera.ClienteCodigo);

                        cabecera.AreaMuelle = areaMuelle;
                        _logger.LogInformation("Área de muelle asignada para {Numero}: {Area}", cabecera.Numero, areaMuelle);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Error obteniendo área de muelle para {Numero}: {Error}", cabecera.Numero, ex.Message);
                        cabecera.AreaMuelle = "GENERAL"; // Valor por defecto
                    }

                    // Procesar detalles asociados
                    var detallesAsociados = _detallesTemp.Where(d => d.Numero == cabecera.Numero).ToList();
                    
                    if (!detallesAsociados.Any())
                    {
                        _logger.LogWarning("No se encontraron detalles para cabecera {Numero}", cabecera.Numero);
                        errores.Add($"No se encontraron detalles para cabecera {cabecera.Numero}");
                        continue;
                    }

                    // Insertar en tablas temporales de la base de datos
                    await InsertarEnTablasTemporales(cabecera, detallesAsociados);

                    etapasInsertadas++;
                    procesados++;
                    
                    _logger.LogInformation("Cabecera {Numero} procesada exitosamente con {DetallesCount} detalles", 
                        cabecera.Numero, detallesAsociados.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando cabecera {Numero}", cabecera.Numero);
                    errores.Add($"Error procesando cabecera {cabecera.Numero}: {ex.Message}");
                }
            }

            return (procesados, etapasInsertadas, errores);
        }

        private async Task<(int procesados, int etapasInsertadas, List<string> errores)> ProcesarTxtDataAsync()
        {
            var errores = new List<string>();
            int procesados = 0;
            int etapasInsertadas = 0;

            foreach (var pedido in _pedidosTemp)
            {
                try
                {
                    // Validar el pedido completo
                    await _validation.ValidatePedidoTxtAsync(pedido);

                    // Convertir pedido TXT a formato cabecera/detalle
                    var cabecera = ConvertirPedidoACabecera(pedido);
                    var detalles = ConvertirPedidoADetalles(pedido);

                    // OBTENER ÁREA DE MUELLE para pedidos TXT
                    try
                    {
                        var areaMuelle = await _muelleService.ObtenerAreaMuelleAsync(
                            cabecera.Direccion ?? string.Empty, 
                            cabecera.SubClienteCodigo, 
                            cabecera.LocalidadNombre,
                            cabecera.ClienteCodigo); // Para TXT usamos localidad como CP

                        cabecera.AreaMuelle = areaMuelle;
                        _logger.LogInformation("Área de muelle asignada para pedido TXT {Numero}: {Area}", pedido.numero, areaMuelle);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Error obteniendo área de muelle para pedido TXT {Numero}: {Error}", pedido.numero, ex.Message);
                        cabecera.AreaMuelle = "GENERAL"; // Valor por defecto
                    }

                    await InsertarEnTablasTemporales(cabecera, detalles);
                    
                    etapasInsertadas++;
                    procesados++;

                    _logger.LogInformation("Pedido TXT {Numero} procesado exitosamente", pedido.numero);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando pedido TXT {Numero}: {Error}", pedido.numero, ex.Message);
                    errores.Add($"Error procesando pedido TXT {pedido.numero}: {ex.Message}");
                }
            }

            return (procesados, etapasInsertadas, errores);
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
                LocalidadNombre = pedido.localidadCodigo, // En TXT viene como código
                CodigoPostal = "", // No disponible en formato TXT actual
                ValorDeclarado = pedido.importeFactura,
                ReferenciaA = pedido.referenciaA,
                ReferenciaB = pedido.referenciaB,
                Observaciones = pedido.observaciones,
                // Campos específicos de logística no disponibles en TXT
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
                LoteVencimiento = DateTime.Now.AddYears(1), // Valor por defecto
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
                // Insertar en CabeceraTemp
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
                    cabecera.LocalidadNombre, // Este es el codigo postal real en algunos casos
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

                // Insertar detalles en DetalleTemp
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
                        DespachoParcial = false // Valor por defecto
                    }, transaction);
                }

                transaction.Commit();
                
                _logger.LogInformation("Insertado en tablas temporales: Cabecera {Numero} con {DetallesCount} detalles, Área: {Area}",
                    cabecera.Numero, detalles.Count, cabecera.AreaMuelle);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Error insertando en tablas temporales para cabecera {Numero}", cabecera.Numero);
                throw;
            }
        }

        private bool EsDocumentoParaApiExterna(string tipoCodigo)
        {
            // Define aquí qué tipos de código van a la API externa
            var tiposApiExterna = new[] { "001", "002", "050" }; 
            return tiposApiExterna.Contains(tipoCodigo);
        }

        private async Task EnviarAApiExterna(CabeceraDto cabecera, List<DetalleDto> detalles)
        {
            try
            {
                // Convertir a formato requerido por la API externa
                var payload = new
                {
                    numero = cabecera.Numero,
                    fecha = cabecera.FechaEmision,
                    cliente = cabecera.ClienteCodigo,
                    direccion = cabecera.Direccion,
                    areaMuelle = cabecera.AreaMuelle,
                    items = detalles.Select(d => new
                    {
                        producto = d.ProductoCodigo,
                        cantidad = d.Cantidad,
                        lote = d.LoteCodigo
                    })
                };

                var token = await _apiService.AuthenticateAsync();
                await _apiService.CreateOrderAsync(token, payload);
                
                _logger.LogInformation("Documento {Numero} enviado a API externa", cabecera.Numero);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando documento {Numero} a API externa", cabecera.Numero);
                throw;
            }
        }

        private async Task GuardarPedidoLocalmente(PedidoDto pedido)
        {
            // Convertir pedido a formato cabecera/detalle y guardar localmente
            var cabecera = ConvertirPedidoACabecera(pedido);
            var detalles = ConvertirPedidoADetalles(pedido);
            
            await InsertarEnTablasTemporales(cabecera, detalles);
            
            _logger.LogInformation("Pedido {Numero} guardado localmente como fallback", pedido.numero);
        }
    }
}