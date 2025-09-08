using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace EsaLogistica.Api.Services
{
    public class NominatimAddressValidationService : IAddressValidationService
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _cfg;

        // Rate limit simple (Nominatim ~1 req/seg)
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static DateTime _last = DateTime.MinValue;

        public NominatimAddressValidationService(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            _cfg = cfg;
        }

        public async Task<AddressValidationResult> ValidateAsync(
            string direccion,
            string? localidad,
            string? provincia,
            string? codigoPostal,
            CancellationToken ct = default)
        {
            // ---- Throttle ----
            var delayMs = _cfg.GetValue<int>("AddressValidation:Nominatim:DelayBetweenRequestsMs", 1100);
            await _gate.WaitAsync(ct);
            try
            {
                var elapsed = DateTime.UtcNow - _last;
                if (elapsed.TotalMilliseconds < delayMs)
                    await Task.Delay(delayMs - (int)elapsed.TotalMilliseconds, ct);
                _last = DateTime.UtcNow;
            }
            finally { _gate.Release(); }

            // ---- Query ----
            var query = $"{direccion}, {localidad}, {provincia}, Argentina".Trim();
            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(query)}&format=json&limit=1";

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new AddressValidationResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    0,
                    "nominatim",
                    new[] { $"HTTP {(int)response.StatusCode}" }
                );
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return new AddressValidationResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    0,
                    "nominatim",
                    new[] { "No results found" }
                );
            }

            var first = doc.RootElement[0];
            var displayAddress = first.GetProperty("display_name").GetString();

            var latStr = first.GetProperty("lat").GetString();
            var lonStr = first.GetProperty("lon").GetString();

            double? lat = double.TryParse(latStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var la) ? la : null;
            double? lon = double.TryParse(lonStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lo) ? lo : null;

            return new AddressValidationResult(
                true,
                displayAddress,
                null,
                null,
                null,
                lat,
                lon,
                1.0,
                "nominatim",
                null
            );
        }
    }
}
