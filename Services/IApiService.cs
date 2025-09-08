using System.Threading.Tasks;

namespace EsaLogistica.Api.Services
{
    public interface IApiService
    {
        Task<string> AuthenticateAsync();
        Task CreateOrderAsync(string token, object payload);
        Task<bool> CheckStockAsync(string productoCodigo);
    }
}