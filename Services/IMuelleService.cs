using System;
using System.Threading.Tasks;
using EsaLogistica.Api.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using Dapper;

namespace EsaLogistica.Api.Services
{
    public interface IMuelleService
    {
        Task<string?> ObtenerAreaMuelleAsync(string direccion, string? subClienteCodigo, string? codigoPostal, string cliente);
        Task ActualizarAreaMuelleAsync(int cabeceraId, string areaMuelle);
    }

    public class MuelleService : IMuelleService
    {
        private readonly ILogger<MuelleService> _logger;
        private readonly string? _connectionString;

        public MuelleService(ILogger<MuelleService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _connectionString = configuration.GetConnectionString("Servidor4");
        }

        public async Task<string?> ObtenerAreaMuelleAsync(string direccion, string? subClienteCodigo, string? codigoPostal)
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                throw new InvalidOperationException("Cadena de conexión no configurada");

            using var connection = new SqlConnection(_connectionString);

            try
            {
                // 1. PRIORIDAD 1: Buscar por dirección + subCliente
                if (!string.IsNullOrWhiteSpace(subClienteCodigo))
                {
                    var areaPorSubCliente = await connection.QuerySingleOrDefaultAsync<string>(
                        @"SELECT TOP 1 AreaMuelle 
                          FROM MuelleAreas 
                          WHERE Direccion = @Direccion 
                          AND SubClienteCodigo = @SubClienteCodigo
                          AND Cliente = @Cliente",
                        new { Direccion = direccion, SubClienteCodigo = subClienteCodigo, Cliente = Cliente});

                    if (!string.IsNullOrWhiteSpace(areaPorSubCliente))
                    {
                        _logger.LogInformation("Área de muelle encontrada por dirección + subCliente: {Area}", areaPorSubCliente);
                        return areaPorSubCliente;
                    }
                }

                // 2. PRIORIDAD 2: Buscar por código postal
                if (!string.IsNullOrWhiteSpace(codigoPostal))
                {
                    var areaPorCP = await connection.QuerySingleOrDefaultAsync<string>(
                        @"SELECT TOP 1 AreaMuelle 
                          FROM MuelleAreas 
                          WHERE CodigoPostal = @CodigoPostal",
                        new { CodigoPostal = codigoPostal });

                    if (!string.IsNullOrWhiteSpace(areaPorCP))
                    {
                        _logger.LogInformation("Área de muelle encontrada por código postal: {Area}", areaPorCP);
                        return areaPorCP;
                    }
                }

                // 3. PRIORIDAD 3: Área por defecto
                _logger.LogWarning("No se encontró área específica, usando área por defecto");
                return "00001001"; // O null si prefieres error

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo área de muelle");
                throw new Exception($"Error obteniendo área de muelle: {ex.Message}");
            }
        }

        public async Task ActualizarAreaMuelleAsync(int cabeceraId, string areaMuelle)
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                throw new InvalidOperationException("Cadena de conexión no configurada");

            using var connection = new SqlConnection(_connectionString);

            try
            {
                await connection.ExecuteAsync(
                    @"UPDATE CabeceraTemp 
                      SET AreaMuelle = @AreaMuelle 
                      WHERE IdCabecera = @CabeceraId",
                    new { AreaMuelle = areaMuelle, CabeceraId = cabeceraId });

                _logger.LogInformation("Área de muelle actualizada: ID {CabeceraId} -> {Area}", cabeceraId, areaMuelle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error actualizando área de muelle");
                throw new Exception($"Error actualizando área de muelle: {ex.Message}");
            }
        }
    }
}