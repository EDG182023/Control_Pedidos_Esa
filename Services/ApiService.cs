using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace EsaLogistica.Api.Services
{
    public class ApiService : IApiService
    {
        private readonly HttpClient _http;
        public ApiService(HttpClient http) => _http = http;

        public async Task<string> AuthenticateAsync()
        {
            // TODO: reemplazar por credenciales reales o leer de configuración segura
            var auth = new { usuario = "esa-logistica", hash = "esa-logistica2024!" };

            using var content = new StringContent(JsonSerializer.Serialize(auth), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync("api/auth/login", content);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Auth falló [{(int)resp.StatusCode}]: {body}");

            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;

            return token ?? string.Empty; // evita CS8603
        }

        public async Task CreateOrderAsync(string token, object payload)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/saadpedidos/crear");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? string.Empty);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            Console.WriteLine($"[ApiService] {(int)resp.StatusCode} {resp.StatusCode}: {body}");

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"[{(int)resp.StatusCode}] {body}");
        }

        public async Task<bool> CheckStockAsync(string productoCodigo)
        {
            var resp = await _http.GetAsync($"api/stock/{productoCodigo}");
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Stock check falló [{(int)resp.StatusCode}]: {body}");

            using var doc = JsonDocument.Parse(body);
            var cantidad = doc.RootElement.TryGetProperty("stock", out var s)
                ? s.GetInt32()
                : 0;

            return cantidad > 0;
        }
    }
}
