using System.Net.Http;
using System.Text.Json;
using System.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace EsaLogistica.Api.Services
{
    public class GeorefArAddressValidationService : IAddressValidationService
    {
        private readonly HttpClient _http;
        private readonly ILogger<GeorefArAddressValidationService>? _logger;

        public GeorefArAddressValidationService(HttpClient http, ILogger<GeorefArAddressValidationService>? logger = null)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<AddressValidationResult> ValidateAsync(
            string direccion, string? localidad, string? provincia, string? codigoPostal, CancellationToken ct = default)
        {
            try
            {
                var qb = HttpUtility.ParseQueryString(string.Empty);
                qb["direccion"] = direccion;
                if (!string.IsNullOrWhiteSpace(provincia)) qb["provincia"] = provincia!;
                if (!string.IsNullOrWhiteSpace(localidad)) qb["localidad_censal"] = localidad!;
                qb["max"] = "1";
                qb["aplanar"] = "true";

                var url = "/georef/api/direcciones?" + qb.ToString();
                using var resp = await _http.GetAsync(url, ct);
                
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("Georef API error: HTTP {StatusCode}", (int)resp.StatusCode);
                    return new AddressValidationResult(false, null, null, null, null, null, null, 0, "georef-ar",
                        new[] { $"HTTP {(int)resp.StatusCode}" });
                }

                var jsonContent = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(jsonContent);
                
                if (!doc.RootElement.TryGetProperty("direcciones", out var arr) || arr.GetArrayLength() == 0)
                {
                    _logger?.LogInformation("No se encontraron direcciones para: {Direccion}", direccion);
                    return new AddressValidationResult(false, null, null, null, null, null, null, 0, "georef-ar",
                        new[] { "Sin coincidencias" });
                }

                var d = arr[0];
                
                // Usar TryGetProperty para evitar excepciones
                string? norm = d.TryGetProperty("direccion_normalizada", out var normProp) ? normProp.GetString() : null;
                
                string? prov = null;
                if (d.TryGetProperty("provincia", out var provinciaObj) && 
                    provinciaObj.TryGetProperty("nombre", out var provinciaName))
                {
                    prov = provinciaName.GetString();
                }
                
                string? loc = null;
                if (d.TryGetProperty("localidad_censal", out var localidadObj) && 
                    localidadObj.TryGetProperty("nombre", out var localidadName))
                {
                    loc = localidadName.GetString();
                }
                
                string? cp = d.TryGetProperty("codigo_postal", out var cpProp) ? cpProp.GetString() : null;
                
                double? lat = null;
                double? lng = null;
                if (d.TryGetProperty("ubicacion", out var ubicacion))
                {
                    if (ubicacion.TryGetProperty("lat", out var latProp))
                        lat = latProp.GetDouble();
                    if (ubicacion.TryGetProperty("lon", out var lngProp))
                        lng = lngProp.GetDouble();
                }

                var mismatches = new List<string>();
                double conf = 0.8;
                
                if (!string.IsNullOrWhiteSpace(provincia) && !string.Equals(provincia, prov, System.StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"Provincia: {prov}≠{provincia}");
                    conf -= 0.2;
                }
                if (!string.IsNullOrWhiteSpace(localidad) && !string.Equals(localidad, loc, System.StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"Localidad: {loc}≠{localidad}");
                    conf -= 0.2;
                }
                if (!string.IsNullOrWhiteSpace(codigoPostal) && !string.Equals(codigoPostal, cp, System.StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"CP: {cp}≠{codigoPostal}");
                    conf -= 0.2;
                }

                _logger?.LogInformation("Dirección validada: {Direccion} -> {DireccionNormalizada}", direccion, norm);
                
                return new AddressValidationResult(conf >= 0.6, norm, prov, loc, cp, lat, lng, conf, "georef-ar", mismatches.ToArray());
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Error parsing JSON from Georef API for address: {Direccion}", direccion);
                return new AddressValidationResult(false, null, null, null, null, null, null, 0, "georef-ar",
                    new[] { $"JSON parse error: {ex.Message}" });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error in Georef validation for address: {Direccion}", direccion);
                return new AddressValidationResult(false, null, null, null, null, null, null, 0, "georef-ar",
                    new[] { $"Error: {ex.Message}" });
            }
        }
    }
}